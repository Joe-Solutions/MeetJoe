using System.Security.Claims;
using MeetJoe.Api.Common;
using MeetJoe.Application.Activity;
using MeetJoe.Application.Calls;
using MeetJoe.Application.Chat;
using MeetJoe.Application.Common;
using MeetJoe.Application.Files;
using MeetJoe.Application.Meetings;
using MeetJoe.Application.Organizations;
using MeetJoe.Application.Presence;
using MeetJoe.Application.Profiles;
using MeetJoe.Application.Search;
using MeetJoe.Application.Workspaces;
using MeetJoe.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace MeetJoe.Api.Endpoints;

/// <summary>
/// The REST surface, one section per feature. Handlers only translate HTTP to application calls;
/// access rules live in the application services.
/// </summary>
internal static class ApiEndpoints
{
    public static void MapApiEndpoints(this IEndpointRouteBuilder app)
    {
        MapPublicGuestInvites(app);

        var api = app.MapGroup("/api").RequireAuthorization();

        MapProfile(api);
        MapOrganizations(api);
        MapChat(api);
        MapActivity(api);
        MapWorkspaces(api);
        MapFiles(api);
        MapMeetings(api);
        MapCalls(api);
        MapSearch(api);
        api.MapMusicEndpoints();
    }

    /// <summary>Unauthenticated join links from email / deep links.</summary>
    private static void MapPublicGuestInvites(IEndpointRouteBuilder app)
    {
        var pub = app.MapGroup("/api/public").AllowAnonymous();

        pub.MapGet("/meeting-invites/{token}", (string token, MeetingService meetings, CancellationToken ct) =>
            meetings.GetGuestInviteAsync(token, ct));

        pub.MapPost("/meeting-invites/{token}/join",
            (string token, GuestJoinRequest request, MeetingService meetings, CancellationToken ct) =>
                meetings.JoinAsGuestAsync(token, request, ct));

        pub.MapGet("/meeting-invites/{token}/session",
            (string token, MeetingService meetings, CancellationToken ct) =>
                meetings.GetGuestSessionAsync(token, ct));

        pub.MapPost("/meeting-invites/{token}/leave", async (string token, MeetingService meetings, CancellationToken ct) =>
        {
            await meetings.LeaveAsGuestAsync(token, ct);
            return Results.NoContent();
        });

        pub.MapGet("/meeting-invites/{token}/messages",
            (string token, DateTimeOffset? before, int? limit, MeetingService meetings, CancellationToken ct) =>
                meetings.GetGuestMeetingMessagesAsync(token, before, limit, ct));

        pub.MapPost("/meeting-invites/{token}/messages",
            (string token, GuestChatRequest request, MeetingService meetings, CancellationToken ct) =>
                meetings.SendGuestMeetingChatAsync(token, request, ct));
    }

    private static void MapProfile(RouteGroupBuilder api)
    {
        api.MapGet("/me", (ClaimsPrincipal user, ProfileService profiles, CancellationToken ct) =>
            profiles.GetAsync(user.GetUserId(), ct));

        api.MapPut("/me", (UpdateProfileRequest request, ClaimsPrincipal user, ProfileService profiles, CancellationToken ct) =>
            profiles.UpdateAsync(user.GetUserId(), request, ct));

        api.MapPut("/me/presence", (SetPresenceRequest request, ClaimsPrincipal user, PresenceService presence, CancellationToken ct) =>
            presence.SetAvailabilityAsync(user.GetUserId(), request.Availability, ct));

        api.MapPost("/me/avatar", async (HttpRequest http, ClaimsPrincipal user, ProfileService profiles, CancellationToken ct) =>
        {
            if (!http.HasFormContentType) throw AppException.Validation("Expected a multipart image upload.");
            var form = await http.ReadFormAsync(ct);
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault()
                       ?? throw AppException.Validation("Choose an image to upload.");
            await using var stream = file.OpenReadStream();
            return await profiles.UploadAvatarAsync(
                user.GetUserId(), file.FileName, file.ContentType, file.Length, stream, ct);
        }).DisableAntiforgery();

        api.MapDelete("/me/avatar", (ClaimsPrincipal user, ProfileService profiles, CancellationToken ct) =>
            profiles.ClearAvatarAsync(user.GetUserId(), ct));

        api.MapGet("/users/{id:guid}/avatar",
            async (Guid id, ClaimsPrincipal user, ProfileService profiles, IFileStorage storage, HttpContext http, CancellationToken ct) =>
            {
                var (contentType, key) = await profiles.GetAvatarAsync(user.GetUserId(), id, ct);
                var stream = new MemoryStream();
                await storage.CopyToAsync(key, stream, ct);
                stream.Position = 0;
                // Avatars are versioned via ?v= on the client URL; avoid intermediary caches of old bytes.
                http.Response.Headers.CacheControl = "private, no-cache";
                return Results.File(stream, contentType);
            });
    }

