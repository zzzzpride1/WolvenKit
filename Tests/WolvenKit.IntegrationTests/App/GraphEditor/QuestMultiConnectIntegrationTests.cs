using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Moq;
using Nodify;
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

namespace WolvenKit.IntegrationTests.App.GraphEditor;

public class QuestMultiConnectIntegrationTests
{
    // Like the existing graph fixtures, this class runs in its own WPF testhost process.
    [StaFact]
    public void RoutedShiftSwipePreviewsOneBatchAndCoexistsWithExistingGestures()
    {
        using var resolver = new ModernDependencyResolver();
        using var restoreResolver = resolver.WithResolver(suppressResolverCallback: true);
        resolver.InitializeSplat();
        var ownsApplication = Application.Current is null;
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var resources = CreateResources();
        app.Resources.MergedDictionaries.Add(resources);
        var previousSelection = NodeSelectionService.Instance.SelectedNode;
        GraphEditorView? view = null;
        Window? window = null;
        using var graph = RedGraph.GenerateQuestGraph("multi-ui.questphase", new questGraphDefinition(), Mock.Of<INodeWrapperFactory>());
        try
        {
            var types = new[] { typeof(questFactsDBManagerNodeDefinition), typeof(questPauseConditionNodeDefinition),
                typeof(questConditionNodeDefinition), typeof(questEventManagerNodeDefinition), typeof(questFlowControlNodeDefinition) };
            var positions = new[] { new Point(30, 80), new Point(350, 80), new Point(350, 290), new Point(350, 500), new Point(700, 100) };
            for (var i = 0; i < types.Length; i++) graph.CreateQuestNode(types[i], positions[i]);
            var nodes = graph.Nodes.ToArray();
            var source = (QuestOutputConnectorViewModel)nodes[0].Output.Single(port => port.Name == "Out");
            var b = (QuestInputConnectorViewModel)nodes[1].Input.Single(port => port.Name == "In");
            var c = (QuestInputConnectorViewModel)nodes[2].Input.Single(port => port.Name == "In");
            var d = (QuestInputConnectorViewModel)nodes[3].Input.Single(port => port.Name == "In");
            var alternate = (QuestOutputConnectorViewModel)nodes[2].Output.Single(port => port.Name == "True");
            var cut = nodes[1].Input.Single(port => port.Name == "CutDestination");
            view = new GraphEditorView { Source = graph };
            window = new Window { Content = view, Width = 1300, Height = 950, Left = -20000, Top = -20000,
                ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
                WindowStartupLocation = WindowStartupLocation.Manual, ResizeMode = ResizeMode.NoResize };
            window.Show();
            Layout(window);
            for (var i = 0; i < nodes.Length; i++) nodes[i].Location = positions[i];
            view.Editor.SetCurrentValue(NodifyEditor.ViewportZoomProperty, 1d);
            view.Editor.SetCurrentValue(NodifyEditor.ViewportLocationProperty, new Point());
            Layout(window);

            // Shift has real routed key events; no system input or WPF private state is modified.
            Start(view, source);
            Assert.Empty(graph.Connections);
            Assert.Equal(QuestMultiConnectFeedback.Source, GraphEditorView.GetQuestMultiConnectState(Port(view, source)));
            foreach (var node in new[] { nodes[1], nodes[2], nodes[1] }) Move(Body(view, node));
            Move(Body(view, nodes[4])); // Multiple ordinary inputs: do not guess one.
            Assert.Equal(QuestMultiConnectFeedback.Ambiguous, GraphEditorView.GetQuestMultiConnectState(Body(view, nodes[4])));
            Move(Port(view, cut)); // An explicit invalid socket must not fall through to node targeting.
            Move(Port(view, alternate));
            Move(Port(view, d));
            Move(Body(view, nodes[2]));
            foreach (var target in new[] { b, c, d })
                Assert.Equal(QuestMultiConnectFeedback.Queued, GraphEditorView.GetQuestMultiConnectState(Port(view, target)));
            Assert.Empty(graph.Connections);
            Assert.Empty(source.Data.Connections);
            var overlay = Assert.IsType<Canvas>(view.FindName("QuestMultiConnectOverlay"));
            Assert.Equal(4, overlay.Children.OfType<Connection>().Count()); // One pointer cable and three queued targets.
            Layout(window);
            var fixedPoint = SocketCenter(view, source);
            foreach (var target in new[] { b, c, d })
            {
                var point = SocketCenter(view, target);
                Assert.True(overlay.Children.OfType<Connection>().Any(cable =>
                    (cable.Source - fixedPoint).Length < 0.5 && (cable.Target - point).Length < 0.5),
                    $"Expected {fixedPoint} -> {point}; native {Port(view, source).Anchor} / {Port(view, target).Anchor}; " +
                    string.Join("; ", overlay.Children.OfType<Connection>().Select(cable => $"{cable.Source} -> {cable.Target}")));
            }
            var picked = Port(view, source).FindConnectionTarget(view.Editor.GetLocationInsideEditor(SocketCenter(view, b), view.Editor));
            Assert.Same(b, picked?.DataContext); // Native hit testing finds the port through the preview layer.
            var snapshot = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            snapshot.Render(view);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(snapshot));
            using (var image = File.Create(Path.Combine(AppContext.BaseDirectory, "quest-multiconnect-ui-preview.png"))) encoder.Save(image);
            Assert.True(MouseEvent(view.Editor, Mouse.PreviewMouseUpEvent).Handled);
            Assert.Equal(3, graph.Connections.Count);
            foreach (var target in new[] { b, c, d }) AssertEdge(graph, source, target);
            AssertClean(view);
            KeyEvent(view.Editor, Key.LeftShift, false);
            var originals = graph.Connections.ToArray();
            var handles = source.Data.Connections.ToArray();

            Start(view, source);
            Move(Body(view, nodes[1]));
            Move(Body(view, nodes[1]));
            MouseEvent(view.Editor, Mouse.PreviewMouseUpEvent);
            Assert.Equal(originals, graph.Connections.ToArray());
            Assert.Equal(handles, source.Data.Connections.ToArray());
            AssertClean(view);

            // Cancellation after several accepted targets creates no backing or visual edges.
            Start(view, alternate);
            Move(Body(view, nodes[1])); Move(Body(view, nodes[3]));
            Assert.True(KeyEvent(view.Editor, Key.Escape, true).Handled);
            Assert.Equal(originals, graph.Connections.ToArray());
            Assert.Empty(alternate.Data.Connections);
            AssertClean(view);

            // Both the fixed source and queued endpoints follow viewport transforms.
            Start(view, alternate);
            Move(Body(view, nodes[1]));
            Layout(window);
            var preview = Assert.IsType<Connection>(view.FindName("QuestMultiConnectPreview"));
            var before = preview.Source;
            view.Editor.SetCurrentValue(NodifyEditor.ViewportZoomProperty, 1.4d);
            view.Editor.SetCurrentValue(NodifyEditor.ViewportLocationProperty, new Point(40, 20));
            Layout(window);
            Assert.True(view.IsQuestMultiConnecting);
            Assert.True((preview.Source - before).Length > 1);
            var projected = SocketCenter(view, alternate);
            Assert.InRange((preview.Source - projected).Length, 0, 0.5);
            var queued = Assert.Single(overlay.Children.OfType<Connection>(), cable => !ReferenceEquals(cable, preview));
            Assert.InRange((queued.Target - SocketCenter(view, b)).Length, 0, 0.5);
            Assert.True(KeyEvent(view.Editor, Key.LeftShift, false).Handled);
            Assert.Equal(originals, graph.Connections.ToArray());
            AssertClean(view);

            // Middle button hands control back to native panning, without committing the pending batch.
            Start(view, alternate); Move(Body(view, nodes[1]));
            Assert.False(MouseEvent(view.Editor, Mouse.PreviewMouseDownEvent, MouseButton.Middle).Handled);
            AssertClean(view);
            Assert.Equal(originals, graph.Connections.ToArray());

            // Node dragging/marquee and Ctrl/Alt selection chords are not claimed by Shift-swipe.
            KeyEvent(view.Editor, Key.LeftShift, true);
            Assert.False(MouseEvent(Body(view, nodes[0]), Mouse.PreviewMouseDownEvent).Handled);
            Assert.False(view.IsQuestMultiConnecting);
            foreach (var key in new[] { Key.LeftCtrl, Key.LeftAlt })
            {
                KeyEvent(view.Editor, key, true);
                Assert.False(MouseEvent(Port(view, source), Mouse.PreviewMouseDownEvent).Handled);
                Assert.False(view.IsQuestMultiConnecting);
                KeyEvent(view.Editor, key, false);
            }
            KeyEvent(view.Editor, Key.LeftShift, false);
            var sourceControl = Assert.IsType<NodeOutput>(Port(view, source));
            var header = sourceControl.Header;
            var text = new TextBox { Text = "property editing" };
            sourceControl.SetCurrentValue(NodeOutput.HeaderProperty, text);
            Layout(window);
            KeyEvent(text, Key.LeftShift, true);
            Assert.False(MouseEvent(text, Mouse.PreviewMouseDownEvent).Handled);
            Assert.False(view.IsQuestMultiConnecting);
            KeyEvent(text, Key.LeftShift, false);
            sourceControl.SetCurrentValue(NodeOutput.HeaderProperty, header);
            Layout(window);

            // Starting rewiring cancels an in-progress swipe; both features retain the original edges.
            Start(view, alternate); Move(Body(view, nodes[1]));
            var edge = Assert.IsType<QuestConnectionViewModel>(originals[0]);
            Assert.True(view.BeginQuestConnectionRewire(edge, QuestConnectionEndpoint.Destination));
            Assert.False(view.IsQuestMultiConnecting);
            Assert.True(view.IsQuestConnectionRewiring);
            view.CancelQuestConnectionRewire();
            Assert.Equal(originals, graph.Connections.ToArray());

            Start(view, alternate); Move(Body(view, nodes[1]));
            using (var other = new RedGraph("multi-other.scene", new scnSceneResource()))
            {
                view.Source = other; Layout(window); AssertClean(view);
                Assert.Equal(originals, graph.Connections.ToArray());
                view.Source = graph; Layout(window);
            }
            // Native creation still follows the existing pending-connection command after all cancellation paths.
            var native = Port(view, alternate);
            native.BeginConnecting();
            native.UpdatePendingConnection(d.Anchor);
            native.EndConnecting(Port(view, d));
            Assert.Equal(4, graph.Connections.Count);
            AssertEdge(graph, alternate, d);
            AssertClean(view);

            Start(view, alternate); Move(Body(view, nodes[1]));
            Mouse.Capture(null); AssertClean(view);
            Assert.Equal(4, graph.Connections.Count);
            Start(view, alternate); Move(Body(view, nodes[1]));
            window.Content = null; Layout(window); AssertClean(view);
            Assert.Equal(4, graph.Connections.Count);
        }
        finally
        {
            if (view is not null)
            {
                view.CancelQuestMultiConnect(); view.CancelQuestConnectionRewire();
                view.Editor.SelectedItems?.Clear(); view.Source = null;
            }
            if (window is not null) { window.Content = null; window.Close(); }
            NodeSelectionService.Instance.SelectedNode = previousSelection;
            app.Resources.MergedDictionaries.Remove(resources);
            if (ownsApplication) app.Shutdown();
        }
    }

    private static void Start(GraphEditorView view, BaseConnectorViewModel source)
    {
        KeyEvent(view.Editor, Key.LeftShift, true);
        Assert.True(MouseEvent(Port(view, source), Mouse.PreviewMouseDownEvent).Handled);
        Assert.True(view.IsQuestMultiConnecting);
        Assert.Same(view.Editor, Mouse.Captured);
    }
    private static KeyEventArgs KeyEvent(UIElement source, Key key, bool down)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(source)!, Environment.TickCount, key)
            { RoutedEvent = down ? Keyboard.PreviewKeyDownEvent : Keyboard.PreviewKeyUpEvent };
        source.RaiseEvent(args); return args;
    }
    private static MouseButtonEventArgs MouseEvent(UIElement source, RoutedEvent routedEvent, MouseButton button = MouseButton.Left)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, button) { RoutedEvent = routedEvent };
        source.RaiseEvent(args); return args;
    }
    private static void Move(UIElement source) => source.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
        { RoutedEvent = Mouse.PreviewMouseMoveEvent });
    private static Point SocketCenter(GraphEditorView view, BaseConnectorViewModel port)
    {
        var control = Port(view, port);
        var thumb = Assert.IsAssignableFrom<FrameworkElement>(control.Template.FindName("PART_Connector", control));
        return thumb.TranslatePoint(new Point(thumb.ActualWidth / 2, thumb.ActualHeight / 2), view.Editor);
    }
    private static Connector Port(GraphEditorView view, BaseConnectorViewModel port) =>
        Assert.Single(Descendants<Connector>(view.Editor), control => ReferenceEquals(control.DataContext, port));
    private static Node Body(GraphEditorView view, NodeViewModel node) =>
        Assert.Single(Descendants<Node>(view.Editor), control => ReferenceEquals(control.DataContext, node));
    private static void AssertEdge(RedGraph graph, QuestOutputConnectorViewModel source, QuestInputConnectorViewModel target)
    {
        var edge = Assert.Single(graph.Connections.OfType<QuestConnectionViewModel>(), value =>
            ReferenceEquals(value.Source, source) && ReferenceEquals(value.Target, target));
        Assert.Same(source.Data, edge.ConnectionDefinition.Source.GetValue());
        Assert.Same(target.Data, edge.ConnectionDefinition.Destination.GetValue());
        Assert.Single(source.Data.Connections, handle => ReferenceEquals(handle.GetValue(), edge.ConnectionDefinition));
        Assert.Single(target.Data.Connections, handle => ReferenceEquals(handle.GetValue(), edge.ConnectionDefinition));
    }
    private static void AssertClean(GraphEditorView view)
    {
        Assert.False(view.IsQuestMultiConnecting);
        Assert.NotSame(view.Editor, Mouse.Captured);
        if (view.FindName("QuestMultiConnectOverlay") is Canvas overlay)
            Assert.False(overlay.IsVisible);
        Assert.All(Descendants<Connector>(view.Editor), control => Assert.Equal(QuestMultiConnectFeedback.None,
            GraphEditorView.GetQuestMultiConnectState(control)));
        Assert.All(Descendants<Node>(view.Editor), control => Assert.Equal(QuestMultiConnectFeedback.None,
            GraphEditorView.GetQuestMultiConnectState(control)));
    }
    private static ResourceDictionary CreateResources()
    {
        var resources = new ResourceDictionary { ["ContentBackground"] = Brushes.Black, ["WolvenKitYellow"] = Brushes.Yellow,
            ["WolvenKitGreen"] = Brushes.Green, ["WolvenKitRed"] = Brushes.Red,
            ["BooleanToVisibilityConverter"] = new BooleanToVisibilityConverter(), ["WolvenKitGridScale"] = 1d };
        resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("pack://application:,,,/Nodify;component/Themes/Generic.xaml") });
        return resources;
    }
    private static void Layout(Window window)
    {
        window.UpdateLayout(); Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T value) yield return value;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
