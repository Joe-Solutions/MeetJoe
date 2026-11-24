namespace MeetJoe.Domain.Tests;

public class JoinCodeTests
{
    [Fact]
    public void Generated_codes_are_two_groups_of_five_unambiguous_characters()
    {
        for (var i = 0; i < 200; i++)
        {
            var code = JoinCodes.Generate();

            Assert.Matches("^[A-HJKMNP-Z2-9]{5}-[A-HJKMNP-Z2-9]{5}$", code);
            Assert.Equal(code, JoinCodes.Normalize(code));
        }
    }

    [Theory]
    [InlineData("k7mpq-2xhwd", "K7MPQ-2XHWD")]
    [InlineData("  K7MPQ2XHWD ", "K7MPQ-2XHWD")]
    [InlineData("k7m pq 2xh wd", "K7MPQ-2XHWD")]
    public void Typed_codes_are_normalized(string typed, string expected) =>
        Assert.Equal(expected, JoinCodes.Normalize(typed));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("K7MPQ-2XHW")]     // too short
    [InlineData("K7MPQ-2XHWDD")]   // too long
    [InlineData("K7MPQ-2XHW0")]    // zero is not in the alphabet
    [InlineData("K7MPQ-2XHWI")]    // nor is I
    public void Anything_else_is_not_a_code(string? typed) =>
        Assert.Null(JoinCodes.Normalize(typed));
}

public class OrganizationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_creator_becomes_the_owner()
    {
        var ownerId = Guid.NewGuid();

        var organization = Organization.Create("  Northwind  ", ownerId, Now);

        Assert.Equal("Northwind", organization.Name);
        var membership = Assert.Single(organization.Memberships);
        Assert.Equal(ownerId, membership.UserId);
        Assert.Equal(OrgRole.Owner, membership.Role);
        Assert.NotNull(JoinCodes.Normalize(organization.JoinCode));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_name_is_required(string name)
    {
        var error = Assert.Throws<DomainException>(() => Organization.Create(name, Guid.NewGuid(), Now));
        Assert.Equal("Organization name is required.", error.Message);
    }

    [Theory]
    [InlineData(OrgRole.Owner, true)]
    [InlineData(OrgRole.Admin, true)]
    [InlineData(OrgRole.Member, false)]
    [InlineData(OrgRole.Guest, false)]
    public void Only_owners_and_admins_manage(OrgRole role, bool expected) =>
        Assert.Equal(expected, new Membership(Guid.NewGuid(), Guid.NewGuid(), role, Now).CanManage);

    [Fact]
    public void A_member_can_leave_but_the_last_owner_cannot()
    {
        var owner = Guid.NewGuid();
        var member = Guid.NewGuid();
        var organization = Organization.Create("Northwind", owner, Now);
        organization.Memberships.Add(new Membership(organization.Id, member, OrgRole.Member, Now));

        organization.Leave(member);
        Assert.DoesNotContain(organization.Memberships, m => m.UserId == member);

        var error = Assert.Throws<DomainException>(() => organization.Leave(owner));
        Assert.Equal("Add another owner before leaving this organization.", error.Message);
    }
}

public class UserTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_email_is_also_the_sign_in_name()
    {
        var user = User.Create(" ana@example.com ", " Ana Reyes ", Now);

        Assert.Equal("ana@example.com", user.Email);
        Assert.Equal("ana@example.com", user.UserName);
        Assert.Equal("Ana Reyes", user.DisplayName);
        Assert.Equal(Availability.Available, user.Availability);
    }

    [Fact]
    public void A_blank_status_message_clears_it()
    {
        var user = User.Create("ana@example.com", "Ana", Now);
        user.UpdateProfile("Ana", "In a workshop");

        user.UpdateProfile("Ana R.", "   ");

        Assert.Equal("Ana R.", user.DisplayName);
        Assert.Null(user.StatusMessage);
    }

    [Fact]
    public void Display_names_and_status_messages_have_a_length_limit()
    {
        var user = User.Create("ana@example.com", "Ana", Now);

        Assert.Throws<DomainException>(() => user.UpdateProfile(new string('a', User.MaxDisplayNameLength + 1), null));
        Assert.Throws<DomainException>(() => user.UpdateProfile("Ana", new string('a', User.MaxStatusMessageLength + 1)));
        Assert.Equal("Ana", user.DisplayName);
    }
}

