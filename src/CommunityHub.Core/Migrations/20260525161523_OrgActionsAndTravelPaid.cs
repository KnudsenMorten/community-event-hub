using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class OrgActionsAndTravelPaid : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPaid",
                table: "TravelReimbursements",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PaidAt",
                table: "TravelReimbursements",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaidNotes",
                table: "TravelReimbursements",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OrganizerActionItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: true),
                    Summary = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ResolvedNotes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizerActionItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrganizerActionItems_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OrganizerActionItems_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrganizerActionItems_EventId_Type_ParticipantId_ResolvedAt",
                table: "OrganizerActionItems",
                columns: new[] { "EventId", "Type", "ParticipantId", "ResolvedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OrganizerActionItems_ParticipantId",
                table: "OrganizerActionItems",
                column: "ParticipantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrganizerActionItems");

            migrationBuilder.DropColumn(
                name: "IsPaid",
                table: "TravelReimbursements");

            migrationBuilder.DropColumn(
                name: "PaidAt",
                table: "TravelReimbursements");

            migrationBuilder.DropColumn(
                name: "PaidNotes",
                table: "TravelReimbursements");
        }
    }
}
