using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Newtonsoft.Json;
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
public class QuestConnectionRewiringTests
{
    [TestMethod]
    public void RewireDestinationPreservesDefinitionMetadataAndSharedHandles()
    {
        using var fixture = new Fixture();
        var source = fixture.Add<questFactsDBManagerNodeDefinition>();
        var oldTarget = fixture.Add<questPauseConditionNodeDefinition>();
        var newTarget = fixture.Add<questFlowControlNodeDefinition>();
        var definition = new gamestateMachineTransitionDefinition { Priority = 7.25f };
        var original = fixture.Edge(source, "Out", oldTarget, "In", definition, sharedHandle: true);
        original.IsSelected = true;
        original.PathType = 1;
        var sourcePort = Output(source, "Out");
        var oldPort = Input(oldTarget, "In");
        var newPort = Input(newTarget, "Switch");
        var handle = sourcePort.Data.Connections.Single();
        var fixedEndpoint = definition.Source;
        var movingEndpoint = definition.Destination;
        Assert.AreSame(handle, oldPort.Data.Connections.Single(), "Exercise one shared RED handle wrapper.");
        fixture.Clean();
        var before = new Snapshot(fixture);

        Assert.IsTrue(fixture.Graph.CanStartQuestConnectionRewire(original, out var reason), reason);
        Assert.IsTrue(fixture.Graph.CanRewireQuestConnection(original, QuestConnectionEndpoint.Destination, newPort, out reason), reason);
        before.AssertUnchanged(fixture);
        Assert.IsTrue(fixture.Graph.TryRewireQuestConnection(original, QuestConnectionEndpoint.Destination, newPort, out reason), reason);

        var moved = (QuestConnectionViewModel)fixture.Graph.Connections.Single();
        Assert.AreNotSame(original, moved);
        Assert.AreSame(definition, moved.ConnectionDefinition);
        Assert.AreSame(fixedEndpoint, definition.Source);
        Assert.AreNotSame(movingEndpoint, definition.Destination);
        Assert.AreSame(sourcePort, moved.Source);
        Assert.AreSame(newPort, moved.Target);
        Assert.AreSame(handle, sourcePort.Data.Connections.Single());
        Assert.AreSame(handle, newPort.Data.Connections.Single());
        Assert.AreEqual(0, oldPort.Data.Connections.Count);
        Assert.IsFalse(oldPort.IsConnected);
        Assert.IsTrue(moved.IsSelected);
        Assert.AreEqual(1, moved.PathType);
        Assert.AreEqual(7.25f, (float)definition.Priority);
        Assert.IsTrue(fixture.Document.Document.IsDirty);
        AssertIntegrity(fixture.Graph, fixture.Data);
    }

    [TestMethod]
    public void RewireSourceMovesOnlyItsHandleAndKeepsDistinctReciprocalWrapper()
    {
        using var fixture = new Fixture();
        var oldSource = fixture.Add<questFactsDBManagerNodeDefinition>();
        var newSource = fixture.Add<questPauseConditionNodeDefinition>();
        var target = fixture.Add<questJournalNodeDefinition>();
        var original = fixture.Edge(oldSource, "Out", target, "Active");
        var oldPort = Output(oldSource, "Out");
        var newPort = Output(newSource, "Out");
        var targetPort = Input(target, "Active");
        var sourceHandle = oldPort.Data.Connections.Single();
        var targetHandle = targetPort.Data.Connections.Single();
        Assert.AreNotSame(sourceHandle, targetHandle, "Exercise distinct wrappers for one connection definition.");
        var definition = original.ConnectionDefinition;
        var fixedEndpoint = definition.Destination;
        var movingEndpoint = definition.Source;
        fixture.Clean();

        Assert.IsTrue(fixture.Graph.TryRewireQuestConnection(original, QuestConnectionEndpoint.Source, newPort, out var reason), reason);

        var moved = (QuestConnectionViewModel)fixture.Graph.Connections.Single();
        Assert.AreSame(definition, moved.ConnectionDefinition);
        Assert.AreSame(fixedEndpoint, definition.Destination);
        Assert.AreNotSame(movingEndpoint, definition.Source);
        Assert.AreSame(newPort, moved.Source);
        Assert.AreSame(targetPort, moved.Target);
        Assert.AreSame(sourceHandle, newPort.Data.Connections.Single());
        Assert.AreSame(targetHandle, targetPort.Data.Connections.Single());
        Assert.AreEqual(0, oldPort.Data.Connections.Count);
        Assert.IsFalse(oldPort.IsConnected);
        Assert.IsTrue(newPort.IsConnected);
        Assert.IsTrue(targetPort.IsConnected);
        AssertIntegrity(fixture.Graph, fixture.Data);
    }

    [TestMethod]
    public void BranchesParallelEdgesAndDuplicateSocketNamesKeepTheirExactIdentity()
    {
        using var fixture = new Fixture();
        var condition = fixture.Add<questConditionNodeDefinition>();
        var hub = fixture.Add<questLogicalHubNodeDefinition>();
        var flow = fixture.Add<questFlowControlNodeDefinition>();
        var journal = fixture.Add<questJournalNodeDefinition>();
        var pause = fixture.Add<questPauseConditionNodeDefinition>();
        var firstBranch = fixture.AddOutput(condition, "branch:custom");
        var secondBranch = fixture.AddOutput(condition, "branch:custom");
        var beforeEdge = fixture.Edge(condition, "False", hub, "In1");
        var original = fixture.Edge(firstBranch, Input(flow, "Switch"));
        var parallel = fixture.Edge(firstBranch, Input(flow, "Switch"));
        var branch = fixture.Edge(firstBranch, Input(hub, "In1"));
        var occupiedTarget = fixture.Edge(pause, "Out", journal, "Succeeded");
        var afterEdge = fixture.Edge(condition, "True", flow, "Open");
        var untouched = new[] { beforeEdge, parallel, branch, occupiedTarget, afterEdge };
        var originalVisuals = fixture.Graph.Connections.ToArray();
        var untouchedHandles = untouched.Select(edge => new EdgeHandles(edge)).ToArray();
        var fixedSourceHandles = firstBranch.Data.Connections.ToArray();
        var movingHandle = Input(flow, "Switch").Data.Connections
            .Single(handle => ReferenceEquals(handle.GetValue(), original.ConnectionDefinition));
        var definition = original.ConnectionDefinition;
        fixture.Clean();

        Assert.IsTrue(fixture.Graph.TryRewireQuestConnection(original, QuestConnectionEndpoint.Destination,
            Input(journal, "Succeeded"), out var reason), reason);

        var moved = (QuestConnectionViewModel)fixture.Graph.Connections[1];
        Assert.AreSame(definition, moved.ConnectionDefinition);
        Assert.AreSame(Input(journal, "Succeeded"), moved.Target);
        Assert.AreSame(movingHandle, Input(journal, "Succeeded").Data.Connections.Last());
        AssertReferences(fixedSourceHandles, firstBranch.Data.Connections);
        Assert.AreEqual(2, Input(journal, "Succeeded").Data.Connections.Count, "Preserve the pre-existing incoming edge.");
        Assert.IsTrue(Input(flow, "Switch").IsConnected, "The parallel edge still uses the old input.");
        AssertOtherEdges();

        Assert.IsTrue(fixture.Graph.TryRewireQuestConnection(moved, QuestConnectionEndpoint.Source, secondBranch, out reason), reason);

        moved = (QuestConnectionViewModel)fixture.Graph.Connections[1];
        Assert.AreSame(definition, moved.ConnectionDefinition);
        Assert.AreSame(secondBranch, moved.Source, "Equal names must not select the first matching socket.");
        Assert.IsTrue(firstBranch.IsConnected, "Other branches remain on the original source socket.");
        AssertOtherEdges();
        AssertIntegrity(fixture.Graph, fixture.Data);

        void AssertOtherEdges()
        {
            Assert.AreEqual(originalVisuals.Length, fixture.Graph.Connections.Count);
            for (var i = 0; i < originalVisuals.Length; i++)
            {
                if (i != 1) Assert.AreSame(originalVisuals[i], fixture.Graph.Connections[i]);
            }
            foreach (var handles in untouchedHandles) handles.AssertPreserved();
        }
    }

