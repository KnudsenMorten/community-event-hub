using CommunityHub.Auth;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations.Sessions;
using CommunityHub.Core.Volunteers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Volunteer;

/// <summary>
/// VOLUNTEER "My Availability" (operator 2026-06-21): a volunteer marks, per
/// event day, how much they can work — Full (whole day), Half (split: work part,
/// attend part), Blocked (attending only) or Unavailable (not present). Plus any
/// extra lead-up days configured for volunteers (e.g. the packing day).
/// Coordinators read this when assigning shifts so a volunteer is never scheduled
/// outside their windows. On Save the volunteer's availability is emailed to the
/// volunteer lead (operator 2026-06-23). Self-service only — a volunteer edits
/// their own row via <see cref="ICurrentParticipantAccessor"/>; the client never
/// supplies the id.
/// </summary>
[Authorize]
public class AvailabilityModel : PageModel
{
    /// <summary>The volunteer lead who is notified whenever a volunteer saves their availability.</summary>
    public const string NotifyEmail = "mlh@expertslive.dk";

    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly EventEditionConfigLoader _cfg;
    private readonly EventConfigOptions _cfgOptions;
    private readonly IEmailSender _email;
    private readonly SyncDeltaQueueService _queue;
    private readonly ILogger<AvailabilityModel> _logger;

