using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SoMeEditionCreditsAndSponsorOrgId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LinkedInOrganizationId",
                table: "SponsorInfos",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EventSystemUrl",
                table: "SoMeSettings",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EventTags",
                table: "SoMeSettings",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OrganizerCredits",
                table: "SoMeSettings",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LinkedInOrganizationId",
                table: "SponsorInfos");

            migrationBuilder.DropColumn(
                name: "EventSystemUrl",
                table: "SoMeSettings");

            migrationBuilder.DropColumn(
                name: "EventTags",
                table: "SoMeSettings");

            migrationBuilder.DropColumn(
                name: "OrganizerCredits",
                table: "SoMeSettings");
        }
    }
}
