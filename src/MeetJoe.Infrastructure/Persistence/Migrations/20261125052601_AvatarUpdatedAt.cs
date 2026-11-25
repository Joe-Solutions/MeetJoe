using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetJoe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AvatarUpdatedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AvatarUpdatedAt",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            // Give existing photos a version so clients stop serving stale cached bitmaps.
            migrationBuilder.Sql(
                """
                UPDATE "AspNetUsers"
                SET "AvatarUpdatedAt" = COALESCE("CreatedAt", TIMESTAMPTZ '2024-01-01 00:00:00+00')
                WHERE "AvatarStorageKey" IS NOT NULL AND "AvatarUpdatedAt" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AvatarUpdatedAt",
                table: "AspNetUsers");
        }
    }
}
