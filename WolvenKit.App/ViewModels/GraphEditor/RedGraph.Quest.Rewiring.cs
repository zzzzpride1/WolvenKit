using System;
using System.Collections.Generic;
using System.Linq;
using WolvenKit.App.ViewModels.GraphEditor.Nodes;
using WolvenKit.App.ViewModels.GraphEditor.Nodes.Quest;
using WolvenKit.App.ViewModels.GraphEditor.Nodes.Quest.Internal;
using WolvenKit.RED4.Types;

namespace WolvenKit.App.ViewModels.GraphEditor;

public enum QuestConnectionEndpoint { Source, Destination }

public partial class RedGraph
{
    /// <summary>Only live reciprocal flow or Cut links can be moved; endpoint types and graph scope are preserved.</summary>
    public bool CanStartQuestConnectionRewire(QuestConnectionViewModel? connection, out string reason)
    {
        reason = "The connection is no longer part of the currently displayed quest graph.";
        if (GraphType != RedGraphType.Quest || connection is null || !Connections.Contains(connection) ||
            connection.Source is not QuestOutputConnectorViewModel source ||
            connection.Target is not QuestInputConnectorViewModel target)
        {
            return false;
        }
        // Vanilla quest graphs use these two distinct relationships. Never convert between them.
        if (!((source.Data.Type == Enums.questSocketType.Output && target.Data.Type == Enums.questSocketType.Input) ||
              (source.Data.Type == Enums.questSocketType.CutSource && target.Data.Type == Enums.questSocketType.CutDestination)))
        {
            reason = "Only Output/Input and CutSource/CutDestination connections can be rewired.";
            return false;
        }
        if (!IsLiveQuestRewireSocket(source) || !IsLiveQuestRewireSocket(target) ||
            !ReferenceEquals(connection.ConnectionDefinition.Source?.GetValue(), source.Data) ||
            !ReferenceEquals(connection.ConnectionDefinition.Destination?.GetValue(), target.Data) ||
            source.Data.Connections.Count(handle => ReferenceEquals(handle.GetValue(), connection.ConnectionDefinition)) != 1 ||
            target.Data.Connections.Count(handle => ReferenceEquals(handle.GetValue(), connection.ConnectionDefinition)) != 1 ||
            Connections.OfType<QuestConnectionViewModel>().Count(edge => ReferenceEquals(edge.ConnectionDefinition, connection.ConnectionDefinition)) != 1)
        {
            reason = "The connection or its sockets are stale or inconsistent. Reopen the graph before rewiring.";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    /// <summary>Read-only validation, also used for interactive socket feedback. Occupied inputs may accept additional edges.</summary>
    public bool CanRewireQuestConnection(QuestConnectionViewModel connection, QuestConnectionEndpoint endpoint,
        BaseConnectorViewModel? target, out string reason)
    {
        if (!CanStartQuestConnectionRewire(connection, out reason)) return false;
        if (endpoint is not (QuestConnectionEndpoint.Source or QuestConnectionEndpoint.Destination))
        {
            reason = "Choose a source or destination endpoint.";
            return false;
        }
        if (target is null)
        {
            reason = "Drop on a compatible socket; empty canvas does not change the connection.";
            return false;
        }
        var socket = GetQuestRewireSocket(target);
        var originalSocket = GetQuestRewireSocket(endpoint == QuestConnectionEndpoint.Source ? connection.Source : connection.Target)!;
        if ((endpoint == QuestConnectionEndpoint.Source ? target is not QuestOutputConnectorViewModel : target is not QuestInputConnectorViewModel) ||
            socket is null || !socket.Type.Equals(originalSocket.Type))
        {
            reason = $"The replacement must keep the original {originalSocket.Type} socket type.";
            return false;
        }
        if (!IsLiveQuestRewireSocket(target))
        {
            reason = "The target socket must belong to a live node in the same quest graph scope.";
            return false;
        }
        if (ReferenceEquals(target, endpoint == QuestConnectionEndpoint.Source ? connection.Source : connection.Target))
        {
            reason = string.Empty;
            return true;
        }
        var source = endpoint == QuestConnectionEndpoint.Source ? (QuestOutputConnectorViewModel)target : (QuestOutputConnectorViewModel)connection.Source;
        var destination = endpoint == QuestConnectionEndpoint.Destination ? (QuestInputConnectorViewModel)target : (QuestInputConnectorViewModel)connection.Target;
        // Inspect backing handles as well: a hidden/stale visual edge must not allow a duplicate pair.
        if (source.Data.Connections.Concat(destination.Data.Connections).Any(handle =>
                handle.GetValue() is graphGraphConnectionDefinition edge && !ReferenceEquals(edge, connection.ConnectionDefinition) &&
                ReferenceEquals(edge.Source?.GetValue(), source.Data) && ReferenceEquals(edge.Destination?.GetValue(), destination.Data)) ||
            Connections.Any(edge => !ReferenceEquals(edge, connection) && ReferenceEquals(edge.Source, source) && ReferenceEquals(edge.Target, destination)))
        {
            reason = "A connection already joins these exact sockets. Other connections will not be replaced.";
            return false;
        }
        if (socket!.Connections.MaxSize >= 0 && socket.Connections.Count >= socket.Connections.MaxSize)
        {
            reason = "The target socket cannot accept another connection.";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Moves one endpoint, preserving the concrete RED definition and both existing handle wrappers.
    /// Only the visual connection is replaced, since its endpoints are immutable. No toggle-connect logic is used.
    /// </summary>
    public bool TryRewireQuestConnection(QuestConnectionViewModel connection, QuestConnectionEndpoint endpoint,
        BaseConnectorViewModel? target, out string reason)
    {
        if (!CanRewireQuestConnection(connection, endpoint, target, out reason)) return false;
        var oldPort = endpoint == QuestConnectionEndpoint.Source ? (BaseConnectorViewModel)connection.Source : connection.Target;
        if (ReferenceEquals(oldPort, target)) return true;
        var oldSocket = GetQuestRewireSocket(oldPort)!;
        var newSocket = GetQuestRewireSocket(target!)!;
        var oldHandles = oldSocket.Connections.ToArray();
        var newHandles = newSocket.Connections.ToArray();
        var definition = connection.ConnectionDefinition;
        var oldSource = definition.Source;
        var oldDestination = definition.Destination;
        var handleIndex = Array.FindIndex(oldHandles, handle => ReferenceEquals(handle.GetValue(), definition));
        var index = Connections.IndexOf(connection);
        var affected = Nodes.OfType<BaseQuestViewModel>()
            .Where(node => node.Input.Contains(connection.Target) || node.Output.Contains(connection.Source) ||
                           node.Input.Contains(target) || node.Output.Contains(target)).ToArray();
        var flags = affected.SelectMany(node => node.Input.Cast<BaseConnectorViewModel>().Concat(node.Output))
            .Select(port => (Port: port, Connected: port.IsConnected, Visible: port.IsVisible)).ToArray();
        var wasDirty = DocumentViewModel?.IsDirty;
        try
        {
            // Move exactly this edge's handle by index; structural equality would match parallel edges too.
            oldSocket.Connections.RemoveAt(handleIndex);
            newSocket.Connections.Add(oldHandles[handleIndex]);
            if (endpoint == QuestConnectionEndpoint.Source)
                definition.Source = new CWeakHandle<graphGraphSocketDefinition>(newSocket);
            else
                definition.Destination = new CWeakHandle<graphGraphSocketDefinition>(newSocket);

            var source = endpoint == QuestConnectionEndpoint.Source ? (QuestOutputConnectorViewModel)target! : connection.Source;
            var destination = endpoint == QuestConnectionEndpoint.Destination ? (QuestInputConnectorViewModel)target! : connection.Target;
            var replacement = new QuestConnectionViewModel(source, destination, definition)
            {
                PathType = connection.PathType,
                IsSelected = connection.IsSelected
            };
            oldPort.IsConnected = oldSocket.Connections.Count > 0;
            Connections[index] = replacement;
            foreach (var owner in affected) owner.UpdateSocketVisibility();
            // Global node refresh may regenerate/prune sockets (for example deferred Workspot outputs).
            // A connection-only edit needs non-structural Data notifications, including the fixed endpoint.
            var fixedSocket = GetQuestRewireSocket(endpoint == QuestConnectionEndpoint.Source ? connection.Target : connection.Source)!;
            RefreshCVM([oldSocket, newSocket, fixedSocket]);
            DocumentViewModel?.SetIsDirty(true);
            reason = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            // Restore data before publishing the original visual edge. A failing notification must not
            // prevent the remaining restoration steps (ObservableCollection mutates before notifying).
            definition.Source = oldSource;
            definition.Destination = oldDestination;
            oldSocket.Connections.Clear();
            foreach (var handle in oldHandles) oldSocket.Connections.Add(handle);
            newSocket.Connections.Clear();
            foreach (var handle in newHandles) newSocket.Connections.Add(handle);
            try { Connections[index] = connection; }
            catch (Exception notificationFailure) { _loggerService?.Error(notificationFailure); }
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
            reason = "The connection could not be rewired; the original connection was restored.";
            return false;
        }
    }

    private static questSocketDefinition? GetQuestRewireSocket(BaseConnectorViewModel port) => port switch
    {
        QuestOutputConnectorViewModel output => output.Data,
        QuestInputConnectorViewModel input => input.Data,
        _ => null
    };

    private bool IsLiveQuestRewireSocket(BaseConnectorViewModel port)
    {
        if (_data is not graphGraphDefinition graph || GetQuestRewireSocket(port) is not { } socket) return false;
        var owners = Nodes.OfType<BaseQuestViewModel>().Where(node => node.UniqueId == port.OwnerId &&
            (port is QuestOutputConnectorViewModel ? node.Output.Contains(port) : node.Input.Contains(port))).ToArray();
        return owners.Length == 1 && owners[0].Data is questNodeDefinition definition && owners[0].UniqueId == (ushort)definition.Id &&
               graph.Nodes.Count(handle => ReferenceEquals(handle.GetValue(), definition)) == 1 &&
               definition.Sockets.Count(handle => ReferenceEquals(handle.GetValue(), socket)) == 1;
    }
}
