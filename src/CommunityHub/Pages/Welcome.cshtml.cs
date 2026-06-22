using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Email;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages;

/// <summary>
/// The first-login PORTAL welcome (shown once, gated by
/// <see cref="CommunityHub.Core.Domain.Participant.WelcomeShownAt"/>). Renders the
/// per-role welcome CONTENT (the same variant the welcome email uses, via
/// <see cref="WelcomeVariants"/>) as a body-only fragment — the email
/// <c>_layout.html</c> shell and the <c>Subject:</c> line are stripped, so the
/// portal shows just the content card, not the email chrome. "Continue" stamps
/// <see cref="CommunityHub.Core.Domain.Participant.WelcomeShownAt"/>.
/// </summary>
[Authorize]
public class WelcomeModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly TimeProvider _clock;
    private readonly EmailTemplateProvider _templates;
    private readonly IEmailContextAccessor? _emailContext;

    public WelcomeModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        TimeProvider clock,
        EmailTemplateProvider templates,
        IEmailContextAccessor? emailContext = null)
    {
        _db = db;
        _participant = participant;
        _clock = clock;
        _templates = templates;
        _emailContext = emailContext;
    }

    public string FirstName { get; private set; } = string.Empty;
    public string RoleName { get; private set; } = string.Empty;
    public string EventDisplayName { get; private set; } = string.Empty;
    public string EventCode { get; private set; } = string.Empty;

    /// <summary>
    /// The per-role welcome content, rendered body-only (no email layout, no
    /// Subject line) — already HTML, safe to emit verbatim into the page card.
    /// </summary>
    public string WelcomeBodyHtml { get; private set; } = string.Empty;

    public IActionResult OnGet()
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        FirstName = string.IsNullOrWhiteSpace(me.FullName) ? "there" : me.FullName.Split(' ')[0];
        RoleName = me.Role.ToString();

        var ev = _db.Events
            .Where(e => e.Id == me.EventId)
            .Select(e => new { e.DisplayName, e.Code, e.CommunityName })
            .FirstOrDefault();
        var communityName = ev?.CommunityName ?? string.Empty;
        if (ev is not null)
        {
            EventDisplayName = ev.DisplayName;
            EventCode = ev.Code;
        }

        // Render the per-role welcome variant as a body-only fragment (no email
        // shell). Setting the EmailContext lets the per-edition DB override resolve
        // for this event too; the private config file + shipped default are honoured
        // regardless. Best-effort: a misconfigured template never blocks the portal.
        var templateKey = WelcomeVariants.TemplateKeyFor(me.Role);

        // Roles with NO welcome (Organizer, Attendee — operator 2026-06-22) skip the
        // first-login welcome entirely: stamp it shown so Index stops redirecting
        // here, then go straight to the hub.
        if (string.IsNullOrEmpty(templateKey))
        {
            var skip = _db.Participants.FirstOrDefault(x => x.Id == me.ParticipantId);
            if (skip is not null && skip.WelcomeShownAt is null)
            {
                skip.WelcomeShownAt = _clock.GetUtcNow();
                _db.SaveChanges();
            }
            return RedirectToPage("/Index");
        }

        var tokens = _templates.NewTokenSet();
        tokens["firstName"] = FirstName;
        tokens["roleName"] = me.Role.ToString();
        tokens["communityName"] = communityName;
        tokens["eventDisplayName"] = EventDisplayName;
        tokens["eventCode"] = EventCode;
        // hubUrl/supportEmail/brandColor/logoUrl are seeded by NewTokenSet().
        try
        {
            if (_emailContext is not null && me.EventId > 0)
            {
                using (_emailContext.Set(new EmailContext(
                    templateKey, me.EventId, me.ParticipantId, me.FullName,
                    FeatureKey: "welcome-email")))
                {
                    WelcomeBodyHtml = _templates.RenderBodyFragment(templateKey, tokens);
                }
            }
            else
            {
                WelcomeBodyHtml = _templates.RenderBodyFragment(templateKey, tokens);
            }
        }
        catch
        {
            // No welcome template available ⇒ render the page with an empty body card
            // rather than 500. The Continue button still works.
            WelcomeBodyHtml = string.Empty;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostContinueAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        var p = await _db.Participants.FirstOrDefaultAsync(x => x.Id == me.ParticipantId, ct);
        if (p is not null && p.WelcomeShownAt is null)
        {
            p.WelcomeShownAt = _clock.GetUtcNow();
            await _db.SaveChangesAsync(ct);
        }
        return RedirectToPage("/Index");
    }
}
