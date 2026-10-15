using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Newtonsoft.Json.Linq;
using WolvenKit.App.Controllers;
using WolvenKit.App.Factories;
using WolvenKit.App.ViewModels.GraphEditor;
using WolvenKit.App.ViewModels.GraphEditor.Nodes.Quest;
using WolvenKit.App.ViewModels.GraphEditor.Nodes.Quest.Internal;
using WolvenKit.Common;
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
public class QuestGroupDuplicationTests
{
    [TestMethod]
    public void BasicGroupClonesPropertiesPositionsAndInternalConnections()
    {
        var data = GraphTestHelpers.CreateQuestGraph();
        using var graph = Generate(data);
        var facts = Add<questFactsDBManagerNodeDefinition>(graph, 10, 20);
        var pause = Add<questPauseConditionNodeDefinition>(graph, 140, 80);
        var audio = Add<questAudioNodeDefinition>(graph, 320, 50);
        var factData = (questFactsDBManagerNodeDefinition)facts.Data;
        ((questSetVar_NodeType)factData.Type.GetValue()!).FactName = "group_test_fact";
        ((questSetVar_NodeType)factData.Type.GetValue()!).Value = 42;
        ((questPauseConditionNodeDefinition)pause.Data).Condition = new CHandle<questIBaseCondition>(
            new questTriggerCondition { TriggerAreaRef = "#estate_trigger" });
        ((questAudioNodeDefinition)audio.Data).Type = new CHandle<questIAudioNodeType>(
            new questAudioEventNodeType { AmbientUniqueName = "estate_audio", Emitter = "estate_emitter" });
        pause.ShowUnusedSockets = false;
        Connect(graph, facts, "Out", pause, "In");
        Connect(graph, pause, "Out", audio, "In");
        var before = new Snapshot(graph, data);

        var copies = graph.DuplicateQuestNodes(new object[] { audio, facts, pause, facts });

        Assert.AreEqual(3, copies.Count);
        Assert.AreEqual(6, graph.Nodes.Count);
        Assert.AreEqual(4, graph.Connections.Count);
        Assert.AreEqual(6, graph.Nodes.Select(node => node.UniqueId).Distinct().Count());
        AssertCopies(before, graph, data, copies);
        Assert.AreEqual(new Point(60, 70), copies[0].Location);
        Assert.AreEqual(new Point(190, 130), copies[1].Location);
        Assert.AreEqual(new Point(370, 100), copies[2].Location);
        Assert.IsFalse(copies[1].ShowUnusedSockets);
        var copiedFacts = (questFactsDBManagerNodeDefinition)copies[0].Data;
        var copiedValue = (questSetVar_NodeType)copiedFacts.Type.GetValue()!;
        Assert.AreEqual("group_test_fact", copiedValue.FactName.ToString());
        Assert.AreEqual(42, (int)copiedValue.Value);
        Assert.AreNotSame(factData.Type.GetValue(), copiedValue);
        Assert.AreEqual((NodeRef)"#estate_trigger", ((questTriggerCondition)
            ((questPauseConditionNodeDefinition)copies[1].Data).Condition.GetValue()!).TriggerAreaRef);
        copiedValue.Value = 99;
        Assert.AreEqual(42, (int)((questSetVar_NodeType)factData.Type.GetValue()!).Value);
        AssertRoundTrips(data);
    }

