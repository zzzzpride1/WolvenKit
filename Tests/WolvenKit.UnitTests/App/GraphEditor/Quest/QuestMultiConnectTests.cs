using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Newtonsoft.Json.Linq;
using WolvenKit.App.Controllers;
using WolvenKit.App.Factories;
using WolvenKit.App.Services;
using WolvenKit.App.ViewModels.GraphEditor;
using WolvenKit.App.ViewModels.GraphEditor.Nodes;
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
public class QuestMultiConnectTests
{
    [TestMethod]
    public void OneOutputConnectsToSeveralNodesWithReciprocalBackingEdges()
    {
        using var f = new Fixture();
        var source = Out(f.Add<questFactsDBManagerNodeDefinition>());
        var b = In(f.Add<questPauseConditionNodeDefinition>());
        var c = In(f.Add<questConditionNodeDefinition>());
        var d = In(f.Add<questLogicalHubNodeDefinition>(), "In1");
        f.Clean();
        var before = new Snapshot(f);
        Assert.IsTrue(f.Graph.CanStartQuestMultiConnect(source, out var reason), reason);
        foreach (var target in new[] { b, c, d })
            Assert.IsTrue(f.Graph.CanConnectQuestFlow(source, target, out reason), reason);
        Assert.IsTrue(f.Graph.CanConnectMultipleQuestNodes(source, new[] { b, c, d }, out reason), reason);
        before.AssertUnchanged(f);

        Connect(f, source, b, c, d);

        CollectionAssert.AreEquivalent(new[] { "1:Out->2:In", "1:Out->3:In", "1:Out->4:In1" },
            NamedTopology(f.Graph));
        Assert.AreEqual(3, source.Data.Connections.Count);
        Assert.IsTrue(f.Document.Document.IsDirty);
        AssertIntegrity(f);
    }

    [TestMethod]
    public void RepeatedTargetsAndExistingParallelPairsAreNeverToggledOrReplaced()
    {
        using var f = new Fixture();
        var source = Out(f.Add<questConditionNodeDefinition>(), "True");
        var bNode = f.Add<questPauseConditionNodeDefinition>();
        var b = In(bNode);
        var cNode = f.Add<questLogicalHubNodeDefinition>();
        var c = In(cNode, "In1");
        var cAlias = new QuestInputConnectorViewModel(c.Name, c.Title, c.OwnerId, c.Data);
        cNode.Input.Add(cAlias);
        var d = In(f.Add<questJournalNodeDefinition>(), "Active");
        var original = f.Edge(source, b, sharedHandle: true,
            definition: new gamestateMachineTransitionDefinition { Priority = 3.25f });
        original.IsSelected = true;
        original.PathType = 1;
        f.Edge(source, b);
        var alias = new QuestInputConnectorViewModel(b.Name, b.Title, b.OwnerId, b.Data) { IsConnected = true };
        bNode.Input.Add(alias);
        var originals = new Snapshot(f);
        f.Clean();

        Connect(f, source, b, c, cAlias, alias, b, c);
        Assert.AreEqual(3, f.Graph.Connections.Count, "B, C, B adds only C; both existing parallel B edges survive.");
        originals.AssertExistingPreserved(f);
        var beforeD = new Snapshot(f);

        Connect(f, source, d, c);
        Assert.AreEqual(4, f.Graph.Connections.Count, "D, C adds only D.");
        beforeD.AssertExistingPreserved(f);
        f.Clean();
        var beforeNoOp = new Snapshot(f);

        Connect(f, source, c, b, alias, d, c);
        beforeNoOp.AssertUnchanged(f);
        AssertIntegrity(f);
    }

    [TestMethod]
    public void ExplicitNamedInputsPreserveOtherBranchesAndOccupiedInputs()
    {
        using var f = new Fixture();
        var condition = f.Add<questConditionNodeDefinition>();
        var source = Out(condition, "True");
        var otherSource = Out(f.Add<questPauseConditionNodeDefinition>());
        var flow = f.Add<questFlowControlNodeDefinition>();
        var journal = f.Add<questJournalNodeDefinition>();
        var and = f.Add<questLogicalAndNodeDefinition>();
        var xor = f.Add<questLogicalXorNodeDefinition>();
        f.Edge(Out(condition, "False"), In(flow, "Open"));
        f.Edge(otherSource, In(flow, "Switch"));
        var custom1 = f.AddInput(and, "custom:branch");
        var custom2 = f.AddInput(and, "custom:branch");
        var before = new Snapshot(f);

        Connect(f, source, In(flow, "Switch"), In(journal, "Succeeded"), In(and, "In2"),
            In(xor, "In2"), custom1, custom2);

        before.AssertExistingPreserved(f);
        Assert.AreEqual(8, f.Graph.Connections.Count);
        Assert.AreEqual(2, In(flow, "Switch").Data.Connections.Count);
        Assert.AreEqual(0, In(and, "In1").Data.Connections.Count);
        Assert.AreEqual(0, In(xor, "In1").Data.Connections.Count);
        foreach (var port in new[] { custom1, custom2 })
            Assert.AreEqual(1, port.Data.Connections.Count, "Different sockets with equal names are different targets.");
        AssertIntegrity(f);
    }

