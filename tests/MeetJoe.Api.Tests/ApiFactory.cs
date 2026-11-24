using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeetJoe.Application.Common;
using MeetJoe.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace MeetJoe.Api.Tests;

/// <summary>
/// The real API, in-process, on a throwaway database in a real PostgreSQL server.
/// Uses the Docker Compose instance by default; set MEETJOE_TEST_POSTGRES to point elsewhere
/// (a connection string without a Database).
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string PostgresVariable = "MEETJOE_TEST_POSTGRES";
    private const string ComposePostgres = "Host=localhost;Port=5433;Username=meetjoe;Password=meetjoe_dev";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _server = Environment.GetEnvironmentVariable(PostgresVariable) ?? ComposePostgres;
    private readonly string _database = $"meetjoe_test_{Guid.NewGuid():N}";
    private readonly string _keysPath = Path.Combine(Path.GetTempPath(), $"meetjoe-test-keys-{Guid.NewGuid():N}");

    public InMemoryFileStorage Storage { get; } = new();
    public ManualTimeProvider Clock { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // UseSetting, not ConfigureAppConfiguration: Program reads these while it is still building.
        builder.UseSetting("ConnectionStrings:Postgres", $"{_server};Database={_database}");
        builder.UseSetting("ConnectionStrings:Redis", string.Empty);
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Auth:RequireHttps", "false");
        builder.UseSetting("Auth:KeysPath", _keysPath);
        builder.UseSetting("Auth:RateLimitPerMinute", "100000");
        builder.UseSetting("LiveKit:Url", "ws://media.test:7880");
        builder.UseSetting("LiveKit:ApiKey", "test_key");
        builder.UseSetting("LiveKit:ApiSecret", "test_secret_0123456789abcdef0123456789abcdef");

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IFileStorage>();
            services.AddSingleton<IFileStorage>(Storage);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }

    public Task InitializeAsync()
    {
        try
        {
            _ = Server; // Starts the host, which creates and migrates the database.
        }
        catch (Exception ex) when (ex is NpgsqlException || ex.InnerException is NpgsqlException or System.Net.Sockets.SocketException)
        {
            throw new InvalidOperationException(
                "The API tests need PostgreSQL. Start it with `docker compose -f deploy/docker-compose.yml up -d postgres`, " +
                $"or set {PostgresVariable}.", ex);
        }

        return Task.CompletedTask;
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();

        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection($"{_server};Database=postgres");
        await connection.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", connection);
        await drop.ExecuteNonQueryAsync();

        if (Directory.Exists(_keysPath)) Directory.Delete(_keysPath, recursive: true);
    }

    // ---- People -----------------------------------------------------------

    public const string Password = "correct horse battery";

    public async Task<TestUser> NewUserAsync(string displayName)
    {
        var email = $"{displayName.ToLowerInvariant().Replace(' ', '.')}-{Guid.NewGuid():N}@example.com";
        using var anonymous = CreateClient();
        var registered = await anonymous.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, Password, displayName));
        Assert.Equal(System.Net.HttpStatusCode.Created, registered.StatusCode);
        var summary = (await registered.Content.ReadFromJsonAsync<UserSummaryDto>(Json))!;

        var tokens = await RequestTokenAsync(anonymous, new()
        {
            ["grant_type"] = "password",
            ["client_id"] = AuthConstants.DesktopClientId,
            ["username"] = email,
            ["password"] = Password,
            ["scope"] = "offline_access",
        });
        var token = tokens.GetProperty("access_token").GetString()!;

        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return new TestUser(this, summary.Id, email, displayName, token, tokens.GetProperty("refresh_token").GetString()!, client);
    }

    public static async Task<JsonElement> RequestTokenAsync(HttpClient client, Dictionary<string, string> form)
    {
        var response = await client.PostAsync(AuthConstants.TokenEndpoint, new FormUrlEncodedContent(form));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(response.IsSuccessStatusCode, $"Token request failed: {body}");
        return body;
    }

    /// <summary>An organization with these people in it. The first is the owner.</summary>
    public async Task<(OrganizationDto Organization, TestUser[] People)> NewOrganizationAsync(params string[] names)
    {
        var people = new TestUser[names.Length];
        for (var i = 0; i < names.Length; i++) people[i] = await NewUserAsync(names[i]);

        var organization = await people[0].PostAsync<OrganizationDto>("/api/orgs", new CreateOrganizationRequest($"Org {Guid.NewGuid():N}"));
        foreach (var person in people.Skip(1))
            await person.PostAsync<OrganizationDto>("/api/orgs/join", new JoinOrganizationRequest(organization.JoinCode!));
        return (organization, people);
    }

    public HubConnection NewHub(string path, string? accessToken)
    {
        return new HubConnectionBuilder()
            .WithUrl(new Uri(Server.BaseAddress, path), options =>
            {
                // The in-memory test server has no sockets, so use long polling over its handler.
                options.HttpMessageHandlerFactory = _ => Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                if (accessToken is not null) options.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
            })
            .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}

