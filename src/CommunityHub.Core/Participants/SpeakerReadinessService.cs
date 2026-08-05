using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Entitlements;
using CommunityHub.Forms;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Participants;

/// <summary>
/// Builds a speaker's <see cref="SpeakerReadiness"/> "am I ready?" rollup
/// (REQUIREMENTS §134) from EXISTING data only — it is a read-only AGGREGATOR, never a
/// new source of truth. It resolves one <see cref="ReadinessSignal"/> per readiness
/// check, then hands them to the pure <see cref="SpeakerReadinessCalculator"/> for the
/// score + the what's-missing split.
///
/// <para>Signal sourcing (each reuses the mechanism the speaker form / wizard already
/// uses, so behaviour stays consistent):</para>
/// <list type="bullet">
///   <item><b>details</b> — the speaker has edited their own Speaker Details / bio:
///   <see cref="SpeakerProfile.BioLastEditedBySpeakerAt"/> is set (the "speaker acted"
///   marker, same P13 signal as <c>SpeakerWizardService</c> — a Sessionize import never
///   sets it, so an imported-but-untouched bio does not count).</item>
///   <item><b>headshot</b> — <see cref="SpeakerProfile.PhotoUrl"/> or
///   <see cref="SpeakerProfile.PhotoSharePointPath"/> is present.</item>
///   <item><b>hotel</b> — applicable only when entitled to <see cref="OrderItem.Hotel"/>
///   (via <see cref="OrderEntitlements"/>, the same gate the Hotel form uses); done when
///   a <see cref="HotelBooking"/> exists.</item>
///   <item><b>dinner</b> — applicable only when entitled to
///   <see cref="OrderItem.AppreciationDinner"/>; done when a <see cref="DinnerSignup"/>
///   exists.</item>
///   <item><b>upload-preview / upload-final</b> — the two §120 presentation-upload
///   speaker-deadline tasks (<c>speakerdl:{pid}:upload-preview-presentation</c> /
///   <c>…:upload-final-presentation</c>); done when the task is marked Done (these carry
///   no form-data signal, so the manual mark-done IS the signal).</item>
///   <item><b>tasks</b> — every OTHER assigned to-do (Signal join, promote, code of
///   conduct, swag/lunch/travel deadlines, …) is complete. ⚠️ <b>APPLICABLE ONLY WHEN THE
///   SPEAKER ACTUALLY HAS SUCH A TASK</b> (§784.9(d)): with none, there is nothing to
///   report and the item is dropped, rather than counting as done for a speaker who has
///   done nothing. The label carries the open/total counts.</item>
/// </list>
///
/// <para>⚰️ <b>§784.9(a) — there is deliberately NO "Master Class prep notes" signal.</b> The
/// operator: it is <i>"an optional service, not a task"</i>. Readiness answers "has this speaker
/// done what we NEED from them?", and declining an option is not un-readiness — counting it held
/// master-class speakers permanently below 100% and pushed them up a roster sorted by who needs
/// chasing. Do not re-add it.</para>
///
/// <para>There is intentionally NO "A/V form" signal: this edition has no audio/visual
/// form (no such model/table), and §134 forbids adding a new source of truth. The day a
/// real A/V form lands, add one <see cref="ReadinessSignal"/> here — the calculator and
/// both views pick it up automatically.</para>
///
/// <para>Read-only: this service never writes. A surface that wants task auto-completion
/// from already-submitted form data should run <see cref="FormTaskReconciler"/> BEFORE
/// reading (the speaker hub already does); the organizer roster reads a live snapshot
/// without mutating anyone's tasks.</para>
/// </summary>
public sealed class SpeakerReadinessService
{
    private readonly CommunityHubDbContext _db;
    private readonly SignalGroupsProvider? _signal;
    private readonly Core.Content.WelcomeCopyStore? _welcome;

    /// <remarks>
    /// §784.9(c) — <paramref name="signal"/> and <paramref name="welcome"/> are the SAME two
    /// optional providers <see cref="SpeakerWizardService"/> takes, and for the same reason: they
    /// decide whether the Signal / Welcome Get-Started steps exist at all. Optional + last so a
    /// host that has not wired them still resolves this service (the steps simply do not appear),
    /// which is the pattern the wizard already established.
    /// </remarks>
    public SpeakerReadinessService(
        CommunityHubDbContext db,
        SignalGroupsProvider? signal = null,
        Core.Content.WelcomeCopyStore? welcome = null)
    {
        _db = db;
        _signal = signal;
        _welcome = welcome;
    }

