using CommunityHub.Auth;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages;

/// <summary>
/// The site root (<c>/</c>). It serves TWO audiences from one route:
///  - <b>Anonymous visitors</b> get the PUBLIC landing page (REQUIREMENTS §21
///    PUBLIC): the active edition's name/dates/venue + a sign-in / visit-event
///    CTA and links into the public Sessions / Speakers / Sponsors / Master Class
///    pages. No redirect to Login — the landing renders in place so the public
///    pages are reachable + shareable (SEO). <see cref="Landing"/> is set and the
///    view renders the landing branch.
///  - <b>Signed-in participants</b> get the role-personalized hub (CONTEXT.md §4):
///    a section is shown only if it applies to the participant's
///    <see cref="ParticipantRole"/>. The model loads their per-edition data
///    (tasks, form status) for the view.
///
/// The page is <see cref="AllowAnonymousAttribute">AllowAnonymous</see> so the
/// landing is reachable signed-out; the hub branch still requires a resolved
/// participant (genuinely-gated hub data redirects to Login as before).
/// </summary>
[AllowAnonymous]
public class IndexModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SpeakerDeadlineSeeder _speakerDeadlines;
    private readonly EventEditionConfigLoader _eventConfigLoader;
    private readonly EventConfigOptions _eventConfigOptions;
    private readonly CommunityHub.Core.Reminders.CalendarFeedTokenService _calendarTokens;
    private readonly CommunityHub.Core.Reminders.ParticipantCalendarBuilder _calendarBuilder;
    private readonly CommunityHub.Core.Participants.ParticipantChecklistBuilder _checklist;
    private readonly CommunityHub.Core.Reminders.SpeakerSessionsService _speakerSessions;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        SpeakerDeadlineSeeder speakerDeadlines,
        EventEditionConfigLoader eventConfigLoader,
        EventConfigOptions eventConfigOptions,
        CommunityHub.Core.Reminders.CalendarFeedTokenService calendarTokens,
        CommunityHub.Core.Reminders.ParticipantCalendarBuilder calendarBuilder,
        CommunityHub.Core.Participants.ParticipantChecklistBuilder checklist,
        CommunityHub.Core.Reminders.SpeakerSessionsService speakerSessions,
        ILogger<IndexModel> logger)
    {
        _db = db;
        _participant = participant;
        _speakerDeadlines = speakerDeadlines;
        _eventConfigLoader = eventConfigLoader;
        _eventConfigOptions = eventConfigOptions;
        _calendarTokens = calendarTokens;
        _calendarBuilder = calendarBuilder;
        _checklist = checklist;
        _speakerSessions = speakerSessions;
        _logger = logger;
    }

    /// <summary>The speaker's own sessions, surfaced on the speaker landing card.</summary>
    public IReadOnlyList<CommunityHub.Core.Reminders.MySpeakerSession> SpeakerSessions { get; private set; }
        = System.Array.Empty<CommunityHub.Core.Reminders.MySpeakerSession>();

    public CommunityHub.Core.Config.EditionDates? EventDates { get; private set; }

    public CurrentParticipant Me { get; private set; } = null!;
    public string CommunityName { get; private set; } = "Community Hub";
    public string EventDisplayName { get; private set; } = string.Empty;

    // --- Section visibility (driven by role) --------------------------------
    public bool ShowHotel { get; private set; }
    public bool ShowDinner { get; private set; }
    public bool ShowVolunteerShifts { get; private set; }
    /// <summary>Show the "Volunteer work" card (assigned tasks + help): volunteers
    /// (and organizers, who also see the structure tools).</summary>
    public bool ShowVolunteerWork { get; private set; }
    /// <summary>True if the signed-in volunteer supervises at least one category —
    /// drives the supervisor-dashboard link.</summary>
    public bool IsCategorySupervisor { get; private set; }
    /// <summary>How many volunteer tasks the participant is assigned to.</summary>
    public int MyVolunteerTaskCount { get; private set; }
    public bool ShowSpeakerDeadlines { get; private set; }
    public bool ShowSponsorPipeline { get; private set; }
    public bool ShowAttendeeArea { get; private set; }
    public bool ShowOrganizerTools { get; private set; }

    // --- Section data -------------------------------------------------------
    public int OpenTaskCount { get; private set; }
    public bool HotelSubmitted { get; private set; }
    public bool DinnerSubmitted { get; private set; }
    public bool VolunteerSubmitted { get; private set; }
    public MasterClassBookingStatus? AttendeeBookingStatus { get; private set; }

    /// <summary>
    /// The unified participant checklist (REQUIREMENTS Top-8 #7) — the SAME shape
    /// the Tasks page and attendee My-event render via the shared
    /// <c>_ChecklistCard</c> partial, built by the shared
    /// <see cref="CommunityHub.Core.Participants.ParticipantChecklistBuilder"/>.
    /// </summary>
    public CommunityHub.Core.Participants.ParticipantChecklist Checklist { get; private set; } =
        new(System.Array.Empty<CommunityHub.Core.Participants.ChecklistRow>(),
            System.Array.Empty<CommunityHub.Core.Participants.ChecklistRow>());

    // --- Calendar sync ------------------------------------------------------
    /// <summary>The participant's iCal feed token (minted on first hub view).</summary>
    public string CalendarToken { get; private set; } = string.Empty;
    /// <summary>https://host/cal/{token}.ics — the subscribe/download URL.</summary>
    public string CalendarHttpsUrl { get; private set; } = string.Empty;
    /// <summary>webcal://host/cal/{token}.ics — one-click subscribe URL.</summary>
    public string CalendarWebcalUrl { get; private set; } = string.Empty;
    /// <summary>
    /// Whether the organizer has calendar sync enabled for this edition
    /// (<see cref="Event.CalendarSyncEnabled"/>). When false the "Add to my
    /// calendar" card is hidden — the feed itself also 404s.
    /// </summary>
    public bool CalendarSyncEnabled { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null)
        {
            // No session cookie ⇒ go STRAIGHT to the Event Hub sign-in (operator
            // 2026-06-21: the marketing landing page is removed entirely). The public
            // Sessions / Speakers / Sponsors pages remain reachable at their own
            // routes; only the root "/" no longer shows a landing.
            return RedirectToPage("/Login");
        }

        // First-time-after-login welcome: redirect once, then never again.
        // The /Welcome page sets WelcomeShownAt to UtcNow when the user clicks
        // OK, so subsequent /Index loads skip this branch.
        var welcomeShown = await _db.Participants
            .Where(p => p.Id == me.ParticipantId)
            .Select(p => p.WelcomeShownAt)
            .FirstOrDefaultAsync(ct);
        if (welcomeShown is null)
        {
            return RedirectToPage("/Welcome");
        }

        Me = me;

        var ev = await _db.Events
            .Where(e => e.Id == me.EventId)
            .Select(e => new { e.CommunityName, e.DisplayName, e.CalendarSyncEnabled })
            .FirstOrDefaultAsync(ct);
        if (ev is not null)
        {
            CommunityName = ev.CommunityName;
            EventDisplayName = ev.DisplayName;
            CalendarSyncEnabled = ev.CalendarSyncEnabled;
        }

        // Auto-seed speaker-deadline tasks on every visit by a speaker /
        // master-class speaker. Idempotent on SourceKey: existing tasks are
        // skipped, NEW deadlines added to the speaker-deadlines JSON config
        // appear on the next page load automatically -- no Functions run
        // required, no admin step.
        if (me.Role == ParticipantRole.Speaker)
        {
            try
            {
                await _speakerDeadlines.SeedAsync(me.EventId, ct);
            }
            catch (Exception ex)
            {
                // Don't fail the hub page if the config is missing/broken --
                // log it so the organizer can spot a deploy issue.
                _logger.LogWarning(ex,
                    "Speaker-deadline seeding failed for event {EventId}", me.EventId);
            }
        }

        ApplyRoleVisibility(me.Role);
        await LoadSectionDataAsync(me, ct);

        // Speaker landing card (operator 2026-06-21): show the speaker their own
        // sessions right on the hub (pending tasks already render via the checklist;
        // important dates link out to the Calendar).
        if (me.Role is ParticipantRole.Speaker)
        {
            SpeakerSessions = await _speakerSessions.GetMySessionsAsync(me.EventId, me.ParticipantId, me.Role, ct);
        }

        // Key-dates panel data -- loaded for everyone (the card shows the
        // edition's preDay / day1 / day2 / lockDate). Returns null when the
        // dates section is missing from event.<edition>.json.
        try { EventDates = _eventConfigLoader.Load(_eventConfigOptions.EventConfigPath).Dates; }
        catch (Exception ex)
        { _logger.LogWarning(ex, "Index: failed to load event dates from {Path}", _eventConfigOptions.EventConfigPath); }

        // Calendar sync: mint (idempotently) the participant's feed token and
        // build the subscribe/download URLs from the current request host so the
        // base URL is per-environment (dev vs prod) with no extra config. Skipped
        // entirely when the organizer has disabled calendar sync for the edition
        // (CalendarSyncEnabled) — the feed itself 404s, and the card stays hidden.
        if (CalendarSyncEnabled)
        {
            try
            {
                CalendarToken = await _calendarTokens.EnsureTokenAsync(me.ParticipantId, ct);
                var host = Request.Host.Value ?? string.Empty;
                CalendarHttpsUrl = $"{Request.Scheme}://{host}/cal/{CalendarToken}.ics";
                // webcal:// makes Outlook/Apple offer "Subscribe" directly; it is the
                // same path over the same TLS endpoint, just a scheme the OS handles.
                CalendarWebcalUrl = $"webcal://{host}/cal/{CalendarToken}.ics";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Index: failed to ensure calendar feed token for participant {Pid}",
                    me.ParticipantId);
            }
        }

        return Page();
    }

    /// <summary>
    /// One-off "Download .ics" for a single task (its own VEVENT, same stable
    /// UID as the feed so a later subscribe does not duplicate it). Scoped to
    /// the signed-in participant's own (or their sponsor company's) task.
    /// </summary>
    public async Task<IActionResult> OnGetCalendarItemAsync(int taskId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        var host = Request.Host.Value ?? "communityhub";
        var ics = await _calendarBuilder.BuildSingleTaskAsync(me.ParticipantId, taskId, host, ct);
        if (ics is null) return NotFound();

        return File(
            System.Text.Encoding.UTF8.GetBytes(ics),
            "text/calendar; charset=utf-8",
            $"task-{taskId}.ics");
    }

    /// <summary>
    /// Which sections each role sees. Defaults chosen per CONTEXT.md section 4:
    ///  - Organizer         : everything + organizer tools
    ///  - Speaker           : hotel, dinner, speaker deadlines (pre-day nuance
    ///                        folds into the seeded deadlines + entitlements)
    ///  - Volunteer         : hotel, dinner, volunteer shifts
    ///  - Sponsor           : sponsor pipeline
    ///  - Attendee          : attendee area only
    /// Tasks are shown to every role.
    /// </summary>
    private void ApplyRoleVisibility(ParticipantRole role)
    {
        switch (role)
        {
            case ParticipantRole.Organizer:
                // Organizers get hotel + dinner (they attend) + the sponsor pipeline
                // + organizer tools. They are NOT volunteers or speakers, so the
                // Volunteer-shifts and Speaker-hub cards are NOT shown on their hub
                // (operator 2026-06-20).
                ShowHotel = ShowDinner = true;
                ShowSponsorPipeline = true;
                ShowOrganizerTools = true;
                break;
            case ParticipantRole.Speaker:
                ShowHotel = ShowDinner = ShowSpeakerDeadlines = true;
                break;
            case ParticipantRole.Volunteer:
                ShowHotel = ShowDinner = ShowVolunteerShifts = true;
                ShowVolunteerWork = true;
                break;
            case ParticipantRole.Sponsor:
                ShowSponsorPipeline = true;
                break;
            case ParticipantRole.Attendee:
                ShowAttendeeArea = true;
                break;
        }
    }

    private async Task LoadSectionDataAsync(
        CurrentParticipant me, CancellationToken ct)
    {
        // Backfill auto-task rows for forms the participant submitted BEFORE
        // the auto-task feature went live (or before they re-visited the form
        // page). Keeps the unified Pending/Completed lists consistent with the
        // per-form cards: Hotel/Dinner/Volunteer-shifts "Submitted" cards <->
        // matching Done task rows.
        await BackfillFormAutoTasksAsync(me, ct);

        // The unified checklist (pending/completed + overdue + form deep-links) is
        // built by the SHARED ParticipantChecklistBuilder so the Hub, the Tasks page
        // and attendee My-event all show the same "what's still needed" view. It
        // already covers sponsor company-scoped tasks (AssignedParticipantId=null,
        // SponsorCompanyId set), so the Hub no longer says "all complete" while
        // /Sponsor/Tasks shows pending work.
        Checklist = await _checklist.BuildAsync(me.EventId, me.ParticipantId, ct);
        OpenTaskCount = Checklist.OpenCount;

        if (ShowHotel)
        {
            // "Submitted" = the participant made an explicit decision:
            //   declined (NeedsRoom = false), OR
            //   needs a room AND both dates filled.
            HotelSubmitted = await _db.HotelBookings.AnyAsync(
                h => h.EventId == me.EventId
                     && h.ParticipantId == me.ParticipantId
                     && (h.NeedsRoom == false
                         || (h.NeedsRoom && h.CheckInDate != null && h.CheckOutDate != null)),
                ct);
        }

        if (ShowDinner)
        {
            DinnerSubmitted = await _db.DinnerSignups.AnyAsync(
                d => d.EventId == me.EventId
                     && d.ParticipantId == me.ParticipantId, ct);
        }

        if (ShowVolunteerShifts)
        {
            VolunteerSubmitted = await _db.VolunteerAvailabilities.AnyAsync(
                v => v.EventId == me.EventId
                     && v.ParticipantId == me.ParticipantId, ct);
        }

        if (ShowVolunteerWork)
        {
            MyVolunteerTaskCount = await _db.VolunteerTaskAssignments
                .CountAsync(a => a.EventId == me.EventId
                                 && a.ParticipantId == me.ParticipantId, ct);
            IsCategorySupervisor = await _db.VolunteerCategories
                .AnyAsync(c => c.EventId == me.EventId
                               && c.SupervisorParticipantId == me.ParticipantId, ct);
        }

        if (ShowAttendeeArea)
        {
            AttendeeBookingStatus = await _db.Attendees
                .Where(a => a.EventId == me.EventId && a.Email == me.Email)
                .Select(a => (MasterClassBookingStatus?)a.BookingStatus)
                .FirstOrDefaultAsync(ct);
        }
    }

    /// <summary>
    /// Ensure the unified Pending / Completed task lists reflect the actual
    /// state of the per-form submissions (Hotel / Dinner / Volunteer-shifts /
    /// Swag). For each form where the participant has a record that meets the
    /// completion rule, upsert a SourceKey-tagged ParticipantTask in state Done.
    /// Lets users see prior submissions in the unified list even when those
    /// were saved before the auto-task feature was wired into each form.
    /// </summary>
    private async Task BackfillFormAutoTasksAsync(
        CurrentParticipant me, CancellationToken ct)
    {
        var entries = new List<(string key, string title, DateOnly? due, bool complete)>();

        // Hotel: declined OR (needs room AND both dates).
        var hotel = await _db.HotelBookings.FirstOrDefaultAsync(
            h => h.EventId == me.EventId && h.ParticipantId == me.ParticipantId, ct);
        if (hotel is not null)
        {
            bool complete = (!hotel.NeedsRoom)
                || (hotel.NeedsRoom && hotel.CheckInDate is not null && hotel.CheckOutDate is not null);
            entries.Add(($"hotel-form:{me.ParticipantId}",
                "Complete the Hotel form", null, complete));
        }

        // Dinner: explicit RSVP (Yes / No / Maybe, not NotAnswered).
        var dinner = await _db.DinnerSignups.FirstOrDefaultAsync(
            d => d.EventId == me.EventId && d.ParticipantId == me.ParticipantId, ct);
        if (dinner is not null && dinner.Rsvp != DinnerRsvp.NotAnswered)
        {
            entries.Add(($"dinner-form:{me.ParticipantId}",
                "Complete the Appreciation Dinner RSVP", null, true));
        }

        // Volunteer-shifts: at least one shift picked.
        var vol = await _db.VolunteerAvailabilities.FirstOrDefaultAsync(
            v => v.EventId == me.EventId && v.ParticipantId == me.ParticipantId, ct);
        if (vol is not null && !string.IsNullOrWhiteSpace(vol.SelectedShifts))
        {
            entries.Add(($"volunteer-form:{me.ParticipantId}",
                "Complete the Volunteer shifts sign-up", null, true));
        }

        // Nothing to do.
        if (entries.Count == 0) return;

        var keys = entries.Select(e => e.key).ToList();
        var existing = await _db.Tasks
            .Where(t => t.EventId == me.EventId
                        && t.AssignedParticipantId == me.ParticipantId
                        && keys.Contains(t.SourceKey!))
            .ToListAsync(ct);

        bool changed = false;
        foreach (var e in entries)
        {
            var row = existing.FirstOrDefault(x => x.SourceKey == e.key);
            if (row is null)
            {
                _db.Tasks.Add(new ParticipantTask
                {
                    EventId = me.EventId,
                    AssignedParticipantId = me.ParticipantId,
                    Title = e.title,
                    Description = null,
                    DueDate = e.due,
                    State = e.complete ? TaskState.Done : TaskState.Open,
                    SourceKey = e.key,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
                changed = true;
            }
            else if (e.complete && row.State != TaskState.Done)
            {
                row.State = TaskState.Done;
                changed = true;
            }
        }
        if (changed) await _db.SaveChangesAsync(ct);
    }
}