    [TestMethod]
    public void BranchingGroupPreservesSocketIdentityCyclesCutsAndParallelEdges()
    {
        var data = GraphTestHelpers.CreateQuestGraph();
        using (var setup = Generate(data))
        {
            Add<questConditionNodeDefinition>(setup);
            Add<questLogicalHubNodeDefinition>(setup);
            var flow = Add<questFlowControlNodeDefinition>(setup);
            Add<questCutControlNodeDefinition>(setup);
            ((questFlowControlNodeDefinition)flow.Data).OpensAt = 7;
            ((questFlowControlNodeDefinition)flow.Data).ClosesAt = 11;
            // Duplicate names deliberately require mapping by socket identity/index.
            ((questSocketDefinition)((questNodeDefinition)setup.Nodes[0].Data).Sockets[2].GetValue()!).Name = "True";
        }
        using var graph = Generate(data);
        var condition = (BaseQuestViewModel)graph.Nodes[0];
        var hub = (BaseQuestViewModel)graph.Nodes[1];
        var flowNode = (BaseQuestViewModel)graph.Nodes[2];
        var cut = (BaseQuestViewModel)graph.Nodes[3];
        Connect(graph, condition.Output[0], hub.Input.Single(port => port.Name == "In1"));
        Connect(graph, condition.Output[1], flowNode.Input.Single(port => port.Name == "Open"));
        var parallel = Connect(graph, hub, "Out1", flowNode, "Switch");
        var definition = GraphTestHelpers.ConnectQuestSockets(((QuestOutputConnectorViewModel)parallel.Source).Data,
            ((QuestInputConnectorViewModel)parallel.Target).Data);
        graph.Connections.Add(new QuestConnectionViewModel(parallel.Source, parallel.Target, definition));
        Connect(graph, hub, "Out1", condition, "In");
        Connect(graph, flowNode, "Out", flowNode, "In");
        Connect(graph, cut, "CutSource", flowNode, "CutDestination");
        Connect(graph, flowNode, "Out", cut, "In");
        var before = new Snapshot(graph, data);

        var copies = graph.DuplicateQuestNodes(graph.Nodes.Cast<object>().ToArray());

        Assert.AreEqual(4, copies.Count);
        Assert.AreEqual(16, graph.Connections.Count);
        AssertCopies(before, graph, data, copies);
        var copiedFlow = (questFlowControlNodeDefinition)copies[2].Data;
        Assert.AreEqual((ushort)7, (ushort)copiedFlow.OpensAt);
        Assert.AreEqual((ushort)11, (ushort)copiedFlow.ClosesAt);
        Assert.AreEqual(2, copies[0].Output.Count(port => port.Name == "True"));
        AssertRoundTrips(data);
    }

    [TestMethod]
    public void SubsetDuplicationOmitsExternalEdgesAndUsesOnlyCurrentGraph()
    {
        var data = GraphTestHelpers.CreateQuestGraph();
        using var graph = Generate(data);
        var start = Add<questStartNodeDefinition>(graph);
        var facts = Add<questFactsDBManagerNodeDefinition>(graph);
        var pause = Add<questPauseConditionNodeDefinition>(graph);
        var end = Add<questEndNodeDefinition>(graph);
        Connect(graph, start, "Out", facts, "In");
        Connect(graph, facts, "Out", pause, "In");
        Connect(graph, pause, "Out", end, "In");
        Connect(graph, facts, "Out", end, "In");
        var before = new Snapshot(graph, data);

        var copies = graph.DuplicateQuestNodes(new object[] { facts, pause, new GraphCommentViewModel() });

        Assert.AreEqual(2, copies.Count);
        Assert.AreEqual(5, graph.Connections.Count);
        AssertCopies(before, graph, data, copies, new NodeViewModel[] { facts, pause });
        Assert.AreEqual(0, copies[0].Input.OfType<QuestInputConnectorViewModel>()
            .Single(port => port.Name == "In").Data.Connections.Count);
        Assert.AreEqual(0, copies[1].Output.OfType<QuestOutputConnectorViewModel>().Single().Data.Connections.Count);
        var copiedNodes = copies.Select(node => node.UniqueId).ToHashSet();
        foreach (var connection in graph.Connections.Skip(before.Connections.Length))
        {
            Assert.IsTrue(copiedNodes.Contains(connection.Source.OwnerId));
            Assert.IsTrue(copiedNodes.Contains(connection.Target.OwnerId));
        }
    }

