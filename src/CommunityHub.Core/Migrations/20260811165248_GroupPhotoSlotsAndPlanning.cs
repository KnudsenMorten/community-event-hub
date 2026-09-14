using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class GroupPhotoSlotsAndPlanning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PlannedAtUtc",
                table: "GroupPhotoRegistrations",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SlotNotifiedAt",
                table: "GroupPhotoRegistrations",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SlotPublishedAt",
                table: "GroupPhotoRegistrations",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GroupPhotoSlots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    StartUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DurationMinutes = table.Column<int>(type: "int", nullable: false),
                    IsPreDay = table.Column<bool>(type: "bit", nullable: false),
                    Location = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsBlocked = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupPhotoSlots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GroupPhotoSlots_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GroupPhotoSlots_EventId_StartUtc",
                table: "GroupPhotoSlots",
                columns: new[] { "EventId", "StartUtc" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GroupPhotoSlots");

            migrationBuilder.DropColumn(
                name: "PlannedAtUtc",
                table: "GroupPhotoRegistrations");

            migrationBuilder.DropColumn(
                name: "SlotNotifiedAt",
                table: "GroupPhotoRegistrations");

            migrationBuilder.DropColumn(
                name: "SlotPublishedAt",
                table: "GroupPhotoRegistrations");
        }
    }
}
