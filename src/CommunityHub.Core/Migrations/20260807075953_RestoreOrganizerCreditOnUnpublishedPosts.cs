using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <summary>
    /// §932.2 — put the organizer credit back on every UNPUBLISHED post that lost it.
    /// </summary>
    /// <remarks>
    /// <para>🔴 <b>Repairing real damage, not tidying.</b> §932's editor bug deleted the credit from
    /// the stored body on every save between 2026-08-06 07:59 and the fix. Post <c>6345</c> published
    /// bare at 07:00 on 2026-08-07 and cannot be recalled; post <c>496</c> was **approved and due at
    /// 11:00 the same morning**, and would have gone the same way.</para>
    ///
    /// <para>🔒 <b>UNPUBLISHED ONLY.</b> A published post is history — rewriting its stored words
    /// would make the hub disagree with what is actually on LinkedIn, which is worse than the gap it
    /// would be papering over.</para>
    ///
    /// <para>🔑 <b>Appended to the column that WINS.</b> A post's words live in
    /// <c>ManualTextOverride</c> when he has edited it and in <c>AutoText</c> otherwise, and only the
    /// winning column is published. Writing to the other one would look like a fix and change
    /// nothing — which is exactly how the original defect stayed invisible.</para>
    ///
    /// <para>🔒 <b>Idempotent</b>: only rows carrying NEITHER token are touched, so re-running is a
    /// no-op and a post where he has placed the credit himself keeps his placement.</para>
    ///
    /// <para>⚠️ <b>This is a one-off repair, and the recurrence is prevented in CODE</b> (§932.1 —
    /// the editor no longer strips the credit on save). A migration that had to be re-run every time
    /// someone edited a post would be a symptom, not a fix.</para>
    /// </remarks>
    public partial class RestoreOrganizerCreditOnUnpublishedPosts : Migration
    {
        // §933 — his preferred token names.
        private const string CreditBlock = "{EventNameShort} Organizers:\n{Organizers}";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The edited body wins, so it is the one that must carry the credit.
            migrationBuilder.Sql($@"
                UPDATE SoMePosts
                   SET ManualTextOverride = RTRIM(ManualTextOverride) + CHAR(10) + CHAR(10) + N'{CreditBlock}'
                 WHERE IsDeleted = 0
                   AND PublishedAtUtc IS NULL
                   AND ManualTextOverride IS NOT NULL
                   AND LTRIM(RTRIM(ManualTextOverride)) <> ''
                   AND ManualTextOverride NOT LIKE '%{{Organizers}}%'
                   AND ManualTextOverride NOT LIKE '%{{OrganizerLinkedInUrls}}%';");

            // …and where there is no edit, the composed text is what publishes.
            migrationBuilder.Sql($@"
                UPDATE SoMePosts
                   SET AutoText = RTRIM(AutoText) + CHAR(10) + CHAR(10) + N'{CreditBlock}'
                 WHERE IsDeleted = 0
                   AND PublishedAtUtc IS NULL
                   AND ManualTextOverride IS NULL
                   AND AutoText IS NOT NULL
                   AND LTRIM(RTRIM(AutoText)) <> ''
                   AND AutoText NOT LIKE '%{{Organizers}}%'
                   AND AutoText NOT LIKE '%{{OrganizerLinkedInUrls}}%';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 🔒 Deliberately NOT reversed. Removing an organizer credit from a post that is about to
            // publish is the defect this exists to repair; a rollback must not re-create it.
        }
    }
}