/// <summary>A signed-in person and the requests they can make.</summary>
public sealed class TestUser(
    ApiFactory factory, Guid id, string email, string displayName, string accessToken, string refreshToken, HttpClient client)
{
    public Guid Id { get; } = id;
    public string Email { get; } = email;
    public string DisplayName { get; } = displayName;
    public string AccessToken { get; } = accessToken;
    public string RefreshToken { get; } = refreshToken;
    public HttpClient Client { get; } = client;

    public async Task<T> GetAsync<T>(string path)
    {
        var response = await Client.GetAsync(path);
        return await ReadAsync<T>(response);
    }

    public async Task<T> PostAsync<T>(string path, object? body = null)
    {
        var response = await Client.PostAsJsonAsync(path, body, ApiFactory.Json);
        return await ReadAsync<T>(response);
    }

    public Task<HttpResponseMessage> PostAsync(string path, object? body = null) =>
        Client.PostAsJsonAsync(path, body, ApiFactory.Json);

    public Task<HttpResponseMessage> PutAsync(string path, object body) =>
        Client.PutAsJsonAsync(path, body, ApiFactory.Json);

    public async Task<T> PutAsync<T>(string path, object body)
    {
        var response = await Client.PutAsJsonAsync(path, body, ApiFactory.Json);
        return await ReadAsync<T>(response);
    }

    public async Task<T> DeleteAsync<T>(string path)
    {
        var response = await Client.DeleteAsync(path);
        return await ReadAsync<T>(response);
    }

    public Task<MessageDto> SendAsync(Guid? conversationId, Guid? channelId, string? body, params Guid[] attachments) =>
        PostAsync<MessageDto>("/api/messages", new SendMessageRequest(conversationId, channelId, body, attachments));

    public async Task<HttpResponseMessage> UploadRawAsync(string path, string fileName, byte[] content)
    {
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(content);
        part.Headers.ContentType = new MediaTypeHeaderValue("text/html"); // Deliberately misleading.
        form.Add(part, "file", fileName);
        return await Client.PostAsync(path, form);
    }

    public async Task<FileDto> UploadAsync(string path, string fileName, byte[] content) =>
        await ReadAsync<FileDto>(await UploadRawAsync(path, fileName, content));

    public async Task<HubConnection> ConnectAsync(string hubPath)
    {
        var hub = factory.NewHub(hubPath, AccessToken);
        await hub.StartAsync();
        return hub;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            Assert.Fail($"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(ApiFactory.Json))!;
    }
}

internal static class ResponseAssert
{
    /// <summary>Asserts a problem-details failure and returns its human-readable detail.</summary>
    public static async Task<string> ProblemAsync(Task<HttpResponseMessage> request, int status, string code)
    {
        var response = await request;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True((int)response.StatusCode == status, $"Expected {status} but got {(int)response.StatusCode}: {text}");

        var problem = JsonSerializer.Deserialize<ApiProblem>(text, ApiFactory.Json)!;
        Assert.Equal(code, problem.Code);
        Assert.False(string.IsNullOrWhiteSpace(problem.Detail));
        return problem.Detail!;
    }
}

/// <summary>Collects what a hub pushes to one client so a test can wait for it.</summary>
public sealed class Inbox<T>
{
    private readonly ConcurrentQueue<T> _items = new();
    private readonly ConcurrentQueue<T> _everything = new();
    private readonly SemaphoreSlim _signal = new(0);

    /// <summary>Everything received so far, including items already returned by <see cref="NextAsync"/>.</summary>
    public IReadOnlyCollection<T> Everything => _everything.ToList();

    public void Add(T item)
    {
        _everything.Enqueue(item);
        _items.Enqueue(item);
        _signal.Release();
    }

    public async Task<T> NextAsync(Func<T, bool>? match = null, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero || !await _signal.WaitAsync(remaining))
                throw new TimeoutException($"No matching {typeof(T).Name} arrived within {timeoutMs} ms.");

            if (_items.TryDequeue(out var item) && (match is null || match(item))) return item;
        }
    }

    /// <summary>True if nothing arrives for a short while.</summary>
    public async Task<bool> StaysEmptyAsync(int forMs = 400) => !await _signal.WaitAsync(forMs);
}

public sealed class InMemoryFileStorage : IFileStorage
{
    private readonly ConcurrentDictionary<string, byte[]> _objects = new();

    public IReadOnlyCollection<string> Keys => _objects.Keys.ToList();

    public async Task PutAsync(string key, Stream content, long size, string contentType, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        _objects[key] = buffer.ToArray();
    }

    public async Task CopyToAsync(string key, Stream destination, CancellationToken ct = default) =>
        await destination.WriteAsync(_objects[key], ct);

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        _objects.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}

/// <summary>Controllable clock for tests that need ring timeouts and similar deadlines.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow = new(2026, 11, 24, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan by) => _utcNow += by;
}
