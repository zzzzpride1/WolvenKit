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
public class QuestConnectionBundleTests
{
    [DataTestMethod]
    [DataRow(QuestConnectionBundleDirection.Outgoing, 3)]
    [DataRow(QuestConnectionBundleDirection.Incoming, 2)]
    [DataRow(QuestConnectionBundleDirection.All, 5)]
    public void CopyReplicatesOnlyTheRequestedDirectionAndPreservesOriginals(
        QuestConnectionBundleDirection direction, int expectedCount)
    {
        using var f = new Fixture();
        var shape = CreateShape(f);
        f.Clean();
        var before = new Snapshot(f);
        var bundle = Capture(f, shape.Anchor, direction, QuestConnectionBundleOperation.Copy);
        Assert.AreEqual(expectedCount, bundle.ConnectionCount);
        Assert.AreEqual(shape.Anchor.UniqueId, bundle.AnchorNodeId);
        Assert.AreEqual(direction, bundle.Direction);
        Assert.AreEqual(QuestConnectionBundleOperation.Copy, bundle.Operation);
        Assert.IsTrue(f.Graph.IsQuestConnectionBundleCurrent(bundle, out var reason), reason);
        Assert.IsTrue(f.Graph.TryPreviewQuestConnectionBundle(bundle, shape.Destination, null, out var preview, out reason), reason);
        Assert.IsNotNull(preview);
        Assert.AreEqual(expectedCount, preview!.Connections.Count);
        Assert.IsFalse(preview.IntroducesSelfConnections);
        Assert.IsTrue(preview.Connections.All(connection => !connection.AlreadyExists));
        before.AssertUnchanged(f);
        Paste(f, bundle, shape.Destination);
        before.AssertOriginalsPreserved(f);
        CollectionAssert.AreEquivalent(ExpectedShape(QuestConnectionBundleOperation.Copy, direction), NamedTopology(f.Graph));
        Assert.AreEqual(9, f.Graph.Nodes.Count, "Copying connections must not clone nodes.");
        Assert.IsFalse(bundle.IsConsumed, "Copy remains reusable.");
        Assert.IsTrue(f.Document.Document.IsDirty);
        AssertIntegrity(f.Graph, f.Data);
    }

    [DataTestMethod]
    [DataRow(QuestConnectionBundleDirection.Outgoing, 3)]
    [DataRow(QuestConnectionBundleDirection.Incoming, 2)]
    [DataRow(QuestConnectionBundleDirection.All, 5)]
    public void CutMovesOnlyTheRequestedDirectionAndPreservesBackingAndVisualIdentity(
        QuestConnectionBundleDirection direction, int expectedCount)
    {
        using var f = new Fixture();
        var shape = CreateShape(f);
        var selected = direction switch
        {
            QuestConnectionBundleDirection.Outgoing => shape.Outgoing,
            QuestConnectionBundleDirection.Incoming => shape.Incoming,
            _ => shape.Incoming.Concat(shape.Outgoing).ToArray()
        };
        foreach (var edge in selected) { edge.IsSelected = true; edge.PathType = 1; }
        var originals = f.Graph.Connections.OfType<QuestConnectionViewModel>().Select(edge => new EdgeState(f.Graph, edge)).ToArray();
        f.Clean();
        var before = new Snapshot(f);
        var bundle = Capture(f, shape.Anchor, direction, QuestConnectionBundleOperation.Cut);
        Assert.AreEqual(expectedCount, bundle.ConnectionCount);
        Assert.IsFalse(bundle.IsConsumed);
        before.AssertUnchanged(f);
        Paste(f, bundle, shape.Destination);
        before.AssertNodeStructurePreserved(f);
        foreach (var state in originals)
        {
            if (selected.Any(edge => ReferenceEquals(edge, state.Edge)))
                state.AssertMoved(f.Graph,
                    state.Edge.Source.OwnerId == shape.Anchor.UniqueId ? Out(shape.Destination) : (QuestOutputConnectorViewModel)state.Edge.Source,
                    state.Edge.Target.OwnerId == shape.Anchor.UniqueId ? In(shape.Destination) : (QuestInputConnectorViewModel)state.Edge.Target);
            else state.AssertUnchanged(f.Graph);
        }
        CollectionAssert.AreEquivalent(ExpectedShape(QuestConnectionBundleOperation.Cut, direction), NamedTopology(f.Graph));
        Assert.IsTrue(bundle.IsConsumed);
        Assert.IsTrue(f.Document.Document.IsDirty);
        AssertIntegrity(f.Graph, f.Data);
    }


    [TestMethod]
    public void ThreeNamedOutputsMapToTheirCorrespondingDestinationSockets()
    {
        using var f = new Fixture();
        var anchor = f.Add<questFactsDBManagerNodeDefinition>();
        var destination = f.Add<questFactsDBManagerNodeDefinition>();
        var outputs = new[] { f.AddOutput(anchor, "Out1"), f.AddOutput(anchor, "Out2"), f.AddOutput(anchor, "Out3") };
        foreach (var name in new[] { "Out3", "Out1", "Out2" }) f.AddOutput(destination, name);
        var targets = new[] { In(f.Add<questPauseConditionNodeDefinition>()), In(f.Add<questConditionNodeDefinition>()), In(f.Add<questEventManagerNodeDefinition>()) };
        for (var i = 0; i < outputs.Length; i++) f.Edge(outputs[i], targets[i]);
        f.Clean();
        var before = new Snapshot(f);
        var bundle = Capture(f, anchor, QuestConnectionBundleDirection.Outgoing);

        Assert.IsTrue(f.Graph.TryGetQuestConnectionBundleSocketMappings(bundle, destination, out var mappings, out var reason), reason);
        Assert.AreEqual(3, mappings.Count);
        foreach (var mapping in mappings)
        {
            Assert.IsTrue(mapping.Socket.IsOutput);
            Assert.AreSame(Out(destination, mapping.Socket.Name), mapping.Suggested);
        }
        before.AssertUnchanged(f);
        Paste(f, bundle, destination);

        for (var i = 0; i < outputs.Length; i++)
            Assert.AreEqual(1, f.Graph.Connections.Count(edge => ReferenceEquals(edge.Source, Out(destination, $"Out{i + 1}")) && ReferenceEquals(edge.Target, targets[i])));
        Assert.AreEqual(0, Out(destination).Data.Connections.Count, "Do not collapse branches onto the first output.");
        before.AssertOriginalsPreserved(f);
        AssertIntegrity(f.Graph, f.Data);
    }