    [TestMethod]
    public void NestedPhasesAndResourceReferencesSurviveJsonAndBinarySaveReopen()
    {
        var nestedData = GraphTestHelpers.CreateQuestGraph();
        using (var nested = Generate(nestedData))
        {
            var a = Add<questStartNodeDefinition>(nested);
            var b = Add<questPauseConditionNodeDefinition>(nested);
            ((questPauseConditionNodeDefinition)b.Data).Condition = new CHandle<questIBaseCondition>(
                new questTriggerCondition { TriggerAreaRef = "#persistent_trigger" });
            var c = Add<questEndNodeDefinition>(nested);
            Connect(nested, a, "Out", b, "In");
            Connect(nested, b, "Out", c, "In");
        }
        var childData = GraphTestHelpers.CreateQuestGraph();
        using (var child = Generate(childData))
        {
            var a = Add<questStartNodeDefinition>(child);
            var b = Add<questAudioNodeDefinition>(child);
            ((questAudioNodeDefinition)b.Data).Type = new CHandle<questIAudioNodeType>(
                new questAudioEventNodeType { AmbientUniqueName = "persistent_audio", Emitter = "estate" });
            var c = Add<questEndNodeDefinition>(child);
            ((questPhaseNodeDefinition)Add<questPhaseNodeDefinition>(child).Data).PhaseGraph = nestedData;
            Connect(child, a, "Out", b, "In");
            Connect(child, b, "Out", c, "In");
        }
        var data = GraphTestHelpers.CreateQuestGraph();
        using var graph = Generate(data);
        var phase = Add<questPhaseNodeDefinition>(graph);
        ((questPhaseNodeDefinition)phase.Data).PhaseGraph = childData;
        var external = Add<questPhaseNodeDefinition>(graph);
        ((questPhaseNodeDefinition)external.Data).PhaseResource = new("base\\quest\\shared.questphase");
        var facts = Add<questFactsDBManagerNodeDefinition>(graph);
        var factValue = (questSetVar_NodeType)((questFactsDBManagerNodeDefinition)facts.Data).Type.Chunk!;
        factValue.FactName = "persistent_group_fact";
        factValue.Value = 17;
        var marker = Add<questDeletionMarkerNodeDefinition>(graph);
        ((questDeletionMarkerNodeDefinition)marker.Data).DeletedNodeIds.Add((ushort)facts.UniqueId);
        ((questDeletionMarkerNodeDefinition)marker.Data).DeletedNodeIds.Add(999);
        Connect(graph, facts, "Out", phase, "CutDestination");
        var before = new Snapshot(graph, data);

        var copies = graph.DuplicateQuestNodes(graph.Nodes.Cast<object>().ToArray());

        AssertCopies(before, graph, data, copies);
        var copiedPhase = (questPhaseNodeDefinition)copies[0].Data;
        Assert.AreNotSame(childData, copiedPhase.PhaseGraph.GetValue());
        AssertGraphCopy(childData, copiedPhase.PhaseGraph.Chunk!);
        Assert.AreEqual(((questPhaseNodeDefinition)external.Data).PhaseResource,
            ((questPhaseNodeDefinition)copies[1].Data).PhaseResource);
        CollectionAssert.AreEqual(new ushort[] { (ushort)copies[2].UniqueId, 999 },
            ((questDeletionMarkerNodeDefinition)copies[3].Data).DeletedNodeIds.Select(id => (ushort)id).ToArray());
        var childView = ((questPhaseNodeDefinitionWrapper)copies[0]).Graph!;
        Assert.AreEqual(2, childView.Connections.Count);
        Assert.AreEqual(2, childView.Nodes.OfType<questPhaseNodeDefinitionWrapper>().Single().Graph!.Connections.Count);
        AssertRoundTrips(data);
    }