public class ConversationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Org = Guid.NewGuid();

    [Fact]
    public void A_pair_has_the_same_key_whoever_starts_the_chat()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        Assert.Equal(Conversation.DirectKeyFor(a, b), Conversation.DirectKeyFor(b, a));
        Assert.NotEqual(Conversation.DirectKeyFor(a, b), Conversation.DirectKeyFor(a, Guid.NewGuid()));
    }

    [Fact]
    public void A_direct_conversation_has_exactly_the_two_people()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var conversation = Conversation.CreateDirect(Org, a, b, Now);

        Assert.Equal(ConversationKind.Direct, conversation.Kind);
        Assert.Equal([a, b], conversation.Members.Select(m => m.UserId));
        Assert.Equal(Conversation.DirectKeyFor(a, b), conversation.DirectKey);
    }

    [Fact]
    public void Nobody_can_start_a_direct_conversation_with_themselves()
    {
        var a = Guid.NewGuid();
        Assert.Throws<DomainException>(() => Conversation.CreateDirect(Org, a, a, Now));
    }

    [Fact]
    public void A_group_needs_three_distinct_people()
    {
        var creator = Guid.NewGuid();
        var other = Guid.NewGuid();

        // The creator and a repeated person do not count twice.
        Assert.Throws<DomainException>(() => Conversation.CreateGroup(Org, creator, [other, other, creator], null, Now));

        var group = Conversation.CreateGroup(Org, creator, [other, Guid.NewGuid(), other], "  Launch  ", Now);
        Assert.Equal(3, group.Members.Count);
        Assert.Equal("Launch", group.Title);
        Assert.Null(group.DirectKey);
    }

    [Fact]
    public void Reading_never_moves_the_marker_backwards()
    {
        var member = new ConversationMember(Guid.NewGuid(), Guid.NewGuid(), Now);

        member.MarkRead(Now.AddMinutes(5));
        member.MarkRead(Now.AddMinutes(2));

        Assert.Equal(Now.AddMinutes(5), member.LastReadAt);
    }
}

public class MessageTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid Sender = Guid.NewGuid();

    private static Message InConversation(string? body, params Guid[] files) =>
        Message.Create(Org, Sender, Guid.NewGuid(), null, body, files, Now);

    [Fact]
    public void A_message_goes_to_a_conversation_or_a_channel_never_both_or_neither()
    {
        Assert.Throws<DomainException>(() => Message.Create(Org, Sender, null, null, "hi", [], Now));
        Assert.Throws<DomainException>(() => Message.Create(Org, Sender, Guid.NewGuid(), Guid.NewGuid(), "hi", [], Now));

        Assert.NotNull(Message.Create(Org, Sender, null, Guid.NewGuid(), "hi", [], Now).ChannelId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n\t ")]
    public void A_message_needs_text_or_an_attachment(string? body)
    {
        Assert.Throws<DomainException>(() => InConversation(body));

        Assert.Equal(string.Empty, InConversation(body, Guid.NewGuid()).Body);
    }

    [Fact]
    public void Text_is_trimmed_and_limited()
    {
        Assert.Equal("hello", InConversation("  hello \n").Body);
        Assert.Equal(Message.MaxBodyLength, InConversation(new string('a', Message.MaxBodyLength)).Body.Length);
        Assert.Throws<DomainException>(() => InConversation(new string('a', Message.MaxBodyLength + 1)));
    }

    [Fact]
    public void Attachments_are_limited_and_not_repeated()
    {
        var file = Guid.NewGuid();
        Assert.Single(InConversation("see attached", file, file).Attachments);

        var tooMany = Enumerable.Range(0, Message.MaxAttachments + 1).Select(_ => Guid.NewGuid()).ToArray();
        Assert.Throws<DomainException>(() => InConversation("too many", tooMany));
    }

    [Fact]
    public void The_preview_is_one_line_and_short()
    {
        Assert.Equal("first line second line", InConversation("first line\n\n  second   line").Preview());

        var longPreview = InConversation(new string('a', 500)).Preview();
        Assert.Equal(Message.MaxPreviewLength, longPreview.Length);
        Assert.EndsWith("…", longPreview);
    }

    [Fact]
    public void A_message_with_only_files_previews_as_attachments()
    {
        Assert.Equal("Sent an attachment", InConversation(null, Guid.NewGuid()).Preview());
        Assert.Equal("Sent 2 attachments", InConversation(null, Guid.NewGuid(), Guid.NewGuid()).Preview());
    }

    [Fact]
    public void Recording_a_message_updates_the_conversation_summary()
    {
        var conversation = Conversation.CreateDirect(Org, Sender, Guid.NewGuid(), Now);
        var message = Message.Create(Org, Sender, conversation.Id, null, "On my way", [], Now.AddMinutes(1));

        conversation.RecordMessage(message);

        Assert.Equal("On my way", conversation.LastMessagePreview);
        Assert.Equal(Now.AddMinutes(1), conversation.LastMessageAt);
    }

    [Fact]
    public void An_author_can_edit_and_soft_delete_a_message()
    {
        var message = InConversation("hello");
        message.Edit("hello there", Now.AddMinutes(1));
        Assert.Equal("hello there", message.Body);
        Assert.Equal(Now.AddMinutes(1), message.EditedAt);

        message.SoftDelete(Now.AddMinutes(2));
        Assert.True(message.IsDeleted);
        Assert.Equal(string.Empty, message.Body);
        Assert.Equal("Message deleted", message.Preview());
        Assert.Throws<DomainException>(() => message.Edit("nope", Now.AddMinutes(3)));
    }

    [Fact]
    public void A_reply_keeps_the_parent_id()
    {
        var parentId = Guid.NewGuid();
        var reply = Message.Create(Org, Sender, Guid.NewGuid(), null, "re", [], Now, parentId);
        Assert.Equal(parentId, reply.ParentMessageId);
    }
}

public class MessageReactionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Emoji_is_required_and_cannot_contain_spaces()
    {
        var reaction = MessageReaction.Create(Guid.NewGuid(), Guid.NewGuid(), "👍", Now);
        Assert.Equal("👍", reaction.Emoji);
        Assert.Throws<DomainException>(() => MessageReaction.Create(Guid.NewGuid(), Guid.NewGuid(), "  ", Now));
        Assert.Throws<DomainException>(() => MessageReaction.Create(Guid.NewGuid(), Guid.NewGuid(), "thumbs up", Now));
    }
}

