using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>Outcome of one sponsor-company welcome resend.</summary>
public sealed record SponsorWelcomeResult(
    string SponsorCompanyId,
    int CoordinatorsResolved,
    int Sent,
    int Skipped,
    bool Blocked = false,
    string? Reason = null);

/// <summary>
/// The sponsor-facing welcome/intro send + reset, honouring the universal
/// sponsor-email audience rule (REQUIREMENTS §7c): the welcome goes to every
/// EVENT-COORDINATOR contact of a sponsor company (signer-only excluded,
/// both-roles included, all coordinators), resolved by the shared
/// <see cref="SponsorRecipientResolver"/>. Wraps the existing idempotent
/// <see cref="WelcomeEmailService"/> (one welcome per participant, tracked in
/// <c>SentReminder</c> <c>ReminderType="welcome"</c>) so a resend only fires
/// after the welcome flag is reset.
///
/// Two organizer operations:
///   * <see cref="SendForCompanyAsync"/> — (re)send the welcome to a company's
///     coordinators (idempotent: already-welcomed coordinators are skipped, so
///     call <see cref="ResetForCompanyAsync"/> first for a true resend).
///   * <see cref="ResetForCompanyAsync"/> — delete the matching <c>welcome</c>
///     <c>SentReminder</c> rows so the next send actually goes out.
/// </summary>
public sealed class SponsorWelcomeEmailService
{
    private const string WelcomeReminderType = "welcome";

    private readonly CommunityHubDbContext _db;
    private readonly SponsorRecipientResolver _recipients;
    private readonly WelcomeEmailService _welcome;

    // §340-B: the shared §219 bulk-send pacer. Optional so existing constructions and every
    // test are unchanged (null ⇒ no delay, exactly as before); DI supplies the real one.
    private readonly Email.IBulkSendPacer? _pacer;

    public SponsorWelcomeEmailService(
        CommunityHubDbContext db,
        SponsorRecipientResolver recipients,
        WelcomeEmailService welcome,
        Email.IBulkSendPacer? pacer = null)
    {
        _db = db;
        _recipients = recipients;
        _welcome = welcome;
        _pacer = pacer;
    }

    /// <summary>
    /// Send the welcome to all coordinator contacts of one sponsor company.
    /// Idempotent per coordinator (the underlying <see cref="WelcomeEmailService"/>
    /// no-ops if that participant has already been welcomed) — so to force a
    /// resend, reset first via <see cref="ResetForCompanyAsync"/>. Returns counts
    /// of resolved coordinators, actual sends, and idempotent skips.
    /// </summary>
    public async Task<SponsorWelcomeResult> SendForCompanyAsync(
        int eventId, string sponsorCompanyId, CancellationToken ct = default)
    {
        // ⚰️ §816 — THE UPLOAD-FOLDER DEPENDENCY GUARD IS DELETED. DO NOT REINSTATE IT.
        //
        // It held every Gold+ booth company's welcome until a `SponsorUploadLocation` with an edit
        // link existed — correct in June, when each company got its own provisioned folder under
        // `/Sponsors/Sponsor Upload/` and welcoming earlier meant linking to a folder that did not
        // exist yet.
        //
        // 🔴 **§784.14 RETIRED THAT TREE** (operator 2026-08-03: *"That tree /Sponsors/Sponsor Upload
        // is retired and shouldn't be pre-created at all … retire the old logic and delete the old
        // folders"*), the folders were deleted in DEV and PROD, and the wall task no longer declares
        // an upload subfolder — so nothing writes those rows any more. **The guard was waiting for
        // an artifact of a retired mechanism, and could never clear.**
        //
        // ⚠️ WHAT IT COST, measured on a live sponsor (§816): Glueckkanja — Platinum, booth E-2 —
        // was pulled every 15 minutes, task-seeded (10 tasks), given a coordinator and provisioned
        // in Zoho, and its welcome was silently withheld. Nothing looked broken: the pull logged it,
        // the reconcile ran, the job reported success. The companies that PASS this guard hold
        // LEGACY rows from when the old method was live, which is exactly why the first sponsor
        // onboarded after the retirement was the first to be blocked.
        //
        // 🔑 §784.14 listed three things hanging off the provisioning loop (folder creation, the
        // edit links in task descriptions, the rows the watcher and deliverables read) — **this
        // guard was not one of them.** [[ceh-two-switch-trap]]: the upload model changed and the half
        // that DEPENDED on the old model was never told.
        //
        // 🔒 Logos and collateral now live in a FLAT structure (`Sponsors/Logo/Web`,
        // `Sponsors/Logo/Print`, …), which needs no per-company folder — so there is nothing left
        // for a welcome to wait for. If a future upload model ever needs preparing again, gate on
        // THAT model's own readiness signal, never on a row a retired service used to write.

        var coordinators = await _recipients.ResolveAsync(eventId, sponsorCompanyId, ct);
        int sent = 0, skipped = 0;
        foreach (var c in coordinators)
        {
            var didSend = await _welcome.SendWelcomeAsync(c.ParticipantId, ct);
            if (didSend)
            {
                sent++;
                // §340-B PACING: this is a bulk loop — SendForAllSponsorsAsync calls it once per
                // company, so a "resend to all sponsors" / the 15-min reconcile can walk every
                // coordinator of every company back to back. Paced AFTER an actual send, never
                // before the attempt: the overwhelming majority of iterations are idempotent
                // skips of already-welcomed coordinators, so an up-front delay would add
                // ~150 ms per coordinator every 15 minutes for nothing. Same shape as
                // WelcomeReconcileJob; the pacer is the shared §219 IBulkSendPacer.
                if (_pacer is not null) await _pacer.PaceAsync(ct);
            }
            else skipped++;
        }
        return new SponsorWelcomeResult(
            sponsorCompanyId, coordinators.Count, sent, skipped);
    }

