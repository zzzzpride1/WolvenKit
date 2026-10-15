using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Moq;
using Splat;
using WolvenKit.App.Factories;
using WolvenKit.App.Services;
using WolvenKit.App.ViewModels.GraphEditor;
using WolvenKit.RED4.Types;
using WolvenKit.Views.GraphEditor;
using Xunit;
using Point = System.Windows.Point;

namespace WolvenKit.IntegrationTests.App.GraphEditor;

public class QuestGroupDuplicationIntegrationTests
{
    [StaFact]
    public void DuplicateSelectionSelectsWholeCloneGroupThroughRealNodifyBindings()
    {
        using var resolver = new ModernDependencyResolver();
        using var restoreResolver = resolver.WithResolver(suppressResolverCallback: true);
        resolver.InitializeSplat();
        var ownsApplication = Application.Current is null;
        var application = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var resources = new ResourceDictionary
        {
            ["ContentBackground"] = Brushes.Black,
            ["WolvenKitYellow"] = Brushes.Yellow,
            ["WolvenKitGreen"] = Brushes.Green,
            ["BooleanToVisibilityConverter"] = new BooleanToVisibilityConverter(),
            ["WolvenKitGridScale"] = 1d
        };
        resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Nodify;component/Themes/Generic.xaml")
        });
        application.Resources.MergedDictionaries.Add(resources);
        var previousNode = NodeSelectionService.Instance.SelectedNode;
        GraphEditorView? view = null;
        using var graph = RedGraph.GenerateQuestGraph("duplication-ui.questphase", new questGraphDefinition(),
            Mock.Of<INodeWrapperFactory>());
        try
        {
            graph.CreateQuestNode(typeof(questLogicalHubNodeDefinition), new Point(20, 40));
            graph.CreateQuestNode(typeof(questLogicalHubNodeDefinition), new Point(220, 80));
            var originals = graph.Nodes.ToArray();
            graph.PendingConnection.Source = originals[0].Output.Single();
            graph.PendingConnection.Target = originals[1].Input.Single(port => port.Name == "In1");
            graph.Connect();
            var comment = new GraphCommentViewModel();
            graph.Comments.Add(comment);
            view = new GraphEditorView { Source = graph };
            view.Editor.GetBindingExpression(ItemsControl.ItemsSourceProperty)!.UpdateTarget();
            Assert.False(view.DuplicateSelectedQuestNodes());
            var selectedItems = view.Editor.SelectedItems!;
            foreach (var original in originals) selectedItems.Add(original);
            selectedItems.Add(comment);

            Assert.True(view.DuplicateSelectedQuestNodes());

            var copies = graph.Nodes.Except(originals).ToArray();
            Assert.Equal(2, copies.Length);
            Assert.Equal(2, graph.Connections.Count);
            Assert.Equal(2, selectedItems.Count);
            Assert.All(copies, copy => Assert.Contains(copy, selectedItems.Cast<object>()));
            Assert.All(originals, original => Assert.DoesNotContain(original, selectedItems.Cast<object>()));
            Assert.DoesNotContain(comment, selectedItems.Cast<object>());
            Assert.Equal(copies.OrderBy(node => node.UniqueId),
                view.SelectedNodes.Cast<NodeViewModel>().OrderBy(node => node.UniqueId));
            Assert.Contains(view.SelectedNode, copies);
            Assert.Contains(NodeSelectionService.Instance.SelectedNode, copies);
            Assert.Equal(new Point(70, 90), copies[0].Location);
            Assert.Equal(new Point(270, 130), copies[1].Location);
            using var scene = new RedGraph("unrelated.scene", new scnSceneResource());
            view.Source = scene;
            Assert.False(view.DuplicateSelectedQuestNodes());
        }
        finally
        {
            if (view is not null)
            {
                view.Editor.SelectedItems?.Clear();
                view.Source = null;
            }
            NodeSelectionService.Instance.SelectedNode = previousNode;
            application.Resources.MergedDictionaries.Remove(resources);
            if (ownsApplication) application.Shutdown();
        }
    }
}
