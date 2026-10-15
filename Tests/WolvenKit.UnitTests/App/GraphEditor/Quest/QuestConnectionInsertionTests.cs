using System;
using System.Collections.Specialized;
using System.IO;
using System.Text;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WolvenKit.App.Factories;
using WolvenKit.App.ViewModels.GraphEditor;
using WolvenKit.App.ViewModels.GraphEditor.Nodes.Quest;
using WolvenKit.App.ViewModels.GraphEditor.Nodes.Quest.Internal;
using WolvenKit.Common.Conversion;
using WolvenKit.Core.Interfaces;
using WolvenKit.RED4.Archive.CR2W;
using WolvenKit.RED4.Archive.IO;
using WolvenKit.RED4.CR2W.JSON;
using WolvenKit.RED4.Types;
using WolvenKit.UnitTests.App.GraphEditor.Shared;
using Point = System.Windows.Point;

namespace WolvenKit.UnitTests.App.GraphEditor.Quest;

[TestClass]
[DoNotParallelize]
public class QuestConnectionInsertionTests
{
    [TestMethod]
    public void InsertOrdinaryNodesPreservesEndpointsAndSurvivesSaveReopen()
    {
        foreach (var type in new[] { typeof(questFactsDBManagerNodeDefinition),
                     typeof(questPauseConditionNodeDefinition), typeof(questLogicalHubNodeDefinition) })
        {
            var data = GraphTestHelpers.CreateQuestGraph();
            using var graph = GenerateGraph(data);
            var source = AddNode(graph, typeof(questStartNodeDefinition));
            var target = AddNode(graph, typeof(questEndNodeDefinition));
            var original = Connect(graph, source, "Out", target, "In");
            var candidate = graph.PrepareQuestNode(type, new Point(120, 80));

            Assert.IsTrue(graph.TryInsertQuestNode(original, candidate), type.Name);

            Assert.AreEqual(new Point(120, 80), candidate.Location);
            Assert.AreEqual(3U, candidate.UniqueId);
            Assert.AreEqual(3, graph.Nodes.Count);
            Assert.AreEqual(3, graph.CanvasItems.Count);
            Assert.AreEqual(3, data.Nodes.Count);
            Assert.AreEqual(2, graph.Connections.Count);
            Assert.AreSame(candidate.Data, data.Nodes.Last().GetValue());
            AssertReplacement(graph, original, candidate);
            AssertSavedGraph(data, graph);
        }
    }

    [TestMethod]
    public void InsertWithExplicitSocketsPreservesBranchesAndOtherConnections()
    {
        foreach (var (type, inputName, outputName) in new[]
                 {
                     (typeof(questFlowControlNodeDefinition), "Open", "Out"),
                     (typeof(questConditionNodeDefinition), "In", "True"),
                     (typeof(questLogicalAndNodeDefinition), "In2", "Out1")
                 })
        {
            var data = GraphTestHelpers.CreateQuestGraph();
            using var graph = GenerateGraph(data);
            var source = AddNode(graph, typeof(questConditionNodeDefinition));
            var target = AddNode(graph, typeof(questFlowControlNodeDefinition));
            var end = AddNode(graph, typeof(questEndNodeDefinition));
            var start = AddNode(graph, typeof(questStartNodeDefinition));
            var original = Connect(graph, source, "True", target, "Switch");
            var untouched = new[] { Connect(graph, source, "False", end, "In"),
                Connect(graph, source, "True", end, "In"), Connect(graph, start, "Out", target, "Switch") };
            var candidate = graph.PrepareQuestNode(type, new Point(60, 40));
            var input = candidate.Input.OfType<QuestInputConnectorViewModel>().Single(port => port.Name == inputName);
            var output = candidate.Output.OfType<QuestOutputConnectorViewModel>().Single(port => port.Name == outputName);

            Assert.IsTrue(graph.TryInsertQuestNode(original, candidate, input, output), type.Name);

            Assert.AreEqual(5, graph.Connections.Count);
            Assert.AreEqual(5, data.Nodes.Count);
            AssertReplacement(graph, original, candidate);
            foreach (var connection in untouched)
            {
                Assert.IsTrue(graph.Connections.Contains(connection));
                AssertReciprocal(connection);
            }
            Assert.AreEqual(1, input.Data.Connections.Count);
            Assert.AreEqual(1, output.Data.Connections.Count);
            Assert.IsTrue(((questNodeDefinition)candidate.Data).Sockets.Select(handle => handle.GetValue())
                .OfType<questSocketDefinition>().Except(new[] { input.Data, output.Data })
                .All(socket => socket.Connections.Count == 0));
            AssertSavedGraph(data, graph);
        }
    }

