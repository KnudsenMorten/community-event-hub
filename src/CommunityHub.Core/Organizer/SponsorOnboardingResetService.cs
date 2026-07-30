using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Organizer;

/// <summary>What a sponsor reset actually cleared — shown to the organizer and written to audit.</summary>
/// <param name="Ok">False when the company could not be resolved for the edition, or nothing was selected.</param>
/// <param name="TasksReopened">Company-scoped sponsor tasks moved back to Open.</param>
/// <param name="BoothMembersRemoved">Booth-member rows deleted from the hub.</param>
/// <param name="OverviewCleared">The saved company description was cleared.</param>
/// <param name="WelcomesRearmed">Sponsor contacts whose welcome-with-login may send again.</param>
/// <param name="LedgerRowsRemoved">`SentReminder` rows deleted (mail may fire again).</param>
/// <param name="Detail">Human-readable summary for the organizer and the audit entry.</param>
/// <param name="Warnings">
/// Things the reset deliberately could NOT undo. Never empty-by-omission: if a deliverable is still
/// on file somewhere the hub does not own, it is named here — because the next sync pass will
/// re-close the very task this reset just re-opened, and a silent reset would look like a bug.
/// </param>
public sealed record SponsorResetResult(
    bool Ok,
    int TasksReopened,
    int BoothMembersRemoved,
    bool OverviewCleared,
    int WelcomesRearmed,
    int LedgerRowsRemoved,
    string Detail,
    IReadOnlyList<string> Warnings);

/// <summary>
/// §366 — the sponsor half of the §355 re-onboarding reset (operator 2026-07-26: <i>"the reset of
/// sponsors didn't reset correctly, as 2 tasks were not reset, one of them was initial onboarding of
/// sponsor + booth members"</i>, then <i>"does the organizer interface reset functionality fix this
/// for the future, so a reset handles everything"</i>).
///
/// <para><b>Why a sponsor needs its OWN service.</b> An attendee's onboarding hangs off a person;
/// a sponsor's hangs off a COMPANY. Sponsor tasks carry <c>SourceKey = "sponsor:{companyId}:…"</c>
/// with <b><c>AssignedParticipantId = NULL</c></b>, so every assignee-based query — including the
/// by-hand SQL reset and the attendee service's <c>ReopenTasksAsync</c> — misses them entirely.
/// That is precisely why "Initial onboarding of sponsor" and "Register booth members" survived.</para>
///
/// <para><b>The second, deeper trap: re-opening a task is not enough.</b>
/// <see cref="Integrations.SponsorOrderPullService"/> AUTO-CLOSES three sponsor tasks whenever the
/// deliverable they ask for is already on file (booth members present, sponsor-wall file seen,
/// company description saved). So a reset that only flipped tasks back to Open would be silently
/// undone on the next pull, and the operator would report the same bug again. Each switch here
/// therefore clears the BACKING DATA as well as the task.</para>
///
/// <para><b>Deliberately does NOT touch</b>: the <see cref="SponsorInfo"/> row itself, the package /
/// tier / booth, the logo, the contacts' logins or rings, and — importantly — <b>nothing in Zoho or
/// any other external system</b>. A reset is for re-testing onboarding; it must never reach out and
/// delete real people or real orders. Where that means the reset is incomplete, it says so via
/// <see cref="SponsorResetResult.Warnings"/> rather than pretending.</para>
/// </summary>
public sealed class SponsorOnboardingResetService
{
    /// <summary>
    /// The sponsor-wall upload is NOT resettable from here and this explains why in the UI. The
    /// "done" signal is a file the folder watcher SAW in SharePoint, so deleting the hub row just
    /// makes the hub forget — the watcher re-adds it on the next scan and the task re-closes.
    /// </summary>
    public const string WallUploadWarning =
        "Sponsor wall design: still on file — the 'done' signal is the uploaded file itself, seen in "
        + "SharePoint, so this reset cannot clear it. Remove the file in SharePoint if you need that "
        + "task to stay open; otherwise the next webshop pull will close it again.";

    private readonly CommunityHubDbContext _db;

    public SponsorOnboardingResetService(CommunityHubDbContext db) => _db = db;

