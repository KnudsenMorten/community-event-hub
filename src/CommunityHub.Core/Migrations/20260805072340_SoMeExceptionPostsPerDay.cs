using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SoMeExceptionPostsPerDay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 🔴 defaultValue 3, NOT the scaffolder's 0 — the SECOND time this has bitten (see
            // 20260805060237_SoMePostMediaKind, where a 0 would have halved the daily rhythm).
            // The entity default is 3; a raw 0 clamps back to the normal rhythm, which silently
            // DISABLES exceptions on the one edition that already has a settings row. An added
            // column must land on the value the code means, never on the CLR default.
            migrationBuilder.AddColumn<int>(
                name: "ExceptionPostsPerDay",
                table: "SoMeSettings",
                type: "int",
                nullable: false,
                defaultValue: 3);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExceptionPostsPerDay",
                table: "SoMeSettings");
        }
    }
}
