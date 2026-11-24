using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
        if (_stage is not null) _stage.PropertyChanged += OnStagePropertyChanged;
        SyncMediaHost();
    }

    private void UnbindStage()
    {
        if (_stage is not null) _stage.PropertyChanged -= OnStagePropertyChanged;
        _stage = null;
    }

    private void OnStagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MeetingStageViewModel.IsMediaOpen)
            or nameof(MeetingStageViewModel.MediaPageUri))
            SyncMediaHost();
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

    private async Task TearDownMediaAsync()
    {
        var hosts = MediaHost.Children.OfType<InAppMediaHost>().ToList();
        foreach (var host in hosts)
        {
            try { await host.ShutdownAsync(); }
            catch { /* ignore */ }
        }

        MediaHost.Children.Clear();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void OpenMedia(Uri uri)
    {
        if (MediaHost.Children.OfType<InAppMediaHost>().FirstOrDefault() is not { } host)
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
                _stage.IsCameraOn = host.IsCameraEnabled;
                _stage.IsMicOn = host.IsMicEnabled;
                LayoutMediaHost(host);
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
            _stage.ToggleCameraHandler = () => host.ToggleCameraAsync();
            _stage.ToggleMicHandler = () => host.ToggleMicAsync();
        }

        host.Show(uri);
        LayoutMediaHost(host);
    }

    /// <summary>
    /// Keep LiveKit in an uncovered PiP so Avalonia tiles can own the Meet grid without
    /// occluding WKWebView capture/playback on macOS.
    /// </summary>
    private void LayoutMediaHost(InAppMediaHost host)
    {
        host.Width = 320;
        host.Height = 180;
        host.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        host.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom;
        host.Margin = new Avalonia.Thickness(12);
        host.ZIndex = 5;
        host.IsVisible = true;
        host.IsHitTestVisible = false;
        host.Opacity = _stage?.IsCameraOn == true ? 1 : 0.04;
    }
}