    // The §120 presentation-upload deadline slugs (must match SpeakerDeadlineSeeder's
    // Slug() of the config titles, and SpeakerWizardService's keys).
    public const string PreviewTaskSlug = "upload-preview-presentation";
    public const string FinalTaskSlug = "upload-final-presentation";

    // Fix-it deep links (kept here so both views stay consistent).
    private const string DetailsLink = "/Speaker/Details";
    private const string HotelLink = "/Forms/Hotel";
    private const string DinnerLink = "/Forms/Dinner";
    private const string TasksLink = "/Speaker/Tasks";
    private const string HubLink = "/Speaker";

    /// <summary>
    /// Readiness for ONE speaker. Returns null when the participant does not exist in the
    /// edition or has no <see cref="SpeakerProfile"/> (i.e. is not a speaker).
    /// </summary>
    public async Task<SpeakerReadiness?> BuildForSpeakerAsync(
        int eventId, int participantId, CancellationToken ct = default)
    {
        var participant = await _db.Participants
            .FirstOrDefaultAsync(p => p.Id == participantId && p.EventId == eventId, ct);
        if (participant is null) return null;

        var profile = await _db.SpeakerProfiles
            .FirstOrDefaultAsync(sp => sp.EventId == eventId && sp.ParticipantId == participantId, ct);
        if (profile is null) return null;

        var overrides = await _db.ParticipantOrderOverrides
            .Where(o => o.EventId == eventId && o.ParticipantId == participantId)
            .ToListAsync(ct);
        // §299 C5: entitlement days derive from the speaker's linked sessions.
        var days = await SpeakerDayScope.DaysForSpeakerAsync(_db, eventId, participantId, ct);
        var entitled = OrderEntitlements.Effective(participant, profile, days, overrides);

        var hotelDone = entitled.Contains(OrderItem.Hotel)
            && await _db.HotelBookings.AnyAsync(
                h => h.EventId == eventId && h.ParticipantId == participantId, ct);
        var dinnerDone = entitled.Contains(OrderItem.AppreciationDinner)
            && await _db.DinnerSignups.AnyAsync(
                d => d.EventId == eventId && d.ParticipantId == participantId, ct);

        // ALL of the speaker's assigned tasks (tagged + untagged) — drives the two
        // upload items and the "other to-dos" catch-all.
        var tasks = (await _db.Tasks
                .Where(t => t.EventId == eventId && t.AssignedParticipantId == participantId)
                .Select(t => new { t.SourceKey, t.State })
                .ToListAsync(ct))
            .Select(t => new TaskRow(t.SourceKey, t.State))
            .ToList();

        // §784.9(c) — the Get-Started steps, composed by the WIZARD's own rule.
        var facts = new SpeakerWizardFacts(
            Entitled: entitled,
            WelcomeConfigured: _welcome?.Exists(ParticipantRole.Speaker) == true,
            CalendarSet: profile.CalendarEmailSetAt != null,
            DetailsEdited: profile.BioLastEditedBySpeakerAt != null,
            HotelBooked: hotelDone,
            DinnerSignedUp: dinnerDone,
            SwagSaved: await _db.SwagPreferences.AnyAsync(
                s => s.EventId == eventId && s.ParticipantId == participantId, ct),
            LunchSignedUp: await _db.LunchSignups.AnyAsync(
                l => l.EventId == eventId && l.ParticipantId == participantId, ct),
            SignalInScope: _signal?.InScope(ParticipantRole.Speaker) == true,
            SignalTaskDone: tasks.Any(t => t.State == TaskState.Done
                                           && t.SourceKey == WizardStepTasks.Signal(participantId)),
            PartyRsvped: await _db.PartyRsvps.AnyAsync(
                r => r.EventId == eventId && r.ParticipantId == participantId, ct),
            PolicyAccepted: await _db.ParticipantPolicyAcceptances.AnyAsync(
                a => a.EventId == eventId && a.ParticipantId == participantId, ct),
            // Not a readiness signal — the "deadlines" step is informational and always Done, and
            // it is filtered out below anyway. Passed false so it is not even composed.
            HasOutsideWizardTask: false);

        // ⚰️ §784.9(a) — the master-class prep queries are GONE with the signal they fed. Leaving
        // them would have meant two round-trips per speaker to compute a value nothing reads.
        var signals = BuildSignals(
            participantId, profile, entitled, hotelDone, dinnerDone, tasks, facts);

        return SpeakerReadinessCalculator.Compute(
            participantId, participant.FullName, participant.Email, signals);
    }