public class ActivityItemTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Activity_is_not_created_for_the_actor()
    {
        var user = Guid.NewGuid();
        Assert.Throws<DomainException>(() => ActivityItem.Create(
            Guid.NewGuid(), user, user, ActivityKind.Reply, Guid.NewGuid(), Guid.NewGuid(), null, "x", Now));
    }

    [Fact]
    public void Marking_read_is_idempotent()
    {
        var item = ActivityItem.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ActivityKind.Mention,
            Guid.NewGuid(), Guid.NewGuid(), null, "Ana mentioned you", Now);
        Assert.True(item.IsUnread);
        item.MarkRead(Now.AddMinutes(1));
        item.MarkRead(Now.AddMinutes(2));
        Assert.Equal(Now.AddMinutes(1), item.ReadAt);
    }
}

public class CallTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Starting_a_call_rings_everyone_else()
    {
        var ana = Guid.NewGuid();
        var ben = Guid.NewGuid();
        var call = Call.Start(Guid.NewGuid(), Guid.NewGuid(), ana, [ben], Now);

        Assert.Equal(CallKind.Direct, call.Kind);
        Assert.Equal(CallParticipantStatus.Joined, call.Participants.Single(p => p.UserId == ana).Status);
        Assert.Equal(CallParticipantStatus.Ringing, call.Participants.Single(p => p.UserId == ben).Status);
    }

    [Fact]
    public void Ending_marks_unanswered_participants_as_missed()
    {
        var ana = Guid.NewGuid();
        var ben = Guid.NewGuid();
        var call = Call.Start(Guid.NewGuid(), Guid.NewGuid(), ana, [ben], Now);

        call.End(Now.AddSeconds(30));

        Assert.False(call.IsActive);
        Assert.Equal(CallParticipantStatus.Missed, call.Participants.Single(p => p.UserId == ben).Status);
    }

    [Fact]
    public void An_unanswered_ring_expires_after_thirty_seconds()
    {
        var ana = Guid.NewGuid();
        var ben = Guid.NewGuid();
        var call = Call.Start(Guid.NewGuid(), Guid.NewGuid(), ana, [ben], Now);

        Assert.False(call.IsUnansweredRingExpired(Now.AddSeconds(29)));
        Assert.True(call.IsUnansweredRingExpired(Now.AddSeconds(30)));

        call.MarkConnected(Now.AddSeconds(10));
        Assert.False(call.IsUnansweredRingExpired(Now.AddSeconds(60)));
    }
}

