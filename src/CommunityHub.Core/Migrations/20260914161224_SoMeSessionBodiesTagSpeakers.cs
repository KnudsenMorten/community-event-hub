using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <summary>
    /// §1224 — every stored SESSION body gets the speaker tag line <c>🎤 With {Speakers}</c>.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-09-14: <i>"i found a bug in the master class some publishing today, as the
    /// {speakers} were not included, so none of the speakers were tagged. fix that. also verify that
    /// other technical sessions include tagging of speakers. update the template + the planned/scheduled
    /// posts that exist"</i>.</para>
    ///
    /// <para>🔑 Measured on PROD first: the shipped Type 2 template, all 38 imported session wordings
    /// (<c>SoMeBodySamples</c>, Kind 2) and all 18 unpublished session posts carried NO speaker token —
    /// so master classes, panels and technical sessions alike announced nobody. The line goes directly
    /// under <c>{IntroText}</c>, the one token every one of those bodies has exactly once.</para>
    ///
    /// <para>🔒 Scope: session wordings, session template overrides, and session posts that have NOT
    /// published (<c>Status = 0</c>, not deleted) — both text columns, since <c>ManualTextOverride</c>
    /// wins when present. A published post is a record of what went out and is left alone.</para>
    ///
    /// <para>🔒 Idempotent: a body that already mentions <c>{Speakers}</c> is skipped. The separator
    /// follows the body's own line endings so a CRLF body does not gain a lone LF.</para>
    ///
    /// <para>⚠️ <c>{Speakers}</c> is a REQUIRED variable (SoMeEmptyVariableGate): a session post whose
    /// speakers cannot be resolved is now held from publishing instead of naming nobody.</para>
    /// </remarks>
    public partial class SoMeSessionBodiesTagSpeakers : Migration
    {
        private const string Line = "N'🎤 With {Speakers}'";

        private static string Insert(string column) =>
            $"REPLACE({column}, N'{{IntroText}}', N'{{IntroText}}' "
            + $"+ CASE WHEN CHARINDEX(CHAR(13), {column}) > 0 THEN CHAR(13)+CHAR(10)+CHAR(13)+CHAR(10) ELSE CHAR(10)+CHAR(10) END "
            + $"+ {Line})";

        private static string Remove(string column) =>
            $"REPLACE(REPLACE({column}, CHAR(13)+CHAR(10)+CHAR(13)+CHAR(10) + {Line}, N''), CHAR(10)+CHAR(10) + {Line}, N'')";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($@"
                UPDATE SoMeBodySamples SET Body = {Insert("Body")}
                 WHERE Kind = 2 AND Body LIKE N'%{{IntroText}}%' AND Body NOT LIKE N'%{{Speakers}}%';");

            migrationBuilder.Sql($@"
                UPDATE SoMeTemplates SET Body = {Insert("Body")}
                 WHERE Kind = 2 AND Body LIKE N'%{{IntroText}}%' AND Body NOT LIKE N'%{{Speakers}}%';");

            migrationBuilder.Sql($@"
                UPDATE SoMePosts SET AutoText = {Insert("AutoText")}
                 WHERE TemplateKind = 2 AND Status = 0 AND IsDeleted = 0
                   AND AutoText LIKE N'%{{IntroText}}%' AND AutoText NOT LIKE N'%{{Speakers}}%';");

            migrationBuilder.Sql($@"
                UPDATE SoMePosts SET ManualTextOverride = {Insert("ManualTextOverride")}
                 WHERE TemplateKind = 2 AND Status = 0 AND IsDeleted = 0
                   AND ManualTextOverride LIKE N'%{{IntroText}}%' AND ManualTextOverride NOT LIKE N'%{{Speakers}}%';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"UPDATE SoMeBodySamples SET Body = {Remove("Body")} WHERE Kind = 2;");
            migrationBuilder.Sql($"UPDATE SoMeTemplates SET Body = {Remove("Body")} WHERE Kind = 2;");
            migrationBuilder.Sql($"UPDATE SoMePosts SET AutoText = {Remove("AutoText")} WHERE TemplateKind = 2 AND Status = 0;");
            migrationBuilder.Sql(
                $"UPDATE SoMePosts SET ManualTextOverride = {Remove("ManualTextOverride")} "
                + "WHERE TemplateKind = 2 AND Status = 0 AND ManualTextOverride IS NOT NULL;");
        }
    }
}
