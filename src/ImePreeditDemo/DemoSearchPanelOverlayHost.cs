// Hosts the search panel in the Window's OverlayLayer and makes it draggable.
//   How    : the panel (DemoSearchPanelView) is added to the Window's OverlayLayer - a Canvas-derived layer
//            under VisualLayerManager that renders above the normal controls - and positioned absolutely
//            with Canvas.Left/Top.
//   Why it matters here: the OverlayLayer sits OUTSIDE the visual tree of the TextArea, so
//            TextInputMethodClientRequestedEvent raised by the search TextBox never bubbles through the
//            TextArea and is therefore never taken over by it. See the README.
//   Note   : OverlayLayer.GetOverlayLayer returns null while the visual is not attached, so it is looked up
//            lazily when the panel is first opened (Ctrl+F), not in the constructor.
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;            // VectorEventArgs (argument of Thumb.DragDelta)

namespace ImePreeditDemo;

/// <summary>
/// Places the search panel on the OverlayLayer of the Window that owns the editor, so it can be dragged
/// anywhere inside the window rather than being confined to the editor. Positioning, dragging, clamping
/// and open/close all live here.
/// </summary>
public sealed class DemoSearchPanelOverlayHost
{
    // Margins for the default position (top-right of the editor).
    private const double RightMargin = 12;
    private const double TopMargin = 6;

    private readonly Control _target;       // the editor (the forked TextEditor)
    private readonly DemoSearchPanelView _panel;
    private OverlayLayer? _layer;
    private Window? _window;
    private bool _isOpen;
    private bool _initialArranged;          // re-position once, after the real Bounds are known

    public DemoSearchPanelOverlayHost(Control target, DemoSearchPanelView panel)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));
    }

    /// <summary>Whether the panel is currently on the OverlayLayer.</summary>
    public bool IsOpen => _isOpen;

    /// <summary>The search panel this host manages.</summary>
    public DemoSearchPanelView Panel => _panel;

    /// <summary>Show the panel on the OverlayLayer at its default position. If already open, just focus the search box.</summary>
    public void Open()
    {
        if (_isOpen)
        {
            _panel.SearchBox.Focus();
            return;
        }

        _layer = OverlayLayer.GetOverlayLayer(_target);
        if (_layer is null) return;

        if (!_layer.Children.Contains(_panel))
            _layer.Children.Add(_panel);

        _isOpen = true;
        _initialArranged = false;

        _panel.Measure(_layer.Bounds.Size);
        PositionTopRight();

        _panel.DragThumb.DragDelta += OnDragDelta;

        _window = TopLevel.GetTopLevel(_target) as Window;
        if (_window is not null) _window.Resized += OnWindowResized;
        _layer.LayoutUpdated += OnLayerLayoutUpdated;
    }

    /// <summary>Remove the panel from the OverlayLayer and unhook everything. The position is not persisted.</summary>
    public void Close()
    {
        if (!_isOpen) return;
        _isOpen = false;

        _panel.DragThumb.DragDelta -= OnDragDelta;
        if (_window is not null) { _window.Resized -= OnWindowResized; _window = null; }
        if (_layer is not null)
        {
            _layer.LayoutUpdated -= OnLayerLayoutUpdated;
            _layer.Children.Remove(_panel);
            _layer = null;
        }
    }

    /// <summary>Translate the top-right corner of the editor into OverlayLayer coordinates and place the panel there.</summary>
    private void PositionTopRight()
    {
        if (_layer is null) return;
        var p = _target.TranslatePoint(new Point(_target.Bounds.Width, 0), _layer);
        if (p is null) return;
        double w = PanelWidth();
        double left = p.Value.X - w - RightMargin;
        double top = p.Value.Y + TopMargin;
        SetPositionClamped(left, top);
    }

    /// <summary>Move by the drag delta, clamped to the window.</summary>
    private void OnDragDelta(object? sender, VectorEventArgs e)
    {
        double left = Canvas.GetLeft(_panel);
        double top = Canvas.GetTop(_panel);
        if (double.IsNaN(left)) left = 0;
        if (double.IsNaN(top)) top = 0;
        SetPositionClamped(left + e.Vector.X, top + e.Vector.Y);
    }

    /// <summary>Re-clamp on resize so the panel cannot end up outside the window.</summary>
    private void OnWindowResized(object? sender, WindowResizedEventArgs e) => ReclampCurrent();

    /// <summary>Re-position once the first arrange pass has given the panel a real size (Bounds can be 0 right after Measure).</summary>
    private void OnLayerLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_initialArranged && _panel.Bounds.Width > 0)
        {
            _initialArranged = true;
            PositionTopRight();
        }
    }

    private void ReclampCurrent()
    {
        double left = Canvas.GetLeft(_panel);
        double top = Canvas.GetTop(_panel);
        if (double.IsNaN(left)) left = 0;
        if (double.IsNaN(top)) top = 0;
        SetPositionClamped(left, top);
    }

    private double PanelWidth() => _panel.Bounds.Width > 0 ? _panel.Bounds.Width : _panel.DesiredSize.Width;
    private double PanelHeight() => _panel.Bounds.Height > 0 ? _panel.Bounds.Height : _panel.DesiredSize.Height;

    /// <summary>Set left/top clamped to the bounds of the OverlayLayer (roughly the window client area).</summary>
    private void SetPositionClamped(double left, double top)
    {
        if (_layer is null) return;
        double maxLeft = Math.Max(0, _layer.Bounds.Width - PanelWidth());
        double maxTop = Math.Max(0, _layer.Bounds.Height - PanelHeight());
        Canvas.SetLeft(_panel, Math.Clamp(left, 0, maxLeft));
        Canvas.SetTop(_panel, Math.Clamp(top, 0, maxTop));
    }
}
