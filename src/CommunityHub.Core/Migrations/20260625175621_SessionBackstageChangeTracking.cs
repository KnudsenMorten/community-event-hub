using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SessionBackstageChangeTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "BackstageChangeCheckedAt",
                table: "Sessions",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "BackstageEndsAt",
                table: "Sessions",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BackstageRoom",
                table: "Sessions",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BackstageSessionId",
                table: "Sessions",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "BackstageStartsAt",
                table: "Sessions",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ActiveFromForBroadRings",
                table: "FeatureSettings",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_EventId_BackstageSessionId",
                table: "Sessions",
                columns: new[] { "EventId", "BackstageSessionId" },
                unique: true,
                filter: "[BackstageSessionId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Sessions_EventId_BackstageSessionId",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "BackstageChangeCheckedAt",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "BackstageEndsAt",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "BackstageRoom",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "BackstageSessionId",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "BackstageStartsAt",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "ActiveFromForBroadRings",
                table: "FeatureSettings");
        }
    }
}