    [TestMethod]
    public void RejectedOrCancelledInsertionLeavesGraphUnchanged()
    {
        using var fixture = new GraphDocumentTestFixture("mod\\insertion.questphase");
        var data = GraphTestHelpers.CreateQuestGraph();
        using var graph = GenerateGraph(data);
        var source = AddNode(graph, typeof(questStartNodeDefinition));
        var target = AddNode(graph, typeof(questEndNodeDefinition));
        var original = Connect(graph, source, "Out", target, "In");
        graph.DocumentViewModel = fixture.Document;
        fixture.Document.SetIsDirty(false);

        foreach (var type in new[] { typeof(questStartNodeDefinition), typeof(questEndNodeDefinition),
                     typeof(questInputNodeDefinition), typeof(questOutputNodeDefinition),
                     typeof(questConditionNodeDefinition) })
        {
            using var candidate = graph.PrepareQuestNode(type, new Point());
            Assert.IsFalse(graph.TryInsertQuestNode(original, candidate), type.Name);
            AssertUnchanged();
        }
        using (var candidate = graph.PrepareQuestNode(typeof(questCutControlNodeDefinition), new Point()))
        {
            var input = candidate.Input.OfType<QuestInputConnectorViewModel>().Single(port => port.Name == "In");
            var cutOutput = candidate.Output.OfType<QuestOutputConnectorViewModel>().Single(port => port.Name == "CutSource");
            Assert.IsFalse(graph.TryInsertQuestNode(original, candidate, input, cutOutput));
            Assert.IsFalse(graph.TryInsertQuestNode(original, candidate, (QuestInputConnectorViewModel)original.Target));
            var stale = new QuestConnectionViewModel(original.Source, original.Target, original.ConnectionDefinition);
            Assert.IsFalse(graph.TryInsertQuestNode(stale, candidate));
            AssertUnchanged();
        }
        using (var cancelled = graph.PrepareQuestNode(typeof(questPauseConditionNodeDefinition), new Point()))
        {
            AssertUnchanged();
        }
        // Fail after both replacement edges exist and the original edge has been removed.
        NotifyCollectionChangedEventHandler failCommit = (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Remove && args.OldItems?.Contains(original) == true)
            {
                throw new InvalidOperationException("Simulated connection collection callback failure.");
            }
        };
        graph.Connections.CollectionChanged += failCommit;
        try
        {
            using var failed = graph.PrepareQuestNode(typeof(questFactsDBManagerNodeDefinition), new Point());
            Assert.IsFalse(graph.TryInsertQuestNode(original, failed));
            AssertUnchanged();
        }
        finally
        {
            graph.Connections.CollectionChanged -= failCommit;
        }
        var inserted = graph.PrepareQuestNode(typeof(questFactsDBManagerNodeDefinition), new Point());
        Assert.AreEqual(3U, inserted.UniqueId, "Rejected and cancelled candidates must not consume node IDs.");
        Assert.IsTrue(graph.TryInsertQuestNode(original, inserted));
        Assert.IsTrue(fixture.Document.IsDirty);