    [TestMethod]
    public void RepresentativeQuestNodesUseTheirRealNamedFlowSockets()
    {
        var cases = new[]
        {
            (typeof(questFactsDBManagerNodeDefinition), "Out", typeof(questPauseConditionNodeDefinition), "In", typeof(questFlowControlNodeDefinition), "Open"),
            (typeof(questPauseConditionNodeDefinition), "Out", typeof(questConditionNodeDefinition), "In", typeof(questLogicalAndNodeDefinition), "In2"),
            (typeof(questConditionNodeDefinition), "True", typeof(questLogicalHubNodeDefinition), "In1", typeof(questLogicalXorNodeDefinition), "In2"),
            (typeof(questLogicalHubNodeDefinition), "Out1", typeof(questLogicalAndNodeDefinition), "In1", typeof(questJournalNodeDefinition), "Failed"),
            (typeof(questLogicalAndNodeDefinition), "Out1", typeof(questLogicalXorNodeDefinition), "In1", typeof(questFactsDBManagerNodeDefinition), "In"),
            (typeof(questLogicalXorNodeDefinition), "Out1", typeof(questFlowControlNodeDefinition), "Close", typeof(questPauseConditionNodeDefinition), "In"),
            (typeof(questFlowControlNodeDefinition), "Out", typeof(questJournalNodeDefinition), "Inactive", typeof(questOutputNodeDefinition), "In"),
            (typeof(questInputNodeDefinition), "Out", typeof(questPauseConditionNodeDefinition), "In", typeof(questOutputNodeDefinition), "In"),
            (typeof(questJournalNodeDefinition), "Out", typeof(questOutputNodeDefinition), "In", typeof(questConditionNodeDefinition), "In")
        };
        foreach (var (sourceType, output, oldType, oldInput, newType, newInput) in cases)
        {
            using var fixture = new Fixture();
            var source = fixture.Add(sourceType);
            var oldTarget = fixture.Add(oldType);
            var newTarget = fixture.Add(newType);
            var original = fixture.Edge(source, output, oldTarget, oldInput);
            var destination = Input(newTarget, newInput);

            Assert.IsTrue(fixture.Graph.TryRewireQuestConnection(original, QuestConnectionEndpoint.Destination,
                destination, out var reason), $"{sourceType.Name} -> {newType.Name}.{newInput}: {reason}");

            var moved = (QuestConnectionViewModel)fixture.Graph.Connections.Single();
            Assert.AreSame(Output(source, output), moved.Source);
            Assert.AreSame(destination, moved.Target);
            Assert.AreEqual(0, Input(oldTarget, oldInput).Data.Connections.Count);
            AssertIntegrity(fixture.Graph, fixture.Data);
        }
    }

    [TestMethod]
    public void InvalidNullDetachedForeignAndNestedTargetsLeaveBothScopesUnchanged()
    {
        using var fixture = new Fixture();
        using var nested = new Fixture();
        var source = fixture.Add<questFactsDBManagerNodeDefinition>();
        var oldTarget = fixture.Add<questPauseConditionNodeDefinition>();
        var alternate = fixture.Add<questConditionNodeDefinition>();
        var phase = fixture.Add<questPhaseNodeDefinition>();
        var nestedSource = nested.Add<questFactsDBManagerNodeDefinition>();
        var nestedTarget = nested.Add<questPauseConditionNodeDefinition>();
        nested.Edge(nestedSource, "Out", nestedTarget, "In");
        ((questPhaseNodeDefinition)phase.Data).PhaseGraph = nested.Data;
        var original = fixture.Edge(source, "Out", oldTarget, "In");
        using var detached = fixture.Graph.PrepareQuestNode(typeof(questPauseConditionNodeDefinition), new Point());
        fixture.Clean();
        nested.Clean();
        var before = new Snapshot(fixture);
        var nestedBefore = new Snapshot(nested);
        Assert.IsFalse(fixture.Graph.CanStartQuestConnectionRewire(null, out var missingReason));
        Assert.IsFalse(string.IsNullOrWhiteSpace(missingReason));

        foreach (var (endpoint, target) in new (QuestConnectionEndpoint, BaseConnectorViewModel?)[]
                 {
                     (QuestConnectionEndpoint.Destination, null),
                     (QuestConnectionEndpoint.Source, null),
                     (QuestConnectionEndpoint.Destination, Output(alternate, "True")),
                     (QuestConnectionEndpoint.Source, Input(alternate, "In")),
                     (QuestConnectionEndpoint.Destination, Input(detached, "In")),
                     (QuestConnectionEndpoint.Destination, Input(nestedTarget, "In")),
                     (QuestConnectionEndpoint.Source, Output(nestedSource, "Out")),
                     ((QuestConnectionEndpoint)99, Input(alternate, "In"))
                 })
        {
            Assert.IsFalse(fixture.Graph.CanRewireQuestConnection(original, endpoint, target, out var reason));
            Assert.IsFalse(string.IsNullOrWhiteSpace(reason));
            Assert.IsFalse(fixture.Graph.TryRewireQuestConnection(original, endpoint, target, out reason));
            Assert.IsFalse(string.IsNullOrWhiteSpace(reason));
            before.AssertUnchanged(fixture);
            nestedBefore.AssertUnchanged(nested);
        }

        using var foreign = new Fixture();
        var foreignSource = foreign.Add<questFactsDBManagerNodeDefinition>();
        var foreignTarget = foreign.Add<questPauseConditionNodeDefinition>();
        foreign.Clean();
        var foreignBefore = new Snapshot(foreign);
        AssertRejected(fixture, original, QuestConnectionEndpoint.Source, Output(foreignSource, "Out"));
        AssertRejected(fixture, original, QuestConnectionEndpoint.Destination, Input(foreignTarget, "In"));
        before.AssertUnchanged(fixture);
        foreignBefore.AssertUnchanged(foreign);
    }

