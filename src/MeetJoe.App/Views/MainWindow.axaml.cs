using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using MeetJoe.App.Controls;
using MeetJoe.App.ViewModels;

namespace MeetJoe.App.Views;

public partial class MainWindow : Window
{
    private bool _exitFlowRunning;
    private bool _exitCleanupDone;

    public MainWindow()
    {
        InitializeComponent();
        // Avalonia exposes Activated as an event (no OnActivated override).
        Activated += (_, _) =>
        {
            if (DataContext is MainViewModel main)
                _ = main.HandleWindowActivatedAsync();
        };
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_exitCleanupDone)
        {
            base.OnClosing(e);
            return;
        }

        // Always confirm + shut down LiveKit/WebView before the native window dies.
        // Destroying an active WKWebView (camera/mic) on macOS segfaults → crash popup.
        e.Cancel = true;
        if (_exitFlowRunning) return;
        _exitFlowRunning = true;
        _ = FinishExitAsync();
    }

    private async Task FinishExitAsync()
    {
        try
        {
            var main = DataContext as MainViewModel;
            var confirmed = main is null || await main.ConfirmExitAsync();
            if (!confirmed)
                return;

            foreach (var host in this.GetLogicalDescendants().OfType<InAppMediaHost>().ToList())
            {
                try { await host.ShutdownAsync(); }
                catch { /* best-effort */ }
            }

            if (main is not null)
                await main.PrepareForExitAsync();

            _exitCleanupDone = true;
            await Dispatcher.UIThread.InvokeAsync(Close);
        }
        catch
        {
            // If cleanup fails, still allow a forced close after confirmation was given.
            if (_exitCleanupDone) return;
            _exitCleanupDone = true;
            await Dispatcher.UIThread.InvokeAsync(Close);
        }
        finally
        {
            // User cancelled the dialog — allow another close attempt later.
            if (!_exitCleanupDone)
                _exitFlowRunning = false;
        }
    }
}