    /// <summary>
    /// Send the welcome to every sponsor company's coordinators in the edition
    /// (each company resolved independently). Returns the aggregated per-company
    /// results. Used by the organizer "resend to all sponsors" action.
    /// </summary>
    public async Task<IReadOnlyList<SponsorWelcomeResult>> SendForAllSponsorsAsync(
        int eventId, CancellationToken ct = default)
    {
        var companyIds = await DistinctSponsorCompanyIdsAsync(eventId, ct);
        var results = new List<SponsorWelcomeResult>();
        foreach (var companyId in companyIds)
        {
            results.Add(await SendForCompanyAsync(eventId, companyId, ct));
        }
        return results;
    }

    /// <summary>
    /// Clear the <c>welcome</c> <see cref="SentReminder"/> rows for a sponsor
    /// company's coordinator contacts so a subsequent
    /// <see cref="SendForCompanyAsync"/> actually re-sends (the welcome send is
    /// otherwise once-ever per participant). Only the coordinators' welcome rows
    /// are removed — no other reminder type is touched. Returns the number of
    /// ledger rows deleted.
    /// </summary>
    public async Task<int> ResetForCompanyAsync(
        int eventId, string sponsorCompanyId, CancellationToken ct = default)
    {
        var coordinators = await _recipients.ResolveAsync(eventId, sponsorCompanyId, ct);
        if (coordinators.Count == 0) return 0;

        // §340-G-1: keyed on the OCCASION KEY, exactly as the send is — not on the address.
        //
        // §326bf moved the SEND's idempotency to `welcome:{participantId}`, because an address is
        // not an identity: correcting a typo, a re-import that changes the case, or a Sessionize
        // update made a stored row unmatchable. This reset was left matching `RecipientEmail`, so
        // the two halves drifted — and silently, in the worse direction. A coordinator whose
        // address changed since their welcome has a ledger row under the OLD address, so the reset
        // matched nothing, deleted nothing, and returned "0 rows" as if there had been nothing to
        // do; the follow-up re-send then found the row still present and skipped. "Reset, then
        // resend" would do nothing at all, forever, with no error anywhere — the same shape of
        // failure as §401, and the reason a reset must be keyed identically to the thing it resets.
        //
        // The send resolves each participant from THIS list, so keying the reset the same way makes
        // the pair correct by construction rather than by both happening to agree today.
        var occasionKeys = coordinators.Select(c => $"welcome:{c.ParticipantId}").ToList();
        var rows = await _db.SentReminders
            .Where(s => s.EventId == eventId
                        && s.ReminderType == WelcomeReminderType
                        && occasionKeys.Contains(s.OccasionKey))
            .ToListAsync(ct);
        if (rows.Count == 0) return 0;

        _db.SentReminders.RemoveRange(rows);
        await _db.SaveChangesAsync(ct);
        return rows.Count;
    }

    /// <summary>Clear the welcome ledger for ALL sponsor companies' coordinators in the edition.</summary>
    public async Task<int> ResetForAllSponsorsAsync(
        int eventId, CancellationToken ct = default)
    {
        var companyIds = await DistinctSponsorCompanyIdsAsync(eventId, ct);
        int total = 0;
        foreach (var companyId in companyIds)
        {
            total += await ResetForCompanyAsync(eventId, companyId, ct);
        }
        return total;
    }

    private async Task<List<string>> DistinctSponsorCompanyIdsAsync(
        int eventId, CancellationToken ct) =>
        await _db.Participants
            .Where(p => p.EventId == eventId
                        && p.Role == ParticipantRole.Sponsor
                        && p.SponsorCompanyId != null
                        && p.SponsorCompanyId != "")
            .Select(p => p.SponsorCompanyId!)
            .Distinct()
            .ToListAsync(ct);
}
