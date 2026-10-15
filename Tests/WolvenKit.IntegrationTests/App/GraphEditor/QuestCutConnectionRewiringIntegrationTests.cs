using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Concurrency;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Moq;
using Nodify;
using ReactiveUI;
using Splat;
using WolvenKit.App.Factories;
using WolvenKit.App.Services;
using WolvenKit.App.ViewModels.GraphEditor;
using WolvenKit.App.ViewModels.GraphEditor.Nodes;
using WolvenKit.App.ViewModels.GraphEditor.Nodes.Quest;
using WolvenKit.App.ViewModels.GraphEditor.Nodes.Quest.Internal;
using WolvenKit.Common;
using WolvenKit.Core.Interfaces;
using WolvenKit.RED4.Types;
using WolvenKit.Views.GraphEditor;
using Xunit;
using Point = System.Windows.Point;
using Window = System.Windows.Window;

namespace WolvenKit.IntegrationTests.App.GraphEditor;

public class QuestCutConnectionRewiringIntegrationTests
{
    // Run in an isolated testhost, as WPF permits only one Application per process.
    [StaFact]
    public void ExistingCutCableUsesRoutedEndpointHandlesAndPreservesDataAcrossMovesAndCancellation()
    {
        using var resolver = new ModernDependencyResolver();
        using var restoreResolver = resolver.WithResolver(suppressResolverCallback: true);
        resolver.InitializeSplat();
        var ownsApplication = Application.Current is null;
        var application = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var resources = CreateResources();
        application.Resources.MergedDictionaries.Add(resources);
        var previousScheduler = RxApp.MainThreadScheduler;
        RxApp.MainThreadScheduler = new SynchronizationContextScheduler(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var previousSelection = NodeSelectionService.Instance.SelectedNode;
        var factory = new Mock<INodeWrapperFactory>();
        factory.Setup(value => value.QuestPhaseNodeDefinitionWrapper(It.IsAny<questPhaseNodeDefinition>()))
            .Returns((questPhaseNodeDefinition definition) => new questPhaseNodeDefinitionWrapper(definition,
                Mock.Of<ILoggerService>(), factory.Object, Mock.Of<IArchiveManager>()));
        using var graph = RedGraph.GenerateQuestGraph("cut-rewiring-ui.questphase", new questGraphDefinition(), factory.Object);
        GraphEditorView? view = null;
        Window? window = null;
        try
        {
            var locations = new[] { new Point(40, 80), new Point(40, 350), new Point(400, 80), new Point(760, 350), new Point(400, 570) };
            var types = new[] { typeof(questCutControlNodeDefinition), typeof(questCutControlNodeDefinition),
                typeof(questPauseConditionNodeDefinition), typeof(questPhaseNodeDefinition), typeof(questPauseConditionNodeDefinition) };
            for (var index = 0; index < types.Length; index++) graph.CreateQuestNode(types[index], locations[index]);
            var nodes = graph.Nodes.ToArray();
            var a = (QuestOutputConnectorViewModel)nodes[0].Output.Single(port => port.Name == "CutSource");
            var b = (QuestOutputConnectorViewModel)nodes[1].Output.Single(port => port.Name == "CutSource");
            var pause = (QuestInputConnectorViewModel)nodes[2].Input.Single(port => port.Name == "CutDestination");
            var phase = (QuestInputConnectorViewModel)nodes[3].Input.Single(port => port.Name == "CutDestination");
            var branchTarget = (QuestInputConnectorViewModel)nodes[4].Input.Single(port => port.Name == "CutDestination");
            var ordinaryOutput = (QuestOutputConnectorViewModel)nodes[0].Output.Single(port => port.Name == "Out");
            var ordinaryInput = (QuestInputConnectorViewModel)nodes[2].Input.Single(port => port.Name == "In");
            // Seed existing backing data directly: this fixture does not add a Cut-creation workflow.
            var edge = Seed(graph, a, pause);
            edge.PathType = 1;
            edge.IsSelected = true;
            var branch = Seed(graph, a, branchTarget);
            var ordinary = Seed(graph, ordinaryOutput, ordinaryInput);
            var definition = edge.ConnectionDefinition;
            var sourceHandle = a.Data.Connections.Single(handle => ReferenceEquals(handle.GetValue(), definition));
            var destinationHandle = Assert.Single(pause.Data.Connections);
            var branchSourceHandle = a.Data.Connections.Single(handle => ReferenceEquals(handle.GetValue(), branch.ConnectionDefinition));
            var branchSource = branch.ConnectionDefinition.Source;
            var branchDestination = branch.ConnectionDefinition.Destination;
            var unaffected = new Dictionary<questSocketDefinition, CHandle<graphGraphConnectionDefinition>[]>(ReferenceEqualityComparer.Instance);
            foreach (var socket in new[] { branchTarget.Data, ordinaryOutput.Data, ordinaryInput.Data })
                unaffected.Add(socket, socket.Connections.ToArray());

            view = new GraphEditorView { Source = graph };
            window = new Window { Content = view, Width = 1250, Height = 950, Left = -20000, Top = -20000,
                WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false,
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize };
            window.Show();
            Layout(window);
            for (var index = 0; index < nodes.Length; index++) nodes[index].Location = locations[index];
            view.Editor.SetCurrentValue(NodifyEditor.ViewportZoomProperty, 1d);
            view.Editor.SetCurrentValue(NodifyEditor.ViewportLocationProperty, new Point());
            Layout(window);

            void AssertPreserved(QuestOutputConnectorViewModel source, QuestInputConnectorViewModel target)
            {
                edge = Assert.Single(graph.Connections.OfType<QuestConnectionViewModel>(), candidate => ReferenceEquals(candidate.ConnectionDefinition, definition));
                Assert.Same(source, edge.Source);
                Assert.Same(target, edge.Target);
                Assert.Same(source.Data, definition.Source.GetValue());
                Assert.Same(target.Data, definition.Destination.GetValue());
                Assert.Same(sourceHandle, source.Data.Connections.Single(handle => ReferenceEquals(handle.GetValue(), definition)));
                Assert.Same(destinationHandle, target.Data.Connections.Single(handle => ReferenceEquals(handle.GetValue(), definition)));
                Assert.Equal(Enums.questSocketType.CutSource, (Enums.questSocketType)source.Data.Type);
                Assert.Equal(Enums.questSocketType.CutDestination, (Enums.questSocketType)target.Data.Type);
                Assert.Equal(1, edge.PathType);
                Assert.Equal(3, graph.Connections.Count);
                Assert.Contains(graph.Connections, candidate => ReferenceEquals(candidate, branch));
                Assert.Contains(graph.Connections, candidate => ReferenceEquals(candidate, ordinary));
                Assert.Same(branchSourceHandle, a.Data.Connections.Single(handle => ReferenceEquals(handle.GetValue(), branch.ConnectionDefinition)));
                Assert.Same(branchSource, branch.ConnectionDefinition.Source);
                Assert.Same(branchDestination, branch.ConnectionDefinition.Destination);
                foreach (var pair in unaffected)
                {
                    var socket = Assert.IsType<questSocketDefinition>(pair.Key);
                    Assert.Equal(pair.Value.Length, socket.Connections.Count);
                    for (var index = 0; index < pair.Value.Length; index++) Assert.Same(pair.Value[index], socket.Connections[index]);
                }
                foreach (var socket in new[] { a.Data, b.Data, pause.Data, phase.Data })
                    Assert.Equal(ReferenceEquals(socket, source.Data) || ReferenceEquals(socket, target.Data) ? 1 : 0,
                        socket.Connections.Count(handle => ReferenceEquals(handle.GetValue(), definition)));
            }

            // Cancellation preserves the original selected VM; successful native realization may change visual selection.
            StartHandleDrag(view, edge, QuestConnectionEndpoint.Destination, window);
            var selectedOriginal = edge;
            var initialEscape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(view.Editor)!, Environment.TickCount, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            view.Editor.RaiseEvent(initialEscape);
            Assert.True(initialEscape.Handled);
            AssertPreserved(a, pause);
            Assert.Same(selectedOriginal, edge);
            Assert.True(edge.IsSelected);
            AssertClean(view);

            // The existing Cut cable exposes both normal endpoint handles on real routed hover.
            var fixedSource = definition.Source;
            StartHandleDrag(view, edge, QuestConnectionEndpoint.Destination, window);
            AssertPreserved(a, pause); // Preview must not mutate the backing graph.
            Feedback(view, a, QuestRewireSocketFeedback.Fixed);
            Feedback(view, pause, QuestRewireSocketFeedback.Moving);
            Feedback(view, phase, QuestRewireSocketFeedback.Compatible);
            Feedback(view, branchTarget, QuestRewireSocketFeedback.Incompatible);
            Feedback(view, ordinaryInput, QuestRewireSocketFeedback.Incompatible);
            RaisePointerMove(FindConnector(view, phase));
            Assert.Equal("Release to rewire · Esc cancels", Assert.IsType<TextBlock>(view.FindName("QuestRewireHintText")).Text);
            Assert.Same(Brushes.LightGreen, Assert.IsType<Connection>(view.FindName("QuestRewirePreview")).Stroke);
            Assert.True(RaiseMouse(FindConnector(view, phase), Mouse.PreviewMouseUpEvent).Handled);
            AssertPreserved(a, phase);
            Assert.Same(fixedSource, definition.Source);
            AssertClean(view);

            var fixedDestination = definition.Destination;
            StartHandleDrag(view, edge, QuestConnectionEndpoint.Source, window);
            AssertPreserved(a, phase);
            Feedback(view, a, QuestRewireSocketFeedback.Moving);
            Feedback(view, phase, QuestRewireSocketFeedback.Fixed);
            Feedback(view, b, QuestRewireSocketFeedback.Compatible);
            Feedback(view, ordinaryOutput, QuestRewireSocketFeedback.Incompatible);
            RaisePointerMove(FindConnector(view, b));
            Assert.True(RaiseMouse(FindConnector(view, b), Mouse.PreviewMouseUpEvent).Handled);
            AssertPreserved(b, phase);
            Assert.Same(fixedDestination, definition.Destination);
            AssertClean(view);

            // Escape, empty canvas and mixed ordinary/Cut sockets never delete or convert a cable.
            var unchangedSource = definition.Source;
            var unchangedDestination = definition.Destination;
            foreach (var endpoint in new[] { QuestConnectionEndpoint.Source, QuestConnectionEndpoint.Destination })
            {
                var retainedSelection = edge.IsSelected;
                StartHandleDrag(view, edge, endpoint, window);
                var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(view.Editor)!, Environment.TickCount, Key.Escape)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                view.Editor.RaiseEvent(escape);
                Assert.True(escape.Handled);
                AssertPreserved(b, phase);
                AssertClean(view);
                StartHandleDrag(view, edge, endpoint, window);
                Assert.True(RaiseMouse(view.Editor, Mouse.PreviewMouseUpEvent).Handled);
                AssertPreserved(b, phase);
                AssertClean(view);
                StartHandleDrag(view, edge, endpoint, window);
                var incompatible = endpoint == QuestConnectionEndpoint.Source ? (BaseConnectorViewModel)ordinaryOutput : ordinaryInput;
                RaisePointerMove(FindConnector(view, incompatible));
                Assert.Same(Brushes.IndianRed, Assert.IsType<Connection>(view.FindName("QuestRewirePreview")).Stroke);
                Assert.True(RaiseMouse(FindConnector(view, incompatible), Mouse.PreviewMouseUpEvent).Handled);
                AssertPreserved(b, phase);
                AssertClean(view);
                Assert.Same(unchangedSource, definition.Source);
                Assert.Same(unchangedDestination, definition.Destination);
                Assert.Equal(retainedSelection, edge.IsSelected);
            }
            StartHandleDrag(view, edge, QuestConnectionEndpoint.Destination, window);
            var unloading = Descendants<Connector>(view.Editor).ToArray();
            window.Content = null;
            Layout(window);
            Assert.False(view.IsQuestConnectionRewiring);
            Assert.NotSame(view.Editor, Mouse.Captured);
            Assert.All(unloading, connector => Assert.Equal(QuestRewireSocketFeedback.None, GraphEditorView.GetQuestRewireSocketState(connector)));
            AssertPreserved(b, phase);
        }
        finally
        {
            if (view is not null) { view.CancelQuestConnectionRewire(); view.Editor.SelectedItems?.Clear(); view.Source = null; }
            if (window is not null) { window.Content = null; window.Close(); }
            NodeSelectionService.Instance.SelectedNode = previousSelection;
            RxApp.MainThreadScheduler = previousScheduler;
            application.Resources.MergedDictionaries.Remove(resources);
            if (ownsApplication) application.Shutdown();
        }
    }

