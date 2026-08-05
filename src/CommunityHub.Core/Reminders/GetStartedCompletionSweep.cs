using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Participants;
using CommunityHub.Forms;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// §746 — find everyone who has reached 100% and has not been reported yet, wherever they finished.
/// </summary>
/// <remarks>
/// <para>🔥 <b>Why this exists instead of more page hooks.</b> §720 hooked the completion notice into
/// <c>/Forms/Wizard</c> — and real people finished their last step on <c>/Forms/Hotel</c>,
/// <c>/Forms/Lunch</c>, <c>/Forms/Swag</c> and <c>/Forms/Dinner</c>, so no mail ever went out
/// (operator: *"3 have completed already. bug"*). Those standalone pages share no base class, so
/// wiring each one would work today and <b>break silently the next time a form page is added</b>.</para>
///
/// <para>🔒 <b>Completion is OBSERVED here, not caused.</b> Asking "who is at 100% with no ledger
/// row?" catches every path — the wizard, a standalone page, an organiser edit, a task reconciler,
/// and any page that does not exist yet. That is the property a call site can never have.</para>
///
/// <para>The notifier is idempotent (once-ever, keyed on <c>getstarted-complete:{pid}</c>), so
/// running this every 5 minutes re-checks freely and cannot double-send. The wizard hook stays in
/// place: it delivers instantly on the commonest path, and this closes every other one.</para>
/// </remarks>
public sealed class GetStartedCompletionSweep
{
    private readonly CommunityHubDbContext _db;
    private readonly GetStartedCompletionNotifier _notifier;
    private readonly SpeakerWizardService _speakerWizard;
    private readonly RoleWizardService _roleWizard;
    private readonly AttendeeWizardService _attendeeWizard;
    private readonly SponsorWizardService _sponsorWizard;

    public GetStartedCompletionSweep(
        CommunityHubDbContext db,
        GetStartedCompletionNotifier notifier,
        SpeakerWizardService speakerWizard,
        RoleWizardService roleWizard,
        AttendeeWizardService attendeeWizard,
        SponsorWizardService sponsorWizard)
    {
        _db = db;
        _notifier = notifier;
        _speakerWizard = speakerWizard;
        _roleWizard = roleWizard;
        _attendeeWizard = attendeeWizard;
        _sponsorWizard = sponsorWizard;
    }

    /// <summary>
    /// How far back to look for activity on a normal pass. Three times the 5-minute cadence, so a
    /// slow run or a missed tick cannot open a gap a completion falls through.
    /// </summary>
    public static readonly TimeSpan ActivityWindow = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Sweep for newly-complete participants.
    /// </summary>
    /// <param name="fullSweep">
    /// 🔑 <b>The cost control</b> (operator 2026-07-31: *"but dont impact performance necessary with
    /// this against sql"* — a fair objection, and the first version deserved it). A wizard build is
    /// several queries, so rebuilding one per participant every 5 minutes would be real load for a
    /// result that is almost always "nobody finished".
    ///
    /// <para><b>false (the 5-minute pass):</b> only participants who DID something in the last
    /// <see cref="ActivityWindow"/> are candidates — read from the audit trail, one indexed query.
    /// Normally that list is empty and the pass ends after two cheap reads with zero wizard builds.</para>
    ///
    /// <para><b>true (hourly):</b> every unreported participant is checked. This is the backstop for
    /// a completion caused by something that is not the person's own click — an organiser edit, a
    /// task reconciler, a job — where the audit actor is somebody else and the fast path would not
    /// see them. Cheap to run rarely; wrong to skip entirely.</para>
    /// </param>
    public async Task<int> RunAsync(
        int eventId, bool fullSweep = false, CancellationToken ct = default)
    {
        // Everyone already reported — one read. The notifier is idempotent anyway, but skipping
        // them here is what keeps the pass from rebuilding wizards for people long since done.
        var reported = (await _db.SentReminders.AsNoTracking()
                .Where(s => s.EventId == eventId
                            && s.ReminderType == GetStartedCompletionNotifier.ReminderType)
                .Select(s => s.OccasionKey)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var query = _db.Participants.AsNoTracking()
            // The same remindable rule the digest uses: someone who cannot sign in cannot have
            // completed anything worth reporting.
            .Remindable()
            .Where(p => p.EventId == eventId);

        if (!fullSweep)
        {
            // 🔒 The fast path. A completion is caused by a save, and every save is audited with
            // the actor's participant id — so "who could possibly have just finished?" is an
            // indexed lookup rather than a scan of everybody's wizard.
            var since = DateTime.UtcNow - ActivityWindow;
            var active = await _db.AuditEntries.AsNoTracking()
                .Where(a => a.EventId == eventId
                            && a.OccurredUtc >= since
                            && a.ActorParticipantId != null)
                .Select(a => a.ActorParticipantId!.Value)
                .Distinct()
                .ToListAsync(ct);

            if (active.Count == 0) return 0;      // the overwhelmingly common case: two reads, done
            query = query.Where(p => active.Contains(p.Id));
        }

        var people = await query.Select(p => new { p.Id, p.Role }).ToListAsync(ct);

        var sent = 0;
        foreach (var p in people)
        {
            if (reported.Contains(GetStartedCompletionNotifier.OccasionKeyFor(p.Id))) continue;

            var allDone = await IsCompleteAsync(eventId, p.Id, p.Role, ct);
            if (!allDone) continue;

            if (await _notifier.NotifyIfNewlyCompleteAsync(eventId, p.Id, allDone: true, ct))
                sent++;
        }

        return sent;
    }

    /// <summary>
    /// Is this person's wizard 100% complete? Uses the SAME wizard services the pages render from,
    /// so "complete" here can never drift from what the participant sees on their own progress bar.
    /// A role with no wizard is never complete — there is nothing to finish.
    /// </summary>
    private async Task<bool> IsCompleteAsync(
        int eventId, int participantId, ParticipantRole role, CancellationToken ct)
    {
        switch (role)
        {
            case ParticipantRole.Speaker:
                return (await _speakerWizard.BuildAsync(eventId, participantId, ct)).AllDone;

            case ParticipantRole.Attendee:
                return (await _attendeeWizard.BuildAsync(eventId, participantId, ct)).AllDone;

            case ParticipantRole.Sponsor:
            {
                // Sponsor is company-scoped; without a company there is no wizard to complete.
                var companyId = await _db.Participants.AsNoTracking()
                    .Where(x => x.Id == participantId)
                    .Select(x => x.SponsorCompanyId)
                    .FirstOrDefaultAsync(ct);
                if (string.IsNullOrWhiteSpace(companyId)) return false;
                return (await _sponsorWizard.BuildAsync(eventId, participantId, ct)).AllDone;
            }

            default:
                return (await _roleWizard.BuildAsync(eventId, participantId, ct)).AllDone;
        }
    }
}
