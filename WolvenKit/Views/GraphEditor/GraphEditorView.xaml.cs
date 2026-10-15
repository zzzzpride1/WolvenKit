using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Nodify;
using Syncfusion.SfSkinManager;
using ReactiveUI;
using Splat;
using WolvenKit.App.Services;
using WolvenKit.App.Interaction;
using WolvenKit.App.ViewModels.Controls;
using WolvenKit.App.ViewModels.Dialogs;
using WolvenKit.App.ViewModels.GraphEditor;
using WolvenKit.App.ViewModels.GraphEditor.Nodes.Quest;
using WolvenKit.App.ViewModels.GraphEditor.Nodes.Scene;
using WolvenKit.App.ViewModels.Shell;
using WolvenKit.Views.Templates;
using WolvenKit.App.ViewModels.GraphEditor.Nodes;
using WolvenKit.App.ViewModels.GraphEditor.Nodes.Behavior;
using WolvenKit.App.ViewModels.GraphEditor.Nodes.Quest.Internal;
using WolvenKit.App.ViewModels.GraphEditor.Nodes.Scene.Internal;
using WolvenKit.Common;
using WolvenKit.Common.Model;
using WolvenKit.Common.Services;
using WolvenKit.Core.Interfaces;
using WolvenKit.RED4.Types;

namespace WolvenKit.Views.GraphEditor;

#nullable enable
public record NodeCreationParams(Type Type, RedTypeTemplateSelectionOption? RedTypeTemplateSelectionOption = null);
#nullable disable
/// <summary>
/// Interaktionslogik für GraphEditorView.xaml
/// </summary>
public partial class GraphEditorView : UserControl
{
    private readonly RedTypeTemplateService _redTypeTemplateService = Locator.Current.GetService<RedTypeTemplateService>();
    private readonly ILoggerService _loggerService = Locator.Current.GetService<ILoggerService>();
    private readonly ISettingsManager _settingsManager = Locator.Current.GetService<ISettingsManager>();
    private RedTypeTemplateDropdownViewModel _nodeTemplateOptions;
    private System.Windows.Point _actionPaletteGraphPosition;
    private double _actionPalettePopupHorizontalOffset;
    private BehaviorNodeViewModel _actionPaletteBehaviorParent;
    private bool _actionPaletteCreatesBehaviorRoot;
    private QuestConnectionViewModel _actionPaletteQuestInsertionConnection;

