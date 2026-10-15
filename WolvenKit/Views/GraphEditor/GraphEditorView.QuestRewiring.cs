#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Nodify;
using WolvenKit.App.ViewModels.GraphEditor;
using WolvenKit.App.ViewModels.GraphEditor.Nodes;
using WolvenKit.App.ViewModels.GraphEditor.Nodes.Quest.Internal;

namespace WolvenKit.Views.GraphEditor;

public enum QuestRewireSocketFeedback { None, Compatible, Incompatible, Fixed, Moving }

public partial class GraphEditorView
{
    public static readonly DependencyProperty QuestRewireSocketStateProperty = DependencyProperty.RegisterAttached(
        "QuestRewireSocketState", typeof(QuestRewireSocketFeedback), typeof(GraphEditorView), new PropertyMetadata(QuestRewireSocketFeedback.None));
    public static QuestRewireSocketFeedback GetQuestRewireSocketState(DependencyObject element) =>
        (QuestRewireSocketFeedback)element.GetValue(QuestRewireSocketStateProperty);
    public static void SetQuestRewireSocketState(DependencyObject element, QuestRewireSocketFeedback value) =>
        element.SetValue(QuestRewireSocketStateProperty, value);

    public static readonly DependencyProperty QuestRewireActiveProperty = DependencyProperty.RegisterAttached(
        "QuestRewireActive", typeof(bool), typeof(GraphEditorView), new PropertyMetadata(false));
    public static bool GetQuestRewireActive(DependencyObject element) => (bool)element.GetValue(QuestRewireActiveProperty);
    public static void SetQuestRewireActive(DependencyObject element, bool value) => element.SetValue(QuestRewireActiveProperty, value);

    private RedGraph? _questRewireGraph;
    private QuestConnectionViewModel? _questRewireConnection;
    private QuestConnectionViewModel? _questRewireHoveredConnection;
    private Connection? _questRewireActiveVisual;
    private Connector? _questRewireFixedConnector;
    private QuestConnectionEndpoint _questRewireEndpoint;
    private Point _questRewirePointer;
    private bool _questRewireClickToDrop;
    private bool _questRewireCommitting;
    private readonly HashSet<Connector> _questRewireFeedback = new();

    public bool IsQuestConnectionRewiring => _questRewireConnection is not null;

