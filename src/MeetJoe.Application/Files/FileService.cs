using MeetJoe.Application.Common;
using MeetJoe.Contracts;
using Microsoft.EntityFrameworkCore;
using Domain = MeetJoe.Domain;

namespace MeetJoe.Application.Files;

public sealed record FileDownload(string FileName, string ContentType, long Size, string StorageKey);

/// <summary>
/// Files are uploaded to a conversation, channel, or meeting first, then attached to a message.
/// Whoever can read that place can read the file.
/// </summary>
public sealed class FileService(IAppDbContext db, IFileStorage storage, TimeProvider time)
{
    public async Task<FileDto> UploadAsync(
        Guid userId,
        Guid? conversationId,
        Guid? channelId,
        string? fileName,
        string? contentType,
        long size,
        Stream content,
        CancellationToken ct = default,
        Guid? meetingId = null)
    {
        var target = await db.ResolveTargetAsync(userId, conversationId, channelId, ct, meetingId);
        var file = Domain.FileObject.Create(
            target.OrganizationId, userId, conversationId, channelId, fileName, contentType, size, time.Now(),
            meetingId);

        await storage.PutAsync(file.StorageKey, content, size, file.ContentType, ct);
        db.Files.Add(file);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            // Do not leave an object nobody can reach. Uploads that are saved but never attached
            // to a message are not cleaned up yet.
            await storage.DeleteAsync(file.StorageKey, CancellationToken.None);
            throw;
        }

        var uploaderName = await db.Users.Where(u => u.Id == userId).Select(u => u.DisplayName).FirstAsync(ct);
        var location = meetingId is not null ? "Meeting" : string.Empty;
        return new FileDto(
            file.Id, file.FileName, file.ContentType, file.Size, file.CreatedAt,
            new UserSummaryDto(userId, uploaderName), conversationId, channelId, location, meetingId);
    }

    /// <summary>Checks access and returns what is needed to stream the file.</summary>
    public async Task<FileDownload> GetForDownloadAsync(Guid userId, Guid fileId, CancellationToken ct = default)
    {
        var file = await db.Files.AsNoTracking().FirstOrDefaultAsync(f => f.Id == fileId, ct)
                   ?? throw AppException.NotFound("File");
        try
        {
            await db.ResolveTargetAsync(userId, file.ConversationId, file.ChannelId, ct, file.MeetingId);
        }
        catch (AppException)
        {
            throw AppException.NotFound("File");
        }

        return new FileDownload(file.FileName, file.ContentType, file.Size, file.StorageKey);
    }

    public Task CopyContentAsync(FileDownload file, Stream destination, CancellationToken ct = default) =>
        storage.CopyToAsync(file.StorageKey, destination, ct);

    /// <summary>Every file shared in a conversation or channel the user belongs to, newest first.</summary>
    public async Task<IReadOnlyList<FileDto>> ListAsync(Guid userId, Guid organizationId, CancellationToken ct = default)
    {
        await db.RequireMembershipAsync(userId, organizationId, ct);

        var rows = await db.Files.AsNoTracking()
            .Where(f => f.OrganizationId == organizationId
                        && db.Attachments.Any(a => a.FileObjectId == f.Id)
                        && ((f.ConversationId != null
                             && db.ConversationMembers.Any(m => m.ConversationId == f.ConversationId && m.UserId == userId))
                            || (f.ChannelId != null
                                && db.Channels.Any(c =>
                                    c.Id == f.ChannelId && c.Workspace.Members.Any(m => m.UserId == userId)))))
            .OrderByDescending(f => f.CreatedAt)
            .Take(500)
            .Select(f => new
            {
                f.Id,
                f.FileName,
                f.ContentType,
                f.Size,
                f.CreatedAt,
                f.UploaderId,
                UploaderName = f.Uploader.DisplayName,
                f.ConversationId,
                f.ChannelId,
                ChannelLocation = db.Channels
                    .Where(c => c.Id == f.ChannelId)
                    .Select(c => c.Workspace.Name + " / " + c.Name)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        return rows
            .Select(r => new FileDto(
                r.Id, r.FileName, r.ContentType, r.Size, r.CreatedAt,
                new UserSummaryDto(r.UploaderId, r.UploaderName), r.ConversationId, r.ChannelId,
                r.ChannelLocation ?? "Chat"))
            .ToList();
    }
}