    [TestMethod]
    public void MixedCutUndefinedAndSyntheticBoundarySocketsAreRejected()
    {
        using var fixture = new Fixture();
        var source = fixture.Add<questFactsDBManagerNodeDefinition>();
        var target = fixture.Add<questPauseConditionNodeDefinition>();
        var cut = fixture.Add<questCutControlNodeDefinition>();
        var inputNode = fixture.Add<questInputNodeDefinition>();
        var outputNode = fixture.Add<questOutputNodeDefinition>();
        var original = fixture.Edge(source, "Out", target, "In");
        var syntheticInput = inputNode.Input.OfType<QuestInputConnectorViewModel>()
            .Single(port => port.Data.Type == Enums.questSocketType.Undefined);
        var syntheticOutput = outputNode.Output.OfType<QuestOutputConnectorViewModel>()
            .Single(port => port.Data.Type == Enums.questSocketType.Undefined);
        var undefinedOutput = fixture.AddOutput(cut, "untyped", Enums.questSocketType.Undefined);
        fixture.Clean();

        AssertRejected(fixture, original, QuestConnectionEndpoint.Destination, Input(cut, "CutDestination"));
        AssertRejected(fixture, original, QuestConnectionEndpoint.Source, Output(cut, "CutSource"));
        AssertRejected(fixture, original, QuestConnectionEndpoint.Destination, syntheticInput);
        AssertRejected(fixture, original, QuestConnectionEndpoint.Source, syntheticOutput);
        AssertRejected(fixture, original, QuestConnectionEndpoint.Source, undefinedOutput);

        foreach (var unsupported in new[]
                 {
                     fixture.Edge(cut, "CutSource", target, "In"),
                     fixture.Edge(source, "Out", cut, "CutDestination"),
                     fixture.Edge(undefinedOutput, Input(target, "In"))
                 })
        {
            fixture.Clean();
            var before = new Snapshot(fixture);
            Assert.IsFalse(fixture.Graph.CanStartQuestConnectionRewire(unsupported, out var reason));
            Assert.IsFalse(string.IsNullOrWhiteSpace(reason));
            AssertRejected(fixture, unsupported, QuestConnectionEndpoint.Destination, Input(outputNode, "In"));
            before.AssertUnchanged(fixture);
        }
    }

    [TestMethod]
    public void CutEndpointMovesPreserveRealCutControlBranchesMetadataAndRoundTrips()
    {
        foreach (var sharedHandle in new[] { false, true })
        {
            using var fixture = new Fixture();
            var source = fixture.Add<questCutControlNodeDefinition>();
            var oldTarget = fixture.Add<questPhaseNodeDefinition>();
            var alternate = fixture.Add<questCutControlNodeDefinition>();
            var alternateSource = fixture.Add<questCutControlNodeDefinition>();
            var branchTarget = fixture.Add<questPhaseNodeDefinition>();
            ((questCutControlNodeDefinition)source.Data).Permanent = true;
            ((questCutControlNodeDefinition)alternate.Data).Permanent = true;
            ((questCutControlNodeDefinition)alternateSource.Data).Permanent = false;
            var definition = new gamestateMachineTransitionDefinition { Priority = 8.25f };
            var original = fixture.Edge(source, "CutSource", oldTarget, "CutDestination", definition, sharedHandle);
            var branch = fixture.Edge(source, "CutSource", branchTarget, "CutDestination");
            var ordinary = fixture.Edge(source, "Out", alternate, "In");
            var branchHandles = new EdgeHandles(branch);
            var ordinaryHandles = new EdgeHandles(ordinary);
            var sourcePort = Output(source, "CutSource");
            var oldPort = Input(oldTarget, "CutDestination");
            var newPort = Input(alternate, "CutDestination");
            var newSource = Output(alternateSource, "CutSource");
            var sourceHandle = sourcePort.Data.Connections.Single(handle => ReferenceEquals(handle.GetValue(), definition));
            var targetHandle = oldPort.Data.Connections.Single();
            var sourceEndpoint = definition.Source;
            original.IsSelected = true;
            original.PathType = 1;
            fixture.Clean();
            var before = new Snapshot(fixture);

            Assert.IsTrue(fixture.Graph.CanStartQuestConnectionRewire(original, out var reason), reason);
            Assert.IsTrue(fixture.Graph.CanRewireQuestConnection(original, QuestConnectionEndpoint.Destination, newPort, out reason), reason);
            before.AssertUnchanged(fixture);
            Assert.IsTrue(fixture.Graph.TryRewireQuestConnection(original, QuestConnectionEndpoint.Destination, newPort, out reason), reason);

            var moved = (QuestConnectionViewModel)fixture.Graph.Connections[0];
            Assert.AreSame(definition, moved.ConnectionDefinition);
            Assert.AreSame(sourceEndpoint, definition.Source);
            Assert.AreSame(sourcePort, moved.Source);
            Assert.AreSame(newPort, moved.Target);
            Assert.AreSame(targetHandle, newPort.Data.Connections.Single());
            Assert.AreEqual(0, oldPort.Data.Connections.Count);
            Assert.IsFalse(oldPort.IsConnected);
            Assert.IsTrue(sourcePort.IsConnected, "The other Cut branch still uses the source.");
            var destinationEndpoint = definition.Destination;

            Assert.IsTrue(fixture.Graph.TryRewireQuestConnection(moved, QuestConnectionEndpoint.Source, newSource, out reason), reason);

            moved = (QuestConnectionViewModel)fixture.Graph.Connections[0];
            Assert.AreSame(definition, moved.ConnectionDefinition);
            Assert.AreSame(destinationEndpoint, definition.Destination);
            Assert.AreSame(newSource, moved.Source);
            Assert.AreSame(newPort, moved.Target);
            Assert.AreSame(sourceHandle, newSource.Data.Connections.Single());
            Assert.AreSame(targetHandle, newPort.Data.Connections.Single());
            Assert.AreSame(branch, fixture.Graph.Connections[1]);
            Assert.AreSame(ordinary, fixture.Graph.Connections[2]);
            branchHandles.AssertPreserved();
            ordinaryHandles.AssertPreserved();
            Assert.AreEqual(1, sourcePort.Data.Connections.Count);
            Assert.IsTrue(sourcePort.IsConnected);
            Assert.IsTrue(moved.IsSelected);
            Assert.AreEqual(1, moved.PathType);
            Assert.AreEqual(8.25f, (float)definition.Priority);
            Assert.IsTrue((bool)((questCutControlNodeDefinition)source.Data).Permanent);
            Assert.IsTrue((bool)((questCutControlNodeDefinition)alternate.Data).Permanent);
            Assert.IsFalse((bool)((questCutControlNodeDefinition)alternateSource.Data).Permanent);
            Assert.IsTrue(fixture.Document.Document.IsDirty);
            AssertIntegrity(fixture.Graph, fixture.Data);
            // Reopening preserves the concrete definition, metadata, and shared topology; CLR identity is process-local.
            AssertRoundTrips(fixture);
        }
    }

    [TestMethod]
    public void CutRewiresRejectTypeConversionsWrongDirectionForeignAndStaleSockets()
    {
        using var fixture = new Fixture();
        using var foreign = new Fixture();
        var source = fixture.Add<questCutControlNodeDefinition>();
        var target = fixture.Add<questCutControlNodeDefinition>();
        var alternate = fixture.Add<questCutControlNodeDefinition>();
        var foreignNode = foreign.Add<questCutControlNodeDefinition>();
        var original = fixture.Edge(source, "CutSource", target, "CutDestination");
        fixture.Clean();
        foreign.Clean();
        var foreignBefore = new Snapshot(foreign);
        foreach (var (endpoint, candidate) in new (QuestConnectionEndpoint, BaseConnectorViewModel?)[]
                 {
                     (QuestConnectionEndpoint.Source, Output(alternate, "Out")),
                     (QuestConnectionEndpoint.Destination, Input(alternate, "In")),
                     (QuestConnectionEndpoint.Source, Input(alternate, "CutDestination")),
                     (QuestConnectionEndpoint.Destination, Output(alternate, "CutSource")),
                     (QuestConnectionEndpoint.Source, Output(foreignNode, "CutSource")),
                     (QuestConnectionEndpoint.Destination, Input(foreignNode, "CutDestination")),
                     (QuestConnectionEndpoint.Source, null),
                     (QuestConnectionEndpoint.Destination, null)
                 })
        {
            AssertRejected(fixture, original, endpoint, candidate);
            foreignBefore.AssertUnchanged(foreign);
        }
        var staleCandidate = Input(alternate, "CutDestination");
        ((questNodeDefinition)alternate.Data).Sockets.RemoveAt(((questNodeDefinition)alternate.Data).Sockets.ToList()
            .FindIndex(handle => ReferenceEquals(handle.GetValue(), staleCandidate.Data)));
        AssertRejected(fixture, original, QuestConnectionEndpoint.Destination, staleCandidate);
        Input(target, "CutDestination").Data.Connections.Clear();
        Assert.IsFalse(fixture.Graph.CanStartQuestConnectionRewire(original, out var reason));
        Assert.IsFalse(string.IsNullOrWhiteSpace(reason));
        AssertRejected(fixture, original, QuestConnectionEndpoint.Source, Output(alternate, "CutSource"));
    }

