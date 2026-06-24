using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SponsorCompanyDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LinkedInUrl",
                table: "SponsorInfos",
                type: "nvarchar(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TwitterUrl",
                table: "SponsorInfos",
                type: "nvarchar(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ZohoExhibitorId",
                table: "SponsorInfos",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ZohoSponsorId",
                table: "SponsorInfos",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LinkedInUrl",
                table: "SponsorInfos");

            migrationBuilder.DropColumn(
                name: "TwitterUrl",
                table: "SponsorInfos");

            migrationBuilder.DropColumn(
                name: "ZohoExhibitorId",
                table: "SponsorInfos");

            migrationBuilder.DropColumn(
                name: "ZohoSponsorId",
                table: "SponsorInfos");
        }
    }
}
