using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SpeakerDetailsFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BackstageSpeakerId",
                table: "SpeakerProfiles",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FirstName",
                table: "SpeakerProfiles",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastName",
                table: "SpeakerProfiles",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MvpCategories",
                table: "SpeakerProfiles",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhotoSharePointPath",
                table: "SpeakerProfiles",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SessionizeSpeakerId",
                table: "SpeakerProfiles",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BackstageSpeakerId",
                table: "SpeakerProfiles");

            migrationBuilder.DropColumn(
                name: "FirstName",
                table: "SpeakerProfiles");

            migrationBuilder.DropColumn(
                name: "LastName",
                table: "SpeakerProfiles");

            migrationBuilder.DropColumn(
                name: "MvpCategories",
                table: "SpeakerProfiles");

            migrationBuilder.DropColumn(
                name: "PhotoSharePointPath",
                table: "SpeakerProfiles");

            migrationBuilder.DropColumn(
                name: "SessionizeSpeakerId",
                table: "SpeakerProfiles");
        }
    }
}
