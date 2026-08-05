using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class EmailReminderCadencePerRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EmailReminderCadences_EventId_TemplateKey",
                table: "EmailReminderCadences");

            migrationBuilder.AddColumn<int>(
                name: "Role",
                table: "EmailReminderCadences",
                type: "int",
                nullable: true);

            // 🔒 §881 — UNFILTERED, deliberately. EF generated `filter: "[Role] IS NOT NULL"`, which
            // leaves the all-roles rows (Role NULL) unconstrained: unlimited duplicates for one
            // template, with the resolver picking one arbitrarily while the upsert keeps adding more.
            // §705.3b hit exactly this on EmailTemplateRing. SQL Server treats NULLs as EQUAL in an
            // unfiltered unique index, which gives the intended shape — one all-roles row plus at
            // most one row per named role.
            migrationBuilder.CreateIndex(
                name: "IX_EmailReminderCadences_EventId_TemplateKey_Role",
                table: "EmailReminderCadences",
                columns: new[] { "EventId", "TemplateKey", "Role" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EmailReminderCadences_EventId_TemplateKey_Role",
                table: "EmailReminderCadences");

            migrationBuilder.DropColumn(
                name: "Role",
                table: "EmailReminderCadences");

            migrationBuilder.CreateIndex(
                name: "IX_EmailReminderCadences_EventId_TemplateKey",
                table: "EmailReminderCadences",
                columns: new[] { "EventId", "TemplateKey" },
                unique: true);
        }
    }
}
