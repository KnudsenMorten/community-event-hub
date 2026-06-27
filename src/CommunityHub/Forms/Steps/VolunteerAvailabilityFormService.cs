using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations.Sessions;
using CommunityHub.Core.Volunteers;
using CommunityHub.Pages.Volunteer;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Forms.Steps;

/// <summary>
/// The render + edit model for the volunteer "My Availability" step (REQUIREMENTS §148).
/// It is shared by the standalone <c>/volunteer/availability</c> page AND the inline wizard
/// step, and is the model the <c>_AvailabilityFields</c> partial binds to. The per-day
/// editable choices are the bound <see cref="Inputs"/> (filled by model binding from the
/// flat <c>Inputs[i].*</c> form keys); the display-only fields (<see cref="Days"/>,
/// <see cref="LastSavedAt"/>, <see cref="Notice"/>) are <see cref="BindNeverAttribute"/> and
/// are populated by <see cref="VolunteerAvailabilityFormService"/>, never from the POST.
/// </summary>
public sealed class VolunteerAvailabilityFormModel
{
    // ----- editable (bound from the POST) --------------------------------
    /// <summary>The posted per-day choice (slot + free note) — the same shape the
    /// standalone page binds. Re-uses <see cref="AvailabilityModel.DayInput"/> so the
    /// standalone page and the wizard bind byte-for-byte identically.</summary>
    public List<AvailabilityModel.DayInput> Inputs { get; set; } = new();

    // ----- display-only (set by the service; never bound) -----------------
    /// <summary>One editable row per event day, pre-filled with any saved value (renders the options).</summary>
    [BindNever] public IReadOnlyList<AvailabilityModel.DayRow> Days { get; set; } = Array.Empty<AvailabilityModel.DayRow>();

    /// <summary>When this volunteer last saved their availability (UTC) — the latest UpdatedAt
    /// across their day rows, for the "Last saved …" line (§51); null = never.</summary>
    [BindNever] public DateTimeOffset? LastSavedAt { get; set; }

    /// <summary>The post-save flash the standalone page surfaces via TempData; null until a save runs.</summary>
    [BindNever] public string? Notice { get; set; }
}

/// <summary>
/// Shared submit-service for the volunteer "My Availability" form (REQUIREMENTS §148). It
/// encapsulates the form's ENTIRE behavior — the OnGet load (per-event-day rows with the
/// slot→Level option set) and the OnPost persist + ALL side-effects that MUST be preserved:
/// a FIRST submission (no existing rows) applies directly + emails the volunteer lead
/// (<see cref="AvailabilityModel.NotifyEmail"/>, fail-soft); a later EDIT does NOT overwrite —
/// it builds per-day old→new diffs and ENQUEUEs a Volunteer Update via
/// <see cref="SyncDeltaQueueService"/> (NotifyNew throttled) so an organizer approves it
/// (§59 delta-approval). Client-injected days outside the edition are ignored. Both the
/// standalone <c>/volunteer/availability</c> page AND the inline
/// <see cref="VolunteerAvailabilityStepHandler"/> call this exact logic, so the two stay
/// identical. Implements the <see cref="IWizardFormService"/> marker so it self-registers
/// by concrete type.
/// </summary>
public sealed class VolunteerAvailabilityFormService : IWizardFormService
{
    private readonly CommunityHubDbContext _db;
    private readonly EventEditionConfigLoader _cfg;
    private readonly EventConfigOptions _cfgOptions;
    private readonly IEmailSender _email;
    private readonly SyncDeltaQueueService _queue;
    private readonly ILogger<VolunteerAvailabilityFormService> _logger;

    public VolunteerAvailabilityFormService(
        CommunityHubDbContext db,
        EventEditionConfigLoader cfg,
        EventConfigOptions cfgOptions,
        IEmailSender email,
        SyncDeltaQueueService queue,
        ILogger<VolunteerAvailabilityFormService> logger)
    {
        _db = db;
        _cfg = cfg;
        _cfgOptions = cfgOptions;
        _email = email;
        _queue = queue;
        _logger = logger;
    }

    /// <summary>Availability is a VOLUNTEER-only scheduling input (RoleWizardService adds the
    /// step for volunteers only) — the §148 relevance gate.</summary>
    public static bool IsRelevant(ParticipantRole role) => role == ParticipantRole.Volunteer;

