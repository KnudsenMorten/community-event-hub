using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Group photos management (README: Organizers Hub). Register a company +
/// lead contact, schedule the photo slot, and send a calendar invite to the
/// lead plus internal participants. The ICS UID is stable per registration,
/// so re-sending after a slot move UPDATES the recipients' calendar entry
/// instead of duplicating it.
/// </summary>
[Authorize]
public class GroupPhotosModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly EmailTemplateProvider _templates;
    private readonly IEmailSender _emailSender;
    private readonly TimeProvider _clock;
    private readonly IEmailContextAccessor? _context;
    private readonly CommunityHub.Core.Integrations.GroupPhotoScheduleService _schedule;

    public GroupPhotosModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        EmailTemplateProvider templates,
        IEmailSender emailSender,
        TimeProvider clock,
        CommunityHub.Core.Integrations.GroupPhotoScheduleService schedule,
        IEmailContextAccessor? context = null)
    {
        _db = db;
        _participant = participant;
        _templates = templates;
        _emailSender = emailSender;
        _clock = clock;
        _schedule = schedule;
        _context = context;
    }

    public bool AccessDenied { get; private set; }
    public string? Notice { get; private set; }
    public List<GroupPhotoRegistration> Registrations { get; private set; } = new();
    [BindProperty(SupportsGet = true)] public string? Msg { get; set; }

    // ---- §1077 stage 5: the slots, the plan, the publish -------------------
    public IReadOnlyList<GroupPhotoSlot> Slots { get; private set; } = Array.Empty<GroupPhotoSlot>();
    public GroupPhotoPlan? Plan { get; private set; }
    [BindProperty] public string? SlotText { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        Notice = Msg;
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    private async Task LoadAsync(int eventId, CancellationToken ct)
    {
        Registrations = await _db.GroupPhotoRegistrations
            .Where(r => r.EventId == eventId)
            .OrderBy(r => r.ScheduledAtUtc == null)   // unscheduled last
            .ThenBy(r => r.ScheduledAtUtc)
            .ThenBy(r => r.CompanyName)
            .ToListAsync(ct);

        Slots = await _schedule.SlotsAsync(eventId, ct);
    }

    /// <summary>
    /// §1077 stage 5 — paste the operator's timeslots, one per line
    /// (<c>2027-02-09 11:30</c>). ⚠️ Unreadable lines are REPORTED, never skipped.
    /// </summary>
    public async Task<IActionResult> OnPostAddSlotsAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        // 🔴 The LAST day is the "main day" — anything before it is a pre-day slot. Operator
        // 2026-08-11: *"it is 2 days - preday 9 feb 2027 … and main day 10 feb 2027"*, and
        // *"attendees are there 2 days only"*. Event.PreDayDate is the 8th (master class / setup),
        // which is NOT one of the photo days.
        var mainDay = await _db.Events.Where(e => e.Id == me.EventId)
            .Select(e => (DateOnly?)e.EndDate).FirstOrDefaultAsync(ct);

        var (added, rejected) = await _schedule.AddSlotsFromTextAsync(me.EventId, SlotText, mainDay, ct: ct);

        var msg = $"Added {added} slot(s).";
        if (rejected.Count > 0) msg += " Not added: " + string.Join(" · ", rejected);
        return RedirectToPage(new { Msg = msg });
    }

    /// <summary>A starter set for testing — 25 slots the operator then edits or replaces.</summary>
    public async Task<IActionResult> OnPostStarterSlotsAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var added = await _schedule.AddStarterSetAsync(me.EventId, ct);
        return RedirectToPage(new { Msg = added > 0
            ? $"Added a starter set of {added} slots. Edit or delete them and paste your real times."
            : "Slots already exist — the starter set only fills an empty schedule, so it cannot "
              + "double a real one." });
    }

    public async Task<IActionResult> OnPostDeleteSlotAsync(int id, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var problem = await _schedule.DeleteSlotAsync(id, ct);
        return RedirectToPage(new { Msg = problem ?? "Slot removed." });
    }

    /// <summary>Propose a plan and SAVE it as planned times. Tells nobody.</summary>
    public async Task<IActionResult> OnPostProposeAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var plan = await _schedule.ProposeAsync(me.EventId, ct);
        var saved = await _schedule.SaveProposalAsync(me.EventId, plan, ct);

        Notice = $"Proposed {plan.Assignments.Count} slot(s) — {saved} new, "
               + $"{plan.Assignments.Count - saved} already published and left alone. "
               + $"{plan.UnusedSlots.Count} slot(s) unused."
               + (plan.Unplaced.Count > 0
                   ? " ⚠️ No slot for: " + string.Join(" · ", plan.Unplaced.Select(u => $"{u.CompanyName} ({u.Reason})"))
                   : string.Empty);

        Plan = plan;
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>
    /// 🔴 PUBLISH — the moment the plan becomes a promise: the time appears on each company's own
    /// page and the planner treats it as pinned from then on.
    /// </summary>
    public async Task<IActionResult> OnPostPublishAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var result = await _schedule.PublishAsync(me.EventId, ct);
        return RedirectToPage(new { Msg = $"Published: {result}. Each company now sees its time on "
                                        + "its own page." });
    }

    /// <summary>The running order as Excel, for the partner coordinating the photos from our side.</summary>
    public async Task<IActionResult> OnGetPartnerExcelAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var rows = await _schedule.ScheduleAsync(me.EventId, ct);

        using var wb = new ClosedXML.Excel.XLWorkbook();
        var ws = wb.Worksheets.Add("Group photos");
        var headers = new[]
        {
            "Time (UTC)", "Day", "Company", "People", "Contact", "E-mail", "Mobile", "Location", "State",
        };
        for (var i = 0; i < headers.Length; i++)
        {
            ws.Cell(1, i + 1).Value = headers[i];
            ws.Cell(1, i + 1).Style.Font.Bold = true;
        }

        var r = 2;
        foreach (var row in rows)
        {
            // ⚠️ A real date cell, not text: a running order people sort by time is the whole point,
            // and text sorts 9:45 after 14:30.
            if (row.StartUtc is { } when)
            {
                ws.Cell(r, 1).Value = when.UtcDateTime;
                ws.Cell(r, 1).Style.DateFormat.Format = "yyyy-mm-dd hh:mm";
            }
            ws.Cell(r, 2).Value = row.Day;
            ws.Cell(r, 3).Value = row.CompanyName;
            ws.Cell(r, 4).Value = row.People;
            ws.Cell(r, 5).Value = row.ContactName;
            ws.Cell(r, 6).Value = row.ContactEmail;
            ws.Cell(r, 7).Value = row.ContactMobile;
            ws.Cell(r, 8).Value = row.Location;
            ws.Cell(r, 9).Value = row.State;
            r++;
        }
        ws.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return File(ms.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"group-photos-{_clock.GetUtcNow():yyyyMMdd}.xlsx");
    }

    /// <summary>Every scheduled photo as ONE calendar file — the partner imports it once.</summary>
    public async Task<IActionResult> OnGetPartnerCalendarAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var ics = await _schedule.ScheduleIcsAsync(me.EventId, ct);
        return File(System.Text.Encoding.UTF8.GetBytes(ics), "text/calendar",
            $"group-photos-{_clock.GetUtcNow():yyyyMMdd}.ics");
    }

    public async Task<IActionResult> OnPostCreateAsync(
        string companyName, string contactName, string contactEmail,
        int ticketCount, string? internalParticipants, string? location, string? notes,
        DateTime? scheduledLocal, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        if (string.IsNullOrWhiteSpace(companyName) || string.IsNullOrWhiteSpace(contactEmail))
        {
            return RedirectToPage(new { Msg = "Company name and contact email are required." });
        }

        _db.GroupPhotoRegistrations.Add(new GroupPhotoRegistration
        {
            EventId = me.EventId,
            CompanyName = companyName.Trim(),
            ContactName = (contactName ?? string.Empty).Trim(),
            ContactEmail = contactEmail.Trim(),
            TicketCount = Math.Max(0, ticketCount),
            InternalParticipants = (internalParticipants ?? string.Empty).Trim(),
            Location = location?.Trim(),
            Notes = notes?.Trim(),
            // The form's datetime-local is Danish wall-clock; store as the
            // matching UTC instant (CET/CEST offset resolved per date).
            ScheduledAtUtc = ToUtc(scheduledLocal),
            CreatedAt = _clock.GetUtcNow(),
        });
        await _db.SaveChangesAsync(ct);
        return RedirectToPage(new { Msg = $"Registered '{companyName}'." });
    }

    public async Task<IActionResult> OnPostUpdateAsync(
        int id, string contactName, string contactEmail,
        int ticketCount, string? internalParticipants, string? location, string? notes,
        DateTime? scheduledLocal, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var row = await _db.GroupPhotoRegistrations.FirstOrDefaultAsync(
            r => r.Id == id && r.EventId == me.EventId, ct);
        if (row is null) return RedirectToPage(new { Msg = "Registration not found." });

        row.ContactName = (contactName ?? string.Empty).Trim();
        row.ContactEmail = (contactEmail ?? string.Empty).Trim();
        row.TicketCount = Math.Max(0, ticketCount);
        row.InternalParticipants = (internalParticipants ?? string.Empty).Trim();
        row.Location = location?.Trim();
        row.Notes = notes?.Trim();
        row.ScheduledAtUtc = ToUtc(scheduledLocal);
        await _db.SaveChangesAsync(ct);
        return RedirectToPage(new { Msg = $"Updated '{row.CompanyName}'." });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var row = await _db.GroupPhotoRegistrations.FirstOrDefaultAsync(
            r => r.Id == id && r.EventId == me.EventId, ct);
        if (row is not null)
        {
            _db.GroupPhotoRegistrations.Remove(row);
            await _db.SaveChangesAsync(ct);
        }
        return RedirectToPage(new { Msg = "Registration removed." });
    }

    /// <summary>Send (or re-send) the calendar invite to the lead + internal staff.</summary>
    public async Task<IActionResult> OnPostSendInviteAsync(int id, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var row = await _db.GroupPhotoRegistrations
            .Include(r => r.Event)
            .FirstOrDefaultAsync(r => r.Id == id && r.EventId == me.EventId, ct);
        if (row is null) return RedirectToPage(new { Msg = "Registration not found." });
        if (!row.Qualifies)
        {
            return RedirectToPage(new { Msg = $"'{row.CompanyName}' has {row.TicketCount} ticket(s) - the group photo is for companies with more than {GroupPhotoRegistration.QualifyingTicketThreshold} tickets (volume package). Update the ticket count to send." });
        }
        if (row.ScheduledAtUtc is null)
        {
            return RedirectToPage(new { Msg = $"'{row.CompanyName}' has no slot yet - set a time before sending the invite." });
        }

        var startUtc = row.ScheduledAtUtc.Value;
        var endUtc = startUtc.AddMinutes(row.DurationMinutes);
        var slotLocal = ToLocal(startUtc);

        // §169: the invite goes to the company's appointed lead. When that lead is a
        // known Participant in this edition, their {{hubUrl}} CTA becomes their personal
        // /go/{token} auto-login magic-link; an external lead with no Participant keeps the
        // plain hub URL (fail-safe — NewTokenSet swallows a null id and never throws).
        var leadNorm = (row.ContactEmail ?? string.Empty).Trim().ToLowerInvariant();
        int? leadPid = leadNorm.Length == 0
            ? null
            : await _db.Participants
                .Where(p => p.EventId == row.EventId && p.Email.ToLower() == leadNorm)
                .Select(p => (int?)p.Id)
                .FirstOrDefaultAsync(ct);

        // Token values are HTML-encoded by the renderer at the seam
        // (EmailTemplateRenderer, REQUIREMENTS §10c-4) — pass raw text.
        var tokens = _templates.NewTokenSet(leadPid);
        tokens["contactName"] = string.IsNullOrWhiteSpace(row.ContactName) ? "there" : row.ContactName.Split(' ')[0];
        tokens["companyName"] = row.CompanyName;
        tokens["eventDisplayName"] = row.Event.DisplayName;
        tokens["slotTime"] = slotLocal.ToString("dddd d MMMM yyyy, HH:mm") + " (local)";
        tokens["location"] = string.IsNullOrWhiteSpace(row.Location) ? row.Event.VenueName ?? "the venue" : row.Location;
        var rendered = _templates.Render("group-photo-invite", tokens);

        // Stable UID per registration: a slot move + re-send UPDATES the
        // entry in every recipient's calendar.
        var ics = IcsCalendarBuilder.BuildVEvent(
            uid: $"group-photo-{row.EventId}-{row.Id}@communityhub",
            summary: $"Group photo - {row.CompanyName} ({row.Event.DisplayName})",
            // §1077.8 — the operator's own invitation wording, shared with the coordinator's own
            // download so the two calendar entries in circulation say the same thing.
            description: string.IsNullOrWhiteSpace(row.Notes)
                ? GroupPhotoInviteText.Build(row.Event.DisplayName, row.Event.CommunityName)
                : row.Notes,
            location: string.IsNullOrWhiteSpace(row.Location) ? row.Event.VenueName ?? "" : row.Location!,
            startUtc: startUtc,
            endUtc: endUtc,
            organizerEmail: me.Email,
            organizerName: me.FullName,
            // §234: a real invite needs the recipient as ATTENDEE or clients won't
            // offer accept/decline.
            attendeeEmail: row.ContactEmail?.Trim(),
            attendeeName: row.ContactName);

        // The calendar invite goes to the APPOINTED COMPANY LEAD ONLY (operator
        // 2026-06-22). InternalParticipants is reference-only and not invited.
        var lead = row.ContactEmail?.Trim();
        if (string.IsNullOrWhiteSpace(lead) || !lead.Contains('@'))
        {
            return RedirectToPage(new { Msg = $"'{row.CompanyName}' has no valid lead email - set the company lead's address before sending the invite." });
        }

        bool sent;
        // Ring-governed by the group-photo-invites feature (operator 2026-06-22).
        // 🔒 §707.2b — the key was already the FIRST argument (`Category`), not `TemplateName`, so the
        // gate saw no mail identity and used the feature ring. Position, not name.
        using (_context?.Set(new EmailContext(
            "group-photo-invite", row.EventId, null, row.ContactName,
            TemplateName: "group-photo-invite",
            FeatureKey: "group-photo-invites")))
        {
            try
            {
                await _emailSender.SendWithIcsAsync(
                    lead, rendered.Subject, rendered.HtmlBody, ics,
                    $"group-photo-{row.CompanyName}.ics".Replace(' ', '-'), ct);
                sent = true;
            }
            catch { sent = false; }
        }

        row.InviteLastSentAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return RedirectToPage(new { Msg = sent
            ? $"Invite for '{row.CompanyName}' sent to the company lead ({lead})."
            : $"Invite for '{row.CompanyName}' could NOT be sent (delivery failed or blocked by the ring/kill switch)." });
    }

    // --- Danish wall-clock <-> UTC (same convention as the hotel invite) ---
    private static readonly TimeZoneInfo DanishTz =
        TimeZoneInfo.FindSystemTimeZoneById("Europe/Copenhagen");

    private static DateTimeOffset? ToUtc(DateTime? local) =>
        local is null
            ? null
            : new DateTimeOffset(
                TimeZoneInfo.ConvertTimeToUtc(
                    DateTime.SpecifyKind(local.Value, DateTimeKind.Unspecified), DanishTz));

    private static DateTime ToLocal(DateTimeOffset utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(utc.UtcDateTime, DanishTz);
}