    [TestMethod]
    public void ExternalJournalWeakReferencesKeepTheirTargetButNotMutableWrappers()
    {
        var data = GraphTestHelpers.CreateQuestGraph();
        using var graph = Generate(data);
        var node = Add<questEventManagerNodeDefinition>(graph);
        var entry = new gameJournalQuest();
        var payload = new questMappinVariantChangedEvent { Entry = new CWeakHandle<gameJournalEntry>(entry) };
        ((questEventManagerNodeDefinition)node.Data).Event = new CHandle<IScriptable>(payload);
        var before = new Snapshot(graph, data);

        var copy = graph.DuplicateQuestNodes(new object[] { node }).Single();

        before.AssertOriginalsPreserved(graph, data);
        var copiedPayload = (questMappinVariantChangedEvent)((questEventManagerNodeDefinition)copy.Data).Event.GetValue()!;
        Assert.AreNotSame(payload, copiedPayload);
        Assert.AreNotSame(payload.Entry, copiedPayload.Entry);
        Assert.AreSame(entry, copiedPayload.Entry.GetValue());
        copiedPayload.Entry.SetValue(null);
        Assert.AreSame(entry, payload.Entry.GetValue());
    }

    [TestMethod]
    public void InvalidSelectionsStaleEdgesAndCommitFailuresLeaveNoPartialClones()
    {
        using var document = new GraphDocumentTestFixture("mod\\duplication.questphase");
        var data = GraphTestHelpers.CreateQuestGraph();
        using var graph = Generate(data);
        var a = Add<questFactsDBManagerNodeDefinition>(graph);
        var b = Add<questPauseConditionNodeDefinition>(graph);
        var original = Connect(graph, a, "Out", b, "In");
        graph.DocumentViewModel = document.Document;
        document.Document.SetIsDirty(false);
        var before = new Snapshot(graph, data);
        Assert.AreEqual(0, graph.DuplicateQuestNodes(Array.Empty<object>()).Count);
        using (var foreign = graph.PrepareQuestNode(typeof(questAudioNodeDefinition), new Point()))
        {
            Assert.ThrowsException<InvalidOperationException>(() => graph.DuplicateQuestNodes(new object[] { a, foreign }));
        }
        var factNode = (questFactsDBManagerNodeDefinition)a.Data;
        var originalPayload = factNode.Type;
        var unsupported = (questSetVar_NodeType)originalPayload.Chunk!.DeepCopy();
        unsupported.AddDynamicProperty("unsupportedProperty", typeof(CString));
        unsupported.SetProperty("unsupportedProperty", (CString)"keep_me");
        factNode.Type = new CHandle<questIFactsDBManagerNodeType>(unsupported);
        Assert.ThrowsException<InvalidOperationException>(() => graph.DuplicateQuestNodes(new object[] { a }));
        factNode.Type = originalPayload;
        var pauseNode = (questPauseConditionNodeDefinition)b.Data;
        var originalCondition = pauseNode.Condition;
        var sharedCondition = new questTriggerCondition { TriggerAreaRef = "#shared_condition" };
        var logical = new questLogicalCondition();
        logical.Conditions.Clear();
        logical.Conditions.Add(new CHandle<questIBaseCondition>(sharedCondition));
        logical.Conditions.Add(new CHandle<questIBaseCondition>(sharedCondition));
        pauseNode.Condition = new CHandle<questIBaseCondition>(logical);
        var sharedBefore = new Snapshot(graph, data);
        Assert.ThrowsException<InvalidOperationException>(() => graph.DuplicateQuestNodes(new object[] { b }));
        sharedBefore.AssertUnchanged(graph, data);
        pauseNode.Condition = originalCondition;
        var target = ((QuestInputConnectorViewModel)original.Target).Data;
        var handle = target.Connections.Single();
        target.Connections.Clear();
        Assert.ThrowsException<InvalidOperationException>(() => graph.DuplicateQuestNodes(new object[] { a, b }));
        target.Connections.Add(handle);
        graph.Nodes[1] = a; // A stale visual list must not hide B's ID from allocation.
        Assert.ThrowsException<InvalidOperationException>(() => graph.DuplicateQuestNodes(new object[] { a }));
        graph.Nodes[1] = b;
        before.AssertUnchanged(graph, data);

        foreach (var collection in new INotifyCollectionChanged[] { graph.CanvasItems, graph.Nodes, graph.Connections })
        {
            var failOnce = true;
            NotifyCollectionChangedEventHandler failure = (_, args) =>
            {
                if (failOnce && args.Action == NotifyCollectionChangedAction.Add)
                {
                    failOnce = false;
                    throw new InvalidOperationException("Simulated commit failure.");
                }
            };
            collection.CollectionChanged += failure;
            try
            {
                Assert.ThrowsException<InvalidOperationException>(() => graph.DuplicateQuestNodes(new object[] { a, b }));
                Assert.IsFalse(failOnce, "The injected failure must reach the requested commit stage.");
            }
            finally
            {
                collection.CollectionChanged -= failure;
            }
            before.AssertUnchanged(graph, data);
            Assert.IsFalse(document.Document.IsDirty);
        }
        var copies = graph.DuplicateQuestNodes(new object[] { a, b });
        Assert.AreEqual(3U, copies[0].UniqueId);
        Assert.AreEqual(4U, copies[1].UniqueId);
        Assert.IsTrue(document.Document.IsDirty);

        var cycleData = GraphTestHelpers.CreateQuestGraph();
        using var cycleGraph = Generate(cycleData);
        var phase = Add<questPhaseNodeDefinition>(cycleGraph);
        ((questPhaseNodeDefinition)phase.Data).PhaseGraph = cycleData;
        Assert.ThrowsException<InvalidOperationException>(() => cycleGraph.DuplicateQuestNodes(new object[] { phase }));
        Assert.AreEqual(1, cycleGraph.Nodes.Count);
        Assert.AreSame(cycleData, ((questPhaseNodeDefinition)phase.Data).PhaseGraph.GetValue());

        // Reopening a graph near the ID limit must still find free IDs, without overflow or 65535.
        var highData = GraphTestHelpers.CreateQuestGraph();
        using (var setup = Generate(highData))
        {
            ((questNodeDefinition)Add<questLogicalHubNodeDefinition>(setup).Data).Id = 65534;
            ((questNodeDefinition)Add<questLogicalHubNodeDefinition>(setup).Data).Id = 0;
        }
        using var highGraph = Generate(highData);
        var highCopies = highGraph.DuplicateQuestNodes(highGraph.Nodes.Cast<object>().ToArray());
        CollectionAssert.AreEqual(new uint[] { 1, 2 }, highCopies.Select(node => node.UniqueId).ToArray());
        Assert.AreEqual(4, highGraph.Nodes.Select(node => node.UniqueId).Distinct().Count());
    }

