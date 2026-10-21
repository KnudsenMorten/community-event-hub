using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SponsorSoMeTagContacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SoMeTagContacts",
                table: "SponsorInfos",
                type: "bit",
                nullable: false,
                defaultValue: true);   // §1312 — existing sponsors keep tagging their people; only an explicit opt-out stops it
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SoMeTagContacts",
                table: "SponsorInfos");
        }
    }
}