    private static void MapOrganizations(RouteGroupBuilder api)
    {
        api.MapGet("/orgs", (ClaimsPrincipal user, OrganizationService orgs, CancellationToken ct) =>
            orgs.GetMineAsync(user.GetUserId(), ct));

        api.MapPost("/orgs", (CreateOrganizationRequest request, ClaimsPrincipal user, OrganizationService orgs, CancellationToken ct) =>
            orgs.CreateAsync(user.GetUserId(), request, ct));

        api.MapPost("/orgs/join", (JoinOrganizationRequest request, ClaimsPrincipal user, OrganizationService orgs, CancellationToken ct) =>
            orgs.JoinAsync(user.GetUserId(), request, ct));

        api.MapGet("/orgs/{orgId:guid}/members", (Guid orgId, ClaimsPrincipal user, OrganizationService orgs, CancellationToken ct) =>
            orgs.GetMembersAsync(user.GetUserId(), orgId, ct));

        api.MapPost("/orgs/{orgId:guid}/leave", async (Guid orgId, ClaimsPrincipal user, OrganizationService orgs, CancellationToken ct) =>
        {
            await orgs.LeaveAsync(user.GetUserId(), orgId, ct);
            return Results.NoContent();
        });
    }

    private static void MapChat(RouteGroupBuilder api)
    {
        api.MapGet("/orgs/{orgId:guid}/conversations", (Guid orgId, ClaimsPrincipal user, ChatService chat, CancellationToken ct) =>
            chat.GetConversationsAsync(user.GetUserId(), orgId, ct));

        api.MapPost("/orgs/{orgId:guid}/conversations",
            (Guid orgId, CreateConversationRequest request, ClaimsPrincipal user, ChatService chat, CancellationToken ct) =>
                chat.CreateConversationAsync(user.GetUserId(), orgId, request, ct));

        api.MapGet("/conversations/{id:guid}/messages",
            (Guid id, DateTimeOffset? before, int? limit, ClaimsPrincipal user, ChatService chat, CancellationToken ct) =>
                chat.GetMessagesAsync(user.GetUserId(), id, null, before, limit, ct));

        api.MapPost("/conversations/{id:guid}/read", async (Guid id, ClaimsPrincipal user, ChatService chat, CancellationToken ct) =>
        {
            await chat.MarkReadAsync(user.GetUserId(), id, ct);
            return Results.NoContent();
        });

        api.MapPut("/conversations/{id:guid}/prefs",
            (Guid id, UpdateConversationPrefsRequest request, ClaimsPrincipal user, ChatService chat, CancellationToken ct) =>
                chat.UpdateConversationPrefsAsync(user.GetUserId(), id, request, ct));

        api.MapPost("/conversations/{id:guid}/members",
            (Guid id, AddConversationMemberRequest request, ClaimsPrincipal user, ChatService chat, CancellationToken ct) =>
                chat.AddMemberAsync(user.GetUserId(), id, request, ct));

        api.MapDelete("/conversations/{id:guid}/members/{userId:guid}",
            async (Guid id, Guid userId, ClaimsPrincipal user, ChatService chat, CancellationToken ct) =>
            {
                var updated = await chat.RemoveMemberAsync(user.GetUserId(), id, userId, ct);
                return updated is null ? Results.NoContent() : Results.Ok(updated);
            });

        api.MapPut("/conversations/{id:guid}/title",
            (Guid id, RenameConversationRequest request, ClaimsPrincipal user, ChatService chat, CancellationToken ct) =>
                chat.RenameAsync(user.GetUserId(), id, request, ct));

        api.MapGet("/channels/{id:guid}/messages",
            (Guid id, DateTimeOffset? before, int? limit, ClaimsPrincipal user, ChatService chat, CancellationToken ct) =>
                chat.GetMessagesAsync(user.GetUserId(), null, id, before, limit, ct));

        api.MapPost("/channels/{id:guid}/read", async (Guid id, ClaimsPrincipal user, ChatService chat, CancellationToken ct) =>
        {
            await chat.MarkChannelReadAsync(user.GetUserId(), id, ct);
            return Results.NoContent();
        });

        api.MapGet("/channels/{id:guid}/read-cursors", (Guid id, ClaimsPrincipal user, ChatService chat, CancellationToken ct) =>
            chat.GetChannelReadCursorsAsync(user.GetUserId(), id, ct));

        // Same operation as ChatHub.SendMessage, for clients without a realtime connection.
        api.MapPost("/messages", (SendMessageRequest request, ClaimsPrincipal user, ChatService chat, CancellationToken ct) =>
            chat.SendAsync(user.GetUserId(), request, ct));

        api.MapGet("/messages/{id:guid}/replies",
            (Guid id, ClaimsPrincipal user, ChatService chat, CancellationToken ct) =>
                chat.GetRepliesAsync(user.GetUserId(), id, ct));

        api.MapPut("/messages/{id:guid}",
            (Guid id, EditMessageRequest request, ClaimsPrincipal user, ChatService chat, CancellationToken ct) =>
                chat.EditAsync(user.GetUserId(), id, request, ct));

        api.MapDelete("/messages/{id:guid}",
            (Guid id, ClaimsPrincipal user, ChatService chat, CancellationToken ct) =>
                chat.DeleteAsync(user.GetUserId(), id, ct));

        api.MapPost("/messages/{id:guid}/reactions",
            (Guid id, ToggleReactionRequest request, ClaimsPrincipal user, ChatService chat, CancellationToken ct) =>
                chat.ToggleReactionAsync(user.GetUserId(), id, request, ct));
    }

