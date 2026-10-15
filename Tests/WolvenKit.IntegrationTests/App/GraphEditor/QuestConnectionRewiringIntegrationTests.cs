using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Moq;
using Nodify;
using Nodify.Events;
using Splat;
using WolvenKit.App.Factories;
using WolvenKit.App.Services;
using WolvenKit.App.ViewModels.GraphEditor;
using WolvenKit.App.ViewModels.GraphEditor.Nodes;
using WolvenKit.App.ViewModels.GraphEditor.Nodes.Quest.Internal;
using WolvenKit.RED4.Types;
using WolvenKit.Views.GraphEditor;
using Xunit;
using Point = System.Windows.Point;
using Window = System.Windows.Window;
using Vector = System.Windows.Vector;

namespace WolvenKit.IntegrationTests.App.GraphEditor;

public class QuestConnectionRewiringIntegrationTests
{
    // Run this class in its own testhost. WPF permits one Application per process, and the
    // established QuestGroupDuplicationIntegrationTests fixture shuts its Application down.
    [StaFact]
    public void RoutedEndpointDragsAndCancellationPreserveTheGraphAndOrdinaryConnectionCreation()
    {
        using var resolver = new ModernDependencyResolver();
        using var restoreResolver = resolver.WithResolver(suppressResolverCallback: true);
        resolver.InitializeSplat();
        var ownsApplication = Application.Current is null;
        var application = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var resources = CreateResources();
        application.Resources.MergedDictionaries.Add(resources);
        var previousSelection = NodeSelectionService.Instance.SelectedNode;
        GraphEditorView? view = null;
        Window? window = null;
        using var graph = RedGraph.GenerateQuestGraph("rewiring-ui.questphase", new questGraphDefinition(),
            Mock.Of<INodeWrapperFactory>());
        try
        {
            foreach (var location in new[] { new Point(40, 100), new Point(350, 100), new Point(350, 360), new Point(700, 240) })
            {
                graph.CreateQuestNode(typeof(questLogicalHubNodeDefinition), location);
            }

            var nodes = graph.Nodes.ToArray();
            var a = (QuestOutputConnectorViewModel)nodes[0].Output.Single();
            var b = (QuestInputConnectorViewModel)nodes[1].Input.Single(port => port.Name == "In1");
            var bOutput = (QuestOutputConnectorViewModel)nodes[1].Output.Single();
            var c = (QuestInputConnectorViewModel)nodes[2].Input.Single(port => port.Name == "In1");
            var d = (QuestInputConnectorViewModel)nodes[3].Input.Single(port => port.Name == "In1");
            var dOutput = (QuestOutputConnectorViewModel)nodes[3].Output.Single();
            var cut = nodes[2].Input.Single(port => port.Name == "CutDestination");
            Connect(graph, a, b);
            Connect(graph, a, d);
            var edge = AssertConnection(graph, a, b);
            var branch = AssertConnection(graph, a, d);
            var branchHandle = Assert.Single(d.Data.Connections);

            view = new GraphEditorView { Source = graph };
            window = new Window
            {
                Content = view,
                Width = 1200,
                Height = 850,
                Left = -20000,
                Top = -20000,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                Title = "Quest connection rewiring integration fixture"
            };
            window.Show();
            Layout(window);
            // The view's normal deferred GraphStateLoad arranges nodes on first attachment.
            for (var index = 0; index < nodes.Length; index++)
            {
                nodes[index].Location = new[] { new Point(40, 100), new Point(350, 100), new Point(350, 360), new Point(700, 240) }[index];
            }
            view.Editor.SetCurrentValue(NodifyEditor.ViewportZoomProperty, 1d);
            view.Editor.SetCurrentValue(NodifyEditor.ViewportLocationProperty, new Point());
            Layout(window);
            Assert.True(view.Editor.IsVisible);
            Assert.All(new BaseConnectorViewModel[] { a, b, bOutput, c, d, dOutput, cut },
                port => Assert.True(FindConnector(view, port).IsVisible));

            // Destination handle: A -> B becomes A -> C, preserving the original until release.
            StartHandleDrag(view, edge, QuestConnectionEndpoint.Destination, window);
            Assert.Same(edge, AssertConnection(graph, a, b));
            AssertFeedback(view, a, QuestRewireSocketFeedback.Fixed);
            AssertFeedback(view, b, QuestRewireSocketFeedback.Moving);
            AssertFeedback(view, c, QuestRewireSocketFeedback.Compatible);
            AssertFeedback(view, bOutput, QuestRewireSocketFeedback.Incompatible);
            AssertFeedback(view, cut, QuestRewireSocketFeedback.Incompatible);
            AssertFeedback(view, d, QuestRewireSocketFeedback.Incompatible); // Would duplicate the sibling edge.
            var destinationRelease = RaiseMouse(FindConnector(view, c), Mouse.PreviewMouseUpEvent);
            Assert.True(destinationRelease.Handled);
            edge = AssertConnection(graph, a, c);
            Assert.DoesNotContain(graph.Connections, connection => ReferenceEquals(connection.Source, a) && ReferenceEquals(connection.Target, b));
            Assert.Equal(2, graph.Connections.Count);
            AssertBranchUnchanged(graph, branch, d, branchHandle);
            AssertCleanFeedback(view);

            // Source handle: A -> C becomes B -> C without touching A -> D.
            StartHandleDrag(view, edge, QuestConnectionEndpoint.Source, window);
            Assert.Same(edge, AssertConnection(graph, a, c));
            AssertFeedback(view, a, QuestRewireSocketFeedback.Moving);
            AssertFeedback(view, c, QuestRewireSocketFeedback.Fixed);
            AssertFeedback(view, bOutput, QuestRewireSocketFeedback.Compatible);
            AssertFeedback(view, dOutput, QuestRewireSocketFeedback.Compatible);
            AssertFeedback(view, b, QuestRewireSocketFeedback.Incompatible);
            RaisePointerMove(FindConnector(view, bOutput));
            Assert.True(RaiseMouse(FindConnector(view, bOutput), Mouse.PreviewMouseUpEvent).Handled);
            edge = AssertConnection(graph, bOutput, c);
            Assert.Equal(2, graph.Connections.Count);
            AssertBranchUnchanged(graph, branch, d, branchHandle);
            AssertCleanFeedback(view);

            // Context-menu entry uses the same state machine, with a routed target click to commit.
            view.BeginQuestConnectionRewire(edge, QuestConnectionEndpoint.Source);
            Assert.True(view.IsQuestConnectionRewiring);
            Assert.Same(view.Editor, Mouse.Captured);
            Assert.Same(edge, AssertConnection(graph, bOutput, c));
            RaisePointerMove(FindConnector(view, a));
            Assert.Equal("Click to rewire · Esc cancels", Assert.IsType<TextBlock>(view.FindName("QuestRewireHintText")).Text);
            Assert.True(RaiseMouse(FindConnector(view, a), Mouse.PreviewMouseDownEvent).Handled);
            edge = AssertConnection(graph, a, c);
            AssertBranchUnchanged(graph, branch, d, branchHandle);
            AssertCleanFeedback(view);

            // Wrong-direction and unsupported Cut drops cancel without changing either edge.
            foreach (var incompatible in new BaseConnectorViewModel[] { bOutput, cut })
            {
                StartHandleDrag(view, edge, QuestConnectionEndpoint.Destination, window);
                Assert.True(RaiseMouse(FindConnector(view, incompatible), Mouse.PreviewMouseUpEvent).Handled);
                Assert.Same(edge, AssertConnection(graph, a, c));
                Assert.Equal(2, graph.Connections.Count);
                AssertBranchUnchanged(graph, branch, d, branchHandle);
                AssertCleanFeedback(view);
            }

            // Empty-canvas release and Escape travel through the editor's routed input handlers.
            StartHandleDrag(view, edge, QuestConnectionEndpoint.Destination, window);
            Assert.True(RaiseMouse(view.Editor, Mouse.PreviewMouseUpEvent).Handled);
            Assert.Same(edge, AssertConnection(graph, a, c));
            AssertCleanFeedback(view);
            view.BeginQuestConnectionRewire(edge, QuestConnectionEndpoint.Destination);
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(view.Editor)!, Environment.TickCount, Key.Escape)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            };
            view.Editor.RaiseEvent(escape);
            Assert.True(escape.Handled);
            Assert.Same(edge, AssertConnection(graph, a, c));
            AssertBranchUnchanged(graph, branch, d, branchHandle);
            AssertCleanFeedback(view);

