using MeetJoe.App.Services;
using MeetJoe.App.Services.Spotify;
using MeetJoe.App.ViewModels;

namespace MeetJoe.App.Tests;

public class MusicViewModelTests
{
    private readonly World _world = new();

    [Fact]
    public void Music_starts_disconnected_with_client_id_ready()
    {
        var session = _world.NewSession();
        var music = new MusicViewModel(session, new MemorySpotifyTokenStore());

        Assert.False(music.IsConnected);
        Assert.False(music.NeedsClientId);
        Assert.True(music.ShowConnectPrompt);
        Assert.False(music.HasNowPlaying);
    }

    [Fact]
    public void Disconnect_clears_now_playing()
    {
        var store = new MemorySpotifyTokenStore();
        store.SaveRefreshToken("fake-refresh");
        var config = new AppConfig { SpotifyClientId = "test-client-id" };
        var session = _world.NewSession() with { Config = config };
        var music = new MusicViewModel(session, store);

        Assert.True(music.IsConnected);

        music.DisconnectCommand.Execute(null);

        Assert.False(music.IsConnected);
        Assert.Null(store.LoadRefreshToken());
        Assert.False(music.HasNowPlaying);
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
