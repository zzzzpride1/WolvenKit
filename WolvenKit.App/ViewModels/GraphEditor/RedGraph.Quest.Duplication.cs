using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using WolvenKit.App.ViewModels.GraphEditor.Nodes.Quest;
using WolvenKit.App.ViewModels.GraphEditor.Nodes.Quest.Internal;
using WolvenKit.RED4.Types;

namespace WolvenKit.App.ViewModels.GraphEditor;

public partial class RedGraph
{
    /// <summary>
    /// Duplicates a subgraph in the current quest graph. Candidates and their internal edges are
    /// prepared off-graph; no IDs or document changes are committed until preparation succeeds.
    /// Comments are ignored. Invalid/stale selections or unsupported payloads fail without edits.
    /// </summary>
    public IReadOnlyList<BaseQuestViewModel> DuplicateQuestNodes(IEnumerable<object> selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (GraphType != RedGraphType.Quest || _data is not graphGraphDefinition graph)
        {
            throw new InvalidOperationException("Group duplication requires a quest graph.");
        }

        var selected = new HashSet<object>(selection.Where(item => item is not GraphCommentViewModel),
            ReferenceEqualityComparer.Instance);
        if (selected.Count == 0)
        {
            return Array.Empty<BaseQuestViewModel>();
        }
        if (selected.Any(item => item is not BaseQuestViewModel node || !Nodes.Contains(node) ||
                                 node.Data is not questNodeDefinition))
        {
            throw new InvalidOperationException("Select supported quest nodes from the currently displayed graph only.");
        }

        // Use graph order, not click order. Object identity matters: RED equality is structural.
        var originals = Nodes.OfType<BaseQuestViewModel>().Where(selected.Contains).ToArray();
        var definitions = originals.Select(node => (questNodeDefinition)node.Data).ToArray();
        var edges = ValidateQuestDuplicationGraph(graph);
        ValidateQuestDuplicationView(graph, edges);
        var checkedPayloads = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var definition in definitions)
        {
            ValidateQuestDuplicatePayload(definition, new HashSet<object>(ReferenceEqualityComparer.Instance), checkedPayloads);
            ValidateQuestDuplicatePhases(definition);
        }

