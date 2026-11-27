using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeetJoe.App.Services;
using MeetJoe.App.Services.Spotify;

namespace MeetJoe.App.ViewModels;

public enum MusicPane
{
    Home,
    Search,
    Library,
    Playlists,
}

public partial class MusicPlaylistItemViewModel(SpotifyPlaylist playlist) : ObservableObject
{
    public string Id { get; } = playlist.Id;
    public string Name { get; } = playlist.Name;
    public string Subtitle { get; } = playlist.TrackCount == 1
        ? "1 track"
        : $"{playlist.TrackCount} tracks";
    public string? ImageUrl { get; } = playlist.ImageUrl;
    public string Uri { get; } = playlist.Uri;
}

public partial class MusicTrackItemViewModel(SpotifyTrack track) : ObservableObject
{
    public string Id { get; } = track.Id;
    public string Name { get; } = track.Name;
    public string Artists { get; } = track.Artists;
    public string? AlbumName { get; } = track.AlbumName;
    public string? ImageUrl { get; } = track.ImageUrl;
    public string Uri { get; } = track.Uri;
    public string DurationText { get; } = FormatDuration(track.DurationMs);

    private static string FormatDuration(int ms)
    {
        if (ms <= 0) return string.Empty;
        var span = TimeSpan.FromMilliseconds(ms);
        return span.TotalHours >= 1
            ? span.ToString(@"h\:mm\:ss")
            : span.ToString(@"m\:ss");
    }
}

/// <summary>Spotify-like Music section — Connect OAuth + library browse + Connect playback.</summary>
public partial class MusicViewModel : ViewModelBase
{
    private readonly SessionContext _context;
    private readonly SpotifyAuthService _auth;
    private readonly SpotifyApiClient _api;
    private readonly DispatcherTimer _playbackTimer;
    private CancellationTokenSource? _loadCts;
    private bool _pausedForCall;

    public MusicViewModel(SessionContext context, ISpotifyTokenStore? tokenStore = null)
    {
        _context = context;
        var store = tokenStore ?? new FileSpotifyTokenStore();
        _auth = new SpotifyAuthService(
            context.Config.SpotifyClientId,
            store,
            uri => context.Platform.OpenUriAsync(uri));
        _api = new SpotifyApiClient(_auth);

        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _playbackTimer.Tick += (_, _) => _ = RefreshPlaybackAsync(silent: true);

        IsConnected = _auth.IsConnected;
        NeedsClientId = !_auth.HasClientId;
        if (IsConnected)
            _ = LoadConnectedAsync();
    }

