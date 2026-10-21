using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class VolunteerPlannerWindows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<TimeOnly>(
                name: "EndTime",
                table: "VolunteerTasks",
                type: "time",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "StartTime",
                table: "VolunteerTasks",
                type: "time",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "VolunteerCategoryPreferences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    CategoryId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    Rank = table.Column<int>(type: "int", nullable: false),
                    AddedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VolunteerCategoryPreferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VolunteerCategoryPreferences_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_VolunteerCategoryPreferences_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_VolunteerCategoryPreferences_VolunteerCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "VolunteerCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VolunteerPlanningRoster",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    AddedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VolunteerPlanningRoster", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VolunteerPlanningRoster_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VolunteerPlanningRoster_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerCategoryPreferences_CategoryId_ParticipantId",
                table: "VolunteerCategoryPreferences",
                columns: new[] { "CategoryId", "ParticipantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerCategoryPreferences_EventId_ParticipantId",
                table: "VolunteerCategoryPreferences",
                columns: new[] { "EventId", "ParticipantId" });

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerCategoryPreferences_ParticipantId",
                table: "VolunteerCategoryPreferences",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerPlanningRoster_EventId_ParticipantId",
                table: "VolunteerPlanningRoster",
                columns: new[] { "EventId", "ParticipantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerPlanningRoster_ParticipantId",
                table: "VolunteerPlanningRoster",
                column: "ParticipantId");

            // §1297 — BACKFILL the typed window from the free-text values that already hold it.
            //
            // 🔑 Every one of the 127 live ELDK27 tasks carries a clean "HH:mm" in Shift and
            // TimeEnd (33 distinct starts, 33 distinct ends, 0 malformed), so the planner can be
            // interval-aware on day one instead of only for tasks somebody re-saves.
            //
            // ⚠️ TRY_CONVERT, not CONVERT: a row whose Shift is prose ("Day 1, morning") yields
            // NULL and stays unscheduled, which the planner handles. CONVERT would fail the whole
            // migration on one such row. The text remains the source of truth either way —
            // VolunteerTaskWindow.WindowOf re-parses it when the typed column is null.
            migrationBuilder.Sql(@"
UPDATE VolunteerTasks
   SET StartTime = TRY_CONVERT(time, LTRIM(RTRIM(Shift))),
       EndTime   = TRY_CONVERT(time, LTRIM(RTRIM(TimeEnd)))
 WHERE Shift IS NOT NULL OR TimeEnd IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VolunteerCategoryPreferences");

            migrationBuilder.DropTable(
                name: "VolunteerPlanningRoster");

            migrationBuilder.DropColumn(
                name: "EndTime",
                table: "VolunteerTasks");

            migrationBuilder.DropColumn(
                name: "StartTime",
                table: "VolunteerTasks");
        }
    }
}