    /// <summary>
    /// Readiness for EVERY speaker in the edition (organizer roster), sorted lowest
    /// readiness first (the people who need chasing), then by name. Batch-loaded — no
    /// per-speaker round-trips.
    /// </summary>
    public async Task<IReadOnlyList<SpeakerReadiness>> BuildRosterAsync(
        int eventId, CancellationToken ct = default)
    {
        var profiles = await _db.SpeakerProfiles
            .Where(s => s.EventId == eventId)
            .ToListAsync(ct);
        if (profiles.Count == 0) return Array.Empty<SpeakerReadiness>();

        var ids = profiles.Select(p => p.ParticipantId).ToHashSet();

        var participants = await _db.Participants
            .Where(p => p.EventId == eventId && ids.Contains(p.Id))
            .ToListAsync(ct);
        var byId = participants.ToDictionary(p => p.Id);

        var overridesByPid = (await _db.ParticipantOrderOverrides
                .Where(o => o.EventId == eventId && ids.Contains(o.ParticipantId))
                .ToListAsync(ct))
            .GroupBy(o => o.ParticipantId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ParticipantOrderOverride>)g.ToList());

        var hotelPids = (await _db.HotelBookings
                .Where(h => h.EventId == eventId && ids.Contains(h.ParticipantId))
                .Select(h => h.ParticipantId).Distinct().ToListAsync(ct))
            .ToHashSet();
        var dinnerPids = (await _db.DinnerSignups
                .Where(d => d.EventId == eventId && ids.Contains(d.ParticipantId))
                .Select(d => d.ParticipantId).Distinct().ToListAsync(ct))
            .ToHashSet();

