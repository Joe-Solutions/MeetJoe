using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeetJoe.App.Services;

namespace MeetJoe.App.ViewModels;

public partial class MusicRecentItemViewModel(string url, string title) : ObservableObject
{
    public string Url { get; } = url;
    public string Title { get; } = title;
}

/// <summary>Music section — paste or browse a YouTube URL in an in-app WebView.</summary>
public partial class MusicViewModel : ViewModelBase
{
    private const int MaxRecent = 12;
    private readonly string _recentPath;

    public MusicViewModel(SessionContext context, string? recentStorePath = null)
    {
        _ = context;
        _recentPath = recentStorePath
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MeetJoe",
                "music-recent.json");
        LoadRecentFromDisk();
    }

    public ObservableCollection<MusicRecentItemViewModel> RecentUrls { get; } = [];

    public bool HasRecent => RecentUrls.Count > 0;

    [ObservableProperty] private string _urlInput = string.Empty;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _statusMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNowPlaying))]
    [NotifyPropertyChangedFor(nameof(NowPlayingLine))]
    [NotifyPropertyChangedFor(nameof(HasPlayer))]
    private string? _nowPlayingTitle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NowPlayingLine))]
    private Uri? _activeUrl;

    [ObservableProperty] private bool _isPlaying;

    public bool HasNowPlaying => ActiveUrl is not null;
    public bool HasPlayer => ActiveUrl is not null;
    public string NowPlayingLine =>
        string.IsNullOrWhiteSpace(NowPlayingTitle) ? "YouTube" : NowPlayingTitle;

    /// <summary>View hosts the WebView; raised when ActiveUrl should be shown/refreshed.</summary>
    public event Action<Uri>? NavigateRequested;

    /// <summary>Ask the hosted WebView to pause / resume HTML5 or YouTube player API.</summary>
    public event Action? PauseRequested;
    public event Action? ResumeRequested;

    /// <summary>Tear down the WebView (Stop / leaving session).</summary>
    public event Action? StopRequested;

    public void Detach()
    {
        NavigateRequested = null;
        PauseRequested = null;
        ResumeRequested = null;
        StopRequested = null;
    }

    public Task LoadAsync() => Task.CompletedTask;

    /// <summary>Pause YouTube when a MeetJoe call or meeting starts. Does not auto-resume.</summary>
    public Task PauseForCallAsync()
    {
        if (!HasNowPlaying) return Task.CompletedTask;
        PauseRequested?.Invoke();
        IsPlaying = false;
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void OpenUrl()
    {
        ErrorMessage = null;
        if (!YouTubeUrl.TryNormalize(UrlInput, out var url, out var title))
        {
            ErrorMessage = "Paste a YouTube link (youtube.com or youtu.be).";
            return;
        }

        NavigateTo(url, title);
    }

    [RelayCommand]
    private void OpenYouTubeHome()
    {
        ErrorMessage = null;
        UrlInput = "https://www.youtube.com";
        NavigateTo(new Uri("https://www.youtube.com/"), "YouTube");
    }

    [RelayCommand]
    private void OpenRecent(MusicRecentItemViewModel? item)
    {
        if (item is null) return;
        UrlInput = item.Url;
        if (!YouTubeUrl.TryNormalize(item.Url, out var url, out var title))
        {
            ErrorMessage = "That saved link is no longer valid.";
            return;
        }

        NavigateTo(url, string.IsNullOrWhiteSpace(item.Title) ? title : item.Title);
    }

    [RelayCommand]
    private void TogglePlayPause()
    {
        if (!HasNowPlaying) return;
        if (IsPlaying)
        {
            PauseRequested?.Invoke();
            IsPlaying = false;
        }
        else
        {
            ResumeRequested?.Invoke();
            IsPlaying = true;
        }
    }

    [RelayCommand]
    private void Stop()
    {
        ActiveUrl = null;
        NowPlayingTitle = null;
        IsPlaying = false;
        StopRequested?.Invoke();
        StatusMessage = null;
    }

    [RelayCommand]
    private void ClearRecent()
    {
        RecentUrls.Clear();
        OnPropertyChanged(nameof(HasRecent));
        SaveRecentToDisk();
    }

    private void NavigateTo(Uri url, string title)
    {
        ActiveUrl = url;
        NowPlayingTitle = title;
        IsPlaying = true;
        StatusMessage = "Playing in MeetJoe — leave Music and audio may stop until you return.";
        Remember(url.AbsoluteUri, title);
        NavigateRequested?.Invoke(url);
    }

    /// <summary>Called by the view when the WebView is (re)attached and needs the current URL.</summary>
    public void RequestCurrentNavigation()
    {
        if (ActiveUrl is { } url)
            NavigateRequested?.Invoke(url);
    }

    private void Remember(string url, string title)
    {
        for (var i = RecentUrls.Count - 1; i >= 0; i--)
        {
            if (string.Equals(RecentUrls[i].Url, url, StringComparison.OrdinalIgnoreCase))
                RecentUrls.RemoveAt(i);
        }

        RecentUrls.Insert(0, new MusicRecentItemViewModel(url, title));
        while (RecentUrls.Count > MaxRecent)
            RecentUrls.RemoveAt(RecentUrls.Count - 1);
        OnPropertyChanged(nameof(HasRecent));
        SaveRecentToDisk();
    }

    private void LoadRecentFromDisk()
    {
        try
        {
            if (!File.Exists(_recentPath)) return;
            var json = File.ReadAllText(_recentPath);
            var items = JsonSerializer.Deserialize<List<RecentDto>>(json);
            if (items is null) return;
            RecentUrls.Clear();
            foreach (var item in items.Take(MaxRecent))
            {
                if (string.IsNullOrWhiteSpace(item.Url)) continue;
                RecentUrls.Add(new MusicRecentItemViewModel(item.Url, item.Title ?? "YouTube"));
            }
            OnPropertyChanged(nameof(HasRecent));
        }
        catch
        {
            // Ignore corrupt local cache.
        }
    }

    private void SaveRecentToDisk()
    {
        try
        {
            var dir = Path.GetDirectoryName(_recentPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var items = RecentUrls.Select(r => new RecentDto(r.Url, r.Title)).ToList();
            File.WriteAllText(_recentPath, JsonSerializer.Serialize(items));
        }
        catch
        {
            // Local cache is best-effort.
        }
    }

    private sealed record RecentDto(string Url, string? Title);
}
