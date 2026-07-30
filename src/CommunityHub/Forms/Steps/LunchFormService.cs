using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Resources;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace CommunityHub.Forms.Steps;

/// <summary>
/// The render + edit model for the Lunch step (REQUIREMENTS §148). It is shared by the
/// standalone <c>/Forms/Lunch</c> page AND the inline wizard step, and is the model the
/// <c>_LunchFields</c> partial binds to. The EDITABLE fields (top of the class) are the
/// only ones model binding fills; the DISPLAY fields are <see cref="BindNeverAttribute"/>
/// and are populated by <see cref="LunchFormService"/> (load + save), never from the POST.
/// </summary>
public sealed class LunchFormModel
{
    // ----- editable (bound from the POST) --------------------------------
    public bool LunchEarlySetupDay { get; set; }
    public bool LunchSetupDay { get; set; }
    // §178c: now a plain CHECKBOX like the setup-day lunches (operator wants ONE
    // select/unselect style) — checked = yes, unchecked = no. The DB column
    // LunchSignup.LunchPreDay is a non-nullable bool, so this round-trips with no
    // migration. (Was a bool? Yes/No radio under the old §62 "must declare" rule.)
    public bool LunchPreDay { get; set; }
    public string? Notes { get; set; }

    // ----- display-only (set by the service; never bound) -----------------
    [BindNever] public ParticipantRole Role { get; set; }
    [BindNever] public string FullName { get; set; } = string.Empty;
    [BindNever] public string Email { get; set; } = string.Empty;

    /// <summary>True when no lunch day applies / the pre-day is auto-counted (MC speaker): the
    /// standalone page shows the "not relevant" / "auto-counted" card; the wizard skips the step.</summary>
    [BindNever] public bool AccessDenied { get; set; }

    /// <summary>True when the PRE-DAY Yes/No radio should be shown (the "must declare" group).</summary>
    [BindNever] public bool ShowPreDay { get; set; }

    /// <summary>True when the SETUP-day checkboxes should be shown (on-site setup crew).</summary>
    [BindNever] public bool ShowSetupDay { get; set; }

    /// <summary>True when this person's pre-day lunch is AUTO-COUNTED (crew / MC speaker).</summary>
    [BindNever] public bool PreDayAutoCounted { get; set; }

    /// <summary>REQUIREMENTS §51 — when this signup was last saved (UpdatedAt); null = never.</summary>
    [BindNever] public DateTimeOffset? LastSavedAt { get; set; }

    [BindNever] public string EarlySetupDayLabel { get; set; } = "Packing day (Sun)";
    [BindNever] public string SetupDayLabel { get; set; } = "Setup day (Mon)";
    [BindNever] public string PreDayLabel { get; set; } = "Pre-day (Master Class)";
    [BindNever] public string MainDayLabel { get; set; } = "main day";

    // §178b: per-day availability gate. True when the person is explicitly marked
    // UNAVAILABLE (VolunteerDayAvailability.Level == Unavailable) on that day — they
    // are not on site, so they can't have lunch. The view dims + disables that day's
    // checkbox; the service force-unchecks it on load and never persists a checked
    // value for it. False (the default) when availability is not recorded or the day
    // is available — never block in that case.
    [BindNever] public bool EarlySetupDayUnavailable { get; set; }
    [BindNever] public bool SetupDayUnavailable { get; set; }
    [BindNever] public bool PreDayUnavailable { get; set; }

    /// <summary>Success message after a save (the standalone page surfaces it via the shared flash toast).</summary>
    [BindNever] public string? Message { get; set; }
}

/// <summary>
/// Shared submit-service for the Lunch form (REQUIREMENTS §148). It encapsulates the
/// form's ENTIRE behavior — the OnGet load, the OnPost validate/persist, and ALL
/// side-effects (per-role visibility resolution, day-label resolution, auto-task
/// ensure+done) — so that BOTH the standalone <c>/Forms/Lunch</c> page and the inline
/// <see cref="LunchStepHandler"/> call the exact same logic and stay byte-for-byte
/// identical. Implements the <see cref="IWizardFormService"/> marker so it self-registers
/// by concrete type.
/// </summary>
public sealed class LunchFormService : IWizardFormService
{
    /// <summary>SourceKey prefix for the "complete the lunch form" auto-task — <c>lunch-form:{pid}</c>.</summary>
    public const string LunchTaskKey = "lunch-form";

    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;
    private readonly IStringLocalizer<SharedResource> _loc;

    public LunchFormService(
        CommunityHubDbContext db,
        TimeProvider clock,
        IStringLocalizer<SharedResource> loc)
    {
        _db = db;
        _clock = clock;
        _loc = loc;
    }

