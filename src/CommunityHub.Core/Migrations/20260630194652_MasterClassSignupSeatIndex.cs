using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class MasterClassSignupSeatIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MasterClassSignups_SessionId",
                table: "MasterClassSignups");

            migrationBuilder.CreateIndex(
                name: "IX_MasterClassSignups_SessionId_Status",
                table: "MasterClassSignups",
                columns: new[] { "SessionId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MasterClassSignups_SessionId_Status",
                table: "MasterClassSignups");

            migrationBuilder.CreateIndex(
                name: "IX_MasterClassSignups_SessionId",
                table: "MasterClassSignups",
                column: "SessionId");
        }
    }
}
