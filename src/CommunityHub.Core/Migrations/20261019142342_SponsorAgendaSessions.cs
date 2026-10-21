using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SponsorAgendaSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SponsorCompanyId",
                table: "Sessions",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SponsorSlotKey",
                table: "Sessions",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_EventId_SponsorSlotKey",
                table: "Sessions",
                columns: new[] { "EventId", "SponsorSlotKey" },
                unique: true,
                filter: "[SponsorSlotKey] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Sessions_EventId_SponsorSlotKey",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "SponsorCompanyId",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "SponsorSlotKey",
                table: "Sessions");
        }
    }
}
