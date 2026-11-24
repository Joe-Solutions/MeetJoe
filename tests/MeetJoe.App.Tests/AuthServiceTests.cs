using System.Net;
using System.Text;
using MeetJoe.App.Services;

namespace MeetJoe.App.Tests;

public class AuthServiceTests
{
    private readonly MemorySessionStore _store = new();
    private readonly TokenServer _server = new();

    private AuthService NewAuth() =>
        new(new HttpClient(_server) { BaseAddress = new Uri("https://api.test/") }, _store);

    [Fact]
    public async Task Signing_in_saves_the_refresh_token()
    {
        await NewAuth().SignInAsync("ana@example.com", "pw");

        Assert.Equal("refresh-1", _store.Token);
    }

    [Fact]
    public async Task A_new_launch_resumes_with_the_saved_token_and_keeps_the_rolled_one()
    {
        _store.Token = "refresh-saved";
        var auth = NewAuth();

        Assert.True(await auth.TryResumeAsync());

        Assert.True(auth.IsSignedIn);
        Assert.Equal("refresh-saved", _server.LastRefreshToken);
        Assert.Equal("refresh-1", _store.Token);
    }

    [Fact]
    public async Task A_rejected_saved_token_is_forgotten()
    {
        _store.Token = "refresh-revoked";
        _server.RejectRefresh = true;
        var auth = NewAuth();

        Assert.False(await auth.TryResumeAsync());

        Assert.False(auth.IsSignedIn);
        Assert.Null(_store.Token);
    }

    [Fact]
    public async Task Resuming_offline_keeps_the_saved_token_for_next_time()
    {
        _store.Token = "refresh-saved";
        _server.Offline = true;

        Assert.False(await NewAuth().TryResumeAsync());

        Assert.Equal("refresh-saved", _store.Token);
    }

    [Fact]
    public async Task Signing_out_forgets_the_saved_token()
    {
        var auth = NewAuth();
        await auth.SignInAsync("ana@example.com", "pw");

        auth.SignOut();

        Assert.Null(_store.Token);
    }

    private sealed class MemorySessionStore : ISessionStore
    {
        public string? Token { get; set; }
        public string? LoadRefreshToken() => Token;
        public void SaveRefreshToken(string refreshToken) => Token = refreshToken;
        public void Clear() => Token = null;
    }

    /// <summary>Answers the OAuth token endpoint; every success issues the next refresh token.</summary>
    private sealed class TokenServer : HttpMessageHandler
    {
        private int _issued;

        public bool RejectRefresh { get; set; }
        public bool Offline { get; set; }
        public string? LastRefreshToken { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Offline) throw new HttpRequestException("offline");
            var form = (await request.Content!.ReadAsStringAsync(ct))
                .Split('&')
                .Select(pair => pair.Split('='))
                .ToDictionary(kv => kv[0], kv => Uri.UnescapeDataString(kv[1]));

            if (form["grant_type"] == "refresh_token")
            {
                LastRefreshToken = form["refresh_token"];
                if (RejectRefresh)
                    return Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"The token is no longer valid."}""");
            }

            _issued++;
            return Json(HttpStatusCode.OK, $$"""{"access_token":"access-{{_issued}}","refresh_token":"refresh-{{_issued}}","expires_in":1800}""");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
