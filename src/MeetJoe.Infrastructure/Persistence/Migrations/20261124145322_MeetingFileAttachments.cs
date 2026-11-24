using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetJoe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MeetingFileAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "MeetingId",
                table: "Files",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Files_MeetingId",
                table: "Files",
                column: "MeetingId");

            migrationBuilder.AddForeignKey(
                name: "FK_Files_Meetings_MeetingId",
                table: "Files",
                column: "MeetingId",
                principalTable: "Meetings",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Files_Meetings_MeetingId",
                table: "Files");

            migrationBuilder.DropIndex(
                name: "IX_Files_MeetingId",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "MeetingId",
                table: "Files");
        }
    }
}