    private static BaseQuestViewModel Add<T>(RedGraph graph, double x = 0, double y = 0) where T : questNodeDefinition
    {
        var id = graph.CreateQuestNode(typeof(T), new Point(x, y));
        return graph.Nodes.OfType<BaseQuestViewModel>().Single(node => node.UniqueId == id);
    }

    private static QuestConnectionViewModel Connect(RedGraph graph, BaseQuestViewModel source, string output,
        BaseQuestViewModel target, string input) => Connect(graph, source.Output.Single(port => port.Name == output),
            target.Input.Single(port => port.Name == input));

    private static QuestConnectionViewModel Connect(RedGraph graph, OutputConnectorViewModel source, InputConnectorViewModel target)
    {
        graph.PendingConnection.Source = source;
        graph.PendingConnection.Target = target;
        graph.Connect();
        return graph.Connections.OfType<QuestConnectionViewModel>().Single(connection =>
            ReferenceEquals(connection.Source, source) && ReferenceEquals(connection.Target, target));
    }

    private static RedGraph Generate(questGraphDefinition data) => RedGraph.GenerateQuestGraph("test.questphase", data,
        new NodeWrapperFactory(Mock.Of<ILoggerService>(), Mock.Of<IArchiveManager>(), Mock.Of<IGameControllerFactory>()));