public class FileObjectTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData(@"C:\Users\ana\Desktop\notes.txt", "notes.txt")]
    [InlineData("..", "file")]
    [InlineData("", "file")]
    [InlineData(null, "file")]
    [InlineData("  spaced name.txt  ", "spaced name.txt")]
    [InlineData("line\r\nbreak.txt", "linebreak.txt")]
    [InlineData(".hidden", "hidden")]
    public void File_names_lose_their_directories_and_control_characters(string? supplied, string expected) =>
        Assert.Equal(expected, FileObject.SanitizeFileName(supplied));

    [Fact]
    public void Very_long_names_are_cut_to_the_limit() =>
        Assert.Equal(FileObject.MaxFileNameLength, FileObject.SanitizeFileName(new string('a', 400)).Length);

    [Fact]
    public void The_storage_key_never_contains_the_file_name()
    {
        var org = Guid.NewGuid();

        var file = FileObject.Create(org, Guid.NewGuid(), Guid.NewGuid(), null, "../../secrets.txt", null, 10, Now);

        Assert.Equal($"{org:N}/{file.Id:N}", file.StorageKey);
        Assert.Equal("secrets.txt", file.FileName);
        Assert.Equal("application/octet-stream", file.ContentType);
    }

    [Fact]
    public void Empty_and_oversized_files_are_refused()
    {
        var org = Guid.NewGuid();
        var uploader = Guid.NewGuid();
        var conversation = Guid.NewGuid();

        Assert.Throws<DomainException>(() => FileObject.Create(org, uploader, conversation, null, "a.txt", null, 0, Now));
        Assert.Throws<DomainException>(() =>
            FileObject.Create(org, uploader, conversation, null, "a.txt", null, FileObject.MaxSizeBytes + 1, Now));
        Assert.NotNull(FileObject.Create(org, uploader, conversation, null, "a.txt", null, FileObject.MaxSizeBytes, Now));
    }

    [Fact]
    public void A_file_is_shared_in_exactly_one_place()
    {
        Assert.Throws<DomainException>(() =>
            FileObject.Create(Guid.NewGuid(), Guid.NewGuid(), null, null, "a.txt", null, 1, Now));
        Assert.Throws<DomainException>(() =>
            FileObject.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "a.txt", null, 1, Now));
        Assert.Throws<DomainException>(() =>
            FileObject.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "a.txt", null, 1, Now,
                meetingId: Guid.NewGuid()));
        Assert.NotNull(FileObject.Create(
            Guid.NewGuid(), Guid.NewGuid(), null, null, "a.txt", null, 1, Now, meetingId: Guid.NewGuid()));
    }
}

public class WorkspaceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_new_workspace_has_its_creator_as_owner_and_a_General_channel()
    {
        var creator = Guid.NewGuid();

        var workspace = Workspace.Create(Guid.NewGuid(), creator, " Product ", "  ", Now);

        Assert.Equal("Product", workspace.Name);
        Assert.Null(workspace.Description);
        var member = Assert.Single(workspace.Members);
        Assert.Equal((creator, WorkspaceRole.Owner), (member.UserId, member.Role));
        Assert.Equal("General", Assert.Single(workspace.Channels).Name);
    }

    [Fact]
    public void Channel_names_are_compared_without_case_or_padding()
    {
        var workspace = Guid.NewGuid();

        var first = Channel.Create(workspace, "Design Reviews", null, Now);
        var second = Channel.Create(workspace, "  design reviews ", null, Now);

        Assert.Equal(first.NormalizedName, second.NormalizedName);
        Assert.Equal("Design Reviews", first.Name);
    }

    [Fact]
    public void A_channel_needs_a_name_within_the_limit()
    {
        Assert.Throws<DomainException>(() => Channel.Create(Guid.NewGuid(), " ", null, Now));
        Assert.Throws<DomainException>(() =>
            Channel.Create(Guid.NewGuid(), new string('a', Channel.MaxNameLength + 1), null, Now));
    }

    [Fact]
    public void A_private_channel_starts_with_its_creator_as_owner()
    {
        var workspace = Guid.NewGuid();
        var creator = Guid.NewGuid();
        var other = Guid.NewGuid();

        var channel = Channel.CreatePrivate(workspace, creator, "Secret", null, [other], Now);

        Assert.True(channel.IsPrivate);
        Assert.Equal(2, channel.Members.Count);
        Assert.Equal(ChannelRole.Owner, channel.Members.Single(m => m.UserId == creator).Role);
        Assert.Equal(ChannelRole.Member, channel.Members.Single(m => m.UserId == other).Role);
        channel.AddMember(Guid.NewGuid(), ChannelRole.Member, Now.AddMinutes(1));
        Assert.Equal(3, channel.Members.Count);
    }
}

