using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using WolvenKit.App.ViewModels.GraphEditor.Nodes;
using WolvenKit.App.ViewModels.GraphEditor.Nodes.Quest;
using WolvenKit.App.ViewModels.GraphEditor.Nodes.Quest.Internal;
using WolvenKit.RED4.CR2W.JSON;
using WolvenKit.RED4.Types;

namespace WolvenKit.App.ViewModels.GraphEditor;

public partial class RedGraph
{
    /// <summary>Captures ordinary flow edges without changing the document.</summary>
    public bool TryCaptureQuestConnectionBundle(NodeViewModel? anchor, QuestConnectionBundleDirection direction,
        QuestConnectionBundleOperation operation, out QuestConnectionBundle? bundle, out string reason)
    {
        bundle = null;
        if (direction is not (QuestConnectionBundleDirection.All or QuestConnectionBundleDirection.Incoming or QuestConnectionBundleDirection.Outgoing) ||
            operation is not (QuestConnectionBundleOperation.Copy or QuestConnectionBundleOperation.Cut))
        {
            reason = "Choose a supported connection direction and clipboard operation.";
            return false;
        }
        if (!TryGetLiveQuestBundleNode(anchor, out var sourceNode, out reason)) return false;
        try
        {
            var selectedSockets = ((questNodeDefinition)sourceNode.Data).Sockets
                .Select(handle => handle.GetValue()).OfType<questSocketDefinition>()
                .Where(socket => IsQuestBundleDirectionSocket(socket, direction)).ToArray();
            var selectedDefinitions = new HashSet<graphGraphConnectionDefinition>(ReferenceEqualityComparer.Instance);
            // Include backing records missing from the visual graph.
            foreach (var socket in selectedSockets)
            {
                var ports = sourceNode.Input.Cast<BaseConnectorViewModel>().Concat(sourceNode.Output)
                    .Where(port => ReferenceEquals(GetQuestRewireSocket(port), socket) && IsLiveQuestRewireSocket(port)).ToArray();
                if (ports.Length == 0)
                {
                    reason = "A selected ordinary socket is missing from the visual graph.";
                    return false;
                }
                foreach (var handle in socket.Connections)
                {
                    if (handle?.GetValue() is not graphGraphConnectionDefinition definition)
                    {
                        reason = "A selected socket contains a missing connection definition.";
                        return false;
                    }
                    if (IsQuestBundleCutConnection(definition)) continue;
                    if (definition.Source?.GetValue() is not questSocketDefinition output ||
                        definition.Destination?.GetValue() is not questSocketDefinition input ||
                        output.Type != Enums.questSocketType.Output || input.Type != Enums.questSocketType.Input ||
                        (socket.Type == Enums.questSocketType.Output ? !ReferenceEquals(output, socket) : !ReferenceEquals(input, socket)))
                    {
                        reason = "A selected ordinary connection has stale or inconsistent endpoints.";
                        return false;
                    }
                    selectedDefinitions.Add(definition);
                }
            }
            // Include visual records with missing reciprocal backing entries.
            foreach (var visual in Connections.OfType<QuestConnectionViewModel>())
            {
                if (IsQuestBundleCutConnection(visual.ConnectionDefinition)) continue;
                var selectedSource = direction != QuestConnectionBundleDirection.Incoming &&
                    (sourceNode.Output.Contains(visual.Source) || selectedSockets.Any(socket => ReferenceEquals(socket, GetQuestRewireSocket(visual.Source))));
                var selectedTarget = direction != QuestConnectionBundleDirection.Outgoing &&
                    (sourceNode.Input.Contains(visual.Target) || selectedSockets.Any(socket => ReferenceEquals(socket, GetQuestRewireSocket(visual.Target))));
                if (selectedSource || selectedTarget) selectedDefinitions.Add(visual.ConnectionDefinition);
            }
            var visuals = Connections.OfType<QuestConnectionViewModel>()
                .Where(edge => selectedDefinitions.Contains(edge.ConnectionDefinition)).ToArray();
            if (selectedDefinitions.Count == 0)
            {
                reason = "This node has no ordinary connections in the selected direction.";
                return false;
            }
            if (visuals.Length != selectedDefinitions.Count ||
                visuals.Select(edge => edge.ConnectionDefinition).Distinct(ReferenceEqualityComparer.Instance).Count() != selectedDefinitions.Count)
            {
                reason = "Some selected connections are missing or duplicated in the visual graph.";
                return false;
            }
            var sockets = new List<QuestConnectionBundleSocket>();
            var slotByData = new Dictionary<questSocketDefinition, QuestConnectionBundleSocket>(ReferenceEqualityComparer.Instance);
            QuestConnectionBundleSocket Slot(BaseConnectorViewModel port)
            {
                var data = GetQuestRewireSocket(port)!;
                if (slotByData.TryGetValue(data, out var existing)) return existing;
                var slot = new QuestConnectionBundleSocket
                {
                    Name = port.Name,
                    IsOutput = port is QuestOutputConnectorViewModel,
                    RawName = data.Name,
                    OriginalPort = new WeakReference<BaseConnectorViewModel>(port)
                };
                slotByData.Add(data, slot);
                sockets.Add(slot);
                return slot;
            }
            var captures = new List<QuestConnectionBundleEdgeCapture>();
            foreach (var edge in visuals)
            {
                if (!CanStartQuestConnectionRewire(edge, out reason)) return false;
                var output = (QuestOutputConnectorViewModel)edge.Source;
                var input = (QuestInputConnectorViewModel)edge.Target;
                var sourceSlot = direction != QuestConnectionBundleDirection.Incoming && sourceNode.Output.Contains(output) ? Slot(output) : null;
                var targetSlot = direction != QuestConnectionBundleDirection.Outgoing && sourceNode.Input.Contains(input) ? Slot(input) : null;
                if (sourceSlot is null && targetSlot is null)
                {
                    reason = "A selected connection no longer belongs to the source node.";
                    return false;
                }
                captures.Add(new QuestConnectionBundleEdgeCapture
                {
                    Visual = new(edge),
                    Definition = new(edge.ConnectionDefinition),
                    Source = new(output),
                    Target = new(input),
                    SourceHandle = new(output.Data.Connections.Single(handle => ReferenceEquals(handle.GetValue(), edge.ConnectionDefinition))),
                    TargetHandle = new(input.Data.Connections.Single(handle => ReferenceEquals(handle.GetValue(), edge.ConnectionDefinition))),
                    SourceEndpoint = new(edge.ConnectionDefinition.Source),
                    TargetEndpoint = new(edge.ConnectionDefinition.Destination),
                    SourceConnections = new(output.Data.Connections),
                    TargetConnections = new(input.Data.Connections),
                    SourceName = output.Data.Name,
                    TargetName = input.Data.Name,
                    SourceSlot = sourceSlot,
                    TargetSlot = targetSlot
                });
            }
            var json = CaptureQuestBundleMetadata(visuals.Select(edge => edge.ConnectionDefinition));
            bundle = new QuestConnectionBundle
            {
                Operation = operation,
                Direction = direction,
                ConnectionCount = captures.Count,
                AnchorNodeId = sourceNode.UniqueId,
                Sockets = sockets.AsReadOnly(),
                Owner = new(this),
                Graph = new((graphGraphDefinition)_data),
                Anchor = new(sourceNode),
                Edges = captures.AsReadOnly(),
                Layouts = Array.AsReadOnly(Nodes.OfType<BaseQuestViewModel>().Where(node => visuals.Any(edge =>
                    node.Output.Contains(edge.Source) || node.Input.Contains(edge.Target))).Select(CaptureQuestBundleLayout).ToArray()),
                MetadataJson = json
            };
            reason = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            _loggerService?.Error(exception);
            reason = "The connection bundle could not be captured; the graph was not changed.";
            return false;
        }
    }