    [TestMethod]
    public void RepresentativeNodesResolveOnlyUnambiguousRealInputs()
    {
        var cases = new (Type Type, string? Input, string? Output)[]
        {
            (typeof(questFactsDBManagerNodeDefinition), "In", "Out"),
            (typeof(questPauseConditionNodeDefinition), "In", "Out"),
            (typeof(questConditionNodeDefinition), "In", "True"),
            (typeof(questLogicalHubNodeDefinition), "In1", "Out1"),
            (typeof(questLogicalAndNodeDefinition), null, "Out1"),
            (typeof(questLogicalXorNodeDefinition), null, "Out1"),
            (typeof(questFlowControlNodeDefinition), null, "Out"),
            (typeof(questJournalNodeDefinition), null, "Out"),
            (typeof(questEventManagerNodeDefinition), "In", "Out"),
            (typeof(questInputNodeDefinition), null, "Out"),
            (typeof(questOutputNodeDefinition), "In", null),
            (typeof(questUseWorkspotNodeDefinition), "In", "Success")
        };
        foreach (var (type, input, output) in cases)
        {
            using var f = new Fixture();
            var root = f.Add<questFactsDBManagerNodeDefinition>();
            var source = Out(root);
            var node = f.Add(type);
            var end = In(f.Add<questPauseConditionNodeDefinition>());
            f.Clean();
            var before = new Snapshot(f);
            var automatic = f.Graph.GetQuestMultiConnectTarget(source, node, out var reason);
            if (input is null)
            {
                Assert.IsNull(automatic, type.Name);
                Assert.IsFalse(string.IsNullOrWhiteSpace(reason), type.Name);
            }
            else Assert.AreSame(In(node, input), automatic, type.Name);
            Assert.IsNull(f.Graph.GetQuestMultiConnectTarget(source, root, out _), "Whole-node hover skips its own source.");
            Assert.IsNull(f.Graph.GetQuestMultiConnectTarget(source, null, out _));
            before.AssertUnchanged(f);
            if (output is not null)
            {
                var nodeSource = Out(node, output);
                Assert.IsTrue(f.Graph.CanStartQuestMultiConnect(nodeSource, out reason), $"{type.Name}: {reason}");
                Connect(f, nodeSource, end);
            }
            if (input is not null) Connect(f, source, In(node, input));
            if (type == typeof(questInputNodeDefinition))
                Assert.IsFalse(f.Graph.CanConnectQuestFlow(source, node.Input.Single(port => port.Name == "In1"), out _));
            AssertIntegrity(f);
        }
        using var self = new Fixture();
        var selfNode = self.Add<questFactsDBManagerNodeDefinition>();
        Connect(self, Out(selfNode), In(selfNode)); // Explicit ordinary self-links retain normal quest semantics.
        AssertIntegrity(self);
    }

    [TestMethod]
    public void AnyInvalidTargetRejectsTheWholeBatchWithoutMutatingEitherScope()
    {
        using var f = new Fixture();
        using var child = new Fixture();
        using var foreign = new Fixture();
        var source = Out(f.Add<questFactsDBManagerNodeDefinition>());
        var good = In(f.Add<questPauseConditionNodeDefinition>());
        var alternate = f.Add<questFlowControlNodeDefinition>();
        var cut = f.Add<questCutControlNodeDefinition>();
        var boundaryIn = f.Add<questInputNodeDefinition>();
        var boundaryOut = f.Add<questOutputNodeDefinition>();
        var phase = f.Add<questPhaseNodeDefinition>();
        var childNode = child.Add<questPauseConditionNodeDefinition>();
        ((questPhaseNodeDefinition)phase.Data).PhaseGraph = child.Data;
        var foreignNode = foreign.Add<questPauseConditionNodeDefinition>();
        using var detached = f.Graph.PrepareQuestNode(typeof(questPauseConditionNodeDefinition), new Point());
        var invalidTargets = new BaseConnectorViewModel?[]
        {
            null, Out(alternate), In(cut, "CutDestination"), In(boundaryIn, "In1"), In(childNode),
            In(foreignNode), In(detached), f.AddInput(cut, "undefined", Enums.questSocketType.Undefined)
        };
        f.Clean();
        child.Clean();
        foreign.Clean();
        var childBefore = new Snapshot(child);
        var foreignBefore = new Snapshot(foreign);
        foreach (var invalid in invalidTargets)
        {
            Assert.IsFalse(f.Graph.CanConnectQuestFlow(source, invalid, out var reason));
            Assert.IsFalse(string.IsNullOrWhiteSpace(reason));
            Reject(f, source, new BaseConnectorViewModel?[] { good, invalid, In(alternate, "Switch") });
            childBefore.AssertUnchanged(child);
            foreignBefore.AssertUnchanged(foreign);
        }
        foreach (var invalidSource in new BaseConnectorViewModel?[]
                 {
                     null, good, Out(cut, "CutSource"),
                     boundaryOut.Output.OfType<QuestOutputConnectorViewModel>().Single(port => port.Data.Type == Enums.questSocketType.Undefined),
                     f.AddOutput(cut, "undefined", Enums.questSocketType.Undefined),
                     Out(foreignNode), Out(detached)
                 })
        {
            Assert.IsFalse(f.Graph.CanStartQuestMultiConnect(invalidSource, out var reason));
            Assert.IsFalse(string.IsNullOrWhiteSpace(reason));
            Reject(f, invalidSource, new[] { good });
        }
        Reject(f, source, null);
        Reject(f, source, ThrowingTargets(good));
        Assert.IsNull(f.Graph.GetQuestMultiConnectTarget(source, childNode, out _));
        using var scene = new RedGraph("different.scene", new scnSceneResource());
        Assert.IsFalse(scene.CanStartQuestMultiConnect(source, out _));
    }