            // Test effective rendered coordinates, whether the preview uses graph or viewport space.
            view.BeginQuestConnectionRewire(edge, QuestConnectionEndpoint.Destination);
            Layout(window);
            var preview = Assert.IsType<Connection>(view.FindName("QuestRewirePreview"));
            var handle = Assert.IsType<Border>(view.FindName("QuestRewireSourceHandle"));
            var before = preview.TranslatePoint(preview.Source, view.Editor);
            var expectedBefore = view.Editor.ViewportTransform.Transform(a.Anchor);
            var handleWidth = RenderedWidth(handle, view.Editor);
            view.Editor.SetCurrentValue(NodifyEditor.ViewportZoomProperty, 1.4d);
            view.Editor.SetCurrentValue(NodifyEditor.ViewportLocationProperty, new Point(70, 45));
            Layout(window);
            Assert.True(view.IsQuestConnectionRewiring);
            var after = preview.TranslatePoint(preview.Source, view.Editor);
            var expectedAfter = view.Editor.ViewportTransform.Transform(a.Anchor);
            Assert.True((after - before).Length > 1);
            AssertNear(expectedAfter - expectedBefore, after - before);
            Assert.InRange(Math.Abs(RenderedWidth(handle, view.Editor) - handleWidth), 0, 0.5);
            AssertNear(a.Anchor, view.Editor.GetLocationInsideEditor(expectedAfter, view.Editor));
            Assert.Same(edge, AssertConnection(graph, a, c));
            view.CancelQuestConnectionRewire();
            AssertCleanFeedback(view);