    public bool IsQuestConnectionBundleCurrent(QuestConnectionBundle? bundle, out string reason) =>
        TryResolveQuestConnectionBundle(bundle, out _, out _, out reason);

    public bool TryGetQuestConnectionBundleSocketMappings(QuestConnectionBundle? bundle, NodeViewModel? destination,
        out IReadOnlyList<QuestConnectionBundleSocketMapping> mappings, out string reason)
    {
        mappings = [];
        if (!TryResolveQuestConnectionBundle(bundle, out var anchor, out _, out reason) ||
            !TryGetLiveQuestBundleNode(destination, out var destinationNode, out reason)) return false;
        var result = new List<QuestConnectionBundleSocketMapping>();
        foreach (var slot in bundle!.Sockets)
        {
            var choices = destinationNode.Input.Cast<BaseConnectorViewModel>().Concat(destinationNode.Output)
                .Where(port => slot.IsOutput ? port is QuestOutputConnectorViewModel && GetQuestRewireSocket(port)?.Type == Enums.questSocketType.Output
                    : port is QuestInputConnectorViewModel && GetQuestRewireSocket(port)?.Type == Enums.questSocketType.Input)
                .Where(IsLiveQuestRewireSocket)
                .DistinctBy(port => GetQuestRewireSocket(port)!, ReferenceEqualityComparer.Instance).ToArray();
            if (choices.Length == 0)
            {
                reason = $"The destination has no ordinary {(slot.IsOutput ? "output" : "input")} socket for {slot.Name}.";
                return false;
            }
            BaseConnectorViewModel? suggested = null;
            if (ReferenceEquals(anchor, destinationNode) && slot.OriginalPort.TryGetTarget(out var original) &&
                choices.Any(port => ReferenceEquals(GetQuestRewireSocket(port), GetQuestRewireSocket(original))))
            {
                suggested = choices.First(port => ReferenceEquals(GetQuestRewireSocket(port), GetQuestRewireSocket(original)));
                if (IsLiveQuestRewireSocket(original)) suggested = original;
            }
            else
            {
                var named = choices.Where(port => GetQuestRewireSocket(port)!.Name == slot.RawName).ToArray();
                if (named.Length == 1) suggested = named[0];
                else if (choices.Length == 1) suggested = choices[0];
            }
            result.Add(new QuestConnectionBundleSocketMapping(slot, Array.AsReadOnly(choices), suggested));
        }
        mappings = result.AsReadOnly();
        reason = string.Empty;
        return true;
    }