    private static void AssertCopies(Snapshot before, RedGraph graph, questGraphDefinition data,
        IReadOnlyList<BaseQuestViewModel> copies, IReadOnlyList<NodeViewModel>? originals = null)
    {
        originals ??= before.Nodes;
        Assert.AreEqual(originals.Count, copies.Count);
        before.AssertOriginalsPreserved(graph, data);
        Assert.AreEqual(data.Nodes.Count, graph.Nodes.Count);
        Assert.AreEqual(graph.Nodes.Count, graph.CanvasItems.OfType<NodeViewModel>().Count());
        Assert.AreEqual(graph.Nodes.Count, graph.Nodes.Select(node => node.UniqueId).Distinct().Count());
        var map = copies.Select((node, index) => (node.UniqueId, Original: originals[index].UniqueId))
            .ToDictionary(pair => pair.UniqueId, pair => pair.Original);
        var originalInternal = before.Connections.Where(connection => map.Values.Contains(connection.Source.OwnerId) &&
            map.Values.Contains(connection.Target.OwnerId)).Select(connection => Describe(connection, before.Nodes));
        var newConnections = graph.Connections.OfType<QuestConnectionViewModel>().Except(before.Connections).ToArray();
        CollectionAssert.AreEquivalent(originalInternal.ToArray(), newConnections.Select(connection => Describe(connection,
            copies.Cast<NodeViewModel>().ToArray(), map)).ToArray());
        foreach (var copy in copies)
        {
            Assert.IsFalse(before.Nodes.Any(original => ReferenceEquals(original.Data, copy.Data)));
            Assert.IsTrue(data.Nodes.Any(handle => ReferenceEquals(handle.GetValue(), copy.Data)));
        }
        AssertReciprocal(graph);
    }

    private static string Describe(QuestConnectionViewModel edge, IReadOnlyList<NodeViewModel> nodes,
        IReadOnlyDictionary<uint, uint>? remap = null)
    {
        var source = nodes.Single(node => node.Output.Contains(edge.Source));
        var target = nodes.Single(node => node.Input.Contains(edge.Target));
        var sourceId = remap is null ? source.UniqueId : remap[source.UniqueId];
        var targetId = remap is null ? target.UniqueId : remap[target.UniqueId];
        return $"{sourceId}:{((questNodeDefinition)source.Data).Sockets.ToList().FindIndex(handle => ReferenceEquals(handle.GetValue(), ((QuestOutputConnectorViewModel)edge.Source).Data))}" +
            $"->{targetId}:{((questNodeDefinition)target.Data).Sockets.ToList().FindIndex(handle => ReferenceEquals(handle.GetValue(), ((QuestInputConnectorViewModel)edge.Target).Data))}";
    }

    private static void AssertReciprocal(RedGraph graph)
    {
        foreach (var edge in graph.Connections.OfType<QuestConnectionViewModel>())
        {
            var source = ((QuestOutputConnectorViewModel)edge.Source).Data;
            var target = ((QuestInputConnectorViewModel)edge.Target).Data;
            Assert.AreSame(source, edge.ConnectionDefinition.Source.GetValue());
            Assert.AreSame(target, edge.ConnectionDefinition.Destination.GetValue());
            Assert.AreEqual(1, source.Connections.Count(handle => ReferenceEquals(handle.GetValue(), edge.ConnectionDefinition)));
            Assert.AreEqual(1, target.Connections.Count(handle => ReferenceEquals(handle.GetValue(), edge.ConnectionDefinition)));
            Assert.IsTrue(edge.Source.IsConnected);
            Assert.IsTrue(edge.Target.IsConnected);
        }
    }