    /// <summary>
    /// Crew/organizer roles that are on site for the SETUP days (before the pre-day):
    /// volunteers, organizers, media and event-partners. They see all three lunch days.
    /// Speakers + master-class speakers are NOT on site for setup; their form only offers
    /// the Pre-day / Master Class lunch (StartDate).
    /// </summary>
    public static bool ShowSetupDayFor(ParticipantRole role) =>
        role is ParticipantRole.Volunteer
             or ParticipantRole.Organizer
             or ParticipantRole.Media
             or ParticipantRole.EventPartner;

    /// <summary>
    /// Roles whose PRE-DAY lunch is AUTO-COUNTED in the headcount (no pre-day choice;
    /// counted automatically in the organizer report) — crew that is always on site for
    /// the pre-day (operator 2026-06-24). Master-class speakers are also auto-counted but
    /// detected via <see cref="SpeakerProfile.SpeakingPreDay"/>.
    /// </summary>
    public static bool PreDayAutoCountedRole(ParticipantRole role) =>
        role is ParticipantRole.Organizer or ParticipantRole.Media or ParticipantRole.EventPartner;

    /// <summary>
    /// Resolve per-role form visibility (operator 2026-06-24) into <paramref name="model"/>:
    ///   - PRE-DAY radio shown to the "must declare" group (non-MC Speaker, Sponsor,
    ///     Volunteer); HIDDEN for auto-counted crew + MC speakers.
    ///   - SETUP-day checkboxes shown to on-site setup crew (Volunteer + Organizer +
    ///     Media + Event-partner).
    /// Returns true when at least one day applies (the form is relevant). A SPEAKER must
    /// be ENTITLED (LunchPreDay OR LunchMainDay) — the SAME gate the nav + speaker wizard
    /// use. MC speakers (pre-day auto, no setup) get a friendly "auto-counted" view.
    /// </summary>
    private async Task<bool> ResolveAccessAsync(
        LunchFormModel model, int eventId, int participantId, ParticipantRole role, CancellationToken ct)
    {
        // §3 parity: a SPEAKER sees the Lunch form only when ENTITLED (LunchPreDay OR
        // LunchMainDay) — the SAME gate the nav (_Layout) and the speaker wizard use.
        if (role == ParticipantRole.Speaker
            && !await FormEntitlementGate.IsEntitledToAnyAsync(
                _db, eventId, participantId, ct,
                OrderItem.LunchPreDay, OrderItem.LunchMainDay))
        {
            return false;
        }

        // §294 (operator 2026-07-11): pre-day lunch is NOT gated and NOT auto-counted for
        // speakers — ANY speaker can participate in the pre-day, but some arrive after lunch, so
        // every speaker gets the opt-in checkbox and tells us if they're there. Only the always-
        // on-site crew (Organizer/Media/EventPartner) are auto-counted (no checkbox). The old
        // SpeakingPreDay flag is not populated from the schedule; master-class status is now
        // validated by a real MasterClass session (see PresentsMasterClassAsync) and drives only
        // the DEFAULT (checked) in LoadAsync, not visibility.
        model.PreDayAutoCounted = PreDayAutoCountedRole(role);
        model.ShowPreDay = !model.PreDayAutoCounted
            && role is ParticipantRole.Speaker or ParticipantRole.Sponsor or ParticipantRole.Volunteer;
        model.ShowSetupDay = ShowSetupDayFor(role);
        return model.ShowPreDay || model.ShowSetupDay;
    }

    /// <summary>
    /// §294 — a "master class speaker" is validated by an ACTUAL <see cref="SessionType.MasterClass"/>
    /// session they present (via <see cref="SessionSpeaker"/>), NOT the un-populated
    /// <see cref="SpeakerProfile.SpeakingPreDay"/> flag. Such speakers default the pre-day lunch
    /// checkbox to YES; every other speaker defaults to NO (opt-in).
    /// </summary>
    private Task<bool> PresentsMasterClassAsync(int eventId, int participantId, CancellationToken ct) =>
        _db.Sessions.AnyAsync(s => s.EventId == eventId
            && s.Type == SessionType.MasterClass
            && s.SessionSpeakers.Any(ss => ss.ParticipantId == participantId), ct);

    /// <summary>Relevance gate (REQUIREMENTS §148) — true when at least one lunch day applies to
    /// this participant. False (no applicable day / MC auto-counted) skips the inline step.</summary>
    public async Task<bool> IsRelevantAsync(int eventId, int participantId, ParticipantRole role, CancellationToken ct)
    {
        var probe = new LunchFormModel();
        return await ResolveAccessAsync(probe, eventId, participantId, role, ct);
    }

    /// <summary>Completion detection (REQUIREMENTS §148) — a <see cref="LunchSignup"/> row exists.
    /// Mirrors SpeakerWizardService / RoleWizardService.</summary>
    public Task<bool> IsDoneAsync(int eventId, int participantId, CancellationToken ct) =>
        _db.LunchSignups.AnyAsync(l => l.EventId == eventId && l.ParticipantId == participantId, ct);

