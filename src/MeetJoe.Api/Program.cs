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
using MeetJoe.Infrastructure;
using MeetJoe.Infrastructure.Persistence;
using MeetJoe.Infrastructure.Presence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddMeetJoeAuth(builder.Configuration, builder.Environment);

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

// Room for the largest allowed upload plus multipart framing.
builder.WebHost.ConfigureKestrel(kestrel =>
    kestrel.Limits.MaxRequestBodySize = FileObject.MaxSizeBytes + 1024 * 1024);

var app = builder.Build();

app.UseExceptionHandler();
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapAuthEndpoints();
app.MapApiEndpoints();
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