    [TestMethod]
    public void AmbiguousMissingAndCollapsingMappingsRejectUntilAnExplicitDistinctMappingIsProvided()
    {
        using var f = new Fixture();
        var anchor = f.Add<questFactsDBManagerNodeDefinition>();
        var first = f.AddOutput(anchor, "branch");
        var second = f.AddOutput(anchor, "branch");
        var destination = f.Add<questConditionNodeDefinition>();
        var missing = f.Add<questOutputNodeDefinition>();
        var firstTarget = In(f.Add<questPauseConditionNodeDefinition>());
        var secondTarget = In(f.Add<questEventManagerNodeDefinition>());
        f.Edge(first, firstTarget);
        f.Edge(second, secondTarget);
        f.Clean();
        var bundle = Capture(f, anchor, QuestConnectionBundleDirection.Outgoing);
        Assert.AreEqual(2, bundle.Sockets.Count);
        Assert.AreNotSame(bundle.Sockets[0], bundle.Sockets[1]);
        Assert.IsTrue(f.Graph.TryGetQuestConnectionBundleSocketMappings(bundle, destination, out var choices, out var reason), reason);
        Assert.IsTrue(choices.All(choice => choice.Suggested is null && choice.Choices.Count == 2));
        RejectPaste(f, bundle, destination);
        RejectPaste(f, bundle, missing);

        var firstDestinationPort = Out(destination, "False");
        var alias = new QuestOutputConnectorViewModel("False", "False", destination.UniqueId, firstDestinationPort.Data);
        destination.Output.Add(alias);
        var collapsed = new Dictionary<QuestConnectionBundleSocket, BaseConnectorViewModel>
        {
            [bundle.Sockets[0]] = firstDestinationPort, [bundle.Sockets[1]] = alias
        };
        RejectPaste(f, bundle, destination, collapsed);
        var wrongDirection = new Dictionary<QuestConnectionBundleSocket, BaseConnectorViewModel>
        {
            [bundle.Sockets[0]] = In(destination), [bundle.Sockets[1]] = Out(destination, "True")
        };
        RejectPaste(f, bundle, destination, wrongDirection);
        var explicitMapping = new Dictionary<QuestConnectionBundleSocket, BaseConnectorViewModel>
        {
            [bundle.Sockets[0]] = firstDestinationPort, [bundle.Sockets[1]] = Out(destination, "True")
        };
        Paste(f, bundle, destination, explicitMapping);
        Assert.AreEqual(1, firstDestinationPort.Data.Connections.Count);
        Assert.AreEqual(1, Out(destination, "True").Data.Connections.Count);
        CollectionAssert.AreEquivalent(new[] { firstTarget.OwnerId, secondTarget.OwnerId }, f.Graph.Connections
            .Where(edge => edge.Source.OwnerId == destination.UniqueId).Select(edge => edge.Target.OwnerId).ToArray());
        AssertIntegrity(f.Graph, f.Data);
    }

    [TestMethod]
    public void ExistingPairsParallelGroupsAndOccupiedInputsKeepEveryExistingIdentity()
    {
        using var f = new Fixture();
        var anchor = f.Add<questFactsDBManagerNodeDefinition>();
        var source = Out(anchor);
        f.AddOutput(anchor, "Out"); // Exact identity must win when pasting back onto this same node.
        var destination = f.Add<questPauseConditionNodeDefinition>();
        var fresh = f.Add<questFactsDBManagerNodeDefinition>();
        var moveTarget = f.Add<questFactsDBManagerNodeDefinition>();
        var b = In(f.Add<questConditionNodeDefinition>());
        var c = In(f.Add<questEventManagerNodeDefinition>());
        var other = Out(f.Add<questPauseConditionNodeDefinition>());
        f.Edge(source, b, true, new gamestateMachineTransitionDefinition { Priority = 2.5f });
        f.Edge(source, b);
        f.Edge(source, c);
        var satisfied = f.Edge(Out(destination), b, true);
        var occupied = f.Edge(other, c);
        f.Clean();
        var bundle = Capture(f, anchor, QuestConnectionBundleDirection.Outgoing);
        var before = new Snapshot(f);
        Paste(f, bundle, anchor);
        before.AssertUnchanged(f);

        Paste(f, bundle, destination);
        before.AssertOriginalsPreserved(f);
        Assert.AreEqual(6, f.Graph.Connections.Count, "The existing B pair satisfies the whole parallel group; only C is added.");
        Assert.AreSame(satisfied, f.Graph.Connections[3]);
        Assert.AreSame(occupied, f.Graph.Connections[4]);
        Assert.AreEqual(3, c.Data.Connections.Count, "Preserve the occupied input's unrelated incoming edge.");
        f.Clean();
        var repeat = new Snapshot(f);
        Paste(f, bundle, destination);
        repeat.AssertUnchanged(f);

        Paste(f, bundle, fresh);
        Assert.AreEqual(2, Out(fresh).Data.Connections.Count(handle => ReferenceEquals(((graphGraphConnectionDefinition)handle.GetValue()!).Destination.GetValue(), b.Data)));
        Assert.AreEqual(9, f.Graph.Connections.Count, "A new pair reproduces intentional parallel records.");
        var cut = Capture(f, anchor, QuestConnectionBundleDirection.Outgoing, QuestConnectionBundleOperation.Cut);
        RejectPaste(f, cut, destination);
        Assert.IsFalse(cut.IsConsumed, "A move collision must not discard the original identities.");
        Paste(f, cut, moveTarget);
        Assert.AreEqual(0, source.Data.Connections.Count);
        Assert.AreEqual(3, Out(moveTarget).Data.Connections.Count);
        Assert.AreEqual(9, f.Graph.Connections.Count);
        AssertIntegrity(f.Graph, f.Data);
    }

    [TestMethod]
    public void PendingCutIsNondestructiveAndIsConsumedOnlyAfterSuccessfulPaste()
    {
        using var f = new Fixture();
        var anchor = f.Add<questFactsDBManagerNodeDefinition>();
        var destination = f.Add<questPauseConditionNodeDefinition>();
        var missing = f.Add<questOutputNodeDefinition>();
        f.Edge(Out(anchor), In(f.Add<questConditionNodeDefinition>()));
        f.Clean();
        var before = new Snapshot(f);
        var cancelled = Capture(f, anchor, QuestConnectionBundleDirection.Outgoing, QuestConnectionBundleOperation.Cut);
        before.AssertUnchanged(f);
        cancelled.Cancel();
        Assert.IsTrue(cancelled.IsCancelled);
        Assert.IsFalse(cancelled.IsConsumed);
        Assert.IsFalse(f.Graph.IsQuestConnectionBundleCurrent(cancelled, out var reason));
        Assert.IsFalse(string.IsNullOrWhiteSpace(reason));
        RejectPaste(f, cancelled, destination);
        before.AssertUnchanged(f);

        var cut = Capture(f, anchor, QuestConnectionBundleDirection.Outgoing, QuestConnectionBundleOperation.Cut);
        RejectPaste(f, cut, missing);
        Assert.IsFalse(cut.IsConsumed);
        Assert.IsTrue(f.Graph.IsQuestConnectionBundleCurrent(cut, out reason), reason);
        before.AssertUnchanged(f);
        Paste(f, cut, destination);
        Assert.IsTrue(cut.IsConsumed);
        RejectPaste(f, cut, anchor);
        Assert.IsFalse(f.Graph.IsQuestConnectionBundleCurrent(cut, out _));
        AssertIntegrity(f.Graph, f.Data);
    }


