using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeetJoe.App.Controls;
using MeetJoe.App.ViewModels;

namespace MeetJoe.App.Views;

public partial class MeetingStageView : UserControl
{
    private CalendarViewModel? _vm;
    private MeetingStageViewModel? _stage;

    public MeetingStageView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BindViewModel(DataContext as CalendarViewModel);

        // Tunnel so Enter is seen before the text box inserts a newline.
        LiveChatComposer.AddHandler(KeyDownEvent, OnChatKeyDown, RoutingStrategies.Tunnel);
        LobbyChatComposer.AddHandler(KeyDownEvent, OnChatKeyDown, RoutingStrategies.Tunnel);
        ComposerFileDrop.Enable(LiveChatDropZone, files =>
            _stage?.AttachFilesAsync(files) ?? Task.CompletedTask);
        ComposerFileDrop.Enable(LobbyChatDropZone, files =>
            _stage?.AttachFilesAsync(files) ?? Task.CompletedTask);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        // Keep LiveKit alive in shell PiP while the user browses other sections.
        ParkMediaHost();
    }

    private void BindViewModel(CalendarViewModel? vm)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnCalendarPropertyChanged;
        UnbindStage();
        _vm = vm;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnCalendarPropertyChanged;
            BindStage(_vm.Stage);
        }

        SyncMediaHost();
    }

    private void OnCalendarPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CalendarViewModel.Stage))
            BindStage(_vm?.Stage);
    }

    private void BindStage(MeetingStageViewModel? stage)
    {
        UnbindStage();
        _stage = stage;
        if (_stage is not null)
        {
            _stage.PropertyChanged += OnStagePropertyChanged;
            _stage.ChatFollowRequested += OnChatFollowRequested;
            OnChatFollowRequested();
        }

        SyncMediaHost();
    }

    private void UnbindStage()
    {
        if (_stage is not null)
        {
            _stage.PropertyChanged -= OnStagePropertyChanged;
            _stage.ChatFollowRequested -= OnChatFollowRequested;
        }

        _stage = null;
    }

    private void OnChatFollowRequested() =>
        Dispatcher.UIThread.Post(() =>
        {
            if (LiveChatScroller.IsEffectivelyVisible)
                LiveChatScroller.ScrollToEnd();
            if (LobbyChatScroller.IsEffectivelyVisible)
                LobbyChatScroller.ScrollToEnd();
        }, DispatcherPriority.Loaded);

    private void OnStagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MeetingStageViewModel.IsMediaOpen)
            or nameof(MeetingStageViewModel.MediaPageUri))
            SyncMediaHost();
        else if (e.PropertyName is nameof(MeetingStageViewModel.ShowAddPeople)
                 or nameof(MeetingStageViewModel.IsLiveKitSurfaceReady))
            // WKWebView paints above Avalonia; hide it so the invite dialog is not clipped.
            LayoutMediaHostForInvite();
    }

    private void OnChatKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;

        e.Handled = true;
        if (_stage?.SendChatCommand.CanExecute(null) == true)
            _stage.SendChatCommand.Execute(null);
    }

    private void SyncMediaHost()
    {
        if (_stage is not { IsMediaOpen: true })
        {
            _ = TearDownMediaAsync();
            return;
        }

        OpenMedia(_stage.MediaPageUri);
    }

    private void ParkMediaHost()
    {
        if (_stage is not { IsMediaOpen: true }) return;
        if (MediaHost.Children.OfType<InAppMediaHost>().FirstOrDefault() is not { } host) return;

        MediaHost.Children.Remove(host);
        MeetingMediaParking.Park(host);
        _ = host.SetPipModeAsync(true);
    }

    private async Task TearDownMediaAsync()
    {
        var hosts = MediaHost.Children.OfType<InAppMediaHost>().ToList();
        foreach (var host in hosts)
        {
            try { await host.ShutdownAsync(); }
            catch { /* ignore */ }
        }

        MediaHost.Children.Clear();
        // Only kill the parked PiP host when the meeting itself is ending (not when parking).
        if (_stage is not { IsMediaOpen: true })
            await MeetingMediaParking.ShutdownAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void OpenMedia(Uri uri)
    {
        var host = MediaHost.Children.OfType<InAppMediaHost>().FirstOrDefault();
        var reclaimed = false;
        if (host is null)
        {
            host = MeetingMediaParking.Take();
            if (host is not null)
            {
                reclaimed = true;
                MediaHost.Children.Clear();
                MediaHost.Children.Add(host);
            }
        }

        if (host is null)
        {
            host = new InAppMediaHost();
            host.SetLeaveHandler(() =>
            {
                if (_vm?.LeaveCommand.CanExecute(null) == true)
                    _ = _vm.LeaveCommand.ExecuteAsync(null);
            });
            host.MediaStateChanged += () =>
            {
                if (_stage is null) return;
                // Avoid layout→preferred-media reapply racing cam/mic clicks.
                _stage.IsCameraOn = host.IsCameraEnabled;
                _stage.IsMicOn = host.IsMicEnabled;
                _stage.IsScreenShareOn = host.IsScreenShareEnabled;
                _stage.IsRecording = host.IsRecording;
            };
            host.RecordingTimerChanged += startedAt => _stage?.ApplyLocalRecordingTimer(startedAt);
            host.RoomRecordingChanged += (on, by, at) => _stage?.ApplyRoomRecording(on, by, at);
            host.SessionStarted += ms => _stage?.ApplySessionStart(ms);
            host.SessionWarning += () => _stage?.ApplySessionWarning();
            host.SessionExpired += () =>
            {
                if (_stage?.ConsumeSessionExpired() != true) return;
                if (_vm?.LeaveCommand.CanExecute(null) == true)
                    _ = _vm.LeaveCommand.ExecuteAsync(null);
            };
            host.ActiveSpeakersChanged += ids =>
            {
                if (_stage is null) return;
                _stage.ApplyActiveSpeakers(ids);
            };
            host.MutedParticipantsChanged += ids =>
            {
                if (_stage is null) return;
                _stage.ApplyMutedParticipants(ids);
            };
            host.PermissionNeeded += kind =>
            {
                if (_stage is null) return;
                _ = _stage.NotifyMediaPermissionNeededAsync(kind);
            };
            host.MediaReady += () =>
            {
                if (_stage is null) return;
                LayoutMediaHost(host);
                _stage.MarkLiveKitSurfaceReady();
            };
            MediaHost.Children.Clear();
            MediaHost.Children.Add(host);
        }

        if (_stage is not null)
        {
            host.ApplyPreferredMedia(_stage.IsCameraOn, _stage.IsMicOn);
            _stage.ToggleCameraHandler = () => host.ToggleCameraAsync();
            _stage.ToggleMicHandler = () => host.ToggleMicAsync();
            _stage.ToggleScreenShareHandler = () => host.ToggleScreenShareAsync();
            _stage.StartRecordingHandler = (url, token, name) => host.StartRecordingAsync(url, token, name);
            _stage.StopRecordingHandler = () => host.StopRecordingAsync();
        }

        host.Show(uri);
        LayoutMediaHost(host);
        _ = host.SetPipModeAsync(false);
        // Only enforce prefs when coming back from PiP — not on every OpenMedia.
        if (reclaimed) _ = host.SyncPreferredMediaToPageAsync();
    }

    /// <summary>
    /// WebView fills the stage so LiveKit paints video inside each grid tile (initials when
    /// camera off). Do not cover it with Avalonia chrome on macOS — hide the native surface
    /// while Add people is open so the modal is not occluded.
    /// </summary>
    private void LayoutMediaHost(InAppMediaHost host)
    {
        var inviting = _stage?.ShowAddPeople == true;

        host.Width = double.NaN;
        host.Height = double.NaN;
        host.Margin = default;
        host.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        host.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
        host.ZIndex = 5;
        host.IsHitTestVisible = false;
        host.Opacity = 1;
        host.IsVisible = !inviting;

        MediaHost.Width = double.NaN;
        MediaHost.Height = double.NaN;
        MediaHost.Margin = default;
        MediaHost.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        MediaHost.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
        MediaHost.IsVisible = !inviting;
    }

    private void LayoutMediaHostForInvite()
    {
        if (MediaHost.Children.OfType<InAppMediaHost>().FirstOrDefault() is { } host)
            LayoutMediaHost(host);
        else
            MediaHost.IsVisible = _stage?.ShowAddPeople != true;
    }
}
