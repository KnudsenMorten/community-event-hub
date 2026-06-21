using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

[Authorize]
public class SendInvitationsModel : PageModel
{
    private const string ReminderType = "invitation";

    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly MagicLinkService _magic;
    private readonly IEmailSender _emailSender;
    private readonly EmailTemplateProvider _templates;
    private readonly CommunityHub.Branding.ActiveEventNameProvider _activeEvent;
    private readonly TimeProvider _clock;

    public SendInvitationsModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        MagicLinkService magic,
        IEmailSender emailSender,
        EmailTemplateProvider templates,
        CommunityHub.Branding.ActiveEventNameProvider activeEvent,
        TimeProvider clock)
    {
        _db = db;
        _participant = participant;
        _magic = magic;
        _emailSender = emailSender;
        _templates = templates;
        _activeEvent = activeEvent;
        _clock = clock;
    }

    public bool AccessDenied { get; private set; }
    public int ActiveCount { get; private set; }
    public int AlreadySentCount { get; private set; }
    public string? Message { get; private set; }
    public List<RoleCount> ByRole { get; private set; } = new();
    public record RoleCount(string Role, int Active, int AlreadySent, int Pending);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        await LoadCountsAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostSendAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var participants = await _db.Participants
            .Where(p => p.EventId == me.EventId && p.IsActive)
            .Select(p => new { p.Id, p.Email, p.FullName, p.Role })
            .ToListAsync(ct);

        var alreadySent = (await _db.SentReminders
            .Where(s => s.EventId == me.EventId && s.ReminderType == ReminderType)
            .Select(s => s.RecipientEmail)
            .ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var pending = participants.Where(p => !alreadySent.Contains(p.Email)).ToList();
        if (pending.Count == 0)
        {
            await LoadCountsAsync(me.EventId, ct);
            Message = "All active participants already received an invitation. " +
                      "Nothing to send.";
            return Page();
        }

        var origin = $"{Request.Scheme}://{Request.Host}";
        var communityName = _activeEvent.GetCommunityName();
        var eventCode = await _db.Events
            .Where(e => e.Id == me.EventId)
            .Select(e => e.Code)
            .FirstOrDefaultAsync(ct) ?? "Event Hub";

        int sent = 0, failed = 0;
        foreach (var p in pending)
        {
            try
            {
                var token = _magic.CreateToken(p.Id);
                var link = $"{origin}/Login/Magic?token={Uri.EscapeDataString(token)}";
                var firstName = string.IsNullOrWhiteSpace(p.FullName) ? "there" : p.FullName.Split(' ')[0];

                var tokens = _templates.NewTokenSet();
                tokens["firstName"] = firstName;
                tokens["roleName"] = p.Role.ToString();
                tokens["communityName"] = communityName;
                tokens["eventCode"] = eventCode;
                tokens["magicLink"] = link;

                var rendered = _templates.Render("invitation", tokens);
                await _emailSender.SendAsync(p.Email, rendered.Subject, rendered.HtmlBody, ct);

                _db.SentReminders.Add(new SentReminder
                {
                    EventId = me.EventId,
                    RecipientEmail = p.Email,
                    ReminderType = ReminderType,
                    OccasionKey = $"invitation:{p.Id}",
                    SentAt = _clock.GetUtcNow(),
                });
                sent++;
            }
            catch
            {
                failed++;
            }
        }
        await _db.SaveChangesAsync(ct);

        await LoadCountsAsync(me.EventId, ct);
        Message = $"Sent {sent} invitation(s)."
                  + (failed > 0 ? $" {failed} failed (will retry next click)." : "");
        return Page();
    }

    private async Task LoadCountsAsync(int eventId, CancellationToken ct)
    {
        var active = await _db.Participants
            .Where(p => p.EventId == eventId && p.IsActive)
            .Select(p => new { p.Email, p.Role })
            .ToListAsync(ct);

        var alreadySent = (await _db.SentReminders
            .Where(s => s.EventId == eventId && s.ReminderType == ReminderType)
            .Select(s => s.RecipientEmail)
            .ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        ActiveCount = active.Count;
        AlreadySentCount = active.Count(p => alreadySent.Contains(p.Email));

        ByRole = active
            .GroupBy(p => p.Role.ToString())
            .Select(g => new RoleCount(
                g.Key,
                g.Count(),
                g.Count(p => alreadySent.Contains(p.Email)),
                g.Count(p => !alreadySent.Contains(p.Email))))
            .OrderBy(r => r.Role)
            .ToList();
    }

}