    /// <summary>
    /// Load the form's current state — the SAME load the standalone page's OnGet used:
    /// resolve per-role visibility + day labels, ensure the auto-task exists, and hydrate
    /// from any existing signup. When no day applies (or the pre-day is auto-counted) the
    /// returned model carries <see cref="LunchFormModel.AccessDenied"/> = true.
    /// </summary>
    public async Task<LunchFormModel> LoadAsync(
        int eventId, int participantId, ParticipantRole role, string fullName, string email, CancellationToken ct)
    {
        var model = new LunchFormModel { Role = role, FullName = fullName, Email = email };

        if (!await ResolveAccessAsync(model, eventId, participantId, role, ct))
        {
            model.AccessDenied = true;
            return model;
        }

        await ResolveDayLabelsAsync(model, eventId, participantId, ct);

        // Make sure the "complete the lunch form" task exists on first visit so it shows
        // up under My tasks even before the form is filled in.
        await EnsureLunchTaskExistsAsync(eventId, participantId, ct);

        var existing = await _db.LunchSignups.FirstOrDefaultAsync(
            l => l.EventId == eventId && l.ParticipantId == participantId, ct);
        if (existing is not null)
        {
            model.LunchEarlySetupDay = existing.LunchEarlySetupDay;
            model.LunchSetupDay = existing.LunchSetupDay;
            model.LunchPreDay = existing.LunchPreDay;
            model.Notes = existing.Notes;
            model.LastSavedAt = existing.UpdatedAt;
        }
        else if (model.ShowPreDay && role == ParticipantRole.Speaker)
        {
            // §294: no prior answer yet — a MASTER-CLASS speaker defaults to YES for the pre-day
            // lunch (they're on-site for their master class); every other speaker defaults to NO
            // and opts in. Validated by a real MasterClass session, not the SpeakingPreDay flag.
            model.LunchPreDay = await PresentsMasterClassAsync(eventId, participantId, ct);
        }

        // §178b: a day the person is no longer available on shows UNCHECKED (and the
        // view disables it) even if a stale Yes was saved before their availability
        // changed — you can't have lunch on a day you're not on site.
        if (model.EarlySetupDayUnavailable) model.LunchEarlySetupDay = false;
        if (model.SetupDayUnavailable)      model.LunchSetupDay = false;
        if (model.PreDayUnavailable)        model.LunchPreDay = false;
        return model;
    }

    /// <summary>
    /// Persist + run all side-effects (REQUIREMENTS §148) — the SAME logic the standalone
    /// page's OnPost ran. Visibility/relevance is RE-DERIVED from the DB here, so a crafted
    /// POST can never bypass it. Returns <see cref="WizardStepOutcome.NotRelevant"/> when no
    /// day applies; otherwise upserts the signup (every day is a checkbox now — §178c — so
    /// there is always a definite Yes/No, no required-answer rejection), marks the auto-task
    /// done, and returns <see cref="WizardStepOutcome.Advance"/>. <paramref name="modelState"/>
    /// is retained for parity with the other wizard form services.
    /// </summary>
    public async Task<WizardStepOutcome> SaveAsync(
        LunchFormModel model, int eventId, int participantId, string fullName, string email,
        ParticipantRole role, ModelStateDictionary modelState, CancellationToken ct)
    {
        model.Role = role;
        model.FullName = fullName;
        model.Email = email;

        // Relevance/visibility re-derived server-side (never trusted from the post).
        if (!await ResolveAccessAsync(model, eventId, participantId, role, ct))
        {
            model.AccessDenied = true;
            return WizardStepOutcome.NotRelevant;
        }

        await ResolveDayLabelsAsync(model, eventId, participantId, ct);

        // §178c: the pre-day lunch is now a CHECKBOX (checked = yes, unchecked = no), so
        // there is always a definite answer — no "you must pick" validation is needed
        // (it dropped with the Yes/No radio).

        var signup = await _db.LunchSignups.FirstOrDefaultAsync(
            l => l.EventId == eventId && l.ParticipantId == participantId, ct);

        if (signup is null)
        {
            signup = new LunchSignup
            {
                EventId = eventId,
                ParticipantId = participantId,
                CreatedAt = _clock.GetUtcNow(),
                UpdatedAt = _clock.GetUtcNow(),
            };
            _db.LunchSignups.Add(signup);
        }
        else
        {
            signup.UpdatedAt = _clock.GetUtcNow();
        }

        // Setup-day values only persist for on-site crew (defensive against tampering).
        // §178b: a day the person is marked Unavailable on can never be saved as a Yes,
        // even from a crafted POST (the view disables it; this is the server guard).
        signup.LunchEarlySetupDay = model.ShowSetupDay && model.LunchEarlySetupDay && !model.EarlySetupDayUnavailable;
        signup.LunchSetupDay = model.ShowSetupDay && model.LunchSetupDay && !model.SetupDayUnavailable;
        // Pre-day only persists for the "must declare" group; auto-counted roles never set
        // it (it's added automatically in the organizer report). §178c: it's a checkbox now,
        // so checked => true, unchecked => false.
        signup.LunchPreDay = model.ShowPreDay && model.LunchPreDay && !model.PreDayUnavailable;
        signup.Notes = model.Notes;

        await _db.SaveChangesAsync(ct);

        // Saving the form is what marks the lunch task Done.
        await MarkLunchTaskDoneAsync(eventId, participantId, ct);

        model.LastSavedAt = signup.UpdatedAt;
        model.Message = "Your lunch preferences have been saved.";
        return WizardStepOutcome.Advance;
    }