    private void InitializeQuestConnectionRewiring()
    {
        AddHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler(QuestRewire_OnPreviewMouseMove), true);
        AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(QuestRewire_OnPreviewMouseDown), true);
        AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(QuestRewire_OnPreviewMouseUp), true);
        AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(QuestRewire_OnPreviewKeyDown), true);
        Editor.LostMouseCapture += QuestRewire_OnLostMouseCapture;
        Editor.ViewportUpdated += (_, _) => UpdateQuestRewirePreview();
        Unloaded += (_, _) => CancelQuestConnectionRewire();
    }

    /// <summary>Context menu alternative: preview the chosen endpoint, then click a compatible socket.</summary>
    public bool BeginQuestConnectionRewire(QuestConnectionViewModel connection, QuestConnectionEndpoint endpoint) =>
        BeginQuestConnectionRewire(connection, endpoint, clickToDrop: true);

    private bool BeginQuestConnectionRewire(QuestConnectionViewModel connection, QuestConnectionEndpoint endpoint, bool clickToDrop)
    {
        CancelQuestMultiConnect();
        CancelQuestConnectionRewire();
        var graph = Source;
        if (graph is null || !graph.CanStartQuestConnectionRewire(connection, out _) ||
            endpoint is not (QuestConnectionEndpoint.Source or QuestConnectionEndpoint.Destination) || !Editor.IsVisible)
            return false;

        CloseActionPalette();
        _questRewireGraph = graph;
        _questRewireConnection = connection;
        _questRewireHoveredConnection = connection;
        _questRewireEndpoint = endpoint;
        _questRewireClickToDrop = clickToDrop;
        _questRewirePointer = Mouse.GetPosition(Editor);
        graph.Nodes.CollectionChanged += QuestRewire_OnGraphChanged;
        graph.Connections.CollectionChanged += QuestRewire_OnGraphChanged;
        _questRewireActiveVisual = QuestRewireDescendants<Connection>(Editor)
            .FirstOrDefault(visual => ReferenceEquals(visual.DataContext, connection));
        if (_questRewireActiveVisual is { } visual) SetQuestRewireActive(visual, true);
        BaseConnectorViewModel fixedPort = endpoint == QuestConnectionEndpoint.Source ? connection.Target : connection.Source;
        _questRewireFixedConnector = QuestRewireDescendants<Connector>(Editor)
            .FirstOrDefault(control => ReferenceEquals(control.DataContext, fixedPort));
        RefreshQuestRewireSocketFeedback();
        Editor.Focus();
        // Capturing the editor subtree preserves native wheel and middle-button event routes.
        if (!Mouse.Capture(Editor, CaptureMode.SubTree))
        {
            CancelQuestConnectionRewire();
            return false;
        }
        QuestRewirePreview.SetCurrentValue(VisibilityProperty, Visibility.Visible);
        QuestRewireHint.SetCurrentValue(VisibilityProperty, Visibility.Visible);
        QuestRewireHintText.SetCurrentValue(TextBlock.TextProperty, endpoint == QuestConnectionEndpoint.Source
            ? "Choose an output socket · Esc cancels" : "Choose an input socket · Esc cancels");
        UpdateQuestRewirePreview();
        return true;
    }

    public void CancelQuestConnectionRewire()
    {
        var graph = _questRewireGraph;
        var wasRewiring = IsQuestConnectionRewiring;
        _questRewireConnection = null;
        _questRewireGraph = null;
        _questRewireHoveredConnection = null;
        if (graph is not null)
        {
            graph.Nodes.CollectionChanged -= QuestRewire_OnGraphChanged;
            graph.Connections.CollectionChanged -= QuestRewire_OnGraphChanged;
        }
        if (_questRewireActiveVisual is { } visual) visual.ClearValue(QuestRewireActiveProperty);
        _questRewireActiveVisual = null;
        _questRewireFixedConnector = null;
        foreach (var connector in _questRewireFeedback) connector.ClearValue(QuestRewireSocketStateProperty);
        _questRewireFeedback.Clear();
        QuestRewirePreview.SetCurrentValue(VisibilityProperty, Visibility.Collapsed);
        QuestRewireHint.SetCurrentValue(VisibilityProperty, Visibility.Collapsed);
        QuestRewireSourceHandle.SetCurrentValue(VisibilityProperty, Visibility.Collapsed);
        QuestRewireDestinationHandle.SetCurrentValue(VisibilityProperty, Visibility.Collapsed);
        if (wasRewiring && ReferenceEquals(Mouse.Captured, Editor)) Mouse.Capture(null);
    }

    private void QuestRewireHandle_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || sender is not FrameworkElement { Tag: QuestConnectionEndpoint endpoint } ||
            _questRewireHoveredConnection is not { } connection) return;
        e.Handled = true;
        BeginQuestConnectionRewire(connection, endpoint, clickToDrop: false);
    }

    private void QuestRewire_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (IsQuestMultiConnecting) return;
        if (IsQuestConnectionRewiring)
        {
            if (!EnsureQuestRewireIsLive()) return;
            _questRewirePointer = e.GetPosition(Editor);
            var target = FindQuestRewireTarget(e)?.DataContext as BaseConnectorViewModel;
            var valid = _questRewireGraph!.CanRewireQuestConnection(_questRewireConnection!, _questRewireEndpoint, target, out var reason);
            QuestRewirePreview.SetCurrentValue(System.Windows.Shapes.Shape.StrokeProperty, target is null ? Brushes.Gold : valid ? Brushes.LightGreen : Brushes.IndianRed);
            QuestRewireHintText.SetCurrentValue(TextBlock.TextProperty, target is null
                ? (_questRewireEndpoint == QuestConnectionEndpoint.Source ? "Choose an output socket · Esc cancels" : "Choose an input socket · Esc cancels")
                : valid ? (_questRewireClickToDrop ? "Click to rewire · Esc cancels" : "Release to rewire · Esc cancels") : reason);
            UpdateQuestRewirePreview();
            if (e.MiddleButton != MouseButtonState.Pressed) e.Handled = true;
            return;
        }
        if (QuestRewireAncestor<Border>(e.OriginalSource as DependencyObject) is { } handle &&
            (ReferenceEquals(handle, QuestRewireSourceHandle) || ReferenceEquals(handle, QuestRewireDestinationHandle))) return;
        var hovered = QuestRewireAncestor<Connection>(e.OriginalSource as DependencyObject)?.DataContext as QuestConnectionViewModel;
        if (!ReferenceEquals(hovered, _questRewireHoveredConnection))
        {
            _questRewireHoveredConnection = Source?.CanStartQuestConnectionRewire(hovered, out _) == true ? hovered : null;
            UpdateQuestRewirePreview();
        }
    }

    private void QuestRewire_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsQuestConnectionRewiring) return;
        if (e.ChangedButton == MouseButton.Right)
        {
            e.Handled = true;
            CancelQuestConnectionRewire();
        }
        else if (e.ChangedButton == MouseButton.Left)
        {
            e.Handled = true;
            if (_questRewireClickToDrop) CompleteQuestConnectionRewire(FindQuestRewireTarget(e)?.DataContext as BaseConnectorViewModel);
        }
    }

    private void QuestRewire_OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!IsQuestConnectionRewiring || e.ChangedButton != MouseButton.Left) return;
        e.Handled = true;
        if (!_questRewireClickToDrop) CompleteQuestConnectionRewire(FindQuestRewireTarget(e)?.DataContext as BaseConnectorViewModel);
    }

    private void QuestRewire_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (IsQuestConnectionRewiring && e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelQuestConnectionRewire();
        }
    }

    private void CompleteQuestConnectionRewire(BaseConnectorViewModel? target)
    {
        try
        {
            if (!EnsureQuestRewireIsLive()) return;
            _questRewireCommitting = true;
            _questRewireGraph!.TryRewireQuestConnection(_questRewireConnection!, _questRewireEndpoint, target, out _);
        }
        catch (Exception exception) { _loggerService?.Error(exception); }
        finally
        {
            _questRewireCommitting = false;
            CancelQuestConnectionRewire();
        }
    }

    private bool EnsureQuestRewireIsLive()
    {
        if (ReferenceEquals(Source, _questRewireGraph) &&
            _questRewireGraph?.CanStartQuestConnectionRewire(_questRewireConnection, out _) == true) return true;
        CancelQuestConnectionRewire();
        return false;
    }

    private void QuestRewire_OnGraphChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_questRewireCommitting) CancelQuestConnectionRewire();
    }

    private void QuestRewire_OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (IsQuestConnectionRewiring && !Editor.IsMouseCaptureWithin) CancelQuestConnectionRewire();
    }

    private Connector? FindQuestRewireTarget(MouseEventArgs e)
    {
        // OriginalSource is useful for normal routed input as well as synthetic WPF event tests.
        var direct = QuestRewireAncestor<Connector>(e.OriginalSource as DependencyObject);
        if (direct is not null && ReferenceEquals(direct.Editor, Editor)) return direct;
        // Cache the fixed control for the session: mouse movement must not traverse every node's visuals.
        // Nodify searches its nodes layer, bypassing cables rendered above the sockets.
        return _questRewireFixedConnector?.FindTargetConnector(Editor.GetLocationInsideEditor(e));
    }

    private void RefreshQuestRewireSocketFeedback()
    {
        if (_questRewireConnection is not { } connection || _questRewireGraph is not { } graph) return;
        foreach (var connector in QuestRewireDescendants<Connector>(Editor))
        {
            if (connector.DataContext is not BaseConnectorViewModel port) continue;
            var state = graph.CanRewireQuestConnection(connection, _questRewireEndpoint, port, out _)
                ? QuestRewireSocketFeedback.Compatible : QuestRewireSocketFeedback.Incompatible;
            if (ReferenceEquals(port, connection.Source)) state = _questRewireEndpoint == QuestConnectionEndpoint.Source ? QuestRewireSocketFeedback.Moving : QuestRewireSocketFeedback.Fixed;
            if (ReferenceEquals(port, connection.Target)) state = _questRewireEndpoint == QuestConnectionEndpoint.Destination ? QuestRewireSocketFeedback.Moving : QuestRewireSocketFeedback.Fixed;
            SetQuestRewireSocketState(connector, state);
            _questRewireFeedback.Add(connector);
        }
    }

    private void UpdateQuestRewirePreview()
    {
        var connection = _questRewireConnection ?? _questRewireHoveredConnection;
        if (connection is null)
        {
            QuestRewireSourceHandle.SetCurrentValue(VisibilityProperty, Visibility.Collapsed);
            QuestRewireDestinationHandle.SetCurrentValue(VisibilityProperty, Visibility.Collapsed);
            return;
        }
        var source = Editor.ViewportTransform.Transform(connection.Source.Anchor);
        var target = Editor.ViewportTransform.Transform(connection.Target.Anchor);
        var active = IsQuestConnectionRewiring;
        var movingSource = active && _questRewireEndpoint == QuestConnectionEndpoint.Source;
        var movingTarget = active && _questRewireEndpoint == QuestConnectionEndpoint.Destination;
        // Native sockets are 14 graph pixels wide; keep the fixed-size grab areas outside them at every zoom.
        var socketRadius = 7 * Editor.ViewportZoom;
        PlaceQuestRewireHandle(QuestRewireSourceHandle, movingSource ? _questRewirePointer
            : new Point(source.X + socketRadius + QuestRewireSourceHandle.Width / 2 + 2, source.Y));
        PlaceQuestRewireHandle(QuestRewireDestinationHandle, movingTarget ? _questRewirePointer
            : new Point(target.X - socketRadius - QuestRewireDestinationHandle.Width / 2 - 2, target.Y));
        QuestRewireSourceHandle.SetCurrentValue(IsHitTestVisibleProperty, !active);
        QuestRewireDestinationHandle.SetCurrentValue(IsHitTestVisibleProperty, !active);
        QuestRewireSourceDot.SetCurrentValue(System.Windows.Shapes.Shape.FillProperty, movingSource ? Brushes.Gold : active ? Brushes.LightGreen : Brushes.DimGray);
        QuestRewireDestinationDot.SetCurrentValue(System.Windows.Shapes.Shape.FillProperty, movingTarget ? Brushes.Gold : active ? Brushes.LightGreen : Brushes.DimGray);
        if (!active) return;
        QuestRewirePreview.SetCurrentValue(BaseConnection.SourceProperty, movingSource ? _questRewirePointer : source);
        QuestRewirePreview.SetCurrentValue(BaseConnection.TargetProperty, movingTarget ? _questRewirePointer : target);
        Canvas.SetLeft(QuestRewireHint, _questRewirePointer.X + 18);
        Canvas.SetTop(QuestRewireHint, _questRewirePointer.Y + 18);
    }

    private static void PlaceQuestRewireHandle(FrameworkElement handle, Point center)
    {
        Canvas.SetLeft(handle, center.X - handle.Width / 2);
        Canvas.SetTop(handle, center.Y - handle.Height / 2);
        handle.SetCurrentValue(VisibilityProperty, Visibility.Visible);
    }

    private static T? QuestRewireAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T match) return match;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return null;
    }

    private static IEnumerable<T> QuestRewireDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in QuestRewireDescendants<T>(child)) yield return nested;
        }
    }
}
