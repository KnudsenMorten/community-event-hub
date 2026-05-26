using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class TravelReimbursements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TravelReimbursements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    RequestReimbursement = table.Column<bool>(type: "bit", nullable: false),
                    SessionType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    OriginCity = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    EstimatedFlightCostEur = table.Column<decimal>(type: "decimal(10,2)", nullable: true),
                    CannotStayInLimits = table.Column<bool>(type: "bit", nullable: false),
                    Explanation = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TravelReimbursements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TravelReimbursements_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TravelReimbursements_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TravelReimbursements_EventId_ParticipantId",
                table: "TravelReimbursements",
                columns: new[] { "EventId", "ParticipantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TravelReimbursements_ParticipantId",
                table: "TravelReimbursements",
                column: "ParticipantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TravelReimbursements");
        }
    }
}
