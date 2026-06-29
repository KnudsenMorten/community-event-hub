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
/// App game sponsor participation (README: Organizers Hub). Register which
/// sponsors take part (and the gift they committed), track confirmation,
/// and send the "remember to bring the gift" reminder to the sponsor's
/// contacts through the branded template system.
/// </summary>
[Authorize]
public class AppGameModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly EmailTemplateProvider _templates;
    private readonly IEmailSender _emailSender;
    private readonly TimeProvider _clock;
    private readonly IEmailContextAccessor? _context;

    public AppGameModel(
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
    public List<AppGameParticipation> Participations { get; private set; } = new();
    public List<(string Id, string Name)> SponsorCompanies { get; private set; } = new();
    [BindProperty(SupportsGet = true)] public string? Msg { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        Notice = Msg;
        Participations = await _db.AppGameParticipations
            .Where(p => p.EventId == me.EventId)
            .OrderBy(p => p.GiftConfirmed)
            .ThenBy(p => p.CompanyName)
            .ToListAsync(ct);

        // Sponsor companies known to this edition (contacts carry the id;
        // the display name falls back to the id when no nicer name exists).
        SponsorCompanies = (await _db.Participants
            .Where(p => p.EventId == me.EventId
                        && p.Role == ParticipantRole.Sponsor
                        && p.SponsorCompanyId != null)
            .Select(p => p.SponsorCompanyId!)
            .Distinct()
            .OrderBy(cid => cid)
            .ToListAsync(ct))
            .Select(cid => (cid, cid))
            .ToList();
        return Page();
    }

    public async Task<IActionResult> OnPostCreateAsync(
        string sponsorCompanyId, string companyName, string giftDescription,
        string? notes, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        if (string.IsNullOrWhiteSpace(sponsorCompanyId) || string.IsNullOrWhiteSpace(companyName))
        {
            return RedirectToPage(new { Msg = "Sponsor company and display name are required." });
        }

        var exists = await _db.AppGameParticipations.AnyAsync(
            p => p.EventId == me.EventId && p.SponsorCompanyId == sponsorCompanyId, ct);
        if (exists)
        {
            return RedirectToPage(new { Msg = $"'{companyName}' is already registered." });
        }

        _db.AppGameParticipations.Add(new AppGameParticipation
        {
            EventId = me.EventId,
            SponsorCompanyId = sponsorCompanyId.Trim(),
            CompanyName = companyName.Trim(),
            GiftDescription = (giftDescription ?? string.Empty).Trim(),
            Notes = notes?.Trim(),
            CreatedAt = _clock.GetUtcNow(),
        });
        await _db.SaveChangesAsync(ct);
        return RedirectToPage(new { Msg = $"Registered '{companyName}' for the app game." });
    }

    public async Task<IActionResult> OnPostUpdateAsync(
        int id, string giftDescription, bool giftConfirmed, string? notes, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var row = await _db.AppGameParticipations.FirstOrDefaultAsync(
            p => p.Id == id && p.EventId == me.EventId, ct);
        if (row is null) return RedirectToPage(new { Msg = "Registration not found." });

        row.GiftDescription = (giftDescription ?? string.Empty).Trim();
        row.GiftConfirmed = giftConfirmed;
        row.Notes = notes?.Trim();
        await _db.SaveChangesAsync(ct);
        return RedirectToPage(new { Msg = $"Updated '{row.CompanyName}'." });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var row = await _db.AppGameParticipations.FirstOrDefaultAsync(
            p => p.Id == id && p.EventId == me.EventId, ct);
        if (row is not null)
        {
            _db.AppGameParticipations.Remove(row);
            await _db.SaveChangesAsync(ct);
        }
        return RedirectToPage(new { Msg = "Registration removed." });
    }

    /// <summary>Send the bring-the-gift reminder to all the sponsor's contacts.</summary>
    public async Task<IActionResult> OnPostSendReminderAsync(int id, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var row = await _db.AppGameParticipations
            .Include(p => p.Event)
            .FirstOrDefaultAsync(p => p.Id == id && p.EventId == me.EventId, ct);
        if (row is null) return RedirectToPage(new { Msg = "Registration not found." });

        var contacts = await _db.Participants
            .Where(p => p.EventId == me.EventId
                        && p.IsActive
                        && p.Role == ParticipantRole.Sponsor
                        && p.SponsorCompanyId == row.SponsorCompanyId)
            .Select(p => new { p.Id, p.Email, p.FullName })
            .ToListAsync(ct);
        if (contacts.Count == 0)
        {
            return RedirectToPage(new { Msg = $"No active sponsor contacts found for '{row.CompanyName}'." });
        }

        int sent = 0, failed = 0;
        foreach (var c in contacts)
        {
            try
            {
                // Token values are HTML-encoded by the renderer at the seam
                // (EmailTemplateRenderer, REQUIREMENTS §10c-4) — pass raw text.
                // §169: this fan-out sends ONE body PER sponsor contact (each is a
                // Participant) — pass their id so every recipient's {{hubUrl}} CTA is
                // their OWN /go/{token} magic-link (fail-safe: none ⇒ plain hub URL).
                var tokens = _templates.NewTokenSet(c.Id);
                tokens["firstName"] = string.IsNullOrWhiteSpace(c.FullName) ? "there" : c.FullName.Split(' ')[0];
                tokens["companyName"] = row.CompanyName;
                tokens["eventDisplayName"] = row.Event.DisplayName;
                tokens["giftDescription"] = string.IsNullOrWhiteSpace(row.GiftDescription)
                    ? "the gift your team committed" : row.GiftDescription;
                var rendered = _templates.Render("app-game-gift-reminder", tokens);
                // Ring-governed by the sponsor-reminders feature (operator 2026-06-22).
                using (_context?.Set(new EmailContext(
                    "app-game-gift-reminder", row.EventId, null, c.FullName,
                    FeatureKey: "sponsor-reminders")))
                {
                    await _emailSender.SendAsync(c.Email, rendered.Subject, rendered.HtmlBody, ct);
                }
                sent++;
            }
            catch { failed++; }
        }

        row.ReminderLastSentAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return RedirectToPage(new { Msg = $"Gift reminder for '{row.CompanyName}': {sent} sent{(failed > 0 ? $", {failed} failed" : "")}." });
    }
}
