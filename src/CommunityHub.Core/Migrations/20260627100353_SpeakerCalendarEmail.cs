using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SpeakerCalendarEmail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CalendarEmail",
                table: "SpeakerProfiles",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CalendarEmailSetAt",
                table: "SpeakerProfiles",
                type: "datetimeoffset",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CalendarEmail",
                table: "SpeakerProfiles");

            migrationBuilder.DropColumn(
                name: "CalendarEmailSetAt",
                table: "SpeakerProfiles");
        }
    }
}