    [TestMethod]
    public void CapturedConnectionsRejectStaleTopologyAndMetadataButAllowUnrelatedNodeProperties()
    {
        foreach (var change in new[]
                 {
                     "visual-node", "backing-node", "visual-port", "backing-socket", "anchor-layout", "external-layout",
                     "reciprocal", "reciprocal-wrapper", "visual-edge", "endpoint-wrapper", "priority", "dynamic", "payload"
                 })
        {
            using var f = new Fixture();
            var anchor = f.Add<questFactsDBManagerNodeDefinition>();
            var destination = f.Add<questPauseConditionNodeDefinition>();
            var target = f.Add<questConditionNodeDefinition>();
            var payload = new questTriggerCondition { TriggerAreaRef = "#original_payload" };
            var definition = new gamestateMachineTransitionDefinition { Priority = 7.25f };
            AddMetadata(definition, payload, "original");
            var edge = f.Edge(Out(anchor), In(target), definition: definition);
            var bundle = Capture(f, anchor, QuestConnectionBundleDirection.Outgoing);
            ((questSetVar_NodeType)((questFactsDBManagerNodeDefinition)anchor.Data).Type.GetValue()!).Value = 93;
            Assert.IsTrue(f.Graph.IsQuestConnectionBundleCurrent(bundle, out var reason), $"Unrelated properties: {reason}");
            switch (change)
            {
                case "visual-node": f.Graph.Nodes.Remove(anchor); break;
                case "backing-node": f.Data.Nodes.RemoveAt(0); break;
                case "visual-port": anchor.Output.Remove((QuestOutputConnectorViewModel)edge.Source); break;
                case "backing-socket":
                    var sockets = ((questNodeDefinition)anchor.Data).Sockets;
                    sockets.RemoveAt(sockets.ToList().FindIndex(handle => ReferenceEquals(handle.GetValue(), Socket(edge.Source))));
                    break;
                case "anchor-layout": f.AddOutput(anchor, "later"); break;
                case "external-layout": f.AddInput(target, "later"); break;
                case "reciprocal": In(target).Data.Connections.Clear(); break;
                case "reciprocal-wrapper": In(target).Data.Connections[0] = new CHandle<graphGraphConnectionDefinition>(definition); break;
                case "visual-edge": f.Graph.Connections.Remove(edge); break;
                case "endpoint-wrapper": definition.Source = new CWeakHandle<graphGraphSocketDefinition>(Socket(edge.Source)); break;
                case "priority": definition.Priority = 8.25f; break;
                case "dynamic": definition.SetProperty("bundleLabel", (CString)"changed"); break;
                case "payload": payload.TriggerAreaRef = "#changed_payload"; break;
            }
            f.Clean();
            var before = new Snapshot(f);
            Assert.IsFalse(f.Graph.IsQuestConnectionBundleCurrent(bundle, out reason), change);
            Assert.IsFalse(string.IsNullOrWhiteSpace(reason), change);
            RejectPaste(f, bundle, destination);
            before.AssertUnchanged(f);
        }
    }

    [TestMethod]
    public void ScopeAndLifetimeChecksRejectForeignReconstructedAndDisposedGraphsWhileNestedSameScopeWorks()
    {
        using var outer = new Fixture();
        using var child = new Fixture();
        using var foreign = new Fixture();
        var phase = outer.Add<questPhaseNodeDefinition>();
        ((questPhaseNodeDefinition)phase.Data).PhaseGraph = child.Data;
        child.Graph.StateParents = "/phase/1";
        var anchor = child.Add<questFactsDBManagerNodeDefinition>();
        var destination = child.Add<questPauseConditionNodeDefinition>();
        child.Edge(Out(anchor), In(child.Add<questConditionNodeDefinition>()));
        var foreignNode = foreign.Add<questPauseConditionNodeDefinition>();
        var outerNode = outer.Add<questPauseConditionNodeDefinition>();
        child.Clean(); outer.Clean(); foreign.Clean();
        var bundle = Capture(child, anchor, QuestConnectionBundleDirection.Outgoing);
        var outerBefore = new Snapshot(outer);
        var foreignBefore = new Snapshot(foreign);
        RejectPaste(child, bundle, foreignNode);
        RejectPaste(child, bundle, outerNode);
        Assert.IsFalse(outer.Graph.TryPasteQuestConnectionBundle(bundle, outerNode, null, out var reason));
        Assert.IsFalse(string.IsNullOrWhiteSpace(reason));
        outerBefore.AssertUnchanged(outer);
        foreignBefore.AssertUnchanged(foreign);
        using (var reconstructed = Generate(child.Data))
        {
            var replacementDestination = reconstructed.Nodes.Single(node => node.UniqueId == destination.UniqueId);
            Assert.IsFalse(reconstructed.TryPasteQuestConnectionBundle(bundle, replacementDestination, null, out reason));
            Assert.IsFalse(string.IsNullOrWhiteSpace(reason));
        }
        Paste(child, bundle, destination);
        AssertIntegrity(child.Graph, child.Data);
        var pending = Capture(child, anchor, QuestConnectionBundleDirection.Outgoing, QuestConnectionBundleOperation.Cut);
        child.Graph.Dispose();
        var disposedBefore = new Snapshot(child);
        Assert.IsFalse(child.Graph.IsQuestConnectionBundleCurrent(pending, out reason));
        Assert.IsFalse(string.IsNullOrWhiteSpace(reason));
        RejectPaste(child, pending, destination);
        disposedBefore.AssertUnchanged(child);
        Assert.IsFalse(pending.IsConsumed);
    }

    [TestMethod]
    public void OrdinaryBundlesIgnoreCutEdgesAndRejectCutOnlyAndSyntheticTransfers()
    {
        foreach (var operation in new[] { QuestConnectionBundleOperation.Copy, QuestConnectionBundleOperation.Cut })
        {
            using var f = new Fixture();
            var anchor = f.Add<questCutControlNodeDefinition>();
            var destination = f.Add<questCutControlNodeDefinition>();
            var cutTarget = f.Add<questCutControlNodeDefinition>();
            var ordinary = f.Edge(Out(anchor), In(f.Add<questPauseConditionNodeDefinition>()));
            var cutEdge = f.Edge(Out(anchor, "CutSource"), In(cutTarget, "CutDestination"), true);
            var cutState = new EdgeState(f.Graph, cutEdge);
            f.Clean();
            var bundle = Capture(f, anchor, QuestConnectionBundleDirection.All, operation);
            Assert.AreEqual(1, bundle.ConnectionCount);
            var badMapping = new Dictionary<QuestConnectionBundleSocket, BaseConnectorViewModel>
            {
                [bundle.Sockets.Single()] = Out(destination, "CutSource")
            };
            RejectPaste(f, bundle, destination, badMapping);
            Paste(f, bundle, destination);
            cutState.AssertUnchanged(f.Graph);
            Assert.AreSame(cutEdge.ConnectionDefinition, Out(anchor, "CutSource").Data.Connections.Single().GetValue());
            Assert.AreSame(ordinary.ConnectionDefinition, operation == QuestConnectionBundleOperation.Cut
                ? Out(destination).Data.Connections.Single().GetValue() : Out(anchor).Data.Connections.Single().GetValue());
            AssertIntegrity(f.Graph, f.Data);
        }
        using var unsupported = new Fixture();
        var cutOnly = unsupported.Add<questCutControlNodeDefinition>();
        var otherCut = unsupported.Add<questCutControlNodeDefinition>();
        unsupported.Edge(Out(cutOnly, "CutSource"), In(otherCut, "CutDestination"));
        var boundary = unsupported.Add<questOutputNodeDefinition>();
        var synthetic = boundary.Output.OfType<QuestOutputConnectorViewModel>().Single();
        unsupported.Edge(synthetic, In(unsupported.Add<questPauseConditionNodeDefinition>()));
        unsupported.Clean();
        var before = new Snapshot(unsupported);
        foreach (var anchor in new[] { cutOnly, boundary })
        {
            Assert.IsFalse(unsupported.Graph.TryCaptureQuestConnectionBundle(anchor, QuestConnectionBundleDirection.Outgoing,
                QuestConnectionBundleOperation.Cut, out var bundle, out var reason));
            Assert.IsNull(bundle);
            Assert.IsFalse(string.IsNullOrWhiteSpace(reason));
            before.AssertUnchanged(unsupported);
        }
    }