    [TestMethod]
    public void StalePortsAndMalformedExistingPairsCannotBecomeCleanNoOps()
    {
        foreach (var scenario in new[]
                 {
                     "source-visual-node", "source-backing-node", "source-socket", "target-visual-node",
                     "target-backing-node", "target-socket", "missing-source-handle", "missing-target-handle",
                     "missing-visual-edge", "stale-definition", "unbacked-target"
                 })
        {
            using var f = new Fixture();
            var sourceNode = f.Add<questFactsDBManagerNodeDefinition>();
            var source = Out(sourceNode);
            var targetNode = f.Add<questPauseConditionNodeDefinition>();
            var target = In(targetNode);
            var fresh = In(f.Add<questLogicalHubNodeDefinition>(), "In1");
            var edge = f.Edge(source, target);
            switch (scenario)
            {
                case "source-visual-node": f.Graph.Nodes.Remove(sourceNode); break;
                case "source-backing-node": f.Data.Nodes.RemoveAt(0); break;
                case "source-socket": sourceNode.Output.Remove(source); break;
                case "target-visual-node": f.Graph.Nodes.Remove(targetNode); break;
                case "target-backing-node": f.Data.Nodes.RemoveAt(1); break;
                case "target-socket": targetNode.Input.Remove(target); break;
                case "missing-source-handle": source.Data.Connections.Clear(); break;
                case "missing-target-handle": target.Data.Connections.Clear(); break;
                case "missing-visual-edge": f.Graph.Connections.Remove(edge); break;
                case "stale-definition": edge.ConnectionDefinition.Destination = new CWeakHandle<graphGraphSocketDefinition>(fresh.Data); break;
                case "unbacked-target":
                    target = new QuestInputConnectorViewModel("In", "In", targetNode.UniqueId,
                        new questSocketDefinition { Name = "In", Type = Enums.questSocketType.Input });
                    targetNode.Input.Add(target);
                    break;
            }
            f.Clean();
            Assert.IsFalse(f.Graph.CanConnectQuestFlow(source, target, out var reason), scenario);
            Assert.IsFalse(string.IsNullOrWhiteSpace(reason), scenario);
            Reject(f, source, new[] { fresh, target });
        }
    }

    [TestMethod]
    public void EmptyAndExistingBatchesAreCleanWhileCapacityCountsOnlyNewDistinctPairs()
    {
        using var f = new Fixture();
        var source = Out(f.Add<questFactsDBManagerNodeDefinition>());
        var b = In(f.Add<questPauseConditionNodeDefinition>());
        var c = In(f.Add<questConditionNodeDefinition>());
        var d = In(f.Add<questEventManagerNodeDefinition>());
        f.Edge(source, b, sharedHandle: true);
        f.Edge(source, b);
        source.Data.Connections.MaxSize = 3;
        b.Data.Connections.MaxSize = 2;
        c.Data.Connections.MaxSize = 1;
        d.Data.Connections.MaxSize = 0;
        f.Clean();
        var emptyBefore = new Snapshot(f);
        Connect(f, source, Array.Empty<BaseConnectorViewModel?>());
        emptyBefore.AssertUnchanged(f);

        Connect(f, source, b, c, c, b);
        Assert.AreEqual(3, source.Data.Connections.Count);
        f.Clean();
        var fullBefore = new Snapshot(f);
        Assert.IsTrue(f.Graph.CanConnectQuestFlow(source, b, out var reason), reason);
        Assert.IsTrue(f.Graph.CanConnectQuestFlow(source, c, out reason), reason);
        Connect(f, source, c, b);
        fullBefore.AssertUnchanged(f);
        source.Data.Connections.MaxSize = 4; // Isolate destination capacity from source capacity.
        Assert.IsFalse(f.Graph.CanConnectQuestFlow(source, d, out _));
        Reject(f, source, new[] { b, d });

        using var aggregate = new Fixture();
        var smallSource = Out(aggregate.Add<questFactsDBManagerNodeDefinition>());
        var first = In(aggregate.Add<questPauseConditionNodeDefinition>());
        var second = In(aggregate.Add<questConditionNodeDefinition>());
        smallSource.Data.Connections.MaxSize = 1;
        Assert.IsTrue(aggregate.Graph.CanConnectQuestFlow(smallSource, first, out reason), reason);
        Assert.IsTrue(aggregate.Graph.CanConnectQuestFlow(smallSource, second, out reason), reason);
        Reject(aggregate, smallSource, new[] { first, second });
        Connect(aggregate, smallSource, first, first);
        Assert.AreEqual(1, aggregate.Graph.Connections.Count);
        AssertIntegrity(aggregate);
    }

