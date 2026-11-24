using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using MeetJoe.App.Controls;
using MeetJoe.App.ViewModels;

namespace MeetJoe.App.Views;

public partial class ShellView : UserControl
{
    private ShellViewModel? _vm;

    public ShellView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BindViewModel(DataContext as ShellViewModel);
        CallChatComposer.AddHandler(KeyDownEvent, OnCallChatKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnCallChatKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
        e.Handled = true;
        if (_vm?.Calls.SendChatCommand.CanExecute(null) == true)
            _vm.Calls.SendChatCommand.Execute(null);
    }

    private void BindViewModel(ShellViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnShellPropertyChanged;
            _vm.Calls.PropertyChanged -= OnCallsPropertyChanged;
            _vm.Calls.CallChromeChanged -= SyncCallStage;
        }

        _vm = vm;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnShellPropertyChanged;
            _vm.Calls.PropertyChanged += OnCallsPropertyChanged;
            _vm.Calls.CallChromeChanged += SyncCallStage;
        }

        SyncCallStage();
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.ShowCallStage)
            or nameof(ShellViewModel.ShowCallBar)
            or nameof(ShellViewModel.Current))
            SyncCallStage();
    }

    private void OnCallsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // WKWebView paints above Avalonia; hide it so the invite dialog is not clipped.
        if (e.PropertyName is nameof(CallsViewModel.ShowAddPeople)
            or nameof(CallsViewModel.IsLiveKitSurfaceReady))
            LayoutCallMediaHost();
    }

    private void SyncCallStage()
    {
        if (_vm is not { Calls.HasMedia: true, Calls.MediaPageUri: { } uri })
        {
            CallStageRoot.IsVisible = false;
            _ = TearDownCallMediaAsync();
            return;
        }

        CallStageRoot.IsVisible = true;

        if (_vm.ShowCallStage)
        {
            CallStageRoot.ClearValue(WidthProperty);
            CallStageRoot.ClearValue(HeightProperty);
            CallStageRoot.HorizontalAlignment = HorizontalAlignment.Stretch;
            CallStageRoot.VerticalAlignment = VerticalAlignment.Stretch;
            CallStageRoot.Opacity = 1;
            CallStageRoot.IsHitTestVisible = true;
            CallStageHeader.IsVisible = true;
            CallStageControls.IsVisible = true;
        }
        else
        {
            // Keep a real WebView surface alive while browsing. A 1×1 host kills WKWebView audio.
            CallStageRoot.Width = 320;
            CallStageRoot.Height = 180;
            CallStageRoot.HorizontalAlignment = HorizontalAlignment.Left;
            CallStageRoot.VerticalAlignment = VerticalAlignment.Bottom;
            CallStageRoot.Opacity = 0.01;
            CallStageRoot.IsHitTestVisible = false;
            CallStageHeader.IsVisible = false;
            CallStageControls.IsVisible = false;
        }

        OpenCallMedia(uri);
        LayoutCallMediaHost();
    }

    /// <summary>
    /// Expanded stage: WebView fills the grid area so LiveKit tiles (video / initials) paint
    /// in-box. Minimized: keep a real surface alive for capture/playback while browsing.
    /// Never stack Avalonia chrome over the WebView on macOS — hide the native surface while
    /// the Add people dialog is open so the modal is not occluded.
    /// </summary>
    private void LayoutCallMediaHost()
    {
        var inviting = _vm?.Calls.ShowAddPeople == true;

        if (CallMediaHost.Children.OfType<InAppMediaHost>().FirstOrDefault() is not { } host)
        {
            CallMediaHost.IsVisible = !inviting;
            return;
        }

        host.ZIndex = 5;
        host.IsHitTestVisible = false;
        host.Opacity = 1;
        host.ClearValue(WidthProperty);
        host.ClearValue(HeightProperty);
        host.ClearValue(MarginProperty);
        host.HorizontalAlignment = HorizontalAlignment.Stretch;
        host.VerticalAlignment = VerticalAlignment.Stretch;
        host.IsVisible = !inviting;

        CallMediaHost.IsVisible = !inviting;

        if (_vm is { ShowCallStage: true })
        {
            CallMediaHost.ClearValue(WidthProperty);
            CallMediaHost.ClearValue(HeightProperty);
            CallMediaHost.ClearValue(MarginProperty);
            CallMediaHost.HorizontalAlignment = HorizontalAlignment.Stretch;
            CallMediaHost.VerticalAlignment = VerticalAlignment.Stretch;
        }
        else
        {
            // Minimized floating keep-alive (~320×180 stage root already).
            CallMediaHost.HorizontalAlignment = HorizontalAlignment.Stretch;
            CallMediaHost.VerticalAlignment = VerticalAlignment.Stretch;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void OpenCallMedia(Uri uri)
    {
        if (CallMediaHost.Children.OfType<InAppMediaHost>().FirstOrDefault() is not { } host)
        {
            host = new InAppMediaHost();
            host.SetLeaveHandler(() =>
            {
                if (_vm?.Calls.HangUpCommand.CanExecute(null) == true)
                    _vm.Calls.HangUpCommand.Execute(null);
            });
            host.MediaStateChanged += () =>
            {
                if (_vm is null) return;
                _vm.Calls.IsCameraOn = host.IsCameraEnabled;
                _vm.Calls.IsMicOn = host.IsMicEnabled;
                LayoutCallMediaHost();
            };
            host.ActiveSpeakersChanged += ids =>
            {
                if (_vm is null) return;
                _vm.Calls.ApplyActiveSpeakers(ids);
            };
            host.MutedParticipantsChanged += ids =>
            {
                if (_vm is null) return;
                _vm.Calls.ApplyMutedParticipants(ids);
            };
            host.PermissionNeeded += kind =>
            {
                if (_vm is null) return;
                _ = _vm.Calls.NotifyMediaPermissionNeededAsync(kind);
            };
            host.MediaReady += () =>
            {
                if (_vm is null) return;
                LayoutCallMediaHost();
                _vm.Calls.MarkLiveKitSurfaceReady();
            };
            CallMediaHost.Children.Clear();
            CallMediaHost.Children.Add(host);
        }

        if (_vm is not null)
        {
            // Re-bind every open so hang-up clearing handlers cannot leave buttons dead.
            _vm.Calls.ToggleCameraHandler = () => host.ToggleCameraAsync();
            _vm.Calls.ToggleMicHandler = () => host.ToggleMicAsync();
        }

        host.Show(uri);
        LayoutCallMediaHost();
    }

    private async Task TearDownCallMediaAsync()
    {
        var hosts = CallMediaHost.Children.OfType<InAppMediaHost>().ToList();
        foreach (var host in hosts)
        {
            try { await host.ShutdownAsync(); }
            catch { /* ignore */ }
        }

        CallMediaHost.Children.Clear();
    }
}
