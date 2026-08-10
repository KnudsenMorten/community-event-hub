using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class AttendeeMonitorExpiry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 🔴 §1040 — WRITTEN BY HAND, and the reason is worth recording.
            //
            // `AttendeeMonitor.ExpiresAt` was added AFTER the CreateTable migration, and while
            // reworking it I deleted the generated AddColumn migration but the model SNAPSHOT kept
            // the property. EF then had nothing to diff and produced an EMPTY migration — twice —
            // so the column existed in the model and in no database.
            //
            // ⚠️ The symptom was not a build error. DEV created the table without the column, every
            // query on the entity threw at runtime, and the page redirected to /Error — which
            // requires auth — so it presented as "the anonymous page redirects to login". An empty
            // migration is silent; what it breaks is not.
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExpiresAt",
                table: "AttendeeMonitors",
                type: "datetimeoffset",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExpiresAt",
                table: "AttendeeMonitors");
        }
    }
}