        var tasksByPid = (await _db.Tasks
                .Where(t => t.EventId == eventId
                            && t.AssignedParticipantId != null
                            && ids.Contains(t.AssignedParticipantId.Value))
                .Select(t => new { Pid = t.AssignedParticipantId!.Value, t.SourceKey, t.State })
                .ToListAsync(ct))
            .GroupBy(t => t.Pid)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<TaskRow>)g.Select(t => new TaskRow(t.SourceKey, t.State)).ToList());

        // §299 C5: derived presenting days per speaker (drives main-day lunch
        // entitlement), loaded once for the roster.
        var daysBySpeaker = await SpeakerDayScope.DaysBySpeakerAsync(_db, eventId, ct);

        // §784.9(c) — the Get-Started facts, BATCH-loaded. ⚠️ One set query each, never one per
        // speaker: the roster is the whole point of this method and it already avoids per-speaker
        // round-trips for everything else. The composition itself is the wizard's
        // (SpeakerWizardService.ComposeSteps), so the organizer and the speaker cannot disagree
        // about which steps exist.
        var swagPids = (await _db.SwagPreferences
                .Where(s => s.EventId == eventId && ids.Contains(s.ParticipantId))
                .Select(s => s.ParticipantId).Distinct().ToListAsync(ct))
            .ToHashSet();
        var lunchPids = (await _db.LunchSignups
                .Where(l => l.EventId == eventId && ids.Contains(l.ParticipantId))
                .Select(l => l.ParticipantId).Distinct().ToListAsync(ct))
            .ToHashSet();
        // ⚠️ PartyRsvp.ParticipantId is NULLABLE — a public party RSVP carries no participant at
        // all. Only the stamped ones can belong to a speaker, so the null rows are filtered out
        // here rather than being coerced into a speaker's row.
        var partyPids = (await _db.PartyRsvps
                .Where(r => r.EventId == eventId
                            && r.ParticipantId != null
                            && ids.Contains(r.ParticipantId.Value))
                .Select(r => r.ParticipantId!.Value).Distinct().ToListAsync(ct))
            .ToHashSet();
        var acceptedPids = (await _db.ParticipantPolicyAcceptances
                .Where(a => a.EventId == eventId && ids.Contains(a.ParticipantId))
                .Select(a => a.ParticipantId).Distinct().ToListAsync(ct))
            .ToHashSet();

        var welcomeConfigured = _welcome?.Exists(ParticipantRole.Speaker) == true;
        var signalInScope = _signal?.InScope(ParticipantRole.Speaker) == true;

        // ⚰️ §784.9(a) — the two master-class set-building queries are GONE with the signal.

        var result = new List<SpeakerReadiness>(profiles.Count);
        foreach (var profile in profiles)
        {
            var pid = profile.ParticipantId;
            if (!byId.TryGetValue(pid, out var participant)) continue; // orphan profile

            var overrides = overridesByPid.TryGetValue(pid, out var ov)
                ? ov : Array.Empty<ParticipantOrderOverride>();
            var days = daysBySpeaker.TryGetValue(pid, out var dd) ? dd : SpeakerDays.None;
            var entitled = OrderEntitlements.Effective(participant, profile, days, overrides);

            var hotelDone = entitled.Contains(OrderItem.Hotel) && hotelPids.Contains(pid);
            var dinnerDone = entitled.Contains(OrderItem.AppreciationDinner) && dinnerPids.Contains(pid);

            var taskRows = tasksByPid.TryGetValue(pid, out var tl) ? tl : Array.Empty<TaskRow>();

            var facts = new SpeakerWizardFacts(
                Entitled: entitled,
                WelcomeConfigured: welcomeConfigured,
                CalendarSet: profile.CalendarEmailSetAt != null,
                DetailsEdited: profile.BioLastEditedBySpeakerAt != null,
                HotelBooked: hotelDone,
                DinnerSignedUp: dinnerDone,
                SwagSaved: swagPids.Contains(pid),
                LunchSignedUp: lunchPids.Contains(pid),
                SignalInScope: signalInScope,
                SignalTaskDone: taskRows.Any(t => t.State == TaskState.Done
                                                  && t.SourceKey == WizardStepTasks.Signal(pid)),
                PartyRsvped: partyPids.Contains(pid),
                PolicyAccepted: acceptedPids.Contains(pid),
                HasOutsideWizardTask: false);   // informational step, never a readiness signal

            var signals = BuildSignals(pid, profile, entitled, hotelDone, dinnerDone, taskRows, facts);

            result.Add(SpeakerReadinessCalculator.Compute(
                pid, participant.FullName, participant.Email, signals));
        }

        return result
            .OrderBy(r => r.Percent)
            .ThenBy(r => r.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private readonly record struct TaskRow(string? SourceKey, TaskState State);

    /// <summary>
    /// Assemble the ordered signal list for one speaker from already-resolved inputs.
    /// Pure (no DB) so both the single + roster paths share identical logic.
    /// </summary>
    /// <summary>
    /// §784.9(c) — the Get-Started steps this view SHOWS, and the words it shows them in.
    /// </summary>
    /// <remarks>
    /// <para>🔒 <b>Three wizard steps are deliberately NOT here, and each for its own reason:</b></para>
    /// <list type="bullet">
    ///   <item><c>details</c>, <c>hotel</c>, <c>dinner</c> — readiness already has its own signal
    ///   for each, resolved from the same data. Adding the wizard's copy would print the row
    ///   twice and count it twice in the denominator.</item>
    ///   <item><c>welcome</c> and <c>deadlines</c> — read-only summary steps that are ALWAYS Done
    ///   because they ask the speaker for nothing. Counting them would hand every speaker free
    ///   completion, which is precisely the §784.9(d) defect that made this column untrustworthy
    ///   in the first place.</item>
    /// </list>
    /// <para>⚠️ A step present in the wizard but absent from this map is silently not shown. That
    /// is the intended behaviour for the five above; for anything new it is a bug, so add the
    /// label here when a Get-Started step is added.</para>
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> GetStartedLabels =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["calendar"] = "Calendar email step completed",
            ["swag"] = "Swag / gift preferences",
            ["lunch"] = "Lunch sign-up",
            ["signal"] = "Signal groups joined",
            ["party"] = "Party sign-up",
            ["accept"] = "Code of Conduct & privacy accepted",
        };

    private static List<ReadinessSignal> BuildSignals(
        int participantId,
        SpeakerProfile profile,
        IReadOnlySet<OrderItem> entitled,
        bool hotelDone,
        bool dinnerDone,
        IReadOnlyList<TaskRow> tasks,
        SpeakerWizardFacts wizardFacts)
    {
        var previewKey = $"speakerdl:{participantId}:{PreviewTaskSlug}";
        var finalKey = $"speakerdl:{participantId}:{FinalTaskSlug}";

        bool TaskDone(string key) =>
            tasks.Any(t => t.State == TaskState.Done
                           && string.Equals(t.SourceKey, key, StringComparison.Ordinal));

        // 🔴 §784.9(d) — THE DEFECT THAT MADE THE WHOLE COLUMN UNTRUSTWORTHY.
        //
        // This used to read `!tasks.Any(open && not-an-upload)`, which is TRUE for a speaker with no
        // tasks at all — so a speaker who had never logged in, and for whom nothing had been seeded,
        // scored "1 of 8 done". Something counted as complete that nobody had done. Once one item
        // can be vacuously true, no number in the column can be taken at face value.
        //
        // 🔒 The fix is APPLICABILITY, not a flipped boolean: with no other to-dos there is nothing
        // to report, so the item is dropped entirely (the calculator ignores non-applicable signals)
        // and the denominator shrinks honestly. Forcing Done=false instead would invent a piece of
        // outstanding work that does not exist — the opposite lie.
        var otherTasks = tasks
            .Where(t => !string.Equals(t.SourceKey, previewKey, StringComparison.Ordinal)
                        && !string.Equals(t.SourceKey, finalKey, StringComparison.Ordinal))
            .ToList();

        var otherTasksApplicable = otherTasks.Count > 0;
        var otherTasksOpen = otherTasks.Count(t => t.State != TaskState.Done);
        var otherTasksDone = otherTasksApplicable && otherTasksOpen == 0;

        // §784.9(b) — "Other to-dos complete" told the reader nothing: not what they are, not how
        // many are left. The label now carries the count, which is the fact an organizer chasing a
        // speaker actually needs.
        var otherTasksLabel = otherTasksOpen == 0
            ? $"Other to-dos ({otherTasks.Count} done)"
            : $"Other to-dos ({otherTasksOpen} of {otherTasks.Count} still open)";

        var detailsDone = profile.BioLastEditedBySpeakerAt != null;
        var headshotDone =
            !string.IsNullOrWhiteSpace(profile.PhotoUrl)
            || !string.IsNullOrWhiteSpace(profile.PhotoSharePointPath);

        // 🔴 §784.9(c) — THE GET-STARTED STEPS, WHICH WERE MISSING ENTIRELY.
        //
        // Operator 2026-08-03: *"I am also missing the get started onboarding steps in the view
        // (only a view are shown, but some are not shown like party or lunch sign-up)"*. Readiness
        // was built from the TASK table plus a handful of forms, and the Get-Started wizard is
        // neither — so an organizer chasing a speaker could not see the very steps the speaker is
        // being chased about.
        //
        // 🔒 Composed by SpeakerWizardService.ComposeSteps — the same function that renders the
        // speaker's own wizard. Not a second query over the same tables: two independent
        // definitions of "which steps does this speaker have" would agree on the day they were
        // written and quietly disagree afterwards.
        //
        // ⚠️ Applicability rides along for free: the wizard only EMITS a step the speaker is
        // entitled to, so a speaker with no lunch entitlement gets no lunch row rather than a row
        // marked incomplete for something never offered to them.
        var getStarted = SpeakerWizardService.ComposeSteps(wizardFacts)
            .Where(s => GetStartedLabels.ContainsKey(s.Key))
            .Select(s => new ReadinessSignal(
                $"getstarted:{s.Key}", GetStartedLabels[s.Key], true, s.Done, s.Route))
            .ToList();

        return new List<ReadinessSignal>
        {
            new("details",        "Speaker details & bio",         true,                                            detailsDone,          DetailsLink),
            new("headshot",       "Headshot photo",                true,                                            headshotDone,         DetailsLink),
            new("hotel",          "Hotel booking",                 entitled.Contains(OrderItem.Hotel),              hotelDone,            HotelLink),
            new("dinner",         "Appreciation dinner RSVP",      entitled.Contains(OrderItem.AppreciationDinner), dinnerDone,           DinnerLink),
            new("upload-preview", "Preview presentation uploaded", true,                                            TaskDone(previewKey), TasksLink),
            new("upload-final",   "Final presentation uploaded",   true,                                            TaskDone(finalKey),   TasksLink),
            // ⚰️ §784.9(a) — the "Master Class prep notes" signal is REMOVED.
            //
            // Operator 2026-08-03: it is "an optional service, not a task". Readiness answers "has
            // this speaker done what we NEED from them?", and prep notes are something a speaker may
            // choose to publish for their master class — declining is not un-readiness. Counting it
            // held master-class speakers permanently below 100% for exercising an option, which also
            // pushed them to the top of a roster sorted by who needs chasing.
            //
            // 🔒 Deleted rather than made non-applicable: an always-inapplicable signal still reads
            // as something we intend to measure, and the next person would "fix" it back on.
            // mcLinked/mcPrepDone are still resolved by the callers — see the note there.
            new("tasks",          otherTasksLabel,                 otherTasksApplicable,            otherTasksDone,       TasksLink),
        }
        // Get-Started last: the existing signals are the ones an organizer chases hardest, and a
        // roster row is read left-to-right.
        .Concat(getStarted)
        .ToList();
    }
}
