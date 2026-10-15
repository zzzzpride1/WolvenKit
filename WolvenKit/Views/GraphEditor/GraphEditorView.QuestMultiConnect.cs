#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Nodify;
using WolvenKit.App.ViewModels.GraphEditor;
using WolvenKit.App.ViewModels.GraphEditor.Nodes;
using WolvenKit.App.ViewModels.GraphEditor.Nodes.Quest;
using WolvenKit.App.ViewModels.GraphEditor.Nodes.Quest.Internal;

namespace WolvenKit.Views.GraphEditor;

public enum QuestMultiConnectFeedback { None, Source, Compatible, Incompatible, Ambiguous, Queued }

public partial class GraphEditorView
{
    public static readonly DependencyProperty QuestMultiConnectStateProperty = DependencyProperty.RegisterAttached(
        "QuestMultiConnectState", typeof(QuestMultiConnectFeedback), typeof(GraphEditorView), new PropertyMetadata(QuestMultiConnectFeedback.None));
    public static QuestMultiConnectFeedback GetQuestMultiConnectState(DependencyObject element) =>
        (QuestMultiConnectFeedback)element.GetValue(QuestMultiConnectStateProperty);
    public static void SetQuestMultiConnectState(DependencyObject element, QuestMultiConnectFeedback value) =>
        element.SetValue(QuestMultiConnectStateProperty, value);

    private readonly HashSet<Key> _questMultiModifierKeys = new();
    private readonly Dictionary<BaseConnectorViewModel, Connector> _questMultiPorts = new();
    private readonly Dictionary<NodeViewModel, Node> _questMultiNodes = new();
    private readonly Dictionary<QuestInputConnectorViewModel, Connection> _questMultiCables = new();
    private readonly List<QuestInputConnectorViewModel> _questMultiTargets = new();
    private RedGraph? _questMultiGraph;
    private QuestOutputConnectorViewModel? _questMultiSource;
    private Connector? _questMultiOrigin;
    private Window? _questMultiWindow;
    private Point _questMultiPointer;
    private bool _questMultiCommitting;

    public bool IsQuestMultiConnecting => _questMultiSource is not null;

