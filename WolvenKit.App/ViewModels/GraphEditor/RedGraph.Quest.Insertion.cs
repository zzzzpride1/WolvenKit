using System;
using System.Collections.Generic;
using System.Linq;
using WolvenKit.App.ViewModels.GraphEditor.Nodes.Quest;
using WolvenKit.App.ViewModels.GraphEditor.Nodes.Quest.Internal;
using WolvenKit.Common.Model;
using WolvenKit.RED4.Types;

namespace WolvenKit.App.ViewModels.GraphEditor;

public partial class RedGraph
{
    /// <summary>
    /// Creates a detached node using the normal node/template and socket initialization.
    /// No graph data, dirty state or ID allocation is committed until the node is added.
    /// The caller owns the candidate and must dispose it if insertion is cancelled.
    /// </summary>
    public BaseQuestViewModel PrepareQuestNode(Type type, System.Windows.Point position,
        RedTypeTemplateSelectionOption? template = null)
    {
        if (GraphType != RedGraphType.Quest || !typeof(questNodeDefinition).IsAssignableFrom(type) || type.IsAbstract)
        {
            throw new ArgumentException("A concrete quest node type is required.", nameof(type));
        }

        var id = GetNextAvailableQuestNodeId();
        if (id == ushort.MaxValue)
        {
            throw new InvalidOperationException("No available quest node IDs remaining.");
        }

        var definition = InternalCreateQuestNode(type, template, id);
        var node = WrapQuestNode(definition, true);
        node.Location = position;
        return node;
    }

    public static IReadOnlyList<QuestInputConnectorViewModel> GetQuestInsertionInputs(BaseQuestViewModel node) =>
        node.Input.OfType<QuestInputConnectorViewModel>()
            .Where(port => port.Data.Type == Enums.questSocketType.Input && HasQuestBackingSocket(node, port.Data))
            .ToArray();

    public static IReadOnlyList<QuestOutputConnectorViewModel> GetQuestInsertionOutputs(BaseQuestViewModel node) =>
        node.Output.OfType<QuestOutputConnectorViewModel>()
            .Where(port => port.Data.Type == Enums.questSocketType.Output && HasQuestBackingSocket(node, port.Data))
            .ToArray();

    private static bool HasQuestBackingSocket(NodeViewModel node, questSocketDefinition socket) =>
        node.Data is graphGraphNodeDefinition definition &&
        definition.Sockets.Any(handle => ReferenceEquals(handle.GetValue(), socket));

    /// <summary>Only live, reciprocal, ordinary flow connections can be split; cut links retain their semantics.</summary>
    public bool CanInsertQuestNodeOnConnection(QuestConnectionViewModel connection)
    {
        if (GraphType != RedGraphType.Quest || !Connections.Contains(connection) ||
            connection.Source is not QuestOutputConnectorViewModel source ||
            connection.Target is not QuestInputConnectorViewModel target ||
            source.Data.Type != Enums.questSocketType.Output || target.Data.Type != Enums.questSocketType.Input)
        {
            return false;
        }

        return Nodes.Any(node => node.Output.Contains(source) && HasQuestBackingSocket(node, source.Data)) &&
               Nodes.Any(node => node.Input.Contains(target) && HasQuestBackingSocket(node, target.Data)) &&
               ReferenceEquals(connection.ConnectionDefinition.Source.GetValue(), source.Data) &&
               ReferenceEquals(connection.ConnectionDefinition.Destination.GetValue(), target.Data) &&
               source.Data.Connections.Count(handle => ReferenceEquals(handle.GetValue(), connection.ConnectionDefinition)) == 1 &&
               target.Data.Connections.Count(handle => ReferenceEquals(handle.GetValue(), connection.ConnectionDefinition)) == 1;
    }