            // Hover handles must not intercept native socket clicks when zoom enlarges the sockets.
            view.Editor.SetCurrentValue(NodifyEditor.ViewportZoomProperty, 2d);
            RaisePointerMove(Assert.Single(Descendants<Connection>(view.Editor), control => ReferenceEquals(control.DataContext, edge)));
            Layout(window);
            foreach (var port in new BaseConnectorViewModel[] { a, c })
            {
                var center = view.Editor.ViewportTransform.Transform(port.Anchor);
                // This point lies inside the native 14 graph-pixel socket, beyond the cable stroke.
                var socketPoint = new Point(center.X + (port is QuestOutputConnectorViewModel ? 12 : -12), center.Y + 4);
                var hit = view.InputHitTest(view.Editor.TranslatePoint(socketPoint, view)) as DependencyObject;
                Assert.Same(FindConnector(view, port), Ancestor<Connector>(hit));
            }

            // Switching graph scope clears old socket feedback and releases capture.
            view.BeginQuestConnectionRewire(edge, QuestConnectionEndpoint.Destination);
            var oldConnectors = Descendants<Connector>(view.Editor).ToArray();
            using (var otherGraph = new RedGraph("rewiring-other.scene", new scnSceneResource()))
            {
                view.Source = otherGraph;
                Layout(window);
                Assert.False(view.IsQuestConnectionRewiring);
                Assert.NotSame(view.Editor, Mouse.Captured);
                Assert.All(oldConnectors, connector => Assert.Equal(QuestRewireSocketFeedback.None,
                    GraphEditorView.GetQuestRewireSocketState(connector)));
                Assert.Same(edge, AssertConnection(graph, a, c));
                view.Source = graph;
                Layout(window);
            }

            // Deleting an endpoint, or removing the original edge, invalidates the active session.
            view.BeginQuestConnectionRewire(edge, QuestConnectionEndpoint.Destination);
            Assert.True(view.IsQuestConnectionRewiring);
            graph.RemoveNode(nodes[2]);
            Layout(window);
            AssertCleanFeedback(view);
            Assert.DoesNotContain(nodes[2], graph.Nodes);
            Assert.Same(branch, Assert.Single(graph.Connections));
            AssertBranchUnchanged(graph, branch, d, branchHandle);
            view.BeginQuestConnectionRewire(branch, QuestConnectionEndpoint.Source);
            Assert.True(view.IsQuestConnectionRewiring);
            graph.RemoveQuestConnectionPublic(branch);
            Layout(window);
            AssertCleanFeedback(view);
            Assert.Empty(graph.Connections);

