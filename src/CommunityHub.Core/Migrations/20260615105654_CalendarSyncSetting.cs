using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class CalendarSyncSetting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Organizer master switch for calendar sync (REQUIREMENTS §5). Defaults
            // TRUE so existing editions keep calendar sync ON after the upgrade and
            // new editions start enabled; an organizer can disable it on
            // /Organizer/CalendarSettings.
            migrationBuilder.AddColumn<bool>(
                name: "CalendarSyncEnabled",
                table: "Events",
                type: "bit",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CalendarSyncEnabled",
                table: "Events");
        }
    }
}