public class MeetingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Each_meeting_gets_its_own_room()
    {
        var first = Meeting.Create(Guid.NewGuid(), Guid.NewGuid(), "Standup", Now);
        var second = Meeting.Create(Guid.NewGuid(), Guid.NewGuid(), "Standup", Now);

        Assert.StartsWith("mj-", first.RoomName);
        Assert.NotEqual(first.RoomName, second.RoomName);
        Assert.True(first.IsActive);
    }

    [Fact]
    public void Ending_a_meeting_marks_everyone_still_present_as_gone()
    {
        var meeting = Meeting.Create(Guid.NewGuid(), Guid.NewGuid(), "Standup", Now);
        var early = new MeetingParticipant(meeting.Id, Guid.NewGuid(), Now);
        var stayed = new MeetingParticipant(meeting.Id, Guid.NewGuid(), Now);
        meeting.Participants.AddRange([early, stayed]);
        early.Leave(Now.AddMinutes(10));

        meeting.End(Now.AddMinutes(30));

        Assert.False(meeting.IsActive);
        Assert.Equal(Now.AddMinutes(10), early.LeftAt);
        Assert.Equal(Now.AddMinutes(30), stayed.LeftAt);
    }

    [Fact]
    public void Ending_twice_keeps_the_first_end_time()
    {
        var meeting = Meeting.Create(Guid.NewGuid(), Guid.NewGuid(), "Standup", Now);

        meeting.End(Now.AddMinutes(30));
        meeting.End(Now.AddMinutes(45));

        Assert.Equal(Now.AddMinutes(30), meeting.EndedAt);
    }

    [Fact]
    public void Scheduling_adds_invites_and_expands_recurrence()
    {
        var host = Guid.NewGuid();
        var invitee = Guid.NewGuid();
        var starts = Now.AddDays(1);

        var meeting = Meeting.Schedule(
            Guid.NewGuid(), host, "Weekly", "Notes", starts, starts.AddHours(1), true, [invitee], Now,
            RecurrenceKind.Weekly, starts.AddDays(30));

        Assert.True(meeting.IsScheduled);
        Assert.True(meeting.LobbyEnabled);
        Assert.Equal(invitee, Assert.Single(meeting.Invites).UserId);
        Assert.NotNull(meeting.SeriesId);
        Assert.Equal(4, meeting.NextOccurrences(4).Count());
        Assert.Equal(starts.AddDays(7), meeting.NextOccurrences(1).Single());
    }

    [Fact]
    public void Live_meetings_can_invite_people_who_are_not_already_in()
    {
        var host = Guid.NewGuid();
        var guest = Guid.NewGuid();
        var already = Guid.NewGuid();
        var meeting = Meeting.Create(Guid.NewGuid(), host, "Standup", Now);
        meeting.Invites.Add(new MeetingInvite(meeting.Id, already, Now));

        var added = meeting.InvitePeople([guest, already, host], Now.AddMinutes(1));

        Assert.Equal(guest, Assert.Single(added).UserId);
        Assert.Equal(2, meeting.Invites.Count);
    }

    [Fact]
    public void A_recording_starts_active_and_can_finish_or_fail()
    {
        var meetingId = Guid.NewGuid();
        var host = Guid.NewGuid();
        var recording = MeetingRecording.Start(meetingId, host, Now);

        Assert.Equal(RecordingStatus.Recording, recording.Status);
        Assert.True(recording.IsInProgress);
        Assert.Null(recording.EndedAt);

        recording.MarkReady("org/file", "standup.webm", Now.AddMinutes(5));
        Assert.Equal(RecordingStatus.Ready, recording.Status);
        Assert.False(recording.IsInProgress);
        Assert.Equal("standup.webm", recording.FileName);
        Assert.Equal(Now.AddMinutes(5), recording.EndedAt);

        var failed = MeetingRecording.Start(meetingId, host, Now);
        failed.MarkFailed(Now.AddMinutes(1));
        Assert.Equal(RecordingStatus.Failed, failed.Status);
        Assert.False(failed.IsInProgress);
    }
}
