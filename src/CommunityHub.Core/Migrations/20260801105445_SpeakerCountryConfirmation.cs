using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SpeakerCountryConfirmation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CountryConfirmedAt",
                table: "SpeakerProfiles",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CountryConfirmedBy",
                table: "SpeakerProfiles",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CountryConfirmedInBackstage",
                table: "SpeakerProfiles",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CountryConfirmedAt",
                table: "SpeakerProfiles");

            migrationBuilder.DropColumn(
                name: "CountryConfirmedBy",
                table: "SpeakerProfiles");

            migrationBuilder.DropColumn(
                name: "CountryConfirmedInBackstage",
                table: "SpeakerProfiles");
        }
    }
}
