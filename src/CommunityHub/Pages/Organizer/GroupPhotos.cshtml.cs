using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
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

    public GroupPhotosModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        EmailTemplateProvider templates,
        IEmailSender emailSender,
        TimeProvider clock,
        IEmailContextAccessor? context = null)
    {
        _db = db;
        _participant = participant;
        _templates = templates;
        _emailSender = emailSender;
        _clock = clock;
        _context = context;
    }

    public bool AccessDenied { get; private set; }
    public string? Notice { get; private set; }
    public List<GroupPhotoRegistration> Registrations { get; private set; } = new();
    [BindProperty(SupportsGet = true)] public string? Msg { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        Notice = Msg;
        Registrations = await _db.GroupPhotoRegistrations
            .Where(r => r.EventId == me.EventId)
            .OrderBy(r => r.ScheduledAtUtc == null)   // unscheduled last
            .ThenBy(r => r.ScheduledAtUtc)
            .ThenBy(r => r.CompanyName)
            .ToListAsync(ct);
        return Page();
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

        // Token values are HTML-encoded by the renderer at the seam
        // (EmailTemplateRenderer, REQUIREMENTS §10c-4) — pass raw text.
        var tokens = _templates.NewTokenSet();
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
            description: row.Notes ?? "Group photo session",
            location: string.IsNullOrWhiteSpace(row.Location) ? row.Event.VenueName ?? "" : row.Location!,
            startUtc: startUtc,
            endUtc: endUtc,
            organizerEmail: me.Email,
            organizerName: me.FullName);

        // The calendar invite goes to the APPOINTED COMPANY LEAD ONLY (operator
        // 2026-06-22). InternalParticipants is reference-only and not invited.
        var lead = row.ContactEmail?.Trim();
        if (string.IsNullOrWhiteSpace(lead) || !lead.Contains('@'))
        {
            return RedirectToPage(new { Msg = $"'{row.CompanyName}' has no valid lead email - set the company lead's address before sending the invite." });
        }

        bool sent;
        // Ring-governed by the group-photo-invites feature (operator 2026-06-22).
        using (_context?.Set(new EmailContext(
            "group-photo-invite", row.EventId, null, row.ContactName,
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
