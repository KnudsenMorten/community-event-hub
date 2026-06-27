using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SpeakerBackstageChangeTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BackstageBio",
                table: "SpeakerProfiles",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "BackstageChangeCheckedAt",
                table: "SpeakerProfiles",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BackstageCountry",
                table: "SpeakerProfiles",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BackstageLinkedIn",
                table: "SpeakerProfiles",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BackstageName",
                table: "SpeakerProfiles",
                type: "nvarchar(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BackstageTagline",
                table: "SpeakerProfiles",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BackstageTwitter",
                table: "SpeakerProfiles",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BackstageBio",
                table: "SpeakerProfiles");

            migrationBuilder.DropColumn(
                name: "BackstageChangeCheckedAt",
                table: "SpeakerProfiles");

            migrationBuilder.DropColumn(
                name: "BackstageCountry",
                table: "SpeakerProfiles");

            migrationBuilder.DropColumn(
                name: "BackstageLinkedIn",
                table: "SpeakerProfiles");

            migrationBuilder.DropColumn(
                name: "BackstageName",
                table: "SpeakerProfiles");

            migrationBuilder.DropColumn(
                name: "BackstageTagline",
                table: "SpeakerProfiles");

            migrationBuilder.DropColumn(
                name: "BackstageTwitter",
                table: "SpeakerProfiles");
        }
    }
}