    public AvailabilityModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        EventEditionConfigLoader cfg,
        EventConfigOptions cfgOptions,
        IEmailSender email,
        SyncDeltaQueueService queue,
        ILogger<AvailabilityModel> logger)
    {
        _db = db;
        _participant = participant;
        _cfg = cfg;
        _cfgOptions = cfgOptions;
        _email = email;
        _queue = queue;
        _logger = logger;
    }

    /// <summary>
    /// One editable row per event day, pre-filled with any saved value. RawNote is
    /// the full stored Note (incl. any "[slot]" tag — used to re-select the right
    /// option); UserNote is the volunteer's free text only (shown in the textarea).
    /// </summary>
    public record DayRow(DateOnly Day, string Label, VolunteerAvailabilityLevel Level, string? RawNote)
    {
        public string? UserNote => VolunteerDayOptions.StripSlot(RawNote);
    }

    public IReadOnlyList<DayRow> Days { get; private set; } = Array.Empty<DayRow>();

    /// <summary>When this volunteer last saved their availability (UTC) — the latest
    /// UpdatedAt across their day rows, for the "Last saved …" line (§51).</summary>
    public DateTimeOffset? LastSavedAt { get; private set; }

    [BindProperty] public List<DayInput> Inputs { get; set; } = new();

    public class DayInput
    {
        public DateOnly Day { get; set; }
        /// <summary>The chosen option's stable slot id (see <see cref="VolunteerDayOptions"/>).</summary>
        public string? Slot { get; set; }
        public string? Note { get; set; }
    }

    [TempData] public string? Notice { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        await LoadAsync(me.EventId, me.ParticipantId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        // Only accept days that genuinely belong to this edition — ignore anything
        // the client may have injected.
        var allDays = await EventDaysAsync(me.EventId, ct);
        var validDays = allDays.Select(d => d.Day).ToHashSet();
        var labelByDay = allDays.ToDictionary(d => d.Day, d => d.Label);

        var existing = await _db.VolunteerDayAvailabilities
            .Where(x => x.EventId == me.EventId && x.ParticipantId == me.ParticipantId)
            .ToListAsync(ct);

        // §59 DELTA-APPROVAL QUEUE (mirrors the §38e "first submission applies, a later
        // CHANGE is queued" pattern). The presence of existing availability rows IS the
        // "already submitted" baseline — no extra column needed. A FIRST-time submission
        // (no rows yet) applies directly below; a later EDIT does NOT silently overwrite —
        // it is enqueued as a Volunteer Update delta for the organizer to approve, and the
        // volunteer keeps seeing their current (approved) availability until then.
        var alreadySubmitted = existing.Count > 0;

        // Resolve each valid posted day to its desired (Level, Note) once.
        var desired = new List<(DateOnly Day, VolunteerAvailabilityLevel Level, string? Note)>();
        foreach (var input in Inputs)
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
                Notice = "No changes detected — your current availability is unchanged.";
                return RedirectToPage();
            }

            var before = await _queue.CountPendingAsync(me.EventId, ct);
            await _queue.EnqueueVolunteerAvailabilityUpdateAsync(
                me.EventId, me.ParticipantId, me.FullName, changes, ct);
            // Notify the organizer that a change is awaiting approval (throttled, never throws).
            await _queue.NotifyNewAsync(me.EventId, before, ct);

            _logger.LogInformation(
                "Volunteer {ParticipantId} submitted an availability CHANGE for event {EventId} "
                + "({Count} day(s)) — queued for organizer approval.",
                me.ParticipantId, me.EventId, changes.Count);

            Notice = "Your change has been submitted for organizer review. "
                + "Until it is approved, your previously saved availability still applies — "
                + "the days below show your current (approved) availability.";
            return RedirectToPage();
        }

        // FIRST-time submission → apply directly (no queue).
        foreach (var d in desired)
        {
            _db.VolunteerDayAvailabilities.Add(new VolunteerDayAvailability
            {
                EventId = me.EventId,
                ParticipantId = me.ParticipantId,
                Day = d.Day,
                Level = d.Level,
                Note = d.Note,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Volunteer {ParticipantId} saved availability for event {EventId}.",
            me.ParticipantId, me.EventId);

        // Notify the volunteer lead on every save (add OR update) so they can
        // re-allocate shifts (operator 2026-06-23). Fail-soft: a mail problem must
        // not lose the saved availability.
        await NotifyLeadAsync(me, validDays, labelByDay, ct);

        Notice = "Your availability has been saved and emailed to Morten Leth. "
            + "Thank you — this helps us schedule you fairly.";
        return RedirectToPage();
    }

    /// <summary>
    /// Build the per-day old→new diff list for a volunteer availability EDIT. Only days whose
    /// (Level, Note) actually changed are included; an empty result means nothing changed. The
    /// new value carries the machine payload an Approve will apply (see
    /// <see cref="SyncDeltaQueueService.BuildVolunteerAvailabilityChanges"/>).
    /// </summary>
    private static IReadOnlyList<CommunityHub.Core.Domain.SyncFieldChange> BuildAvailabilityChanges(
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
        CurrentParticipant me, HashSet<DateOnly> validDays,
        IReadOnlyDictionary<DateOnly, string> labelByDay, CancellationToken ct)
    {
        try
        {
            var rows = (await _db.VolunteerDayAvailabilities
                    .Where(x => x.EventId == me.EventId && x.ParticipantId == me.ParticipantId)
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
                $"<p>Volunteer <b>{System.Net.WebUtility.HtmlEncode(me.FullName)}</b> "
                + $"(<a href=\"mailto:{System.Net.WebUtility.HtmlEncode(me.Email)}\">{System.Net.WebUtility.HtmlEncode(me.Email)}</a>) "
                + "saved their availability:</p>"
                + "<table style=\"border-collapse:collapse;font-size:14px;\">"
                + "<tr><th align=\"left\" style=\"padding:4px 10px 4px 0;\">Day</th>"
                + "<th align=\"left\" style=\"padding:4px 10px;\">Availability</th>"
                + "<th align=\"left\" style=\"padding:4px 0;\">Note</th></tr>"
                + string.Concat(lines)
                + "</table>";

            await _email.SendAsync(
                NotifyEmail,
                $"[Volunteer availability] {me.FullName} updated their availability",
                html, ct);
        }
        catch (Exception ex)
        {
            // Never fail the save because the notification couldn't go out.
            _logger.LogError(ex,
                "Availability save notification to {Lead} failed for volunteer {ParticipantId}.",
                NotifyEmail, me.ParticipantId);
        }
    }

    private static string LevelLabel(VolunteerAvailabilityLevel level) => level switch
    {
        VolunteerAvailabilityLevel.Full => "Full day",
        VolunteerAvailabilityLevel.Half => "Half day",
        VolunteerAvailabilityLevel.Blocked => "Blocked (attending only)",
        VolunteerAvailabilityLevel.Unavailable => "Cannot help",
        _ => level.ToString(),
    };

    private async Task LoadAsync(int eventId, int participantId, CancellationToken ct)
    {
        var days = await EventDaysAsync(eventId, ct);

        var saved = await _db.VolunteerDayAvailabilities
            .Where(x => x.EventId == eventId && x.ParticipantId == participantId)
            .ToDictionaryAsync(x => x.Day, ct);

        // §51 — last saved = the latest UpdatedAt across this volunteer's day rows.
        LastSavedAt = saved.Count == 0 ? null : saved.Values.Max(x => x.UpdatedAt);

        Days = days.Select(d =>
        {
            saved.TryGetValue(d.Day, out var row);
            return new DayRow(
                d.Day,
                d.Label,
                row?.Level ?? VolunteerAvailabilityLevel.Full,
                row?.Note);
        }).ToList();
    }

    /// <summary>
    /// Pre-day (if any) + every main day StartDate..EndDate, PLUS any configured
    /// extra volunteer days (e.g. the packing day) that fall outside the public
    /// range — all ordered, deduped.
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
