using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class VolunteerShiftDecision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DecisionAt",
                table: "VolunteerTaskAssignments",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DecisionNote",
                table: "VolunteerTaskAssignments",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DecisionStatus",
                table: "VolunteerTaskAssignments",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DecisionAt",
                table: "VolunteerTaskAssignments");

            migrationBuilder.DropColumn(
                name: "DecisionNote",
                table: "VolunteerTaskAssignments");

            migrationBuilder.DropColumn(
                name: "DecisionStatus",
                table: "VolunteerTaskAssignments");
        }
    }
}
