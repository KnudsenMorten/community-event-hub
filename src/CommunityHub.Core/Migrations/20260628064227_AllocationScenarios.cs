using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class AllocationScenarios : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AllocationScenarios",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    OwnerParticipantId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    DroppedParticipantId = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CommittedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CommittedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    DiscardedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AllocationScenarios", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AllocationScenarios_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AllocationScenarios_Participants_DroppedParticipantId",
                        column: x => x.DroppedParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AllocationScenarios_Participants_OwnerParticipantId",
                        column: x => x.OwnerParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "AllocationScenarioMoves",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ScenarioId = table.Column<int>(type: "int", nullable: false),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    Op = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    TaskId = table.Column<int>(type: "int", nullable: true),
                    HotelId = table.Column<int>(type: "int", nullable: true),
                    TargetRole = table.Column<int>(type: "int", nullable: false, defaultValue: 3),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AllocationScenarioMoves", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AllocationScenarioMoves_AllocationScenarios_ScenarioId",
                        column: x => x.ScenarioId,
                        principalTable: "AllocationScenarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AllocationScenarioMoves_Hotels_HotelId",
                        column: x => x.HotelId,
                        principalTable: "Hotels",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AllocationScenarioMoves_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AllocationScenarioMoves_VolunteerTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "VolunteerTasks",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_AllocationScenarioMoves_HotelId",
                table: "AllocationScenarioMoves",
                column: "HotelId");

            migrationBuilder.CreateIndex(
                name: "IX_AllocationScenarioMoves_ParticipantId",
                table: "AllocationScenarioMoves",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_AllocationScenarioMoves_ScenarioId_ParticipantId_TaskId_HotelId",
                table: "AllocationScenarioMoves",
                columns: new[] { "ScenarioId", "ParticipantId", "TaskId", "HotelId" },
                unique: true,
                filter: "[TaskId] IS NOT NULL AND [HotelId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AllocationScenarioMoves_TaskId",
                table: "AllocationScenarioMoves",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_AllocationScenarios_DroppedParticipantId",
                table: "AllocationScenarios",
                column: "DroppedParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_AllocationScenarios_EventId_OwnerParticipantId_Status",
                table: "AllocationScenarios",
                columns: new[] { "EventId", "OwnerParticipantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AllocationScenarios_OwnerParticipantId",
                table: "AllocationScenarios",
                column: "OwnerParticipantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AllocationScenarioMoves");

            migrationBuilder.DropTable(
                name: "AllocationScenarios");
        }
    }
}