    public bool TryPreviewQuestConnectionBundle(QuestConnectionBundle? bundle, NodeViewModel? destination,
        IReadOnlyDictionary<QuestConnectionBundleSocket, BaseConnectorViewModel>? mapping,
        out QuestConnectionBundlePreview? preview, out string reason)
    {
        preview = null;
        if (!TryPrepareQuestBundlePaste(bundle, destination, mapping, out var plan, out reason)) return false;
        preview = new QuestConnectionBundlePreview(
            Array.AsReadOnly(plan.Select(edge => new QuestConnectionBundlePreviewEdge(edge.Source, edge.Target, edge.AlreadyExists)).ToArray()),
            plan.Any(edge => edge.IntroducesSelfConnection));
        return true;
    }
    public bool TryPasteQuestConnectionBundle(QuestConnectionBundle? bundle, NodeViewModel? destination,
        IReadOnlyDictionary<QuestConnectionBundleSocket, BaseConnectorViewModel>? mapping,
        out string reason, bool allowNewSelfConnections = false)
    {
        if (!TryPrepareQuestBundlePaste(bundle, destination, mapping, out var plan, out reason)) return false;
        if (!allowNewSelfConnections && plan.Any(edge => edge.IntroducesSelfConnection))
        {
            reason = "This paste creates a new self-connection. Confirm it explicitly before pasting.";
            return false;
        }
        var isMove = bundle!.Operation == QuestConnectionBundleOperation.Cut;
        var edits = plan.Where(edge => isMove
            ? !ReferenceEquals(edge.Original.Source.Data, edge.Source.Data) || !ReferenceEquals(edge.Original.Target.Data, edge.Target.Data)
            : !edge.AlreadyExists).ToArray();
        if (edits.Length == 0)
        {
            if (isMove) bundle.IsConsumed = true;
            reason = string.Empty;
            return true;
        }
        graphGraphConnectionDefinition[]? copies = null;
        try
        {
            if (!isMove)
            {
                copies = RedJsonSerializer.Deserialize<graphGraphConnectionDefinition[]>(bundle.MetadataJson);
                if (copies is null || copies.Length != plan.Length ||
                    copies.Where((definition, index) => definition is null || definition.GetType() != plan[index].Original.Definition.GetType()).Any())
                {
                    reason = "The captured connection metadata cannot be restored safely.";
                    return false;
                }
            }
        }
        catch (Exception exception)
        {
            _loggerService?.Error(exception);
            reason = "The captured connection metadata could not be restored; the graph was not changed.";
            return false;
        }
        var touched = edits.SelectMany(edge => new[] { edge.Original.Source.Data, edge.Original.Target.Data, edge.Source.Data, edge.Target.Data })
            .Distinct<questSocketDefinition>(ReferenceEqualityComparer.Instance).ToArray();
        var arrays = touched.Select(socket => (Socket: socket, Array: socket.Connections, Handles: socket.Connections.ToArray())).ToArray();
        var originalVisuals = Connections.ToArray();
        var affected = Nodes.OfType<BaseQuestViewModel>().Where(node =>
            node.Input.Cast<BaseConnectorViewModel>().Concat(node.Output).Any(port => GetQuestRewireSocket(port) is { } socket &&
                touched.Any(item => ReferenceEquals(item, socket)))).ToArray();
        var flags = affected.SelectMany(node => node.Input.Cast<BaseConnectorViewModel>().Concat(node.Output))
            .Select(port => (Port: port, Connected: port.IsConnected, Visible: port.IsVisible)).ToArray();
        var endpoints = edits.Select(edge => (Definition: edge.Original.Definition,
            Source: edge.Original.Definition.Source, Target: edge.Original.Definition.Destination)).ToArray();
        var wasDirty = DocumentViewModel?.IsDirty;
        try
        {
            if (isMove)
            {
                // Remove all moving memberships first: final capacity may depend on another member leaving.
                foreach (var edge in edits)
                {
                    if (!ReferenceEquals(edge.Original.Source.Data, edge.Source.Data))
                        RemoveQuestBundleHandle(edge.Original.Source.Data, edge.Original.SourceHandle);
                    if (!ReferenceEquals(edge.Original.Target.Data, edge.Target.Data))
                        RemoveQuestBundleHandle(edge.Original.Target.Data, edge.Original.TargetHandle);
                }
                foreach (var edge in edits)
                {
                    var definition = edge.Original.Definition;
                    if (!ReferenceEquals(edge.Original.Source.Data, edge.Source.Data))
                    {
                        edge.Source.Data.Connections.Add(edge.Original.SourceHandle);
                        // Other definitions may alias the old weak wrapper; replace it rather than mutate it.
                        definition.Source = new CWeakHandle<graphGraphSocketDefinition>(edge.Source.Data);
                    }
                    if (!ReferenceEquals(edge.Original.Target.Data, edge.Target.Data))
                    {
                        edge.Target.Data.Connections.Add(edge.Original.TargetHandle);
                        definition.Destination = new CWeakHandle<graphGraphSocketDefinition>(edge.Target.Data);
                    }
                    var replacement = new QuestConnectionViewModel(edge.Source, edge.Target, definition)
                    {
                        IsSelected = edge.Original.Visual.IsSelected,
                        PathType = edge.Original.Visual.PathType
                    };
                    Connections[Array.FindIndex(originalVisuals, item => ReferenceEquals(item, edge.Original.Visual))] = replacement;
                }
            }
            else
            {
                foreach (var edge in edits)
                {
                    var definition = copies![edge.Index];
                    var copy = AddQuestConnection(edge.Source, edge.Target, notify: false, definition: definition);
                    copy.PathType = edge.Original.Visual.PathType;
                }
            }
            foreach (var (port, _, _) in flags)
                if (GetQuestRewireSocket(port) is { } socket && touched.Any(item => ReferenceEquals(item, socket)))
                    port.IsConnected = socket.Connections.Count > 0;
            foreach (var node in affected) node.UpdateSocketVisibility();
            RefreshCVM(touched);
            if (bundle.IsCancelled || _disposedValue) throw new InvalidOperationException("The pending paste was cancelled.");
            DocumentViewModel?.SetIsDirty(true);
            if (bundle.IsCancelled || _disposedValue) throw new InvalidOperationException("The pending paste was cancelled.");
            if (isMove) bundle.IsConsumed = true;
            reason = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            // Notifications can throw after mutation. Every restoration step must run independently.
            foreach (var (definition, source, target) in endpoints)
            {
                RestoreQuestBundleStep(() => definition.Source = source);
                RestoreQuestBundleStep(() => definition.Destination = target);
            }
            foreach (var (socket, array, handles) in arrays)
            {
                RestoreQuestBundleStep(() => socket.Connections = array);
                RestoreQuestBundleStep(array.Clear);
                foreach (var handle in handles) RestoreQuestBundleStep(() => array.Add(handle));
            }
            RestoreQuestBundleVisuals(originalVisuals);
            foreach (var (port, connected, visible) in flags)
            {
                RestoreQuestBundleStep(() => port.IsConnected = connected);
                RestoreQuestBundleStep(() => port.IsVisible = visible);
            }
            if (wasDirty is { } dirty) RestoreQuestBundleStep(() => DocumentViewModel?.SetIsDirty(dirty));
            foreach (var node in affected) RestoreQuestBundleStep(() => node.TriggerPropertyChanged(nameof(node.Data)));
            _loggerService?.Error(exception);
            reason = "The bundle could not be pasted; all connection changes were rolled back.";
            return false;
        }
    }

