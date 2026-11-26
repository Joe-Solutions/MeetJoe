using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetJoe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MeetingGuestInvites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InviteToken",
                table: "MeetingInvites",
                type: "character varying(43)",
                maxLength: 43,
                nullable: true);

            // Unique opaque tokens for existing member invites (before unique index).
            migrationBuilder.Sql(
                """
                UPDATE "MeetingInvites"
                SET "InviteToken" = substr(md5(random()::text || "MeetingId"::text || "UserId"::text), 1, 32)
                WHERE "InviteToken" IS NULL OR "InviteToken" = '';
                """);

            migrationBuilder.AlterColumn<string>(
                name: "InviteToken",
                table: "MeetingInvites",
                type: "character varying(43)",
                maxLength: 43,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(43)",
                oldMaxLength: 43,
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "MeetingGuestInvites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MeetingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    InviteToken = table.Column<string>(type: "character varying(43)", maxLength: 43, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    JoinedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    JoinedUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeetingGuestInvites", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MeetingGuestInvites_AspNetUsers_JoinedUserId",
                        column: x => x.JoinedUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MeetingGuestInvites_Meetings_MeetingId",
                        column: x => x.MeetingId,
                        principalTable: "Meetings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MeetingInvites_InviteToken",
                table: "MeetingInvites",
                column: "InviteToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MeetingGuestInvites_InviteToken",
                table: "MeetingGuestInvites",
                column: "InviteToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MeetingGuestInvites_JoinedUserId",
                table: "MeetingGuestInvites",
                column: "JoinedUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MeetingGuestInvites_MeetingId_Email",
                table: "MeetingGuestInvites",
                columns: new[] { "MeetingId", "Email" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MeetingGuestInvites");

            migrationBuilder.DropIndex(
                name: "IX_MeetingInvites_InviteToken",
                table: "MeetingInvites");

            migrationBuilder.DropColumn(
                name: "InviteToken",
                table: "MeetingInvites");
        }
    }
}
