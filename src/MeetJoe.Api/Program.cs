using System.Text.Json.Serialization;
using MeetJoe.Api.Auth;
using MeetJoe.Api.Common;
using MeetJoe.Api.Endpoints;
using MeetJoe.Api.Hosting;
using MeetJoe.Api.Realtime;
using MeetJoe.Application;
using MeetJoe.Application.Common;
using MeetJoe.Contracts;
using MeetJoe.Domain;
using MeetJoe.Api.Updates;
using MeetJoe.Infrastructure;
using MeetJoe.Infrastructure.Persistence;
using MeetJoe.Infrastructure.Presence;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

// Trust X-Forwarded-* from the reverse proxy (Caddy) so OpenIddict sees HTTPS.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddMeetJoeAuth(builder.Configuration, builder.Environment);
builder.Services.Configure<DesktopUpdatesOptions>(builder.Configuration.GetSection(DesktopUpdatesOptions.SectionName));
builder.Services.AddSingleton<IDesktopUpdateFeed, DesktopUpdateFeed>();

builder.Services.AddScoped<IRealtimePublisher, SignalRRealtimePublisher>();
builder.Services.AddSingleton<IUserIdProvider, SubjectUserIdProvider>();
builder.Services.AddHostedService<CallRingTimeoutWorker>();
builder.Services
    .AddSignalR(options => options.AddFilter<HubErrorFilter>())
    .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<AppExceptionHandler>();
builder.Services.AddCors(options =>
{
    options.AddPolicy("GuestJoin", policy =>
        policy.AllowAnyHeader().AllowAnyMethod().SetIsOriginAllowed(_ => true));
});

// Room for the largest allowed upload (recordings) plus multipart framing.
builder.WebHost.ConfigureKestrel(kestrel =>
    kestrel.Limits.MaxRequestBodySize = FileObject.MaxRecordingSizeBytes + 1024 * 1024);

var app = builder.Build();

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseRateLimiter();
app.UseCors("GuestJoin");
app.UseDefaultFiles();
app.UseStaticFiles();

// Velopack release packages (semver nupkgs + releases.*.json) per RID.
var updateOpts = app.Configuration.GetSection(DesktopUpdatesOptions.SectionName).Get<DesktopUpdatesOptions>()
    ?? new DesktopUpdatesOptions();
var updateRoot = Path.IsPathRooted(updateOpts.RootPath)
    ? updateOpts.RootPath
    : Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, updateOpts.RootPath));
Directory.CreateDirectory(updateRoot);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(updateRoot),
    RequestPath = updateOpts.UrlPath.TrimEnd('/'),
    ServeUnknownFileTypes = true,
});

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapAuthEndpoints();
app.MapApiEndpoints();
app.MapDesktopUpdateEndpoints();
app.MapHub<ChatHub>(HubPaths.Chat);
app.MapHub<PresenceHub>(HubPaths.Presence);
app.MapHub<NotificationHub>(HubPaths.Notifications);
app.MapHub<MeetingHub>(HubPaths.Meeting);
app.MapHub<CallHub>(HubPaths.Call);

await InitializeAsync(app);
await app.RunAsync();

static async Task InitializeAsync(WebApplication app)
{
    await using var scope = app.Services.CreateAsyncScope();
    var services = scope.ServiceProvider;
    var logger = services.GetRequiredService<ILogger<Program>>();

    if (app.Configuration.GetValue("Database:MigrateOnStartup", false))
        await services.GetRequiredService<AppDbContext>().Database.MigrateAsync();

    await AuthSetup.SeedClientsAsync(services);

    // Connections recorded by a previous run of this process no longer exist.
    if (services.GetService<RedisPresenceStore>() is { } presence)
    {
        try
        {
            await presence.ResetAsync();
        }
        catch (StackExchange.Redis.RedisException ex)
        {
            logger.LogWarning(ex, "Could not reset presence state in Redis; presence may be stale until it is reachable.");
        }
    }
}

public partial class Program;