            // Native Connector methods raise real pending-connection events. The existing template
            // must still route those events to ConnectCommand after every rewire/cancel path above.
            var nativeSource = FindConnector(view, a);
            var nativeTarget = FindConnector(view, d);
            var starts = 0;
            var completions = 0;
            PendingConnectionEventHandler started = (_, _) => starts++;
            PendingConnectionEventHandler completed = (_, _) => completions++;
            view.Editor.AddHandler(Connector.PendingConnectionStartedEvent, started, true);
            view.Editor.AddHandler(Connector.PendingConnectionCompletedEvent, completed, true);
            try
            {
                nativeSource.BeginConnecting();
                Assert.True(nativeSource.IsPendingConnection);
                Assert.False(view.IsQuestConnectionRewiring);
                Assert.Same(a, graph.PendingConnection.Source);
                nativeSource.UpdatePendingConnection(d.Anchor);
                nativeSource.EndConnecting(nativeTarget);
                Assert.False(nativeSource.IsPendingConnection);
                Assert.Equal(1, starts);
                Assert.Equal(1, completions);
                Assert.Single(graph.Connections);
                edge = AssertConnection(graph, a, d);
                AssertCleanFeedback(view);
            }
            finally
            {
                view.Editor.RemoveHandler(Connector.PendingConnectionStartedEvent, started);
                view.Editor.RemoveHandler(Connector.PendingConnectionCompletedEvent, completed);
                if (nativeSource.IsPendingConnection) nativeSource.CancelConnecting();
            }