    /// <summary>
    /// Reset the selected parts of one sponsor COMPANY's onboarding. Switches are independent —
    /// re-testing booth-member registration must not wipe the company description.
    /// </summary>
    /// <param name="resetAllTasks">
    /// The catch-all §366 fix: re-open EVERY <c>sponsor:{companyId}:*</c> task, including ad-hoc and
    /// tier-specific ones no individual switch knows about. Note the two data-backed tasks will
    /// re-close on the next pull unless their switch is ticked too — hence the warnings.
    /// </param>
    public async Task<SponsorResetResult> ResetAsync(
        int eventId,
        string sponsorCompanyId,
        bool resetWelcome,
        bool resetOverview,
        bool resetBoothMembers,
        bool resetAllTasks,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sponsorCompanyId))
        {
            return Failed("No sponsor company selected.");
        }

        var info = await _db.SponsorInfos
            .FirstOrDefaultAsync(s => s.EventId == eventId && s.SponsorCompanyId == sponsorCompanyId, ct);
        if (info is null)
        {
            return Failed("Sponsor company not found in this edition.");
        }

        if (!resetWelcome && !resetOverview && !resetBoothMembers && !resetAllTasks)
        {
            return Failed("Nothing selected — choose at least one thing to reset.");
        }

        var prefix = $"sponsor:{sponsorCompanyId}:";
        var parts = new List<string>();
        var warnings = new List<string>();
        var reopenKeys = new HashSet<string>(StringComparer.Ordinal);
        int booth = 0, welcomes = 0, ledger = 0;
        var overviewCleared = false;

        if (resetOverview)
        {
            // The auto-close signal is a NON-EMPTY CompanyDescription, so it has to go — flipping
            // the task alone would last exactly until the next webshop pull.
            overviewCleared = !string.IsNullOrWhiteSpace(info.CompanyDescription);
            info.CompanyDescription = null;
            reopenKeys.Add(prefix + "initial-onboarding-of-sponsor");
            parts.Add(overviewCleared
                ? "company overview cleared"
                : "company overview was already empty");
        }

        if (resetBoothMembers)
        {
            // HARD delete, not the usual DeletedAt tombstone. A tombstone is the right tool when a
            // sponsor removes a real person (it stops the Zoho pull resurrecting them); here the
            // intent is "as if never registered", and a tombstone would then BLOCK re-entering the
            // same address — turning a reset into a dead end.
            var members = await _db.SponsorBoothMembers
                .Where(m => m.EventId == eventId && m.SponsorCompanyId == sponsorCompanyId)
                .ToListAsync(ct);
            _db.SponsorBoothMembers.RemoveRange(members);
            booth = members.Count;
            reopenKeys.Add(prefix + "register-booth-members");
            parts.Add($"booth members removed from the hub ({booth})");

            // Honest warning: the members may also exist in Zoho, and SyncBoothMembersAsync pulls
            // them BACK into the hub, which re-closes the task. We do not delete them there — that
            // is an outward-facing, irreversible act and not something a reset should do silently.
            if (!string.IsNullOrWhiteSpace(info.ZohoExhibitorId) && booth > 0)
            {
                warnings.Add(
                    "Booth members: this company has a Zoho exhibitor record, so the next booth sync "
                    + "will re-import any members that still exist THERE — which re-closes the task. "
                    + "The reset deliberately does not delete people in Zoho; remove them in Zoho "
                    + "first if you need a truly clean run.");
            }
        }

        if (resetWelcome)
        {
            // Sponsor contacts are ordinary Participants tagged with the company, and the sponsor
            // welcome uses the same once-ever stamp as every other role (§360).
            var contacts = await _db.Participants
                .Where(p => p.EventId == eventId && p.SponsorCompanyId == sponsorCompanyId)
                .ToListAsync(ct);
            foreach (var c in contacts)
            {
                if (c.WelcomeWithLoginSentAt is null) continue;
                c.WelcomeWithLoginSentAt = null;
                welcomes++;
            }

            // §340-G-1: cleared by ADDRESS **or** by each contact's own `welcome:{participantId}`
            // occasion key. §326bf moved the SEND's idempotency to that key because an address is
            // not an identity — a typo fix, or a re-import that changes the case, leaves the row
            // under the OLD address where an address-only clear cannot see it. It would then delete
            // nothing, report success, and the re-send would find the surviving row and skip: a
            // reset that silently does nothing at all. Same defect fixed the same day in
            // `SponsorWelcomeEmailService` and `AttendeeOnboardingResetService`.
            var emails = contacts.Select(c => c.Email).Where(e => !string.IsNullOrWhiteSpace(e)).ToList();
            var welcomeKeys = contacts.Select(c => $"welcome:{c.Id}").ToList();
            if (welcomeKeys.Count > 0)
            {
                var rows = await _db.SentReminders
                    .Where(s => s.EventId == eventId
                                && s.ReminderType == "welcome"
                                && (emails.Contains(s.RecipientEmail)
                                    || welcomeKeys.Contains(s.OccasionKey)))
                    .ToListAsync(ct);
                _db.SentReminders.RemoveRange(rows);
                ledger += rows.Count;
            }
            parts.Add($"welcome re-armed for {welcomes} contact(s), {ledger} ledger row(s) cleared");
        }

        // Re-open: either the whole company (the §366 catch-all) or just the switched tasks.
        int tasks;
        if (resetAllTasks)
        {
            tasks = await ReopenAsync(t => t.SourceKey!.StartsWith(prefix), ct);
            parts.Add($"all company tasks re-opened ({tasks})");

            // The wall-upload task is in that sweep but cannot be made to STAY open from here.
            var wallOnFile = await _db.SponsorUploadFiles.AnyAsync(
                f => f.Location.EventId == eventId
                     && f.Location.SponsorCompanyId == sponsorCompanyId
                     && f.Location.FolderKey == "SPONSORWALL", ct);
            if (wallOnFile) warnings.Add(WallUploadWarning);

            // Same trap for the two data-backed tasks when their own switch was left off.
            if (!resetOverview && !string.IsNullOrWhiteSpace(info.CompanyDescription))
            {
                warnings.Add(
                    "Initial onboarding of sponsor: re-opened, but the company description is still "
                    + "saved, so the next webshop pull will close it again. Tick \"Company overview\" "
                    + "as well to make it stick.");
            }
            if (!resetBoothMembers)
            {
                var stillHasMembers = await _db.SponsorBoothMembers.AnyAsync(
                    m => m.EventId == eventId && m.SponsorCompanyId == sponsorCompanyId
                         && m.DeletedAt == null, ct);
                if (stillHasMembers)
                {
                    warnings.Add(
                        "Register booth members: re-opened, but booth members are still on file, so "
                        + "the next webshop pull will close it again. Tick \"Booth members\" as well "
                        + "to make it stick.");
                }
            }
        }
        else
        {
            tasks = reopenKeys.Count == 0
                ? 0
                : await ReopenAsync(t => reopenKeys.Contains(t.SourceKey!), ct);
            if (tasks > 0) parts.Add($"{tasks} task(s) re-opened");
        }

        await _db.SaveChangesAsync(ct);

        var detail = $"Reset sponsor onboarding for company {sponsorCompanyId}: "
                     + string.Join("; ", parts) + ".";
        return new SponsorResetResult(
            true, tasks, booth, overviewCleared, welcomes, ledger, detail, warnings);
    }

    private static SponsorResetResult Failed(string why) =>
        new(false, 0, 0, false, 0, 0, why, Array.Empty<string>());

    /// <summary>
    /// Move matching tasks back to Open. Clears <c>CompletedAt</c> (a Done time left behind keeps
    /// reading as finished in the §332 ratios) and re-stamps <c>CreatedAt</c> per §358, so the
    /// reminder cadence starts a full window from now instead of firing immediately.
    /// </summary>
    private async Task<int> ReopenAsync(
        System.Linq.Expressions.Expression<Func<ParticipantTask, bool>> match, CancellationToken ct)
    {
        var rows = await _db.Tasks
            .Where(t => t.SourceKey != null && t.State != TaskState.Open)
            .Where(match)
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        foreach (var t in rows)
        {
            t.State = TaskState.Open;
            t.CompletedAt = null;
            t.CreatedAt = now;
        }
        return rows.Count;
    }
}