    [TestMethod]
    public void CutMutationAndRefreshFailuresRestoreBothEndpointsHandlesFlagsAndDirtyState()
    {
        foreach (var failRefresh in new[] { false, true })
        foreach (var initiallyDirty in new[] { false, true })
        foreach (var endpoint in new[] { QuestConnectionEndpoint.Source, QuestConnectionEndpoint.Destination })
        {
            using var fixture = new Fixture();
            var source = fixture.Add<questCutControlNodeDefinition>();
            var target = fixture.Add<questCutControlNodeDefinition>();
            var alternate = fixture.Add<questCutControlNodeDefinition>();
            ((questCutControlNodeDefinition)source.Data).Permanent = true;
            var original = fixture.Edge(source, "CutSource", target, "CutDestination",
                new gamestateMachineTransitionDefinition { Priority = 6.5f }, sharedHandle: true);
            fixture.Edge(source, "Out", target, "In");
            original.IsSelected = true;
            original.PathType = 1;
            target.ShowUnusedSockets = false;
            alternate.ShowUnusedSockets = false;
            target.UpdateSocketVisibility();
            alternate.UpdateSocketVisibility();
            fixture.Document.Document.SetIsDirty(initiallyDirty);
            BaseConnectorViewModel candidate = endpoint == QuestConnectionEndpoint.Source
                ? Output(alternate, "CutSource") : Input(alternate, "CutDestination");
            var before = new Snapshot(fixture);
            var injected = false;
            NotifyCollectionChangedEventHandler mutationFailure = (_, args) =>
            {
                if (!injected && args.Action == NotifyCollectionChangedAction.Replace && args.OldItems?.Contains(original) == true)
                {
                    injected = true;
                    throw new InvalidOperationException("Simulated Cut rewiring replacement failure.");
                }
            };
            PropertyChangedEventHandler refreshFailure = (_, args) =>
            {
                if (!injected && args.PropertyName == nameof(NodeViewModel.Data))
                {
                    injected = true;
                    throw new InvalidOperationException("Simulated Cut rewiring refresh failure.");
                }
            };
            if (failRefresh)
                foreach (var node in fixture.Graph.Nodes) node.PropertyChanged += refreshFailure;
            else fixture.Graph.Connections.CollectionChanged += mutationFailure;
            try
            {
                Assert.IsFalse(fixture.Graph.TryRewireQuestConnection(original, endpoint, candidate, out var reason));
                Assert.IsFalse(string.IsNullOrWhiteSpace(reason));
                Assert.IsTrue(injected, "The Cut rewire must reach the requested failure stage.");
            }
            finally
            {
                fixture.Graph.Connections.CollectionChanged -= mutationFailure;
                foreach (var node in fixture.Graph.Nodes) node.PropertyChanged -= refreshFailure;
            }
            before.AssertUnchanged(fixture);
            AssertIntegrity(fixture.Graph, fixture.Data);
            AssertRoundTrips(fixture);
        }
    }

    [TestMethod]
    public void CutNoOpsCapacityAndDuplicatePairsAreNondestructive()
    {
        foreach (var endpoint in new[] { QuestConnectionEndpoint.Source, QuestConnectionEndpoint.Destination })
        foreach (var visualDuplicate in new[] { false, true })
        {
            using var fixture = new Fixture();
            var source = fixture.Add<questCutControlNodeDefinition>();
            var target = fixture.Add<questCutControlNodeDefinition>();
            var alternate = fixture.Add<questCutControlNodeDefinition>();
            var original = fixture.Edge(source, "CutSource", target, "CutDestination");
            BaseConnectorViewModel unchanged = endpoint == QuestConnectionEndpoint.Source ? original.Source : original.Target;
            fixture.Clean();
            var before = new Snapshot(fixture);
            Assert.IsTrue(fixture.Graph.TryRewireQuestConnection(original, endpoint, unchanged, out var reason), reason);
            before.AssertUnchanged(fixture);
            BaseConnectorViewModel candidate = endpoint == QuestConnectionEndpoint.Source
                ? Output(alternate, "CutSource") : Input(alternate, "CutDestination");
            var candidateSocket = candidate is QuestOutputConnectorViewModel output ? output.Data : ((QuestInputConnectorViewModel)candidate).Data;
            candidateSocket.Connections.MaxSize = 0;
            AssertRejected(fixture, original, endpoint, candidate);
            candidateSocket.Connections.MaxSize = 2; // Enough capacity: the next rejection must detect the duplicate pair.
            fixture.Edge(endpoint == QuestConnectionEndpoint.Source ? (QuestOutputConnectorViewModel)candidate : (QuestOutputConnectorViewModel)original.Source,
                endpoint == QuestConnectionEndpoint.Destination ? (QuestInputConnectorViewModel)candidate : (QuestInputConnectorViewModel)original.Target,
                addVisual: visualDuplicate);
            fixture.Clean();
            AssertRejected(fixture, original, endpoint, candidate);
        }
    }

    [TestMethod]
    public void DuplicatePairsAreRejectedEvenWhenOnlyBackingSocketsContainTheOtherEdge()
    {
        foreach (var visualDuplicate in new[] { true, false })
        {
            foreach (var endpoint in new[] { QuestConnectionEndpoint.Source, QuestConnectionEndpoint.Destination })
            {
                using var fixture = new Fixture();
                var a = fixture.Add<questFactsDBManagerNodeDefinition>();
                var b = fixture.Add<questPauseConditionNodeDefinition>();
                var c = fixture.Add<questLogicalHubNodeDefinition>();
                var original = fixture.Edge(a, "Out", b, "In");
                BaseConnectorViewModel target;
                if (endpoint == QuestConnectionEndpoint.Destination)
                {
                    fixture.Edge(Output(a, "Out"), Input(c, "In1"), addVisual: visualDuplicate);
                    target = Input(c, "In1");
                }
                else
                {
                    fixture.Edge(Output(c, "Out1"), Input(b, "In"), addVisual: visualDuplicate);
                    target = Output(c, "Out1");
                }
                fixture.Clean();
                AssertRejected(fixture, original, endpoint, target);
            }
        }
    }