    private static void MapActivity(RouteGroupBuilder api)
    {
        api.MapGet("/orgs/{orgId:guid}/activity",
            (Guid orgId, int? limit, ClaimsPrincipal user, ActivityService activity, CancellationToken ct) =>
                activity.GetAsync(user.GetUserId(), orgId, limit, ct));

        api.MapPost("/orgs/{orgId:guid}/activity/read",
            async (Guid orgId, ClaimsPrincipal user, ActivityService activity, CancellationToken ct) =>
            {
                await activity.MarkAllReadAsync(user.GetUserId(), orgId, ct);
                return Results.NoContent();
            });

        api.MapPost("/activity/{id:guid}/read",
            async (Guid id, ClaimsPrincipal user, ActivityService activity, CancellationToken ct) =>
            {
                await activity.MarkReadAsync(user.GetUserId(), id, ct);
                return Results.NoContent();
            });
    }

    private static void MapWorkspaces(RouteGroupBuilder api)
    {
        api.MapGet("/orgs/{orgId:guid}/workspaces", (Guid orgId, ClaimsPrincipal user, WorkspaceService workspaces, CancellationToken ct) =>
            workspaces.GetWorkspacesAsync(user.GetUserId(), orgId, ct));

        api.MapPost("/orgs/{orgId:guid}/workspaces",
            (Guid orgId, CreateWorkspaceRequest request, ClaimsPrincipal user, WorkspaceService workspaces, CancellationToken ct) =>
                workspaces.CreateAsync(user.GetUserId(), orgId, request, ct));

        api.MapGet("/workspaces/{id:guid}/members", (Guid id, ClaimsPrincipal user, WorkspaceService workspaces, CancellationToken ct) =>
            workspaces.GetMembersAsync(user.GetUserId(), id, ct));

        api.MapPost("/workspaces/{id:guid}/members",
            async (Guid id, AddWorkspaceMemberRequest request, ClaimsPrincipal user, WorkspaceService workspaces, CancellationToken ct) =>
            {
                await workspaces.AddMemberAsync(user.GetUserId(), id, request, ct);
                return Results.NoContent();
            });

        api.MapPost("/workspaces/{id:guid}/channels",
            (Guid id, CreateChannelRequest request, ClaimsPrincipal user, WorkspaceService workspaces, CancellationToken ct) =>
                workspaces.CreateChannelAsync(user.GetUserId(), id, request, ct));

        api.MapGet("/channels/{id:guid}/members",
            (Guid id, ClaimsPrincipal user, WorkspaceService workspaces, CancellationToken ct) =>
                workspaces.GetChannelMembersAsync(user.GetUserId(), id, ct));

        api.MapPost("/channels/{id:guid}/members",
            async (Guid id, AddChannelMemberRequest request, ClaimsPrincipal user, WorkspaceService workspaces, CancellationToken ct) =>
            {
                await workspaces.AddChannelMemberAsync(user.GetUserId(), id, request, ct);
                return Results.NoContent();
            });

        api.MapDelete("/channels/{id:guid}/members/{userId:guid}",
            async (Guid id, Guid userId, ClaimsPrincipal user, WorkspaceService workspaces, CancellationToken ct) =>
            {
                await workspaces.RemoveChannelMemberAsync(user.GetUserId(), id, userId, ct);
                return Results.NoContent();
            });

        api.MapPut("/channels/{id:guid}/prefs",
            (Guid id, UpdateChannelPrefsRequest request, ClaimsPrincipal user, WorkspaceService workspaces, CancellationToken ct) =>
                workspaces.UpdateChannelPrefsAsync(user.GetUserId(), id, request, ct));
    }

