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
    private FloatingPipDrag? _meetingPipDrag;
    private FloatingPipDrag? _callPipDrag;
    private YouTubePlayerHost? _musicHost;
    private Panel? _musicPlayerSlot;

    public ShellView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BindViewModel(DataContext as ShellViewModel);
        CallChatComposer.AddHandler(KeyDownEvent, OnCallChatKeyDown, RoutingStrategies.Tunnel);
        ComposerFileDrop.Enable(CallChatDropZone, files =>
            _vm?.Calls.AttachFilesAsync(files) ?? Task.CompletedTask);

        _meetingPipDrag = new FloatingPipDrag(MeetingPipRoot, MeetingPipHeader);
        _callPipDrag = new FloatingPipDrag(CallStageRoot, CallStageHeader);
        // Place default PiP once the shell has a real size.
        LayoutUpdated += OnShellLayoutUpdated;
        MusicView.PlayerSlotChanged += OnMusicPlayerSlotChanged;
        DetachedFromVisualTree += (_, _) =>
        {
            MusicView.PlayerSlotChanged -= OnMusicPlayerSlotChanged;
            _ = ShutdownMusicHostAsync();
        };
    }

    private Size _lastShellContentSize;

    private void OnShellLayoutUpdated(object? sender, EventArgs e)
    {
        // Re-clamp PiP when the shell content area changes size (resize / rail).
        if (MeetingPipRoot.Parent is not Control parent) return;
        var size = parent.Bounds.Size;
        if (size == _lastShellContentSize) return;
        _lastShellContentSize = size;
        if (MeetingPipRoot.IsVisible)
            _meetingPipDrag?.ApplyFloatingLayout(300, 248);
        if (_callPipActive)
            _callPipDrag?.ApplyFloatingLayout(300, 248);
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
            UnbindMusic(_vm.Music);
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
            BindMusic(_vm.Music);
        }

        SyncCallStage();
        SyncMeetingPip();
        SyncMusicPlayerSurface();
    }

    private void BindMusic(MusicViewModel music)
    {
        music.NavigateRequested += OnMusicNavigate;
        music.PauseRequested += OnMusicPause;
        music.ResumeRequested += OnMusicResume;
        music.StopRequested += OnMusicStop;
        music.PropertyChanged += OnMusicPropertyChanged;
    }

    private void UnbindMusic(MusicViewModel music)
    {
        music.NavigateRequested -= OnMusicNavigate;
        music.PauseRequested -= OnMusicPause;
        music.ResumeRequested -= OnMusicResume;
        music.StopRequested -= OnMusicStop;
        music.PropertyChanged -= OnMusicPropertyChanged;
    }

    private void OnMusicPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MusicViewModel.HasPlayer)
            or nameof(MusicViewModel.ActiveUrl))
            SyncMusicPlayerSurface();
    }

    private void OnMusicPlayerSlotChanged(Panel? slot)
    {
        _musicPlayerSlot = slot;
        Dispatcher.UIThread.Post(() =>
        {
            // Returning to Music: dock the keep-alive host back into the page slot.
            // First open after process start with a restored URL: re-fire navigation.
            if (_vm?.Music.HasPlayer == true && _musicHost is null)
                _vm.Music.RequestCurrentNavigation();
            else
                SyncMusicPlayerSurface();
        }, DispatcherPriority.Loaded);
    }

    private void OnMusicNavigate(Uri url)
    {
        Dispatcher.UIThread.Post(() =>
        {
            EnsureMusicHost();
            _musicHost?.Show(url);
            SyncMusicPlayerSurface();
        }, DispatcherPriority.Loaded);
    }

    private void OnMusicPause() => _ = _musicHost?.PauseAsync();

    private void OnMusicResume() => _ = _musicHost?.PlayAsync();

    private void OnMusicStop() => _ = ShutdownMusicHostAsync();

    private void EnsureMusicHost()
    {
        if (_musicHost is not null) return;
        _musicHost = new YouTubePlayerHost();
    }

    private void SyncMusicPlayerSurface()
    {
        if (_vm?.Music.HasPlayer != true || _musicHost is null)
        {
            // Keep host only while a session is active.
            if (_vm?.Music.HasPlayer != true && _musicHost is not null)
                _ = ShutdownMusicHostAsync();
            return;
        }

        var target = _musicPlayerSlot ?? MusicPlayerKeepAlive;
        if (ReferenceEquals(_musicHost.Parent, target)) return;

        if (_musicHost.Parent is Panel previous)
            previous.Children.Remove(_musicHost);
        target.Children.Clear();
        target.Children.Add(_musicHost);
    }

    private async Task ShutdownMusicHostAsync()
    {
        var host = _musicHost;
        _musicHost = null;
        if (host is null) return;

        if (host.Parent is Panel parent)
            parent.Children.Remove(host);
        MusicPlayerKeepAlive.Children.Clear();
        _musicPlayerSlot?.Children.Clear();

        try { await host.ShutdownAsync(); }
        catch { /* ignore */ }
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
            or nameof(ShellViewModel.ShowMeetingPip)
            or nameof(ShellViewModel.ShowMusicMiniPlayer))
        {
            SyncCallStage();
            SyncMeetingPip();
            SyncMusicPlayerSurface();
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
            if (_callPipDrag is not null)
            {
                _callPipDrag.IsEnabled = false;
                _callPipDrag.ResetSavedPosition();
            }
            _callPipActive = false;
            CallStageRoot.IsVisible = false;
            CallStageRoot.ClearValue(WidthProperty);
            CallStageRoot.ClearValue(HeightProperty);
            CallStageRoot.ClearValue(MarginProperty);
            CallStageRoot.HorizontalAlignment = HorizontalAlignment.Stretch;
            CallStageRoot.VerticalAlignment = VerticalAlignment.Stretch;
            _ = TearDownCallMediaAsync();
            return;
        }

        CallStageRoot.IsVisible = true;

        if (_vm.ShowCallStage)
        {
            if (_callPipDrag is { IsEnabled: true })
                _callPipDrag.RememberCurrentPosition();
            if (_callPipDrag is not null)
                _callPipDrag.IsEnabled = false;

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
            CallStageHeader.Padding = new Thickness(16, 10);
            CallStageControls.Padding = new Thickness(16, 12);
            CallStageRoot.ClipToBounds = false;
        }
        else
        {
            // Teams-style floating PiP — draggable within the shell content area.
            CallStageRoot.Opacity = 1;
            CallStageRoot.IsHitTestVisible = true;
            CallStageRoot.ClipToBounds = true;
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
            CallStageControls.Padding = new Thickness(8, 6);
            if (_callPipDrag is not null)
            {
                _callPipDrag.IsEnabled = true;
                _callPipDrag.ApplyFloatingLayout(300, 248);
            }
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
                    _ = _vm.Calls.HangUpCommand.ExecuteAsync(null);
            });
            host.MediaStateChanged += () =>
            {
                if (_vm is null) return;
                // Do not call LayoutCallMediaHost here — it used to re-apply preferred media and
                // race user cam/mic clicks (often undoing the toggle).
                _vm.Calls.IsCameraOn = host.IsCameraEnabled;
                _vm.Calls.IsMicOn = host.IsMicEnabled;
                _vm.Calls.IsScreenShareOn = host.IsScreenShareEnabled;
                _vm.Calls.IsRecording = host.IsRecording;
            };
            host.RecordingTimerChanged += startedAt => _vm?.Calls.ApplyLocalRecordingTimer(startedAt);
            host.RoomRecordingChanged += (on, by, at) => _vm?.Calls.ApplyRoomRecording(on, by, at);
            host.SessionStarted += ms => _vm?.Calls.ApplySessionStart(ms);
            host.SessionWarning += () => _vm?.Calls.ApplySessionWarning();
            host.SessionExpired += () =>
            {
                if (_vm?.Calls.ConsumeSessionExpired() != true) return;
                if (_vm.Calls.HangUpCommand.CanExecute(null))
                    _ = _vm.Calls.HangUpCommand.ExecuteAsync(null);
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
            _vm.Calls.StartRecordingHandler = (url, token, name) => host.StartRecordingAsync(url, token, name);
            _vm.Calls.StopRecordingHandler = () => host.StopRecordingAsync();
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
            if (MeetingPipRoot.IsVisible)
                _meetingPipDrag?.RememberCurrentPosition();
            if (_meetingPipDrag is not null)
                _meetingPipDrag.IsEnabled = false;
            MeetingPipRoot.IsVisible = false;
            // Return host to parking (MeetingStage will Take on reopen) or shut down if meeting ended.
            if (MeetingPipMediaHost.Children.OfType<InAppMediaHost>().FirstOrDefault() is { } hosted)
            {
                MeetingPipMediaHost.Children.Remove(hosted);
                if (_vm?.Calendar.IsInMeeting == true && stage is { IsMediaOpen: true })
                    MeetingMediaParking.Park(hosted);
                else
                {
                    _meetingPipDrag?.ResetSavedPosition();
                    _ = ShutdownMeetingHostAsync(hosted);
                }
            }
            else if (_vm?.Calendar.IsInMeeting != true)
            {
                _meetingPipDrag?.ResetSavedPosition();
                _ = MeetingMediaParking.ShutdownAsync();
            }

            return;
        }

        MeetingPipRoot.IsVisible = true;
        if (_meetingPipDrag is not null)
        {
            _meetingPipDrag.IsEnabled = true;
            _meetingPipDrag.ApplyFloatingLayout(300, 248);
        }

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
                s.IsRecording = host.IsRecording;
            };
            host.RecordingTimerChanged += startedAt =>
                _vm?.Calendar.Stage?.ApplyLocalRecordingTimer(startedAt);
            host.RoomRecordingChanged += (on, by, at) =>
                _vm?.Calendar.Stage?.ApplyRoomRecording(on, by, at);
            host.SessionStarted += ms => _vm?.Calendar.Stage?.ApplySessionStart(ms);
            host.SessionWarning += () => _vm?.Calendar.Stage?.ApplySessionWarning();
            host.SessionExpired += () =>
            {
                if (_vm?.Calendar.Stage?.ConsumeSessionExpired() != true) return;
                if (_vm.Calendar.LeaveCommand.CanExecute(null))
                    _ = _vm.Calendar.LeaveCommand.ExecuteAsync(null);
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
        stage.StartRecordingHandler = (url, token, name) => host.StartRecordingAsync(url, token, name);
        stage.StopRecordingHandler = () => host.StopRecordingAsync();
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
