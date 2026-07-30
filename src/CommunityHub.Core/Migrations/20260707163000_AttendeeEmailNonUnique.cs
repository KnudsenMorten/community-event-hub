using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class AttendeeEmailNonUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Attendees_EventId_Email",
                table: "Attendees");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReassignmentValidationPendingSince",
                table: "Attendees",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Attendees_EventId_Email",
                table: "Attendees",
                columns: new[] { "EventId", "Email" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Attendees_EventId_Email",
                table: "Attendees");

            migrationBuilder.DropColumn(
                name: "ReassignmentValidationPendingSince",
                table: "Attendees");

            migrationBuilder.CreateIndex(
                name: "IX_Attendees_EventId_Email",
                table: "Attendees",
                columns: new[] { "EventId", "Email" },
                unique: true);
        }
    }
}
