using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SpeakerBioOwnedBySpeaker : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "BioLastEditedBySpeakerAt",
                table: "SpeakerProfiles",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhotoUrl",
                table: "SpeakerProfiles",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SpeakerEditedFields",
                table: "SpeakerProfiles",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BioLastEditedBySpeakerAt",
                table: "SpeakerProfiles");

            migrationBuilder.DropColumn(
                name: "PhotoUrl",
                table: "SpeakerProfiles");

            migrationBuilder.DropColumn(
                name: "SpeakerEditedFields",
                table: "SpeakerProfiles");
        }
    }
}