    private static QuestConnectionViewModel Seed(RedGraph graph, QuestOutputConnectorViewModel source, QuestInputConnectorViewModel target)
    {
        var definition = new graphGraphConnectionDefinition { Source = new CWeakHandle<graphGraphSocketDefinition>(source.Data),
            Destination = new CWeakHandle<graphGraphSocketDefinition>(target.Data) };
        source.Data.Connections.Add(new CHandle<graphGraphConnectionDefinition>(definition));
        target.Data.Connections.Add(new CHandle<graphGraphConnectionDefinition>(definition));
        var edge = new QuestConnectionViewModel(source, target, definition);
        graph.Connections.Add(edge);
        source.IsConnected = target.IsConnected = true;
        return edge;
    }

    private static void StartHandleDrag(GraphEditorView view, QuestConnectionViewModel edge, QuestConnectionEndpoint endpoint, Window window)
    {
        RaisePointerMove(Assert.Single(Descendants<Connection>(view.Editor), control => ReferenceEquals(control.DataContext, edge)));
        Layout(window);
        foreach (var name in new[] { "QuestRewireSourceHandle", "QuestRewireDestinationHandle" })
            Assert.True(Assert.IsType<Border>(view.FindName(name)).IsVisible);
        var handle = Assert.IsType<Border>(view.FindName(endpoint == QuestConnectionEndpoint.Source ? "QuestRewireSourceHandle" : "QuestRewireDestinationHandle"));
        Assert.Equal(endpoint, handle.Tag);
        Assert.True(RaiseMouse(handle, Mouse.PreviewMouseDownEvent).Handled);
        Assert.True(view.IsQuestConnectionRewiring);
        Assert.Same(view.Editor, Mouse.Captured);
        Assert.True(Assert.IsType<Connection>(view.FindName("QuestRewirePreview")).IsVisible);
    }

