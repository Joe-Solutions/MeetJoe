using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetJoe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConversationCreatedByAndEmptyGroup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CreatedById",
                table: "Conversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_CreatedById",
                table: "Conversations",
                column: "CreatedById");

            migrationBuilder.AddForeignKey(
                name: "FK_Conversations_AspNetUsers_CreatedById",
                table: "Conversations",
                column: "CreatedById",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // Groups: earliest member is treated as creator for rename permissions.
            migrationBuilder.Sql(
                """
                UPDATE "Conversations" c
                SET "CreatedById" = (
                    SELECT m."UserId"
                    FROM "ConversationMembers" m
                    WHERE m."ConversationId" = c."Id"
                    ORDER BY m."JoinedAt", m."UserId"
                    LIMIT 1
                )
                WHERE c."Kind" = 1 AND c."CreatedById" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Conversations_AspNetUsers_CreatedById",
                table: "Conversations");

            migrationBuilder.DropIndex(
                name: "IX_Conversations_CreatedById",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "CreatedById",
                table: "Conversations");
        }
    }
}