    private static QuestConnectionBundleNodeLayoutCapture CaptureQuestBundleLayout(BaseQuestViewModel node) => new()
    {
        Node = new(node),
        InputCount = node.Input.Count,
        Ports = Array.AsReadOnly(node.Input.Cast<BaseConnectorViewModel>().Concat(node.Output).Select(port =>
        {
            var socket = GetQuestRewireSocket(port) ?? throw new InvalidOperationException("Unsupported quest socket layout.");
            return new QuestConnectionBundlePortLayoutCapture { Port = new(port), RawName = socket.Name, Type = socket.Type };
        }).ToArray()),
        Sockets = Array.AsReadOnly(((questNodeDefinition)node.Data).Sockets.Select(handle =>
        {
            var socket = handle.GetValue() as questSocketDefinition ?? throw new InvalidOperationException("Unsupported backing socket layout.");
            return new QuestConnectionBundleBackingLayoutCapture { Handle = new(handle), Data = new(socket), RawName = socket.Name, Type = socket.Type };
        }).ToArray())
    };

    private bool IsQuestBundleLayoutCurrent(QuestConnectionBundleNodeLayoutCapture layout)
    {
        if (!layout.Node.TryGetTarget(out var node) || !TryGetLiveQuestBundleNode(node, out _, out _) ||
            node.Input.Count != layout.InputCount || node.Input.Count + node.Output.Count != layout.Ports.Count ||
            ((questNodeDefinition)node.Data).Sockets.Count != layout.Sockets.Count) return false;
        var ports = node.Input.Cast<BaseConnectorViewModel>().Concat(node.Output).ToArray();
        for (var index = 0; index < ports.Length; index++)
        {
            var captured = layout.Ports[index];
            if (!captured.Port.TryGetTarget(out var original) || !ReferenceEquals(original, ports[index]) ||
                GetQuestRewireSocket(original) is not { } socket || socket.Name != captured.RawName || !socket.Type.Equals(captured.Type)) return false;
        }
        var sockets = ((questNodeDefinition)node.Data).Sockets;
        for (var index = 0; index < sockets.Count; index++)
        {
            var captured = layout.Sockets[index];
            if (!captured.Handle.TryGetTarget(out var handle) || !ReferenceEquals(handle, sockets[index]) ||
                !captured.Data.TryGetTarget(out var socket) || !ReferenceEquals(socket, handle.GetValue()) ||
                socket.Name != captured.RawName || !socket.Type.Equals(captured.Type)) return false;
        }
        return true;
    }

