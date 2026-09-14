using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <summary>
    /// §1192 — the Call for Speakers close date, which §1188 left out.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-09-12: <i>"cfs date is missing - it is 31 aug 2026"</i>.</para>
    ///
    /// <para>⚠️ <b>Left out of §1188 because I read him wrongly.</b> Asked whether to update it he
    /// answered <i>"no cfs is done 31 aug"</i>, which I took as "no, leave it" — it meant "no [new
    /// date], it closed on 31 August". The field was empty, so the answer describing reality and the
    /// field holding it were two different things.</para>
    ///
    /// <para>🔴 <b>Empty is not harmless here.</b> §925.1: without a close date a track that has not
    /// RECEIVED its sessions scores the same as one that has FINISHED receiving them, so every track
    /// reads as settled and the whole speaker campaign becomes announceable at once. The Type 1 round
    /// dates now floor that, but the settle signal itself was blind.</para>
    ///
    /// <para>🔒 Active edition only, by JOIN — never a hardcoded id (§1188).</para>
    /// </remarks>
    public partial class SetCallForSpeakersCloseDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(@"
UPDATE s
   SET s.CallForSpeakersClosesOn = '2026-08-31'
  FROM SoMeSettings s
  JOIN Events e ON e.Id = s.EventId
 WHERE e.IsActive = 1;");

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(@"
UPDATE s
   SET s.CallForSpeakersClosesOn = NULL
  FROM SoMeSettings s
  JOIN Events e ON e.Id = s.EventId
 WHERE e.IsActive = 1;");
    }
}
