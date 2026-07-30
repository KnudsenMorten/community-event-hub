using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class EmailTemplateRingPerRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EmailTemplateRings_EventId_TemplateKey",
                table: "EmailTemplateRings");

            migrationBuilder.AddColumn<int>(
                name: "Role",
                table: "EmailTemplateRings",
                type: "int",
                nullable: true);

            // 🔒 NO FILTER — deliberately. EF generated `filter: "[Role] IS NOT NULL"`, which enforces
            // uniqueness ONLY on role-specific rows and would allow UNLIMITED all-roles rows for the same
            // template. Resolution would then pick one arbitrarily while the upsert kept adding more —
            // an invisible split-brain over who receives a mail.
            //
            // SQL Server treats NULLs as EQUAL for unique-index purposes, so an unfiltered index gives
            // exactly the intended shape: at most ONE all-roles row (Role IS NULL) plus at most one row
            // per named role.
            migrationBuilder.CreateIndex(
                name: "IX_EmailTemplateRings_EventId_TemplateKey_Role",
                table: "EmailTemplateRings",
                columns: new[] { "EventId", "TemplateKey", "Role" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EmailTemplateRings_EventId_TemplateKey_Role",
                table: "EmailTemplateRings");

            migrationBuilder.DropColumn(
                name: "Role",
                table: "EmailTemplateRings");

            migrationBuilder.CreateIndex(
                name: "IX_EmailTemplateRings_EventId_TemplateKey",
                table: "EmailTemplateRings",
                columns: new[] { "EventId", "TemplateKey" },
                unique: true);
        }
    }
}
