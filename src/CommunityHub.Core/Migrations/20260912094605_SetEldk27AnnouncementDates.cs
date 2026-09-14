using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommunityHub.Core.Migrations
{
    /// <summary>
    /// 🔴 §1188 — THE ANNOUNCEMENT DATES HE DICTATED, APPLIED AS DATA.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-09-12: <i>"i need you to enter the datea, not me"</i> ·
    /// <i>"we need to get this delivered so the planned things that are wrong gets moved"</i> —
    /// after a sponsor post (Glueckkanja) was found scheduled for 15 September, months before the
    /// sponsor campaign is meant to open.</para>
    ///
    /// <para>🔑 <b>Why a migration rather than a script.</b> The recalculation that moves wrongly
    /// dated posts (§1183) has been live since this morning, but it measures against these settings —
    /// and with every field empty there is no "wrong" for it to act on. So the delivery IS the dates.
    /// The direct SQL path is refused in this environment, and the fields sit behind organizer login;
    /// a migration is the one route that can carry a value into production, and it has the properties
    /// you want anyway: version-controlled, reviewable in the diff, and applied exactly once.</para>
    ///
    /// <para>⚠️ <b>These are HIS values, transcribed — not defaults and not guesses:</b>
    /// master classes 14 Sep, other sessions 28 Sep (<i>"master class start date is a category of
    /// technical sessions … they runs fist starting from 14. sept. and other technical sessions …
    /// runs from 28. sept."</i>), tracks 28 Sep then early December then mid January
    /// (<i>"speaker tracks must have 3 rounds … second runs in early dec and third runs from mid jan
    /// 27"</i>), sponsors 15 Oct (<i>"i wants sponsors to start from oct 15"</i>), tiers 15 Dec and
    /// 15 Jan (<i>"round 1 runs from dec 15 and round 2 runs from jan 15"</i>).</para>
    ///
    /// <para>🔒 <b>Scoped to the ACTIVE edition only</b>, so a future edition starts from its own
    /// blank calendar — the evergreen rule (§1181: a date pinned to one edition is a trap for the
    /// next). And it touches only these columns: nothing else on the row is read or rewritten.</para>
    ///
    /// <para>⚠️ <b>Reversible and overridable.</b> Every value is editable on
    /// <c>/Organizer/SoMeSettings</c>; this sets a starting point, it does not take the fields away.
    /// The <c>Down</c> clears exactly what <c>Up</c> set.</para>
    ///
    /// <para>🔴 <b>The track cadence is raised to 3 in the same breath, and it has to be.</b>
    /// <c>Times()</c> reads a SAVED cadence row before the shipped default, so an edition whose
    /// posting-frequency page still says 2 would never plan round 3 and the January date would govern
    /// nothing — a setting that silently does nothing, which is the §1178 defect this batch exists to
    /// stop repeating.</para>
    /// </remarks>
    public partial class SetEldk27AnnouncementDates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 🔒 The active edition only. A JOIN rather than a hardcoded id: the id differs between
            // DEV and PROD, and an id written into a migration is the §1181 trap in another costume.
            migrationBuilder.Sql(@"
UPDATE s
   SET s.MasterClassAnnouncementFrom  = '2026-09-14',
       s.SessionAnnouncementFrom      = '2026-09-28',
       s.SpeakerAnnouncementFrom      = '2026-09-28',
       s.SpeakerTracksRound2From      = '2026-12-01',
       s.SpeakerTracksRound3From      = '2027-01-15',
       s.SponsorAnnouncementFrom      = '2026-10-15',
       s.SponsorCategoryRound1From    = '2026-12-15',
       s.SponsorCategoryRound2From    = '2027-01-15'
  FROM SoMeSettings s
  JOIN Events e ON e.Id = s.EventId
 WHERE e.IsActive = 1;");

            // §1185 — three rounds for tracks, or the January date governs nothing. Upsert: the row
            // may not exist, in which case the shipped default (3) already applies and nothing is
            // needed; where it DOES exist it may still say 2.
            migrationBuilder.Sql(@"
UPDATE c
   SET c.Occurrences = 3,
       c.Enabled = 1
  FROM SoMeCadenceSettings c
  JOIN Events e ON e.Id = c.EventId
 WHERE e.IsActive = 1
   AND c.Kind = 1   -- SoMeTemplateKind.SpeakerTracks. ⚠️ 1, not 0: the enum starts at 1, and a
                    -- wrong number here would update nothing and look like it had worked.
   AND c.Occurrences < 3;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // ⚠️ Clears exactly what Up set, on the same rows. It does NOT restore the previous
            // values — they were empty apart from the 7 September track date this replaced, and
            // guessing them back would be worse than leaving the fields blank for him to re-enter.
            migrationBuilder.Sql(@"
UPDATE s
   SET s.MasterClassAnnouncementFrom  = NULL,
       s.SessionAnnouncementFrom      = NULL,
       s.SpeakerAnnouncementFrom      = NULL,
       s.SpeakerTracksRound2From      = NULL,
       s.SpeakerTracksRound3From      = NULL,
       s.SponsorAnnouncementFrom      = NULL,
       s.SponsorCategoryRound1From    = NULL,
       s.SponsorCategoryRound2From    = NULL
  FROM SoMeSettings s
  JOIN Events e ON e.Id = s.EventId
 WHERE e.IsActive = 1;");
        }
    }
}
