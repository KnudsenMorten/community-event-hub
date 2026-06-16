using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SessionManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EvaluationEmailedAt",
                table: "Sessions",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EvaluationFormUrl",
                table: "Sessions",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsHubAdded",
                table: "Sessions",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Length",
                table: "Sessions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RoomQrGeneratedAt",
                table: "Sessions",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RoomQrUrl",
                table: "Sessions",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Type",
                table: "Sessions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_EventId_Length",
                table: "Sessions",
                columns: new[] { "EventId", "Length" });

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_EventId_Room",
                table: "Sessions",
                columns: new[] { "EventId", "Room" });

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_EventId_Type",
                table: "Sessions",
                columns: new[] { "EventId", "Type" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Sessions_EventId_Length",
                table: "Sessions");

            migrationBuilder.DropIndex(
                name: "IX_Sessions_EventId_Room",
                table: "Sessions");

            migrationBuilder.DropIndex(
                name: "IX_Sessions_EventId_Type",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "EvaluationEmailedAt",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "EvaluationFormUrl",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "IsHubAdded",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "Length",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "RoomQrGeneratedAt",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "RoomQrUrl",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "Type",
                table: "Sessions");
        }
    }
}
