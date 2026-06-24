using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SponsorEventCoordinator : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EventCoordinatorCompanyName",
                table: "SponsorInfos",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EventCoordinatorEmail",
                table: "SponsorInfos",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EventCoordinatorFirstName",
                table: "SponsorInfos",
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EventCoordinatorLastName",
                table: "SponsorInfos",
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EventCoordinatorPhone",
                table: "SponsorInfos",
                type: "nvarchar(60)",
                maxLength: 60,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EventCoordinatorCompanyName",
                table: "SponsorInfos");

            migrationBuilder.DropColumn(
                name: "EventCoordinatorEmail",
                table: "SponsorInfos");

            migrationBuilder.DropColumn(
                name: "EventCoordinatorFirstName",
                table: "SponsorInfos");

            migrationBuilder.DropColumn(
                name: "EventCoordinatorLastName",
                table: "SponsorInfos");

            migrationBuilder.DropColumn(
                name: "EventCoordinatorPhone",
                table: "SponsorInfos");
        }
    }
}
