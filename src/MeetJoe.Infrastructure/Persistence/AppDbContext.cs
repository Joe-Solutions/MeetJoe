using MeetJoe.Application.Common;
using MeetJoe.Domain;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MeetJoe.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityUserContext<User, Guid>(options), IAppDbContext
{
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationMember> ConversationMembers => Set<ConversationMember>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<MessageReaction> MessageReactions => Set<MessageReaction>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<FileObject> Files => Set<FileObject>();
    public DbSet<ActivityItem> ActivityItems => Set<ActivityItem>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<WorkspaceMember> WorkspaceMembers => Set<WorkspaceMember>();
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<ChannelMember> ChannelMembers => Set<ChannelMember>();
    public DbSet<Meeting> Meetings => Set<Meeting>();
    public DbSet<MeetingParticipant> MeetingParticipants => Set<MeetingParticipant>();
    public DbSet<MeetingInvite> MeetingInvites => Set<MeetingInvite>();
    public DbSet<MeetingRecording> MeetingRecordings => Set<MeetingRecording>();
    public DbSet<Call> Calls => Set<Call>();
    public DbSet<CallParticipant> CallParticipants => Set<CallParticipant>();

    /// <summary>Provider setup shared by the running app and design-time tooling.</summary>
    public static void Configure(DbContextOptionsBuilder builder, string connectionString)
    {
        builder.UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName));
        builder.UseOpenIddict<Guid>();
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<User>(e =>
        {
            e.Property(x => x.DisplayName).HasMaxLength(User.MaxDisplayNameLength);
            e.Property(x => x.StatusMessage).HasMaxLength(User.MaxStatusMessageLength);
            e.Property(x => x.AvatarStorageKey).HasMaxLength(200);
            e.Property(x => x.AvatarContentType).HasMaxLength(User.MaxAvatarContentTypeLength);
        });

        builder.Entity<Organization>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(Organization.MaxNameLength);
            e.Property(x => x.JoinCode).HasMaxLength(16);
            e.HasIndex(x => x.JoinCode).IsUnique();
        });

        builder.Entity<Membership>(e =>
        {
            e.HasKey(x => new { x.OrganizationId, x.UserId });
            e.HasOne(x => x.Organization).WithMany(o => o.Memberships).HasForeignKey(x => x.OrganizationId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
            e.HasIndex(x => x.UserId);
        });

        builder.Entity<Conversation>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(Conversation.MaxTitleLength);
            e.Property(x => x.DirectKey).HasMaxLength(80);
            e.Property(x => x.LastMessagePreview).HasMaxLength(Message.MaxPreviewLength);
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedById).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => new { x.OrganizationId, x.DirectKey }).IsUnique();
        });

        builder.Entity<ConversationMember>(e =>
        {
            e.HasKey(x => new { x.ConversationId, x.UserId });
            e.HasOne(x => x.Conversation).WithMany(c => c.Members).HasForeignKey(x => x.ConversationId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
            e.HasIndex(x => x.UserId);
        });

        builder.Entity<Message>(e =>
        {
            e.Property(x => x.Body).HasMaxLength(Message.MaxBodyLength);
            e.Property(x => x.LinkPreviewUrl).HasMaxLength(2000);
            e.Property(x => x.LinkPreviewTitle).HasMaxLength(300);
            e.Property(x => x.LinkPreviewDescription).HasMaxLength(500);
            e.Ignore(x => x.IsDeleted);
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId);
            e.HasOne<Conversation>().WithMany(c => c.Messages).HasForeignKey(x => x.ConversationId);
            e.HasOne<Channel>().WithMany().HasForeignKey(x => x.ChannelId);
            e.HasOne<Meeting>().WithMany().HasForeignKey(x => x.MeetingId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentMessageId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Sender).WithMany().HasForeignKey(x => x.SenderId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.ConversationId, x.CreatedAt });
            e.HasIndex(x => new { x.ChannelId, x.CreatedAt });
            e.HasIndex(x => new { x.MeetingId, x.CreatedAt });
            e.HasIndex(x => x.ParentMessageId);
        });

        builder.Entity<MessageReaction>(e =>
        {
            e.HasKey(x => new { x.MessageId, x.UserId, x.Emoji });
            e.Property(x => x.Emoji).HasMaxLength(MessageReaction.MaxEmojiLength);
            e.HasOne(x => x.Message).WithMany(m => m.Reactions).HasForeignKey(x => x.MessageId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.UserId);
        });

        builder.Entity<Attachment>(e =>
        {
            e.HasOne<Message>().WithMany(m => m.Attachments).HasForeignKey(x => x.MessageId);
            e.HasOne(x => x.FileObject).WithMany().HasForeignKey(x => x.FileObjectId);
            e.HasIndex(x => x.FileObjectId).IsUnique();
        });

        builder.Entity<ActivityItem>(e =>
        {
            e.Property(x => x.Summary).HasMaxLength(ActivityItem.MaxSummaryLength);
            e.Ignore(x => x.IsUnread);
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId);
            e.HasOne(x => x.Recipient).WithMany().HasForeignKey(x => x.RecipientId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Actor).WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Message>().WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Meeting>().WithMany().HasForeignKey(x => x.MeetingId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Call>().WithMany().HasForeignKey(x => x.CallId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.RecipientId, x.CreatedAt });
            e.HasIndex(x => new { x.OrganizationId, x.RecipientId, x.CreatedAt });
        });

        builder.Entity<FileObject>(e =>
        {
            e.Property(x => x.FileName).HasMaxLength(FileObject.MaxFileNameLength);
            e.Property(x => x.ContentType).HasMaxLength(255);
            e.Property(x => x.StorageKey).HasMaxLength(100);
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId);
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId);
            e.HasOne<Channel>().WithMany().HasForeignKey(x => x.ChannelId);
            e.HasOne<Meeting>().WithMany().HasForeignKey(x => x.MeetingId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Uploader).WithMany().HasForeignKey(x => x.UploaderId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
            e.HasIndex(x => x.MeetingId);
        });

        builder.Entity<Workspace>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(Workspace.MaxNameLength);
            e.Property(x => x.Description).HasMaxLength(Workspace.MaxDescriptionLength);
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId);
            e.HasIndex(x => x.OrganizationId);
        });

        builder.Entity<WorkspaceMember>(e =>
        {
            e.HasKey(x => new { x.WorkspaceId, x.UserId });
            e.HasOne(x => x.Workspace).WithMany(w => w.Members).HasForeignKey(x => x.WorkspaceId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
            e.HasIndex(x => x.UserId);
        });

        builder.Entity<Channel>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(Channel.MaxNameLength);
            e.Property(x => x.NormalizedName).HasMaxLength(Channel.MaxNameLength);
            e.Property(x => x.Description).HasMaxLength(Channel.MaxDescriptionLength);
            e.Ignore(x => x.IsPrivate);
            e.HasOne(x => x.Workspace).WithMany(w => w.Channels).HasForeignKey(x => x.WorkspaceId);
            e.HasIndex(x => new { x.WorkspaceId, x.NormalizedName }).IsUnique();
        });

        builder.Entity<ChannelMember>(e =>
        {
            e.HasKey(x => new { x.ChannelId, x.UserId });
            e.HasOne(x => x.Channel).WithMany(c => c.Members).HasForeignKey(x => x.ChannelId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
            e.HasIndex(x => x.UserId);
        });

        builder.Entity<Meeting>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(Meeting.MaxTitleLength);
            e.Property(x => x.Description).HasMaxLength(Meeting.MaxDescriptionLength);
            e.Property(x => x.RoomName).HasMaxLength(64);
            e.Ignore(x => x.IsActive);
            e.Ignore(x => x.IsScheduled);
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId);
            e.HasOne(x => x.Host).WithMany().HasForeignKey(x => x.HostId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.OrganizationId, x.EndedAt });
            e.HasIndex(x => new { x.OrganizationId, x.StartsAt });
            e.HasIndex(x => x.RoomName).IsUnique();
            e.HasIndex(x => x.SeriesId);
        });

        builder.Entity<MeetingParticipant>(e =>
        {
            e.Ignore(x => x.IsPresent);
            e.HasOne<Meeting>().WithMany(m => m.Participants).HasForeignKey(x => x.MeetingId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
            e.HasIndex(x => x.MeetingId);
        });

        builder.Entity<MeetingInvite>(e =>
        {
            e.HasKey(x => new { x.MeetingId, x.UserId });
            e.HasOne<Meeting>().WithMany(m => m.Invites).HasForeignKey(x => x.MeetingId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
            e.HasIndex(x => x.UserId);
        });

        builder.Entity<MeetingRecording>(e =>
        {
            e.Property(x => x.StorageKey).HasMaxLength(100);
            e.Property(x => x.FileName).HasMaxLength(FileObject.MaxFileNameLength);
            e.HasOne<Meeting>().WithMany(m => m.Recordings).HasForeignKey(x => x.MeetingId);
            e.HasIndex(x => x.MeetingId);
        });

        builder.Entity<Call>(e =>
        {
            e.Property(x => x.RoomName).HasMaxLength(64);
            e.Ignore(x => x.IsActive);
            e.Ignore(x => x.IsConnected);
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId);
            e.HasOne(x => x.Conversation).WithMany().HasForeignKey(x => x.ConversationId);
            e.HasOne(x => x.Initiator).WithMany().HasForeignKey(x => x.InitiatorId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
            e.HasIndex(x => new { x.ConversationId, x.EndedAt });
            e.HasIndex(x => x.RoomName).IsUnique();
        });

        builder.Entity<CallParticipant>(e =>
        {
            e.HasKey(x => new { x.CallId, x.UserId });
            e.HasOne(x => x.Call).WithMany(c => c.Participants).HasForeignKey(x => x.CallId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
            e.HasIndex(x => x.UserId);
        });

        foreach (var entityType in builder.Model.GetEntityTypes().Where(t => t.ClrType.Assembly == typeof(User).Assembly))
        {
            var key = entityType.FindPrimaryKey();
            if (key is { Properties: [{ ClrType: var type } property] } && type == typeof(Guid))
                property.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
        }
    }
}

/// <summary>Lets <c>dotnet ef</c> create the context without starting the API host.</summary>
public sealed class AppDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public const string LocalConnectionString =
        "Host=localhost;Port=5433;Database=meetjoe;Username=meetjoe;Password=meetjoe_dev";

    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres") ?? LocalConnectionString;
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContext.Configure(builder, connectionString);
        return new AppDbContext(builder.Options);
    }
}
