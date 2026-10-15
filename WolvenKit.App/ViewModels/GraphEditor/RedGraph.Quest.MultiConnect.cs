using System;
using System.Collections.Generic;
using System.Linq;
using WolvenKit.App.ViewModels.GraphEditor.Nodes;
using WolvenKit.App.ViewModels.GraphEditor.Nodes.Quest;
using WolvenKit.App.ViewModels.GraphEditor.Nodes.Quest.Internal;
using WolvenKit.RED4.Types;

namespace WolvenKit.App.ViewModels.GraphEditor;

public partial class RedGraph
{
    public bool CanStartQuestMultiConnect(BaseConnectorViewModel? source, out string reason)
    {
        reason = "Start Shift-swipe from a live ordinary output socket in this quest graph.";
        if (_disposedValue || GraphType != RedGraphType.Quest || source is not QuestOutputConnectorViewModel output ||
            output.Data.Type != Enums.questSocketType.Output || !IsLiveQuestRewireSocket(output)) return false;
        reason = string.Empty;
        return true;
    }

    /// <summary>Valid existing pairs are accepted without toggling; occupied inputs retain their other edges.</summary>
    public bool CanConnectQuestFlow(BaseConnectorViewModel? source, BaseConnectorViewModel? target, out string reason)
    {
        if (!CanStartQuestMultiConnect(source, out reason)) return false;
        if (target is not QuestInputConnectorViewModel input || input.Data.Type != Enums.questSocketType.Input ||
            !IsLiveQuestRewireSocket(input))
        {
            reason = "Choose a real ordinary input in the same quest graph. Cut and synthetic sockets are not supported.";
            return false;
        }
        var output = (QuestOutputConnectorViewModel)source!;
        if (!TryFindQuestFlowPairs(output, input, out var existing, out reason)) return false;
        if (existing) return true;
        if (!HasQuestConnectionCapacity(output.Data, 1) || !HasQuestConnectionCapacity(input.Data, 1))
        {
            reason = "The source or target socket cannot accept another connection.";
            return false;
        }
        return true;
    }

    /// <summary>Node-body targeting never guesses between multiple eligible inputs or creates automatic self-links.</summary>
    public QuestInputConnectorViewModel? GetQuestMultiConnectTarget(BaseConnectorViewModel? source, NodeViewModel? node, out string reason)
    {
        if (!CanStartQuestMultiConnect(source, out reason)) return null;
        if (node is not BaseQuestViewModel quest || !Nodes.Contains(quest) || node.Output.Contains(source))
        {
            reason = "Pass over another quest node or choose an explicit input socket.";
            return null;
        }
        var candidates = GetQuestInsertionInputs(quest)
            .Where(input => CanConnectQuestFlow(source, input, out _))
            .DistinctBy(input => input.Data, ReferenceEqualityComparer.Instance).ToArray();
        reason = candidates.Length switch
        {
            0 => "This node has no compatible input socket.",
            1 => string.Empty,
            _ => "Multiple inputs are compatible; pass over the intended input socket."
        };
        return candidates.Length == 1 ? candidates[0] : null;
    }

    public bool CanConnectMultipleQuestNodes(BaseConnectorViewModel? source,
        IEnumerable<BaseConnectorViewModel?>? targets, out string reason) =>
        TryPrepareQuestMultiConnect(source, targets, out _, out reason);

