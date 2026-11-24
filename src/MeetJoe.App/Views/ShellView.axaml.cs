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
            _vm.Calls.CallChromeChanged -= SyncCallStage;
        }

        _vm = vm;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnShellPropertyChanged;
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
    /// LiveKit must stay uncovered on macOS. Avalonia tiles keep the Meet grid; the WebView
    /// is a bottom-left PiP (visible when camera is on, near-invisible keep-alive otherwise).
    /// </summary>
    private void LayoutCallMediaHost()
    {
        if (CallMediaHost.Children.OfType<InAppMediaHost>().FirstOrDefault() is not { } host)
            return;

        host.IsVisible = true;
        host.ZIndex = 5;

        if (_vm is { ShowCallStage: true })
        {
            // Camera on: larger uncovered surface helps WKWebView capture; tiles stay visible around it.
            var cameraOn = _vm.Calls.IsCameraOn;
            host.Width = cameraOn ? 420 : 320;
            host.Height = cameraOn ? 260 : 180;
            host.HorizontalAlignment = HorizontalAlignment.Left;
            host.VerticalAlignment = VerticalAlignment.Bottom;
            host.Margin = new Thickness(12);
            // Keep the native surface painted (opacity 0 can stall WKWebView A/V).
            host.Opacity = cameraOn ? 1 : 0.04;
            host.IsHitTestVisible = false;
        }
        else
        {
            // Minimized stage root is already ~320×180 — fill it.
            host.ClearValue(WidthProperty);
            host.ClearValue(HeightProperty);
            host.ClearValue(MarginProperty);
            host.HorizontalAlignment = HorizontalAlignment.Stretch;
            host.VerticalAlignment = VerticalAlignment.Stretch;
            host.Opacity = 1;
            host.IsHitTestVisible = false;
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
