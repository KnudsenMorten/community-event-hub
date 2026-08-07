using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <inheritdoc />
    public partial class BackfillRing1IsTestUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // §940 — BACKFILL. No schema change: the rule is new, the rows are old.
            //
            // Until now nothing in the APPLICATION ever wrote Participants.IsTestUser — only the SQL
            // seeds did, and they pick their rows by the `test-*@` naming convention. So every
            // role-simulation account named anything else is a Ring 1 (= 1) participant that
            // TestDataScope reads as a real person, and can therefore be announced on the company
            // page. Measured on PROD: participant 116 (a Ring-1 volunteer) with IsTestUser = 0.
            //
            // 🔒 STRICTLY ONE-WAY. Nothing here ever sets IsTestUser back to 0: a row that is flagged
            // today was flagged deliberately, and un-flagging it would promote test data into the
            // real exports and the public surfaces — the exact failure this closes, reversed.
            migrationBuilder.Sql(@"
                UPDATE Participants SET IsTestUser = 1
                 WHERE Ring = 1 AND IsTestUser = 0;");

            // The same rule through the SPONSOR COMPANY, which is the second way a person becomes a
            // Ring-1 user: a contact still on the platform default (Ring 3) INHERITS the company
            // default ring, so their own column stays 3 while their EFFECTIVE ring is 1. A contact
            // explicitly narrowed to another ring wins over the company and is left alone — the same
            // precedence RingResolver.EffectiveForContact applies at runtime.
            migrationBuilder.Sql(@"
                UPDATE p SET p.IsTestUser = 1
                  FROM Participants p
                  JOIN SponsorInfos s
                    ON s.EventId = p.EventId
                   AND s.SponsorCompanyId = p.SponsorCompanyId
                 WHERE p.Role = 4                 -- ParticipantRole.Sponsor
                   AND p.Ring = 3                 -- on the platform default ⇒ inherits the company
                   AND s.Ring = 1                 -- company default is Ring 1
                   AND p.IsTestUser = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately empty. The Up is a data repair, not a schema change, and it cannot be
            // undone: the rows it flagged are indistinguishable from the ones the seeds flagged, so
            // a Down would have to clear BOTH and would destroy the correct state it never wrote.
        }
    }
}
