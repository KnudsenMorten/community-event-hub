using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class ReleaseRings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Ring",
                table: "SponsorInfos",
                type: "int",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<int>(
                name: "Ring",
                table: "Participants",
                type: "int",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<int>(
                name: "ReleasedToRing",
                table: "FeatureSettings",
                type: "int",
                nullable: false,
                defaultValue: 3);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Ring",
                table: "SponsorInfos");

            migrationBuilder.DropColumn(
                name: "Ring",
                table: "Participants");

            migrationBuilder.DropColumn(
                name: "ReleasedToRing",
                table: "FeatureSettings");
        }
    }
}
