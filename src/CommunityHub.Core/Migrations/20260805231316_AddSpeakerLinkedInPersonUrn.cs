using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddSpeakerLinkedInPersonUrn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LinkedInPersonUrn",
                table: "SpeakerProfiles",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LinkedInPersonUrnCheckedAt",
                table: "SpeakerProfiles",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LinkedInPersonUrnStatus",
                table: "SpeakerProfiles",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LinkedInPersonUrn",
                table: "SpeakerProfiles");

            migrationBuilder.DropColumn(
                name: "LinkedInPersonUrnCheckedAt",
                table: "SpeakerProfiles");

            migrationBuilder.DropColumn(
                name: "LinkedInPersonUrnStatus",
                table: "SpeakerProfiles");
        }
    }
}