    [TestMethod]
    public void StaleConnectionNodesSocketsDefinitionsAndReciprocityAreRejected()
    {
        foreach (var scenario in new[]
                 {
                     "visual-edge", "source-node", "target-node", "backing-node", "backing-socket",
                     "visual-socket", "definition-source", "definition-target", "missing-reciprocal",
                     "duplicate-reciprocal", "deleted-node", "unbacked-target", "candidate-backing-node",
                     "candidate-backing-socket", "candidate-visual-node", "candidate-visual-socket",
                     "missing-source-reciprocal", "duplicate-source-reciprocal"
                 })
        {
            using var fixture = new Fixture();
            var source = fixture.Add<questFactsDBManagerNodeDefinition>();
            var target = fixture.Add<questPauseConditionNodeDefinition>();
            var alternate = fixture.Add<questFlowControlNodeDefinition>();
            var original = fixture.Edge(source, "Out", target, "In");
            var sourcePort = Output(source, "Out");
            var targetPort = Input(target, "In");
            BaseConnectorViewModel candidate = Input(alternate, "Open");
            switch (scenario)
            {
                case "visual-edge":
                    original = new QuestConnectionViewModel(sourcePort, targetPort, original.ConnectionDefinition);
                    break;
                case "source-node":
                    fixture.Graph.Nodes.Remove(source);
                    break;
                case "target-node":
                    fixture.Graph.Nodes.Remove(target);
                    break;
                case "backing-node":
                    fixture.Data.Nodes.RemoveAt(1);
                    break;
                case "backing-socket":
                    var sockets = ((questNodeDefinition)target.Data).Sockets;
                    sockets.RemoveAt(sockets.ToList().FindIndex(handle => ReferenceEquals(handle.GetValue(), targetPort.Data)));
                    break;
                case "visual-socket":
                    target.Input.Remove(targetPort);
                    break;
                case "definition-source":
                    original.ConnectionDefinition.Source = new CWeakHandle<graphGraphSocketDefinition>(Output(alternate, "Out").Data);
                    break;
                case "definition-target":
                    original.ConnectionDefinition.Destination = new CWeakHandle<graphGraphSocketDefinition>(Input(alternate, "Open").Data);
                    break;
                case "missing-reciprocal":
                    targetPort.Data.Connections.Clear();
                    break;
                case "duplicate-reciprocal":
                    targetPort.Data.Connections.Add(targetPort.Data.Connections.Single());
                    break;
                case "candidate-backing-node":
                    fixture.Data.Nodes.RemoveAt(2);
                    break;
                case "candidate-backing-socket":
                    var candidateSockets = ((questNodeDefinition)alternate.Data).Sockets;
                    candidateSockets.RemoveAt(candidateSockets.ToList().FindIndex(handle =>
                        ReferenceEquals(handle.GetValue(), ((QuestInputConnectorViewModel)candidate).Data)));
                    break;
                case "candidate-visual-node":
                    fixture.Graph.Nodes.Remove(alternate);
                    break;
                case "candidate-visual-socket":
                    alternate.Input.Remove((QuestInputConnectorViewModel)candidate);
                    break;
                case "missing-source-reciprocal":
                    sourcePort.Data.Connections.Clear();
                    break;
                case "duplicate-source-reciprocal":
                    sourcePort.Data.Connections.Add(sourcePort.Data.Connections.Single());
                    break;
                case "deleted-node":
                    fixture.Graph.RemoveNode(target);
                    break;
                case "unbacked-target":
                    var unbacked = new QuestInputConnectorViewModel("Open", "Open", alternate.UniqueId,
                        new questSocketDefinition { Name = "Open", Type = Enums.questSocketType.Input });
                    alternate.Input.Add(unbacked);
                    candidate = unbacked;
                    break;
            }
            fixture.Clean();
            AssertRejected(fixture, original, QuestConnectionEndpoint.Destination, candidate, scenario);
        }
    }

    [TestMethod]
    public void DroppingOnTheUnchangedEndpointIsASuccessfulCleanNoOp()
    {
        using var fixture = new Fixture();
        var source = fixture.Add<questFactsDBManagerNodeDefinition>();
        var target = fixture.Add<questPauseConditionNodeDefinition>();
        var original = fixture.Edge(source, "Out", target, "In");
        original.IsSelected = true;
        fixture.Clean();
        var before = new Snapshot(fixture);
        foreach (var (endpoint, port) in new (QuestConnectionEndpoint, BaseConnectorViewModel)[]
                 {
                     (QuestConnectionEndpoint.Source, original.Source),
                     (QuestConnectionEndpoint.Destination, original.Target)
                 })
        {
            Assert.IsTrue(fixture.Graph.CanRewireQuestConnection(original, endpoint, port, out var reason), reason);
            Assert.IsTrue(fixture.Graph.TryRewireQuestConnection(original, endpoint, port, out reason), reason);
            before.AssertUnchanged(fixture);
        }
    }

    [TestMethod]
    public void RewiringDoesNotInvokeStructuralWorkspotRefreshOrPruneExistingSockets()
    {
        using var fixture = new Fixture();
        var workspot = fixture.Add<questUseWorkspotNodeDefinition>();
        var parameters = new questUseWorkspotParamsV1 { Function = Enums.questUseWorkspotNodeFunctions.UseWorkspot };
        ((questUseWorkspotNodeDefinition)workspot.Data).ParamsV1 = new CHandle<questUseWorkspotParamsV1>(parameters);
        workspot.RefreshFromData();
        var target = fixture.Add<questPauseConditionNodeDefinition>();
        var alternate = fixture.Add<questFactsDBManagerNodeDefinition>();
        var original = fixture.Edge(workspot, "Work Started", target, "In");
        parameters.Function = Enums.questUseWorkspotNodeFunctions.StopWorkspot;
        workspot.RefreshFromData();
        var oldPort = Output(workspot, "Work Started");
        var oldSockets = ((questNodeDefinition)workspot.Data).Sockets.ToArray();
        fixture.Clean();
        var globalNotifications = 0;
        EventHandler<NodePropertyUpdatedEventArgs> structuralRefreshTrap = (_, _) =>
        {
            globalNotifications++;
            throw new InvalidOperationException("A connection-only edit must not refresh structural node configuration.");
        };
        NodePropertyUpdateService.NodePropertyUpdated += structuralRefreshTrap;
        try
        {
            Assert.IsTrue(fixture.Graph.TryRewireQuestConnection(original, QuestConnectionEndpoint.Source,
                Output(alternate, "Out"), out var reason), reason);
        }
        finally
        {
            NodePropertyUpdateService.NodePropertyUpdated -= structuralRefreshTrap;
        }
        Assert.AreEqual(0, globalNotifications);
        AssertReferences(oldSockets, ((questNodeDefinition)workspot.Data).Sockets);
        Assert.IsTrue(workspot.Output.Contains(oldPort));
        Assert.IsFalse(oldPort.IsConnected);
        AssertIntegrity(fixture.Graph, fixture.Data);
    }

