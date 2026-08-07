using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <summary>
    /// §935 — rewrite <c>{OrganizerLinkedInUrls}</c> to <c>{Organizers}</c> in stored post bodies.
    /// </summary>
    /// <remarks>
    /// <para>🔑 Operator 2026-08-07, after sweeping the queue himself: <i>"some refer to some other
    /// {OrganizerLinkedUrl} link, which must be changed to {Organizer}"</i>. Measured before writing
    /// this: <b>75 of 76</b> unpublished posts carried the long spelling.</para>
    ///
    /// <para>🔑 <b>They are the same value.</b> <c>SoMePostComposer</c> maps both tokens to the
    /// organizer credit — his spelling was added alongside the catalog's original one rather than
    /// replacing it. So no post's published text moves by a character; what goes away is a body that
    /// names one thing two ways.</para>
    ///
    /// <para>🔴 <b>The template is fixed in the same change, and that is the point.</b> Every one of
    /// those 75 posts got the long spelling from <c>SoMeTemplateCatalog.Footer</c>. Rewriting the
    /// data alone would have looked like a fix until the next post was composed — the §933.2 shape,
    /// where the data half is safe but incomplete on its own.</para>
    ///
    /// <para>🔒 <c>{OrganizerLinkedInUrls}</c> is NOT retired: still resolved, still offered. He
    /// asked for the posts to be changed, not for the token to be removed, and §933.1 is the standing
    /// reminder that retiring a name is a separate decision with its own risks.</para>
    ///
    /// <para>⚠️ Published rows are swept too — an equivalent token cannot change what a published
    /// post rendered, so the hub cannot end up disagreeing with LinkedIn. Idempotent and reversible.</para>
    /// </remarks>
    public partial class StandardiseOrganizerTokenInStoredPosts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                UPDATE SoMePosts SET AutoText = REPLACE(AutoText, N'{OrganizerLinkedInUrls}', N'{Organizers}')
                 WHERE AutoText LIKE '%{OrganizerLinkedInUrls}%';");

            migrationBuilder.Sql(@"
                UPDATE SoMePosts SET ManualTextOverride = REPLACE(ManualTextOverride, N'{OrganizerLinkedInUrls}', N'{Organizers}')
                 WHERE ManualTextOverride LIKE '%{OrganizerLinkedInUrls}%';");

            migrationBuilder.Sql(@"
                UPDATE SoMeTemplates SET Body = REPLACE(Body, N'{OrganizerLinkedInUrls}', N'{Organizers}')
                 WHERE Body LIKE '%{OrganizerLinkedInUrls}%';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                UPDATE SoMePosts SET AutoText = REPLACE(AutoText, N'{Organizers}', N'{OrganizerLinkedInUrls}')
                 WHERE AutoText LIKE '%{Organizers}%';");

            migrationBuilder.Sql(@"
                UPDATE SoMePosts SET ManualTextOverride = REPLACE(ManualTextOverride, N'{Organizers}', N'{OrganizerLinkedInUrls}')
                 WHERE ManualTextOverride LIKE '%{Organizers}%';");

            migrationBuilder.Sql(@"
                UPDATE SoMeTemplates SET Body = REPLACE(Body, N'{Organizers}', N'{OrganizerLinkedInUrls}')
                 WHERE Body LIKE '%{Organizers}%';");
        }
    }
}