    /// <summary>
    /// Inserts a detached candidate on one edge, preserving both original endpoint sockets and all other edges.
    /// Ambiguous sockets require an explicit choice. Rejection leaves the candidate owned by its caller.
    /// </summary>
    public bool TryInsertQuestNode(QuestConnectionViewModel connection, BaseQuestViewModel node,
        QuestInputConnectorViewModel? input = null, QuestOutputConnectorViewModel? output = null)
    {
        if (!CanInsertQuestNodeOnConnection(connection) || _data is not graphGraphDefinition graph ||
            node.Data is not questNodeDefinition definition || node.UniqueId != (ushort)definition.Id ||
            node.UniqueId >= ushort.MaxValue || Nodes.Contains(node) ||
            graph.Nodes.Any(handle => ReferenceEquals(handle.GetValue(), definition)) ||
            IsQuestNodeIdInUse(definition.Id, graph) ||
            definition.Sockets.Any(handle => handle.GetValue() is not questSocketDefinition socket || socket.Connections.Count != 0))
        {
            return false;
        }

        var inputs = GetQuestInsertionInputs(node);
        var outputs = GetQuestInsertionOutputs(node);
        input ??= inputs.Count == 1 ? inputs[0] : null;
        output ??= outputs.Count == 1 ? outputs[0] : null;
        if (input is null || output is null || !inputs.Contains(input) || !outputs.Contains(output) ||
            input.OwnerId != node.UniqueId || output.OwnerId != node.UniqueId)
        {
            return false;
        }

        var source = (QuestOutputConnectorViewModel)connection.Source;
        var target = (QuestInputConnectorViewModel)connection.Target;
        var originalSourceHandles = source.Data.Connections.ToArray();
        var originalTargetHandles = target.Data.Connections.ToArray();
        var originalConnections = Connections.ToArray();
        var originalId = CurrentQuestNodeId;
        var wasDirty = DocumentViewModel?.IsDirty;
        var nodeHandle = new CHandle<graphGraphNodeDefinition>(definition);

        try
        {
            // Complete validation precedes mutation. Defer notifications until both edges are present.
            graph.Nodes.Add(nodeHandle);
            Nodes.Add(node);
            AddQuestConnection(source, input, notify: false);
            AddQuestConnection(output, target, notify: false);
            RemoveQuestConnection(connection, notify: false);
            RaiseCurrentQuestNodeId(definition.Id);

            GetQuestNodesChunkViewModel()?.RecalculateProperties();
            RefreshCVM([source.Data, input.Data, output.Data, target.Data]);
            NotifyNodesUpdated(source.OwnerId, node.UniqueId, target.OwnerId);
            node.UpdateSocketVisibility();
            Nodes.First(owner => owner.Output.Contains(source)).UpdateSocketVisibility();
            Nodes.First(owner => owner.Input.Contains(target)).UpdateSocketVisibility();
            DocumentViewModel?.SetIsDirty(true);
            return true;
        }
        catch (Exception exception)
        {
            // Preserve exact original handles/definitions even if a collection or refresh callback fails.
            source.Data.Connections.Clear();
            foreach (var handle in originalSourceHandles)
            {
                source.Data.Connections.Add(handle);
            }
            target.Data.Connections.Clear();
            foreach (var handle in originalTargetHandles)
            {
                target.Data.Connections.Add(handle);
            }
            foreach (var handle in definition.Sockets)
            {
                ((questSocketDefinition)handle.GetValue()!).Connections.Clear();
            }
            Connections.Clear();
            foreach (var original in originalConnections)
            {
                Connections.Add(original);
            }
            Nodes.Remove(node);
            graph.Nodes.Remove(nodeHandle);
            CurrentQuestNodeId = originalId;
            input.IsConnected = false;
            output.IsConnected = false;
            source.IsConnected = source.Data.Connections.Count > 0;
            target.IsConnected = target.Data.Connections.Count > 0;
            if (wasDirty is { } dirty)
            {
                DocumentViewModel?.SetIsDirty(dirty);
            }
            _loggerService?.Error(exception);
            return false;
        }
    }
}