    [TestMethod]
    public void SecondAdditionAndLateRefreshFailuresRollBackEveryArrayFlagAndDirtyState()
    {
        foreach (var lateRefresh in new[] { false, true })
        foreach (var initiallyDirty in new[] { false, true })
        {
            using var f = new Fixture();
            var source = Out(f.Add<questFactsDBManagerNodeDefinition>());
            var existing = In(f.Add<questPauseConditionNodeDefinition>());
            var cNode = f.Add<questConditionNodeDefinition>();
            var dNode = f.Add<questEventManagerNodeDefinition>();
            var c = In(cNode);
            var d = In(dNode);
            f.Edge(source, existing, sharedHandle: true);
            cNode.ShowUnusedSockets = false;
            dNode.ShowUnusedSockets = false;
            f.Document.Document.SetIsDirty(initiallyDirty);
            var before = new Snapshot(f);
            var additions = 0;
            var injected = false;
            var edgesAtFailure = 0;
            NotifyCollectionChangedEventHandler addFailure = (_, args) =>
            {
                if (args.Action == NotifyCollectionChangedAction.Add && ++additions == 2 && !injected)
                {
                    injected = true;
                    edgesAtFailure = f.Graph.Connections.Count;
                    throw new InvalidOperationException("Fail the second new edge after its backing handles were added.");
                }
            };
            PropertyChangedEventHandler dataFailure = (_, args) =>
            {
                if (!injected && args.PropertyName == nameof(NodeViewModel.Data))
                {
                    injected = true;
                    edgesAtFailure = f.Graph.Connections.Count;
                    throw new InvalidOperationException("Fail refresh after the complete batch was added.");
                }
            };
            if (lateRefresh)
                foreach (var node in f.Graph.Nodes) node.PropertyChanged += dataFailure;
            else f.Graph.Connections.CollectionChanged += addFailure;
            try
            {
                Assert.IsFalse(f.Graph.TryConnectMultipleQuestNodes(source, new[] { c, d }, out var reason));
                Assert.IsFalse(string.IsNullOrWhiteSpace(reason));
                Assert.IsTrue(injected, "Exercise the requested commit stage rather than an early rejection.");
                Assert.AreEqual(3, edgesAtFailure);
            }
            finally
            {
                f.Graph.Connections.CollectionChanged -= addFailure;
                foreach (var node in f.Graph.Nodes) node.PropertyChanged -= dataFailure;
            }
            before.AssertUnchanged(f);
            AssertIntegrity(f);
            AssertRoundTrips(f);
        }
    }

    [TestMethod]
    public void WorkspotOutputsAndUnrelatedSocketStructureSurviveWithoutGlobalRefresh()
    {
        using var f = new Fixture();
        var workspot = f.Add<questUseWorkspotNodeDefinition>();
        var parameters = new questUseWorkspotParamsV1 { Function = Enums.questUseWorkspotNodeFunctions.UseWorkspot };
        ((questUseWorkspotNodeDefinition)workspot.Data).ParamsV1 = parameters;
        workspot.RefreshFromData();
        var source = Out(workspot, "Work Started");
        var old = In(f.Add<questPauseConditionNodeDefinition>());
        var c = In(f.Add<questEventManagerNodeDefinition>());
        var d = In(f.Add<questConditionNodeDefinition>());
        f.Edge(source, old);
        parameters.Function = Enums.questUseWorkspotNodeFunctions.StopWorkspot;
        workspot.RefreshFromData(); // Connected legacy Work Started is deliberately retained.
        var unrelated = f.Add<questUseWorkspotNodeDefinition>();
        var unrelatedParameters = new questUseWorkspotParamsV1 { Function = Enums.questUseWorkspotNodeFunctions.UseWorkspot };
        ((questUseWorkspotNodeDefinition)unrelated.Data).ParamsV1 = unrelatedParameters;
        unrelated.RefreshFromData();
        unrelatedParameters.Function = Enums.questUseWorkspotNodeFunctions.StopWorkspot;
        f.Clean();
        var before = new Snapshot(f);
        var globalRefreshes = 0;
        EventHandler<NodePropertyUpdatedEventArgs> trap = (_, _) =>
        {
            globalRefreshes++;
            throw new InvalidOperationException("Multi Connect must use nonstructural connection refresh.");
        };
        NodePropertyUpdateService.NodePropertyUpdated += trap;
        try { Connect(f, source, c, d); }
        finally { NodePropertyUpdateService.NodePropertyUpdated -= trap; }
        Assert.AreEqual(0, globalRefreshes);
        before.AssertExistingPreserved(f);
        Assert.AreSame(source, Out(workspot, "Work Started"));
        Assert.AreEqual(3, source.Data.Connections.Count);
        Assert.AreEqual(Enums.questUseWorkspotNodeFunctions.StopWorkspot,
            (Enums.questUseWorkspotNodeFunctions)parameters.Function);
        Assert.IsNotNull(Out(unrelated, "Work Started"), "An unrelated retained output must not be pruned.");
        AssertIntegrity(f);
    }