    private bool TryGetLiveQuestBundleNode(NodeViewModel? node, out BaseQuestViewModel live, out string reason)
    {
        live = null!;
        reason = "Choose a live quest node in the currently displayed graph scope.";
        if (_disposedValue || GraphType != RedGraphType.Quest || _data is not graphGraphDefinition graph ||
            node is not BaseQuestViewModel quest || quest.Data is not questNodeDefinition definition ||
            !Nodes.Contains(quest) || quest.UniqueId != (ushort)definition.Id ||
            graph.Nodes.Count(handle => ReferenceEquals(handle.GetValue(), definition)) != 1 ||
            Nodes.Count(item => ReferenceEquals(item.Data, definition)) != 1) return false;
        live = quest;
        reason = string.Empty;
        return true;
    }

    private bool TryResolveQuestConnectionBundle(QuestConnectionBundle? bundle, out BaseQuestViewModel anchor,
        out QuestBundleLiveEdge[] edges, out string reason)
    {
        anchor = null!;
        edges = [];
        reason = "This connection bundle is cancelled, consumed, stale or belongs to another graph scope.";
        if (_disposedValue || bundle is null || bundle.IsCancelled || bundle.IsConsumed ||
            bundle.Owner is null || !bundle.Owner.TryGetTarget(out var owner) || !ReferenceEquals(owner, this) ||
            bundle.Graph is null || !bundle.Graph.TryGetTarget(out var graph) || !ReferenceEquals(graph, _data) ||
            bundle.Anchor is null || !bundle.Anchor.TryGetTarget(out var sourceNode) ||
            !TryGetLiveQuestBundleNode(sourceNode, out anchor, out _) || sourceNode.UniqueId != bundle.AnchorNodeId) return false;
        try
        {
            if (bundle.Layouts.Any(layout => !IsQuestBundleLayoutCurrent(layout))) return false;
            var result = new List<QuestBundleLiveEdge>();
            foreach (var capture in bundle.Edges)
            {
                if (!capture.Visual.TryGetTarget(out var visual) || !capture.Definition.TryGetTarget(out var definition) ||
                    !capture.Source.TryGetTarget(out var output) || !capture.Target.TryGetTarget(out var input) ||
                    !capture.SourceHandle.TryGetTarget(out var sourceHandle) || !capture.TargetHandle.TryGetTarget(out var targetHandle) ||
                    !capture.SourceEndpoint.TryGetTarget(out var sourceEndpoint) || !capture.TargetEndpoint.TryGetTarget(out var targetEndpoint) ||
                    !capture.SourceConnections.TryGetTarget(out var sourceArray) || !capture.TargetConnections.TryGetTarget(out var targetArray) ||
                    !ReferenceEquals(visual.ConnectionDefinition, definition) || !ReferenceEquals(visual.Source, output) ||
                    !ReferenceEquals(visual.Target, input) || !ReferenceEquals(definition.Source, sourceEndpoint) ||
                    !ReferenceEquals(definition.Destination, targetEndpoint) ||
                    !ReferenceEquals(output.Data.Connections, sourceArray) || !ReferenceEquals(input.Data.Connections, targetArray) ||
                    output.Data.Name != capture.SourceName || input.Data.Name != capture.TargetName ||
                    !CanStartQuestConnectionRewire(visual, out _) ||
                    !ReferenceEquals(output.Data.Connections.Single(handle => ReferenceEquals(handle.GetValue(), definition)), sourceHandle) ||
                    !ReferenceEquals(input.Data.Connections.Single(handle => ReferenceEquals(handle.GetValue(), definition)), targetHandle)) return false;
                if ((capture.SourceSlot is not null && (!sourceNode.Output.Contains(output) ||
                        !capture.SourceSlot.OriginalPort.TryGetTarget(out var originalOutput) || !ReferenceEquals(GetQuestRewireSocket(originalOutput), output.Data))) ||
                    (capture.TargetSlot is not null && (!sourceNode.Input.Contains(input) ||
                        !capture.TargetSlot.OriginalPort.TryGetTarget(out var originalInput) || !ReferenceEquals(GetQuestRewireSocket(originalInput), input.Data)))) return false;
                result.Add(new QuestBundleLiveEdge(capture, visual, definition, output, input, sourceHandle, targetHandle));
            }
            if (result.Count == 0 || result.Count != bundle.ConnectionCount ||
                !string.Equals(bundle.MetadataJson, CaptureQuestBundleMetadata(result.Select(edge => edge.Definition)), StringComparison.Ordinal)) return false;
            edges = result.ToArray();
            reason = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            _loggerService?.Error(exception);
            return false;
        }
    }
    private bool TryPrepareQuestBundlePaste(QuestConnectionBundle? bundle, NodeViewModel? destination,
        IReadOnlyDictionary<QuestConnectionBundleSocket, BaseConnectorViewModel>? mapping,
        out QuestBundlePlannedEdge[] plan, out string reason)
    {
        plan = [];
        if (!TryResolveQuestConnectionBundle(bundle, out _, out var originals, out reason) ||
            !TryGetQuestConnectionBundleSocketMappings(bundle, destination, out var choices, out reason)) return false;
        try
        {
            if (mapping is not null && mapping.Keys.Any(slot => !bundle!.Sockets.Any(item => ReferenceEquals(item, slot))))
            {
                reason = "The socket mapping contains a slot from a different bundle.";
                return false;
            }
            var resolved = new Dictionary<QuestConnectionBundleSocket, BaseConnectorViewModel>(ReferenceEqualityComparer.Instance);
            var used = new HashSet<questSocketDefinition>(ReferenceEqualityComparer.Instance);
            foreach (var choice in choices)
            {
                var port = mapping is not null && mapping.TryGetValue(choice.Socket, out var explicitPort) ? explicitPort : choice.Suggested;
                var data = port is null ? null : GetQuestRewireSocket(port);
                if (data is null || !IsLiveQuestRewireSocket(port!) ||
                    !choice.Choices.Any(item => ReferenceEquals(GetQuestRewireSocket(item), data)) ||
                    (choice.Socket.IsOutput ? port is not QuestOutputConnectorViewModel || data.Type != Enums.questSocketType.Output
                        : port is not QuestInputConnectorViewModel || data.Type != Enums.questSocketType.Input))
                {
                    reason = $"Choose an explicit compatible destination socket for {choice.Socket.Name}.";
                    return false;
                }
                if (!used.Add(data))
                {
                    reason = "Different original sockets must map to different destination sockets.";
                    return false;
                }
                resolved.Add(choice.Socket, port!);
            }
            var pending = originals.Select((edge, index) => new QuestBundlePlannedEdge(index, edge,
                edge.Capture.SourceSlot is { } sourceSlot ? (QuestOutputConnectorViewModel)resolved[sourceSlot] : edge.Source,
                edge.Capture.TargetSlot is { } targetSlot ? (QuestInputConnectorViewModel)resolved[targetSlot] : edge.Target)).ToArray();
            var groups = pending.GroupBy(edge => (edge.Source.Data, edge.Target.Data), QuestBundleSocketPairComparer.Instance).ToArray();
            var selectedDefinitions = new HashSet<graphGraphConnectionDefinition>(originals.Select(edge => edge.Definition), ReferenceEqualityComparer.Instance);
            foreach (var group in groups)
            {
                if (group.Select(edge => (edge.Original.Source.Data, edge.Original.Target.Data))
                    .Distinct(QuestBundleSocketPairComparer.Instance).Count() != 1)
                {
                    reason = "Different original relationships would collapse onto the same destination socket pair.";
                    return false;
                }
                var first = group.First();
                if (!TryFindQuestFlowPairs(first.Source, first.Target, out var existing, out reason)) return false;
                if (bundle!.Operation == QuestConnectionBundleOperation.Copy)
                {
                    foreach (var edge in group) edge.AlreadyExists = existing;
                }
                else if (Connections.OfType<QuestConnectionViewModel>().Any(edge =>
                    !selectedDefinitions.Contains(edge.ConnectionDefinition) &&
                    edge.Source is QuestOutputConnectorViewModel output && ReferenceEquals(output.Data, first.Source.Data) &&
                    edge.Target is QuestInputConnectorViewModel input && ReferenceEquals(input.Data, first.Target.Data)))
                {
                    reason = "A connection already joins a moved pair. The pending cut cannot replace existing connections.";
                    return false;
                }
            }
            var deltas = new Dictionary<questSocketDefinition, int>(ReferenceEqualityComparer.Instance);
            void Delta(questSocketDefinition socket, int amount) =>
                deltas[socket] = deltas.GetValueOrDefault(socket) + amount;
            foreach (var edge in pending)
            {
                if (bundle!.Operation == QuestConnectionBundleOperation.Copy)
                {
                    if (edge.AlreadyExists) continue;
                    Delta(edge.Source.Data, 1);
                    Delta(edge.Target.Data, 1);
                }
                else
                {
                    if (!ReferenceEquals(edge.Original.Source.Data, edge.Source.Data))
                    {
                        Delta(edge.Original.Source.Data, -1);
                        Delta(edge.Source.Data, 1);
                    }
                    if (!ReferenceEquals(edge.Original.Target.Data, edge.Target.Data))
                    {
                        Delta(edge.Original.Target.Data, -1);
                        Delta(edge.Target.Data, 1);
                    }
                }
            }
            if (deltas.Any(pair => pair.Key.Connections.Count + (long)pair.Value < 0 ||
                (pair.Key.Connections.MaxSize >= 0 && pair.Key.Connections.Count + (long)pair.Value > pair.Key.Connections.MaxSize)))
            {
                reason = "A socket cannot accept the entire connection bundle.";
                return false;
            }
            plan = pending;
            reason = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            _loggerService?.Error(exception);
            reason = "The connection mapping could not be validated; the graph was not changed.";
            return false;
        }
    }

