using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Sessions;

/// <summary>
/// PUBLIC, no-login landing page for the (now retired) per-session 1:1 question flow,
/// addressed by the session's unguessable <see cref="Session.PublicToken"/>
/// (<c>/sessions/{token}/ask</c>).
///
/// <b>§136 (operator 2026-06-27): DISABLED.</b> Attendee 1:1 questions are no longer
/// accepted. The route is KEPT so existing links don't 404 — it now shows only a short
/// note pointing attendees to the per-Master-Class Group Q&amp;A or to the organizers.
/// A POST is INERT: it NEVER writes a <see cref="SessionQuestion"/> (no honeypot /
/// rate-limit path remains because nothing is ever stored). Existing question history
/// and the organizer/speaker views over it are untouched.
/// </summary>
[AllowAnonymous]
public class AskModel : PageModel
{
    private readonly SessionQuestionService _svc;

    public AskModel(SessionQuestionService svc)
    {
        _svc = svc;
    }

    // --- View state -------------------------------------------------------
    public Session? Session { get; private set; }
    public string Token { get; private set; } = string.Empty;
    public IReadOnlyList<string> SpeakerNames { get; private set; } = Array.Empty<string>();

    public async Task<IActionResult> OnGetAsync(string token, CancellationToken ct)
    {
        Token = token ?? string.Empty;
        // Resolve only to show a friendly session title; a bad/old token still
        // renders the generic "no longer available" note rather than a 404.
        Session = await _svc.ResolveByPublicTokenAsync(Token, ct);
        LoadSpeakers();
        return Page();
    }

    /// <summary>
    /// INERT (§136): the 1:1 form is gone, but guard the POST so any stray/replayed
    /// submission can NEVER create a question. Nothing is written; the disabled note
    /// is shown.
    /// </summary>
    public async Task<IActionResult> OnPostAsync(string token, CancellationToken ct)
    {
        Token = token ?? string.Empty;
        Session = await _svc.ResolveByPublicTokenAsync(Token, ct);
        LoadSpeakers();
        return Page();
    }

    private void LoadSpeakers()
    {
        if (Session is null) { SpeakerNames = Array.Empty<string>(); return; }
        SpeakerNames = Session.SessionSpeakers
            .Select(ss => ss.Participant?.FullName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .OrderBy(n => n)
            .ToList();
    }
}