    /// <summary>Completion detection (REQUIREMENTS §148) — a <see cref="VolunteerDayAvailability"/>
    /// row exists for (eventId, participantId). Mirrors RoleWizardService.</summary>
    public Task<bool> IsDoneAsync(int eventId, int participantId, CancellationToken ct) =>
        _db.VolunteerDayAvailabilities.AnyAsync(
            a => a.EventId == eventId && a.ParticipantId == participantId, ct);

    /// <summary>
    /// Load the form's current state — the SAME load the standalone page's OnGet used:
    /// build the per-event-day rows pre-filled from any saved availability and surface the
    /// last-saved timestamp. Returns a fully-populated render model.
    /// </summary>
    public async Task<VolunteerAvailabilityFormModel> LoadAsync(
        int eventId, int participantId, CancellationToken ct)
    {
        var model = new VolunteerAvailabilityFormModel();
        await ReloadAsync(model, eventId, participantId, ct);
        return model;
    }

    /// <summary>
    /// Persist + run all side-effects (REQUIREMENTS §148) — the SAME logic the standalone
    /// page's OnPost ran. Days the client may have injected outside the edition are ignored.
    /// A FIRST-time submission (no existing rows) applies directly and emails the volunteer
    /// lead (fail-soft); a later EDIT is NOT applied — it is enqueued as a Volunteer Update
    /// delta for organizer approval, leaving the current (approved) availability in place.
    /// The post-save flash is written to <see cref="VolunteerAvailabilityFormModel.Notice"/>;
    /// the model's display state is reloaded for re-render. Always advances (this form has no
    /// field-level validation that blocks a save).
    /// </summary>
    public async Task<WizardStepOutcome> SaveAsync(
        VolunteerAvailabilityFormModel model, int eventId, int participantId,
        string fullName, string email, CancellationToken ct)
    {
        // Only accept days that genuinely belong to this edition — ignore anything the
        // client may have injected.
        var allDays = await EventDaysAsync(eventId, ct);
        var validDays = allDays.Select(d => d.Day).ToHashSet();
        var labelByDay = allDays.ToDictionary(d => d.Day, d => d.Label);

        var existing = await _db.VolunteerDayAvailabilities
            .Where(x => x.EventId == eventId && x.ParticipantId == participantId)
            .ToListAsync(ct);

        // §59 DELTA-APPROVAL QUEUE (mirrors the §38e "first submission applies, a later
        // CHANGE is queued" pattern). The presence of existing availability rows IS the
        // "already submitted" baseline — no extra column needed.
        var alreadySubmitted = existing.Count > 0;

        // Resolve each valid posted day to its desired (Level, Note) once.
        var desired = new List<(DateOnly Day, VolunteerAvailabilityLevel Level, string? Note)>();
        foreach (var input in model.Inputs)
        {
            if (!validDays.Contains(input.Day)) continue;

            // The posted slot drives both the capacity Level and (combined with the
            // volunteer's free note) the stored Note. Ignore an unknown/absent slot.
            var options = VolunteerDayOptions.For(input.Day);
            var chosen = options.FirstOrDefault(o =>
                string.Equals(o.Slot, input.Slot, StringComparison.OrdinalIgnoreCase));
            if (chosen is null) continue;

            var userNote = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim();
            var note = VolunteerDayOptions.ComposeNote(chosen.Slot, userNote);
            if (note is { Length: > 500 }) note = note[..500];

            desired.Add((input.Day, chosen.Level, note));
        }

        if (alreadySubmitted)
        {
            // EDIT after an initial submission → build per-day old→new diffs and ENQUEUE.
            var changes = BuildAvailabilityChanges(existing, desired);
            if (changes.Count == 0)
            {
                model.Notice = "No changes detected — your current availability is unchanged.";
                await ReloadAsync(model, eventId, participantId, ct);
                return WizardStepOutcome.Advance;
            }

            var before = await _queue.CountPendingAsync(eventId, ct);
            await _queue.EnqueueVolunteerAvailabilityUpdateAsync(
                eventId, participantId, fullName, changes, ct);
            // Notify the organizer that a change is awaiting approval (throttled, never throws).
            await _queue.NotifyNewAsync(eventId, before, ct);

            _logger.LogInformation(
                "Volunteer {ParticipantId} submitted an availability CHANGE for event {EventId} "
                + "({Count} day(s)) — queued for organizer approval.",
                participantId, eventId, changes.Count);

            model.Notice = "Your change has been submitted for organizer review. "
                + "Until it is approved, your previously saved availability still applies — "
                + "the days below show your current (approved) availability.";
            await ReloadAsync(model, eventId, participantId, ct);
            return WizardStepOutcome.Advance;
        }

        // FIRST-time submission → apply directly (no queue).
        foreach (var d in desired)
        {
            _db.VolunteerDayAvailabilities.Add(new VolunteerDayAvailability
            {
                EventId = eventId,
                ParticipantId = participantId,
                Day = d.Day,
                Level = d.Level,
                Note = d.Note,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Volunteer {ParticipantId} saved availability for event {EventId}.",
            participantId, eventId);

        // Notify the volunteer lead on every save (add OR update) so they can re-allocate
        // shifts (operator 2026-06-23). Fail-soft: a mail problem must not lose the save.
        await NotifyLeadAsync(eventId, participantId, fullName, email, validDays, labelByDay, ct);

        model.Notice = "Your availability has been saved and emailed to Morten Leth. "
            + "Thank you — this helps us schedule you fairly.";
        await ReloadAsync(model, eventId, participantId, ct);
        return WizardStepOutcome.Advance;
    }

    // ----- load helper ----------------------------------------------------
    private async Task ReloadAsync(
        VolunteerAvailabilityFormModel model, int eventId, int participantId, CancellationToken ct)
    {
        var days = await EventDaysAsync(eventId, ct);

        var saved = await _db.VolunteerDayAvailabilities
            .Where(x => x.EventId == eventId && x.ParticipantId == participantId)
            .ToDictionaryAsync(x => x.Day, ct);

        // §51 — last saved = the latest UpdatedAt across this volunteer's day rows.
        model.LastSavedAt = saved.Count == 0 ? null : saved.Values.Max(x => x.UpdatedAt);

        model.Days = days.Select(d =>
        {
            saved.TryGetValue(d.Day, out var row);
            return new AvailabilityModel.DayRow(
                d.Day,
                d.Label,
                row?.Level ?? VolunteerAvailabilityLevel.Full,
                row?.Note);
        }).ToList();
    }

    /// <summary>
    /// Build the per-day old→new diff list for a volunteer availability EDIT. Only days whose
    /// (Level, Note) actually changed are included; an empty result means nothing changed. The
    /// new value carries the machine payload an Approve will apply (see
    /// <see cref="SyncDeltaQueueService.BuildVolunteerAvailabilityChanges"/>).
    /// </summary>
    private static IReadOnlyList<SyncFieldChange> BuildAvailabilityChanges(
        IReadOnlyList<VolunteerDayAvailability> existing,
        IReadOnlyList<(DateOnly Day, VolunteerAvailabilityLevel Level, string? Note)> desired)
    {
        var rows = new List<(string, string, string, VolunteerAvailabilityLevel, string?)>();
        foreach (var d in desired)
        {
            var old = existing.FirstOrDefault(x => x.Day == d.Day);
            var oldLevel = old?.Level ?? VolunteerAvailabilityLevel.Full;
            var oldNote = old?.Note;
            var noteChanged = !string.Equals(
                NormalizeNote(oldNote), NormalizeNote(d.Note), StringComparison.Ordinal);
            if (old is not null && old.Level == d.Level && !noteChanged) continue; // unchanged

            var oldLabel = old is null
                ? "(not set)"
                : VolunteerDayLabel(d.Day, oldLevel, oldNote);
            var newLabel = VolunteerDayLabel(d.Day, d.Level, d.Note);
            rows.Add((d.Day.ToString("yyyy-MM-dd"), oldLabel, newLabel, d.Level, d.Note));
        }
        return SyncDeltaQueueService.BuildVolunteerAvailabilityChanges(rows);
    }

    private static string? NormalizeNote(string? n) => string.IsNullOrWhiteSpace(n) ? null : n.Trim();

    /// <summary>A human availability label for the queue UI: the chosen slot plus the free note.</summary>
    private static string VolunteerDayLabel(DateOnly day, VolunteerAvailabilityLevel level, string? note)
    {
        var slot = VolunteerDayOptions.DisplayLabel(day, level, note);
        var free = VolunteerDayOptions.StripSlot(note);
        return string.IsNullOrWhiteSpace(free) ? slot : $"{slot} — {free}";
    }

    private async Task NotifyLeadAsync(
        int eventId, int participantId, string fullName, string email,
        HashSet<DateOnly> validDays, IReadOnlyDictionary<DateOnly, string> labelByDay,
        CancellationToken ct)
    {
        try
        {
            var rows = (await _db.VolunteerDayAvailabilities
                    .Where(x => x.EventId == eventId && x.ParticipantId == participantId)
                    .ToListAsync(ct))
                .Where(x => validDays.Contains(x.Day))
                .OrderBy(x => x.Day)
                .ToList();

            var lines = rows.Select(r =>
                "<tr><td style=\"padding:4px 10px 4px 0;\">"
                + System.Net.WebUtility.HtmlEncode(labelByDay.TryGetValue(r.Day, out var l) ? l : r.Day.ToString("yyyy-MM-dd"))
                + "</td><td style=\"padding:4px 10px;\"><b>"
                + System.Net.WebUtility.HtmlEncode(VolunteerDayOptions.DisplayLabel(r.Day, r.Level, r.Note))
                + "</b></td><td style=\"padding:4px 0;color:#555;\">"
                + System.Net.WebUtility.HtmlEncode(VolunteerDayOptions.StripSlot(r.Note) ?? string.Empty) + "</td></tr>");

            var html =
                $"<p>Volunteer <b>{System.Net.WebUtility.HtmlEncode(fullName)}</b> "
                + $"(<a href=\"mailto:{System.Net.WebUtility.HtmlEncode(email)}\">{System.Net.WebUtility.HtmlEncode(email)}</a>) "
                + "saved their availability:</p>"
                + "<table style=\"border-collapse:collapse;font-size:14px;\">"
                + "<tr><th align=\"left\" style=\"padding:4px 10px 4px 0;\">Day</th>"
                + "<th align=\"left\" style=\"padding:4px 10px;\">Availability</th>"
                + "<th align=\"left\" style=\"padding:4px 0;\">Note</th></tr>"
                + string.Concat(lines)
                + "</table>";

            await _email.SendAsync(
                AvailabilityModel.NotifyEmail,
                $"[Volunteer availability] {fullName} updated their availability",
                html, ct);
        }
        catch (Exception ex)
        {
            // Never fail the save because the notification couldn't go out.
            _logger.LogError(ex,
                "Availability save notification to {Lead} failed for volunteer {ParticipantId}.",
                AvailabilityModel.NotifyEmail, participantId);
        }
    }

    /// <summary>
    /// Pre-day (if any) + every main day StartDate..EndDate, PLUS any configured extra
    /// volunteer days (e.g. the packing day) that fall outside the public range — all
    /// ordered, deduped.
    /// </summary>
    private async Task<IReadOnlyList<(DateOnly Day, string Label)>> EventDaysAsync(
        int eventId, CancellationToken ct)
    {
        var ev = await _db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Id == eventId, ct);
        if (ev is null) return Array.Empty<(DateOnly, string)>();

        var set = new SortedSet<DateOnly>();
        if (ev.PreDayDate is { } pre) set.Add(pre);
        for (var d = ev.StartDate; d <= ev.EndDate; d = d.AddDays(1)) set.Add(d);

        var eventLabels = set.ToDictionary(d => d, d =>
        {
            var isPre = ev.PreDayDate == d || d < ev.StartDate;
            return d.ToString("dddd dd MMM") + (isPre ? " — pre-day" : string.Empty);
        });

        // Merge configured extra volunteer days (outside the public range).
        var cfg = _cfg.Load(_cfgOptions.EventConfigPath);
        foreach (var x in cfg.Volunteer?.ExtraAvailabilityDays ?? new List<VolunteerExtraDay>())
        {
            if (!DateOnly.TryParse(x.Date, out var day)) continue;
            set.Add(day);
            var caption = string.IsNullOrWhiteSpace(x.Label)
                ? day.ToString("dddd dd MMM")
                : $"{day:dddd dd MMM} — {x.Label}";
            eventLabels[day] = caption;
        }

        return set.Select(d => (d, eventLabels[d])).ToList();
    }
}