    private static bool IsQuestBundleDirectionSocket(questSocketDefinition socket, QuestConnectionBundleDirection direction) =>
        (direction != QuestConnectionBundleDirection.Incoming && socket.Type == Enums.questSocketType.Output) ||
        (direction != QuestConnectionBundleDirection.Outgoing && socket.Type == Enums.questSocketType.Input);

    private static bool IsQuestBundleCutConnection(graphGraphConnectionDefinition definition) =>
        definition.Source?.GetValue() is questSocketDefinition source &&
            (source.Type == Enums.questSocketType.CutSource || source.Type == Enums.questSocketType.CutDestination) ||
        definition.Destination?.GetValue() is questSocketDefinition target &&
            (target.Type == Enums.questSocketType.CutSource || target.Type == Enums.questSocketType.CutDestination);

    private static string CaptureQuestBundleMetadata(IEnumerable<graphGraphConnectionDefinition> definitions)
    {
        var shells = definitions.Select(definition =>
        {
            var shell = (graphGraphConnectionDefinition)RedTypeManager.Create(definition.GetType());
            var properties = RedReflection.GetTypeInfo(definition).PropertyInfos.Concat(definition.GetDynamicProperties());
            foreach (var property in properties)
            {
                if (property.RedName is not { } name || name is "source" or "destination") continue;
                var value = definition.GetProperty(name);
                ValidateQuestBundleMetadata(value, new HashSet<object>(ReferenceEqualityComparer.Instance), 0);
                if (property.IsDynamic) shell.AddDynamicProperty(name, property.Type);
                shell.SetProperty(name, value);
            }
            shell.Source = new CWeakHandle<graphGraphSocketDefinition>();
            shell.Destination = new CWeakHandle<graphGraphSocketDefinition>();
            return shell;
        }).ToArray();
        try
        {
            return RedJsonSerializer.Serialize(shells);
        }
        catch
        {
            // Serialize does not clean its per-thread resolvers on failure; flush that state.
            try { RedJsonSerializer.Serialize(new object()); }
            catch { /* Preserve the original capture failure. */ }
            throw;
        }
    }

