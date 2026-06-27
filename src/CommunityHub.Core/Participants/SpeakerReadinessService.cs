using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Entitlements;
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
///   <item><b>masterclass</b> — applicable only when the speaker is LINKED to a
///   <see cref="SessionType.MasterClass"/> session; done when at least one of those
///   sessions has published <see cref="Session.PrepContent"/>.</item>
///   <item><b>tasks</b> — every OTHER assigned to-do (Signal join, promote, code of
///   conduct, swag/lunch/travel deadlines, …) is complete; i.e. no remaining OPEN
///   <see cref="ParticipantTask"/> other than the two upload tasks already shown above.</item>
/// </list>
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

    public SpeakerReadinessService(CommunityHubDbContext db) => _db = db;

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
        var entitled = OrderEntitlements.Effective(participant, profile, overrides);

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

        var mcLinked = await _db.SessionSpeakers.AnyAsync(
            ss => ss.ParticipantId == participantId
                  && ss.Session.EventId == eventId
                  && ss.Session.Type == SessionType.MasterClass, ct);
        var mcPrepDone = mcLinked && await _db.SessionSpeakers.AnyAsync(
            ss => ss.ParticipantId == participantId
                  && ss.Session.EventId == eventId
                  && ss.Session.Type == SessionType.MasterClass
                  && ss.Session.PrepContent != null && ss.Session.PrepContent != "", ct);

        var signals = BuildSignals(
            participantId, profile, entitled, hotelDone, dinnerDone, tasks, mcLinked, mcPrepDone);

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

        // Master-class link + published-prep per speaker, in two set-building queries.
        var mcLinkedPids = (await _db.SessionSpeakers
                .Where(ss => ss.Session.EventId == eventId
                             && ss.Session.Type == SessionType.MasterClass
                             && ids.Contains(ss.ParticipantId))
                .Select(ss => ss.ParticipantId).Distinct().ToListAsync(ct))
            .ToHashSet();
        var mcPrepDonePids = (await _db.SessionSpeakers
                .Where(ss => ss.Session.EventId == eventId
                             && ss.Session.Type == SessionType.MasterClass
                             && ids.Contains(ss.ParticipantId)
                             && ss.Session.PrepContent != null && ss.Session.PrepContent != "")
                .Select(ss => ss.ParticipantId).Distinct().ToListAsync(ct))
            .ToHashSet();

        var result = new List<SpeakerReadiness>(profiles.Count);
        foreach (var profile in profiles)
        {
            var pid = profile.ParticipantId;
            if (!byId.TryGetValue(pid, out var participant)) continue; // orphan profile

            var overrides = overridesByPid.TryGetValue(pid, out var ov)
                ? ov : Array.Empty<ParticipantOrderOverride>();
            var entitled = OrderEntitlements.Effective(participant, profile, overrides);

            var hotelDone = entitled.Contains(OrderItem.Hotel) && hotelPids.Contains(pid);
            var dinnerDone = entitled.Contains(OrderItem.AppreciationDinner) && dinnerPids.Contains(pid);

            var taskRows = tasksByPid.TryGetValue(pid, out var tl) ? tl : Array.Empty<TaskRow>();

            var mcLinked = mcLinkedPids.Contains(pid);
            var mcPrepDone = mcLinked && mcPrepDonePids.Contains(pid);

            var signals = BuildSignals(
                pid, profile, entitled, hotelDone, dinnerDone, taskRows, mcLinked, mcPrepDone);

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
    private static List<ReadinessSignal> BuildSignals(
        int participantId,
        SpeakerProfile profile,
        IReadOnlySet<OrderItem> entitled,
        bool hotelDone,
        bool dinnerDone,
        IReadOnlyList<TaskRow> tasks,
        bool mcLinked,
        bool mcPrepDone)
    {
        var previewKey = $"speakerdl:{participantId}:{PreviewTaskSlug}";
        var finalKey = $"speakerdl:{participantId}:{FinalTaskSlug}";

        bool TaskDone(string key) =>
            tasks.Any(t => t.State == TaskState.Done
                           && string.Equals(t.SourceKey, key, StringComparison.Ordinal));

        // "Other to-dos complete": no OPEN task remains other than the two upload tasks
        // already surfaced as their own items above. An untagged open task (SourceKey
        // null) is neither upload key, so it correctly still blocks.
        var otherTasksDone = !tasks.Any(t =>
            t.State != TaskState.Done
            && !string.Equals(t.SourceKey, previewKey, StringComparison.Ordinal)
            && !string.Equals(t.SourceKey, finalKey, StringComparison.Ordinal));

        var detailsDone = profile.BioLastEditedBySpeakerAt != null;
        var headshotDone =
            !string.IsNullOrWhiteSpace(profile.PhotoUrl)
            || !string.IsNullOrWhiteSpace(profile.PhotoSharePointPath);

        return new List<ReadinessSignal>
        {
            new("details",        "Speaker details & bio",         true,                                            detailsDone,          DetailsLink),
            new("headshot",       "Headshot photo",                true,                                            headshotDone,         DetailsLink),
            new("hotel",          "Hotel booking",                 entitled.Contains(OrderItem.Hotel),              hotelDone,            HotelLink),
            new("dinner",         "Appreciation dinner RSVP",      entitled.Contains(OrderItem.AppreciationDinner), dinnerDone,           DinnerLink),
            new("upload-preview", "Preview presentation uploaded", true,                                            TaskDone(previewKey), TasksLink),
            new("upload-final",   "Final presentation uploaded",   true,                                            TaskDone(finalKey),   TasksLink),
            new("masterclass",    "Master Class prep notes",       mcLinked,                                        mcPrepDone,           HubLink),
            new("tasks",          "Other to-dos complete",         true,                                            otherTasksDone,       TasksLink),
        };
    }
}
