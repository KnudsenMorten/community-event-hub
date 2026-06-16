using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class ParticipantCalendarFeedToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CalendarFeedToken",
                table: "Participants",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Participants_CalendarFeedToken",
                table: "Participants",
                column: "CalendarFeedToken",
                unique: true,
                filter: "[CalendarFeedToken] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Participants_CalendarFeedToken",
                table: "Participants");

            migrationBuilder.DropColumn(
                name: "CalendarFeedToken",
                table: "Participants");
        }
    }
}