    [TestMethod]
    public void JsonAndCr2wReopenPreserveBatchTopologyExistingMetadataAndNodeProperties()
    {
        using var f = new Fixture();
        var facts = f.Add<questFactsDBManagerNodeDefinition>();
        var pause = f.Add<questPauseConditionNodeDefinition>();
        var flow = f.Add<questFlowControlNodeDefinition>();
        var journal = f.Add<questJournalNodeDefinition>();
        var workspot = f.Add<questUseWorkspotNodeDefinition>();
        var condition = f.Add<questConditionNodeDefinition>();
        var factValue = (questSetVar_NodeType)((questFactsDBManagerNodeDefinition)facts.Data).Type.GetValue()!;
        factValue.FactName = "multi_connect_fact";
        factValue.Value = 43;
        ((questPauseConditionNodeDefinition)pause.Data).Condition = new CHandle<questIBaseCondition>(
            new questTriggerCondition { TriggerAreaRef = "#multi_connect_trigger", IsPlayerActivator = true });
        ((questFlowControlNodeDefinition)flow.Data).OpensAt = 7;
        ((questFlowControlNodeDefinition)flow.Data).ClosesAt = 11;
        ((questUseWorkspotNodeDefinition)workspot.Data).ParamsV1 = new CHandle<questUseWorkspotParamsV1>(
            new questUseWorkspotParamsV1 { Function = Enums.questUseWorkspotNodeFunctions.UseWorkspot, WorkspotNode = "#multi_workspot" });
        workspot.RefreshFromData();
        var existing = f.Edge(Out(condition, "False"), In(journal, "Failed"),
            definition: new gamestateMachineTransitionDefinition { Priority = 8.5f });
        Connect(f, Out(facts), In(pause), In(flow, "Switch"), In(journal, "Active"), In(workspot));
        Connect(f, Out(workspot, "Work Started"), In(condition));
        f.Clean();
        var before = new Snapshot(f);
        Connect(f, existing.Source, existing.Target);
        before.AssertUnchanged(f);
        AssertRoundTrips(f);
    }

    private static void Connect(Fixture f, BaseConnectorViewModel? source, params BaseConnectorViewModel?[] targets)
    {
        Assert.IsTrue(f.Graph.TryConnectMultipleQuestNodes(source, targets, out var reason), reason);
    }

    private static void Reject(Fixture f, BaseConnectorViewModel? source, IEnumerable<BaseConnectorViewModel?>? targets)
    {
        var before = new Snapshot(f);
        Assert.IsFalse(f.Graph.CanConnectMultipleQuestNodes(source, targets, out var reason));
        Assert.IsFalse(string.IsNullOrWhiteSpace(reason));
        before.AssertUnchanged(f);
        Assert.IsFalse(f.Graph.TryConnectMultipleQuestNodes(source, targets, out reason));
        Assert.IsFalse(string.IsNullOrWhiteSpace(reason));
        before.AssertUnchanged(f);
    }

    private static IEnumerable<BaseConnectorViewModel?> ThrowingTargets(BaseConnectorViewModel first)
    {
        yield return first;
        throw new InvalidOperationException("Target enumeration failed before validation completed.");
    }