    [TestMethod]
    public void NewSelfConnectionsRequireExplicitConsentWhileOriginalSelfLoopsRemainValid()
    {
        using var f = new Fixture();
        var anchor = f.Add<questFactsDBManagerNodeDefinition>();
        var destination = f.Add<questPauseConditionNodeDefinition>();
        f.Edge(Out(anchor), In(destination));
        f.Clean();
        var bundle = Capture(f, anchor, QuestConnectionBundleDirection.Outgoing);
        var before = new Snapshot(f);
        Assert.IsTrue(f.Graph.TryPreviewQuestConnectionBundle(bundle, destination, null, out var preview, out var reason), reason);
        Assert.IsTrue(preview!.IntroducesSelfConnections);
        before.AssertUnchanged(f);
        RejectPaste(f, bundle, destination);
        Paste(f, bundle, destination, allowNewSelfConnections: true);
        Assert.AreEqual(1, f.Graph.Connections.Count(edge => edge.Source.OwnerId == destination.UniqueId && edge.Target.OwnerId == destination.UniqueId));
        AssertIntegrity(f.Graph, f.Data);
        f.Clean();
        var satisfied = new Snapshot(f);
        Assert.IsTrue(f.Graph.TryPreviewQuestConnectionBundle(bundle, destination, null, out var repeat, out reason), reason);
        Assert.IsFalse(repeat!.IntroducesSelfConnections, "An existing pair is skipped and must not prompt as a newly introduced self-link.");
        Paste(f, bundle, destination);
        satisfied.AssertUnchanged(f);

        foreach (var operation in new[] { QuestConnectionBundleOperation.Copy, QuestConnectionBundleOperation.Cut })
        {
            using var loop = new Fixture();
            var oldNode = loop.Add<questFactsDBManagerNodeDefinition>();
            var newNode = loop.Add<questPauseConditionNodeDefinition>();
            loop.Edge(Out(oldNode), In(oldNode));
            loop.Clean();
            var selfBundle = Capture(loop, oldNode, QuestConnectionBundleDirection.All, operation);
            Assert.AreEqual(1, selfBundle.ConnectionCount, "A self-loop is captured once even though both ends belong to the anchor.");
            Paste(loop, selfBundle, newNode);
            Assert.AreEqual(operation == QuestConnectionBundleOperation.Copy ? 2 : 1, loop.Graph.Connections.Count);
            Assert.AreEqual(1, loop.Graph.Connections.Count(edge => edge.Source.OwnerId == newNode.UniqueId && edge.Target.OwnerId == newNode.UniqueId));
            AssertIntegrity(loop.Graph, loop.Data);
        }
    }

    [TestMethod]
    public void CapacityValidationCountsTheWholeBatchAndCreditsMovedHandles()
    {
        using var f = new Fixture();
        var anchor = f.Add<questFactsDBManagerNodeDefinition>();
        var destination = f.Add<questPauseConditionNodeDefinition>();
        var b = In(f.Add<questConditionNodeDefinition>());
        var c = In(f.Add<questEventManagerNodeDefinition>());
        f.Edge(Out(anchor), b);
        f.Edge(Out(anchor), c);
        Out(destination).Data.Connections.MaxSize = 1;
        b.Data.Connections.MaxSize = 1;
        c.Data.Connections.MaxSize = 1;
        f.Clean();
        var copy = Capture(f, anchor, QuestConnectionBundleDirection.Outgoing);
        RejectPaste(f, copy, destination);
        Out(destination).Data.Connections.MaxSize = 2;
        RejectPaste(f, copy, destination); // Fixed inputs cannot accept the two additional Copy handles.
        var cut = Capture(f, anchor, QuestConnectionBundleDirection.Outgoing, QuestConnectionBundleOperation.Cut);
        Paste(f, cut, destination);
        Assert.AreEqual(2, Out(destination).Data.Connections.Count);
        Assert.AreEqual(0, Out(anchor).Data.Connections.Count);
        Assert.AreEqual(1, b.Data.Connections.Count);
        Assert.AreEqual(1, c.Data.Connections.Count);
        AssertIntegrity(f.Graph, f.Data);
    }


    [TestMethod]
    public void CopyClonesConcreteDynamicAndSharedMetadataAndCutNeverRetargetsAliasedWeakWrappers()
    {
        using var f = new Fixture();
        var anchor = f.Add<questFactsDBManagerNodeDefinition>();
        var destination = f.Add<questPauseConditionNodeDefinition>();
        var moveTarget = f.Add<questFactsDBManagerNodeDefinition>();
        var payload = new questTriggerCondition { TriggerAreaRef = "#bundle_shared_payload", IsPlayerActivator = true };
        var definitions = new[]
        {
            new gamestateMachineTransitionDefinition { Priority = 2.5f },
            new gamestateMachineTransitionDefinition { Priority = 8.75f }
        };
        AddMetadata(definitions[0], payload, "first");
        AddMetadata(definitions[1], payload, "second");
        var a = f.Edge(Out(anchor), In(f.Add<questConditionNodeDefinition>()), true, definitions[0]);
        var b = f.Edge(Out(anchor), In(f.Add<questEventManagerNodeDefinition>()), definition: definitions[1]);
        definitions[1].Source = definitions[0].Source;
        var alias = definitions[0].Source;
        var fixedEndpoints = definitions.Select(definition => definition.Destination).ToArray();
        var originalStates = new[] { new EdgeState(f.Graph, a), new EdgeState(f.Graph, b) };
        f.Clean();
        var copy = Capture(f, anchor, QuestConnectionBundleDirection.Outgoing);
        Paste(f, copy, destination);

        var clones = f.Graph.Connections.OfType<QuestConnectionViewModel>().Where(edge => edge.Source.OwnerId == destination.UniqueId).ToArray();
        Assert.AreEqual(2, clones.Length);
        for (var i = 0; i < clones.Length; i++)
        {
            Assert.AreNotSame(definitions[i], clones[i].ConnectionDefinition);
            Assert.IsInstanceOfType<gamestateMachineTransitionDefinition>(clones[i].ConnectionDefinition);
            Assert.AreEqual((float)definitions[i].Priority, (float)((gamestateMachineTransitionDefinition)clones[i].ConnectionDefinition).Priority);
            Assert.AreEqual(definitions[i].GetProperty("bundleLabel"), clones[i].ConnectionDefinition.GetProperty("bundleLabel"));
            Assert.AreNotSame(definitions[i].Source, clones[i].ConnectionDefinition.Source);
            Assert.AreNotSame(definitions[i].Destination, clones[i].ConnectionDefinition.Destination);
            Assert.AreNotSame(HandleFor(Socket(originalStates[i].Edge.Source), definitions[i]), HandleFor(Socket(clones[i].Source), clones[i].ConnectionDefinition));
            Assert.AreNotSame(HandleFor(Socket(originalStates[i].Edge.Target), definitions[i]), HandleFor(Socket(clones[i].Target), clones[i].ConnectionDefinition));
            originalStates[i].AssertUnchanged(f.Graph);
        }
        var firstPayload = (CHandle<questIBaseCondition>)clones[0].ConnectionDefinition.GetProperty("bundlePayload")!;
        var secondPayload = (CHandle<questIBaseCondition>)clones[1].ConnectionDefinition.GetProperty("bundlePayload")!;
        Assert.AreSame(firstPayload.GetValue(), secondPayload.GetValue(), "Shared metadata inside one bundle must stay shared.");
        Assert.AreNotSame(payload, firstPayload.GetValue(), "A copy must not share mutable metadata with the original.");
        ((questTriggerCondition)firstPayload.GetValue()!).TriggerAreaRef = "#copied_payload";
        Assert.AreEqual((NodeRef)"#bundle_shared_payload", payload.TriggerAreaRef);
        Assert.AreEqual((NodeRef)"#copied_payload", ((questTriggerCondition)secondPayload.GetValue()!).TriggerAreaRef);

        var cut = Capture(f, anchor, QuestConnectionBundleDirection.Outgoing, QuestConnectionBundleOperation.Cut);
        Paste(f, cut, moveTarget);
        Assert.AreSame(Out(anchor).Data, alias.GetValue(), "Replacing endpoint fields must not mutate a wrapper that another reference may hold.");
        for (var i = 0; i < definitions.Length; i++)
        {
            Assert.AreNotSame(alias, definitions[i].Source);
            Assert.AreSame(fixedEndpoints[i], definitions[i].Destination);
            originalStates[i].AssertMoved(f.Graph, Out(moveTarget), (QuestInputConnectorViewModel)originalStates[i].Edge.Target);
        }
        AssertIntegrity(f.Graph, f.Data);
    }

