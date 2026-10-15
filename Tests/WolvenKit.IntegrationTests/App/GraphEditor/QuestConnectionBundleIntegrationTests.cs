using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reactive.Concurrency;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Moq;
using Nodify;
using ReactiveUI;
using Splat;
using WolvenKit.App.Factories;
using WolvenKit.App.Interaction;
using WolvenKit.App.Services;
using WolvenKit.App.ViewModels.GraphEditor;
using WolvenKit.App.ViewModels.GraphEditor.Nodes;
using WolvenKit.App.ViewModels.GraphEditor.Nodes.Quest;
using WolvenKit.App.ViewModels.GraphEditor.Nodes.Quest.Internal;
using WolvenKit.RED4.Types;
using WolvenKit.Views.GraphEditor;
using Xunit;
using Point = System.Windows.Point;
using Window = System.Windows.Window;

namespace WolvenKit.IntegrationTests.App.GraphEditor;

public class QuestConnectionBundleIntegrationTests
{
    [StaFact]
    public void RoutedMenusCopyAndMoveBundlesWithoutChangingOtherEditorState()
    {
        using var resolver = new ModernDependencyResolver();
        using var restoreResolver = resolver.WithResolver(suppressResolverCallback: true);
        resolver.InitializeSplat();
        var ownsApplication = Application.Current is null;
        var application = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var resources = CreateResources();
        application.Resources.MergedDictionaries.Add(resources);
        // Unlike the real application, the testhost defaults Rx to an immediate scheduler.
        // Viewport throttles must marshal back to this fixture's WPF dispatcher.
        var previousScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = new SynchronizationContextScheduler(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var previousSelection = NodeSelectionService.Instance.SelectedNode;
        var previousDropdown = Interactions.AskForDropdownOption;
        var previousConfirmation = Interactions.ShowConfirmation;
        var dropdownCalls = 0;
        var questions = 0;
        Interactions.AskForDropdownOption = _ => { dropdownCalls++; return ""; };
        Interactions.ShowConfirmation = args => { if (args.buttons == WMessageBoxButtons.YesNo) questions++; return WMessageBoxResult.No; };
        GraphEditorView? view = null;
        Window? window = null;
        using var ordinary = Fanout("bundle-ui-ordinary.questphase");
        using var cut = Fanout("bundle-ui-cut.questphase");
        using var malformed = Fanout("bundle-ui-malformed.questphase");
        using var incompatible = Fanout("bundle-ui-incompatible.questphase");
        using var ambiguous = Ambiguous("bundle-ui-ambiguous.questphase");
        using var threeSockets = Ambiguous("bundle-ui-stable-ordinals.questphase");
        AddOutput(threeSockets.Nodes[1], "Choice");
        using var replacement = Fanout("bundle-ui-replacement.questphase");
        using var loop = new Fixture("bundle-ui-self-loop.questphase");
        using var lifecycle = Fanout("bundle-ui-lifecycle.questphase");
        using var disposed = Fanout("bundle-ui-disposed.questphase");
        try
        {
            view = new GraphEditorView { Source = ordinary.Graph };
            window = new Window { Content = view, Width = 1400, Height = 1050, Left = -20000, Top = -20000,
                ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
                WindowStartupLocation = WindowStartupLocation.Manual, ResizeMode = ResizeMode.NoResize };
            window.Show();
            Attach(view, ordinary, window);
            var hint = Assert.IsType<Border>(view.FindName("QuestConnectionClipboardHint"));
            Assert.False(hint.IsHitTestVisible);
            Assert.IsType<TextBlock>(view.FindName("QuestConnectionClipboardHintText"));
            GraphClipboardManager.CopyNode(ordinary.Nodes[0], RedGraphType.Quest);
            var nodePayload = GraphClipboardManager.GetCopiedData();
            Assert.NotNull(nodePayload);
            view.Editor.SelectedItems!.Clear();
            view.Editor.SelectedItems!.Add(ordinary.Nodes[2]);
            view.SelectedNode = ordinary.Nodes[2];
            var selected = view.Editor.SelectedItems!.Cast<object>().ToArray();
            var selectedNode = view.SelectedNode;
            var location = view.Editor.ViewportLocation;
            var zoom = view.Editor.ViewportZoom;
            var original = Snapshot(ordinary.Graph);
            var menu = Menu(view, ordinary.Nodes[0], window);
            foreach (var header in new[] { "Copy Connections", "Cut Connections" })
                Assert.Equal(new[] { "All Connections", "Incoming Connections", "Outgoing Connections" },
                    Item(menu, header).Items.OfType<MenuItem>().Select(item => item.Header as string).ToArray());
            var labels = menu.Items.OfType<MenuItem>().Select(item => item.Header as string).ToArray();
            Assert.True(Array.IndexOf(labels, "Copy Connections") > Array.IndexOf(labels, "Copy Node"));
            Assert.False(Item(menu, "Paste Connections").IsEnabled);
            Click(menu, "Copy Connections", "Outgoing Connections");
            Unchanged(ordinary.Graph, original);
            Assert.Contains("3", Hint(view));
            Assert.True(hint.IsVisible);
            menu = Menu(view, ordinary.Nodes[1], window);
            var paste = Item(menu, "Paste Connections");
            Enter(paste);
            Layout(window);
            Unchanged(ordinary.Graph, original);
            if (view.FindName("QuestConnectionClipboardPreview") is Canvas preview)
            {
                Assert.False(preview.IsHitTestVisible);
                var cables = preview.Children.OfType<Connection>().ToArray();
                Assert.Equal(3, cables.Length);
                var sourcePoint = SocketCenter(view, Output(ordinary.Nodes[1]));
                foreach (var target in ordinary.Nodes.Skip(3).Take(3).Select(node => Input(node)))
                    Assert.Contains(cables, cable => (cable.Source - sourcePoint).Length < 0.5 &&
                        (cable.Target - SocketCenter(view, target)).Length < 0.5);
                paste.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });
                Assert.Empty(preview.Children.OfType<Connection>());
                Enter(paste);
                Assert.Equal(3, preview.Children.OfType<Connection>().Count());
            }
            Click(menu, "Paste Connections");
            Assert.Equal(original.Edges.Length + 3, ordinary.Graph.Connections.Count);
            Preserved(ordinary.Graph, original);
            foreach (var target in ordinary.Nodes.Skip(3).Take(3)) Edge(ordinary.Graph, Output(ordinary.Nodes[1]), Input(target));
            PreviewEmpty(view);

            // Incoming/All retain every original edge, including unsupported CutSource/CutDestination.
            Click(Menu(view, ordinary.Nodes[0], window), "Copy Connections", "Incoming Connections");
            Assert.Contains("1", Hint(view));
            Click(Menu(view, ordinary.Nodes[1], window), "Paste Connections");
            Edge(ordinary.Graph, Output(ordinary.Nodes[2]), Input(ordinary.Nodes[1]));
            Click(Menu(view, ordinary.Nodes[0], window), "Copy Connections", "All Connections");
            Assert.Contains("4", Hint(view));
            Click(Menu(view, ordinary.Nodes[6], window), "Paste Connections");
            Edge(ordinary.Graph, Output(ordinary.Nodes[2]), Input(ordinary.Nodes[6]));
            foreach (var target in ordinary.Nodes.Skip(3).Take(3)) Edge(ordinary.Graph, Output(ordinary.Nodes[6]), Input(target));
            Assert.Equal(original.Edges.Length + 8, ordinary.Graph.Connections.Count);
            Preserved(ordinary.Graph, original);
            Assert.Equal(selected, view.Editor.SelectedItems!.Cast<object>().ToArray());
            Assert.Same(selectedNode, view.SelectedNode);
            Assert.Same(selectedNode, NodeSelectionService.Instance.SelectedNode);
            Assert.Equal(location, view.Editor.ViewportLocation);
            Assert.Equal(zoom, view.Editor.ViewportZoom);
            Assert.Equal(0, dropdownCalls);
            Assert.Equal(0, questions);
            Assert.Same(nodePayload, GraphClipboardManager.GetCopiedData());

            // Cut is pending until valid paste. Both menu cancellation and routed Escape are inert.
            Attach(view, cut, window);
            original = Snapshot(cut.Graph);
            Click(Menu(view, cut.Nodes[0], window), "Cut Connections", "Outgoing Connections");
            Unchanged(cut.Graph, original);
            Assert.Contains("3", Hint(view));
            Assert.True(Hint(view).Contains("cut", StringComparison.OrdinalIgnoreCase));
            Click(Menu(view, cut.Nodes[1], window), "Cancel Pending Connection Cut");
            Unchanged(cut.Graph, original);
            Cleared(view, cut.Nodes[1], window);
            Click(Menu(view, cut.Nodes[0], window), "Cut Connections", "Outgoing Connections");
            Assert.True(Key(view.Editor, System.Windows.Input.Key.Escape).Handled);
            Unchanged(cut.Graph, original);
            Cleared(view, cut.Nodes[1], window);
            Click(Menu(view, cut.Nodes[0], window), "Cut Connections", "Outgoing Connections");
            Unchanged(cut.Graph, original);
            Click(Menu(view, cut.Nodes[1], window), "Paste Connections");
            Assert.Equal(original.Edges.Length, cut.Graph.Connections.Count);
            foreach (var target in cut.Nodes.Skip(3).Take(3))
            {
                NoEdge(cut.Graph, Output(cut.Nodes[0]), Input(target));
                Edge(cut.Graph, Output(cut.Nodes[1]), Input(target));
            }
            Edge(cut.Graph, Output(cut.Nodes[2]), Input(cut.Nodes[0]));
            Assert.Same(original.Edges.Last(), Edge(cut.Graph, Output(cut.Nodes[0], "CutSource"), Input(cut.Nodes[3], "CutDestination")));
            Cleared(view, cut.Nodes[1], window, allowSuccessFeedback: true);

            // Either disable malformed/incompatible actions or reject them without mutation.
            var broken = Edge(malformed.Graph, Output(malformed.Nodes[0]), Input(malformed.Nodes[3]));
            var input = Input(malformed.Nodes[3]).Data;
            input.Connections.Remove(input.Connections.Single(handle => ReferenceEquals(handle.GetValue(), broken.ConnectionDefinition)));
            Attach(view, malformed, window);
            original = Snapshot(malformed.Graph);
            InvokeIfEnabled(Menu(view, malformed.Nodes[0], window), "Copy Connections", "Outgoing Connections");
            Assert.False(Item(Menu(view, malformed.Nodes[1], window), "Paste Connections").IsEnabled);
            Unchanged(malformed.Graph, original);
            Output(incompatible.Nodes[1]).Data.Type = Enums.questSocketType.CutSource;
            Attach(view, incompatible, window);
            Click(Menu(view, incompatible.Nodes[0], window), "Copy Connections", "Outgoing Connections");
            original = Snapshot(incompatible.Graph);
            InvokeIfEnabled(Menu(view, incompatible.Nodes[1], window), "Paste Connections");
            Unchanged(incompatible.Graph, original);

            // Two used source outputs and duplicate destination names require an injective manual map.
            Attach(view, ambiguous, window);
            Click(Menu(view, ambiguous.Nodes[0], window), "Copy Connections", "Outgoing Connections");
            original = Snapshot(ambiguous.Graph);
            var beforeCancel = dropdownCalls;
            Click(Menu(view, ambiguous.Nodes[1], window), "Paste Connections");
            Assert.True(dropdownCalls > beforeCancel);
            Unchanged(ambiguous.Graph, original);
            var seen = new List<string[]>();
            Interactions.AskForDropdownOption = args =>
            {
                dropdownCalls++; seen.Add(args.options.ToArray());
                Assert.Equal(args.options.Count, args.options.Distinct(StringComparer.Ordinal).Count());
                Assert.DoesNotContain(args.options, label => label.Contains("CutSource", StringComparison.Ordinal));
                return args.options.Last();
            };
            Click(Menu(view, ambiguous.Nodes[1], window), "Paste Connections");
            Assert.NotEmpty(seen);
            Assert.Contains(seen.SelectMany(options => options), label => label.EndsWith("[2]", StringComparison.Ordinal));
            var outputs = ambiguous.Nodes[1].Output.OfType<QuestOutputConnectorViewModel>()
                .Where(port => port.Data.Type == Enums.questSocketType.Output).ToArray();
            Edge(ambiguous.Graph, outputs[1], Input(ambiguous.Nodes[2]));
            Edge(ambiguous.Graph, outputs[0], Input(ambiguous.Nodes[3]));
            NoEdge(ambiguous.Graph, outputs[0], Input(ambiguous.Nodes[2]));
            Preserved(ambiguous.Graph, original);

            // Duplicate destination labels retain their original ordinal after the first choice is removed.
            Attach(view, threeSockets, window);
            Click(Menu(view, threeSockets.Nodes[0], window), "Copy Connections", "Outgoing Connections");
            var stablePrompts = new List<string[]>();
            Interactions.AskForDropdownOption = args =>
            {
                stablePrompts.Add(args.options.ToArray());
                return args.options.First();
            };
            Click(Menu(view, threeSockets.Nodes[1], window), "Paste Connections");
            Assert.Equal(2, stablePrompts.Count);
            Assert.Equal(new[] { "Choice [1]", "Choice [2]", "Choice [3]" }, stablePrompts[0]);
            Assert.Equal(new[] { "Choice [2]", "Choice [3]" }, stablePrompts[1]);
            var stableOutputs = threeSockets.Nodes[1].Output.OfType<QuestOutputConnectorViewModel>()
                .Where(port => port.Data.Type == Enums.questSocketType.Output).ToArray();
            Edge(threeSockets.Graph, stableOutputs[0], Input(threeSockets.Nodes[2]));
            Edge(threeSockets.Graph, stableOutputs[1], Input(threeSockets.Nodes[3]));
            Attach(view, ambiguous, window);

            // A modal prompt can reenter the app and switch Source before the pending commit.
            Click(Menu(view, ambiguous.Nodes[0], window), "Copy Connections", "Outgoing Connections");
            original = Snapshot(ambiguous.Graph);
            var replacementOriginal = Snapshot(replacement.Graph);
            Interactions.AskForDropdownOption = args => { Attach(view, replacement, window); return args.options.Last(); };
            Click(Menu(view, ambiguous.Nodes[1], window), "Paste Connections");
            Assert.Same(replacement.Graph, view.Source);
            Unchanged(ambiguous.Graph, original);
            Unchanged(replacement.Graph, replacementOriginal);
            Cleared(view, replacement.Nodes[1], window);

            // Only a newly introduced self-loop prompts. Refusal and prompt-time scope change are inert.
            var loopSource = loop.Add(typeof(questFactsDBManagerNodeDefinition));
            var loopTarget = loop.Add(typeof(questFactsDBManagerNodeDefinition));
            Connect(loop.Graph, Output(loopSource), Input(loopTarget));
            Attach(view, loop, window);
            Click(Menu(view, loopSource, window), "Copy Connections", "Outgoing Connections");
            original = Snapshot(loop.Graph);
            var beforeQuestions = questions;
            Interactions.ShowConfirmation = args => { Assert.Equal(WMessageBoxButtons.YesNo, args.buttons); questions++; return WMessageBoxResult.No; };
            Click(Menu(view, loopTarget, window), "Paste Connections");
            Assert.Equal(beforeQuestions + 1, questions);
            Unchanged(loop.Graph, original);
            Interactions.ShowConfirmation = args =>
            {
                Assert.Equal(WMessageBoxButtons.YesNo, args.buttons); questions++;
                Attach(view, replacement, window); return WMessageBoxResult.Yes;
            };
            Click(Menu(view, loopTarget, window), "Paste Connections");
            Assert.Same(replacement.Graph, view.Source);
            Unchanged(loop.Graph, original);
            Unchanged(replacement.Graph, replacementOriginal);
            Attach(view, loop, window);
            Click(Menu(view, loopSource, window), "Copy Connections", "Outgoing Connections");
            Interactions.ShowConfirmation = args => { Assert.Equal(WMessageBoxButtons.YesNo, args.buttons); questions++; return WMessageBoxResult.Yes; };
            Click(Menu(view, loopTarget, window), "Paste Connections");
            Assert.Equal(original.Edges.Length + 1, loop.Graph.Connections.Count);
            Edge(loop.Graph, Output(loopTarget), Input(loopTarget));
            Preserved(loop.Graph, original);

            // Source change, actual unload, explicit clear and direct graph disposal expire the clipboard.
            Attach(view, lifecycle, window);
            original = Snapshot(lifecycle.Graph);
            Click(Menu(view, lifecycle.Nodes[0], window), "Cut Connections", "All Connections");
            Attach(view, replacement, window);
            Cleared(view, replacement.Nodes[1], window);
            Attach(view, lifecycle, window);
            Cleared(view, lifecycle.Nodes[1], window);
            Unchanged(lifecycle.Graph, original);
            Click(Menu(view, lifecycle.Nodes[0], window), "Cut Connections", "Outgoing Connections");
            CloseMenus(view); window.Content = null; Layout(window); window.Content = view; Layout(window);
            Cleared(view, lifecycle.Nodes[1], window);
            Unchanged(lifecycle.Graph, original);
            Click(Menu(view, lifecycle.Nodes[0], window), "Cut Connections", "Outgoing Connections");
            view.ClearQuestConnectionClipboard();
            Cleared(view, lifecycle.Nodes[1], window);
            Unchanged(lifecycle.Graph, original);

            // Native pending-connection events still use the original creation command after all paths.
            CloseMenus(view);
            var native = Port(view, Output(lifecycle.Nodes[1]));
            native.BeginConnecting();
            Assert.True(native.IsPendingConnection);
            native.UpdatePendingConnection(Input(lifecycle.Nodes[3]).Anchor);
            native.EndConnecting(Port(view, Input(lifecycle.Nodes[3])));
            Assert.False(native.IsPendingConnection);
            Assert.Equal(original.Edges.Length + 1, lifecycle.Graph.Connections.Count);
            Edge(lifecycle.Graph, Output(lifecycle.Nodes[1]), Input(lifecycle.Nodes[3]));
            Assert.Same(nodePayload, GraphClipboardManager.GetCopiedData());
            Attach(view, disposed, window);
            Click(Menu(view, disposed.Nodes[0], window), "Cut Connections", "Outgoing Connections");
            original = Snapshot(disposed.Graph);
            disposed.Graph.Dispose();
            Cleared(view, disposed.Nodes[1], window);
            Unchanged(disposed.Graph, original);
            Assert.Same(nodePayload, GraphClipboardManager.GetCopiedData());
        }
        finally
        {
            Interactions.AskForDropdownOption = previousDropdown;
            Interactions.ShowConfirmation = previousConfirmation;
            if (view is not null)
            {
                CloseMenus(view); view.ClearQuestConnectionClipboard();
                view.CancelQuestMultiConnect(); view.CancelQuestConnectionRewire();
                view.Editor.SelectedItems?.Clear(); view.Source = null;
            }
            if (window is not null) { window.Content = null; window.Close(); }
            GraphClipboardManager.Clear();
            NodeSelectionService.Instance.SelectedNode = previousSelection;
            application.Resources.MergedDictionaries.Remove(resources);
            if (ownsApplication) application.Shutdown();
            RxApp.MainThreadScheduler = previousScheduler;
        }
    }

    private static Fixture Fanout(string name)
    {
        var f = new Fixture(name);
        for (var i = 0; i < 3; i++) f.Add(typeof(questFactsDBManagerNodeDefinition));
        for (var i = 0; i < 3; i++) f.Add(typeof(questPauseConditionNodeDefinition));
        f.Add(typeof(questFactsDBManagerNodeDefinition));
        Connect(f.Graph, Output(f.Nodes[2]), Input(f.Nodes[0]));
        foreach (var target in f.Nodes.Skip(3).Take(3)) Connect(f.Graph, Output(f.Nodes[0]), Input(target));
        Connect(f.Graph, AddOutput(f.Nodes[0], "CutSource", Enums.questSocketType.CutSource), Input(f.Nodes[3], "CutDestination"));
        return f;
    }
    private static Fixture Ambiguous(string name)
    {
        var f = new Fixture(name);
        f.Add(typeof(questFactsDBManagerNodeDefinition)); f.Add(typeof(questFactsDBManagerNodeDefinition));
        f.Add(typeof(questPauseConditionNodeDefinition)); f.Add(typeof(questPauseConditionNodeDefinition));
        var alternate = AddOutput(f.Nodes[0], "Alternate");
        var old = Output(f.Nodes[1]); f.Nodes[1].Output.Remove(old);
        var data = (graphGraphNodeDefinition)f.Nodes[1].Data;
        data.Sockets.Remove(data.Sockets.Single(handle => ReferenceEquals(handle.GetValue(), old.Data)));
        AddOutput(f.Nodes[1], "Choice"); AddOutput(f.Nodes[1], "Choice");
        AddOutput(f.Nodes[1], "CutSource", Enums.questSocketType.CutSource);
        Connect(f.Graph, Output(f.Nodes[0]), Input(f.Nodes[2])); Connect(f.Graph, alternate, Input(f.Nodes[3]));
        return f;
    }
    private static QuestOutputConnectorViewModel AddOutput(BaseQuestViewModel node, string name,
        Enums.questSocketType type = Enums.questSocketType.Output)
    {
        var data = new questSocketDefinition { Name = name, Type = type };
        ((graphGraphNodeDefinition)node.Data).Sockets.Add(new CHandle<graphGraphSocketDefinition>(data));
        var port = new QuestOutputConnectorViewModel(name, name, node.UniqueId, data); node.Output.Add(port); return port;
    }
    private static void Attach(GraphEditorView view, Fixture f, Window window)
    {
        CloseMenus(view); view.Editor.SelectedItems!.Clear(); view.Source = f.Graph; Layout(window);
        for (var i = 0; i < f.Nodes.Count; i++) f.Nodes[i].Location = new Point(30 + i % 3 * 430, 50 + i / 3 * 280);
        view.Editor.SetCurrentValue(NodifyEditor.ViewportZoomProperty, 1d);
        view.Editor.SetCurrentValue(NodifyEditor.ViewportLocationProperty, new Point()); Layout(window);
    }
    private static ContextMenu Menu(GraphEditorView view, NodeViewModel model, Window window)
    {
        CloseMenus(view);
        var node = Assert.Single(Descendants<Node>(view.Editor), control => ReferenceEquals(control.DataContext, model));
        node.ContextMenu ??= new ContextMenu();
        node.ContextMenu.PlacementTarget = node; node.ContextMenu.Placement = PlacementMode.Relative;
        // WPF has no public ContextMenuEventArgs constructor. Reflect event args only, then use
        // the actual routed event so the XAML hookup and production menu callbacks are exercised.
        var args = (ContextMenuEventArgs)System.Activator.CreateInstance(typeof(ContextMenuEventArgs),
            BindingFlags.Instance | BindingFlags.NonPublic, null, [node, true], null)!;
        args.RoutedEvent = FrameworkElement.ContextMenuOpeningEvent; node.RaiseEvent(args);
        Assert.True(args.Handled); Layout(window); return Assert.IsType<ContextMenu>(node.ContextMenu);
    }
    private static MenuItem Item(ItemsControl parent, params string[] path)
    {
        MenuItem? item = null;
        foreach (var label in path) { item = Assert.Single(parent.Items.OfType<MenuItem>(), candidate => Equals(candidate.Header, label)); parent = item; }
        return Assert.IsType<MenuItem>(item);
    }
    private static void Click(ContextMenu menu, params string[] path)
    {
        var item = Item(menu, path); Assert.True(item.IsEnabled, string.Join(" > ", path));
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); menu.SetCurrentValue(ContextMenu.IsOpenProperty, false);
    }
    private static void InvokeIfEnabled(ContextMenu menu, params string[] path)
    {
        if (Item(menu, path).IsEnabled) Click(menu, path); else menu.SetCurrentValue(ContextMenu.IsOpenProperty, false);
    }
    private static void CloseMenus(GraphEditorView view)
    {
        foreach (var node in Descendants<Node>(view.Editor)) node.ContextMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        view.Editor.ContextMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
    }
    private static void Enter(UIElement source) => source.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
    private static string Hint(GraphEditorView view) => Assert.IsType<TextBlock>(view.FindName("QuestConnectionClipboardHintText")).Text;
    private static void Cleared(GraphEditorView view, NodeViewModel node, Window window, bool allowSuccessFeedback = false)
    {
        var menu = Menu(view, node, window);
        Assert.False(Item(menu, "Paste Connections").IsEnabled);
        Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, "Cancel Pending Connection Cut"));
        if (!allowSuccessFeedback) Assert.False(Assert.IsType<Border>(view.FindName("QuestConnectionClipboardHint")).IsVisible);
        PreviewEmpty(view); menu.SetCurrentValue(ContextMenu.IsOpenProperty, false);
    }
    private static void PreviewEmpty(GraphEditorView view)
    {
        if (view.FindName("QuestConnectionClipboardPreview") is Canvas preview)
        { Assert.False(preview.IsHitTestVisible); Assert.Empty(preview.Children.OfType<Connection>()); }
    }
    private static KeyEventArgs Key(UIElement source, System.Windows.Input.Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(source)!, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        source.RaiseEvent(args); return args;
    }
    private static Connector Port(GraphEditorView view, BaseConnectorViewModel port) =>
        Assert.Single(Descendants<Connector>(view.Editor), control => ReferenceEquals(control.DataContext, port));
    private static Point SocketCenter(GraphEditorView view, BaseConnectorViewModel port)
    {
        var control = Port(view, port);
        var thumb = Assert.IsAssignableFrom<FrameworkElement>(control.Template.FindName("PART_Connector", control));
        return thumb.TranslatePoint(new Point(thumb.ActualWidth / 2, thumb.ActualHeight / 2), view.Editor);
    }
    private static QuestInputConnectorViewModel Input(BaseQuestViewModel node, string name = "In") => node.Input.OfType<QuestInputConnectorViewModel>().Single(port => port.Name == name);
    private static QuestOutputConnectorViewModel Output(BaseQuestViewModel node, string name = "Out") => node.Output.OfType<QuestOutputConnectorViewModel>().Single(port => port.Name == name);
    private static QuestConnectionViewModel Connect(RedGraph graph, QuestOutputConnectorViewModel source, QuestInputConnectorViewModel target)
    {
        graph.PendingConnection.Source = source; graph.PendingConnection.Target = target; graph.Connect(); return Edge(graph, source, target);
    }
    private static QuestConnectionViewModel Edge(RedGraph graph, QuestOutputConnectorViewModel source, QuestInputConnectorViewModel target)
    {
        var edge = Assert.Single(graph.Connections.OfType<QuestConnectionViewModel>(), candidate => ReferenceEquals(candidate.Source, source) && ReferenceEquals(candidate.Target, target));
        Assert.Same(source.Data, edge.ConnectionDefinition.Source.GetValue()); Assert.Same(target.Data, edge.ConnectionDefinition.Destination.GetValue());
        Assert.Single(source.Data.Connections, handle => ReferenceEquals(handle.GetValue(), edge.ConnectionDefinition));
        Assert.Single(target.Data.Connections, handle => ReferenceEquals(handle.GetValue(), edge.ConnectionDefinition)); return edge;
    }
    private static void NoEdge(RedGraph graph, QuestOutputConnectorViewModel source, QuestInputConnectorViewModel target) =>
        Assert.DoesNotContain(graph.Connections, edge => ReferenceEquals(edge.Source, source) && ReferenceEquals(edge.Target, target));
    private static GraphSnapshot Snapshot(RedGraph graph)
    {
        var handles = new Dictionary<questSocketDefinition, CHandle<graphGraphConnectionDefinition>[]>(ReferenceEqualityComparer.Instance);
        foreach (var socket in graph.Nodes.Select(node => (graphGraphNodeDefinition)node.Data).SelectMany(node => node.Sockets)
            .Select(handle => handle.GetValue()).OfType<questSocketDefinition>())
            handles.TryAdd(socket, socket.Connections.ToArray());
        return new GraphSnapshot(graph.Connections.ToArray(), handles);
    }
    private static void Unchanged(RedGraph graph, GraphSnapshot before)
    {
        SameReferences(before.Edges, graph.Connections);
        foreach (var (socket, handles) in before.Handles) SameReferences(handles, socket.Connections);
    }
    private static void Preserved(RedGraph graph, GraphSnapshot before)
    {
        foreach (var edge in before.Edges) Assert.Contains(graph.Connections, item => ReferenceEquals(item, edge));
        foreach (var (socket, handles) in before.Handles) foreach (var handle in handles)
            Assert.Contains(socket.Connections, item => ReferenceEquals(item, handle));
    }
    private static void SameReferences<T>(IEnumerable<T> expected, IEnumerable<T> actual) where T : class
    {
        var first = expected.ToArray(); var second = actual.ToArray();
        Assert.Equal(first.Length, second.Length);
        for (var index = 0; index < first.Length; index++) Assert.Same(first[index], second[index]);
    }
    private sealed record GraphSnapshot(ConnectionViewModel[] Edges, Dictionary<questSocketDefinition, CHandle<graphGraphConnectionDefinition>[]> Handles);
    private sealed class Fixture : IDisposable
    {
        public RedGraph Graph { get; }
        public List<BaseQuestViewModel> Nodes { get; } = [];
        public Fixture(string name) => Graph = RedGraph.GenerateQuestGraph(name, new questGraphDefinition(), Mock.Of<INodeWrapperFactory>());
        public BaseQuestViewModel Add(Type type)
        {
            var id = Graph.CreateQuestNode(type, new Point(Nodes.Count * 150, 60));
            var node = Graph.Nodes.OfType<BaseQuestViewModel>().Single(candidate => candidate.UniqueId == id); Nodes.Add(node); return node;
        }
        public void Dispose() => Graph.Dispose();
    }
    private static ResourceDictionary CreateResources()
    {
        var resources = new ResourceDictionary { ["ContentBackground"] = Brushes.Black, ["WolvenKitYellow"] = Brushes.Yellow,
            ["WolvenKitGreen"] = Brushes.Green, ["WolvenKitRed"] = Brushes.Red, ["WolvenKitPurple"] = Brushes.Purple,
            ["BooleanToVisibilityConverter"] = new BooleanToVisibilityConverter(), ["WolvenKitGridScale"] = 1d,
            ["WolvenKitMarginTiny"] = new Thickness(4), ["WolvenKitIconMicro"] = 12d };
        resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Nodify;component/Themes/Generic.xaml") }); return resources;
    }
    private static void Layout(Window window)
    {
        window.UpdateLayout(); Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); if (child is T value) yield return value;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