    private static QuestInputConnectorViewModel In(BaseQuestViewModel node, string name = "In") =>
        node.Input.OfType<QuestInputConnectorViewModel>().Single(port => port.Name == name);
    private static QuestOutputConnectorViewModel Out(BaseQuestViewModel node, string name = "Out") =>
        node.Output.OfType<QuestOutputConnectorViewModel>().Single(port => port.Name == name);
    private static RedGraph Generate(questGraphDefinition data) =>
        RedGraph.GenerateQuestGraph("multi-connect.questphase", data,
            new NodeWrapperFactory(Mock.Of<ILoggerService>(), Mock.Of<IArchiveManager>(), Mock.Of<IGameControllerFactory>()));
    private static string[] NamedTopology(RedGraph graph) => graph.Connections.Select(edge =>
        $"{edge.Source.OwnerId}:{edge.Source.Name}->{edge.Target.OwnerId}:{edge.Target.Name}").ToArray();
    private static questSocketDefinition Socket(BaseConnectorViewModel port) => port switch
    {
        QuestInputConnectorViewModel input => input.Data,
        QuestOutputConnectorViewModel output => output.Data,
        _ => throw new InvalidOperationException("Expected a quest socket.")
    };
    private static void SameReferences<T>(IEnumerable<T> expected, IEnumerable<T> actual) where T : class
    {
        var a = expected.ToArray();
        var b = actual.ToArray();
        Assert.AreEqual(a.Length, b.Length);
        for (var i = 0; i < a.Length; i++) Assert.AreSame(a[i], b[i]);
    }
    private static string DataJson(questGraphDefinition data)
    {
        using var file = new CR2WFile { RootChunk = new questQuestPhaseResource { Graph = data } };
        return JObject.Parse(RedJsonSerializer.Serialize(new RedFileDto(file)))["Data"]!.ToString();
    }
    private static void AssertIntegrity(Fixture f) => AssertIntegrity(f.Graph, f.Data);
    private static void AssertIntegrity(RedGraph graph, questGraphDefinition data)
    {
        var sockets = data.Nodes.SelectMany(handle => ((questNodeDefinition)handle.GetValue()!).Sockets)
            .Select(handle => (questSocketDefinition)handle.GetValue()!).ToArray();
        foreach (var edge in graph.Connections.OfType<QuestConnectionViewModel>())
        {
            var source = Socket(edge.Source);
            var target = Socket(edge.Target);
            Assert.AreSame(source, edge.ConnectionDefinition.Source.GetValue());
            Assert.AreSame(target, edge.ConnectionDefinition.Destination.GetValue());
            Assert.IsTrue(sockets.Any(socket => ReferenceEquals(socket, source)));
            Assert.IsTrue(sockets.Any(socket => ReferenceEquals(socket, target)));
            Assert.AreEqual(1, source.Connections.Count(handle => ReferenceEquals(handle.GetValue(), edge.ConnectionDefinition)));
            Assert.AreEqual(1, target.Connections.Count(handle => ReferenceEquals(handle.GetValue(), edge.ConnectionDefinition)));
        }
        foreach (var socket in sockets)
        foreach (var handle in socket.Connections)
            Assert.AreEqual(1, graph.Connections.OfType<QuestConnectionViewModel>()
                .Count(edge => ReferenceEquals(edge.ConnectionDefinition, handle.GetValue())));
        foreach (var port in graph.Nodes.SelectMany(node => node.Input.Cast<BaseConnectorViewModel>().Concat(node.Output)))
            Assert.AreEqual(Socket(port).Connections.Count > 0, port.IsConnected);
    }
    private static string[] SavedTopology(RedGraph graph) => graph.Connections.OfType<QuestConnectionViewModel>().Select(edge =>
    {
        var source = graph.Nodes.Single(node => node.Output.Contains(edge.Source));
        var target = graph.Nodes.Single(node => node.Input.Contains(edge.Target));
        var sourceIndex = ((questNodeDefinition)source.Data).Sockets.ToList().FindIndex(handle => ReferenceEquals(handle.GetValue(), Socket(edge.Source)));
        var targetIndex = ((questNodeDefinition)target.Data).Sockets.ToList().FindIndex(handle => ReferenceEquals(handle.GetValue(), Socket(edge.Target)));
        var metadata = edge.ConnectionDefinition is gamestateMachineTransitionDefinition transition
            ? ((float)transition.Priority).ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
        return $"{source.UniqueId}:{sourceIndex}->{target.UniqueId}:{targetIndex}:{edge.ConnectionDefinition.GetType().Name}:{metadata}";
    }).ToArray();
    private static void AssertRoundTrips(Fixture f)
    {
        using var original = new CR2WFile { RootChunk = new questQuestPhaseResource { Graph = f.Data } };
        var dto = RedJsonSerializer.Deserialize<RedFileDto>(RedJsonSerializer.Serialize(new RedFileDto(original)))!;
        using var jsonFile = dto.Data!;
        Check(jsonFile);
        using var stream = new MemoryStream();
        using (var writer = new CR2WWriter(stream, Encoding.UTF8, true)) writer.WriteFile(original);
        stream.Position = 0;
        using var reader = new CR2WReader(stream, Encoding.UTF8, true);
        Assert.AreEqual(WolvenKit.RED4.Archive.IO.EFileReadErrorCodes.NoError, reader.ReadFile(out var binaryFile));
        using (binaryFile) Check(binaryFile!);
        void Check(CR2WFile file)
        {
            var data = (questGraphDefinition)((questQuestPhaseResource)file.RootChunk).Graph.GetValue()!;
            using var graph = Generate(data);
            CollectionAssert.AreEqual(f.Graph.Nodes.Select(node => node.UniqueId).ToArray(), graph.Nodes.Select(node => node.UniqueId).ToArray());
            CollectionAssert.AreEquivalent(SavedTopology(f.Graph), SavedTopology(graph));
            AssertIntegrity(graph, data);
            foreach (var node in f.Graph.Nodes)
            {
                var actual = graph.Nodes.Single(candidate => candidate.UniqueId == node.UniqueId);
                Assert.AreEqual(node.Data.GetType(), actual.Data.GetType());
                CollectionAssert.AreEqual(((questNodeDefinition)node.Data).Sockets.Select(handle => ((questSocketDefinition)handle.GetValue()!).Name).ToArray(),
                    ((questNodeDefinition)actual.Data).Sockets.Select(handle => ((questSocketDefinition)handle.GetValue()!).Name).ToArray());
                if (node.Data is questFactsDBManagerNodeDefinition facts)
                {
                    var a = (questSetVar_NodeType)facts.Type.GetValue()!;
                    var b = (questSetVar_NodeType)((questFactsDBManagerNodeDefinition)actual.Data).Type.GetValue()!;
                    Assert.AreEqual(a.FactName, b.FactName);
                    Assert.AreEqual(a.Value, b.Value);
                }
                if (node.Data is questPauseConditionNodeDefinition { Condition.Chunk: questTriggerCondition trigger })
                    Assert.AreEqual(trigger.TriggerAreaRef,
                        ((questTriggerCondition)((questPauseConditionNodeDefinition)actual.Data).Condition.GetValue()!).TriggerAreaRef);
                if (node.Data is questFlowControlNodeDefinition flow)
                {
                    Assert.AreEqual(flow.OpensAt, ((questFlowControlNodeDefinition)actual.Data).OpensAt);
                    Assert.AreEqual(flow.ClosesAt, ((questFlowControlNodeDefinition)actual.Data).ClosesAt);
                }
                if (node.Data is questUseWorkspotNodeDefinition { ParamsV1.Chunk: { } parameters })
                {
                    var b = ((questUseWorkspotNodeDefinition)actual.Data).ParamsV1.Chunk!;
                    Assert.AreEqual(parameters.Function, b.Function);
                    Assert.AreEqual(parameters.WorkspotNode, b.WorkspotNode);
                }
            }
        }
    }

