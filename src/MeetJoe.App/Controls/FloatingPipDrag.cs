using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace MeetJoe.App.Controls;

/// <summary>
/// Makes a floating PiP surface draggable within its parent by dragging a handle
/// (typically the header). Uses Left/Top alignment + Margin so position sticks.
/// </summary>
public sealed class FloatingPipDrag
{
    private readonly Control _surface;
    private readonly Control _handle;
    private Point? _grabInSurface;
    private bool _enabled;
    private Thickness _savedMargin = new(16);
    private bool _hasSaved;

    public FloatingPipDrag(Control surface, Control handle)
    {
        _surface = surface;
        _handle = handle;
        _handle.PointerPressed += OnPointerPressed;
        _handle.PointerMoved += OnPointerMoved;
        _handle.PointerReleased += OnPointerReleased;
        _handle.PointerCaptureLost += OnPointerCaptureLost;
    }

    public bool IsEnabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!value) EndDrag();
            _handle.Cursor = value
                ? new Cursor(StandardCursorType.SizeAll)
                : new Cursor(StandardCursorType.Arrow);
        }
    }

    /// <summary>Place the PiP using Left/Top margins (required for free dragging).</summary>
    public void ApplyFloatingLayout(double width, double height, Thickness? preferredMargin = null)
    {
        _surface.Width = width;
        _surface.Height = height;
        _surface.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        _surface.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;

        if (preferredMargin is { } forced)
        {
            _surface.Margin = ClampMargin(forced, width, height);
            _savedMargin = _surface.Margin;
            _hasSaved = true;
            return;
        }

        if (_hasSaved)
        {
            _surface.Margin = ClampMargin(_savedMargin, width, height);
            return;
        }

        // Default: bottom-right inset.
        var parent = _surface.GetVisualParent() as Control;
        var pw = parent?.Bounds.Width ?? 0;
        var ph = parent?.Bounds.Height ?? 0;
        if (pw <= 0 || ph <= 0)
        {
            _surface.Margin = new Thickness(16);
            // Fall back until first measure; still mark so we don't jump later unexpectedly.
            _savedMargin = _surface.Margin;
            _hasSaved = true;
            return;
        }

        var left = Math.Max(8, pw - width - 16);
        var top = Math.Max(8, ph - height - 16);
        _surface.Margin = new Thickness(left, top, 0, 0);
        _savedMargin = _surface.Margin;
        _hasSaved = true;
    }

    public void RememberCurrentPosition()
    {
        _savedMargin = _surface.Margin;
        _hasSaved = true;
    }

    public void ResetSavedPosition()
    {
        _hasSaved = false;
        _savedMargin = new Thickness(16);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_enabled) return;
        // Don't start a drag when clicking a button inside the header.
        if (e.Source is Visual visual && visual.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;
        if (!e.GetCurrentPoint(_handle).Properties.IsLeftButtonPressed) return;

        _grabInSurface = e.GetPosition(_surface);
        e.Pointer.Capture(_handle);
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_enabled || _grabInSurface is not { } grab) return;
        if (e.Pointer.Captured != _handle) return;

        var parent = _surface.GetVisualParent() as Control;
        if (parent is null) return;

        var inParent = e.GetPosition(parent);
        var width = _surface.Bounds.Width > 0 ? _surface.Bounds.Width : _surface.Width;
        var height = _surface.Bounds.Height > 0 ? _surface.Bounds.Height : _surface.Height;
        if (double.IsNaN(width) || width <= 0) width = 300;
        if (double.IsNaN(height) || height <= 0) height = 248;

        var left = inParent.X - grab.X;
        var top = inParent.Y - grab.Y;
        _surface.Margin = ClampMargin(new Thickness(left, top, 0, 0), width, height);
        _savedMargin = _surface.Margin;
        _hasSaved = true;
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Pointer.Captured == _handle)
            e.Pointer.Capture(null);
        EndDrag();
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndDrag();

    private void EndDrag() => _grabInSurface = null;

    private Thickness ClampMargin(Thickness margin, double width, double height)
    {
        var parent = _surface.GetVisualParent() as Control;
        var pw = parent?.Bounds.Width ?? 0;
        var ph = parent?.Bounds.Height ?? 0;
        if (pw <= 0 || ph <= 0) return margin;

        const double edge = 8;
        var maxLeft = Math.Max(edge, pw - width - edge);
        var maxTop = Math.Max(edge, ph - height - edge);
        var left = Math.Clamp(margin.Left, edge, maxLeft);
        var top = Math.Clamp(margin.Top, edge, maxTop);
        return new Thickness(left, top, 0, 0);
    }
}