    private static void ValidateQuestBundleMetadata(object? value, HashSet<object> visited, int depth)
    {
        if (value is null || value is string || value.GetType().IsValueType) return;
        if (value is graphIGraphObjectDefinition)
            throw new InvalidOperationException("Connection metadata cannot own or reference graph objects.");
        if (!visited.Add(value)) return;
        if (depth >= 256) throw new InvalidOperationException("Connection metadata is nested too deeply.");
        switch (value)
        {
            case IRedBaseHandle handle:
                ValidateQuestBundleMetadata(handle.GetValue(), visited, depth + 1);
                break;
            case RedBaseClass instance:
                foreach (var property in RedReflection.GetTypeInfo(instance).PropertyInfos.Concat(instance.GetDynamicProperties()))
                    if (property.RedName is { } name) ValidateQuestBundleMetadata(instance.GetProperty(name), visited, depth + 1);
                break;
            case CVariant variant:
                ValidateQuestBundleMetadata(variant.Value, visited, depth + 1);
                break;
            case CKeyValuePair pair:
                ValidateQuestBundleMetadata(pair.Value, visited, depth + 1);
                break;
            case IEnumerable array:
                foreach (var item in array) ValidateQuestBundleMetadata(item, visited, depth + 1);
                break;
            case IRedType:
                foreach (var property in value.GetType().GetProperties().Where(property =>
                    property.CanRead && property.GetIndexParameters().Length == 0 &&
                    (typeof(IRedType).IsAssignableFrom(property.PropertyType) || typeof(IEnumerable).IsAssignableFrom(property.PropertyType))))
                    ValidateQuestBundleMetadata(property.GetValue(value), visited, depth + 1);
                break;
        }
    }