    [TestMethod]
    public void MutationAndRefreshFailuresRestoreExactHandlesVisualFlagsAndDirtyState()
    {
        foreach (var failRefresh in new[] { false, true })
        {
            foreach (var initiallyDirty in new[] { false, true })
            {
                foreach (var endpoint in new[] { QuestConnectionEndpoint.Source, QuestConnectionEndpoint.Destination })
                {
                    using var fixture = new Fixture();
                    var source = fixture.Add<questFactsDBManagerNodeDefinition>();
                    var target = fixture.Add<questPauseConditionNodeDefinition>();
                    var alternate = fixture.Add<questFlowControlNodeDefinition>();
                    var journal = fixture.Add<questJournalNodeDefinition>();
                    fixture.Edge(source, "Out", journal, "Active");
                    var original = fixture.Edge(source, "Out", target, "In",
                        new gamestateMachineTransitionDefinition { Priority = 13.5f }, sharedHandle: true);
                    original.IsSelected = true;
                    original.PathType = 1;
                    fixture.Edge(alternate, "Out", journal, "Succeeded");
                    target.ShowUnusedSockets = false;
                    alternate.ShowUnusedSockets = false;
                    target.UpdateSocketVisibility();
                    alternate.UpdateSocketVisibility();
                    fixture.Document.Document.SetIsDirty(initiallyDirty);
                    BaseConnectorViewModel candidate = endpoint == QuestConnectionEndpoint.Source
                        ? Output(alternate, "Out") : Input(alternate, "Switch");
                    var before = new Snapshot(fixture);
                    var injected = false;
                    NotifyCollectionChangedEventHandler mutationFailure = (_, args) =>
                    {
                        if (!injected && args.Action == NotifyCollectionChangedAction.Replace &&
                            args.OldItems?.Contains(original) == true)
                        {
                            injected = true;
                            throw new InvalidOperationException("Simulated rewiring collection replacement failure.");
                        }
                    };
                    PropertyChangedEventHandler refreshFailure = (_, args) =>
                    {
                        if (!injected && args.PropertyName == nameof(NodeViewModel.Data))
                        {
                            injected = true;
                            throw new InvalidOperationException("Simulated rewiring property-panel refresh failure.");
                        }
                    };
                    if (failRefresh)
                    {
                        foreach (var node in fixture.Graph.Nodes) node.PropertyChanged += refreshFailure;
                    }
                    else fixture.Graph.Connections.CollectionChanged += mutationFailure;
                    try
                    {
                        Assert.IsFalse(fixture.Graph.TryRewireQuestConnection(original, endpoint, candidate, out var reason));
                        Assert.IsFalse(string.IsNullOrWhiteSpace(reason));
                        Assert.IsTrue(injected, "The operation must reach the requested mutation/refresh stage.");
                    }
                    finally
                    {
                        fixture.Graph.Connections.CollectionChanged -= mutationFailure;
                        foreach (var node in fixture.Graph.Nodes) node.PropertyChanged -= refreshFailure;
                    }
                    before.AssertUnchanged(fixture);
                    AssertIntegrity(fixture.Graph, fixture.Data);
                    AssertRoundTrips(fixture);
                }
            }
        }
    }

    [TestMethod]
    public void FailingToAddTheMovedHandleDoesNotLeaveAPartialConnection()
    {
        using var fixture = new Fixture();
        var source = fixture.Add<questFactsDBManagerNodeDefinition>();
        var target = fixture.Add<questPauseConditionNodeDefinition>();
        var alternate = fixture.Add<questFlowControlNodeDefinition>();
        var original = fixture.Edge(source, "Out", target, "In");
        var newPort = Input(alternate, "Open");
        newPort.Data.Connections.MaxSize = 0;
        fixture.Clean();
        var before = new Snapshot(fixture);

        Assert.IsFalse(fixture.Graph.TryRewireQuestConnection(original, QuestConnectionEndpoint.Destination, newPort, out var reason));
        Assert.IsFalse(string.IsNullOrWhiteSpace(reason));

        before.AssertUnchanged(fixture);
        AssertIntegrity(fixture.Graph, fixture.Data);
    }

    [TestMethod]
    public void JsonAndBinaryReopenKeepRewiredTopologyConcreteMetadataAndNodeProperties()
    {
        using var fixture = new Fixture();
        var facts = fixture.Add<questFactsDBManagerNodeDefinition>();
        var pause = fixture.Add<questPauseConditionNodeDefinition>();
        var condition = fixture.Add<questConditionNodeDefinition>();
        var flow = fixture.Add<questFlowControlNodeDefinition>();
        var journal = fixture.Add<questJournalNodeDefinition>();
        var factValue = (questSetVar_NodeType)((questFactsDBManagerNodeDefinition)facts.Data).Type.GetValue()!;
        factValue.FactName = "rewire_persistent_fact";
        factValue.Value = 37;
        factValue.SetExactValue = true;
        ((questPauseConditionNodeDefinition)pause.Data).Condition = new CHandle<questIBaseCondition>(
            new questTriggerCondition { TriggerAreaRef = "#rewire_trigger", IsPlayerActivator = true });
        ((questFlowControlNodeDefinition)flow.Data).OpensAt = 7;
        ((questFlowControlNodeDefinition)flow.Data).ClosesAt = 11;
        var first = fixture.Edge(facts, "Out", pause, "In",
            new gamestateMachineTransitionDefinition { Priority = 4.5f });
        var second = fixture.Edge(condition, "True", journal, "Active");
        fixture.Edge(condition, "False", flow, "Close");

        Assert.IsTrue(fixture.Graph.TryRewireQuestConnection(first, QuestConnectionEndpoint.Destination,
            Input(flow, "Switch"), out var reason), reason);
        Assert.IsTrue(fixture.Graph.TryRewireQuestConnection(second, QuestConnectionEndpoint.Source,
            Output(pause, "Out"), out reason), reason);
        Assert.AreSame(first.ConnectionDefinition, ((QuestConnectionViewModel)fixture.Graph.Connections[0]).ConnectionDefinition);

        AssertIntegrity(fixture.Graph, fixture.Data);
        AssertRoundTrips(fixture);
    }

    private static void AssertRejected(Fixture fixture, QuestConnectionViewModel connection,
        QuestConnectionEndpoint endpoint, BaseConnectorViewModel? target, string? context = null)
    {
        var before = new Snapshot(fixture);
        Assert.IsFalse(fixture.Graph.CanRewireQuestConnection(connection, endpoint, target, out var reason), context);
        Assert.IsFalse(string.IsNullOrWhiteSpace(reason), context);
        Assert.IsFalse(fixture.Graph.TryRewireQuestConnection(connection, endpoint, target, out reason), context);
        Assert.IsFalse(string.IsNullOrWhiteSpace(reason), context);
        before.AssertUnchanged(fixture);
    }

    private static QuestInputConnectorViewModel Input(BaseQuestViewModel node, string name) =>
        node.Input.OfType<QuestInputConnectorViewModel>().Single(port => port.Name == name);

    private static QuestOutputConnectorViewModel Output(BaseQuestViewModel node, string name) =>
        node.Output.OfType<QuestOutputConnectorViewModel>().Single(port => port.Name == name);

    private static RedGraph Generate(questGraphDefinition data) =>
        RedGraph.GenerateQuestGraph("rewiring.questphase", data,
            new NodeWrapperFactory(Mock.Of<ILoggerService>(), Mock.Of<IArchiveManager>(), Mock.Of<IGameControllerFactory>()));

