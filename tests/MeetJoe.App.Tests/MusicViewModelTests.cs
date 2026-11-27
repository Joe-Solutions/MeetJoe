using MeetJoe.App.Services;
using MeetJoe.App.ViewModels;

namespace MeetJoe.App.Tests;

public class MusicViewModelTests
{
    private readonly World _world = new();

    [Fact]
    public void OpenUrl_normalizes_youtube_watch_link()
    {
        var path = Path.Combine(Path.GetTempPath(), "meetjoe-music-recent-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var music = new MusicViewModel(_world.NewSession(), path);
            music.UrlInput = "https://youtu.be/dQw4w9WgXcQ";
            music.OpenUrlCommand.Execute(null);

            Assert.True(music.HasNowPlaying);
            Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", music.ActiveUrl!.AbsoluteUri);
            Assert.Single(music.RecentUrls);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void OpenUrl_rejects_non_youtube()
    {
        var path = Path.Combine(Path.GetTempPath(), "meetjoe-music-recent-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var music = new MusicViewModel(_world.NewSession(), path);
            music.UrlInput = "https://example.com/watch";
            music.OpenUrlCommand.Execute(null);

            Assert.False(music.HasNowPlaying);
            Assert.NotNull(music.ErrorMessage);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Stop_clears_now_playing()
    {
        var path = Path.Combine(Path.GetTempPath(), "meetjoe-music-recent-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var music = new MusicViewModel(_world.NewSession(), path);
            music.UrlInput = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";
            music.OpenUrlCommand.Execute(null);
            music.StopCommand.Execute(null);

            Assert.False(music.HasNowPlaying);
            Assert.Null(music.ActiveUrl);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void YouTubeUrl_parses_shorts()
    {
        Assert.True(YouTubeUrl.TryNormalize(
            "https://www.youtube.com/shorts/dQw4w9WgXcQ", out var url, out _));
        Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", url.AbsoluteUri);
    }

    [Fact]
    public async Task Shell_includes_music_between_files_and_settings()
    {
        var app = _world.NewApp();
        await app.EnterAsync();
        var shell = Assert.IsType<ShellViewModel>(app.Current);

        Assert.Equal("Music", shell.RailItems[^1].Label);
        Assert.Same(shell.Music, shell.RailItems[^1].Section);
        Assert.False(shell.ShowMusicMiniPlayer);

        shell.OpenMusicCommand.Execute(null);
        Assert.Same(shell.Music, shell.Current);
        Assert.True(shell.RailItems.Single(i => i.Label == "Music").IsSelected);
    }
}