    // Routed events exercise the production WPF handlers without sending desktop input.
    private static MouseButtonEventArgs RaiseMouse(UIElement source, RoutedEvent routedEvent)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = routedEvent };
        source.RaiseEvent(args);
        return args;
    }

    private static void RaisePointerMove(UIElement source) => source.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
        { RoutedEvent = Mouse.PreviewMouseMoveEvent });
    private static Connector FindConnector(GraphEditorView view, BaseConnectorViewModel port) =>
        Assert.Single(Descendants<Connector>(view.Editor), control => ReferenceEquals(control.DataContext, port));
    private static void Feedback(GraphEditorView view, BaseConnectorViewModel port, QuestRewireSocketFeedback expected) =>
        Assert.Equal(expected, GraphEditorView.GetQuestRewireSocketState(FindConnector(view, port)));
    private static void AssertClean(GraphEditorView view)
    {
        Assert.False(view.IsQuestConnectionRewiring);
        Assert.NotSame(view.Editor, Mouse.Captured);
        Assert.False(Assert.IsType<Connection>(view.FindName("QuestRewirePreview")).IsVisible);
        Assert.All(Descendants<Connector>(view.Editor), connector => Assert.Equal(QuestRewireSocketFeedback.None, GraphEditorView.GetQuestRewireSocketState(connector)));
    }
    private static void Layout(Window window)
    {
        window.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
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
    private static ResourceDictionary CreateResources()
    {
        var resources = new ResourceDictionary { ["ContentBackground"] = Brushes.Black, ["WolvenKitYellow"] = Brushes.Yellow,
            ["WolvenKitGreen"] = Brushes.Green, ["WolvenKitRed"] = Brushes.Red,
            ["BooleanToVisibilityConverter"] = new BooleanToVisibilityConverter(), ["WolvenKitGridScale"] = 1d };
        resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Nodify;component/Themes/Generic.xaml") });
        return resources;
    }
}