    private static void AssertIntegrity(RedGraph graph, questGraphDefinition data)
    {
        var sockets = data.Nodes.SelectMany(handle => ((graphGraphNodeDefinition)handle.GetValue()!).Sockets)
            .Select(handle => (questSocketDefinition)handle.GetValue()!).ToArray();
        foreach (var edge in graph.Connections.OfType<QuestConnectionViewModel>())
        {
            var source = ((QuestOutputConnectorViewModel)edge.Source).Data;
            var target = ((QuestInputConnectorViewModel)edge.Target).Data;
            Assert.IsTrue(sockets.Any(socket => ReferenceEquals(socket, source)));
            Assert.IsTrue(sockets.Any(socket => ReferenceEquals(socket, target)));
            Assert.AreSame(source, edge.ConnectionDefinition.Source.GetValue());
            Assert.AreSame(target, edge.ConnectionDefinition.Destination.GetValue());
            Assert.AreEqual(1, source.Connections.Count(handle => ReferenceEquals(handle.GetValue(), edge.ConnectionDefinition)));
            Assert.AreEqual(1, target.Connections.Count(handle => ReferenceEquals(handle.GetValue(), edge.ConnectionDefinition)));
        }
        foreach (var socket in sockets)
        {
            foreach (var handle in socket.Connections)
            {
                var definition = handle.GetValue();
                Assert.AreEqual(1, graph.Connections.OfType<QuestConnectionViewModel>()
                    .Count(edge => ReferenceEquals(edge.ConnectionDefinition, definition)));
            }
        }
        foreach (var port in graph.Nodes.SelectMany(node => node.Input.Cast<BaseConnectorViewModel>().Concat(node.Output)))
        {
            var socket = port switch
            {
                QuestInputConnectorViewModel input => input.Data,
                QuestOutputConnectorViewModel output => output.Data,
                _ => throw new InvalidOperationException("Unexpected quest connector type.")
            };
            Assert.AreEqual(socket.Connections.Count != 0, port.IsConnected);
        }
    }

    private static void AssertReferences<T>(IEnumerable<T> expected, IEnumerable<T> actual) where T : class
    {
        var expectedArray = expected.ToArray();
        var actualArray = actual.ToArray();
        Assert.AreEqual(expectedArray.Length, actualArray.Length);
        for (var i = 0; i < expectedArray.Length; i++) Assert.AreSame(expectedArray[i], actualArray[i]);
    }

    private static string NormalizedData(questGraphDefinition data)
    {
        using var file = new CR2WFile { RootChunk = new questQuestPhaseResource { Graph = data } };
        var json = JObject.Parse(RedJsonSerializer.Serialize(new RedFileDto(file)));
        return json["Data"]!.ToString(Formatting.None);
    }

    private static string[] Describe(RedGraph graph)
    {
        return graph.Connections.OfType<QuestConnectionViewModel>().Select(edge =>
        {
            var sourceNode = graph.Nodes.Single(node => node.Output.Contains(edge.Source));
            var targetNode = graph.Nodes.Single(node => node.Input.Contains(edge.Target));
            var sourceSocket = ((graphGraphNodeDefinition)sourceNode.Data).Sockets.ToList()
                .FindIndex(handle => ReferenceEquals(handle.GetValue(), ((QuestOutputConnectorViewModel)edge.Source).Data));
            var targetSocket = ((graphGraphNodeDefinition)targetNode.Data).Sockets.ToList()
                .FindIndex(handle => ReferenceEquals(handle.GetValue(), ((QuestInputConnectorViewModel)edge.Target).Data));
            var priority = edge.ConnectionDefinition is gamestateMachineTransitionDefinition transition
                ? ((float)transition.Priority).ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
            return $"{sourceNode.UniqueId}:{sourceSocket}->{targetNode.UniqueId}:{targetSocket}:{edge.ConnectionDefinition.GetType().Name}:{priority}";
        }).ToArray();
    }

    private static void AssertRoundTrips(Fixture fixture)
    {
        using var original = new CR2WFile { RootChunk = new questQuestPhaseResource { Graph = fixture.Data } };
        var dto = RedJsonSerializer.Deserialize<RedFileDto>(RedJsonSerializer.Serialize(new RedFileDto(original)))!;
        using var jsonFile = dto.Data!;
        AssertRestored(jsonFile);
        using var stream = new MemoryStream();
        using (var writer = new CR2WWriter(stream, Encoding.UTF8, true)) writer.WriteFile(original);
        stream.Position = 0;
        using var reader = new CR2WReader(stream, Encoding.UTF8, true);
        Assert.AreEqual(WolvenKit.RED4.Archive.IO.EFileReadErrorCodes.NoError, reader.ReadFile(out var binaryFile));
        using (binaryFile) AssertRestored(binaryFile!);

        void AssertRestored(CR2WFile file)
        {
            var data = (questGraphDefinition)((questQuestPhaseResource)file.RootChunk).Graph.GetValue()!;
            using var graph = Generate(data);
            CollectionAssert.AreEqual(fixture.Graph.Nodes.Select(node => node.UniqueId).ToArray(),
                graph.Nodes.Select(node => node.UniqueId).ToArray());
            CollectionAssert.AreEquivalent(Describe(fixture.Graph), Describe(graph));
            AssertIntegrity(graph, data);
            foreach (var node in fixture.Graph.Nodes)
            {
                var restored = graph.Nodes.Single(candidate => candidate.UniqueId == node.UniqueId);
                Assert.AreEqual(node.Data.GetType(), restored.Data.GetType());
                if (node.Data is questCutControlNodeDefinition cutControl)
                {
                    Assert.AreEqual(cutControl.Permanent, ((questCutControlNodeDefinition)restored.Data).Permanent);
                }
                var expectedSockets = ((questNodeDefinition)node.Data).Sockets
                    .Select(handle => (questSocketDefinition)handle.GetValue()!)
                    .Select(socket => $"{socket.Name}:{socket.Type}").ToArray();
                var restoredSockets = ((questNodeDefinition)restored.Data).Sockets
                    .Select(handle => (questSocketDefinition)handle.GetValue()!)
                    .Select(socket => $"{socket.Name}:{socket.Type}").ToArray();
                CollectionAssert.AreEqual(expectedSockets, restoredSockets, "Socket names, declared types, and order must survive reopening.");
                if (node.Data is questFactsDBManagerNodeDefinition facts)
                {
                    var expected = (questSetVar_NodeType)facts.Type.GetValue()!;
                    var actual = (questSetVar_NodeType)((questFactsDBManagerNodeDefinition)restored.Data).Type.GetValue()!;
                    Assert.AreEqual(expected.FactName, actual.FactName);
                    Assert.AreEqual(expected.Value, actual.Value);
                    Assert.AreEqual(expected.SetExactValue, actual.SetExactValue);
                }
                if (node.Data is questFlowControlNodeDefinition flow)
                {
                    Assert.AreEqual(flow.OpensAt, ((questFlowControlNodeDefinition)restored.Data).OpensAt);
                    Assert.AreEqual(flow.ClosesAt, ((questFlowControlNodeDefinition)restored.Data).ClosesAt);
                }
                if (node.Data is questPauseConditionNodeDefinition { Condition.Chunk: questTriggerCondition trigger })
                {
                    var actual = (questTriggerCondition)((questPauseConditionNodeDefinition)restored.Data).Condition.GetValue()!;
                    Assert.AreEqual(trigger.TriggerAreaRef, actual.TriggerAreaRef);
                    Assert.AreEqual(trigger.IsPlayerActivator, actual.IsPlayerActivator);
                }
            }
        }
    }

    private sealed class Fixture : IDisposable
    {
        public questGraphDefinition Data { get; } = GraphTestHelpers.CreateQuestGraph();
        public RedGraph Graph { get; }
        public GraphDocumentTestFixture Document { get; } = new("mod\\rewiring.questphase");

        public Fixture()
        {
            Graph = Generate(Data);
            Graph.DocumentViewModel = Document.Document;
        }

        public BaseQuestViewModel Add<T>() where T : questNodeDefinition => Add(typeof(T));