        void AssertUnchanged()
        {
            Assert.AreEqual(2, graph.Nodes.Count);
            Assert.AreEqual(2, graph.CanvasItems.Count);
            Assert.AreEqual(2, data.Nodes.Count);
            Assert.AreSame(original, graph.Connections.Single());
            AssertReciprocal(original);
            Assert.AreEqual(1, ((QuestOutputConnectorViewModel)original.Source).Data.Connections.Count);
            Assert.AreEqual(1, ((QuestInputConnectorViewModel)original.Target).Data.Connections.Count);
            Assert.IsFalse(fixture.Document.IsDirty);
        }
    }

    private static BaseQuestViewModel AddNode(RedGraph graph, Type type)
    {
        var id = graph.CreateQuestNode(type, new Point());
        return graph.Nodes.OfType<BaseQuestViewModel>().Single(node => node.UniqueId == id);
    }

    private static QuestConnectionViewModel Connect(RedGraph graph, BaseQuestViewModel source, string output,
        BaseQuestViewModel target, string input)
    {
        graph.PendingConnection.Source = source.Output.Single(port => port.Name == output);
        graph.PendingConnection.Target = target.Input.Single(port => port.Name == input);
        graph.Connect();
        return graph.Connections.OfType<QuestConnectionViewModel>().Single(connection =>
            ReferenceEquals(connection.Source, graph.PendingConnection.Source) &&
            ReferenceEquals(connection.Target, graph.PendingConnection.Target));
    }

    private static void AssertReplacement(RedGraph graph, QuestConnectionViewModel original, BaseQuestViewModel inserted)
    {
        Assert.IsFalse(graph.Connections.Contains(original));
        Assert.IsFalse(graph.Nodes.Select(node => (questNodeDefinition)node.Data)
            .SelectMany(node => node.Sockets).Select(handle => handle.GetValue()).OfType<questSocketDefinition>()
            .SelectMany(socket => socket.Connections)
            .Any(handle => ReferenceEquals(handle.GetValue(), original.ConnectionDefinition)));
        var incoming = graph.Connections.OfType<QuestConnectionViewModel>()
            .Single(connection => inserted.Input.Contains(connection.Target));
        var outgoing = graph.Connections.OfType<QuestConnectionViewModel>()
            .Single(connection => inserted.Output.Contains(connection.Source));
        Assert.AreSame(original.Source, incoming.Source);
        Assert.AreSame(original.Target, outgoing.Target);
        foreach (var connection in graph.Connections.OfType<QuestConnectionViewModel>())
        {
            AssertReciprocal(connection);
        }
    }

    private static void AssertReciprocal(QuestConnectionViewModel connection)
    {
        var source = ((QuestOutputConnectorViewModel)connection.Source).Data;
        var target = ((QuestInputConnectorViewModel)connection.Target).Data;
        Assert.AreSame(source, connection.ConnectionDefinition.Source.GetValue());
        Assert.AreSame(target, connection.ConnectionDefinition.Destination.GetValue());
        Assert.AreEqual(1, source.Connections.Count(handle => ReferenceEquals(handle.GetValue(), connection.ConnectionDefinition)));
        Assert.AreEqual(1, target.Connections.Count(handle => ReferenceEquals(handle.GetValue(), connection.ConnectionDefinition)));
        Assert.IsTrue(connection.Source.IsConnected);
        Assert.IsTrue(connection.Target.IsConnected);
    }

    private static void AssertSavedGraph(questGraphDefinition data, RedGraph graph)
    {
        using var file = new CR2WFile { RootChunk = new questQuestPhaseResource
            { Graph = new CHandle<graphGraphDefinition>(data) } };
        var json = RedJsonSerializer.Serialize(new RedFileDto(file));
        var dto = RedJsonSerializer.Deserialize<RedFileDto>(json)!;
        using var restoredFile = dto.Data!;
        AssertRestoredGraph(restoredFile, graph);

        using var stream = new MemoryStream();
        using (var writer = new CR2WWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.WriteFile(file);
        }
        stream.Position = 0;
        using var reader = new CR2WReader(stream, Encoding.UTF8, leaveOpen: true);
        Assert.AreEqual(EFileReadErrorCodes.NoError, reader.ReadFile(out var binaryFile));
        using (binaryFile)
        {
            AssertRestoredGraph(binaryFile!, graph);
        }
    }

    private static void AssertRestoredGraph(CR2WFile file, RedGraph graph)
    {
        var restoredData = (questGraphDefinition)((questQuestPhaseResource)file.RootChunk).Graph.GetValue()!;
        using var restored = GenerateGraph(restoredData);
        CollectionAssert.AreEqual(graph.Nodes.Select(node => node.UniqueId).ToArray(),
            restored.Nodes.Select(node => node.UniqueId).ToArray());
        CollectionAssert.AreEquivalent(graph.Nodes.Select(node => node.Data.GetType()).ToArray(),
            restored.Nodes.Select(node => node.Data.GetType()).ToArray());
        CollectionAssert.AreEquivalent(graph.Connections.OfType<QuestConnectionViewModel>().Select(Describe).ToArray(),
            restored.Connections.OfType<QuestConnectionViewModel>().Select(Describe).ToArray());
        Assert.AreEqual(graph.Nodes.Count, restoredData.Nodes.Count);
        foreach (var connection in restored.Connections.OfType<QuestConnectionViewModel>())
        {
            AssertReciprocal(connection);
        }
    }

    private static string Describe(QuestConnectionViewModel connection) =>
        $"{connection.Source.OwnerId}:{connection.Source.Name}->{connection.Target.OwnerId}:{connection.Target.Name}";

    private static RedGraph GenerateGraph(questGraphDefinition data) =>
        RedGraph.GenerateQuestGraph("test.questphase", data, Mock.Of<INodeWrapperFactory>());
}
