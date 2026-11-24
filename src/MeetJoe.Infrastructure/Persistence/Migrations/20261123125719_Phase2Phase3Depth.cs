using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetJoe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase2Phase3Depth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LinkPreviewDescription",
                table: "Messages",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LinkPreviewTitle",
                table: "Messages",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LinkPreviewUrl",
                table: "Messages",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MeetingId",
                table: "Messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "Meetings",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EndsAt",
                table: "Meetings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LobbyEnabled",
                table: "Meetings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Recurrence",
                table: "Meetings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RecurrenceUntil",
                table: "Meetings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SeriesId",
                table: "Meetings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StartsAt",
                table: "Meetings",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            // Existing instant meetings used CreatedAt as the start; keep that for rows already stored.
            migrationBuilder.Sql("""UPDATE "Meetings" SET "StartsAt" = "CreatedAt";""");

            migrationBuilder.AddColumn<bool>(
                name: "HandRaised",
                table: "MeetingParticipants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "InLobby",
                table: "MeetingParticipants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsMuted",
                table: "ConversationMembers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsPinned",
                table: "ConversationMembers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "NotifyMode",
                table: "ConversationMembers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PinnedAt",
                table: "ConversationMembers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "MessageId",
                table: "ActivityItems",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "MeetingId",
                table: "ActivityItems",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ChannelMembers",
                columns: table => new
                {
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    JoinedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastReadAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsMuted = table.Column<bool>(type: "boolean", nullable: false),
                    NotifyMode = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelMembers", x => new { x.ChannelId, x.UserId });
                    table.ForeignKey(
                        name: "FK_ChannelMembers_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChannelMembers_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MeetingInvites",
                columns: table => new
                {
                    MeetingId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    InvitedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RespondedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeetingInvites", x => new { x.MeetingId, x.UserId });
                    table.ForeignKey(
                        name: "FK_MeetingInvites_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MeetingInvites_Meetings_MeetingId",
                        column: x => x.MeetingId,
                        principalTable: "Meetings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MeetingRecordings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MeetingId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedById = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeetingRecordings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MeetingRecordings_Meetings_MeetingId",
                        column: x => x.MeetingId,
                        principalTable: "Meetings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_MeetingId_CreatedAt",
                table: "Messages",
                columns: new[] { "MeetingId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Meetings_OrganizationId_StartsAt",
                table: "Meetings",
                columns: new[] { "OrganizationId", "StartsAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Meetings_SeriesId",
                table: "Meetings",
                column: "SeriesId");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityItems_MeetingId",
                table: "ActivityItems",
                column: "MeetingId");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelMembers_UserId",
                table: "ChannelMembers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_MeetingInvites_UserId",
                table: "MeetingInvites",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_MeetingRecordings_MeetingId",
                table: "MeetingRecordings",
                column: "MeetingId");

            migrationBuilder.AddForeignKey(
                name: "FK_ActivityItems_Meetings_MeetingId",
                table: "ActivityItems",
                column: "MeetingId",
                principalTable: "Meetings",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Messages_Meetings_MeetingId",
                table: "Messages",
                column: "MeetingId",
                principalTable: "Meetings",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ActivityItems_Meetings_MeetingId",
                table: "ActivityItems");

            migrationBuilder.DropForeignKey(
                name: "FK_Messages_Meetings_MeetingId",
                table: "Messages");

            migrationBuilder.DropTable(
                name: "ChannelMembers");

            migrationBuilder.DropTable(
                name: "MeetingInvites");

            migrationBuilder.DropTable(
                name: "MeetingRecordings");

            migrationBuilder.DropIndex(
                name: "IX_Messages_MeetingId_CreatedAt",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "IX_Meetings_OrganizationId_StartsAt",
                table: "Meetings");

            migrationBuilder.DropIndex(
                name: "IX_Meetings_SeriesId",
                table: "Meetings");

            migrationBuilder.DropIndex(
                name: "IX_ActivityItems_MeetingId",
                table: "ActivityItems");

            migrationBuilder.DropColumn(
                name: "LinkPreviewDescription",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "LinkPreviewTitle",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "LinkPreviewUrl",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "MeetingId",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "Meetings");

            migrationBuilder.DropColumn(
                name: "EndsAt",
                table: "Meetings");

            migrationBuilder.DropColumn(
                name: "LobbyEnabled",
                table: "Meetings");

            migrationBuilder.DropColumn(
                name: "Recurrence",
                table: "Meetings");

            migrationBuilder.DropColumn(
                name: "RecurrenceUntil",
                table: "Meetings");

            migrationBuilder.DropColumn(
                name: "SeriesId",
                table: "Meetings");

            migrationBuilder.DropColumn(
                name: "StartsAt",
                table: "Meetings");

            migrationBuilder.DropColumn(
                name: "HandRaised",
                table: "MeetingParticipants");

            migrationBuilder.DropColumn(
                name: "InLobby",
                table: "MeetingParticipants");

            migrationBuilder.DropColumn(
                name: "IsMuted",
                table: "ConversationMembers");

            migrationBuilder.DropColumn(
                name: "IsPinned",
                table: "ConversationMembers");

            migrationBuilder.DropColumn(
                name: "NotifyMode",
                table: "ConversationMembers");

            migrationBuilder.DropColumn(
                name: "PinnedAt",
                table: "ConversationMembers");

            migrationBuilder.DropColumn(
                name: "MeetingId",
                table: "ActivityItems");

            migrationBuilder.AlterColumn<Guid>(
                name: "MessageId",
                table: "ActivityItems",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