    private static void AssertGraphCopy(questGraphDefinition expected, questGraphDefinition actual)
    {
        using var expectedView = Generate(expected);
        using var actualView = Generate(actual);
        CollectionAssert.AreEqual(expectedView.Nodes.Select(node => node.UniqueId).ToArray(), actualView.Nodes.Select(node => node.UniqueId).ToArray());
        CollectionAssert.AreEquivalent(expectedView.Connections.OfType<QuestConnectionViewModel>().Select(edge => Describe(edge, expectedView.Nodes)).ToArray(),
            actualView.Connections.OfType<QuestConnectionViewModel>().Select(edge => Describe(edge, actualView.Nodes)).ToArray());
        AssertReciprocal(actualView);
        for (var i = 0; i < expected.Nodes.Count; i++)
        {
            var original = expected.Nodes[i].GetValue();
            var copy = actual.Nodes[i].GetValue();
            Assert.AreEqual(original!.GetType(), copy!.GetType());
            CollectionAssert.AreEqual(((questNodeDefinition)original).Sockets.Select(handle =>
                    ((questSocketDefinition)handle.GetValue()!).Name).ToArray(),
                ((questNodeDefinition)copy).Sockets.Select(handle => ((questSocketDefinition)handle.GetValue()!).Name).ToArray());
            CollectionAssert.AreEqual(((questNodeDefinition)original).Sockets.Select(handle =>
                    ((questSocketDefinition)handle.GetValue()!).Type).ToArray(),
                ((questNodeDefinition)copy).Sockets.Select(handle => ((questSocketDefinition)handle.GetValue()!).Type).ToArray());
            switch (original)
            {
                case questFactsDBManagerNodeDefinition facts:
                    var expectedFact = (questSetVar_NodeType)facts.Type.GetValue()!;
                    var actualFact = (questSetVar_NodeType)((questFactsDBManagerNodeDefinition)copy).Type.GetValue()!;
                    Assert.AreEqual(expectedFact.FactName, actualFact.FactName);
                    Assert.AreEqual(expectedFact.Value, actualFact.Value);
                    Assert.AreEqual(expectedFact.SetExactValue, actualFact.SetExactValue);
                    break;
                case questFlowControlNodeDefinition flow:
                    Assert.AreEqual(flow.OpensAt, ((questFlowControlNodeDefinition)copy).OpensAt);
                    Assert.AreEqual(flow.ClosesAt, ((questFlowControlNodeDefinition)copy).ClosesAt);
                    break;
                case questPhaseNodeDefinition phase:
                    Assert.AreEqual(phase.PhaseResource, ((questPhaseNodeDefinition)copy).PhaseResource);
                    break;
                case questDeletionMarkerNodeDefinition marker:
                    CollectionAssert.AreEqual(marker.DeletedNodeIds.ToArray(),
                        ((questDeletionMarkerNodeDefinition)copy).DeletedNodeIds.ToArray());
                    break;
                case questAudioNodeDefinition { Type.Chunk: questAudioEventNodeType audio }:
                    var actualAudio = (questAudioEventNodeType)((questAudioNodeDefinition)copy).Type.Chunk!;
                    Assert.AreEqual(audio.AmbientUniqueName, actualAudio.AmbientUniqueName);
                    Assert.AreEqual(audio.Emitter, actualAudio.Emitter);
                    break;
                case questPauseConditionNodeDefinition { Condition.Chunk: questTriggerCondition condition }:
                    var actualCondition = (questTriggerCondition)((questPauseConditionNodeDefinition)copy).Condition.Chunk!;
                    Assert.AreEqual(condition.TriggerAreaRef, actualCondition.TriggerAreaRef);
                    Assert.AreEqual(condition.IsPlayerActivator, actualCondition.IsPlayerActivator);
                    break;
            }
            if (original is questPhaseNodeDefinition { PhaseGraph.Chunk: { } child })
            {
                AssertGraphCopy(child, ((questPhaseNodeDefinition)copy).PhaseGraph.Chunk!);
            }
        }
    }

