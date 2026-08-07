using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <summary>
    /// §933 — rewrite <c>{EditionCode}</c> to <c>{EventNameShort}</c> everywhere it is STORED.
    /// </summary>
    /// <remarks>
    /// <para>🔑 Operator 2026-08-07: <i>"go through all posts and replace {EditionCode} to
    /// {EventNameShort} - and retire {EditionCode} as possible variable. i dont like that name and
    /// prefer {EventNameShort} and {EventNameLong} instead"</i>.</para>
    ///
    /// <para>🔒 <b>A pure RENAME, not a behaviour change.</b> Both tokens already resolved to the
    /// same value (the edition's <c>Code</c>), so no post's rendered text moves by a character. What
    /// changes is the word he reads in the editor: <c>{EditionCode}</c> named an internal concept
    /// among tokens that otherwise name things a reader recognises.</para>
    ///
    /// <para>🔒 <b>Idempotent</b> — <c>REPLACE</c> on a body with no occurrence is a no-op, so
    /// re-running changes nothing.</para>
    ///
    /// <para>⚠️ <b>Both text columns, and the template overrides.</b> A post's words can live in
    /// <c>AutoText</c> or in <c>ManualTextOverride</c> (which wins), and an edition may have
    /// rewritten a template in <c>SoMeTemplates</c>. Missing any one of the three would leave the
    /// retired token alive in exactly the place nobody looks.</para>
    ///
    /// <para>🔒 The token still RESOLVES in code after this. Retiring it from the offered list must
    /// never let a body pasted from an old post publish a literal "{EditionCode}" (§824.15's gap
    /// closes silently, which is what makes that failure mode expensive).</para>
    /// </remarks>
    public partial class RetireEditionCodeTokenInPostBodies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                UPDATE SoMePosts SET AutoText = REPLACE(AutoText, N'{EditionCode}', N'{EventNameShort}')
                 WHERE AutoText LIKE '%{EditionCode}%';");

            migrationBuilder.Sql(@"
                UPDATE SoMePosts SET ManualTextOverride = REPLACE(ManualTextOverride, N'{EditionCode}', N'{EventNameShort}')
                 WHERE ManualTextOverride LIKE '%{EditionCode}%';");

            migrationBuilder.Sql(@"
                UPDATE SoMeTemplates SET Body = REPLACE(Body, N'{EditionCode}', N'{EventNameShort}')
                 WHERE Body LIKE '%{EditionCode}%';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reversible, and worth being so: this is a rename, and putting the old name back is
            // exactly as safe as taking it away.
            migrationBuilder.Sql(@"
                UPDATE SoMePosts SET AutoText = REPLACE(AutoText, N'{EventNameShort}', N'{EditionCode}')
                 WHERE AutoText LIKE '%{EventNameShort}%';");

            migrationBuilder.Sql(@"
                UPDATE SoMePosts SET ManualTextOverride = REPLACE(ManualTextOverride, N'{EventNameShort}', N'{EditionCode}')
                 WHERE ManualTextOverride LIKE '%{EventNameShort}%';");

            migrationBuilder.Sql(@"
                UPDATE SoMeTemplates SET Body = REPLACE(Body, N'{EventNameShort}', N'{EditionCode}')
                 WHERE Body LIKE '%{EventNameShort}%';");
        }
    }
}