    private static readonly (string Name, string Color)[] s_commentColorPresets =
    [
        ("Yellow", "#FFFFD400"),
        ("Green", "#FF5BB85B"),
        ("Blue", "#FF3FA7FF"),
        ("Purple", "#FFB77AFF"),
        ("Pink", "#FFFF80C8"),
        ("Red", "#FFFF5C5C"),
        ("Gray", "#FFAAAAAA"),
        ("Orange", "#FFFF9F1C")
    ];

    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(RedGraph), typeof(GraphEditorView), new PropertyMetadata(null, OnSourceChanged));

    /// <summary>
    /// Raised when the graph displayed by the editor changes, including when it is cleared.
    /// </summary>
    public event EventHandler<RedGraph> SourceChanged;

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not GraphEditorView view)
        {
            return;
        }

        view.CancelQuestConnectionRewire();
        view.CancelQuestMultiConnect();
        view.SourceChanged?.Invoke(view, e.NewValue as RedGraph);
        view._actionPaletteQuestInsertionConnection = null;

        if (view.Source is null)
        {
            return;
        }

        view.ActionPalettePopup.SetCurrentValue(System.Windows.Controls.Primitives.Popup.IsOpenProperty, false);
        view.Dispatcher.BeginInvoke(new Action(() =>
        {
            UpdateView(view);
            //view.Source?.ArrangeNodes();
        }), DispatcherPriority.ContextIdle);
    }

    private static void UpdateView(GraphEditorView view)
    {
        if (view.Source is null) // graph editor hasn't been initialized yet
        {
            return;
        }
        view.Source.Editor = view.Editor;
        view.Source.GraphStateLoad();
        //view.Editor.FitToScreen();
    }

    public RedGraph Source
    {
        get => (RedGraph)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public static readonly RoutedEvent NodeDoubleClickEvent = EventManager.RegisterRoutedEvent(nameof(NodeDoubleClick), RoutingStrategy.Direct, typeof(RoutedEventHandler), typeof(GraphEditorView));

    public event RoutedEventHandler NodeDoubleClick
    {
        add => AddHandler(NodeDoubleClickEvent, value);
        remove => RemoveHandler(NodeDoubleClickEvent, value);
    }

    private object _selectedItem;

    public object SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (SetField(ref _selectedItem, value))
            {
                SelectedNode = value as NodeViewModel;
            }
        }
    }

    private NodeViewModel _selectedNode;

    public NodeViewModel SelectedNode
    {
        get => _selectedNode;
        set
        {
            var previousNode = _selectedNode;
            if (!SetField(ref _selectedNode, value))
            {
                return;
            }

            if (value is not null || ReferenceEquals(NodeSelectionService.Instance.SelectedNode, previousNode))
            {
                NodeSelectionService.Instance.SelectedNode = value;
            }
        }
    }

    private ObservableCollection<object> _selectedNodes = new();

    public ObservableCollection<object> SelectedNodes
    {
        get => _selectedNodes;
        set => SetField(ref _selectedNodes, value);
    }

    public System.Windows.Point ViewportLocation { get; set; }

    private readonly AppViewModel _appViewModel;

    public GraphEditorView()
    {
        InitializeComponent();
        InitializeQuestConnectionRewiring();
        InitializeQuestMultiConnect();
        InitializeQuestConnectionClipboard();
        SfSkinManager.SetTheme(Editor, new Theme("Default"));

        _appViewModel = Locator.Current.GetService<AppViewModel>();
        Editor.PreviewKeyDown += Editor_OnPreviewKeyDown;
        ActionPalette.DismissRequested += (_, _) => CloseActionPalette();
        ActionPalette.ActionExecuted += (_, _) => CloseActionPalette();
        ActionPalettePopup.Closed += (_, _) =>
        {
            ActionPalette.Close();
            _actionPaletteQuestInsertionConnection = null;
        };
        ActionPalette.ShouldPlaceVariantsOnLeft = () =>
        {
            var spaceOnRight = Editor.ActualWidth -
                               (_actionPalettePopupHorizontalOffset + GraphActionPalette.MainPanelWidth);
            return spaceOnRight < GraphActionPalette.VariantPanelTotalWidth &&
                   _actionPalettePopupHorizontalOffset >= GraphActionPalette.VariantPanelTotalWidth;
        };

        Observable.FromEventPattern<RoutedEventHandler, RoutedEventArgs>(
            handler => Editor.ViewportUpdated += handler,
            handler => Editor.ViewportUpdated -= handler)
            .Throttle(TimeSpan.FromSeconds(1))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(x =>
            {
                ViewportUpdated();
            });
    }

    private void ViewportUpdated()
    {
        if (Source == null)
        {
            return;
        }

        Source.GraphStateSave();
    }

    internal void PrepareForClose()
    {
        var graph = Source;

        CloseActionPalette();
        SelectedNode = null;
        SelectedNodes.Clear();

        if (graph?.Editor == Editor)
        {
            graph.GraphStateSave();
            graph.Editor = null;
        }

        SetCurrentValue(SourceProperty, null);
    }

    private void ArrangeNodes()
    {
        if (Source == null)
        {
            return;
        }

        UpdateLayout();
        Source.ArrangeNodes();
        Source.GraphStateSave();
        Source.GraphCommentStateSave();
        Source.CenterOnSelectedNodes(SelectedNodes);
    }

    private void Editor_OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not NodifyEditor nodifyEditor || Source == null)
        {
            return;
        }

        nodifyEditor.ContextMenu ??= new ContextMenu();
        nodifyEditor.ContextMenu.Items.Clear();

        // Get the mouse position in graph coordinates for placing new nodes at cursor location
        var mousePosition = GetMousePositionInGraph(nodifyEditor);

        if (Source.GraphType is RedGraphType.Quest or RedGraphType.Scene or RedGraphType.Behavior)
        {
            OpenGraphActionPalette(mousePosition, Mouse.GetPosition(nodifyEditor));
            e.Handled = true;
            return;
        }

        nodifyEditor.ContextMenu.Items.Add(CreateMenuItem("Arrange Items", "ViewDashboard", ArrangeNodes));

        nodifyEditor.ContextMenu.Items.Add(CreateMenuItem("Hide/unhide sockets", "Eye", ToggleAllSockets));

        // Add paste option if clipboard has compatible data
        if (GraphClipboardManager.CanPaste(Source.GraphType))
        {
            nodifyEditor.ContextMenu.Items.Add(new Separator());
            nodifyEditor.ContextMenu.Items.Add(CreateMenuItem("Paste Node", "ContentPaste", "WolvenKitGreen", () =>
            {
                var copiedData = GraphClipboardManager.GetCopiedData();
                if (copiedData != null)
                {
                    Source.PasteNode(copiedData, mousePosition);
                }
            }));
        }

        nodifyEditor.ContextMenu.SetCurrentValue(ContextMenu.IsOpenProperty, true);

        e.Handled = true;
    }

    private void ToggleAllSockets()
    {
        if (Source?.Nodes == null)
        {
            return;
        }

        // Determine the new state based on the first node
        var firstNode = Source.Nodes.FirstOrDefault();
        if (firstNode == null)
        {
            return;
        }
        var newState = !firstNode.ShowUnusedSockets;

        // Apply the new state to all nodes
        foreach (var node in Source.Nodes)
        {
            node.ShowUnusedSockets = newState;
        }

        Source.GraphStateSave();
    }

    private void Node_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not Node { DataContext: NodeViewModel nvm } node || Source == null)
        {
            return;
        }

        node.ContextMenu ??= new ContextMenu();

        node.ContextMenu.Items.Clear();

        if (Source.GraphType == RedGraphType.Behavior)
        {
            if (nvm is BehaviorNodeViewModel behaviorNode && Source.CanAddBehaviorChild(behaviorNode))
            {
                var graphPosition = GetMousePositionInGraph(Editor);
                var popupPosition = Mouse.GetPosition(Editor);
                node.ContextMenu.Items.Add(CreateMenuItem("Add Child ...", "Plus", () =>
                {
                    node.ContextMenu.SetCurrentValue(ContextMenu.IsOpenProperty, false);
                    Dispatcher.BeginInvoke(
                        () => OpenGraphActionPalette(graphPosition, popupPosition, behaviorNode),
                        DispatcherPriority.ContextIdle);
                }));
                node.ContextMenu.Items.Add(new Separator());
            }

            var toggleBehaviorSlotsText = nvm.ShowUnusedSockets ? "Hide Structural Slots" : "Show Structural Slots";
            node.ContextMenu.Items.Add(CreateMenuItem(toggleBehaviorSlotsText, "Eye", "WolvenKitYellow", () =>
            {
                nvm.ShowUnusedSockets = !nvm.ShowUnusedSockets;
                Source.GraphStateSave();
            }));

            node.ContextMenu.SetCurrentValue(ContextMenu.IsOpenProperty, true);
            e.Handled = true;
            return;
        }

        var selectedGraphNodes = SelectedNodes.OfType<NodeViewModel>().Cast<object>().ToList();
        if (selectedGraphNodes.Count > 1)
        {
            if (Source.GraphType == RedGraphType.Quest)
            {
                node.ContextMenu.Items.Add(CreateMenuItem("Duplicate Selected Nodes", "ContentDuplicate", "WolvenKitYellow",
                    () => DuplicateSelectedQuestNodes()));
            }
            node.ContextMenu.Items.Add(CreateMenuItem("Destroy Nodes", "CloseBoxOutline", "WolvenKitRed", () => Source.RemoveNodes(selectedGraphNodes)));
            if (Source.GraphType == RedGraphType.Quest)
            {
                node.ContextMenu.Items.Add(CreateMenuItem("Convert to Phase", "FolderOutline", "WolvenKitRed", () => Source.CreatePhaseFromSelection(selectedGraphNodes)));
            }
            node.ContextMenu.SetCurrentValue(ContextMenu.IsOpenProperty, true);

            e.Handled = true;
            return;
        }

        if (node.DataContext is IDynamicInputNode dynamicInputNode)
        {
            node.ContextMenu.Items.Add(CreateMenuItem("Add Input", "PlusCircle", () => dynamicInputNode.AddInput()));
            node.ContextMenu.Items.Add(new Separator());
        }

        if (node.DataContext is IDynamicOutputNode dynamicOutputNode)
        {
            // Check if it's a section node for specific labeling
            string addLabel = dynamicOutputNode is scnSectionNodeWrapper ? "Add Event Socket" : "Add Output";

            node.ContextMenu.Items.Add(CreateMenuItem(addLabel, "PlusCircle", () => dynamicOutputNode.AddOutput()));
            node.ContextMenu.Items.Add(new Separator());
        }

        if (node.DataContext is scnChoiceNodeWrapper choice)
        {
            node.ContextMenu.Items.Add(CreateMenuItem("Add Choice", "PlusCircle", () => choice.AddChoice()));
            node.ContextMenu.Items.Add(new Separator());
        }

        if (node.DataContext is questSwitchNodeDefinitionWrapper switchNode)
        {
            node.ContextMenu.Items.Add(CreateMenuItem("Add Case", "PlusCircle", () => switchNode.AddCondition()));
            node.ContextMenu.Items.Add(new Separator());
        }

        if (node.DataContext is IGraphProvider graphProvider)
        {
            node.ContextMenu.Items.Add(CreateMenuItem("Recalculate sockets", "Play", "WolvenKitGreen", () => Source.RecalculateSockets(graphProvider)));
            node.ContextMenu.Items.Add(new Separator());
        }

        if (node.DataContext is questPhaseNodeDefinitionWrapper phaseNode)
        {
            node.ContextMenu.Items.Add(CreateMenuItem("Unpack phase", "PackageUp", "WolvenKitRed", () => Source.UnpackPhase(phaseNode)));
            node.ContextMenu.Items.Add(new Separator());
        }

        var toggleSocketsText = nvm.ShowUnusedSockets ? "Hide Unused Sockets" : "Show Unused Sockets";
        node.ContextMenu.Items.Add(CreateMenuItem(toggleSocketsText, "Eye", "WolvenKitYellow",() =>
        {
            nvm.ShowUnusedSockets = !nvm.ShowUnusedSockets;
            Source.GraphStateSave();
        }));

        if (node.DataContext is questSceneNodeDefinitionWrapper sceneNode)
        {
            node.ContextMenu.Items.Add(CreateMenuItem(
                "Add Scene To Project",
                "ArrowLeftCircle",
                "WolvenKitYellow",
                () => sceneNode.AddSceneToProject()));
            node.ContextMenu.Items.Add(new Separator());
        }

        if (Source.GraphType == RedGraphType.Quest)
        {
            node.ContextMenu.Items.Add(CreateMenuItem("Duplicate Selected Nodes", "ContentDuplicate", "WolvenKitYellow", () =>
            {
                if (!Editor.SelectedItems.Contains(nvm))
                {
                    Editor.SelectedItems.Clear();
                    Editor.SelectedItems.Add(nvm);
                }
                DuplicateSelectedQuestNodes();
            }));
        }
        else
        {
            node.ContextMenu.Items.Add(CreateMenuItem("Duplicate Node", "ContentDuplicate", "WolvenKitYellow", () => Source.DuplicateNode(nvm)));
        }
        node.ContextMenu.Items.Add(CreateMenuItem("Copy Node", "ContentCopy", "WolvenKitYellow", () => GraphClipboardManager.CopyNode(nvm, Source.GraphType)));
        if (Source.GraphType == RedGraphType.Quest && nvm is BaseQuestViewModel questAnchor)
        {
            AddQuestConnectionClipboardMenu(node.ContextMenu, questAnchor);
        }

        var templateData = GetTemplateData(nvm);
        if (templateData != null && RedTypeTemplateService.IsTypeTemplatable(templateData.GetType()))
        {
            node.ContextMenu.Items.Add(CreateMenuItem(
                "Create Template from Node",
                "ContentSaveOutline",
                "WolvenKitPurple",
                async () => await _appViewModel.SetActiveDialog(
                    new CreateTemplateFromChunkDialogViewModel(templateData, _redTypeTemplateService, _appViewModel))));
        }

        if (Source.GraphType == RedGraphType.Scene && node.DataContext is BaseSceneViewModel sceneViewModel)
        {
            node.ContextMenu.Items.Add(CreateMenuItem(
                "Detach Node",
                "LinkOff",
                "WolvenKitYellow",
                () => DetachNode(sceneViewModel)));

            if (!(sceneViewModel is scnStartNodeWrapper || sceneViewModel is scnEndNodeWrapper))
            {
                // Check if this node type should use a deletion marker
                bool shouldUseDeletionMarker = ShouldSceneNodeUseDeletionMarker(sceneViewModel);

                if (sceneViewModel is scnDeletionMarkerNodeWrapper)
                {
                    // Deletion markers can always be destroyed
                    node.ContextMenu.Items.Add(CreateMenuItem(
                        "Destroy Deletion Marker",
                        "CloseBoxOutline",
                        "WolvenKitRed",
                        () => Source.RemoveNode(sceneViewModel)));
                }
                else if (shouldUseDeletionMarker)
                {
                    // Critical scene nodes: show both Delete (soft) and Destroy (hard)
                    node.ContextMenu.Items.Add(CreateMenuItem(
                        "Delete Node",
                        "Delete",
                        "WolvenKitRed",
                        () => Source.ReplaceNodeWithDeletionMarker(sceneViewModel)));

                    node.ContextMenu.Items.Add(CreateMenuItem(
                        "Destroy Node",
                        "CloseBoxOutline",
                        "WolvenKitRed",
                        () => Source.RemoveNode(sceneViewModel)));
                }
                else
                {
                    // Non-critical scene nodes: only show Destroy
                    node.ContextMenu.Items.Add(CreateMenuItem(
                        "Destroy Node",
                        "CloseBoxOutline",
                        "WolvenKitRed",
                        () => Source.RemoveNode(sceneViewModel)));
                }
            }

            node.ContextMenu.Items.Add(new Separator());
        }

        if (Source.GraphType == RedGraphType.Quest && node.DataContext is BaseQuestViewModel questViewModel)
        {
            node.ContextMenu.Items.Add(CreateMenuItem(
                "Detach Node",
                "LinkOff",
                "WolvenKitYellow",
                () => DetachQuestNode(questViewModel)));

            if (!(questViewModel is questStartNodeDefinitionWrapper || questViewModel is questEndNodeDefinitionWrapper))
            {
                // Check if this node type should use a deletion marker
                bool shouldUseDeletionMarker = questViewModel.Data is questSignalStoppingNodeDefinition
                    || questViewModel.Data.GetType() == typeof(questSwitchNodeDefinition)
                    || questViewModel.Data.GetType() == typeof(questFlowControlNodeDefinition);

                if (questViewModel is questDeletionMarkerNodeDefinitionWrapper)
                {
                    // Deletion markers can always be destroyed
                    node.ContextMenu.Items.Add(CreateMenuItem(
                        "Destroy Deletion Marker",
                        "CloseBoxOutline",
                        "WolvenKitRed",
                        () => Source.RemoveNode(questViewModel)));
                }
                else if (shouldUseDeletionMarker)
                {
                    // Signal-stopping nodes: show both Delete (soft) and Destroy (hard)
                    node.ContextMenu.Items.Add(CreateMenuItem(
                        "Delete Node",
                        "Delete",
                        "WolvenKitRed",
                        () => Source.ReplaceNodeWithQuestDeletionMarker(questViewModel)));

                    node.ContextMenu.Items.Add(CreateMenuItem(
                        "Destroy Node",
                        "CloseBoxOutline",
                        "WolvenKitRed",
                        () => Source.RemoveNode(questViewModel)));
                }
                else
                {
                    // Non-signal-stopping nodes: only show Destroy
                    node.ContextMenu.Items.Add(CreateMenuItem(
                        "Destroy Node",
                        "CloseBoxOutline",
                        "WolvenKitRed",
                        () => Source.RemoveNode(questViewModel)));
                }
            }

            node.ContextMenu.Items.Add(new Separator());
        }

        // For other node types that aren't scene or quest specific, show destroy option
        if (!(Source.GraphType == RedGraphType.Scene && node.DataContext is BaseSceneViewModel) &&
            !(Source.GraphType == RedGraphType.Quest && node.DataContext is BaseQuestViewModel))
        {
            node.ContextMenu.Items.Add(CreateMenuItem("Destroy Node", "CloseBoxOutline", "WolvenKitRed", () => Source.RemoveNode(nvm)));
        }

        // Add deletion markers info for scene graphs for now
        if (Source.GraphType == RedGraphType.Scene)
        {
            node.ContextMenu.Items.Add(new Separator());

            // Create help item using XAML-defined style
            var infoItem = new MenuItem
            {
                Header = "What do these mean?",
                Style = (Style)Resources["HelpMenuItemStyle"]
            };

            infoItem.Click += (_, _) => {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://wiki.redmodding.org/wolvenkit/wolvenkit-app/editor/scene-editor") { UseShellExecute = true });
                }
                catch (Exception)
                {
                    // Silently handle any exceptions when opening the link
                }
            };

            node.ContextMenu.Items.Add(infoItem);
        }

        node.ContextMenu.SetCurrentValue(ContextMenu.IsOpenProperty, true);

        e.Handled = true;
    }

    private static IRedType GetTemplateData(NodeViewModel node)
    {
        if (node.Data is scnQuestNode sceneQuestNode)
        {
            return sceneQuestNode.QuestNode?.Chunk;
        }

        return node.Data;
    }

    public bool AddCommentFromCurrentCursor()
    {
        if (Source?.GraphType is not (RedGraphType.Quest or RedGraphType.Scene))
        {
            return false;
        }

        Source.AddComment(GetMousePositionInGraph(Editor), SelectedNodes);
        return true;
    }

    private void Comment_OnResizeCompleted(object sender, Nodify.Events.ResizeEventArgs e)
    {
        if (sender is not GroupingNode { DataContext: GraphCommentViewModel comment })
        {
            return;
        }

        comment.Width = e.NewSize.Width;
        comment.Height = e.NewSize.Height;
        Source?.GraphCommentStateSave();
    }

    private void CommentText_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2)
        {
            return;
        }

        if (sender is not FrameworkElement { DataContext: GraphCommentViewModel comment })
        {
            return;
        }

        comment.IsEditing = true;
        e.Handled = true;
    }

    private void CommentEditTextBox_OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox { IsVisible: true } textBox)
        {
            return;
        }

        textBox.Dispatcher.BeginInvoke(new Action(() =>
        {
            textBox.Focus();
            textBox.SelectAll();
        }), DispatcherPriority.Input);
    }

    private void CommentEditTextBox_OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        EndCommentTextEdit(sender);
    }

    private void CommentEditTextBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape))
        {
            return;
        }

        EndCommentTextEdit(sender);
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private static void EndCommentTextEdit(object sender)
    {
        if (sender is not TextBox { DataContext: GraphCommentViewModel comment } textBox)
        {
            return;
        }

        textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        comment.IsEditing = false;
    }

    private void Comment_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not GroupingNode { DataContext: GraphCommentViewModel comment } groupingNode || Source == null)
        {
            return;
        }

        groupingNode.ContextMenu ??= new ContextMenu();
        groupingNode.ContextMenu.Items.Clear();
        var graphPosition = GetMousePositionInGraph(Editor);
        var popupPosition = Mouse.GetPosition(Editor);
        groupingNode.ContextMenu.Items.Add(CreateMenuItem("Add Node ...", "Plus", () =>
        {
            groupingNode.ContextMenu.SetCurrentValue(ContextMenu.IsOpenProperty, false);
            Dispatcher.BeginInvoke(
                () => OpenGraphActionPalette(graphPosition, popupPosition),
                DispatcherPriority.ContextIdle);
        }));
        groupingNode.ContextMenu.Items.Add(new Separator());
        groupingNode.ContextMenu.Items.Add(CreateCommentColorMenu(comment));
        groupingNode.ContextMenu.Items.Add(CreateMenuItem(
            "Reset Comment Size",
            "Resize",
            "WolvenKitYellow",
            () =>
            {
                comment.ResetSize();
                Source.GraphCommentStateSave();
            }));
        groupingNode.ContextMenu.Items.Add(new Separator());
        groupingNode.ContextMenu.Items.Add(CreateMenuItem(
            "Delete Comment",
            "Delete",
            "WolvenKitRed",
            () => Source.RemoveCommentCommand.Execute(comment)));
        groupingNode.ContextMenu.SetCurrentValue(ContextMenu.IsOpenProperty, true);

        e.Handled = true;
    }

    private MenuItem CreateCommentColorMenu(GraphCommentViewModel comment)
    {
        var item = new MenuItem
        {
            Header = "Change Color",
            Padding = (Thickness)Application.Current.Resources["WolvenKitMarginTiny"]!,
            Icon = new IconBox
            {
                IconPack = IconPackType.Material,
                Kind = "Palette",
                Foreground = (Brush)Application.Current.Resources["WolvenKitYellow"]!,
                Margin = (Thickness)Application.Current.Resources["WolvenKitMarginTiny"]!,
                Size = (double)Application.Current.Resources["WolvenKitIconMicro"]!
            }
        };

        foreach (var (name, color) in s_commentColorPresets)
        {
            var colorItem = new MenuItem
            {
                Header = name,
                IsCheckable = true,
                IsChecked = string.Equals(comment.AccentColor, color, StringComparison.OrdinalIgnoreCase),
                Padding = (Thickness)Application.Current.Resources["WolvenKitMarginTiny"]!,
                Icon = new Rectangle
                {
                    Width = 12,
                    Height = 12,
                    Fill = CreateCommentSwatchBrush(color),
                    Stroke = Brushes.White,
                    StrokeThickness = 0.5,
                    Margin = (Thickness)Application.Current.Resources["WolvenKitMarginTiny"]!
                }
            };

            colorItem.Click += (_, _) => comment.AccentColor = color;
            item.Items.Add(colorItem);
        }

        return item;
    }

    private static Brush CreateCommentSwatchBrush(string colorValue)
    {
        try
        {
            if (ColorConverter.ConvertFromString(colorValue) is Color color)
            {
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                return brush;
            }
        }
        catch (FormatException)
        {
        }

        return Brushes.Gold;
    }


    private void Editor_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || e.IsRepeat ||
            Source?.GraphType is not (RedGraphType.Quest or RedGraphType.Scene) ||
            !Editor.IsKeyboardFocusWithin || !Editor.IsMouseOver ||
            ActionPalettePopup.IsOpen || Editor.ContextMenu?.IsOpen == true ||
            IsGraphTextInputFocused())
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var actionId = GraphActionShortcuts.GetActionId(_settingsManager?.GraphActionShortcuts, key, Keyboard.Modifiers);
        if (actionId is null)
        {
            return;
        }

        // IsMouseOver can stay true outside the viewport while Nodify captures the mouse.
        var mousePosition = Mouse.GetPosition(Editor);
        if (mousePosition.X < 0 || mousePosition.Y < 0 ||
            mousePosition.X >= Editor.ActualWidth || mousePosition.Y >= Editor.ActualHeight)
        {
            return;
        }

        EnsureNodeTemplateOptions();
        _nodeTemplateOptions.RefreshFromRegistry();
        var actions = CreateGraphActionPaletteItems(Source, GetMousePositionInGraph(Editor, mousePosition));
        var action = actions.SelectMany(item => item.Variants.Prepend(item))
            .FirstOrDefault(item => item.ActionId == actionId);
        if (action?.TryExecute() == true)
        {
            e.Handled = true;
        }
    }

    internal static bool IsGraphTextInputFocused()
    {
        for (var element = Keyboard.FocusedElement as DependencyObject; element is not null;)
        {
            if (element is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox or ComboBox or
                System.Windows.Documents.TextElement)
            {
                return true;
            }

            element = element is Visual
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }

        return false;
    }

    public void OpenActionPaletteAtViewportCenter()
    {
        if (Source?.GraphType is not (RedGraphType.Quest or RedGraphType.Scene or RedGraphType.Behavior))
        {
            return;
        }

        var popupPosition = new System.Windows.Point(
            Math.Max(8, (Editor.ActualWidth - 380) / 2),
            Math.Max(8, (Editor.ActualHeight - 500) / 2));
        OpenGraphActionPalette(GetViewportCenter(), popupPosition);
    }

    private void OpenGraphActionPalette(
        System.Windows.Point graphPosition,
        System.Windows.Point popupPosition,
        BehaviorNodeViewModel behaviorParent = null,
        QuestConnectionViewModel insertionConnection = null)
    {
        if (Source?.GraphType is not (RedGraphType.Quest or RedGraphType.Scene or RedGraphType.Behavior))
        {
            return;
        }

        CloseActionPalette();
        _actionPaletteQuestInsertionConnection = insertionConnection;
        var selectedParents = SelectedNodes.OfType<BehaviorNodeViewModel>()
            .Where(parent => Source.Nodes.Contains(parent)).Take(2).ToList();
        _actionPaletteBehaviorParent = behaviorParent ?? (selectedParents.Count == 1 ? selectedParents[0] : null);
        _actionPaletteCreatesBehaviorRoot = Source.GraphType == RedGraphType.Behavior && Source.CanCreateBehaviorRoot();
        ActionPalette.ContextDescription = insertionConnection is null
            ? ""
            : "Replace this connection with a node. Choose sockets when the new node has multiple inputs or outputs.";
        EnsureNodeTemplateOptions();
        _nodeTemplateOptions.RefreshFromRegistry();
        _actionPaletteGraphPosition = graphPosition;
        _actionPalettePopupHorizontalOffset = Math.Max(4, popupPosition.X);
        ActionPalettePopup.SetCurrentValue(
            System.Windows.Controls.Primitives.Popup.HorizontalOffsetProperty,
            _actionPalettePopupHorizontalOffset);
        ActionPalettePopup.SetCurrentValue(System.Windows.Controls.Primitives.Popup.VerticalOffsetProperty, Math.Max(4, popupPosition.Y));
        ActionPalette.Open(
            insertionConnection is not null ? "Insert Node on Connection" : Source.GraphType switch
            {
                RedGraphType.Quest => "All Actions for Quest Graph",
                RedGraphType.Scene => "All Actions for Scene Graph",
                RedGraphType.Behavior => "All Actions for Behavior Graph",
                _ => throw new InvalidOperationException("Unsupported graph action palette.")
            },
            CreateGraphActionPaletteItems(Source, graphPosition, insertionConnection));
        ActionPalettePopup.SetCurrentValue(System.Windows.Controls.Primitives.Popup.IsOpenProperty, true);
    }

    private void CloseActionPalette()
    {
        _actionPaletteQuestInsertionConnection = null;
        ActionPalette.Close();
        ActionPalettePopup.SetCurrentValue(System.Windows.Controls.Primitives.Popup.IsOpenProperty, false);
    }

    private void NodeTemplateOptions_OnPostRefresh(object sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (ActionPalettePopup.IsOpen && Source?.GraphType is (RedGraphType.Quest or RedGraphType.Scene or RedGraphType.Behavior))
            {
                ActionPalette.RefreshItems(CreateGraphActionPaletteItems(Source, _actionPaletteGraphPosition, _actionPaletteQuestInsertionConnection));
            }
        });
    }

    private void EnsureNodeTemplateOptions()
    {
        if (_nodeTemplateOptions is not null)
        {
            return;
        }

        _nodeTemplateOptions = new RedTypeTemplateDropdownViewModel(_redTypeTemplateService, _loggerService);
        _nodeTemplateOptions.PostRefresh += NodeTemplateOptions_OnPostRefresh;
    }

    private IReadOnlyList<GraphActionPaletteItem> CreateGraphActionPaletteItems(RedGraph graph, System.Windows.Point position,
        QuestConnectionViewModel insertionConnection = null)
    {
        if (graph.GraphType == RedGraphType.Behavior)
        {
            return CreateBehaviorActionPaletteItems(graph, position);
        }

        var items = new List<GraphActionPaletteItem>();
        var hasNodeSelection = SelectedNodes.OfType<NodeViewModel>().Any();

        if (insertionConnection is null)
        {
            items.Add(CreatePaletteCommand(
                hasNodeSelection ? "Add Comment Around Selection" : "Add Comment",
                "Graph",
                "CommentTextOutline",
                "comment annotation note",
                () => graph.AddComment(position, SelectedNodes)));
            items.Add(CreatePaletteCommand("Arrange Items", "Graph", "ViewDashboard", "layout arrange nodes", ArrangeNodes));
            items.Add(CreatePaletteCommand("Hide/unhide sockets", "Graph", "Eye", "toggle sockets connectors", ToggleAllSockets));

            if (GraphClipboardManager.CanPaste(graph.GraphType))
            {
                items.Add(CreatePaletteCommand("Paste Node", "Graph", "ContentPaste", "clipboard paste", () =>
                {
                    if (GraphClipboardManager.GetCopiedData() is { } copiedData)
                    {
                        graph.PasteNode(copiedData, position);
                    }
                }));
            }

        }

        GraphActionPaletteItem CreateItem(Type type, string category) => CreateNodePaletteItem(
            graph, type, category, position, GraphNodeCreationCatalog.IsSearchOnly(type),
            createNode: insertionConnection is null ? null :
                template => InsertAndSelectQuestNode(graph, insertionConnection, type, position, template));

        var availableTypes = GetNodeCreationTypes(graph);
        var typeMap = availableTypes
            .GroupBy(entry => entry.Type.Name)
            .ToDictionary(group => group.Key, group => group.First());
        var addedTypes = new HashSet<Type>();

        void AddNodeType(string typeName, string category)
        {
            if (typeMap.TryGetValue(typeName, out var entry) && addedTypes.Add(entry.Type))
            {
                items.Add(CreateItem(entry.Type, category));
            }
        }

        foreach (var typeName in GraphNodeCreationCatalog.CommonTypeNames)
        {
            AddNodeType(typeName, "Common");
        }

        foreach (var category in GraphNodeCreationCatalog.Categories)
        {
            if (category.Name == "Debug" && graph.GraphType == RedGraphType.Scene)
            {
                const string title = "UIManager (Debug Warning)";
                items.Add(new GraphActionPaletteItem
                {
                    Title = title,
                    ActionId = "preset:scene:DebugWarning",
                    Subtitle = "Preset scene node",
                    Category = category.Name,
                    SearchText = "debug warning ui manager",
                    Glyph = GraphNodeStyling.GetIconForNodeTitle(title),
                    Execute = () =>
                    {
                        var nodeId = graph.CreateCompositeSceneNode("DebugWarning", typeof(questUIManagerNodeDefinition), position);
                        if (ReferenceEquals(Source, graph))
                        {
                            SelectNodeById(nodeId);
                        }
                    }
                });
            }

            foreach (var typeName in category.TypeNames.Where(typeName => typeName is not null))
            {
                AddNodeType(typeName, category.Name);
            }
        }

        foreach (var entry in availableTypes
                     .Where(entry => !addedTypes.Contains(entry.Type))
                     .OrderBy(entry => entry.Category)
                     .ThenBy(entry => GraphNodeStyling.GetTitleForNodeType(entry.Type)))
        {
            var category = entry.Category == "Scene" ? "Other Scene Nodes" : "Other Quest Nodes";
            items.Add(CreateItem(entry.Type, category));
        }

        return items;
    }

    private IReadOnlyList<GraphActionPaletteItem> CreateBehaviorActionPaletteItems(RedGraph graph, System.Windows.Point position)
    {
        // Keep the opening context stable when template options refresh asynchronously.
        var parent = _actionPaletteBehaviorParent;
        var createRoot = _actionPaletteCreatesBehaviorRoot;
        var canCreate = createRoot
            ? graph.CanCreateBehaviorRoot()
            : parent is not null && graph.CanAddBehaviorChild(parent);
        ActionPalette.ContextDescription = canCreate
            ? createRoot ? "Create the root of this behavior tree." : $"Add a child to {parent.Title}."
            : "Select one composite or a decorator with an empty child slot to add a node.";

        var items = new List<GraphActionPaletteItem>
        {
            CreatePaletteCommand("Arrange Items", "Graph", "ViewDashboard", "layout arrange nodes", ArrangeNodes),
            CreatePaletteCommand("Hide/unhide sockets", "Graph", "Eye", "toggle sockets connectors", ToggleAllSockets)
        };

        var commonTypes = new[]
        {
            typeof(AIbehaviorSelectorTreeNodeDefinition),
            typeof(AIbehaviorSequenceTreeNodeDefinition),
            typeof(AIbehaviorParallelNodeDefinition),
            typeof(AIbehaviorIfElseNodeDefinition),
            typeof(AIbehaviorRepeatNodeDefinition),
            typeof(AIbehaviorInstantTaskNodeDefinition),
            typeof(AIbehaviorMonitorTaskNodeDefinition),
            typeof(AIbehaviorInstantConditionNodeDefinition),
            typeof(AIbehaviorMonitorConditionNodeDefinition),
            typeof(AIbehaviorSubtreeDefinition),
            typeof(AIbehaviorIncludedTreeDefinition)
        };

        foreach (var type in commonTypes.Where(graph.GetBehaviorNodeTypes().Contains))
        {
            items.Add(CreateNodePaletteItem(graph, type, "Common", position,
                isEnabled: canCreate,
                createNode: template => CreateAndSelectBehaviorNode(graph, parent, createRoot, type, position, template)));
        }

        foreach (var type in graph.GetBehaviorNodeTypes()
                     .OrderBy(GetBehaviorNodeCategory)
                     .ThenBy(GraphNodeStyling.GetTitleForNodeType))
        {
            items.Add(CreateNodePaletteItem(graph, type, GetBehaviorNodeCategory(type), position,
                isEnabled: canCreate,
                createNode: template => CreateAndSelectBehaviorNode(graph, parent, createRoot, type, position, template)));
        }

        return items;
    }

    private static string GetBehaviorNodeCategory(Type type)
    {
        if (typeof(AIbehaviorCompositeTreeNodeDefinition).IsAssignableFrom(type)) return "Composite";
        if (typeof(AIbehaviorConditionNodeDefinition).IsAssignableFrom(type)) return "Conditions";
        if (typeof(AIbehaviorTaskNodeDefinition).IsAssignableFrom(type)) return "Tasks";
        if (typeof(AIbehaviorNestedTreeDefinition).IsAssignableFrom(type)) return "Trees";
        if (typeof(AIbehaviorDecoratorNodeDefinition).IsAssignableFrom(type) ||
            type == typeof(AIbehaviorIdleTreeNodeDefinition) ||
            type == typeof(AIbehaviorSucceederNodeDefinition) ||
            type == typeof(AIbehaviorFailerNodeDefinition)) return "Control";
        return "Other Behavior Nodes";
    }

    private void CreateAndSelectBehaviorNode(
        RedGraph graph,
        BehaviorNodeViewModel parent,
        bool createRoot,
        Type type,
        System.Windows.Point position,
        RedTypeTemplateSelectionOption template)
    {
        // A parent may have been replaced or the document switched since opening the palette.
        if (!ReferenceEquals(Source, graph) ||
            !(createRoot ? graph.CanCreateBehaviorRoot() : parent is not null && graph.CanAddBehaviorChild(parent)))
        {
            return;
        }

        var nodeId = createRoot
            ? graph.CreateBehaviorRoot(type, position, template)
            : graph.AddBehaviorChild(parent, type, template);
        SelectNodeById(nodeId);
        var createdNode = graph.Nodes.First(node => node.UniqueId == nodeId);
        Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(Source, graph) && graph.Nodes.Contains(createdNode))
            {
                UpdateLayout();
                graph.CenterOnSelectedNodes(new List<object> { createdNode });
            }
        }, DispatcherPriority.Loaded);
    }

    private GraphActionPaletteItem CreateNodePaletteItem(
        RedGraph graph,
        Type type,
        string category,
        System.Windows.Point position,
        bool isSearchOnly = false,
        bool isEnabled = true,
        Action<RedTypeTemplateSelectionOption> createNode = null)
    {
        createNode ??= template => CreateAndSelectNode(graph, type, position, template);
        var title = GraphNodeStyling.GetTitleForNodeType(type);
        var glyph = GraphNodeStyling.GetIconForNodeTitle(title);
        var templateOptions = GetTemplateOptions(type);
        var defaultTemplate = GetDefaultTemplateOption(templateOptions);
        var templateCount = templateOptions.Count(option => option.Source != RedTypeTemplateSelectionOptionSource.Raw);
        IReadOnlyList<GraphActionPaletteItem> variants = [];

        if (templateCount > 0)
        {
            variants = templateOptions
                .OrderBy(option => option.Name == "default" ? 0 : option.Source == RedTypeTemplateSelectionOptionSource.Raw ? 2 : 1)
                .ThenBy(option => option.Name)
                .Select(option => new GraphActionPaletteItem
                {
                    ActionId = GraphActionShortcuts.GetTemplateActionId(type, option),
                    Title = option.Name,
                    Subtitle = option.Source switch
                    {
                        RedTypeTemplateSelectionOptionSource.User => "User template",
                        RedTypeTemplateSelectionOptionSource.System => "System template",
                        _ => "No template"
                    },
                    Category = category,
                    SearchText = $"{type.Name} {title} {option.Name} template",
                    Glyph = glyph,
                    ParentTitle = title,
                    IsVariant = true,
                    IsEnabled = isEnabled,
                    Execute = () => createNode(option)
                })
                .ToList();
        }

        return new GraphActionPaletteItem
        {
            ActionId = GraphActionShortcuts.GetNodeActionId(type),
            Title = title,
            Subtitle = type.Name,
            Category = category,
            SearchText = type.FullName ?? type.Name,
            Glyph = glyph,
            IsSearchOnly = isSearchOnly,
            IsEnabled = isEnabled,
            Variants = variants,
            Execute = () => createNode(defaultTemplate)
        };
    }

    private IReadOnlyList<RedTypeTemplateSelectionOption> GetTemplateOptions(Type type)
    {
        if (_nodeTemplateOptions.TemplatesByType.TryGetValue(type, out var templates))
        {
            return templates;
        }

        return
        [
            new RedTypeTemplateSelectionOption("No Template", type, "", RedTypeTemplateSelectionOptionSource.Raw)
        ];
    }

    private static RedTypeTemplateSelectionOption GetDefaultTemplateOption(IReadOnlyList<RedTypeTemplateSelectionOption> templates) =>
        templates.FirstOrDefault(option => option is { Name: "default", Source: RedTypeTemplateSelectionOptionSource.User }) ??
        templates.FirstOrDefault(option => option is { Name: "default", Source: RedTypeTemplateSelectionOptionSource.System }) ??
        templates.First(option => option.Source == RedTypeTemplateSelectionOptionSource.Raw);

    private void CreateAndSelectNode(
        RedGraph graph,
        Type type,
        System.Windows.Point position,
        RedTypeTemplateSelectionOption template)
    {
        var nodeId = CreateNode(graph, type, position, template);
        if (ReferenceEquals(Source, graph))
        {
            SelectNodeById(nodeId);
        }
    }

    private void InsertAndSelectQuestNode(RedGraph graph, QuestConnectionViewModel connection,
        Type type, System.Windows.Point position, RedTypeTemplateSelectionOption template)
    {
        CloseActionPalette();
        if (!ReferenceEquals(Source, graph) || !graph.CanInsertQuestNodeOnConnection(connection))
        {
            return;
        }

        BaseQuestViewModel candidate = null;
        var inserted = false;
        try
        {
            candidate = graph.PrepareQuestNode(type, position, template);
            var inputs = RedGraph.GetQuestInsertionInputs(candidate);
            var outputs = RedGraph.GetQuestInsertionOutputs(candidate);
            if (inputs.Count == 0 || outputs.Count == 0)
            {
                Interactions.ShowMessageBox("This node/template needs an ordinary input and output socket to be inserted. The connection was not changed.",
                    "Cannot Insert Node", WMessageBoxButtons.Ok, WMessageBoxImage.Information);
                return;
            }

            var input = ChooseQuestInsertionSocket(inputs, "input", "Connect the original source to this input.");
            if (input is null)
            {
                return;
            }
            var output = ChooseQuestInsertionSocket(outputs, "output", "Connect this output to the original destination.");
            if (output is null)
            {
                return;
            }

            if (!ReferenceEquals(Source, graph) || !graph.TryInsertQuestNode(connection, candidate, input, output))
            {
                Interactions.ShowMessageBox("The graph or selected sockets no longer support this insertion. The connection was not changed.",
                    "Cannot Insert Node", WMessageBoxButtons.Ok, WMessageBoxImage.Information);
                return;
            }

            inserted = true;
            SelectNodeById(candidate.UniqueId);
            graph.GraphStateSave();
        }
        catch (Exception exception)
        {
            _loggerService?.Error(exception);
            Interactions.ShowMessageBox(inserted
                    ? "The node was inserted, but the editor could not finish updating its view. See the log for details."
                    : "The node could not be inserted. The connection was not changed. See the log for details.",
                "Insert Node on Connection", WMessageBoxButtons.Ok, WMessageBoxImage.Error);
        }
        finally
        {
            if (!inserted)
            {
                candidate?.Dispose();
            }
        }
    }

    private static T ChooseQuestInsertionSocket<T>(IReadOnlyList<T> sockets, string kind, string description)
        where T : BaseConnectorViewModel
    {
        if (sockets.Count == 1)
        {
            return sockets[0];
        }

        // Index labels distinguish templates with duplicate socket names; selection maps to the exact socket.
        var choices = sockets.Select((socket, index) => (Label: $"{socket.Name} [{index + 1}]", Socket: socket))
            .ToDictionary(choice => choice.Label, choice => choice.Socket);
        var selected = Interactions.AskForDropdownOption((choices.Keys.ToList(), $"Choose {kind} socket",
            description, null, false, null));
        return choices.TryGetValue(selected, out var socket) ? socket : null;
    }

    private static GraphActionPaletteItem CreatePaletteCommand(
        string title,
        string category,
        string iconKind,
        string searchText,
        Action execute) => new()
        {
            Title = title,
            Category = category,
            IconKind = iconKind,
            SearchText = searchText,
            Execute = execute
        };

    private static List<(Type Type, string Category)> GetNodeCreationTypes(RedGraph graph)
    {
        if (graph.GraphType == RedGraphType.Quest)
        {
            return graph.GetQuestNodeTypes()
                .Select(type => (type, "Quest"))
                .ToList();
        }

        if (graph.GraphType == RedGraphType.Scene)
        {
            return graph.GetSceneNodeTypes()
                .Select(type => (type, "Scene"))
                .Concat(graph.GetQuestNodeTypesForScene().Select(type => (type, "Quest")))
                .ToList();
        }

        return [];
    }

    private uint CreateNode(RedGraph graph, Type type, System.Windows.Point position, RedTypeTemplateSelectionOption template = null) =>
        graph.GraphType switch
        {
            RedGraphType.Quest => graph.CreateQuestNode(type, position, template),
            RedGraphType.Scene => graph.CreateSceneNode(type, position, template),
            _ => throw new InvalidOperationException($"Cannot create a quest or scene node in a {graph.GraphType} graph.")
        };

    private static MenuItem CreateMenuItem(string header, Action click) => CreateMenuItem(header, "Empty", null, click);

    private static MenuItem CreateMenuItem(string header, string iconKind, Action click) => CreateMenuItem(header, iconKind, "", click);

    private static MenuItem CreateMenuItem(string header, string iconKind, string iconColor, Action click)
    {
        var item = new MenuItem
        {
            Header = header,
            Padding = (Thickness)Application.Current.Resources["WolvenKitMarginTiny"]!,
        };

        if (iconKind != null)
        {
            var icon = new IconBox
            {
                IconPack = (iconKind == "Empty") ? IconPackType.Empty : IconPackType.Material,
                Kind = (iconKind == "Empty") ? "" : iconKind,
                Margin = (Thickness)Application.Current.Resources["WolvenKitMarginTiny"]!,
                Size = (double)Application.Current.Resources["WolvenKitIconMicro"]!
            };

            if (iconColor != null)
            {
                icon.Foreground = (Brush)Application.Current.Resources[iconColor] ?? Brushes.White;
            }
            item.Icon = icon;
        }
        item.Click += (_, _) => click();
        return item;
    }

    /// <summary>
    /// Create a menu item with emoji in the text and no additional icon
    /// </summary>
    private static MenuItem CreateEmojiMenuItem(string headerWithEmoji, Action click, double leftMargin = -30)
    {
        var item = new MenuItem
        {
            Header = headerWithEmoji,
            Padding = (Thickness)Application.Current.Resources["WolvenKitMarginSmall"]!,
            Icon = "", // Empty string to minimize icon space
            Margin = new Thickness(leftMargin, 0, 0, 0) // Adjustable left margin
        };
        item.Click += (_, _) => click();
        return item;
    }

    private static MenuItem CreateCategoryMenuItem(string categoryName)
    {
        return new MenuItem
        {
            Header = categoryName,
            Padding = (Thickness)Application.Current.Resources["WolvenKitMarginTiny"]!,
            Icon = new IconBox
            {
                IconPack = IconPackType.Material,
                Kind = "FolderOutline",
                Margin = (Thickness)Application.Current.Resources["WolvenKitMarginTiny"]!,
                Size = (double)Application.Current.Resources["WolvenKitIconMicro"]!
            }
        };
    }

    private static readonly string[] s_commonBehaviorNodeTypeNames =
    {
        nameof(AIbehaviorSelectorTreeNodeDefinition),
        nameof(AIbehaviorSequenceTreeNodeDefinition),
        nameof(AIbehaviorParallelNodeDefinition),
        nameof(AIbehaviorIfElseNodeDefinition),
        nameof(AIbehaviorRepeatNodeDefinition),
        nameof(AIbehaviorInstantTaskNodeDefinition),
        nameof(AIbehaviorMonitorTaskNodeDefinition),
        nameof(AIbehaviorInstantConditionNodeDefinition),
        nameof(AIbehaviorMonitorConditionNodeDefinition),
        nameof(AIbehaviorSubtreeDefinition),
        nameof(AIbehaviorIncludedTreeDefinition),
        nameof(AIbehaviorIdleTreeNodeDefinition),
        nameof(AIbehaviorSucceederNodeDefinition),
        nameof(AIbehaviorFailerNodeDefinition)
    };

    private void AddBehaviorNodeCreationItems(MenuItem parentMenu, Action<NodeCreationParams> createNode, IEnumerable<Type> nodeTypesOverride = null)
    {
        var nodeTypes = (nodeTypesOverride ?? Source.GetBehaviorNodeTypes()).ToList();
        var typeMap = nodeTypes.ToDictionary(type => type.Name, type => type);
        var types = nodeTypes
            .Select(type => new TypeEntry(GraphNodeStyling.GetTitleForNodeType(type), "", type))
            .OrderBy(entry => entry.Name)
            .ToList();

        parentMenu.Items.Add(CreateMenuItem("Search Node ...", "Magnify", "WolvenKitYellow", async () =>
        {
            await _appViewModel.SetActiveDialog(new TypeSelectorDialogViewModel(_redTypeTemplateService, _loggerService, types)
            {
                DialogHandler = model =>
                {
                    _appViewModel.CloseDialogCommand.Execute(null);
                    if (model is TypeSelectorDialogViewModel { SelectedEntry.UserData: Type selectedType } tsdvm)
                    {
                        createNode(new NodeCreationParams(selectedType, tsdvm.RedTypeTemplateDropdownViewModel.SelectedRedTypeTemplate));
                    }
                }
            });
        }));

        parentMenu.Items.Add(new Separator());

        foreach (var typeName in s_commonBehaviorNodeTypeNames)
        {
            AddBehaviorNodeToMenu(parentMenu, typeName, typeMap, createNode, true);
        }
    }

    private void AddBehaviorNodeToMenu(MenuItem parentMenu, string typeName, Dictionary<string, Type> typeMap, Action<NodeCreationParams> createNode, bool isRootItem = false)
    {
        if (!typeMap.TryGetValue(typeName, out var nodeType))
        {
            return;
        }

        var displayName = GraphNodeStyling.GetTitleForNodeType(nodeType);
        var emoji = GraphNodeStyling.GetIconForNodeTitle(displayName);
        var leftMargin = isRootItem ? -30 : -15;
        parentMenu.Items.Add(CreateEmojiMenuItem($"{emoji}   {displayName}", () => createNode(new NodeCreationParams(nodeType)), leftMargin));
    }

    #region INotifyPropertyChanged

    public event PropertyChangedEventHandler PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    #endregion

    private void Node_OnMouseDoubleClick(object sender, MouseButtonEventArgs e) => RaiseEvent(new RoutedEventArgs(NodeDoubleClickEvent));

    // Removed ShowSectionOutputPreview - no longer using dialog for add output

    public void Connection_OnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Connection connection && connection.DataContext is ConnectionViewModel connectionViewModel && Source != null)
        {
            if (Source.GraphType == RedGraphType.Behavior)
            {
                if (Source.CanEditBehaviorChildConnection(connectionViewModel))
                {
                    ShowBehaviorConnectionContextMenu(connection, connectionViewModel);
                }

                e.Handled = true;
                return;
            }

            // Clear other selections and select this connection to highlight it
            Editor.SelectedItems.Clear();
            Editor.SelectedItems.Add(connectionViewModel);

            // Also set the IsSelected property on the view model for binding
            connectionViewModel.IsSelected = true;

            var contextMenu = new ContextMenu();

            if (Source.GraphType == RedGraphType.Quest && connectionViewModel is QuestConnectionViewModel insertionConnection)
            {
                var graph = Source;
                var graphPosition = GetMousePositionInGraph(Editor);
                var popupPosition = Mouse.GetPosition(Editor);
                var insertItem = CreateMenuItem("Insert Node on Connection...", "Plus", () =>
                {
                    contextMenu.SetCurrentValue(ContextMenu.IsOpenProperty, false);
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (ReferenceEquals(Source, graph) && graph.CanInsertQuestNodeOnConnection(insertionConnection))
                        {
                            OpenGraphActionPalette(graphPosition, popupPosition, insertionConnection: insertionConnection);
                        }
                    }, DispatcherPriority.ContextIdle);
                });
                insertItem.IsEnabled = graph.CanInsertQuestNodeOnConnection(insertionConnection);
                insertItem.ToolTip = "Insert a node on an ordinary flow connection. Cut links cannot be split.";
                ToolTipService.SetShowOnDisabled(insertItem, true);
                contextMenu.Items.Add(insertItem);
                foreach (var endpoint in new[] { QuestConnectionEndpoint.Source, QuestConnectionEndpoint.Destination })
                {
                    var rewireEndpoint = endpoint;
                    var rewireItem = CreateMenuItem($"Rewire {endpoint}...", "LinkVariant", () =>
                    {
                        contextMenu.SetCurrentValue(ContextMenu.IsOpenProperty, false);
                        Dispatcher.BeginInvoke(() =>
                        {
                            if (ReferenceEquals(Source, graph)) BeginQuestConnectionRewire(insertionConnection, rewireEndpoint);
                        }, DispatcherPriority.Input);
                    });
                    rewireItem.IsEnabled = graph.CanStartQuestConnectionRewire(insertionConnection, out var reason);
                    rewireItem.ToolTip = rewireItem.IsEnabled ? "Choose a compatible socket. Escape cancels." : reason;
                    ToolTipService.SetShowOnDisabled(rewireItem, true);
                    contextMenu.Items.Add(rewireItem);
                }
                contextMenu.Items.Add(new Separator());
            }

            var deleteMenuItem = CreateMenuItem("Delete Connection", "Delete", "WolvenKitRed", () =>
            {
                if (Source.GraphType == RedGraphType.Quest)
                {
                    if (connectionViewModel is QuestConnectionViewModel questConnection)
                    {
                        Source.RemoveQuestConnectionPublic(questConnection);
                    }
                }
                else if (Source.GraphType == RedGraphType.Scene)
                {
                    if (connectionViewModel is SceneConnectionViewModel sceneConnection)
                    {
                        Source.RemoveSceneConnectionPublic(sceneConnection);
                    }
                }

                Editor.SelectedItems.Clear();
                connectionViewModel.IsSelected = false;
            });

            contextMenu.Items.Add(deleteMenuItem);

            connection.ContextMenu = contextMenu;
            contextMenu.SetCurrentValue(ContextMenu.IsOpenProperty, true);

            e.Handled = true;
        }
    }

    private void ShowBehaviorConnectionContextMenu(Connection connection, ConnectionViewModel connectionViewModel)
    {
        Editor.SelectedItems.Clear();
        Editor.SelectedItems.Add(connectionViewModel);
        connectionViewModel.IsSelected = true;

        var contextMenu = new ContextMenu();

        var canMoveUp = Source.CanMoveBehaviorChild(connectionViewModel, -1);
        var moveUpMenuItem = CreateMenuItem("Move Child Up", "ArrowUp", () =>
        {
            var nodeId = Source.MoveBehaviorChild(connectionViewModel, -1);
            connectionViewModel.IsSelected = false;
            SelectNodeById(nodeId);
        });
        moveUpMenuItem.IsEnabled = canMoveUp;
        contextMenu.Items.Add(moveUpMenuItem);

        var canMoveDown = Source.CanMoveBehaviorChild(connectionViewModel, 1);
        var moveDownMenuItem = CreateMenuItem("Move Child Down", "ArrowDown", () =>
        {
            var nodeId = Source.MoveBehaviorChild(connectionViewModel, 1);
            connectionViewModel.IsSelected = false;
            SelectNodeById(nodeId);
        });
        moveDownMenuItem.IsEnabled = canMoveDown;
        contextMenu.Items.Add(moveDownMenuItem);
        contextMenu.Items.Add(new Separator());

        var replaceMenu = CreateCategoryMenuItem("Replace Child");
        AddBehaviorNodeCreationItems(replaceMenu, type =>
        {
            var nodeId = Source.ReplaceBehaviorChild(connectionViewModel, type.Type, type.RedTypeTemplateSelectionOption);
            connectionViewModel.IsSelected = false;
            SelectNodeById(nodeId);
        });
        contextMenu.Items.Add(replaceMenu);

        var wrapMenu = CreateCategoryMenuItem("Wrap Child With");
        AddBehaviorNodeCreationItems(wrapMenu, type =>
        {
            var nodeId = Source.WrapBehaviorChild(connectionViewModel, type.Type, type.RedTypeTemplateSelectionOption);
            connectionViewModel.IsSelected = false;
            SelectNodeById(nodeId);
        }, Source.GetBehaviorWrapperNodeTypes());
        contextMenu.Items.Add(wrapMenu);
        contextMenu.Items.Add(new Separator());

        contextMenu.Items.Add(CreateMenuItem("Delete Child Branch", "Delete", "WolvenKitRed", () =>
        {
            var nodeId = Source.DeleteBehaviorChild(connectionViewModel);
            connectionViewModel.IsSelected = false;
            SelectNodeById(nodeId);
        }));

        connection.SetCurrentValue(ContextMenuProperty, contextMenu);
        contextMenu.SetCurrentValue(ContextMenu.IsOpenProperty, true);
    }



    /// <summary>
    /// Detaches a node by removing all of its input and output connections
    /// </summary>
    private void DetachNode(BaseSceneViewModel node)
    {
        if (Source == null) return;

        // Create a list to store all connections that need to be removed
        var connectionsToRemove = new List<SceneConnectionViewModel>();

        // Find all connections where this node is the source (output connections)
        foreach (var connection in Source.Connections.OfType<SceneConnectionViewModel>())
        {
            if (connection.Source.OwnerId == node.UniqueId)
            {
                connectionsToRemove.Add(connection);
            }
        }

        // Find all connections where this node is the target (input connections)
        foreach (var connection in Source.Connections.OfType<SceneConnectionViewModel>())
        {
            if (connection.Target.OwnerId == node.UniqueId)
            {
                connectionsToRemove.Add(connection);
            }
        }

        // Remove all the connections in the list
        foreach (var connection in connectionsToRemove)
        {
            Source.RemoveSceneConnectionPublic(connection);
        }
    }

    /// <summary>
    /// Determines if a scene node should use a deletion marker when deleted
    /// </summary>
    private static bool ShouldSceneNodeUseDeletionMarker(BaseSceneViewModel node)
    {
        // For scnQuestNode, check the contained quest node using the same logic as quest nodes
        if (node.Data is scnQuestNode scnQuestNode && scnQuestNode.QuestNode?.Chunk != null)
        {
            var questNode = scnQuestNode.QuestNode.Chunk;

            // Check if the contained quest node is signal-stopping
            if (questNode is questSignalStoppingNodeDefinition)
            {
                return true;
            }

            // Additional quest node types that should use deletion markers for safety
            var criticalQuestTypes = new[]
            {
                typeof(questSwitchNodeDefinition),
                typeof(questFlowControlNodeDefinition)
            };

            if (criticalQuestTypes.Contains(questNode.GetType()))
            {
                return true;
            }
        }

        // For pure scene nodes, manually list which ones should use deletion markers since scene nodes dont have a nice inheritance structure to check compred to quest nodes
        var criticalSceneTypes = new[]
        {
            typeof(scnAndNode),
            typeof(scnSectionNode),
            typeof(scnChoiceNode),
            typeof(scnHubNode),
            typeof(scnXorNode),
            typeof(scnRewindableSectionNode),
            typeof(scnInterruptManagerNode),
            typeof(scnFlowControlNode)
        };

        return criticalSceneTypes.Contains(node.Data.GetType());
    }

    /// <summary>
    /// Detaches a quest node by removing all of its input and output connections
    /// </summary>
    private void DetachQuestNode(BaseQuestViewModel node)
    {
        if (Source == null) return;

        // Create a list to store all connections that need to be removed
        var connectionsToRemove = new List<QuestConnectionViewModel>();

        // Find all connections where this node is the source (output connections)
        foreach (var connection in Source.Connections.OfType<QuestConnectionViewModel>())
        {
            if (connection.Source.OwnerId == node.UniqueId)
            {
                connectionsToRemove.Add(connection);
            }
        }

        // Find all connections where this node is the target (input connections)
        foreach (var connection in Source.Connections.OfType<QuestConnectionViewModel>())
        {
            if (connection.Target.OwnerId == node.UniqueId)
            {
                connectionsToRemove.Add(connection);
            }
        }

        // Remove all the connections in the list
        foreach (var connection in connectionsToRemove)
        {
            Source.RemoveQuestConnectionPublic(connection);
        }
    }

    /// <summary>
    /// Selects a node by its ID
    /// </summary>
    private void SelectNodeById(uint nodeId)
    {
        if (Source?.Nodes == null || Editor == null) return;

        // Find the node with the specified ID
        var targetNode = Source.Nodes.FirstOrDefault(n => n.UniqueId == nodeId);

        if (targetNode != null)
        {
            // Clear current selection
            Editor.SelectedItems.Clear();

            // Select the target node
            Editor.SelectedItems.Add(targetNode);

            // Update the NodeSelectionService
            NodeSelectionService.Instance.SelectedNode = targetNode;
        }
    }

    /// <summary>
    /// Get the center of the visible graph viewport for keyboard-driven node creation.
    /// </summary>
    private System.Windows.Point GetViewportCenter()
    {
        var viewport = Editor.ViewportLocation;
        var size = Editor.ViewportSize;
        return new System.Windows.Point(
            viewport.X + (size.Width / 2),
            viewport.Y + (size.Height / 2));
    }

    /// <summary>
    /// Get the mouse position in graph coordinates for placing new nodes
    /// </summary>
    private System.Windows.Point GetMousePositionInGraph(NodifyEditor editor, System.Windows.Point? mousePosition = null)
    {
        try
        {
            // Get mouse position relative to the editor
            var mousePos = mousePosition ?? Mouse.GetPosition(editor);

            // Convert to graph coordinates by accounting for viewport offset and zoom
            var graphX = (mousePos.X / editor.ViewportZoom) + editor.ViewportLocation.X;
            var graphY = (mousePos.Y / editor.ViewportZoom) + editor.ViewportLocation.Y;

            return new System.Windows.Point(graphX, graphY);
        }
        catch
        {
            // Fallback to viewport center if mouse position can't be determined
            var viewport = editor.ViewportLocation;
            var size = editor.ViewportSize;
            return new System.Windows.Point(
                viewport.X + size.Width / 2,
                viewport.Y + size.Height / 2
            );
        }
    }
}
