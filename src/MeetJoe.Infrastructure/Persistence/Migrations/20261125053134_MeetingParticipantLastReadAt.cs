using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetJoe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MeetingParticipantLastReadAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastReadAt",
                table: "MeetingParticipants",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.Sql(
                """
                UPDATE "MeetingParticipants"
                SET "LastReadAt" = "JoinedAt" - INTERVAL '1 microsecond';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastReadAt",
                table: "MeetingParticipants");
        }
    }
}
