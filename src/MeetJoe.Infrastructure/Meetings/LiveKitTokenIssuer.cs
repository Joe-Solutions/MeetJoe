using Livekit.Server.Sdk.Dotnet;
using MeetJoe.Application.Common;
using Microsoft.Extensions.Options;

namespace MeetJoe.Infrastructure.Meetings;

public sealed class LiveKitOptions
{
    public const string Section = "LiveKit";

    /// <summary>WebSocket URL clients connect to, e.g. <c>ws://localhost:7880</c>.</summary>
    public string Url { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;
    public string ApiSecret { get; set; } = string.Empty;

    /// <summary>How long a join token can be used to enter the room.</summary>
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromHours(2);
}

/// <summary>Signs LiveKit access tokens that let one user join one room.</summary>
public sealed class LiveKitTokenIssuer(IOptions<LiveKitOptions> options) : IMeetingTokenIssuer
{
    public MeetingConnection Issue(
        string roomName, Guid userId, string displayName, bool canPublish = true, bool roomAdmin = false)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.Url) || string.IsNullOrWhiteSpace(settings.ApiKey) ||
            string.IsNullOrWhiteSpace(settings.ApiSecret))
            throw AppException.Unavailable("Meetings are not configured on this server.");

        var token = new AccessToken(settings.ApiKey, settings.ApiSecret)
            .WithIdentity(userId.ToString())
            .WithName(displayName)
            .WithGrants(new VideoGrants
            {
                RoomJoin = true,
                Room = roomName,
                CanPublish = canPublish,
                CanSubscribe = true,
                CanPublishData = true,
                RoomAdmin = roomAdmin,
            })
            .WithTtl(settings.TokenLifetime);

        return new MeetingConnection(settings.Url, token.ToJwt());
    }
}
