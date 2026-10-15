#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Nodify;
using WolvenKit.App.Interaction;
using WolvenKit.App.ViewModels.GraphEditor;
using WolvenKit.App.ViewModels.GraphEditor.Nodes;
using WolvenKit.App.ViewModels.GraphEditor.Nodes.Quest;
using WolvenKit.App.ViewModels.GraphEditor.Nodes.Quest.Internal;
using WolvenKit.RED4.Types;

namespace WolvenKit.Views.GraphEditor;

public partial class GraphEditorView
{
    // Separate from the node/property clipboard; scoped to this view and this exact graph instance.
    private QuestConnectionBundle? _questConnectionBundle;
    private WeakReference<RedGraph>? _questConnectionBundleOwner;
    private QuestConnectionBundlePreview? _questConnectionBundlePreview;
    private DispatcherTimer? _questConnectionClipboardHintTimer;
    private bool _questConnectionClipboardCommitting;

    private void InitializeQuestConnectionClipboard()
    {
        SourceChanged += (_, _) => ClearQuestConnectionClipboard();
        Unloaded += (_, _) => ClearQuestConnectionClipboard();
        Editor.ViewportUpdated += (_, _) => DrawQuestConnectionClipboardPreview();
        AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(QuestConnectionClipboard_OnKeyDown), true);
    }

    /// <summary>Cancel a pending cut or discard a copy without changing any graph data.</summary>
    public void ClearQuestConnectionClipboard()
    {
        if (_questConnectionBundleOwner?.TryGetTarget(out var graph) == true)
        {
            graph.Nodes.CollectionChanged -= QuestConnectionClipboard_OnGraphChanged;
            graph.Connections.CollectionChanged -= QuestConnectionClipboard_OnGraphChanged;
        }
        if (_questConnectionBundle is { IsConsumed: false } bundle) bundle.Cancel();
        _questConnectionBundle = null;
        _questConnectionBundleOwner = null;
        ClearQuestConnectionClipboardPreview();
        _questConnectionClipboardHintTimer?.Stop();
        QuestConnectionClipboardHint.SetCurrentValue(VisibilityProperty, Visibility.Collapsed);
    }

    private void QuestConnectionClipboard_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.Escape || IsQuestConnectionRewiring || IsQuestMultiConnecting ||
            IsGraphTextInputFocused() || QuestRewireAncestor<TextBoxBase>(e.OriginalSource as DependencyObject) is not null ||
            _questConnectionBundle is not { Operation: QuestConnectionBundleOperation.Cut, IsConsumed: false }) return;
        e.Handled = true;
        ClearQuestConnectionClipboard();
    }

    private void QuestConnectionClipboard_OnGraphChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_questConnectionClipboardCommitting) return;
        ClearQuestConnectionClipboardPreview();
        if (_questConnectionBundle is { } bundle &&
            (Source is null || !Source.IsQuestConnectionBundleCurrent(bundle, out _))) ClearQuestConnectionClipboard();
    }

    private void AddQuestConnectionClipboardMenu(ContextMenu menu, BaseQuestViewModel anchor)
    {
        var graph = Source;
        if (graph?.GraphType != RedGraphType.Quest) return;
        if (_questConnectionBundle is { } previous && !graph.IsQuestConnectionBundleCurrent(previous, out _))
            ClearQuestConnectionClipboard();

        foreach (var operation in new[] { QuestConnectionBundleOperation.Copy, QuestConnectionBundleOperation.Cut })
        {
            var parent = CreateMenuItem(operation == QuestConnectionBundleOperation.Copy ? "Copy Connections" : "Cut Connections",
                operation == QuestConnectionBundleOperation.Copy ? "ContentCopy" : "ContentCut", () => { });
            foreach (var (direction, label) in new[]
            {
                (QuestConnectionBundleDirection.All, "All Connections"),
                (QuestConnectionBundleDirection.Incoming, "Incoming Connections"),
                (QuestConnectionBundleDirection.Outgoing, "Outgoing Connections")
            })
            {
                var item = CreateMenuItem(label, () => CaptureQuestConnectionClipboard(graph, anchor, direction, operation));
                // Menu availability needs no serialized clipboard; capture validates the complete bundle on click.
                var enabled = graph.Connections.OfType<QuestConnectionViewModel>().Any(edge =>
                    edge.Source is QuestOutputConnectorViewModel output && output.Data.Type == Enums.questSocketType.Output &&
                    edge.Target is QuestInputConnectorViewModel input && input.Data.Type == Enums.questSocketType.Input &&
                    ((direction != QuestConnectionBundleDirection.Incoming && anchor.Output.Contains(output)) ||
                     (direction != QuestConnectionBundleDirection.Outgoing && anchor.Input.Contains(input))));
                item.SetCurrentValue(IsEnabledProperty, enabled);
                item.SetCurrentValue(ToolTipService.ShowOnDisabledProperty, true);
                item.SetCurrentValue(ToolTipProperty, enabled ? "Standard Input/Output connections only"
                    : "This node has no standard connections in this direction.");
                parent.Items.Add(item);
            }
            menu.Items.Add(parent);
        }

        var paste = CreateMenuItem("Paste Connections", "ContentPaste", () => PasteQuestConnectionClipboard(graph, anchor));
        var pasteReason = "Copy or cut connections from a node in this graph first.";
        var canPaste = _questConnectionBundle is { } bundle &&
            graph.TryGetQuestConnectionBundleSocketMappings(bundle, anchor, out _, out pasteReason);
        paste.SetCurrentValue(IsEnabledProperty, canPaste);
        paste.SetCurrentValue(ToolTipService.ShowOnDisabledProperty, true);
        paste.SetCurrentValue(ToolTipProperty, canPaste ? "Hover to preview; click to paste connections" : pasteReason);
        paste.MouseEnter += (_, _) => PreviewQuestConnectionClipboard(graph, anchor);
        paste.MouseLeave += (_, _) => ClearQuestConnectionClipboardPreview();
        menu.Closed -= QuestConnectionClipboard_OnMenuClosed;
        menu.Closed += QuestConnectionClipboard_OnMenuClosed;
        menu.Items.Add(paste);
        if (_questConnectionBundle is { Operation: QuestConnectionBundleOperation.Cut, IsConsumed: false })
            menu.Items.Add(CreateMenuItem("Cancel Pending Connection Cut", "Close", ClearQuestConnectionClipboard));
        menu.Items.Add(new Separator());
    }

    private void CaptureQuestConnectionClipboard(RedGraph graph, BaseQuestViewModel anchor,
        QuestConnectionBundleDirection direction, QuestConnectionBundleOperation operation)
    {
        if (!ReferenceEquals(Source, graph)) return;
        if (!graph.TryCaptureQuestConnectionBundle(anchor, direction, operation, out var bundle, out var reason) || bundle is null)
        {
            ShowQuestConnectionClipboardFeedback(reason);
            return;
        }
        ClearQuestConnectionClipboard();
        CancelQuestConnectionRewire();
        CancelQuestMultiConnect();
        _questConnectionBundle = bundle;
        _questConnectionBundleOwner = new WeakReference<RedGraph>(graph);
        graph.Nodes.CollectionChanged += QuestConnectionClipboard_OnGraphChanged;
        graph.Connections.CollectionChanged += QuestConnectionClipboard_OnGraphChanged;
        ShowQuestConnectionClipboardFeedback(operation == QuestConnectionBundleOperation.Cut
            ? $"Pending cut: {bundle.ConnectionCount} connections. Paste onto a node; Esc cancels."
            : $"Copied {bundle.ConnectionCount} connections.");
    }

    private bool IsQuestConnectionClipboardLive(RedGraph graph, QuestConnectionBundle bundle) =>
        ReferenceEquals(Source, graph) && ReferenceEquals(_questConnectionBundle, bundle) &&
        graph.IsQuestConnectionBundleCurrent(bundle, out _);

    private void PasteQuestConnectionClipboard(RedGraph graph, BaseQuestViewModel destination)
    {
        var bundle = _questConnectionBundle;
        if (bundle is null || !IsQuestConnectionClipboardLive(graph, bundle)) return;
        ClearQuestConnectionClipboardPreview();
        if (!graph.TryGetQuestConnectionBundleSocketMappings(bundle, destination, out var slots, out var reason))
        {
            ShowQuestConnectionClipboardFeedback(reason);
            return;
        }
        var mapping = new Dictionary<QuestConnectionBundleSocket, BaseConnectorViewModel>();
        var used = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var (slot, index) in slots.Select((slot, index) => (slot, index)))
        {
            var choices = slot.Choices.Where(port => !used.Contains(QuestConnectionClipboardSocketData(port))).ToArray();
            var selected = slot.Suggested;
            if (selected is null || !choices.Contains(selected))
            {
                selected = ChooseQuestConnectionClipboardSocket(choices, slot, index);
                if (!IsQuestConnectionClipboardLive(graph, bundle)) return;
                if (selected is null)
                {
                    ShowQuestConnectionClipboardFeedback("Paste cancelled; connections are unchanged.");
                    return;
                }
            }
            mapping.Add(slot.Socket, selected);
            used.Add(QuestConnectionClipboardSocketData(selected));
        }
        if (!IsQuestConnectionClipboardLive(graph, bundle)) return;
        if (!graph.TryPreviewQuestConnectionBundle(bundle, destination, mapping, out var preview, out reason) || preview is null)
        {
            ShowQuestConnectionClipboardFeedback(reason);
            return;
        }
        var allowSelfConnections = false;
        if (preview.IntroducesSelfConnections)
        {
            allowSelfConnections = Interactions.ShowMessageBox(
                "This paste connects the destination node to itself. Continue?", "Paste connections",
                WMessageBoxButtons.YesNo, WMessageBoxImage.Warning) == WMessageBoxResult.Yes;
            if (!IsQuestConnectionClipboardLive(graph, bundle)) return;
            if (!allowSelfConnections)
            {
                ShowQuestConnectionClipboardFeedback("Paste cancelled; connections are unchanged.");
                return;
            }
        }
        try
        {
            _questConnectionClipboardCommitting = true;
            if (!graph.TryPasteQuestConnectionBundle(bundle, destination, mapping, out reason, allowSelfConnections))
            {
                if (IsQuestConnectionClipboardLive(graph, bundle)) ShowQuestConnectionClipboardFeedback(reason);
                return;
            }
            if (!ReferenceEquals(Source, graph)) return;
            var count = preview.Connections.Count(connection => !connection.AlreadyExists);
            if (bundle.Operation == QuestConnectionBundleOperation.Cut)
            {
                ClearQuestConnectionClipboard();
                ShowQuestConnectionClipboardFeedback($"Moved {bundle.ConnectionCount} connections.");
            }
            else ShowQuestConnectionClipboardFeedback(count == 0 ? "Connections already exist; graph unchanged."
                : $"Pasted {count} connections.");
        }
        catch (Exception exception)
        {
            _loggerService?.Error(exception);
            if (ReferenceEquals(Source, graph)) ShowQuestConnectionClipboardFeedback("Could not paste connections.");
        }
        finally
        {
            _questConnectionClipboardCommitting = false;
            ClearQuestConnectionClipboardPreview();
        }
    }

    private static BaseConnectorViewModel? ChooseQuestConnectionClipboardSocket(
        IReadOnlyList<BaseConnectorViewModel> sockets, QuestConnectionBundleSocketMapping slot, int sourceIndex)
    {
        if (sockets.Count == 0) return null;
        if (sockets.Count == 1) return sockets[0];
        // Reuse the editor's dropdown interaction and indexed socket labels for ambiguous mappings.
        var ordinals = slot.Choices.Select((socket, index) => (socket, index))
            .ToDictionary(choice => choice.socket, choice => choice.index + 1);
        var choices = sockets.Select(socket => (Label: $"{socket.Name} [{ordinals[socket]}]", Socket: socket))
            .ToDictionary(choice => choice.Label, choice => choice.Socket);
        var selected = Interactions.AskForDropdownOption((choices.Keys.ToList(),
            $"Choose {(slot.Socket.IsOutput ? "output" : "input")} socket",
            $"Map source socket '{slot.Socket.Name}' [{sourceIndex + 1}] to a socket on the destination node.", null, false, null));
        return !string.IsNullOrEmpty(selected) && choices.TryGetValue(selected, out var socket) ? socket : null;
    }

    private static object QuestConnectionClipboardSocketData(BaseConnectorViewModel socket) => socket switch
    {
        QuestInputConnectorViewModel input => input.Data,
        QuestOutputConnectorViewModel output => output.Data,
        _ => socket
    };

    private void ShowQuestConnectionClipboardFeedback(string text)
    {
        _questConnectionClipboardHintTimer?.Stop();
        if (_questConnectionBundle is { Operation: QuestConnectionBundleOperation.Cut, IsConsumed: false } pending &&
            !text.StartsWith("Pending cut:", StringComparison.Ordinal))
            text += $" Pending cut: {pending.ConnectionCount} connections; Esc cancels.";
        QuestConnectionClipboardHintText.SetCurrentValue(TextBlock.TextProperty, text);
        QuestConnectionClipboardHint.SetCurrentValue(VisibilityProperty, Visibility.Visible);
        if (_questConnectionBundle is { Operation: QuestConnectionBundleOperation.Cut, IsConsumed: false }) return;
        if (_questConnectionClipboardHintTimer is null)
        {
            _questConnectionClipboardHintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _questConnectionClipboardHintTimer.Tick += (_, _) =>
            {
                _questConnectionClipboardHintTimer.Stop();
                QuestConnectionClipboardHint.SetCurrentValue(VisibilityProperty, Visibility.Collapsed);
            };
        }
        _questConnectionClipboardHintTimer.Start();
    }

    private void PreviewQuestConnectionClipboard(RedGraph graph, BaseQuestViewModel destination)
    {
        ClearQuestConnectionClipboardPreview();
        if (_questConnectionBundle is not { } bundle || !IsQuestConnectionClipboardLive(graph, bundle) ||
            !graph.TryPreviewQuestConnectionBundle(bundle, destination, null, out var preview, out _) || preview is null) return;
        _questConnectionBundlePreview = preview;
        // Disconnected Nodify ports do not continuously update anchors. Settle native layout first.
        Editor.UpdateLayout();
        foreach (var control in QuestRewireDescendants<Connector>(Editor))
            if (control.IsVisible) control.UpdateAnchor();
        DrawQuestConnectionClipboardPreview();
    }

    private void DrawQuestConnectionClipboardPreview()
    {
        QuestConnectionClipboardPreview.Children.Clear();
        if (_questConnectionBundlePreview is not { } preview) return;
        var visible = QuestRewireDescendants<Connector>(Editor).Where(control => control.IsVisible)
            .Select(control => control.DataContext).ToHashSet(ReferenceEqualityComparer.Instance);
        foreach (var edge in preview.Connections)
        {
            // Hidden/unrealized sockets can still be pasted safely; omit only their visual preview.
            if (!visible.Contains(edge.Source) || !visible.Contains(edge.Target)) continue;
            var cable = new Connection();
            cable.SetCurrentValue(BaseConnection.IsSelectableProperty, false);
            cable.SetCurrentValue(BaseConnection.SourceProperty, Editor.ViewportTransform.Transform(edge.Source.Anchor));
            cable.SetCurrentValue(BaseConnection.TargetProperty, Editor.ViewportTransform.Transform(edge.Target.Anchor));
            cable.SetCurrentValue(BaseConnection.SourceOffsetProperty, new Size(0, 0));
            cable.SetCurrentValue(BaseConnection.TargetOffsetProperty, new Size(0, 0));
            cable.SetCurrentValue(System.Windows.Shapes.Shape.StrokeProperty, edge.AlreadyExists ? Brushes.LightSkyBlue : Brushes.Gold);
            cable.SetCurrentValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 2.5d);
            cable.SetCurrentValue(System.Windows.Shapes.Shape.StrokeDashArrayProperty, new DoubleCollection { 3, 2 });
            QuestConnectionClipboardPreview.Children.Add(cable);
        }
    }

    private void QuestConnectionClipboard_OnMenuClosed(object sender, RoutedEventArgs e) => ClearQuestConnectionClipboardPreview();

    private void ClearQuestConnectionClipboardPreview()
    {
        _questConnectionBundlePreview = null;
        QuestConnectionClipboardPreview.Children.Clear();
    }
}
