using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class SponsorIsSponsorIsExhibitorFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsExhibitor",
                table: "SponsorInfos",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsSponsor",
                table: "SponsorInfos",
                type: "bit",
                nullable: false,
                defaultValue: false);

            // 🔴 §1034 — BACKFILL, AND IT IS NOT OPTIONAL.
            //
            // `SponsorInfo` now carries a global query filter of `IsSponsor`. Both columns default
            // to 0, so WITHOUT this statement the very first request after deploy would find ZERO
            // sponsors: the public sponsors page empty, the SoMe planner with nothing to announce,
            // every sponsor's own pages blank. The rows would all still be there — invisible.
            //
            // 🔑 Every row that exists TODAY is a sponsor, by construction: before this change a
            // row was only created for a booth/session purchase, a resolved company name on a
            // sponsor order, or an organizer/sponsor filling in the company forms. Measured on PROD
            // 2026-08-10 — 16 rows, every one a real sponsor company (plus the seeded Test-Silver).
            // ⚠️ Deliberately NOT evidence-based (tier/session), because a Silver sponsor is
            // tier-None with no session and IS a sponsor — that rule would have hidden two of them.
            migrationBuilder.Sql("UPDATE [SponsorInfos] SET [IsSponsor] = 1;");

            // The booth half is derivable and unambiguous: Gold(1) and above have a booth, Silver(0)
            // is digital-only. The same rule as the SponsorInfo.HasBooth property it supersedes.
            migrationBuilder.Sql("UPDATE [SponsorInfos] SET [IsExhibitor] = 1 WHERE [SponsorPackage] >= 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsExhibitor",
                table: "SponsorInfos");

            migrationBuilder.DropColumn(
                name: "IsSponsor",
                table: "SponsorInfos");
        }
    }
}