    private static void MapFiles(RouteGroupBuilder api)
    {
        api.MapGet("/orgs/{orgId:guid}/files", (Guid orgId, ClaimsPrincipal user, FileService files, CancellationToken ct) =>
            files.ListAsync(user.GetUserId(), orgId, ct));

        // Bearer-token API with no cookies, so there is no cross-site request to forge.
        api.MapPost("/conversations/{id:guid}/files",
                (Guid id, IFormFile file, ClaimsPrincipal user, FileService files, CancellationToken ct) =>
                    UploadAsync(id, null, file, user, files, ct))
            .DisableAntiforgery();

        api.MapPost("/channels/{id:guid}/files",
                (Guid id, IFormFile file, ClaimsPrincipal user, FileService files, CancellationToken ct) =>
                    UploadAsync(null, id, file, user, files, ct))
            .DisableAntiforgery();

        api.MapPost("/meetings/{id:guid}/files",
                (Guid id, IFormFile file, ClaimsPrincipal user, FileService files, CancellationToken ct) =>
                    UploadAsync(null, null, file, user, files, ct, id))
            .DisableAntiforgery();

        // Larger limit for client-side WebM recordings (meeting or call conversation).
        api.MapPost("/meetings/{id:guid}/recording/upload",
                (Guid id, IFormFile file, ClaimsPrincipal user, FileService files, CancellationToken ct) =>
                    UploadRecordingAsync(null, id, file, user, files, ct))
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(MeetJoe.Domain.FileObject.MaxRecordingSizeBytes + 1_048_576));

