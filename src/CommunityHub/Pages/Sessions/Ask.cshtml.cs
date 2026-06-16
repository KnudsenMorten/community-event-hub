using System.Security.Cryptography;
using System.Text;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Sessions;

/// <summary>
/// PUBLIC, no-login landing page where an attendee asks a question for a session
/// BEFORE the event. Addressed by the session's unguessable
/// <see cref="Session.PublicToken"/> (<c>/sessions/{token}/ask</c>) so the URL
/// cannot be enumerated. The page shows the session title/abstract + its
/// speaker(s) and a single question form.
///
/// The submitted question lands in the Event Hub ONLY (a
/// <see cref="SessionQuestion"/> linked to the session) and is NEVER shown back
/// publicly. Spam handling mirrors the survey form: a honeypot (any non-empty
/// value → silent 200, no write) plus a soft IP-hash rate-limit. Mobile-first
/// (~360px) + a11y.
/// </summary>
[AllowAnonymous]
public class AskModel : PageModel
{
    /// <summary>How many questions one IP hash may submit per edition within the window.</summary>
    private const int RateLimitMax = 8;
    private static readonly TimeSpan RateLimitWindow = TimeSpan.FromHours(1);

    private readonly SessionQuestionService _svc;
    private readonly TimeProvider _clock;
    private readonly ILogger<AskModel> _log;

    public AskModel(SessionQuestionService svc, TimeProvider clock, ILogger<AskModel> log)
    {
        _svc = svc;
        _clock = clock;
        _log = log;
    }

    // --- View state -------------------------------------------------------
    public Session? Session { get; private set; }
    public string Token { get; private set; } = string.Empty;
    public IReadOnlyList<string> SpeakerNames { get; private set; } = Array.Empty<string>();
    public bool SubmittedOk { get; private set; }
    public string? ErrorMessage { get; private set; }

    // --- Form binding -----------------------------------------------------
    [BindProperty] public string? AskerName { get; set; }
    [BindProperty] public string? AskerEmail { get; set; }
    [BindProperty] public string? QuestionText { get; set; }

    /// <summary>
    /// Honeypot. Hidden off-screen; humans never see it, bots fill every input.
    /// Any non-empty value → silent 200 OK (no DB write). Mirrors the survey form.
    /// </summary>
    [BindProperty] public string? Website { get; set; }

    public async Task<IActionResult> OnGetAsync(string token, CancellationToken ct)
    {
        Token = token ?? string.Empty;
        Session = await _svc.ResolveByPublicTokenAsync(Token, ct);
        if (Session is null) return NotFound();
        LoadSpeakers();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string token, CancellationToken ct)
    {
        Token = token ?? string.Empty;
        Session = await _svc.ResolveByPublicTokenAsync(Token, ct);
        if (Session is null) return NotFound();
        LoadSpeakers();

        // Honeypot — pretend success without writing anything.
        if (!string.IsNullOrWhiteSpace(Website))
        {
            _log.LogInformation(
                "Session-question honeypot tripped for token from {Ip}",
                HttpContext.Connection.RemoteIpAddress);
            SubmittedOk = true;
            return Page();
        }

        if (string.IsNullOrWhiteSpace(QuestionText) || QuestionText.Trim().Length < 2)
        {
            ErrorMessage = "Please type your question before sending.";
            return Page();
        }

        var ipHash = HashIp(HttpContext.Connection.RemoteIpAddress?.ToString());

        // Soft rate-limit (never PII'd back to the IP). Pretend success so a
        // flooder gets no signal, but write nothing.
        var recent = await _svc.CountRecentByIpHashAsync(
            Session.EventId, ipHash, _clock.GetUtcNow() - RateLimitWindow, ct);
        if (recent >= RateLimitMax)
        {
            _log.LogInformation(
                "Session-question soft rate-limit hit ({Count}) for event {EventId}",
                recent, Session.EventId);
            SubmittedOk = true;
            return Page();
        }

        try
        {
            var saved = await _svc.SubmitPublicQuestionAsync(
                Token, AskerName, AskerEmail, QuestionText!, ipHash, ct);
            if (saved is null) return NotFound();

            _log.LogInformation(
                "Session question saved: id={Id} session={SessionId} event={EventId}",
                saved.Id, saved.SessionId, saved.EventId);
        }
        catch (SessionQuestionValidationException ex)
        {
            ErrorMessage = ex.Message;
            return Page();
        }

        SubmittedOk = true;
        return Page();
    }

    private void LoadSpeakers()
    {
        SpeakerNames = Session!.SessionSpeakers
            .Select(ss => ss.Participant?.FullName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .OrderBy(n => n)
            .ToList();
    }

    private static string? HashIp(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return null;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(ip));
        return Convert.ToHexString(bytes)[..32];
    }
}