    private void InitializeQuestMultiConnect()
    {
        AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(QuestMulti_OnMouseDown), true);
        AddHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler(QuestMulti_OnMouseMove), true);
        AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(QuestMulti_OnMouseUp), true);
        AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(QuestMulti_OnKeyDown), true);
        AddHandler(Keyboard.PreviewKeyUpEvent, new KeyEventHandler(QuestMulti_OnKeyUp), true);
        Editor.ViewportUpdated += (_, _) => UpdateQuestMultiPreview();
        Editor.LostMouseCapture += (_, _) =>
        {
            if (!Editor.IsMouseCaptureWithin) CancelQuestMultiConnect();
        };
        Editor.LostKeyboardFocus += (_, _) =>
        {
            if (!Editor.IsKeyboardFocusWithin) CancelQuestMultiConnect();
        };
        Loaded += (_, _) =>
        {
            _questMultiWindow = Window.GetWindow(this);
            if (_questMultiWindow is not null) _questMultiWindow.Deactivated += QuestMulti_OnDeactivated;
        };
        Unloaded += (_, _) =>
        {
            CancelQuestMultiConnect();
            if (_questMultiWindow is not null) _questMultiWindow.Deactivated -= QuestMulti_OnDeactivated;
            _questMultiWindow = null;
        };
    }

    private void QuestMulti_OnDeactivated(object? sender, EventArgs e) => CancelQuestMultiConnect();

    private ModifierKeys QuestMultiModifiers
    {
        get
        {
            // Routed state also drives the gesture when Shift is pressed after the canvas gains focus.
            // The system flags cover keys already held before focus entered the graph.
            var modifiers = Keyboard.Modifiers;
            foreach (var key in _questMultiModifierKeys)
                modifiers |= key switch
                {
                    Key.LeftShift or Key.RightShift => ModifierKeys.Shift,
                    Key.LeftCtrl or Key.RightCtrl => ModifierKeys.Control,
                    Key.LeftAlt or Key.RightAlt => ModifierKeys.Alt,
                    Key.LWin or Key.RWin => ModifierKeys.Windows,
                    _ => ModifierKeys.None
                };
            return modifiers;
        }
    }

    private static bool IsQuestMultiModifier(Key key) => key is Key.LeftShift or Key.RightShift or
        Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    private void QuestMulti_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (Source?.GraphType != RedGraphType.Quest) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (IsGraphTextInputFocused() || QuestRewireAncestor<TextBoxBase>(e.OriginalSource as DependencyObject) is not null)
        {
            CancelQuestMultiConnect();
            return;
        }
        if (IsQuestMultiModifier(key))
        {
            _questMultiModifierKeys.Add(key);
            if (IsQuestMultiConnecting && QuestMultiModifiers != ModifierKeys.Shift) CancelQuestMultiConnect();
        }
        else if (IsQuestMultiConnecting && key == Key.Escape)
        {
            e.Handled = true;
            CancelQuestMultiConnect();
        }
    }

    private void QuestMulti_OnKeyUp(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        _questMultiModifierKeys.Remove(key);
        if (IsQuestMultiConnecting && IsQuestMultiModifier(key) && QuestMultiModifiers != ModifierKeys.Shift)
        {
            e.Handled = true;
            CancelQuestMultiConnect();
        }
    }

    private void QuestMulti_OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsQuestMultiConnecting)
        {
            // Give native panning its normal event route; its button also cancels the uncommitted batch.
            if (e.ChangedButton is MouseButton.Right or MouseButton.Middle)
            {
                if (e.ChangedButton == MouseButton.Right) e.Handled = true;
                CancelQuestMultiConnect();
            }
            else if (e.ChangedButton == MouseButton.Left) e.Handled = true;
            return;
        }
        if (e.Handled || e.ChangedButton != MouseButton.Left || QuestMultiModifiers != ModifierKeys.Shift ||
            IsQuestConnectionRewiring || Editor.IsPanning || Editor.IsSelecting || Mouse.Captured is not null ||
            ActionPalettePopup.IsOpen || Editor.ContextMenu?.IsOpen == true || IsGraphTextInputFocused()) return;
        var original = e.OriginalSource as DependencyObject;
        if (QuestRewireAncestor<TextBoxBase>(original) is not null || QuestRewireAncestor<ButtonBase>(original) is not null ||
            QuestRewireAncestor<ComboBox>(original) is not null || QuestRewireAncestor<PasswordBox>(original) is not null) return;
        var origin = QuestRewireAncestor<Connector>(original);
        if (origin is null || !ReferenceEquals(origin.Editor, Editor) || origin.IsPendingConnection ||
            Source is not { } graph || !graph.CanStartQuestMultiConnect(origin.DataContext as BaseConnectorViewModel, out _)) return;
        e.Handled = true;
        try
        {
            CancelQuestConnectionRewire();
            _questMultiGraph = graph;
            _questMultiSource = (QuestOutputConnectorViewModel)origin.DataContext;
            _questMultiOrigin = origin;
            _questMultiPointer = e.GetPosition(Editor);
            graph.Nodes.CollectionChanged += QuestMulti_OnGraphChanged;
            graph.Connections.CollectionChanged += QuestMulti_OnGraphChanged;
            foreach (var port in QuestRewireDescendants<Connector>(Editor))
            {
                if (port.DataContext is BaseConnectorViewModel model) _questMultiPorts.TryAdd(model, port);
            }
            foreach (var node in QuestRewireDescendants<Node>(Editor))
            {
                if (node.DataContext is NodeViewModel model) _questMultiNodes.TryAdd(model, node);
            }
            foreach (var (port, control) in _questMultiPorts)
                SetQuestMultiConnectState(control, ReferenceEquals(port, _questMultiSource) ? QuestMultiConnectFeedback.Source :
                    graph.CanConnectQuestFlow(_questMultiSource, port, out _) ? QuestMultiConnectFeedback.Compatible : QuestMultiConnectFeedback.Incompatible);
            foreach (var (node, control) in _questMultiNodes)
                SetQuestMultiConnectState(control, GetQuestMultiNodeFeedback(node));
            // Feedback borders affect the native template: settle layout once before anchoring disconnected ports.
            Editor.UpdateLayout();
            origin.UpdateAnchor();
            Editor.Focus();
            if (!Mouse.Capture(Editor, CaptureMode.SubTree)) { CancelQuestMultiConnect(); return; }
            QuestMultiConnectOverlay.SetCurrentValue(VisibilityProperty, Visibility.Visible);
            QuestMultiConnectPreview.SetCurrentValue(System.Windows.Shapes.Shape.StrokeProperty, Brushes.Gold);
            QuestMultiConnectHintText.SetCurrentValue(TextBlock.TextProperty, "Swipe over targets Ă„â€šĂ˘â‚¬ĹˇÄ‚â€šĂ‚Â· release to connect Ă„â€šĂ˘â‚¬ĹˇÄ‚â€šĂ‚Â· Esc cancels");
            UpdateQuestMultiPreview();
        }
        catch (Exception exception)
        {
            _loggerService?.Error(exception);
            CancelQuestMultiConnect();
        }
    }

    private QuestMultiConnectFeedback GetQuestMultiNodeFeedback(NodeViewModel node)
    {
        if (_questMultiSource is null || _questMultiGraph is null) return QuestMultiConnectFeedback.None;
        if (node.Output.Contains(_questMultiSource)) return QuestMultiConnectFeedback.Source;
        if (_questMultiTargets.Any(target => node.Input.Contains(target))) return QuestMultiConnectFeedback.Queued;
        if (_questMultiGraph.GetQuestMultiConnectTarget(_questMultiSource, node, out _) is not null) return QuestMultiConnectFeedback.Compatible;
        return node is BaseQuestViewModel quest && RedGraph.GetQuestInsertionInputs(quest)
            .Count(input => _questMultiGraph.CanConnectQuestFlow(_questMultiSource, input, out _)) > 1
                ? QuestMultiConnectFeedback.Ambiguous : QuestMultiConnectFeedback.Incompatible;
    }

    private void QuestMulti_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!IsQuestMultiConnecting) return;
        if (!EnsureQuestMultiIsLive() || QuestMultiModifiers != ModifierKeys.Shift) { CancelQuestMultiConnect(); return; }
        _questMultiPointer = e.GetPosition(Editor);
        var hit = QuestRewireAncestor<Connector>(e.OriginalSource as DependencyObject) as FrameworkElement ??
            QuestRewireAncestor<Node>(e.OriginalSource as DependencyObject) ??
            _questMultiOrigin?.FindConnectionTarget(Editor.GetLocationInsideEditor(e));
        BaseConnectorViewModel? candidate = null;
        var reason = "Pass over a target node or input socket.";
        if (hit is Connector connector && ReferenceEquals(connector.Editor, Editor))
            candidate = connector.DataContext as BaseConnectorViewModel;
        else if (hit?.DataContext is NodeViewModel node)
            candidate = _questMultiGraph!.GetQuestMultiConnectTarget(_questMultiSource, node, out reason);
        var valid = candidate is not null && _questMultiGraph!.CanConnectQuestFlow(_questMultiSource, candidate, out reason);
        if (valid && !_questMultiTargets.Any(target => ReferenceEquals(target.Data, ((QuestInputConnectorViewModel)candidate!).Data)))
        {
            valid = _questMultiGraph!.CanConnectMultipleQuestNodes(_questMultiSource, _questMultiTargets.Cast<BaseConnectorViewModel>().Append(candidate!), out reason);
            if (valid)
            {
                var target = (QuestInputConnectorViewModel)candidate!;
                if (_questMultiPorts.TryGetValue(target, out var targetControl)) targetControl.UpdateAnchor();
                _questMultiTargets.Add(target);
                var cable = new Connection();
                cable.SetCurrentValue(BaseConnection.SourceOffsetProperty, new Size());
                cable.SetCurrentValue(BaseConnection.TargetOffsetProperty, new Size());
                cable.SetCurrentValue(System.Windows.Shapes.Shape.StrokeProperty, Brushes.DeepSkyBlue);
                cable.SetCurrentValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 2d);
                cable.SetCurrentValue(BaseConnection.IsSelectableProperty, false);
                _questMultiCables.Add(target, cable);
                QuestMultiConnectOverlay.Children.Insert(0, cable);
                foreach (var (port, control) in _questMultiPorts)
                    if (GetQuestMultiPortData(port) is { } data && ReferenceEquals(data, target.Data))
                        SetQuestMultiConnectState(control, QuestMultiConnectFeedback.Queued);
                foreach (var (owner, control) in _questMultiNodes)
                    if (owner.Input.Contains(target)) SetQuestMultiConnectState(control, QuestMultiConnectFeedback.Queued);
            }
        }
        QuestMultiConnectPreview.SetCurrentValue(System.Windows.Shapes.Shape.StrokeProperty,
            candidate is null ? Brushes.Gold : valid ? Brushes.LightGreen : Brushes.IndianRed);
        QuestMultiConnectHintText.SetCurrentValue(TextBlock.TextProperty,
            valid ? $"{_questMultiTargets.Count} targets queued Ă„â€šĂ˘â‚¬ĹˇÄ‚â€šĂ‚Â· release to connect Ă„â€šĂ˘â‚¬ĹˇÄ‚â€šĂ‚Â· Esc cancels" : reason);
        UpdateQuestMultiPreview();
        e.Handled = true;
    }

    private static WolvenKit.RED4.Types.questSocketDefinition? GetQuestMultiPortData(BaseConnectorViewModel port) => port switch
    {
        QuestInputConnectorViewModel input => input.Data,
        QuestOutputConnectorViewModel output => output.Data,
        _ => null
    };

    private void QuestMulti_OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!IsQuestMultiConnecting || e.ChangedButton != MouseButton.Left) return;
        e.Handled = true;
        try
        {
            if (!EnsureQuestMultiIsLive() || QuestMultiModifiers != ModifierKeys.Shift) return;
            _questMultiCommitting = true;
            _questMultiGraph!.TryConnectMultipleQuestNodes(_questMultiSource, _questMultiTargets, out _);
        }
        catch (Exception exception) { _loggerService?.Error(exception); }
        finally { _questMultiCommitting = false; CancelQuestMultiConnect(); }
    }

    private bool EnsureQuestMultiIsLive() => ReferenceEquals(Source, _questMultiGraph) &&
        _questMultiGraph?.CanStartQuestMultiConnect(_questMultiSource, out _) == true;

    private void QuestMulti_OnGraphChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_questMultiCommitting) CancelQuestMultiConnect();
    }

    private void UpdateQuestMultiPreview()
    {
        if (_questMultiSource is null) return;
        var source = Editor.ViewportTransform.Transform(_questMultiSource.Anchor);
        QuestMultiConnectPreview.SetCurrentValue(BaseConnection.SourceProperty, source);
        QuestMultiConnectPreview.SetCurrentValue(BaseConnection.TargetProperty, _questMultiPointer);
        foreach (var (target, cable) in _questMultiCables)
        {
            cable.SetCurrentValue(BaseConnection.SourceProperty, source);
            cable.SetCurrentValue(BaseConnection.TargetProperty, Editor.ViewportTransform.Transform(target.Anchor));
        }
        Canvas.SetLeft(QuestMultiConnectHint, _questMultiPointer.X + 18);
        Canvas.SetTop(QuestMultiConnectHint, _questMultiPointer.Y + 18);
    }

    public void CancelQuestMultiConnect()
    {
        var graph = _questMultiGraph;
        var active = IsQuestMultiConnecting;
        _questMultiGraph = null;
        _questMultiSource = null;
        _questMultiOrigin = null;
        _questMultiModifierKeys.Clear();
        if (graph is not null)
        {
            graph.Nodes.CollectionChanged -= QuestMulti_OnGraphChanged;
            graph.Connections.CollectionChanged -= QuestMulti_OnGraphChanged;
        }
        foreach (var control in _questMultiPorts.Values) control.ClearValue(QuestMultiConnectStateProperty);
        foreach (var control in _questMultiNodes.Values) control.ClearValue(QuestMultiConnectStateProperty);
        _questMultiPorts.Clear(); _questMultiNodes.Clear();
        foreach (var cable in _questMultiCables.Values) QuestMultiConnectOverlay.Children.Remove(cable);
        _questMultiCables.Clear(); _questMultiTargets.Clear();
        QuestMultiConnectOverlay.SetCurrentValue(VisibilityProperty, Visibility.Collapsed);
        if (active && ReferenceEquals(Mouse.Captured, Editor)) Mouse.Capture(null);
    }
}