        public BaseQuestViewModel Add(Type type)
        {
            var id = Graph.CreateQuestNode(type, new Point(Graph.Nodes.Count * 120, 40));
            return Graph.Nodes.OfType<BaseQuestViewModel>().Single(node => node.UniqueId == id);
        }

        public QuestOutputConnectorViewModel AddOutput(BaseQuestViewModel node, string name,
            Enums.questSocketType type = Enums.questSocketType.Output)
        {
            var data = GraphTestHelpers.AddQuestSocket((graphGraphNodeDefinition)node.Data, name, type);
            var port = new QuestOutputConnectorViewModel(name, name, node.UniqueId, data);
            node.Output.Add(port);
            return port;
        }

        public QuestConnectionViewModel Edge(BaseQuestViewModel source, string output, BaseQuestViewModel target,
            string input, graphGraphConnectionDefinition? definition = null, bool sharedHandle = false) =>
            Edge(Output(source, output), Input(target, input), definition, sharedHandle);

        public QuestConnectionViewModel Edge(QuestOutputConnectorViewModel source, QuestInputConnectorViewModel target,
            graphGraphConnectionDefinition? definition = null, bool sharedHandle = false, bool addVisual = true)
        {
            if (definition is null && !sharedHandle)
            {
                definition = GraphTestHelpers.ConnectQuestSockets(source.Data, target.Data);
            }
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
            if (addVisual) Graph.Connections.Add(edge);
            return edge;
        }

        public void Clean() => Document.Document.SetIsDirty(false);

        public void Dispose()
        {
            Graph.Dispose();
            Document.Dispose();
        }
    }

    private sealed class EdgeHandles
    {
        private readonly QuestConnectionViewModel _edge;
        private readonly CHandle<graphGraphConnectionDefinition> _source;
        private readonly CHandle<graphGraphConnectionDefinition> _target;
        private readonly CWeakHandle<graphGraphSocketDefinition> _sourceEndpoint;
        private readonly CWeakHandle<graphGraphSocketDefinition> _targetEndpoint;

        public EdgeHandles(QuestConnectionViewModel edge)
        {
            _edge = edge;
            _source = ((QuestOutputConnectorViewModel)edge.Source).Data.Connections
                .Single(handle => ReferenceEquals(handle.GetValue(), edge.ConnectionDefinition));
            _target = ((QuestInputConnectorViewModel)edge.Target).Data.Connections
                .Single(handle => ReferenceEquals(handle.GetValue(), edge.ConnectionDefinition));
            _sourceEndpoint = edge.ConnectionDefinition.Source;
            _targetEndpoint = edge.ConnectionDefinition.Destination;
        }

        public void AssertPreserved()
        {
            Assert.AreSame(_source, ((QuestOutputConnectorViewModel)_edge.Source).Data.Connections
                .Single(handle => ReferenceEquals(handle.GetValue(), _edge.ConnectionDefinition)));
            Assert.AreSame(_target, ((QuestInputConnectorViewModel)_edge.Target).Data.Connections
                .Single(handle => ReferenceEquals(handle.GetValue(), _edge.ConnectionDefinition)));
            Assert.AreSame(_sourceEndpoint, _edge.ConnectionDefinition.Source);
            Assert.AreSame(_targetEndpoint, _edge.ConnectionDefinition.Destination);
        }
    }

    private sealed class Snapshot
    {
        private readonly string _json;
        private readonly bool _dirty;
        private readonly NodeViewModel[] _nodes;
        private readonly object[] _canvas;
        private readonly ConnectionViewModel[] _connections;
        private readonly CHandle<graphGraphNodeDefinition>[] _nodeHandles;
        private readonly SocketState[] _sockets;
        private readonly PortState[] _ports;
        private readonly DefinitionState[] _definitions;
        private readonly (QuestConnectionViewModel Edge, bool Selected, int PathType)[] _visualFlags;

        public Snapshot(Fixture fixture)
        {
            _json = NormalizedData(fixture.Data);
            _dirty = fixture.Document.Document.IsDirty;
            _nodes = fixture.Graph.Nodes.ToArray();
            _canvas = fixture.Graph.CanvasItems.ToArray();
            _connections = fixture.Graph.Connections.ToArray();
            _nodeHandles = fixture.Data.Nodes.ToArray();
            _sockets = fixture.Data.Nodes.SelectMany(handle => ((graphGraphNodeDefinition)handle.GetValue()!).Sockets)
                .Select(handle => (questSocketDefinition)handle.GetValue()!)
                .Select(socket => new SocketState(socket, socket.Connections, socket.Connections.ToArray())).ToArray();
            _ports = fixture.Graph.Nodes.SelectMany(node => node.Input.Cast<BaseConnectorViewModel>().Concat(node.Output))
                .Select(port => new PortState(port, port.IsConnected, port.IsVisible)).ToArray();
            _definitions = _sockets.SelectMany(socket => socket.Handles).Select(handle => handle.GetValue())
                .OfType<graphGraphConnectionDefinition>()
                .Distinct((IEqualityComparer<graphGraphConnectionDefinition>)ReferenceEqualityComparer.Instance)
                .Select(definition => new DefinitionState(definition, definition.Source, definition.Destination,
                    definition.Source.GetValue(), definition.Destination.GetValue())).ToArray();
            _visualFlags = fixture.Graph.Connections.OfType<QuestConnectionViewModel>()
                .Select(edge => (edge, edge.IsSelected, edge.PathType)).ToArray();
        }

        public void AssertUnchanged(Fixture fixture)
        {
            Assert.AreEqual(_json, NormalizedData(fixture.Data), "Rejected or failed rewiring changed serialized graph data.");
            Assert.AreEqual(_dirty, fixture.Document.Document.IsDirty);
            AssertReferences(_nodes, fixture.Graph.Nodes);
            AssertReferences(_canvas, fixture.Graph.CanvasItems);
            AssertReferences(_connections, fixture.Graph.Connections);
            AssertReferences(_nodeHandles, fixture.Data.Nodes);
            foreach (var socket in _sockets)
            {
                Assert.AreSame(socket.Collection, socket.Socket.Connections);
                AssertReferences(socket.Handles, socket.Socket.Connections);
            }
            foreach (var port in _ports)
            {
                Assert.AreEqual(port.Connected, port.Port.IsConnected);
                Assert.AreEqual(port.Visible, port.Port.IsVisible);
            }
            foreach (var definition in _definitions)
            {
                Assert.AreSame(definition.Source, definition.Definition.Source);
                Assert.AreSame(definition.Target, definition.Definition.Destination);
                Assert.AreSame(definition.SourceValue, definition.Definition.Source.GetValue());
                Assert.AreSame(definition.TargetValue, definition.Definition.Destination.GetValue());
            }
            foreach (var (edge, selected, pathType) in _visualFlags)
            {
                Assert.AreEqual(selected, edge.IsSelected);
                Assert.AreEqual(pathType, edge.PathType);
            }
        }

        private sealed record SocketState(questSocketDefinition Socket,
            CArray<CHandle<graphGraphConnectionDefinition>> Collection, CHandle<graphGraphConnectionDefinition>[] Handles);
        private sealed record PortState(BaseConnectorViewModel Port, bool Connected, bool Visible);
        private sealed record DefinitionState(graphGraphConnectionDefinition Definition,
            CWeakHandle<graphGraphSocketDefinition> Source, CWeakHandle<graphGraphSocketDefinition> Target,
            RedBaseClass? SourceValue, RedBaseClass? TargetValue);
    }
}
