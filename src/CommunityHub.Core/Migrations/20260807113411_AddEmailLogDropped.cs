using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailLogDropped : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Dropped",
                table: "EmailLogs",
                type: "bit",
                nullable: false,
                defaultValue: false);
            // §938 — BACKFILL, so history stops lying too. Every existing ring-gated or
            // kill-switched row is a DROP that was recorded as a failure; without this the retry
            // service would keep treating the ones inside its window as retryable, and the email
            // log would go on showing deliberate policy decisions as things that went wrong.
            migrationBuilder.Sql(@"
                UPDATE EmailLogs SET Dropped = 1
                 WHERE Success = 0 AND Error IS NOT NULL
                   AND (Error LIKE N'Ring-dropped%'
                     OR Error LIKE N'Dropped by global email kill switch%'
                     OR Error LIKE N'Dropped —%');");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Dropped",
                table: "EmailLogs");
        }
    }
}