    private static string Serialize(questGraphDefinition data)
    {
        using var file = new CR2WFile { RootChunk = new questQuestPhaseResource { Graph = data } };
        var json = JObject.Parse(RedJsonSerializer.Serialize(new RedFileDto(file)));
        // Export timestamps change on every save and are not graph data.
        ((JObject)json["Header"]!).Remove("ExportedDateTime");
        return json.ToString();
    }

    private static void AssertRoundTrips(questGraphDefinition data)
    {
        var dto = RedJsonSerializer.Deserialize<RedFileDto>(Serialize(data))!;
        using var jsonFile = dto.Data!;
        AssertGraphCopy(data, (questGraphDefinition)((questQuestPhaseResource)jsonFile.RootChunk).Graph.GetValue()!);
        using var original = new CR2WFile { RootChunk = new questQuestPhaseResource { Graph = data } };
        using var stream = new MemoryStream();
        using (var writer = new CR2WWriter(stream, Encoding.UTF8, true))
        {
            writer.WriteFile(original);
        }
        stream.Position = 0;
        using var reader = new CR2WReader(stream, Encoding.UTF8, true);
        Assert.AreEqual(WolvenKit.RED4.Archive.IO.EFileReadErrorCodes.NoError, reader.ReadFile(out var binaryFile));
        using (binaryFile)
        {
            AssertGraphCopy(data, (questGraphDefinition)((questQuestPhaseResource)binaryFile!.RootChunk).Graph.GetValue()!);
        }
    }

    private sealed class Snapshot
    {
        private readonly CHandle<graphGraphNodeDefinition>[] _handles;
        private readonly Dictionary<questSocketDefinition, CHandle<graphGraphConnectionDefinition>[]> _sockets;
        private readonly string _json;
        public readonly NodeViewModel[] Nodes;
        public readonly QuestConnectionViewModel[] Connections;

        public Snapshot(RedGraph graph, questGraphDefinition data)
        {
            _handles = data.Nodes.ToArray();
            Nodes = graph.Nodes.ToArray();
            Connections = graph.Connections.OfType<QuestConnectionViewModel>().ToArray();
            _sockets = data.Nodes.SelectMany(handle => ((graphGraphNodeDefinition)handle.GetValue()!).Sockets)
                .Select(handle => (questSocketDefinition)handle.GetValue()!)
                .ToDictionary(socket => socket, socket => socket.Connections.ToArray(),
                    (IEqualityComparer<questSocketDefinition>)ReferenceEqualityComparer.Instance);
            _json = Serialize(data);
        }

        public void AssertOriginalsPreserved(RedGraph graph, questGraphDefinition data)
        {
            for (var i = 0; i < Nodes.Length; i++)
            {
                Assert.AreSame(Nodes[i], graph.Nodes[i]);
                Assert.AreSame(_handles[i], data.Nodes[i]);
            }
            foreach (var (socket, handles) in _sockets)
            {
                Assert.AreEqual(handles.Length, socket.Connections.Count);
                for (var i = 0; i < handles.Length; i++) Assert.AreSame(handles[i], socket.Connections[i]);
            }
            foreach (var edge in Connections) Assert.IsTrue(graph.Connections.Contains(edge));
            Assert.AreEqual(_json, Serialize(GraphTestHelpers.CreateQuestGraph(_handles.Select(handle =>
                (graphGraphNodeDefinition)handle.GetValue()!).ToArray())));
        }

        public void AssertUnchanged(RedGraph graph, questGraphDefinition data)
        {
            Assert.AreEqual(Nodes.Length, graph.Nodes.Count);
            Assert.AreEqual(Nodes.Length, data.Nodes.Count);
            Assert.AreEqual(Nodes.Length, graph.CanvasItems.Count);
            Assert.AreEqual(Connections.Length, graph.Connections.Count);
            AssertOriginalsPreserved(graph, data);
        }
    }
}
