using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SponsorContactCmUserId : Migration
    {
        // REQUIREMENTS §7c — unique-identifier contact link. Adds the nullable
        // Participants.CmUserId column (the CM/WordPress user_id) plus an
        // (EventId, CmUserId) index for the id-based CM → hub correlation. Nullable
        // + backfilled by SponsorContactSyncService on the next sync (it writes the
        // CM user_id from /companies/{id}/users on create AND update), so existing
        // rows need no data migration. The contact is linked to CM by id, never name.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Participants_EventId_CmUserId",
                table: "Participants");

            migrationBuilder.DropColumn(
                name: "CmUserId",
                table: "Participants");
        }
    }
}
