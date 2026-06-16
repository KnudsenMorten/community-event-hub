using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SponsorPublicListing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Tier",
                table: "SponsorInfos",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "WebsiteUrl",
                table: "SponsorInfos",
                type: "nvarchar(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SponsorInfos_EventId_Tier",
                table: "SponsorInfos",
                columns: new[] { "EventId", "Tier" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SponsorInfos_EventId_Tier",
                table: "SponsorInfos");

            migrationBuilder.DropColumn(
                name: "Tier",
                table: "SponsorInfos");

            migrationBuilder.DropColumn(
                name: "WebsiteUrl",
                table: "SponsorInfos");
        }
    }
}
