using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddTypeAnnouncementDates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "EventPostWindowEndsOn",
                table: "SoMeSettings",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "SpeakerTracksRound2From",
                table: "SoMeSettings",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EventPostWindowEndsOn",
                table: "SoMeSettings");

            migrationBuilder.DropColumn(
                name: "SpeakerTracksRound2From",
                table: "SoMeSettings");
        }
    }
}
