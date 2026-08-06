using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <summary>
    /// §888.3 — put the organizer credit into every post body, as an ordinary TOKEN.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-06: <i>"remove the crap you build for organizer and make it as a
    /// variable like others"</i>. The credit was stripped out of bodies by §861.4 and stapled on at
    /// publish instead — so it was invisible in the editor, unmovable, and (the part that bit) it
    /// bypassed the variable pipeline entirely, which is why organizers published as plain text
    /// while <c>{Speakers}</c> tagged people properly.</para>
    ///
    /// <para>🔒 <b>Idempotent.</b> Only rows carrying neither token are touched, so re-running is a
    /// no-op and a post where he already placed the credit keeps his placement.</para>
    ///
    /// <para>⚠️ <b>The TOKEN is stored, never the names.</b> Liveness is unchanged: a post written
    /// today still picks up tomorrow's organizer list — and now also tomorrow's mentions.</para>
    /// </remarks>
    public partial class PutOrganizerCreditTokenBackInPostBodies : Migration
    {
        // {EventNameShort} is his spelling (§888.2); {EditionCode} resolves identically.
        private const string CreditBlock = "{EventNameShort} Organizers:\n{Organizers}";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($@"
                UPDATE SoMePosts
                   SET AutoText = RTRIM(AutoText) + CHAR(10) + CHAR(10) + N'{CreditBlock}'
                 WHERE AutoText IS NOT NULL
                   AND LTRIM(RTRIM(AutoText)) <> ''
                   AND AutoText NOT LIKE '%{{Organizers}}%'
                   AND AutoText NOT LIKE '%{{OrganizerLinkedInUrls}}%';");

            migrationBuilder.Sql($@"
                UPDATE SoMePosts
                   SET ManualTextOverride = RTRIM(ManualTextOverride) + CHAR(10) + CHAR(10) + N'{CreditBlock}'
                 WHERE ManualTextOverride IS NOT NULL
                   AND LTRIM(RTRIM(ManualTextOverride)) <> ''
                   AND ManualTextOverride NOT LIKE '%{{Organizers}}%'
                   AND ManualTextOverride NOT LIKE '%{{OrganizerLinkedInUrls}}%';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Strips only the exact block this appended, only from the very end — a credit he moved
            // or reworded is his and survives a rollback.
            migrationBuilder.Sql($@"
                UPDATE SoMePosts
                   SET AutoText = LEFT(AutoText, LEN(AutoText) - LEN(N'{CreditBlock}') - 2)
                 WHERE AutoText LIKE '%' + CHAR(10) + CHAR(10) + N'{CreditBlock}';");

            migrationBuilder.Sql($@"
                UPDATE SoMePosts
                   SET ManualTextOverride = LEFT(ManualTextOverride, LEN(ManualTextOverride) - LEN(N'{CreditBlock}') - 2)
                 WHERE ManualTextOverride LIKE '%' + CHAR(10) + CHAR(10) + N'{CreditBlock}';");
        }
    }
}
