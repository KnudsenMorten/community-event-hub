using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddSoMeAutoApprove : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoApproveEnabled",
                table: "SoMeSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "AutoApproveLeadDays",
                table: "SoMeSettings",
                type: "int",
                nullable: false,
                // 🔒 §918 — 7, NOT the scaffolded 0. EF defaults an int column to 0, which would
                // give every EXISTING edition a one-day lead the moment auto-approval is switched
                // on (the service floors 0 to 1) — a review window of hours where he expected a
                // week. Hand-edited so the stored default matches the C# default.
                defaultValue: 7);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoApproveEnabled",
                table: "SoMeSettings");

            migrationBuilder.DropColumn(
                name: "AutoApproveLeadDays",
                table: "SoMeSettings");
        }
    }
}