            // A real visual-tree unload also cancels, without editing the newly created edge.
            view.BeginQuestConnectionRewire(edge, QuestConnectionEndpoint.Destination);
            Assert.True(view.IsQuestConnectionRewiring);
            var unloadingConnectors = Descendants<Connector>(view.Editor).ToArray();
            window.Content = null;
            Layout(window);
            Assert.False(view.IsQuestConnectionRewiring);
            Assert.NotSame(view.Editor, Mouse.Captured);
            Assert.All(unloadingConnectors, connector => Assert.Equal(QuestRewireSocketFeedback.None,
                GraphEditorView.GetQuestRewireSocketState(connector)));
            Assert.Same(edge, AssertConnection(graph, a, d));
        }
        finally
        {
            if (view is not null)
            {
                view.CancelQuestConnectionRewire();
                view.Editor.SelectedItems?.Clear();
                view.Source = null;
            }
            if (window is not null)
            {
                window.Content = null;
                window.Close();
            }
            NodeSelectionService.Instance.SelectedNode = previousSelection;
            application.Resources.MergedDictionaries.Remove(resources);
            if (ownsApplication) application.Shutdown();
        }
    }

    private static ResourceDictionary CreateResources()
    {
        var resources = new ResourceDictionary
        {
            ["ContentBackground"] = Brushes.Black,
            ["WolvenKitYellow"] = Brushes.Yellow,
            ["WolvenKitGreen"] = Brushes.Green,
            ["WolvenKitRed"] = Brushes.Red,
            ["BooleanToVisibilityConverter"] = new BooleanToVisibilityConverter(),
            ["WolvenKitGridScale"] = 1d
        };
        resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Nodify;component/Themes/Generic.xaml")
        });
        return resources;
    }

    private static void Connect(RedGraph graph, QuestOutputConnectorViewModel source, QuestInputConnectorViewModel target)
    {
        graph.PendingConnection.Source = source;
        graph.PendingConnection.Target = target;
        graph.Connect();
    }

    private static QuestConnectionViewModel AssertConnection(RedGraph graph,
        QuestOutputConnectorViewModel source, QuestInputConnectorViewModel target)
    {
        var connection = Assert.Single(graph.Connections.OfType<QuestConnectionViewModel>(), candidate =>
            ReferenceEquals(candidate.Source, source) && ReferenceEquals(candidate.Target, target));
        Assert.Same(source.Data, connection.ConnectionDefinition.Source.GetValue());
        Assert.Same(target.Data, connection.ConnectionDefinition.Destination.GetValue());
        Assert.Single(source.Data.Connections, handle => ReferenceEquals(handle.GetValue(), connection.ConnectionDefinition));
        Assert.Single(target.Data.Connections, handle => ReferenceEquals(handle.GetValue(), connection.ConnectionDefinition));
        return connection;
    }

    private static void AssertBranchUnchanged(RedGraph graph, QuestConnectionViewModel branch,
        QuestInputConnectorViewModel target, CHandle<graphGraphConnectionDefinition> handle)
    {
        Assert.Contains(branch, graph.Connections);
        Assert.Same(handle, Assert.Single(target.Data.Connections));
        Assert.Same(branch.ConnectionDefinition, handle.GetValue());
        AssertConnection(graph, (QuestOutputConnectorViewModel)branch.Source, target);
    }

    private static void StartHandleDrag(GraphEditorView view, QuestConnectionViewModel edge,
        QuestConnectionEndpoint endpoint, Window window)
    {
        var cable = Assert.Single(Descendants<Connection>(view.Editor), control => ReferenceEquals(control.DataContext, edge));
        RaisePointerMove(cable);
        Layout(window);
        var handleName = endpoint == QuestConnectionEndpoint.Source ? "QuestRewireSourceHandle" : "QuestRewireDestinationHandle";
        var handle = Assert.IsType<Border>(view.FindName(handleName));
        Assert.True(handle.IsVisible);
        Assert.Equal(endpoint, handle.Tag);
        Assert.True(RaiseMouse(handle, Mouse.PreviewMouseDownEvent).Handled);
        Assert.True(view.IsQuestConnectionRewiring);
        Assert.Same(view.Editor, Mouse.Captured);
        Assert.True(Assert.IsType<Connection>(view.FindName("QuestRewirePreview")).IsVisible);
    }

    private static MouseButtonEventArgs RaiseMouse(UIElement source, RoutedEvent routedEvent)
    {
        // RaiseEvent tests real WPF tunneling/handler routes. It deliberately does not fake
        // the physical MouseDevice button state or send input to the user's desktop.
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = routedEvent
        };
        source.RaiseEvent(args);
        return args;
    }

    private static void RaisePointerMove(UIElement source) => source.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
    {
        RoutedEvent = Mouse.PreviewMouseMoveEvent
    });

    private static Connector FindConnector(GraphEditorView view, BaseConnectorViewModel port) =>
        Assert.Single(Descendants<Connector>(view.Editor), control => ReferenceEquals(control.DataContext, port));

    private static void AssertFeedback(GraphEditorView view, BaseConnectorViewModel port, QuestRewireSocketFeedback expected) =>
        Assert.Equal(expected, GraphEditorView.GetQuestRewireSocketState(FindConnector(view, port)));

    private static void AssertCleanFeedback(GraphEditorView view)
    {
        Assert.False(view.IsQuestConnectionRewiring);
        Assert.NotSame(view.Editor, Mouse.Captured);
        Assert.False(Assert.IsType<Connection>(view.FindName("QuestRewirePreview")).IsVisible);
        Assert.All(Descendants<Connector>(view.Editor), connector => Assert.Equal(QuestRewireSocketFeedback.None,
            GraphEditorView.GetQuestRewireSocketState(connector)));
    }

    private static double RenderedWidth(FrameworkElement element, UIElement relativeTo) =>
        (element.TranslatePoint(new Point(element.ActualWidth, 0), relativeTo) - element.TranslatePoint(new Point(), relativeTo)).Length;

    private static void AssertNear(Point expected, Point actual) => AssertNear((Vector)expected, (Vector)actual);

    private static void AssertNear(Vector expected, Vector actual)
    {
        Assert.InRange(Math.Abs(expected.X - actual.X), 0, 0.5);
        Assert.InRange(Math.Abs(expected.Y - actual.Y), 0, 0.5);
    }

    private static void Layout(Window window)
    {
        window.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private static T? Ancestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T match) return match;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
