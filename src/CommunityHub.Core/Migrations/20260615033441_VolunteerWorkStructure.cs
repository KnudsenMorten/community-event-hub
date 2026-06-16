using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class VolunteerWorkStructure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VolunteerCategories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    LeadParticipantId = table.Column<int>(type: "int", nullable: true),
                    SupervisorParticipantId = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VolunteerCategories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VolunteerCategories_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VolunteerCategories_Participants_LeadParticipantId",
                        column: x => x.LeadParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_VolunteerCategories_Participants_SupervisorParticipantId",
                        column: x => x.SupervisorParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "VolunteerSubcategories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    CategoryId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VolunteerSubcategories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VolunteerSubcategories_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_VolunteerSubcategories_VolunteerCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "VolunteerCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VolunteerTasks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    SubcategoryId = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Shift = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VolunteerTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VolunteerTasks_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_VolunteerTasks_VolunteerSubcategories_SubcategoryId",
                        column: x => x.SubcategoryId,
                        principalTable: "VolunteerSubcategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VolunteerHelpRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    TaskId = table.Column<int>(type: "int", nullable: false),
                    CategoryId = table.Column<int>(type: "int", nullable: false),
                    RequestedByParticipantId = table.Column<int>(type: "int", nullable: false),
                    Message = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Response = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    RespondedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RespondedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VolunteerHelpRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VolunteerHelpRequests_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VolunteerHelpRequests_Participants_RequestedByParticipantId",
                        column: x => x.RequestedByParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_VolunteerHelpRequests_VolunteerCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "VolunteerCategories",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_VolunteerHelpRequests_VolunteerTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "VolunteerTasks",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "VolunteerTaskAssignments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    TaskId = table.Column<int>(type: "int", nullable: false),
                    ParticipantId = table.Column<int>(type: "int", nullable: false),
                    AssignedByEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VolunteerTaskAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VolunteerTaskAssignments_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_VolunteerTaskAssignments_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_VolunteerTaskAssignments_VolunteerTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "VolunteerTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerCategories_EventId_Name",
                table: "VolunteerCategories",
                columns: new[] { "EventId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerCategories_EventId_SupervisorParticipantId",
                table: "VolunteerCategories",
                columns: new[] { "EventId", "SupervisorParticipantId" });

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerCategories_LeadParticipantId",
                table: "VolunteerCategories",
                column: "LeadParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerCategories_SupervisorParticipantId",
                table: "VolunteerCategories",
                column: "SupervisorParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerHelpRequests_CategoryId",
                table: "VolunteerHelpRequests",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerHelpRequests_EventId_CategoryId_Status",
                table: "VolunteerHelpRequests",
                columns: new[] { "EventId", "CategoryId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerHelpRequests_EventId_RequestedByParticipantId",
                table: "VolunteerHelpRequests",
                columns: new[] { "EventId", "RequestedByParticipantId" });

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerHelpRequests_RequestedByParticipantId",
                table: "VolunteerHelpRequests",
                column: "RequestedByParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerHelpRequests_TaskId",
                table: "VolunteerHelpRequests",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerSubcategories_CategoryId",
                table: "VolunteerSubcategories",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerSubcategories_EventId_CategoryId",
                table: "VolunteerSubcategories",
                columns: new[] { "EventId", "CategoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerTaskAssignments_EventId_ParticipantId",
                table: "VolunteerTaskAssignments",
                columns: new[] { "EventId", "ParticipantId" });

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerTaskAssignments_ParticipantId",
                table: "VolunteerTaskAssignments",
                column: "ParticipantId");

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerTaskAssignments_TaskId_ParticipantId",
                table: "VolunteerTaskAssignments",
                columns: new[] { "TaskId", "ParticipantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerTasks_EventId_Status",
                table: "VolunteerTasks",
                columns: new[] { "EventId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerTasks_EventId_SubcategoryId",
                table: "VolunteerTasks",
                columns: new[] { "EventId", "SubcategoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_VolunteerTasks_SubcategoryId",
                table: "VolunteerTasks",
                column: "SubcategoryId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VolunteerHelpRequests");

            migrationBuilder.DropTable(
                name: "VolunteerTaskAssignments");

            migrationBuilder.DropTable(
                name: "VolunteerTasks");

            migrationBuilder.DropTable(
                name: "VolunteerSubcategories");

            migrationBuilder.DropTable(
                name: "VolunteerCategories");
        }
    }
}
