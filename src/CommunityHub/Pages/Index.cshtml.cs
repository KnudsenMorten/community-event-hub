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
    private readonly CommunityHub.Core.Config.PartyTaskSeeder _partyTasks;
    private readonly CommunityHub.Forms.WizardStepTaskSeeder _wizardStepTasks;
    private readonly EventEditionConfigLoader _eventConfigLoader;
    private readonly EventConfigOptions _eventConfigOptions;
    private readonly CommunityHub.Core.Email.CalendarInviteEmailService _calendarInvite;
    private readonly CommunityHub.Core.Participants.ParticipantChecklistBuilder _checklist;
    private readonly CommunityHub.Core.Reminders.SpeakerSessionsService _speakerSessions;
    private readonly CommunityHub.Core.Domain.VolunteerStructureService _volunteerStructure;
    private readonly CommunityHub.Core.Participants.FormTaskReconciler _formTaskReconciler;
    private readonly ILogger<IndexModel> _logger;

    private readonly CommunityHub.Core.Tasks.TaskBodyService _taskBodies;
    private readonly CommunityHub.Core.Tasks.SponsorTaskPlaceholderBuilder _taskPlaceholders;

    public IndexModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        SpeakerDeadlineSeeder speakerDeadlines,
        CommunityHub.Core.Config.PartyTaskSeeder partyTasks,
        CommunityHub.Forms.WizardStepTaskSeeder wizardStepTasks,
        EventEditionConfigLoader eventConfigLoader,
        EventConfigOptions eventConfigOptions,
        CommunityHub.Core.Email.CalendarInviteEmailService calendarInvite,
        CommunityHub.Core.Participants.ParticipantChecklistBuilder checklist,
        CommunityHub.Core.Reminders.SpeakerSessionsService speakerSessions,
        CommunityHub.Core.Reminders.MasterClassSignupService masterClassSignups,
        CommunityHub.Core.Domain.VolunteerStructureService volunteerStructure,
        CommunityHub.Core.Participants.FormTaskReconciler formTaskReconciler,
        // §684 — the migrated-body pipeline. The home page's "add to calendar" can reach a SPONSOR
        // company task (see the query in OnPostAddTaskReminderAsync), so it has to know about
        // migrated bodies too or that one entry point silently sends an empty calendar entry.
        CommunityHub.Core.Tasks.TaskBodyService taskBodies,
        CommunityHub.Core.Tasks.SponsorTaskPlaceholderBuilder taskPlaceholders,
        ILogger<IndexModel> logger)
    {
        _taskBodies = taskBodies;
        _taskPlaceholders = taskPlaceholders;
        _db = db;
        _participant = participant;
        _speakerDeadlines = speakerDeadlines;
        _partyTasks = partyTasks;
        _wizardStepTasks = wizardStepTasks;
        _eventConfigLoader = eventConfigLoader;
        _eventConfigOptions = eventConfigOptions;
        _calendarInvite = calendarInvite;
        _checklist = checklist;
        _speakerSessions = speakerSessions;
        _masterClassSignups = masterClassSignups;
        _volunteerStructure = volunteerStructure;
        _formTaskReconciler = formTaskReconciler;
        _logger = logger;
    }

    private readonly CommunityHub.Core.Reminders.MasterClassSignupService _masterClassSignups;

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
    /// <summary>Show the Lunch staff card — crew roles (Media + Event partner) who are
    /// on site and have a lunch headcount, mirroring their Hotel/Dinner cards.</summary>
    public bool ShowLunch { get; private set; }
    /// <summary>Show the Swag staff card — same crew roles as <see cref="ShowLunch"/>.</summary>
    public bool ShowSwag { get; private set; }
    /// <summary>Show the crew intro card (Media + Event partner): a short orienting block
    /// pointing first-time visitors at the Get-Started wizard — their hub home otherwise
    /// has no role card at all after §161 removed the per-form status cards.</summary>
    public bool ShowCrewIntro { get; private set; }
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
    public bool LunchSubmitted { get; private set; }
    public bool SwagSubmitted { get; private set; }
    public bool VolunteerSubmitted { get; private set; }
    public MasterClassBookingStatus? AttendeeBookingStatus { get; private set; }

    /// <summary>Title of the attendee's CONFIRMED Master Class (null if none) — same source as /Attendee/Index.</summary>
    public string? AttendeeConfirmedTitle { get; private set; }

    /// <summary>
    /// True only when the attendee holds the 2-day ticket
    /// (<see cref="TicketStatus.TwoDay"/>) that grants a Master Class seat. When
    /// false the card shows a neutral "no Master Class with this ticket" message
    /// instead of the red "no seat reserved" warning — that warning only makes
    /// sense for an eligible attendee who has not yet booked.
    /// </summary>
    public bool AttendeeMasterClassEligible { get; private set; }

    /// <summary>
    /// The unified participant checklist (REQUIREMENTS Top-8 #7) — the SAME shape
    /// the Tasks page and attendee My-event render via the shared
    /// <c>_ChecklistCard</c> partial, built by the shared
    /// <see cref="CommunityHub.Core.Participants.ParticipantChecklistBuilder"/>.
    /// </summary>
    public CommunityHub.Core.Participants.ParticipantChecklist Checklist { get; private set; } =
        new(System.Array.Empty<CommunityHub.Core.Participants.ChecklistRow>(),
            System.Array.Empty<CommunityHub.Core.Participants.ChecklistRow>());

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

        // §248: the /Welcome first-sign-in interstitial is RETIRED (operator
        // 2026-07-07, fewest-clicks §249) — every arrival (magic-link deep link or
        // hub home) lands directly on the target page; the welcome EMAIL carries the
        // orientation (primary Get-Started + secondary Browse-the-hub buttons). The
        // /Welcome page itself stays routable for the curious, and still stamps
        // WelcomeShownAt on Continue — but nothing redirects there any more.

        Me = me;

        var ev = await _db.Events
            .Where(e => e.Id == me.EventId)
            .Select(e => new { e.CommunityName, e.DisplayName })
            .FirstOrDefaultAsync(ct);
        if (ev is not null)
        {
            CommunityName = ev.CommunityName;
            EventDisplayName = ev.DisplayName;
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

        // §164: ensure the per-participant "party sign-up" task exists for the staff
        // roles (Sponsor/Speaker/Volunteer/EventPartner/Organizer) on their first hub
        // visit — idempotent, so it surfaces in My Tasks + Get Started without waiting
        // for the nightly reminder job. No-op for attendees + when no party is active.
        try
        {
            await _partyTasks.EnsureForParticipantAsync(me.EventId, me.ParticipantId, me.Role, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Party-task seeding failed for event {EventId}", me.EventId);
        }

        // §173e: ensure My-Tasks MIRRORS the Get-Started journey — seed a task for every
        // Get-Started step this role has (idempotent; no-op for roles without a
        // per-participant wizard). FormTaskReconciler (run by the checklist) keeps each
        // task's done-state synced to its step's completion signal.
        try
        {
            await _wizardStepTasks.EnsureForParticipantAsync(me.EventId, me.ParticipantId, me.Role, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Wizard-step task seeding failed for event {EventId}", me.EventId);
        }

        ApplyRoleVisibility(me.Role);
        await LoadSectionDataAsync(me, ct);

        // Speaker landing card (operator 2026-06-21): show the speaker their own
        // sessions right on the hub (pending tasks already render via the checklist).
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

        return Page();
    }

    /// <summary>
    /// §193b "Add Reminder": e-mail the signed-in participant a calendar INVITATION
    /// for one task's due date (replacing the old "Download .ics"). Scoped to the
    /// participant's own (or their sponsor company's) dated task; the invite goes to
    /// their chosen calendar / override e-mail. Re-clicking updates the same entry
    /// (stable UID). Fail-safe: a send failure still redirects with a soft message.
    /// </summary>
    public async Task<IActionResult> OnPostAddReminderAsync(int taskId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        var host = Request.Host.Value ?? "communityhub";
        var sponsorCompanyId = await _db.Participants
            .AsNoTracking()
            .Where(x => x.Id == me.ParticipantId)
            .Select(x => x.SponsorCompanyId)
            .FirstOrDefaultAsync(ct);
        var task = await _db.Tasks
            .AsNoTracking()
            .Where(t => t.Id == taskId
                        && t.EventId == me.EventId
                        && t.DueDate != null
                        && (t.AssignedParticipantId == me.ParticipantId
                            || (sponsorCompanyId != null
                                && t.SponsorCompanyId == sponsorCompanyId)))
            .FirstOrDefaultAsync(ct);

        if (task is null || task.DueDate is null)
        {
            TempData["CalendarInviteMessage"] = "That task could not be found.";
            return RedirectToPage();
        }

        var start = new DateTimeOffset(task.DueDate.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        // 🔒 §684.21 — a MIGRATED task carries Description = null (§684.14), so without this it would
        // fall through to the generic "Deadline from your Event Hub" line and drop the whole body
        // from the calendar entry. §684.10's PLAIN-TEXT flavour: a button becomes "label: url", and
        // no emphasis marker leaks (§685).
        string description;
        try
        {
            var rendered = await _taskBodies.RenderAsync(
                task,
                CommunityHub.Core.Tasks.TaskBodyFlavour.PlainText,
                await _taskPlaceholders.BuildAsync(me.EventId, sponsorCompanyId, ct),
                ct: ct);

            description = rendered is not null && !string.IsNullOrWhiteSpace(rendered.Html)
                ? rendered.Html
                : LegacyDescription(task.Description);
        }
        catch (Exception ex)
        {
            // Fail-soft (§682): a body that will not render must not stop the DATE reaching the
            // sponsor's calendar, which is the point of the invite.
            _logger.LogError(
                ex, "Could not render the migrated body for task {TaskId} into a calendar invite.",
                task.Id);
            description = LegacyDescription(task.Description);
        }

        static string LegacyDescription(string? stored) =>
            string.IsNullOrWhiteSpace(stored)
                ? "Deadline from your Event Hub. Open the hub to update this item."
                : CommunityHub.Core.Email.TaskMarkup.ToPlainText(stored);

        try
        {
            var sent = await _calendarInvite.SendItemInviteAsync(
                me.ParticipantId,
                uid: $"task-{task.Id}@{host}",
                summary: task.Title,
                description: description,
                location: null,
                start: start,
                end: start.AddDays(1),
                allDay: true,
                fileName: "reminder.ics",
                introHtml: $"Here is a reminder for <strong>{System.Net.WebUtility.HtmlEncode(task.Title)}</strong>, due {task.DueDate.Value:d MMM yyyy}.",
                ct: ct);
            TempData["CalendarInviteMessage"] = sent
                ? sent.Confirmation("Reminder")
                : "Calendar invitations are turned off for this event.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Add Reminder failed for task {TaskId}", taskId);
            TempData["CalendarInviteMessage"] = "We couldn't send that reminder just now — please try again.";
        }

        return RedirectToPage();
    }

    /// <summary>
    /// §321 (operator 2026-07-24): ONE button, one calendar invitation PER dated pending
    /// task (own + the sponsor company's). Same per-task UID as the single-task handler
    /// (<c>task-{id}@{host}</c>), so re-clicking updates the same calendar entries instead
    /// of duplicating them. Fail-soft per task; reports the sent count.
    /// </summary>
    public async Task<IActionResult> OnPostAddReminderAllAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        var host = Request.Host.Value ?? "communityhub";
        var sponsorCompanyId = await _db.Participants
            .AsNoTracking()
            .Where(x => x.Id == me.ParticipantId)
            .Select(x => x.SponsorCompanyId)
            .FirstOrDefaultAsync(ct);
        var tasks = await _db.Tasks
            .AsNoTracking()
            .Where(t => t.EventId == me.EventId
                        && t.DueDate != null
                        && t.State != TaskState.Done
                        && (t.AssignedParticipantId == me.ParticipantId
                            || (sponsorCompanyId != null
                                && t.SponsorCompanyId == sponsorCompanyId)))
            .OrderBy(t => t.DueDate)
            .Select(t => new { t.Id, t.Title, t.Description, t.DueDate })
            .ToListAsync(ct);
        if (tasks.Count == 0)
        {
            TempData["CalendarInviteMessage"] = "No dated pending tasks to send reminders for.";
            return RedirectToPage();
        }

        var sent = 0;
        var invitesOff = false;
        foreach (var task in tasks)
        {
            var start = new DateTimeOffset(task.DueDate!.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var description = string.IsNullOrWhiteSpace(task.Description)
                ? "Deadline from your Event Hub. Open the hub to update this item."
                : CommunityHub.Core.Email.TaskMarkup.ToPlainText(task.Description);
            try
            {
                var ok = await _calendarInvite.SendItemInviteAsync(
                    me.ParticipantId,
                    uid: $"task-{task.Id}@{host}",
                    summary: task.Title,
                    description: description,
                    location: null,
                    start: start,
                    end: start.AddDays(1),
                    allDay: true,
                    fileName: "reminder.ics",
                    introHtml: $"Here is a reminder for <strong>{System.Net.WebUtility.HtmlEncode(task.Title)}</strong>, due {task.DueDate.Value:d MMM yyyy}.",
                    ct: ct);
                if (ok) sent++; else invitesOff = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Add-all reminders: send failed for task {TaskId}", task.Id);
            }
        }

        TempData["CalendarInviteMessage"] = sent == 0
            ? (invitesOff
                ? "Calendar invitations are turned off for this event."
                : "We couldn't send the reminders just now — please try again.")
            : $"Sent {sent} calendar invitation{(sent == 1 ? "" : "s")} — one per pending task. Check your inbox.";
        return RedirectToPage();
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
                // Shift self-signup wizard is deprecated, so ShowVolunteerShifts
                // is no longer set (operator). Volunteers still get hotel + dinner
                // + the volunteer-work card.
                ShowHotel = ShowDinner = true;
                ShowVolunteerWork = true;
                break;
            case ParticipantRole.Sponsor:
                ShowSponsorPipeline = true;
                break;
            case ParticipantRole.Attendee:
                ShowAttendeeArea = true;
                break;
            case ParticipantRole.Media:
            case ParticipantRole.EventPartner:
                // Press/photo/video crew and event-partner orgs attend like staff:
                // hotel + dinner + the lunch headcount + swag (ParticipantRole.cs /
                // OrderEntitlements). They are entitled to Lunch + Swag and the nav shows
                // both, so the hub home surfaces the Lunch/Swag staff cards too (bug fix:
                // EventPartner was missing the staff surface that Media should also have).
                ShowHotel = ShowDinner = true;
                ShowLunch = ShowSwag = true;
                // After §161 removed the per-form status cards these roles' hub home had
                // no orienting block at all (every other role gets one) — show the crew
                // intro card pointing first-time visitors at Get started.
                ShowCrewIntro = true;
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

        // Bring OPEN form-owned + mirroring speaker-deadline tasks in line with the
        // actual per-form data the participant has already submitted (Hotel/Dinner/
        // Lunch/Swag/Volunteer/Travel) BEFORE the checklist + cards are computed, so
        // a submission saved before its form wired up auto-completion isn't shown as
        // still pending. Idempotent + no-op when nothing needs changing.
        await _formTaskReconciler.ReconcileAsync(me.EventId, me.ParticipantId, ct);

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

        if (ShowLunch)
        {
            LunchSubmitted = await _db.LunchSignups.AnyAsync(
                l => l.EventId == me.EventId
                     && l.ParticipantId == me.ParticipantId, ct);
        }

        if (ShowSwag)
        {
            SwagSubmitted = await _db.SwagPreferences.AnyAsync(
                s => s.EventId == me.EventId
                     && s.ParticipantId == me.ParticipantId, ct);
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
            // Same authority the nav uses — covers the legacy single-supervisor
            // column AND the multi-supervisor Buckets join table.
            IsCategorySupervisor = await _volunteerStructure
                .IsSupervisorAsync(me.EventId, me.ParticipantId, ct);
        }

        if (ShowAttendeeArea)
        {
            // Use the SAME source of truth as /Attendee/Index — the Master Class
            // SIGNUP status — not the denormalised Attendees.BookingStatus (which
            // was stale, so the home card showed "not reserved" for confirmed
            // attendees; operator 2026-06-23).
            var attendee = await _masterClassSignups.ResolveByEmailAsync(me.EventId, me.Email, ct);
            if (attendee is not null)
            {
                // Only a 2-day ticket grants a Master Class seat. Drives the card's
                // neutral-vs-red messaging when no seat is booked.
                AttendeeMasterClassEligible = attendee.TicketStatus == TicketStatus.TwoDay;

                var mine = await _masterClassSignups.GetForAttendeeAsync(attendee.EventId, attendee.Id, ct);
                var confirmed = mine.Where(s => s.Status == MasterClassSignupStatus.Confirmed).ToList();
                AttendeeConfirmedTitle = confirmed.FirstOrDefault()?.Title;
                AttendeeBookingStatus = confirmed.Count > 1
                    ? MasterClassBookingStatus.MultipleBookings
                    : confirmed.Count == 1
                        ? MasterClassBookingStatus.Booked
                        : (MasterClassBookingStatus?)null;
            }
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

        // Lunch: a saved signup row (pre-day and/or main-day) is the completion
        // signal, mirroring the Hotel/Dinner backfill so entitled roles get the
        // task proactively.
        var lunch = await _db.LunchSignups.FirstOrDefaultAsync(
            l => l.EventId == me.EventId && l.ParticipantId == me.ParticipantId, ct);
        if (lunch is not null)
        {
            entries.Add(($"lunch-form:{me.ParticipantId}",
                "Complete the Lunch form", null, true));
        }

        // Swag: a saved preference row is the completion signal.
        var swag = await _db.SwagPreferences.FirstOrDefaultAsync(
            s => s.EventId == me.EventId && s.ParticipantId == me.ParticipantId, ct);
        if (swag is not null)
        {
            entries.Add(($"swag-form:{me.ParticipantId}",
                "Complete the Swag form", null, true));
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
