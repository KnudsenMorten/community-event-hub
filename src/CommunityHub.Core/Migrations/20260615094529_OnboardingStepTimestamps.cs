using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class OnboardingStepTimestamps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OnboardingCompleted_AppreciationAt",
                table: "Participants",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OnboardingCompleted_BioAt",
                table: "Participants",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OnboardingCompleted_HotelAt",
                table: "Participants",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OnboardingCompleted_PictureAt",
                table: "Participants",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OnboardingCompleted_SwagAt",
                table: "Participants",
                type: "datetimeoffset",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OnboardingCompleted_AppreciationAt",
                table: "Participants");

            migrationBuilder.DropColumn(
                name: "OnboardingCompleted_BioAt",
                table: "Participants");

            migrationBuilder.DropColumn(
                name: "OnboardingCompleted_HotelAt",
                table: "Participants");

            migrationBuilder.DropColumn(
                name: "OnboardingCompleted_PictureAt",
                table: "Participants");

            migrationBuilder.DropColumn(
                name: "OnboardingCompleted_SwagAt",
                table: "Participants");
        }
    }
}
