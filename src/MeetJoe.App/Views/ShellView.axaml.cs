using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using MeetJoe.App.Controls;
using MeetJoe.App.ViewModels;

namespace MeetJoe.App.Views;

public partial class ShellView : UserControl
{
    private ShellViewModel? _vm;
    private bool _callPipActive;

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
            _vm.Calls.ChatFollowRequested -= OnCallChatFollowRequested;
            _vm.Calendar.PropertyChanged -= OnCalendarPropertyChanged;
            MeetingMediaParking.Changed -= OnMeetingMediaParkingChanged;
        }

        _vm = vm;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnShellPropertyChanged;
            _vm.Calls.PropertyChanged += OnCallsPropertyChanged;
            _vm.Calls.CallChromeChanged += SyncCallStage;
            _vm.Calls.ChatFollowRequested += OnCallChatFollowRequested;
            _vm.Calendar.PropertyChanged += OnCalendarPropertyChanged;
            MeetingMediaParking.Changed += OnMeetingMediaParkingChanged;
        }

        SyncCallStage();
        SyncMeetingPip();
    }

    private void OnCalendarPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CalendarViewModel.IsInMeeting)
            or nameof(CalendarViewModel.Stage))
            SyncMeetingPip();
    }

    private void OnMeetingMediaParkingChanged() =>
        Dispatcher.UIThread.Post(SyncMeetingPip, DispatcherPriority.Loaded);

    private void OnCallChatFollowRequested() =>
        Dispatcher.UIThread.Post(() =>
        {
            if (CallChatScroller.IsEffectivelyVisible)
                CallChatScroller.ScrollToEnd();
        }, DispatcherPriority.Loaded);

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.ShowCallStage)
            or nameof(ShellViewModel.ShowCallBar)
            or nameof(ShellViewModel.Current)
            or nameof(ShellViewModel.ShowMeetingPip))
        {
            SyncCallStage();
            SyncMeetingPip();
        }
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
            CallStageRoot.ClearValue(MarginProperty);
            CallStageRoot.HorizontalAlignment = HorizontalAlignment.Stretch;
            CallStageRoot.VerticalAlignment = VerticalAlignment.Stretch;
            CallStageRoot.Opacity = 1;
            CallStageRoot.IsHitTestVisible = true;
            CallStageHeader.IsVisible = true;
            CallStageControls.IsVisible = true;
            CallStageRail.IsVisible = true;
            CallHeaderPeopleButton.IsVisible = true;
            CallHeaderChatButton.IsVisible = true;
            CallHeaderAddPeopleButton.IsVisible = true;
            CallHeaderMinimizeButton.IsVisible = true;
            CallHeaderExpandButton.IsVisible = false;
            if (CallStageRoot.ColumnDefinitions.Count > 1)
                CallStageRoot.ColumnDefinitions[1].Width = new GridLength(280);
        }
        else
        {
            // Teams-style floating PiP: visible single-speaker surface while browsing.
            CallStageRoot.Width = 300;
            CallStageRoot.Height = 248;
            CallStageRoot.Margin = new Thickness(16);
            CallStageRoot.HorizontalAlignment = HorizontalAlignment.Right;
            CallStageRoot.VerticalAlignment = VerticalAlignment.Bottom;
            CallStageRoot.Opacity = 1;
            CallStageRoot.IsHitTestVisible = true;
            CallStageHeader.IsVisible = true;
            CallStageControls.IsVisible = true;
            CallStageRail.IsVisible = false;
            CallHeaderPeopleButton.IsVisible = false;
            CallHeaderChatButton.IsVisible = false;
            CallHeaderAddPeopleButton.IsVisible = false;
            CallHeaderMinimizeButton.IsVisible = false;
            CallHeaderExpandButton.IsVisible = true;
            if (CallStageRoot.ColumnDefinitions.Count > 1)
                CallStageRoot.ColumnDefinitions[1].Width = new GridLength(0);
            CallStageHeader.Padding = new Thickness(10, 8);
            CallStageControls.Padding = new Thickness(10, 8);
        }

        if (_vm.ShowCallStage)
        {
            CallStageHeader.Padding = new Thickness(16, 10);
            CallStageControls.Padding = new Thickness(16, 12);
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

        CallMediaHost.ClearValue(WidthProperty);
        CallMediaHost.ClearValue(HeightProperty);
        CallMediaHost.ClearValue(MarginProperty);
        CallMediaHost.HorizontalAlignment = HorizontalAlignment.Stretch;
        CallMediaHost.VerticalAlignment = VerticalAlignment.Stretch;

        var pip = _vm is { ShowCallStage: false };
        var leavingPip = _callPipActive && !pip;
        _callPipActive = pip;
        _ = ApplyCallMediaModeAsync(host, pip, syncPreferred: leavingPip);
    }

    private static async Task ApplyCallMediaModeAsync(InAppMediaHost host, bool pip, bool syncPreferred)
    {
        try
        {
            await host.SetPipModeAsync(pip);
            // Only when expanding from PiP — never on every layout/media-state tick.
            if (syncPreferred) await host.SyncPreferredMediaToPageAsync();
        }
        catch
        {
            // Best-effort — LiveKit page may still be connecting.
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
                // Do not call LayoutCallMediaHost here — it used to re-apply preferred media and
                // race user cam/mic clicks (often undoing the toggle).
                _vm.Calls.IsCameraOn = host.IsCameraEnabled;
                _vm.Calls.IsMicOn = host.IsMicEnabled;
                _vm.Calls.IsScreenShareOn = host.IsScreenShareEnabled;
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
            // Keep last cam/mic choice across PiP ↔ expanded (and any LiveKit reconnect).
            host.ApplyPreferredMedia(_vm.Calls.IsCameraOn, _vm.Calls.IsMicOn);
            // Re-bind every open so hang-up clearing handlers cannot leave buttons dead.
            _vm.Calls.ToggleCameraHandler = () => host.ToggleCameraAsync();
            _vm.Calls.ToggleMicHandler = () => host.ToggleMicAsync();
            _vm.Calls.ToggleScreenShareHandler = () => host.ToggleScreenShareAsync();
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

    /// <summary>
    /// Teams-style meeting PiP: show parked (or recreated) LiveKit surface while browsing,
    /// hide it when the user returns to Calendar / leaves the meeting.
    /// </summary>
    private void SyncMeetingPip()
    {
        var stage = _vm?.Calendar.Stage;
        var showPip = _vm?.ShowMeetingPip == true
                      && stage is { IsMediaOpen: true, MediaPageUri: not null };

        if (!showPip || stage?.MediaPageUri is not { } uri)
        {
            MeetingPipRoot.IsVisible = false;
            // Return host to parking (MeetingStage will Take on reopen) or shut down if meeting ended.
            if (MeetingPipMediaHost.Children.OfType<InAppMediaHost>().FirstOrDefault() is { } hosted)
            {
                MeetingPipMediaHost.Children.Remove(hosted);
                if (_vm?.Calendar.IsInMeeting == true && stage is { IsMediaOpen: true })
                    MeetingMediaParking.Park(hosted);
                else
                    _ = ShutdownMeetingHostAsync(hosted);
            }
            else if (_vm?.Calendar.IsInMeeting != true)
            {
                _ = MeetingMediaParking.ShutdownAsync();
            }

            return;
        }

        MeetingPipRoot.IsVisible = true;

        var host = MeetingPipMediaHost.Children.OfType<InAppMediaHost>().FirstOrDefault()
                   ?? MeetingMediaParking.Take();
        if (host is null)
        {
            host = new InAppMediaHost();
            host.SetLeaveHandler(() =>
            {
                if (_vm?.Calendar.LeaveCommand.CanExecute(null) == true)
                    _ = _vm.Calendar.LeaveCommand.ExecuteAsync(null);
            });
            host.MediaStateChanged += () =>
            {
                if (_vm?.Calendar.Stage is not { } s) return;
                s.IsCameraOn = host.IsCameraEnabled;
                s.IsMicOn = host.IsMicEnabled;
                s.IsScreenShareOn = host.IsScreenShareEnabled;
            };
            host.ActiveSpeakersChanged += ids => _vm?.Calendar.Stage?.ApplyActiveSpeakers(ids);
            host.MutedParticipantsChanged += ids => _vm?.Calendar.Stage?.ApplyMutedParticipants(ids);
            host.PermissionNeeded += kind =>
            {
                if (_vm?.Calendar.Stage is { } s)
                    _ = s.NotifyMediaPermissionNeededAsync(kind);
            };
        }

        if (!MeetingPipMediaHost.Children.Contains(host))
        {
            // Reparent from wherever the host currently lives (stage or parking).
            if (host.Parent is Panel previous)
                previous.Children.Remove(host);
            MeetingPipMediaHost.Children.Clear();
            MeetingPipMediaHost.Children.Add(host);
        }

        stage.ToggleCameraHandler = () => host.ToggleCameraAsync();
        stage.ToggleMicHandler = () => host.ToggleMicAsync();
        stage.ToggleScreenShareHandler = () => host.ToggleScreenShareAsync();
        host.Show(uri);
        host.HorizontalAlignment = HorizontalAlignment.Stretch;
        host.VerticalAlignment = VerticalAlignment.Stretch;
        host.IsHitTestVisible = false;
        // PiP layout only — do not SyncPreferredMedia here (races cam/mic clicks).
        _ = host.SetPipModeAsync(true);
    }

    private static async Task ShutdownMeetingHostAsync(InAppMediaHost host)
    {
        try { await host.ShutdownAsync(); }
        catch { /* ignore */ }
    }
}
