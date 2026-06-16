using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class VolunteerBucketsAllocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CompletedAt",
                table: "VolunteerTasks",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CompletedByEmail",
                table: "VolunteerTasks",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Criticality",
                table: "VolunteerTasks",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "EldkLeadName",
                table: "VolunteerTasks",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Expectations",
                table: "VolunteerTasks",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Instructions",
                table: "VolunteerTasks",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Prerequisites",
                table: "VolunteerTasks",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ResourcesNeeded",
                table: "VolunteerTasks",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ResponsibleTeam",
                table: "VolunteerTasks",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TimeEnd",
                table: "VolunteerTasks",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EldkLeadName",
                table: "VolunteerCategories",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TaskAllocationDrafts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    OwnerParticipantId = table.Column<int>(type: "int", nullable: false),
                    TaskId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskAllocationDrafts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskAllocationDrafts_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_TaskAllocationDrafts_Participants_OwnerParticipantId",
                        column: x => x.OwnerParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_TaskAllocationDrafts_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_TaskAllocationDrafts_VolunteerTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "VolunteerTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VolunteerBucketSupervisors",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    CategoryId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    AppointedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VolunteerBucketSupervisors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VolunteerBucketSupervisors_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_VolunteerBucketSupervisors_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_VolunteerBucketSupervisors_VolunteerCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "VolunteerCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaskAllocationDrafts_EventId_OwnerParticipantId",
                table: "TaskAllocationDrafts",
                columns: new[] { "EventId", "OwnerParticipantId" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskAllocationDrafts_OwnerParticipantId",
                table: "TaskAllocationDrafts",
                column: "OwnerParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskAllocationDrafts_ParticipantId",
                table: "TaskAllocationDrafts",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskAllocationDrafts_TaskId_ParticipantId",
                table: "TaskAllocationDrafts",
                columns: new[] { "TaskId", "ParticipantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerBucketSupervisors_CategoryId_ParticipantId",
                table: "VolunteerBucketSupervisors",
                columns: new[] { "CategoryId", "ParticipantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerBucketSupervisors_EventId_ParticipantId",
                table: "VolunteerBucketSupervisors",
                columns: new[] { "EventId", "ParticipantId" });

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerBucketSupervisors_ParticipantId",
                table: "VolunteerBucketSupervisors",
                column: "ParticipantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TaskAllocationDrafts");

            migrationBuilder.DropTable(
                name: "VolunteerBucketSupervisors");

            migrationBuilder.DropColumn(
                name: "CompletedAt",
                table: "VolunteerTasks");

            migrationBuilder.DropColumn(
                name: "CompletedByEmail",
                table: "VolunteerTasks");

            migrationBuilder.DropColumn(
                name: "Criticality",
                table: "VolunteerTasks");

            migrationBuilder.DropColumn(
                name: "EldkLeadName",
                table: "VolunteerTasks");

            migrationBuilder.DropColumn(
                name: "Expectations",
                table: "VolunteerTasks");

            migrationBuilder.DropColumn(
                name: "Instructions",
                table: "VolunteerTasks");

            migrationBuilder.DropColumn(
                name: "Prerequisites",
                table: "VolunteerTasks");

            migrationBuilder.DropColumn(
                name: "ResourcesNeeded",
                table: "VolunteerTasks");

            migrationBuilder.DropColumn(
                name: "ResponsibleTeam",
                table: "VolunteerTasks");

            migrationBuilder.DropColumn(
                name: "TimeEnd",
                table: "VolunteerTasks");

            migrationBuilder.DropColumn(
                name: "EldkLeadName",
                table: "VolunteerCategories");
        }
    }
}
