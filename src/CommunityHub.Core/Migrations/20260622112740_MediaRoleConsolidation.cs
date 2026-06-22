using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <summary>
    /// ParticipantRole refactor (operator 2026-06-22): Video(6)+Camera(7) removed; the int
    /// slots are reused as Media=6 + EventPartner=7. Existing Video rows already read as
    /// Media (same int 6 — correct). Existing Camera rows still hold int 7, which now means
    /// EventPartner — WRONG — so migrate them to Media(6). Safe: EventPartner is brand-new,
    /// so at this deploy every Role=7 row is an old Camera crew member that should be Media.
    /// </summary>
    public partial class MediaRoleConsolidation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Camera (old 7) -> Media (6). Video (old 6) already == Media (6), no change.
            migrationBuilder.Sql("UPDATE Participants SET Role = 6 WHERE Role = 7;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Forward-only data fix: once consolidated we can't tell Camera from Video. No-op.
        }
    }
}
