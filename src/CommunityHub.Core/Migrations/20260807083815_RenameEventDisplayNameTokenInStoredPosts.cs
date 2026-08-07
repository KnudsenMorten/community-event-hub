using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <summary>
    /// §933.2 — rewrite <c>{EventDisplayName}</c> to <c>{EventNameLong}</c> in STORED post bodies.
    /// The variable itself is left completely alone.
    /// </summary>
    /// <remarks>
    /// <para>🔑 Operator 2026-08-07, after cancelling the full retirement: <i>"but can we
    /// search/replace in posts and change but leave the variable alone"</i>. So this is the DATA half
    /// only — the posts read the way he wants, and nothing in code moves.</para>
    ///
    /// <para>🔒 <b>{EventDisplayName} REMAINS a first-class, offered token</b> (§933.1). It is still
    /// in <c>SoMeTemplateCatalog</c>'s token list, still emitted by the shipped Type 4 template, and
    /// still resolved. Nothing here retires anything.</para>
    ///
    /// <para>🔑 <b>This is the safe half of what §934 would have been.</b> The hazard there was that
    /// <c>EventDisplayName</c> is ALSO a widely-used C# property — agenda, signage, dashboards,
    /// session pages, command centre — so touching code risked a large unrelated refactor. A SQL
    /// <c>REPLACE</c> on a braced token inside a post body cannot reach any of that.</para>
    ///
    /// <para>🔒 <b>A pure rename with no rendered difference.</b> Both tokens resolve to
    /// <c>Event.DisplayName</c>, so no post's published text moves by a character.</para>
    ///
    /// <para>⚠️ Measured on PROD before writing this: <b>2 posts</b>, both UNPUBLISHED, both in
    /// <c>AutoText</c>; no overrides and no template overrides carried it. Both other columns are
    /// still swept, because "none today" is not "none tomorrow".</para>
    ///
    /// <para>🔒 <b>Published posts are swept too, and that is deliberate here</b> — unlike §932.2's
    /// credit repair. This changes a TOKEN into an equivalent token; it cannot alter what a published
    /// post rendered, so the hub does not end up disagreeing with LinkedIn.</para>
    ///
    /// <para>🔒 Idempotent, and reversible: a rename should be.</para>
    /// </remarks>
    public partial class RenameEventDisplayNameTokenInStoredPosts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                UPDATE SoMePosts SET AutoText = REPLACE(AutoText, N'{EventDisplayName}', N'{EventNameLong}')
                 WHERE AutoText LIKE '%{EventDisplayName}%';");

            migrationBuilder.Sql(@"
                UPDATE SoMePosts SET ManualTextOverride = REPLACE(ManualTextOverride, N'{EventDisplayName}', N'{EventNameLong}')
                 WHERE ManualTextOverride LIKE '%{EventDisplayName}%';");

            migrationBuilder.Sql(@"
                UPDATE SoMeTemplates SET Body = REPLACE(Body, N'{EventDisplayName}', N'{EventNameLong}')
                 WHERE Body LIKE '%{EventDisplayName}%';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                UPDATE SoMePosts SET AutoText = REPLACE(AutoText, N'{EventNameLong}', N'{EventDisplayName}')
                 WHERE AutoText LIKE '%{EventNameLong}%';");

            migrationBuilder.Sql(@"
                UPDATE SoMePosts SET ManualTextOverride = REPLACE(ManualTextOverride, N'{EventNameLong}', N'{EventDisplayName}')
                 WHERE ManualTextOverride LIKE '%{EventNameLong}%';");

            migrationBuilder.Sql(@"
                UPDATE SoMeTemplates SET Body = REPLACE(Body, N'{EventNameLong}', N'{EventDisplayName}')
                 WHERE Body LIKE '%{EventNameLong}%';");
        }
    }
}
