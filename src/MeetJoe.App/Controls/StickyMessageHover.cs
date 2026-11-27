using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace MeetJoe.App.Controls;

/// <summary>
/// Keeps a <c>hovering</c> class on a message row while the pointer is over it (and briefly after leave).
/// Avoids emoji/reply icon flicker when Avalonia ToolTips/Flyouts steal :pointerover from the row.
/// </summary>
public static class StickyMessageHover
{
    public static readonly AttachedProperty<bool> EnableProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Enable", typeof(StickyMessageHover));

    private static readonly ConditionalWeakTable<Control, Tracker> Trackers = new();

    static StickyMessageHover()
    {
        EnableProperty.Changed.AddClassHandler<Control>((control, args) =>
        {
            if (args.GetNewValue<bool>())
                Trackers.GetValue(control, static c => new Tracker(c));
            else if (Trackers.TryGetValue(control, out var tracker))
            {
                tracker.Dispose();
                Trackers.Remove(control);
            }
        });
    }

    public static bool GetEnable(Control control) => control.GetValue(EnableProperty);
    public static void SetEnable(Control control, bool value) => control.SetValue(EnableProperty, value);

    private sealed class Tracker : IDisposable
    {
        private readonly Control _control;
        private CancellationTokenSource? _leaveCts;
        private bool _disposed;

        public Tracker(Control control)
        {
            _control = control;
            control.PointerEntered += OnEntered;
            control.PointerExited += OnExited;
            control.DetachedFromVisualTree += OnDetached;
        }

        private void OnEntered(object? sender, PointerEventArgs e)
        {
            _leaveCts?.Cancel();
            _leaveCts = null;
            _control.Classes.Add("hovering");
        }

        private void OnExited(object? sender, PointerEventArgs e)
        {
            // Delay hide so ToolTip / Flyout popups that briefly clear pointerover do not flicker icons.
            _leaveCts?.Cancel();
            var cts = new CancellationTokenSource();
            _leaveCts = cts;
            _ = HideAfterDelayAsync(cts.Token);
        }

        private async Task HideAfterDelayAsync(CancellationToken ct)
        {
            try
            {
                await Task.Delay(280, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_disposed || ct.IsCancellationRequested) return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed || ct.IsCancellationRequested) return;
                // Still over the row (e.g. tooltip dismissed) — keep actions visible.
                if (_control.IsPointerOver)
                {
                    _control.Classes.Add("hovering");
                    return;
                }

                _control.Classes.Remove("hovering");
            });
        }

        private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e) => Dispose();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _leaveCts?.Cancel();
            _control.PointerEntered -= OnEntered;
            _control.PointerExited -= OnExited;
            _control.DetachedFromVisualTree -= OnDetached;
            _control.Classes.Remove("hovering");
        }
    }
}