    private sealed class Fixture : IDisposable
    {
        public questGraphDefinition Data { get; } = GraphTestHelpers.CreateQuestGraph();
        public RedGraph Graph { get; }
        public GraphDocumentTestFixture Document { get; } = new("mod\\multi-connect.questphase");
        public Fixture() { Graph = Generate(Data); Graph.DocumentViewModel = Document.Document; }
        public BaseQuestViewModel Add<T>() where T : questNodeDefinition => Add(typeof(T));
        public BaseQuestViewModel Add(Type type)
        {
            var id = Graph.CreateQuestNode(type, new Point(Graph.Nodes.Count * 120, 40));
            return Graph.Nodes.OfType<BaseQuestViewModel>().Single(node => node.UniqueId == id);
        }
        public QuestInputConnectorViewModel AddInput(BaseQuestViewModel node, string name, Enums.questSocketType type = Enums.questSocketType.Input)
        {
            var port = new QuestInputConnectorViewModel(name, name, node.UniqueId,
                GraphTestHelpers.AddQuestSocket((questNodeDefinition)node.Data, name, type));
            node.Input.Add(port);
            return port;
        }
        public QuestOutputConnectorViewModel AddOutput(BaseQuestViewModel node, string name, Enums.questSocketType type)
        {
            var port = new QuestOutputConnectorViewModel(name, name, node.UniqueId,
                GraphTestHelpers.AddQuestSocket((questNodeDefinition)node.Data, name, type));
            node.Output.Add(port);
            return port;
        }
        public QuestConnectionViewModel Edge(QuestOutputConnectorViewModel source, QuestInputConnectorViewModel target,
            bool sharedHandle = false, graphGraphConnectionDefinition? definition = null)
        {
            if (definition is null && !sharedHandle) definition = GraphTestHelpers.ConnectQuestSockets(source.Data, target.Data);
            else
            {
                definition ??= new graphGraphConnectionDefinition();
                definition.Source = new CWeakHandle<graphGraphSocketDefinition>(source.Data);
                definition.Destination = new CWeakHandle<graphGraphSocketDefinition>(target.Data);
                var handle = new CHandle<graphGraphConnectionDefinition>(definition);
                source.Data.Connections.Add(handle);
                target.Data.Connections.Add(sharedHandle ? handle : new CHandle<graphGraphConnectionDefinition>(definition));
            }
            var edge = new QuestConnectionViewModel(source, target, definition);
            Graph.Connections.Add(edge);
            return edge;
        }
        public void Clean() => Document.Document.SetIsDirty(false);
        public void Dispose() { Graph.Dispose(); Document.Dispose(); }
    }