        var ids = ReserveQuestDuplicateIds(definitions.Length);
        var idMap = definitions.Select((node, i) => ((ushort)node.Id, ids[i])).ToDictionary(pair => pair.Item1, pair => pair.Item2);
        var candidates = new List<BaseQuestViewModel>();
        try
        {
            var copies = definitions.Select(node => (questNodeDefinition)node.DeepCopy()).ToArray();
            for (var i = 0; i < copies.Length; i++)
            {
                copies[i].Id = ids[i];
                // These IDs refer to nodes in this graph, unlike facts, NodeRefs or resource paths.
                if (copies[i] is questDeletionMarkerNodeDefinition marker)
                {
                    for (var j = 0; j < marker.DeletedNodeIds.Count; j++)
                    {
                        if (idMap.TryGetValue(marker.DeletedNodeIds[j], out var replacement))
                        {
                            marker.DeletedNodeIds[j] = replacement;
                        }
                    }
                }
            }
            var copiedEdges = RebuildQuestDuplicateEdges(definitions, copies);
            var detachedPayloads = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (var copy in copies) DetachQuestDuplicateWeakHandles(copy, detachedPayloads);
            for (var i = 0; i < copies.Length; i++)
            {
                var candidate = WrapQuestNode(copies[i], false);
                candidates.Add(candidate);
                candidate.Location = new System.Windows.Point(originals[i].Location.X + 50, originals[i].Location.Y + 50);
                candidate.ShowUnusedSockets = originals[i].ShowUnusedSockets;
            }

            var inputs = new Dictionary<questSocketDefinition, QuestInputConnectorViewModel>(ReferenceEqualityComparer.Instance);
            var outputs = new Dictionary<questSocketDefinition, QuestOutputConnectorViewModel>(ReferenceEqualityComparer.Instance);
            foreach (var candidate in candidates)
            {
                foreach (var input in candidate.Input.OfType<QuestInputConnectorViewModel>()) inputs.Add(input.Data, input);
                foreach (var output in candidate.Output.OfType<QuestOutputConnectorViewModel>()) outputs.Add(output.Data, output);
            }
            var connections = new List<QuestConnectionViewModel>();
            foreach (var edge in copiedEdges)
            {
                if (edge.Source.GetValue() is not questSocketDefinition source || !outputs.TryGetValue(source, out var output) ||
                    edge.Destination.GetValue() is not questSocketDefinition target || !inputs.TryGetValue(target, out var input))
                {
                    throw new InvalidOperationException("A duplicated connection cannot be represented by this node's sockets.");
                }
                connections.Add(new QuestConnectionViewModel(output, input, edge));
            }
            foreach (var candidate in candidates) candidate.UpdateSocketVisibility();
            CommitQuestDuplicates(graph, candidates, connections);
            return candidates;
        }
        catch
        {
            foreach (var candidate in candidates) candidate.Dispose();
            throw;
        }
    }

    private ushort[] ReserveQuestDuplicateIds(int count)
    {
        var used = ((graphGraphDefinition)_data).Nodes.Select(handle => (ushort)((questNodeDefinition)handle.GetValue()!).Id).ToHashSet();
        var result = new ushort[count];
        var cursor = ((int)CurrentQuestNodeId + 1) % ushort.MaxValue;
        for (var i = 0; i < count; i++)
        {
            var searched = 0;
            while (used.Contains((ushort)cursor) && searched++ < ushort.MaxValue)
            {
                cursor = (cursor + 1) % ushort.MaxValue;
            }
            if (searched >= ushort.MaxValue)
            {
                throw new InvalidOperationException("No available quest node IDs remaining for the selected group.");
            }
            result[i] = (ushort)cursor;
            used.Add(result[i]);
            cursor = (cursor + 1) % ushort.MaxValue;
        }
        return result;
    }

    private static IReadOnlyList<graphGraphConnectionDefinition> ValidateQuestDuplicationGraph(graphGraphDefinition graph)
    {
        var sockets = new HashSet<questSocketDefinition>(ReferenceEqualityComparer.Instance);
        var ids = new HashSet<ushort>();
        foreach (var handle in graph.Nodes)
        {
            if (handle.GetValue() is not questNodeDefinition node || !ids.Add(node.Id))
            {
                throw new InvalidOperationException("The quest graph has unsupported nodes or duplicate node IDs.");
            }
            foreach (var socketHandle in node.Sockets)
            {
                if (socketHandle.GetValue() is not questSocketDefinition socket || !sockets.Add(socket) ||
                    (!IsQuestInputSocket(socket) && !IsQuestOutputSocket(socket)))
                {
                    throw new InvalidOperationException("The quest graph has missing, shared or unsupported sockets.");
                }
            }
        }
        var edges = new List<graphGraphConnectionDefinition>();
        var seen = new HashSet<graphGraphConnectionDefinition>(ReferenceEqualityComparer.Instance);
        foreach (var socket in sockets)
        {
            foreach (var handle in socket.Connections)
            {
                if (handle.GetValue() is not graphGraphConnectionDefinition edge || edge.Source?.GetValue() is not questSocketDefinition source ||
                    edge.Destination?.GetValue() is not questSocketDefinition target || !sockets.Contains(source) ||
                    !sockets.Contains(target) || !IsQuestOutputSocket(source) || !IsQuestInputSocket(target) ||
                    (!ReferenceEquals(socket, source) && !ReferenceEquals(socket, target)) ||
                    source.Connections.Count(item => ReferenceEquals(item.GetValue(), edge)) != 1 ||
                    target.Connections.Count(item => ReferenceEquals(item.GetValue(), edge)) != 1)
                {
                    throw new InvalidOperationException("The quest graph has stale, non-reciprocal or cross-level connections.");
                }
                if (seen.Add(edge)) edges.Add(edge);
            }
        }
        return edges;
    }

    private void ValidateQuestDuplicationView(graphGraphDefinition graph, IReadOnlyList<graphGraphConnectionDefinition> edges)
    {
        var definitions = new HashSet<RedBaseClass>(graph.Nodes.Select(handle => handle.GetValue()!), ReferenceEqualityComparer.Instance);
        if (Nodes.Count != definitions.Count || Nodes.Any(node => node.Data is not questNodeDefinition definition ||
            node.UniqueId != (ushort)definition.Id || !definitions.Remove(definition)))
        {
            throw new InvalidOperationException("Quest node data and the visual graph are out of sync. Reopen the graph before duplicating.");
        }
        var backingEdges = new HashSet<graphGraphConnectionDefinition>(edges, ReferenceEqualityComparer.Instance);
        var seen = new HashSet<graphGraphConnectionDefinition>(ReferenceEqualityComparer.Instance);
        foreach (var connection in Connections)
        {
            if (connection is not QuestConnectionViewModel edge || !backingEdges.Contains(edge.ConnectionDefinition) ||
                !seen.Add(edge.ConnectionDefinition) || edge.Source is not QuestOutputConnectorViewModel source ||
                edge.Target is not QuestInputConnectorViewModel target ||
                !ReferenceEquals(edge.ConnectionDefinition.Source.GetValue(), source.Data) ||
                !ReferenceEquals(edge.ConnectionDefinition.Destination.GetValue(), target.Data) ||
                !Nodes.Any(node => node.UniqueId == source.OwnerId && node.Output.Contains(source) && HasQuestBackingSocket(node, source.Data)) ||
                !Nodes.Any(node => node.UniqueId == target.OwnerId && node.Input.Contains(target) && HasQuestBackingSocket(node, target.Data)))
            {
                throw new InvalidOperationException("Quest connections and the visual graph are out of sync. Reopen the graph before duplicating.");
            }
        }
        if (seen.Count != edges.Count)
        {
            throw new InvalidOperationException("Some quest connections are missing from the visual graph. Reopen it before duplicating.");
        }
    }

    private static void ValidateQuestDuplicatePayload(IRedType value, HashSet<object> path, HashSet<object> visited)
    {
        // Weak game references remain external. Graph connection endpoints are explicitly replaced below.
        if (value is IRedWeakHandle || value.GetType().IsValueType) return;
        if (path.Count >= 256 || !path.Add(value))
        {
            throw new InvalidOperationException("Cannot duplicate an embedded payload with cyclic ownership or excessive nesting.");
        }
        if (!visited.Add(value))
        {
            // Connections are intentionally owned by both endpoint sockets and rebuilt once below.
            // DeepCopy has no identity memo for other shared mutable payloads; do not silently split them.
            if (value is not graphGraphConnectionDefinition &&
                !(value is IRedBaseHandle sharedHandle && sharedHandle.GetValue() is graphGraphConnectionDefinition))
            {
                throw new InvalidOperationException("Cannot safely duplicate shared embedded payloads with the current RED cloning support.");
            }
            path.Remove(value);
            return;
        }
        if (value is IDynamicClass || value is RedBaseClass red && red.GetDynamicProperties().Any() || value is not IRedCloneable)
        {
            throw new InvalidOperationException($"Cannot safely duplicate payload {value.GetType().Name}: dynamic or non-cloneable data.");
        }
        switch (value)
        {
            case RedBaseClass instance:
                foreach (var name in instance.GetPropertyNames())
                {
                    if (instance.GetProperty(name) is { } property) ValidateQuestDuplicatePayload(property, path, visited);
                }
                break;
            case IRedBaseHandle handle when handle.GetValue() is { } child:
                ValidateQuestDuplicatePayload(child, path, visited);
                break;
            case IEnumerable array:
                foreach (var item in array.OfType<IRedType>()) ValidateQuestDuplicatePayload(item, path, visited);
                break;
        }
        path.Remove(value);
    }

    private static IRedType DetachQuestDuplicateWeakHandles(IRedType value, HashSet<object> visited)
    {
        // DeepCopy retains weak-handle wrappers. Copy the wrapper while preserving its external target.
        if (value is IRedWeakHandle weak)
        {
            var copy = (IRedBaseHandle)System.Activator.CreateInstance(value.GetType())!;
            copy.SetValue(weak.GetValue());
            return copy;
        }
        if (value.GetType().IsValueType || !visited.Add(value)) return value;
        switch (value)
        {
            case RedBaseClass instance:
                foreach (var name in instance.GetPropertyNames())
                {
                    if (instance.GetProperty(name) is { } property)
                    {
                        var copy = DetachQuestDuplicateWeakHandles(property, visited);
                        if (!ReferenceEquals(copy, property)) instance.SetProperty(name, copy);
                    }
                }
                break;
            case IRedBaseHandle handle when handle.GetValue() is { } child:
                DetachQuestDuplicateWeakHandles(child, visited);
                break;
            case IList array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is IRedType item)
                    {
                        var copy = DetachQuestDuplicateWeakHandles(item, visited);
                        if (!ReferenceEquals(copy, item)) array[i] = copy;
                    }
                }
                break;
        }
        return value;
    }

    private static void ValidateQuestDuplicatePhases(questNodeDefinition node)
    {
        if (node is not questPhaseNodeDefinition { PhaseGraph.Chunk: { } child }) return;
        ValidateQuestDuplicationGraph(child);
        foreach (var handle in child.Nodes) ValidateQuestDuplicatePhases((questNodeDefinition)handle.GetValue()!);
    }

    private static List<graphGraphConnectionDefinition> RebuildQuestDuplicateEdges(
        IReadOnlyList<questNodeDefinition> originals, IReadOnlyList<questNodeDefinition> copies)
    {
        var sockets = new Dictionary<questSocketDefinition, questSocketDefinition>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < originals.Count; i++)
        {
            for (var j = 0; j < originals[i].Sockets.Count; j++)
            {
                var source = (questSocketDefinition)originals[i].Sockets[j].GetValue()!;
                var copy = (questSocketDefinition)copies[i].Sockets[j].GetValue()!;
                copy.Connections.Clear();
                sockets.Add(source, copy);
            }
            if (originals[i] is questPhaseNodeDefinition { PhaseGraph.Chunk: { } child })
            {
                var copiedChild = ((questPhaseNodeDefinition)copies[i]).PhaseGraph.Chunk!;
                RebuildQuestDuplicateEdges(child.Nodes.Select(handle => (questNodeDefinition)handle.GetValue()!).ToArray(),
                    copiedChild.Nodes.Select(handle => (questNodeDefinition)handle.GetValue()!).ToArray());
            }
        }
        var result = new List<graphGraphConnectionDefinition>();
        var seen = new HashSet<graphGraphConnectionDefinition>(ReferenceEqualityComparer.Instance);
        foreach (var original in sockets.Keys)
        {
            foreach (var handle in original.Connections)
            {
                var edge = (graphGraphConnectionDefinition)handle.GetValue()!;
                if (!seen.Add(edge) || !sockets.TryGetValue((questSocketDefinition)edge.Source.GetValue()!, out var source) ||
                    !sockets.TryGetValue((questSocketDefinition)edge.Destination.GetValue()!, out var target)) continue;
                var copy = (graphGraphConnectionDefinition)edge.DeepCopy();
                // CWeakHandle is not cloneable: replace the handles themselves, never mutate the originals.
                copy.Source = new CWeakHandle<graphGraphSocketDefinition>(source);
                copy.Destination = new CWeakHandle<graphGraphSocketDefinition>(target);
                var copiedHandle = new CHandle<graphGraphConnectionDefinition>(copy);
                source.Connections.Add(copiedHandle);
                target.Connections.Add(copiedHandle);
                result.Add(copy);
            }
        }
        return result;
    }

    private void CommitQuestDuplicates(graphGraphDefinition graph, IReadOnlyList<BaseQuestViewModel> nodes,
        IReadOnlyList<QuestConnectionViewModel> connections)
    {
        var dataCount = graph.Nodes.Count;
        var nodeCount = Nodes.Count;
        var edgeCount = Connections.Count;
        var canvas = CanvasItems.ToArray();
        var previousId = CurrentQuestNodeId;
        var wasDirty = DocumentViewModel?.IsDirty;
        try
        {
            foreach (var node in nodes)
            {
                graph.Nodes.Add(new CHandle<graphGraphNodeDefinition>((questNodeDefinition)node.Data));
                Nodes.Add(node);
            }
            foreach (var connection in connections) Connections.Add(connection);
            foreach (var node in nodes) RaiseCurrentQuestNodeId((ushort)node.UniqueId);
            GetQuestNodesChunkViewModel()?.RecalculateProperties();
            NotifyNodesUpdated(nodes.Select(node => node.UniqueId).ToArray());
            DocumentViewModel?.SetIsDirty(true);
        }
        catch (Exception exception)
        {
            // Only appended candidates were touched. Preserve original handles, connections and their order.
            while (Connections.Count > edgeCount) Connections.RemoveAt(Connections.Count - 1);
            while (Nodes.Count > nodeCount) Nodes.RemoveAt(Nodes.Count - 1);
            while (graph.Nodes.Count > dataCount) graph.Nodes.RemoveAt(graph.Nodes.Count - 1);
            if (!CanvasItems.SequenceEqual(canvas, ReferenceEqualityComparer.Instance))
            {
                CanvasItems.Clear();
                foreach (var item in canvas) CanvasItems.Add(item);
            }
            CurrentQuestNodeId = previousId;
            if (wasDirty is { } dirty) DocumentViewModel?.SetIsDirty(dirty);
            GetQuestNodesChunkViewModel()?.RecalculateProperties();
            throw new InvalidOperationException("Quest group duplication failed; the original graph was restored.", exception);
        }
    }
}
