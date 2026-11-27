using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeetJoe.App.Controls;
using MeetJoe.App.ViewModels;

namespace MeetJoe.App.Views;

public partial class MusicView : UserControl
{
    private MusicViewModel? _vm;
    private YouTubePlayerHost? _host;

    public MusicView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Bind(DataContext as MusicViewModel);
        AttachedToVisualTree += (_, _) =>
        {
            EnsureHost();
            _vm?.RequestCurrentNavigation();
        };
        DetachedFromVisualTree += (_, _) => _ = TearDownHostAsync();
    }

    private void Bind(MusicViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.NavigateRequested -= OnNavigate;
            _vm.PauseRequested -= OnPause;
            _vm.ResumeRequested -= OnResume;
            _vm.StopRequested -= OnStop;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }

        _vm = vm;
        if (_vm is null) return;

        _vm.NavigateRequested += OnNavigate;
        _vm.PauseRequested += OnPause;
        _vm.ResumeRequested += OnResume;
        _vm.StopRequested += OnStop;
        _vm.PropertyChanged += OnVmPropertyChanged;
        EnsureHost();
        _vm.RequestCurrentNavigation();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MusicViewModel.HasPlayer) && _vm?.HasPlayer == true)
            EnsureHost();
    }

    private void OnNavigate(Uri url)
    {
        Dispatcher.UIThread.Post(() =>
        {
            EnsureHost();
            _host?.Show(url);
        });
    }

    private void OnPause() => _ = _host?.PauseAsync();

    private void OnResume() => _ = _host?.PlayAsync();

    private void OnStop() => _ = TearDownHostAsync();

    private void EnsureHost()
    {
        if (_host is not null) return;
        if (PlayerHost is null) return;

        _host = new YouTubePlayerHost();
        PlayerHost.Children.Clear();
        PlayerHost.Children.Add(_host);
    }

    private async Task TearDownHostAsync()
    {
        var host = _host;
        _host = null;
        if (host is null) return;
        PlayerHost?.Children.Clear();
        await host.ShutdownAsync();
    }

    private void OnUrlKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is MusicViewModel music && music.OpenUrlCommand.CanExecute(null))
        {
            music.OpenUrlCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnRecentDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not ListBox { SelectedItem: MusicRecentItemViewModel item }) return;
        if (DataContext is not MusicViewModel music) return;
        if (music.OpenRecentCommand.CanExecute(item))
            music.OpenRecentCommand.Execute(item);
    }
}