    [TestMethod]
    public void SecondMutationAndLateRefreshFailuresRestoreTheWholeBundleAndDirtyState()
    {
        foreach (var operation in new[] { QuestConnectionBundleOperation.Copy, QuestConnectionBundleOperation.Cut })
        foreach (var lateRefresh in new[] { false, true })
        foreach (var initiallyDirty in new[] { false, true })
        {
            using var f = new Fixture();
            var anchor = f.Add<questFactsDBManagerNodeDefinition>();
            var destination = f.Add<questPauseConditionNodeDefinition>();
            var firstTarget = f.Add<questConditionNodeDefinition>();
            var secondTarget = f.Add<questEventManagerNodeDefinition>();
            var unrelated = f.Add<questJournalNodeDefinition>();
            f.Edge(Out(secondTarget), In(unrelated, "Active"));
            var first = f.Edge(Out(anchor), In(firstTarget), true, new gamestateMachineTransitionDefinition { Priority = 4.25f });
            first.IsSelected = true; first.PathType = 1;
            f.Edge(Out(anchor), In(secondTarget));
            destination.ShowUnusedSockets = false;
            destination.UpdateSocketVisibility();
            var bundle = Capture(f, anchor, QuestConnectionBundleDirection.Outgoing, operation);
            f.Document.Document.SetIsDirty(initiallyDirty);
            var before = new Snapshot(f);
            var mutations = 0;
            var injected = false;
            var movedHandlesAtFailure = 0;
            NotifyCollectionChangedEventHandler mutationFailure = (_, args) =>
            {
                var expected = operation == QuestConnectionBundleOperation.Copy ? NotifyCollectionChangedAction.Add : NotifyCollectionChangedAction.Replace;
                if (!injected && args.Action == expected && ++mutations == 2)
                {
                    injected = true;
                    movedHandlesAtFailure = Out(destination).Data.Connections.Count;
                    throw new InvalidOperationException("Fail after the second complete bundle mutation.");
                }
            };
            PropertyChangedEventHandler refreshFailure = (_, args) =>
            {
                if (!injected && args.PropertyName == nameof(NodeViewModel.Data))
                {
                    injected = true;
                    movedHandlesAtFailure = Out(destination).Data.Connections.Count;
                    throw new InvalidOperationException("Fail after all bundle edges were committed, during Data refresh.");
                }
            };
            if (lateRefresh) foreach (var node in f.Graph.Nodes) node.PropertyChanged += refreshFailure;
            else f.Graph.Connections.CollectionChanged += mutationFailure;
            try
            {
                Assert.IsFalse(f.Graph.TryPasteQuestConnectionBundle(bundle, destination, null, out var reason));
                Assert.IsFalse(string.IsNullOrWhiteSpace(reason));
                Assert.IsTrue(injected, "The test must reach its requested failure stage rather than an early validation rejection.");
                Assert.AreEqual(2, movedHandlesAtFailure);
            }
            finally
            {
                f.Graph.Connections.CollectionChanged -= mutationFailure;
                foreach (var node in f.Graph.Nodes) node.PropertyChanged -= refreshFailure;
            }
            before.AssertUnchanged(f);
            Assert.IsFalse(bundle.IsConsumed);
            Assert.IsTrue(f.Graph.IsQuestConnectionBundleCurrent(bundle, out var currentReason), currentReason);
            AssertIntegrity(f.Graph, f.Data);
            Paste(f, bundle, destination);
            AssertIntegrity(f.Graph, f.Data);
        }
    }

    [TestMethod]
    public void WorkspotSocketStructureAndJsonAndCr2wReopenSurviveCopyAndMoveWithoutGlobalRefresh()
    {
        using var f = new Fixture();
        var anchor = Workspot(f, "#bundle_source");
        var destination = Workspot(f, "#bundle_copy");
        var moveTarget = Workspot(f, "#bundle_move");
        var unrelated = Workspot(f, "#bundle_unrelated");
        var facts = f.Add<questFactsDBManagerNodeDefinition>();
        var pause = f.Add<questPauseConditionNodeDefinition>();
        var flow = f.Add<questFlowControlNodeDefinition>();
        var value = (questSetVar_NodeType)((questFactsDBManagerNodeDefinition)facts.Data).Type.GetValue()!;
        value.FactName = "bundle_persistent_fact"; value.Value = 61; value.SetExactValue = true;
        ((questPauseConditionNodeDefinition)pause.Data).Condition = new CHandle<questIBaseCondition>(
            new questTriggerCondition { TriggerAreaRef = "#bundle_trigger", IsPlayerActivator = true });
        ((questFlowControlNodeDefinition)flow.Data).OpensAt = 7;
        ((questFlowControlNodeDefinition)flow.Data).ClosesAt = 11;
        var legacy = Out(anchor, "Work Started");
        f.Edge(legacy, In(pause), true, new gamestateMachineTransitionDefinition { Priority = 9.5f });
        f.Edge(Out(facts), In(flow, "Switch"));
        ((questUseWorkspotNodeDefinition)anchor.Data).ParamsV1.Chunk!.Function = Enums.questUseWorkspotNodeFunctions.StopWorkspot;
        anchor.RefreshFromData(); // Its connected legacy output remains live.
        ((questUseWorkspotNodeDefinition)unrelated.Data).ParamsV1.Chunk!.Function = Enums.questUseWorkspotNodeFunctions.StopWorkspot;
        var structure = new Snapshot(f);
        f.Clean();
        var globalRefreshes = 0;
        EventHandler<NodePropertyUpdatedEventArgs> trap = (_, _) =>
        {
            globalRefreshes++;
            throw new InvalidOperationException("Connection bundles must refresh without regenerating socket structure.");
        };
        NodePropertyUpdateService.NodePropertyUpdated += trap;
        try
        {
            Paste(f, Capture(f, anchor, QuestConnectionBundleDirection.Outgoing), destination);
            Paste(f, Capture(f, anchor, QuestConnectionBundleDirection.Outgoing, QuestConnectionBundleOperation.Cut), moveTarget);
        }
        finally { NodePropertyUpdateService.NodePropertyUpdated -= trap; }
        Assert.AreEqual(0, globalRefreshes);
        structure.AssertNodeStructurePreserved(f);
        Assert.AreSame(legacy, Out(anchor, "Work Started"));
        Assert.IsFalse(legacy.IsConnected);
        Assert.IsNotNull(Out(unrelated, "Work Started"), "Unrelated sockets must not be pruned.");
        Assert.AreEqual(3, f.Graph.Connections.Count);
        AssertIntegrity(f.Graph, f.Data);
        AssertRoundTrips(f);
    }


