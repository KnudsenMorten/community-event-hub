using CommunityHub.Core.Data;
using CommunityHub.Core.Settings;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// §879 — tells the ops mailbox, within ten minutes, that a speaker is <b>held from the Zoho flow</b>.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-05: <i>"speaker held in the queue. i need that to run every 10 min and be
/// notified after 10 min"</i>. This is one half of the mail §879 split in two; the other is
/// <see cref="VolunteersAwaitingReviewJob"/>, weekly, because a volunteer queue is a list to work
/// through and a held speaker is an incident.</para>
///
/// <para>🔑 <b>Ten minutes only works because of the content hash.</b> Without dedup this would mail
/// for as long as a speaker stays held — 144×/day — which is precisely how a real alert becomes
/// noise he stops reading (§609). The job compares a fingerprint of the held SET (who, and why)
/// against <c>JobRunState.LastContentHash</c> and speaks only when it changes. So: told within ten
/// minutes when someone becomes held, silence while nothing moves.</para>
///
/// <para>🔒 <b>This job is now the ONLY sender of that mail.</b> <c>SessionizeImportJob</c> used to
/// send its own copy the instant an import created someone (§304). Two senders with one hash would
/// have double-mailed every import — and §765 already learned that lesson the other way round: when
/// two callers can send the same mail, the one whose cadence the operator can see is not the one
/// deciding. The cost is a delay of at most ten minutes, which is exactly the ten minutes he asked
/// for.</para>
///
/// <para>⚠️ <b>The hash is CLEARED when nothing is held</b>, so the same speaker becoming held again
/// next month is news again rather than a suppressed repeat.</para>
/// </remarks>
public sealed class SpeakersHeldJob
{
    /// <summary>Unchanged from the mail this replaces — §595/§642: the KEY is a live DB row, only
    /// the display name moved when "digest" was banned.</summary>
    public const string FeatureKey = "digest-emails";

    private readonly CommunityHubDbContext _db;
    private readonly FeatureGateService _gate;
    private readonly CommunityHub.Core.Email.OrganizerReviewMailService _mail;
    private readonly ILogger<SpeakersHeldJob> _log;
    private readonly CommunityHub.Core.Diagnostics.JobActivityReporter? _activity;

    public SpeakersHeldJob(
        CommunityHubDbContext db, FeatureGateService gate,
        CommunityHub.Core.Email.OrganizerReviewMailService mail,
        ILogger<SpeakersHeldJob> log,
        CommunityHub.Core.Diagnostics.JobActivityReporter? activity = null)
    {
        _db = db; _gate = gate; _mail = mail; _log = log; _activity = activity;
    }

    // Base tick only (§510/§869.3) — the real cadence is the operator's interval on the Jobs page,
    // JobCatalog default 10, enforced in JobsPauseMiddleware.
    [Function("SpeakersHeldJob")]
    public async Task Run([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        var eventId = await _db.Events.Where(e => e.IsActive).Select(e => (int?)e.Id)
            .FirstOrDefaultAsync(ct);
        if (eventId is null)
        {
            _log.LogWarning("SpeakersHeldJob: no active event.");
            return;
        }

        if (!await _gate.AreAllEnabledAsync(
                eventId.Value, ct, FeatureKey, FeatureCatalog.OutboundEmailKey))
        {
            _activity?.ReportInactive(
                "Review e-mails (or outbound e-mail) are switched off, so nobody is being told when "
                + "a speaker is held from the Zoho flow.");
            return;
        }

        var held = await _mail.HeldSpeakersAsync(eventId.Value, ct);
        var state = await _db.JobRunStates.FirstOrDefaultAsync(s => s.FunctionName == "SpeakersHeldJob", ct);

        if (!held.Any)
        {
            // 🔑 The healthy state, and worth being able to CONFIRM on the Jobs page without opening
            // the queue — "nothing is held" and "this job is broken" must not look alike.
            _activity?.ReportInactive("No speaker is held from the Zoho flow, so no mail was sent.");

            // 🔒 Re-arm: clear the fingerprint so a future hold is news, not a suppressed repeat.
            if (state?.LastContentHash is not null)
            {
                state.LastContentHash = null;
                await _db.SaveChangesAsync(ct);
            }
            return;
        }

        if (state?.LastContentHash == held.Hash)
        {
            // Deliberately INACTIVE, not silence: the queue is not empty, it is unchanged. Those are
            // different facts and the page should be able to say which.
            _activity?.ReportInactive(
                $"The same {held.Count} speaker(s) are still held; already reported, so no repeat mail.");
            _log.LogInformation(
                "SpeakersHeldJob: {Count} held, unchanged since the last mail — silent.", held.Count);
            return;
        }

        _activity?.ReportWork();

        // Never throws: a failed ops mail must not take the job (or its cadence stamp) down.
        try
        {
            await _mail.SendHeldSpeakersAsync(held, ct);

            // 🔒 Stamped only AFTER the send is attempted. Stamping first would mean one transient
            // Brevo failure permanently swallowed the alert for that set — the failure mode this
            // whole job exists to prevent.
            if (state is not null)
            {
                state.LastContentHash = held.Hash;
                state.LastContentMailedAt = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(ct);
            }

            _log.LogInformation(
                "SpeakersHeldJob: mailed {Count} held speaker(s) — the set changed.", held.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "SpeakersHeldJob: send failed; the next run retries.");
        }
    }
}