    /// <summary>
    /// Adds one atomic batch using the existing add helper, never the toggle-connect command.
    /// Invalid members reject the batch. Interactive callers skip invalid hover targets before calling this method.
    /// </summary>
    public bool TryConnectMultipleQuestNodes(BaseConnectorViewModel? source,
        IEnumerable<BaseConnectorViewModel?>? targets, out string reason)
    {
        if (!TryPrepareQuestMultiConnect(source, targets, out var additions, out reason)) return false;
        if (additions.Length == 0) return true;
        var output = (QuestOutputConnectorViewModel)source!;
        var sockets = additions.Select(input => input.Data).Prepend(output.Data).ToArray();
        var arrays = sockets.Select(socket => (Socket: socket, Handles: socket.Connections.ToArray())).ToArray();
        var affected = Nodes.OfType<BaseQuestViewModel>().Where(node => node.Output.Contains(output) ||
            node.Input.OfType<QuestInputConnectorViewModel>().Any(input => sockets.Any(socket => ReferenceEquals(input.Data, socket)))).ToArray();
        var flags = affected.SelectMany(node => node.Input.Cast<BaseConnectorViewModel>().Concat(node.Output))
            .Select(port => (Port: port, Connected: port.IsConnected, Visible: port.IsVisible)).ToArray();
        var originalCount = Connections.Count;
        var wasDirty = DocumentViewModel?.IsDirty;
        try
        {
            foreach (var input in additions) AddQuestConnection(output, input, notify: false);
            foreach (var (port, _, _) in flags)
            {
                if (GetQuestRewireSocket(port) is { } socket && sockets.Any(item => ReferenceEquals(item, socket)))
                    port.IsConnected = socket.Connections.Count > 0;
            }
            foreach (var owner in affected) owner.UpdateSocketVisibility();
            // Connection-only refresh must not regenerate Workspot/dynamic sockets.
            RefreshCVM(sockets);
            DocumentViewModel?.SetIsDirty(true);
            reason = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            foreach (var (socket, handles) in arrays)
            {
                socket.Connections.Clear();
                foreach (var handle in handles) socket.Connections.Add(handle);
            }
            // The add helper only appends. Keep all pre-existing visual edges and their identities untouched.
            for (var index = Connections.Count - 1; index >= originalCount; index--)
            {
                try { Connections.RemoveAt(index); }
                catch (Exception notificationFailure) { _loggerService?.Error(notificationFailure); }
            }
            foreach (var (port, connected, visible) in flags)
            {
                try { port.IsConnected = connected; }
                catch (Exception notificationFailure) { _loggerService?.Error(notificationFailure); }
                try { port.IsVisible = visible; }
                catch (Exception notificationFailure) { _loggerService?.Error(notificationFailure); }
            }
            if (wasDirty is { } dirty)
            {
                try { DocumentViewModel?.SetIsDirty(dirty); }
                catch (Exception notificationFailure) { _loggerService?.Error(notificationFailure); }
            }
            foreach (var owner in affected)
            {
                try { owner.TriggerPropertyChanged(nameof(owner.Data)); }
                catch (Exception notificationFailure) { _loggerService?.Error(notificationFailure); }
            }
            _loggerService?.Error(exception);
            reason = "The batch could not be connected; every new connection was rolled back.";
            return false;
        }
    }

    private bool TryPrepareQuestMultiConnect(BaseConnectorViewModel? source,
        IEnumerable<BaseConnectorViewModel?>? targets, out QuestInputConnectorViewModel[] additions, out string reason)
    {
        additions = [];
        if (!CanStartQuestMultiConnect(source, out reason)) return false;
        if (targets is null)
        {
            reason = "A collection of input targets is required.";
            return false;
        }
        try
        {
            // Finish enumeration and validation before any mutation, including lazily produced targets.
            var candidates = targets.ToArray();
            var output = (QuestOutputConnectorViewModel)source!;
            var seen = new HashSet<questSocketDefinition>(ReferenceEqualityComparer.Instance);
            var pending = new List<QuestInputConnectorViewModel>();
            foreach (var target in candidates)
            {
                if (!CanConnectQuestFlow(output, target, out reason)) return false;
                var input = (QuestInputConnectorViewModel)target!;
                if (!seen.Add(input.Data)) continue;
                if (!TryFindQuestFlowPairs(output, input, out var existing, out reason)) return false;
                if (!existing) pending.Add(input);
            }
            if (!HasQuestConnectionCapacity(output.Data, pending.Count))
            {
                reason = "The source socket cannot accept the entire pending batch.";
                return false;
            }
            additions = pending.ToArray();
            reason = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            _loggerService?.Error(exception);
            reason = "The target collection could not be validated; the graph was not changed.";
            return false;
        }
    }

    private bool TryFindQuestFlowPairs(QuestOutputConnectorViewModel source, QuestInputConnectorViewModel target,
        out bool existing, out string reason)
    {
        // Compare actual backing sockets, including aliases, rather than socket names or VM equality.
        var definitions = source.Data.Connections.Concat(target.Data.Connections).Select(handle => handle.GetValue())
            .OfType<graphGraphConnectionDefinition>()
            .Where(edge => ReferenceEquals(edge.Source?.GetValue(), source.Data) && ReferenceEquals(edge.Destination?.GetValue(), target.Data))
            .Distinct(ReferenceEqualityComparer.Instance).ToArray();
        var visuals = Connections.OfType<QuestConnectionViewModel>().Where(edge =>
            edge.Source is QuestOutputConnectorViewModel output && ReferenceEquals(output.Data, source.Data) &&
            edge.Target is QuestInputConnectorViewModel input && ReferenceEquals(input.Data, target.Data)).ToArray();
        existing = definitions.Length > 0 || visuals.Length > 0;
        if (definitions.Length != visuals.Length || visuals.Any(edge =>
                !definitions.Any(definition => ReferenceEquals(definition, edge.ConnectionDefinition)) ||
                !CanStartQuestConnectionRewire(edge, out _)))
        {
            reason = "An existing connection is stale or inconsistent. Reopen the graph before connecting this pair.";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    private static bool HasQuestConnectionCapacity(questSocketDefinition socket, int additions) =>
        socket.Connections.MaxSize < 0 || socket.Connections.Count + (long)additions <= socket.Connections.MaxSize;
}