        api.MapPost("/conversations/{id:guid}/recording/upload",
                (Guid id, IFormFile file, ClaimsPrincipal user, FileService files, CancellationToken ct) =>
                    UploadRecordingAsync(id, null, file, user, files, ct))
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(MeetJoe.Domain.FileObject.MaxRecordingSizeBytes + 1_048_576));

        api.MapGet("/files/{id:guid}/content",
            async (Guid id, ClaimsPrincipal user, FileService files, HttpContext http, CancellationToken ct) =>
            {
                var file = await files.GetForDownloadAsync(user.GetUserId(), id, ct);

                // Uploaded content is untrusted: always a download, never rendered, never sniffed.
                http.Response.Headers.XContentTypeOptions = "nosniff";
                http.Response.ContentLength = file.Size;
                return Results.Stream(
                    body => files.CopyContentAsync(file, body, ct),
                    contentType: "application/octet-stream",
                    fileDownloadName: file.FileName);
            });
    }

    private static async Task<FileDto> UploadAsync(
        Guid? conversationId,
        Guid? channelId,
        IFormFile file,
        ClaimsPrincipal user,
        FileService files,
        CancellationToken ct,
        Guid? meetingId = null)
    {
        await using var content = file.OpenReadStream();
        return await files.UploadAsync(
            user.GetUserId(), conversationId, channelId, file.FileName, file.ContentType, file.Length, content, ct,
            meetingId);
    }

    private static async Task<FileDto> UploadRecordingAsync(
        Guid? conversationId,
        Guid? meetingId,
        IFormFile file,
        ClaimsPrincipal user,
        FileService files,
        CancellationToken ct)
    {
        await using var content = file.OpenReadStream();
        return await files.UploadRecordingAsync(
            user.GetUserId(), conversationId, meetingId, file.FileName, file.ContentType, file.Length, content, ct);
    }

    private static void MapMeetings(RouteGroupBuilder api)
    {
        api.MapGet("/orgs/{orgId:guid}/meetings", (Guid orgId, ClaimsPrincipal user, MeetingService meetings, CancellationToken ct) =>
            meetings.GetActiveAsync(user.GetUserId(), orgId, ct));

        api.MapGet("/orgs/{orgId:guid}/calendar",
            (Guid orgId, DateTimeOffset from, DateTimeOffset to, ClaimsPrincipal user, MeetingService meetings, CancellationToken ct) =>
                meetings.GetCalendarAsync(user.GetUserId(), orgId, from, to, ct));

        api.MapPost("/orgs/{orgId:guid}/meetings",
            (Guid orgId, CreateMeetingRequest request, ClaimsPrincipal user, MeetingService meetings, CancellationToken ct) =>
                meetings.CreateAsync(user.GetUserId(), orgId, request, ct));

        api.MapPost("/orgs/{orgId:guid}/meetings/schedule",
            (Guid orgId, ScheduleMeetingRequest request, ClaimsPrincipal user, MeetingService meetings, CancellationToken ct) =>
                meetings.ScheduleAsync(user.GetUserId(), orgId, request, ct));

        api.MapPost("/meetings/{id:guid}/join", (Guid id, ClaimsPrincipal user, MeetingService meetings, CancellationToken ct) =>
            meetings.JoinAsync(user.GetUserId(), id, ct));

        api.MapPost("/meetings/{id:guid}/leave", async (Guid id, ClaimsPrincipal user, MeetingService meetings, CancellationToken ct) =>
        {
            await meetings.LeaveAsync(user.GetUserId(), id, ct);
            return Results.NoContent();
        });

        api.MapPost("/meetings/{id:guid}/end", async (Guid id, ClaimsPrincipal user, MeetingService meetings, CancellationToken ct) =>
        {
            await meetings.EndAsync(user.GetUserId(), id, ct);
            return Results.NoContent();
        });

        api.MapPost("/meetings/{id:guid}/invite",
            (Guid id, RespondInviteRequest request, ClaimsPrincipal user, MeetingService meetings, CancellationToken ct) =>
                meetings.RespondToInviteAsync(user.GetUserId(), id, request, ct));

        api.MapPost("/meetings/{id:guid}/participants",
            (Guid id, InviteParticipantsRequest request, ClaimsPrincipal user, MeetingService meetings, CancellationToken ct) =>
                meetings.InviteParticipantsAsync(user.GetUserId(), id, request, ct));

        api.MapGet("/meetings/{id:guid}/messages",
            (Guid id, DateTimeOffset? before, int? limit, ClaimsPrincipal user, ChatService chat, CancellationToken ct) =>
                chat.GetMeetingMessagesAsync(user.GetUserId(), id, before, limit, ct));

        api.MapPost("/meetings/{id:guid}/messages/read", async (Guid id, ClaimsPrincipal user, MeetingService meetings, CancellationToken ct) =>
        {
            await meetings.MarkMeetingChatReadAsync(user.GetUserId(), id, ct);
            return Results.NoContent();
        });

        api.MapPost("/meetings/{id:guid}/messages",
            (Guid id, SendMessageRequest request, ClaimsPrincipal user, MeetingService meetings, CancellationToken ct) =>
                meetings.SendMeetingChatAsync(user.GetUserId(), id, request.Body, ct, request.AttachmentIds));

        api.MapPost("/meetings/{id:guid}/recording",
            (Guid id, ClaimsPrincipal user, MeetingService meetings, CancellationToken ct) =>
                meetings.StartRecordingAsync(user.GetUserId(), id, ct));

        api.MapPost("/meetings/{id:guid}/recording/complete",
            (Guid id, CompleteRecordingRequest request, ClaimsPrincipal user, MeetingService meetings, CancellationToken ct) =>
                meetings.CompleteRecordingAsync(user.GetUserId(), id, request, ct));

        api.MapPost("/meetings/{id:guid}/recording/cancel",
            (Guid id, ClaimsPrincipal user, MeetingService meetings, CancellationToken ct) =>
                meetings.CancelRecordingAsync(user.GetUserId(), id, ct));
    }

    private static void MapCalls(RouteGroupBuilder api)
    {
        api.MapGet("/orgs/{orgId:guid}/calls", (Guid orgId, ClaimsPrincipal user, CallService calls, CancellationToken ct) =>
            calls.GetHistoryAsync(user.GetUserId(), orgId, ct));

        api.MapPost("/orgs/{orgId:guid}/calls",
            (Guid orgId, StartCallRequest request, ClaimsPrincipal user, CallService calls, CancellationToken ct) =>
                calls.StartAsync(user.GetUserId(), orgId, request, ct));

        api.MapPost("/calls/{id:guid}/answer", (Guid id, ClaimsPrincipal user, CallService calls, CancellationToken ct) =>
            calls.AnswerAsync(user.GetUserId(), id, ct));

        api.MapPost("/calls/{id:guid}/decline", async (Guid id, ClaimsPrincipal user, CallService calls, CancellationToken ct) =>
        {
            await calls.DeclineAsync(user.GetUserId(), id, ct);
            return Results.NoContent();
        });

        api.MapPost("/calls/{id:guid}/hangup", async (Guid id, ClaimsPrincipal user, CallService calls, CancellationToken ct) =>
        {
            await calls.HangUpAsync(user.GetUserId(), id, ct);
            return Results.NoContent();
        });

        api.MapPost("/calls/{id:guid}/end", async (Guid id, ClaimsPrincipal user, CallService calls, CancellationToken ct) =>
        {
            await calls.EndAsync(user.GetUserId(), id, ct);
            return Results.NoContent();
        });

        api.MapPost("/calls/{id:guid}/invite",
            (Guid id, InviteToCallRequest request, ClaimsPrincipal user, CallService calls, CancellationToken ct) =>
                calls.InviteAsync(user.GetUserId(), id, request, ct));
    }

    private static void MapSearch(RouteGroupBuilder api)
    {
        api.MapGet("/orgs/{orgId:guid}/search",
            (Guid orgId, string? q, int? limit, ClaimsPrincipal user, SearchService search, CancellationToken ct) =>
                search.SearchAsync(user.GetUserId(), orgId, q, limit, ct));
    }
}