    // ----- auto-task: "Complete the Lunch logistics form" -----------------
    private async Task EnsureLunchTaskExistsAsync(int eventId, int participantId, CancellationToken ct)
    {
        var sourceKey = $"{LunchTaskKey}:{participantId}";
        if (await _db.Tasks.AnyAsync(
                t => t.EventId == eventId && t.AssignedParticipantId == participantId
                     && t.SourceKey == sourceKey, ct)) return;

        var due = await _db.Events
            .Where(e => e.Id == eventId)
            .Select(e => (DateOnly?)e.StartDate.AddDays(-21))
            .FirstOrDefaultAsync(ct);

        _db.Tasks.Add(new ParticipantTask
        {
            EventId = eventId,
            AssignedParticipantId = participantId,
            Title = "Complete the Lunch logistics form",
            Description = "Tell us which lunches you'll join (Pre-day / Master Class -- " +
                          "plus the Packing/Setup days if your role helps before the event). " +
                          "Saving the form marks this task Done.",
            DueDate = due,
            State = TaskState.Open,
            SourceKey = sourceKey,
            CreatedAt = _clock.GetUtcNow(),
        });
        await _db.SaveChangesAsync(ct);
    }

    private async Task MarkLunchTaskDoneAsync(int eventId, int participantId, CancellationToken ct)
    {
        var sourceKey = $"{LunchTaskKey}:{participantId}";
        var task = await _db.Tasks.FirstOrDefaultAsync(
            t => t.EventId == eventId && t.AssignedParticipantId == participantId
                 && t.SourceKey == sourceKey, ct);
        if (task is null || task.State == TaskState.Done) return;

        task.State = TaskState.Done;
        task.CompletedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Resolve display labels for Setup-day and Pre-day from the Event row, plus the
    /// per-day availability gate (§178b). The conference StartDate IS the pre-day /
    /// Master Class day; the day before it is the setup day, and the day before THAT is
    /// the packing day (§178a — that Sunday is for packing, not setup). The EndDate is
    /// the main day -- its lunch is booked for everyone, so it's a note.
    /// </summary>
    private async Task ResolveDayLabelsAsync(LunchFormModel model, int eventId, int participantId, CancellationToken ct)
    {
        var evt = await _db.Events
            .Where(e => e.Id == eventId)
            .Select(e => new { e.StartDate, e.EndDate })
            .FirstOrDefaultAsync(ct);
        if (evt is null) return;

        var preDay        = evt.StartDate;
        var setupDay      = evt.StartDate.AddDays(-1);
        var earlySetupDay = evt.StartDate.AddDays(-2);

        // §178a: the earliest day (Sunday) is the PACKING day, not a setup day.
        model.EarlySetupDayLabel = $"Packing day ({earlySetupDay:dddd, MMM d yyyy})";
        model.SetupDayLabel      = $"Setup day ({setupDay:dddd, MMM d yyyy})";
        model.PreDayLabel        = $"Pre-day / Master Class ({preDay:dddd, MMM d yyyy})";
        model.MainDayLabel       = $"{evt.EndDate:dddd, MMM d yyyy}";

        // §178b: a lunch day is gated off only when the person is EXPLICITLY marked
        // Unavailable (absent) on it — the same per-day signal volunteer scheduling
        // uses. Missing rows (availability never recorded) leave every day enabled, so
        // we only ever DISABLE on a positive "not on site" signal — we never block by
        // default.
        var unavailableDays = await _db.VolunteerDayAvailabilities
            .Where(a => a.EventId == eventId && a.ParticipantId == participantId
                        && a.Level == VolunteerAvailabilityLevel.Unavailable)
            .Select(a => a.Day)
            .ToListAsync(ct);
        model.EarlySetupDayUnavailable = unavailableDays.Contains(earlySetupDay);
        model.SetupDayUnavailable      = unavailableDays.Contains(setupDay);
        model.PreDayUnavailable        = unavailableDays.Contains(preDay);
    }
}
