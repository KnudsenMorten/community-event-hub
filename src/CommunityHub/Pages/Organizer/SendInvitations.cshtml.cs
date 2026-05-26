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
    private readonly CommunityHub.Branding.ActiveEventNameProvider _activeEvent;
    private readonly TimeProvider _clock;

    public SendInvitationsModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        MagicLinkService magic,
        IEmailSender emailSender,
        CommunityHub.Branding.ActiveEventNameProvider activeEvent,
        TimeProvider clock)
    {
        _db = db;
        _participant = participant;
        _magic = magic;
        _emailSender = emailSender;
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
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

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
                var html = BuildInvitationHtml(p.FullName, p.Role, link, eventCode, communityName);
                var subject = $"{eventCode} Event Hub - your one-tap sign-in link";
                await _emailSender.SendAsync(p.Email, subject, html, ct);

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

    private static string BuildInvitationHtml(
        string fullName, ParticipantRole role, string link, string eventCode, string communityName)
    {
        var firstName = string.IsNullOrWhiteSpace(fullName) ? "there" : fullName.Split(' ')[0];
        var encName   = System.Net.WebUtility.HtmlEncode(firstName);
        var encComm   = System.Net.WebUtility.HtmlEncode(communityName);
        var encCode   = System.Net.WebUtility.HtmlEncode(eventCode);
        var encRole   = System.Net.WebUtility.HtmlEncode(role.ToString());

        return $@"<p>Hi {encName},</p>
<p>You have been added as a <strong>{encRole}</strong> for <strong>{encComm}</strong> ({encCode}).</p>
<p>Tap the button below to sign in to your Event Hub — no PIN, no password.</p>
<p>
  <a href=""{link}""
     style=""display:inline-block;background:#008BD2;color:#fff;
            padding:11px 20px;border-radius:7px;text-decoration:none;
            font-weight:bold;font-size:15px;"">
    Open my Event Hub
  </a>
</p>
<p style=""color:#6b7280;font-size:13px;"">
  The link is valid for 14 days. If it expires you can always sign in with your email + a one-time PIN.
</p>
<p>Cheers,<br/>ELDK-team</p>";
    }
}