    private static BaseQuestViewModel Workspot(Fixture f, string reference)
    {
        var node = f.Add<questUseWorkspotNodeDefinition>();
        ((questUseWorkspotNodeDefinition)node.Data).ParamsV1 = new CHandle<questUseWorkspotParamsV1>(
            new questUseWorkspotParamsV1 { Function = Enums.questUseWorkspotNodeFunctions.UseWorkspot, WorkspotNode = reference });
        node.RefreshFromData();
        return node;
    }
    private static void AddMetadata(graphGraphConnectionDefinition definition, questIBaseCondition payload, string label)
    {
        definition.AddDynamicProperty("bundleLabel", typeof(CString));
        definition.SetProperty("bundleLabel", (CString)label);
        definition.AddDynamicProperty("bundlePayload", typeof(CHandle<questIBaseCondition>));
        definition.SetProperty("bundlePayload", new CHandle<questIBaseCondition>(payload));
    }
    private static string[] SavedTopology(RedGraph graph) => graph.Connections.OfType<QuestConnectionViewModel>().Select(edge =>
    {
        var source = graph.Nodes.Single(node => node.Output.Contains(edge.Source));
        var target = graph.Nodes.Single(node => node.Input.Contains(edge.Target));
        var sourceIndex = ((questNodeDefinition)source.Data).Sockets.ToList().FindIndex(handle => ReferenceEquals(handle.GetValue(), Socket(edge.Source)));
        var targetIndex = ((questNodeDefinition)target.Data).Sockets.ToList().FindIndex(handle => ReferenceEquals(handle.GetValue(), Socket(edge.Target)));
        var priority = edge.ConnectionDefinition is gamestateMachineTransitionDefinition metadata
            ? ((float)metadata.Priority).ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
        return $"{source.UniqueId}:{sourceIndex}->{target.UniqueId}:{targetIndex}:{edge.ConnectionDefinition.GetType().Name}:{priority}";
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
                var restored = graph.Nodes.Single(candidate => candidate.UniqueId == node.UniqueId);
                Assert.AreEqual(node.Data.GetType(), restored.Data.GetType());
                CollectionAssert.AreEqual(((questNodeDefinition)node.Data).Sockets.Select(handle => ((questSocketDefinition)handle.GetValue()!).Name).ToArray(),
                    ((questNodeDefinition)restored.Data).Sockets.Select(handle => ((questSocketDefinition)handle.GetValue()!).Name).ToArray());
                if (node.Data is questFactsDBManagerNodeDefinition facts)
                {
                    var a = (questSetVar_NodeType)facts.Type.GetValue()!;
                    var b = (questSetVar_NodeType)((questFactsDBManagerNodeDefinition)restored.Data).Type.GetValue()!;
                    Assert.AreEqual(a.FactName, b.FactName); Assert.AreEqual(a.Value, b.Value); Assert.AreEqual(a.SetExactValue, b.SetExactValue);
                }
                if (node.Data is questPauseConditionNodeDefinition { Condition.Chunk: questTriggerCondition trigger })
                {
                    var actual = (questTriggerCondition)((questPauseConditionNodeDefinition)restored.Data).Condition.GetValue()!;
                    Assert.AreEqual(trigger.TriggerAreaRef, actual.TriggerAreaRef); Assert.AreEqual(trigger.IsPlayerActivator, actual.IsPlayerActivator);
                }
                if (node.Data is questFlowControlNodeDefinition flow)
                {
                    Assert.AreEqual(flow.OpensAt, ((questFlowControlNodeDefinition)restored.Data).OpensAt);
                    Assert.AreEqual(flow.ClosesAt, ((questFlowControlNodeDefinition)restored.Data).ClosesAt);
                }
                if (node.Data is questUseWorkspotNodeDefinition { ParamsV1.Chunk: { } parameters })
                {
                    var actual = ((questUseWorkspotNodeDefinition)restored.Data).ParamsV1.Chunk!;
                    Assert.AreEqual(parameters.Function, actual.Function); Assert.AreEqual(parameters.WorkspotNode, actual.WorkspotNode);
                }
            }
        }
    }

    private static QuestConnectionBundle Capture(Fixture f, NodeViewModel anchor, QuestConnectionBundleDirection direction,
        QuestConnectionBundleOperation operation = QuestConnectionBundleOperation.Copy)
    {
        Assert.IsTrue(f.Graph.TryCaptureQuestConnectionBundle(anchor, direction, operation, out var bundle, out var reason), reason);
        Assert.IsNotNull(bundle);
        return bundle!;
    }
    private static void Paste(Fixture f, QuestConnectionBundle bundle, NodeViewModel destination,
        IReadOnlyDictionary<QuestConnectionBundleSocket, BaseConnectorViewModel>? mapping = null, bool allowNewSelfConnections = false) =>
        Assert.IsTrue(f.Graph.TryPasteQuestConnectionBundle(bundle, destination, mapping, out var reason, allowNewSelfConnections), reason);
    private static void RejectPaste(Fixture f, QuestConnectionBundle bundle, NodeViewModel destination,
        IReadOnlyDictionary<QuestConnectionBundleSocket, BaseConnectorViewModel>? mapping = null)
    {
        var before = new Snapshot(f);
        Assert.IsFalse(f.Graph.TryPasteQuestConnectionBundle(bundle, destination, mapping, out var reason));
        Assert.IsFalse(string.IsNullOrWhiteSpace(reason));
        before.AssertUnchanged(f);
    }
    private static Shape CreateShape(Fixture f)
    {
        var anchor = f.Add<questFactsDBManagerNodeDefinition>();
        var destination = f.Add<questFactsDBManagerNodeDefinition>();
        var b = f.Add<questPauseConditionNodeDefinition>();
        var c = f.Add<questConditionNodeDefinition>();
        var d = f.Add<questEventManagerNodeDefinition>();
        var upstreamA = f.Add<questFactsDBManagerNodeDefinition>();
        var upstreamB = f.Add<questPauseConditionNodeDefinition>();
        var unrelatedSource = f.Add<questConditionNodeDefinition>();
        var unrelatedTarget = f.Add<questJournalNodeDefinition>();
        var outgoing = new[] { f.Edge(Out(anchor), In(b), true, new gamestateMachineTransitionDefinition { Priority = 3.5f }),
            f.Edge(Out(anchor), In(c)), f.Edge(Out(anchor), In(d)) };
        var incoming = new[] { f.Edge(Out(upstreamA), In(anchor)), f.Edge(Out(upstreamB), In(anchor), true) };
        f.Edge(Out(unrelatedSource, "False"), In(unrelatedTarget, "Active"));
        return new Shape(anchor, destination, incoming, outgoing);
    }
    private static string[] ExpectedShape(QuestConnectionBundleOperation operation, QuestConnectionBundleDirection direction)
    {
        var outgoing = new[] { "1:Out->3:In", "1:Out->4:In", "1:Out->5:In" };
        var incoming = new[] { "6:Out->1:In", "7:Out->1:In" };
        var copiedOutgoing = new[] { "2:Out->3:In", "2:Out->4:In", "2:Out->5:In" };
        var copiedIncoming = new[] { "6:Out->2:In", "7:Out->2:In" };
        var expected = new List<string> { "8:False->9:Active" };
        if (operation == QuestConnectionBundleOperation.Copy || direction == QuestConnectionBundleDirection.Incoming) expected.AddRange(outgoing);
        if (operation == QuestConnectionBundleOperation.Copy || direction == QuestConnectionBundleDirection.Outgoing) expected.AddRange(incoming);
        if (direction != QuestConnectionBundleDirection.Incoming) expected.AddRange(copiedOutgoing);
        if (direction != QuestConnectionBundleDirection.Outgoing) expected.AddRange(copiedIncoming);
        return expected.ToArray();
    }
    private static QuestInputConnectorViewModel In(BaseQuestViewModel node, string name = "In") =>
        node.Input.OfType<QuestInputConnectorViewModel>().Single(port => port.Name == name);
    private static QuestOutputConnectorViewModel Out(BaseQuestViewModel node, string name = "Out") =>
        node.Output.OfType<QuestOutputConnectorViewModel>().Single(port => port.Name == name);
    private static questSocketDefinition Socket(BaseConnectorViewModel port) => port switch
    {
        QuestInputConnectorViewModel input => input.Data,
        QuestOutputConnectorViewModel output => output.Data,
        _ => throw new InvalidOperationException("Expected a quest connector.")
    };
    private static CHandle<graphGraphConnectionDefinition> HandleFor(questSocketDefinition socket, graphGraphConnectionDefinition definition) =>
        socket.Connections.Single(handle => ReferenceEquals(handle.GetValue(), definition));
    private static RedGraph Generate(questGraphDefinition data) => RedGraph.GenerateQuestGraph("connection-bundle.questphase", data,
        new NodeWrapperFactory(Mock.Of<ILoggerService>(), Mock.Of<IArchiveManager>(), Mock.Of<IGameControllerFactory>()));
    private static string[] NamedTopology(RedGraph graph) => graph.Connections.Select(edge =>
        $"{edge.Source.OwnerId}:{edge.Source.Name}->{edge.Target.OwnerId}:{edge.Target.Name}").ToArray();
    private static string DataJson(questGraphDefinition data)
    {
        using var file = new CR2WFile { RootChunk = new questQuestPhaseResource { Graph = data } };
        return JObject.Parse(RedJsonSerializer.Serialize(new RedFileDto(file)))["Data"]!.ToString();
    }
    private static void SameReferences<T>(IEnumerable<T> expected, IEnumerable<T> actual) where T : class
    {
        var a = expected.ToArray(); var b = actual.ToArray();
        Assert.AreEqual(a.Length, b.Length);
        for (var i = 0; i < a.Length; i++) Assert.AreSame(a[i], b[i]);
    }
    private static void AssertIntegrity(RedGraph graph, questGraphDefinition data)
    {
        var sockets = data.Nodes.SelectMany(handle => ((questNodeDefinition)handle.GetValue()!).Sockets)
            .Select(handle => (questSocketDefinition)handle.GetValue()!).ToArray();
        foreach (var edge in graph.Connections.OfType<QuestConnectionViewModel>())
        {
            var source = Socket(edge.Source); var target = Socket(edge.Target);
            Assert.AreSame(source, edge.ConnectionDefinition.Source.GetValue());
            Assert.AreSame(target, edge.ConnectionDefinition.Destination.GetValue());
            Assert.IsTrue(sockets.Any(socket => ReferenceEquals(socket, source)));
            Assert.IsTrue(sockets.Any(socket => ReferenceEquals(socket, target)));
            Assert.AreEqual(1, source.Connections.Count(handle => ReferenceEquals(handle.GetValue(), edge.ConnectionDefinition)));
            Assert.AreEqual(1, target.Connections.Count(handle => ReferenceEquals(handle.GetValue(), edge.ConnectionDefinition)));
        }
        foreach (var socket in sockets)
        foreach (var handle in socket.Connections)
            Assert.AreEqual(1, graph.Connections.OfType<QuestConnectionViewModel>().Count(edge => ReferenceEquals(edge.ConnectionDefinition, handle.GetValue())));
        foreach (var port in graph.Nodes.SelectMany(node => node.Input.Cast<BaseConnectorViewModel>().Concat(node.Output)))
            Assert.AreEqual(Socket(port).Connections.Count > 0, port.IsConnected);
    }

    private sealed record Shape(BaseQuestViewModel Anchor, BaseQuestViewModel Destination,
        QuestConnectionViewModel[] Incoming, QuestConnectionViewModel[] Outgoing);
    private sealed class Fixture : IDisposable
    {
        public questGraphDefinition Data { get; } = GraphTestHelpers.CreateQuestGraph();
        public RedGraph Graph { get; }
        public GraphDocumentTestFixture Document { get; } = new("mod\\connection-bundle.questphase");
        public Fixture() { Graph = Generate(Data); Graph.DocumentViewModel = Document.Document; }
        public BaseQuestViewModel Add<T>() where T : questNodeDefinition
        {
            var id = Graph.CreateQuestNode(typeof(T), new Point(Graph.Nodes.Count * 120, 40));
            return Graph.Nodes.OfType<BaseQuestViewModel>().Single(node => node.UniqueId == id);
        }
        public QuestInputConnectorViewModel AddInput(BaseQuestViewModel node, string name)
        {
            var port = new QuestInputConnectorViewModel(name, name, node.UniqueId,
                GraphTestHelpers.AddQuestSocket((questNodeDefinition)node.Data, name, Enums.questSocketType.Input));
            node.Input.Add(port); return port;
        }
        public QuestOutputConnectorViewModel AddOutput(BaseQuestViewModel node, string name)
        {
            var port = new QuestOutputConnectorViewModel(name, name, node.UniqueId,
                GraphTestHelpers.AddQuestSocket((questNodeDefinition)node.Data, name, Enums.questSocketType.Output));
            node.Output.Add(port); return port;
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
            Graph.Connections.Add(edge); return edge;
        }
        public void Clean() => Document.Document.SetIsDirty(false);
        public void Dispose() { Graph.Dispose(); Document.Dispose(); }
    }
    private sealed class EdgeState
    {
        public QuestConnectionViewModel Edge { get; }
        private readonly int _index;
        private readonly CHandle<graphGraphConnectionDefinition> _sourceHandle;
        private readonly CHandle<graphGraphConnectionDefinition> _targetHandle;
        private readonly CWeakHandle<graphGraphSocketDefinition> _source;
        private readonly CWeakHandle<graphGraphSocketDefinition> _target;
        private readonly RedBaseClass? _sourceValue;
        private readonly RedBaseClass? _targetValue;
        private readonly bool _selected;
        private readonly int _path;
        public EdgeState(RedGraph graph, QuestConnectionViewModel edge)
        {
            Edge = edge; _index = graph.Connections.IndexOf(edge);
            _sourceHandle = HandleFor(Socket(edge.Source), edge.ConnectionDefinition);
            _targetHandle = HandleFor(Socket(edge.Target), edge.ConnectionDefinition);
            _source = edge.ConnectionDefinition.Source; _target = edge.ConnectionDefinition.Destination;
            _sourceValue = _source.GetValue(); _targetValue = _target.GetValue();
            _selected = edge.IsSelected; _path = edge.PathType;
        }
        public void AssertUnchanged(RedGraph graph)
        {
            Assert.AreSame(Edge, graph.Connections[_index]);
            Assert.AreSame(_source, Edge.ConnectionDefinition.Source); Assert.AreSame(_target, Edge.ConnectionDefinition.Destination);
            Assert.AreSame(_sourceValue, _source.GetValue()); Assert.AreSame(_targetValue, _target.GetValue());
            Assert.AreSame(_sourceHandle, HandleFor(Socket(Edge.Source), Edge.ConnectionDefinition));
            Assert.AreSame(_targetHandle, HandleFor(Socket(Edge.Target), Edge.ConnectionDefinition));
            Assert.AreEqual(_selected, Edge.IsSelected); Assert.AreEqual(_path, Edge.PathType);
        }
        public void AssertMoved(RedGraph graph, QuestOutputConnectorViewModel source, QuestInputConnectorViewModel target)
        {
            var replacement = (QuestConnectionViewModel)graph.Connections[_index];
            Assert.AreNotSame(Edge, replacement);
            Assert.AreSame(Edge.ConnectionDefinition, replacement.ConnectionDefinition);
            Assert.AreSame(source, replacement.Source); Assert.AreSame(target, replacement.Target);
            Assert.AreSame(_sourceHandle, HandleFor(source.Data, replacement.ConnectionDefinition));
            Assert.AreSame(_targetHandle, HandleFor(target.Data, replacement.ConnectionDefinition));
            if (ReferenceEquals(source, Edge.Source)) Assert.AreSame(_source, replacement.ConnectionDefinition.Source);
            else Assert.AreNotSame(_source, replacement.ConnectionDefinition.Source);
            if (ReferenceEquals(target, Edge.Target)) Assert.AreSame(_target, replacement.ConnectionDefinition.Destination);
            else Assert.AreNotSame(_target, replacement.ConnectionDefinition.Destination);
            Assert.AreSame(_sourceValue, _source.GetValue()); Assert.AreSame(_targetValue, _target.GetValue());
            Assert.AreEqual(_selected, replacement.IsSelected); Assert.AreEqual(_path, replacement.PathType);
        }
    }
    private sealed class Snapshot
    {
        private readonly string _json;
        private readonly bool _dirty;
        private readonly NodeViewModel[] _nodes;
        private readonly object[] _canvas;
        private readonly CHandle<graphGraphNodeDefinition>[] _nodeHandles;
        private readonly ConnectionViewModel[] _edges;
        private readonly EdgeState[] _edgeStates;
        private readonly (NodeViewModel Node, InputConnectorViewModel[] Inputs, OutputConnectorViewModel[] Outputs, CHandle<graphGraphSocketDefinition>[] Sockets, Point Location)[] _structure;
        private readonly (questSocketDefinition Socket, CArray<CHandle<graphGraphConnectionDefinition>> Array, CHandle<graphGraphConnectionDefinition>[] Handles, int Capacity)[] _sockets;
        private readonly (BaseConnectorViewModel Port, bool Connected, bool Visible)[] _ports;
        public Snapshot(Fixture f)
        {
            _json = DataJson(f.Data); _dirty = f.Document.Document.IsDirty;
            _nodes = f.Graph.Nodes.ToArray(); _canvas = f.Graph.CanvasItems.ToArray();
            _nodeHandles = f.Data.Nodes.ToArray(); _edges = f.Graph.Connections.ToArray();
            _edgeStates = _edges.OfType<QuestConnectionViewModel>()
                .Where(edge => Socket(edge.Source).Connections.Count(handle => ReferenceEquals(handle.GetValue(), edge.ConnectionDefinition)) == 1 &&
                               Socket(edge.Target).Connections.Count(handle => ReferenceEquals(handle.GetValue(), edge.ConnectionDefinition)) == 1)
                .Select(edge => new EdgeState(f.Graph, edge)).ToArray();
            _structure = _nodes.Select(node => (node, node.Input.ToArray(), node.Output.ToArray(),
                ((questNodeDefinition)node.Data).Sockets.ToArray(), node.Location)).ToArray();
            _sockets = f.Data.Nodes.SelectMany(handle => ((questNodeDefinition)handle.GetValue()!).Sockets)
                .Select(handle => (questSocketDefinition)handle.GetValue()!)
                .Select(socket => (socket, socket.Connections, socket.Connections.ToArray(), socket.Connections.MaxSize)).ToArray();
            _ports = _nodes.SelectMany(node => node.Input.Cast<BaseConnectorViewModel>().Concat(node.Output))
                .Select(port => (port, port.IsConnected, port.IsVisible)).ToArray();
        }
        public void AssertNodeStructurePreserved(Fixture f)
        {
            SameReferences(_nodes, f.Graph.Nodes); SameReferences(_canvas, f.Graph.CanvasItems); SameReferences(_nodeHandles, f.Data.Nodes);
            foreach (var (node, inputs, outputs, sockets, location) in _structure)
            {
                SameReferences(inputs, node.Input); SameReferences(outputs, node.Output);
                SameReferences(sockets, ((questNodeDefinition)node.Data).Sockets); Assert.AreEqual(location, node.Location);
            }
        }
        public void AssertOriginalsPreserved(Fixture f)
        {
            AssertNodeStructurePreserved(f);
            SameReferences(_edges, f.Graph.Connections.Take(_edges.Length));
            foreach (var state in _edgeStates) state.AssertUnchanged(f.Graph);
            foreach (var (socket, array, handles, capacity) in _sockets)
            {
                Assert.AreSame(array, socket.Connections); Assert.AreEqual(capacity, socket.Connections.MaxSize);
                SameReferences(handles, socket.Connections.Take(handles.Length));
            }
        }
        public void AssertUnchanged(Fixture f)
        {
            AssertOriginalsPreserved(f);
            Assert.AreEqual(_json, DataJson(f.Data)); Assert.AreEqual(_dirty, f.Document.Document.IsDirty);
            Assert.AreEqual(_edges.Length, f.Graph.Connections.Count);
            foreach (var (socket, _, handles, _) in _sockets) Assert.AreEqual(handles.Length, socket.Connections.Count);
            foreach (var (port, connected, visible) in _ports)
            {
                Assert.AreEqual(connected, port.IsConnected); Assert.AreEqual(visible, port.IsVisible);
            }
        }
    }
}