    public ObservableCollection<MusicPlaylistItemViewModel> Playlists { get; } = [];
    public ObservableCollection<MusicTrackItemViewModel> RecentTracks { get; } = [];
    public ObservableCollection<MusicTrackItemViewModel> LikedTracks { get; } = [];
    public ObservableCollection<MusicTrackItemViewModel> DetailTracks { get; } = [];
    public ObservableCollection<MusicTrackItemViewModel> SearchTracks { get; } = [];
    public ObservableCollection<MusicPlaylistItemViewModel> SearchPlaylists { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowConnectPrompt))]
    [NotifyPropertyChangedFor(nameof(ShowLibrary))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowConnectPrompt))]
    private bool _needsClientId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHome))]
    [NotifyPropertyChangedFor(nameof(ShowSearch))]
    [NotifyPropertyChangedFor(nameof(ShowLibraryPane))]
    [NotifyPropertyChangedFor(nameof(ShowPlaylistsPane))]
    private MusicPane _pane = MusicPane.Home;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string _spotifyDisplayName = string.Empty;
    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private string _detailTitle = string.Empty;
    [ObservableProperty] private bool _showDetail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNowPlaying))]
    [NotifyPropertyChangedFor(nameof(NowPlayingLine))]
    private string? _nowPlayingTitle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NowPlayingLine))]
    private string? _nowPlayingArtist;

    [ObservableProperty] private string? _nowPlayingDevice;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private string? _nowPlayingImageUrl;

    public bool ShowConnectPrompt => !IsConnected;
    public bool ShowLibrary => IsConnected;
    public bool ShowHome => Pane == MusicPane.Home && !ShowDetail;
    public bool ShowSearch => Pane == MusicPane.Search && !ShowDetail;
    public bool ShowLibraryPane => Pane == MusicPane.Library && !ShowDetail;
    public bool ShowPlaylistsPane => Pane == MusicPane.Playlists && !ShowDetail;
    public bool HasNowPlaying => !string.IsNullOrWhiteSpace(NowPlayingTitle);
    public string NowPlayingLine =>
        string.IsNullOrWhiteSpace(NowPlayingArtist)
            ? NowPlayingTitle ?? string.Empty
            : $"{NowPlayingTitle} · {NowPlayingArtist}";

    public bool IsHomeSelected => Pane == MusicPane.Home;
    public bool IsSearchSelected => Pane == MusicPane.Search;
    public bool IsLibrarySelected => Pane == MusicPane.Library;
    public bool IsPlaylistsSelected => Pane == MusicPane.Playlists;

    public async Task LoadAsync()
    {
        if (!IsConnected) return;
        await LoadConnectedAsync();
    }

    public void Detach()
    {
        _playbackTimer.Stop();
        _loadCts?.Cancel();
        _auth.Dispose();
        _api.Dispose();
    }

    /// <summary>Pause Spotify when a MeetJoe call or meeting starts. Does not auto-resume.</summary>
    public async Task PauseForCallAsync()
    {
        if (!IsConnected || !IsPlaying) return;
        try
        {
            await _api.PauseAsync();
            _pausedForCall = true;
            IsPlaying = false;
        }
        catch
        {
            // Best-effort — call audio takes priority.
        }
    }

    [RelayCommand]
    private void ShowHomePane()
    {
        Pane = MusicPane.Home;
        ShowDetail = false;
        OnPaneFlagsChanged();
    }

    [RelayCommand]
    private void ShowSearchPane()
    {
        Pane = MusicPane.Search;
        ShowDetail = false;
        OnPaneFlagsChanged();
    }

    [RelayCommand]
    private void ShowLibraryNav()
    {
        Pane = MusicPane.Library;
        ShowDetail = false;
        OnPaneFlagsChanged();
        if (LikedTracks.Count == 0) _ = LoadConnectedAsync();
    }

    [RelayCommand]
    private void ShowPlaylistsNav()
    {
        Pane = MusicPane.Playlists;
        ShowDetail = false;
        OnPaneFlagsChanged();
    }

    [RelayCommand]
    private void CloseDetail()
    {
        ShowDetail = false;
        DetailTracks.Clear();
        DetailTitle = string.Empty;
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        ErrorMessage = null;
        StatusMessage = null;
        if (NeedsClientId)
        {
            ErrorMessage =
                "Add your Spotify app Client ID: set MEETJOE_SPOTIFY_CLIENT_ID, then restart MeetJoe. " +
                "See docs/SPOTIFY_SETUP.md.";
            return;
        }

        IsBusy = true;
        StatusMessage = "Waiting for Spotify login in your browser…";
        try
        {
            await _auth.ConnectAsync();
            IsConnected = true;
            StatusMessage = null;
            await LoadConnectedAsync();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = null;
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe.Error(ex);
            StatusMessage = null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Disconnect()
    {
        _playbackTimer.Stop();
        _auth.Disconnect();
        IsConnected = false;
        SpotifyDisplayName = string.Empty;
        Playlists.Clear();
        RecentTracks.Clear();
        LikedTracks.Clear();
        DetailTracks.Clear();
        SearchTracks.Clear();
        SearchPlaylists.Clear();
        ClearNowPlaying();
        ShowDetail = false;
        StatusMessage = null;
        ErrorMessage = null;
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (!IsConnected) return;
        ErrorMessage = null;
        IsBusy = true;
        try
        {
            var results = await _api.SearchAsync(SearchQuery);
            SearchTracks.Clear();
            foreach (var track in results.Tracks)
                SearchTracks.Add(new MusicTrackItemViewModel(track));
            SearchPlaylists.Clear();
            foreach (var playlist in results.Playlists)
                SearchPlaylists.Add(new MusicPlaylistItemViewModel(playlist));
            Pane = MusicPane.Search;
            ShowDetail = false;
            OnPaneFlagsChanged();
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe.Error(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task OpenPlaylistAsync(MusicPlaylistItemViewModel? playlist)
    {
        if (playlist is null || !IsConnected) return;
        ErrorMessage = null;
        IsBusy = true;
        try
        {
            var tracks = await _api.GetPlaylistTracksAsync(playlist.Id);
            DetailTracks.Clear();
            foreach (var track in tracks)
                DetailTracks.Add(new MusicTrackItemViewModel(track));
            DetailTitle = playlist.Name;
            ShowDetail = true;
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe.Error(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task PlayTrackAsync(MusicTrackItemViewModel? track)
    {
        if (track is null) return;
        await RunPlayerAsync(() => _api.PlayAsync(track.Uri));
        NowPlayingTitle = track.Name;
        NowPlayingArtist = track.Artists;
        NowPlayingImageUrl = track.ImageUrl;
        IsPlaying = true;
        _pausedForCall = false;
    }

    [RelayCommand]
    private async Task PlayPlaylistAsync(MusicPlaylistItemViewModel? playlist)
    {
        if (playlist is null || string.IsNullOrWhiteSpace(playlist.Uri)) return;
        await RunPlayerAsync(() => _api.PlayAsync(playlist.Uri));
        _pausedForCall = false;
        await RefreshPlaybackAsync(silent: true);
    }

    [RelayCommand]
    private async Task TogglePlayPauseAsync()
    {
        if (IsPlaying)
            await RunPlayerAsync(() => _api.PauseAsync());
        else
            await RunPlayerAsync(() => _api.PlayAsync());
        _pausedForCall = false;
        await RefreshPlaybackAsync(silent: true);
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        await RunPlayerAsync(() => _api.NextAsync());
        await RefreshPlaybackAsync(silent: true);
    }

    [RelayCommand]
    private async Task PreviousAsync()
    {
        await RunPlayerAsync(() => _api.PreviousAsync());
        await RefreshPlaybackAsync(silent: true);
    }

    [RelayCommand]
    private Task RefreshPlaybackCommandAsync() => RefreshPlaybackAsync(silent: false);

    private async Task LoadConnectedAsync()
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var me = await _api.GetMeAsync(ct);
            SpotifyDisplayName = me.DisplayName;

            var playlistsTask = _api.GetPlaylistsAsync(ct);
            var recentTask = _api.GetRecentlyPlayedAsync(ct: ct);
            var likedTask = _api.GetLikedTracksAsync(ct: ct);
            await Task.WhenAll(playlistsTask, recentTask, likedTask);

            Playlists.Clear();
            foreach (var playlist in await playlistsTask)
                Playlists.Add(new MusicPlaylistItemViewModel(playlist));

            RecentTracks.Clear();
            foreach (var track in await recentTask)
                RecentTracks.Add(new MusicTrackItemViewModel(track));

            LikedTracks.Clear();
            foreach (var track in await likedTask)
                LikedTracks.Add(new MusicTrackItemViewModel(track));

            await RefreshPlaybackAsync(silent: true);
            if (!_playbackTimer.IsEnabled) _playbackTimer.Start();
        }
        catch (OperationCanceledException)
        {
            // Navigated away / replaced load.
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe.Error(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshPlaybackAsync(bool silent)
    {
        if (!IsConnected) return;
        try
        {
            var state = await _api.GetPlaybackAsync();
            if (state?.Track is { } track)
            {
                NowPlayingTitle = track.Name;
                NowPlayingArtist = track.Artists;
                NowPlayingImageUrl = track.ImageUrl;
                IsPlaying = state.IsPlaying;
                NowPlayingDevice = state.DeviceName;
            }
            else if (!_pausedForCall)
            {
                // Keep last title if Spotify returns no active device.
            }
        }
        catch (Exception ex)
        {
            if (!silent) ErrorMessage = Describe.Error(ex);
        }
    }

    private async Task RunPlayerAsync(Func<Task> action)
    {
        ErrorMessage = null;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe.Error(ex);
        }
    }

    private void ClearNowPlaying()
    {
        NowPlayingTitle = null;
        NowPlayingArtist = null;
        NowPlayingImageUrl = null;
        NowPlayingDevice = null;
        IsPlaying = false;
    }

    private void OnPaneFlagsChanged()
    {
        OnPropertyChanged(nameof(IsHomeSelected));
        OnPropertyChanged(nameof(IsSearchSelected));
        OnPropertyChanged(nameof(IsLibrarySelected));
        OnPropertyChanged(nameof(IsPlaylistsSelected));
        OnPropertyChanged(nameof(ShowHome));
        OnPropertyChanged(nameof(ShowSearch));
        OnPropertyChanged(nameof(ShowLibraryPane));
        OnPropertyChanged(nameof(ShowPlaylistsPane));
    }

    partial void OnPaneChanged(MusicPane value) => OnPaneFlagsChanged();
    partial void OnShowDetailChanged(bool value) => OnPaneFlagsChanged();
}