    private sealed class Snapshot
    {
        private readonly string _json;
        private readonly bool _dirty;
        private readonly NodeViewModel[] _nodes;
        private readonly object[] _canvas;
        private readonly CHandle<graphGraphNodeDefinition>[] _nodeHandles;
        private readonly ConnectionViewModel[] _edges;
        private readonly NodeState[] _structure;
        private readonly (questSocketDefinition Socket, CArray<CHandle<graphGraphConnectionDefinition>> Array, CHandle<graphGraphConnectionDefinition>[] Handles, int Capacity)[] _sockets;
        private readonly (BaseConnectorViewModel Port, bool Connected, bool Visible)[] _ports;
        private readonly (QuestConnectionViewModel Edge, CWeakHandle<graphGraphSocketDefinition> Source, CWeakHandle<graphGraphSocketDefinition> Target, bool Selected, int Path, float? Priority)[] _edgeState;
        public Snapshot(Fixture f)
        {
            _json = DataJson(f.Data);
            _dirty = f.Document.Document.IsDirty;
            _nodes = f.Graph.Nodes.ToArray();
            _canvas = f.Graph.CanvasItems.ToArray();
            _nodeHandles = f.Data.Nodes.ToArray();
            _edges = f.Graph.Connections.ToArray();
            _structure = _nodes.Select(node => new NodeState(node, node.Input.ToArray(), node.Output.ToArray(),
                ((questNodeDefinition)node.Data).Sockets.ToArray(), node.Location)).ToArray();
            _sockets = f.Data.Nodes.SelectMany(handle => ((questNodeDefinition)handle.GetValue()!).Sockets)
                .Select(handle => (questSocketDefinition)handle.GetValue()!)
                .Select(socket => (socket, socket.Connections, socket.Connections.ToArray(), socket.Connections.MaxSize)).ToArray();
            _ports = _nodes.SelectMany(node => node.Input.Cast<BaseConnectorViewModel>().Concat(node.Output))
                .Select(port => (port, port.IsConnected, port.IsVisible)).ToArray();
            _edgeState = _edges.OfType<QuestConnectionViewModel>().Select(edge => (edge, edge.ConnectionDefinition.Source,
                edge.ConnectionDefinition.Destination, edge.IsSelected, edge.PathType,
                edge.ConnectionDefinition is gamestateMachineTransitionDefinition transition ? (float?)transition.Priority : null)).ToArray();
        }
        public void AssertExistingPreserved(Fixture f)
        {
            SameReferences(_nodes, f.Graph.Nodes);
            SameReferences(_canvas, f.Graph.CanvasItems);
            SameReferences(_nodeHandles, f.Data.Nodes);
            SameReferences(_edges, f.Graph.Connections.Take(_edges.Length));
            foreach (var state in _structure)
            {
                SameReferences(state.Inputs, state.Node.Input);
                SameReferences(state.Outputs, state.Node.Output);
                SameReferences(state.Sockets, ((questNodeDefinition)state.Node.Data).Sockets);
                Assert.AreEqual(state.Location, state.Node.Location);
            }
            foreach (var (socket, array, handles, capacity) in _sockets)
            {
                Assert.AreSame(array, socket.Connections);
                Assert.AreEqual(capacity, socket.Connections.MaxSize);
                SameReferences(handles, socket.Connections.Take(handles.Length));
            }
            foreach (var (edge, source, target, selected, path, priority) in _edgeState)
            {
                Assert.AreSame(source, edge.ConnectionDefinition.Source);
                Assert.AreSame(target, edge.ConnectionDefinition.Destination);
                Assert.AreEqual(selected, edge.IsSelected);
                Assert.AreEqual(path, edge.PathType);
                if (priority is { } value) Assert.AreEqual(value, (float)((gamestateMachineTransitionDefinition)edge.ConnectionDefinition).Priority);
            }
        }
        public void AssertUnchanged(Fixture f)
        {
            AssertExistingPreserved(f);
            Assert.AreEqual(_json, DataJson(f.Data));
            Assert.AreEqual(_dirty, f.Document.Document.IsDirty);
            Assert.AreEqual(_edges.Length, f.Graph.Connections.Count);
            foreach (var (socket, _, handles, _) in _sockets) Assert.AreEqual(handles.Length, socket.Connections.Count);
            foreach (var (port, connected, visible) in _ports)
            {
                Assert.AreEqual(connected, port.IsConnected);
                Assert.AreEqual(visible, port.IsVisible);
            }
        }
        private sealed record NodeState(NodeViewModel Node, InputConnectorViewModel[] Inputs, OutputConnectorViewModel[] Outputs,
            CHandle<graphGraphSocketDefinition>[] Sockets, Point Location);
    }
}
