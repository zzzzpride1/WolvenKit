using System;
using System.Collections.Generic;
using WolvenKit.App.ViewModels.GraphEditor.Nodes;
using WolvenKit.App.ViewModels.GraphEditor.Nodes.Quest;
using WolvenKit.App.ViewModels.GraphEditor.Nodes.Quest.Internal;
using WolvenKit.RED4.Types;

namespace WolvenKit.App.ViewModels.GraphEditor;

public enum QuestConnectionBundleDirection { All, Incoming, Outgoing }
public enum QuestConnectionBundleOperation { Copy, Cut }

/// <summary>A captured anchor socket. Live graph references in a clipboard are deliberately weak.</summary>
public sealed class QuestConnectionBundleSocket
{
    public string Name { get; internal set; } = string.Empty;
    public bool IsOutput { get; internal set; }
    internal CName RawName { get; init; }
    internal WeakReference<BaseConnectorViewModel> OriginalPort { get; init; } = null!;
}

/// <summary>
/// A same-scope connection clipboard. Capture and cancellation do not change the document.
/// Detached JSON is the only strongly retained RED payload.
/// </summary>
public sealed class QuestConnectionBundle
{
    public QuestConnectionBundleOperation Operation { get; internal set; }
    public QuestConnectionBundleDirection Direction { get; internal set; }
    public int ConnectionCount { get; internal set; }
    public uint AnchorNodeId { get; internal set; }
    public bool IsConsumed { get; internal set; }
    public bool IsCancelled { get; private set; }
    public IReadOnlyList<QuestConnectionBundleSocket> Sockets { get; internal set; } = [];

    internal WeakReference<RedGraph> Owner { get; init; } = null!;
    internal WeakReference<graphGraphDefinition> Graph { get; init; } = null!;
    internal WeakReference<BaseQuestViewModel> Anchor { get; init; } = null!;
    internal IReadOnlyList<QuestConnectionBundleEdgeCapture> Edges { get; init; } = [];
    internal IReadOnlyList<QuestConnectionBundleNodeLayoutCapture> Layouts { get; init; } = [];
    internal string MetadataJson { get; init; } = string.Empty;

    public void Cancel() => IsCancelled = true;
}

internal sealed class QuestConnectionBundleEdgeCapture
{
    internal WeakReference<QuestConnectionViewModel> Visual { get; init; } = null!;
    internal WeakReference<graphGraphConnectionDefinition> Definition { get; init; } = null!;
    internal WeakReference<QuestOutputConnectorViewModel> Source { get; init; } = null!;
    internal WeakReference<QuestInputConnectorViewModel> Target { get; init; } = null!;
    internal WeakReference<CHandle<graphGraphConnectionDefinition>> SourceHandle { get; init; } = null!;
    internal WeakReference<CHandle<graphGraphConnectionDefinition>> TargetHandle { get; init; } = null!;
    internal WeakReference<CWeakHandle<graphGraphSocketDefinition>> SourceEndpoint { get; init; } = null!;
    internal WeakReference<CWeakHandle<graphGraphSocketDefinition>> TargetEndpoint { get; init; } = null!;
    internal WeakReference<CArray<CHandle<graphGraphConnectionDefinition>>> SourceConnections { get; init; } = null!;
    internal WeakReference<CArray<CHandle<graphGraphConnectionDefinition>>> TargetConnections { get; init; } = null!;
    internal CName SourceName { get; init; }
    internal CName TargetName { get; init; }
    internal QuestConnectionBundleSocket? SourceSlot { get; init; }
    internal QuestConnectionBundleSocket? TargetSlot { get; init; }
}

internal sealed class QuestConnectionBundleNodeLayoutCapture
{
    internal WeakReference<BaseQuestViewModel> Node { get; init; } = null!;
    internal int InputCount { get; init; }
    internal IReadOnlyList<QuestConnectionBundlePortLayoutCapture> Ports { get; init; } = [];
    internal IReadOnlyList<QuestConnectionBundleBackingLayoutCapture> Sockets { get; init; } = [];
}

internal sealed class QuestConnectionBundlePortLayoutCapture
{
    internal WeakReference<BaseConnectorViewModel> Port { get; init; } = null!;
    internal CName RawName { get; init; }
    internal CEnum<Enums.questSocketType> Type { get; init; }
}

internal sealed class QuestConnectionBundleBackingLayoutCapture
{
    internal WeakReference<CHandle<graphGraphSocketDefinition>> Handle { get; init; } = null!;
    internal WeakReference<questSocketDefinition> Data { get; init; } = null!;
    internal CName RawName { get; init; }
    internal CEnum<Enums.questSocketType> Type { get; init; }
}

public sealed record QuestConnectionBundleSocketMapping(QuestConnectionBundleSocket Socket,
    IReadOnlyList<BaseConnectorViewModel> Choices, BaseConnectorViewModel? Suggested);
public sealed record QuestConnectionBundlePreviewEdge(QuestOutputConnectorViewModel Source,
    QuestInputConnectorViewModel Target, bool AlreadyExists);
public sealed record QuestConnectionBundlePreview(IReadOnlyList<QuestConnectionBundlePreviewEdge> Connections,
    bool IntroducesSelfConnections);
