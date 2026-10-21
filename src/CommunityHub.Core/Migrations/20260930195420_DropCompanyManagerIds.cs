using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class DropCompanyManagerIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Participants_EventId_CmUserId",
                table: "Participants");

            migrationBuilder.DropColumn(
                name: "CmDefaultCoordinatorUserId",
                table: "SponsorInfos");

            migrationBuilder.DropColumn(
                name: "CmUserId",
                table: "Participants");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CmDefaultCoordinatorUserId",
                table: "SponsorInfos",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CmUserId",
                table: "Participants",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Participants_EventId_CmUserId",
                table: "Participants",
                columns: new[] { "EventId", "CmUserId" });
        }
    }
}
