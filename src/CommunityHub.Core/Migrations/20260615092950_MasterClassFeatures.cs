using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class MasterClassFeatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BookingEndpointUri",
                table: "Sessions",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "BookingLastSyncedAt",
                table: "Sessions",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LogisticsText",
                table: "Sessions",
                type: "nvarchar(max)",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LogisticsUpdatedAt",
                table: "Sessions",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LogisticsUpdatedByEmail",
                table: "Sessions",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublicSlug",
                table: "Sessions",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MasterClassParticipants",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SessionId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    BookingRecordId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    BookedEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    BookedName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    BookingStatus = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSyncedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MasterClassParticipants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MasterClassParticipants_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MasterClassParticipants_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_MasterClassParticipants_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_PublicSlug",
                table: "Sessions",
                column: "PublicSlug",
                unique: true,
                filter: "[PublicSlug] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MasterClassParticipants_EventId_SessionId",
                table: "MasterClassParticipants",
                columns: new[] { "EventId", "SessionId" });

            migrationBuilder.CreateIndex(
                name: "IX_MasterClassParticipants_EventId_SessionId_BookingRecordId",
                table: "MasterClassParticipants",
                columns: new[] { "EventId", "SessionId", "BookingRecordId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MasterClassParticipants_ParticipantId",
                table: "MasterClassParticipants",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_MasterClassParticipants_SessionId",
                table: "MasterClassParticipants",
                column: "SessionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MasterClassParticipants");

            migrationBuilder.DropIndex(
                name: "IX_Sessions_PublicSlug",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "BookingEndpointUri",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "BookingLastSyncedAt",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "LogisticsText",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "LogisticsUpdatedAt",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "LogisticsUpdatedByEmail",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "PublicSlug",
                table: "Sessions");
        }
    }
}