    private static void RemoveQuestBundleHandle(questSocketDefinition socket, CHandle<graphGraphConnectionDefinition> handle)
    {
        var index = Enumerable.Range(0, socket.Connections.Count).Single(index => ReferenceEquals(socket.Connections[index], handle));
        socket.Connections.RemoveAt(index);
    }

    private void RestoreQuestBundleVisuals(ConnectionViewModel[] originals)
    {
        for (var index = Connections.Count - 1; index >= originals.Length; index--)
        {
            var savedIndex = index;
            RestoreQuestBundleStep(() => Connections.RemoveAt(savedIndex));
        }
        for (var index = 0; index < originals.Length; index++)
        {
            var savedIndex = index;
            if (index < Connections.Count)
            {
                if (!ReferenceEquals(Connections[index], originals[index]))
                    RestoreQuestBundleStep(() => Connections[savedIndex] = originals[savedIndex]);
            }
            else RestoreQuestBundleStep(() => Connections.Add(originals[savedIndex]));
        }
    }

    private static void RestoreQuestBundleStep(Action restore)
    {
        try { restore(); }
        catch (Exception exception) { _loggerService?.Error(exception); }
    }

    private sealed record QuestBundleLiveEdge(QuestConnectionBundleEdgeCapture Capture, QuestConnectionViewModel Visual,
        graphGraphConnectionDefinition Definition, QuestOutputConnectorViewModel Source, QuestInputConnectorViewModel Target,
        CHandle<graphGraphConnectionDefinition> SourceHandle, CHandle<graphGraphConnectionDefinition> TargetHandle);

    private sealed class QuestBundlePlannedEdge
    {
        internal int Index { get; }
        internal QuestBundleLiveEdge Original { get; }
        internal QuestOutputConnectorViewModel Source { get; }
        internal QuestInputConnectorViewModel Target { get; }
        internal bool AlreadyExists { get; set; }
        internal bool IntroducesSelfConnection => !AlreadyExists && Source.OwnerId == Target.OwnerId && Original.Source.OwnerId != Original.Target.OwnerId;
        internal QuestBundlePlannedEdge(int index, QuestBundleLiveEdge original,
            QuestOutputConnectorViewModel source, QuestInputConnectorViewModel target)
        {
            Index = index;
            Original = original;
            Source = source;
            Target = target;
        }
    }

    private sealed class QuestBundleSocketPairComparer : IEqualityComparer<(questSocketDefinition Source, questSocketDefinition Target)>
    {
        internal static readonly QuestBundleSocketPairComparer Instance = new();
        public bool Equals((questSocketDefinition Source, questSocketDefinition Target) left,
            (questSocketDefinition Source, questSocketDefinition Target) right) =>
            ReferenceEquals(left.Source, right.Source) && ReferenceEquals(left.Target, right.Target);
        public int GetHashCode((questSocketDefinition Source, questSocketDefinition Target) value) =>
            HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value.Source),
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value.Target));
    }
}
