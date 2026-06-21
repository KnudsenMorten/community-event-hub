using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class AttendeeBackstageTicketId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BackstageTicketId",
                table: "Attendees",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Attendees_EventId_BackstageTicketId",
                table: "Attendees",
                columns: new[] { "EventId", "BackstageTicketId" },
                unique: true,
                filter: "[BackstageTicketId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Attendees_EventId_BackstageTicketId",
                table: "Attendees");

            migrationBuilder.DropColumn(
                name: "BackstageTicketId",
                table: "Attendees");
        }
    }
}
