using System.Security.Cryptography;
using System.Text;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Sessions;

/// <summary>
/// PUBLIC, no-login page where an attendee EVALUATES a session (HappyOrNot-style 1–5
/// smiley rating + optional comment), typically reached by scanning the ROOM QR.
/// Addressed by the session's unguessable <see cref="Session.PublicToken"/>
/// (<c>/sessions/{token}/evaluate</c>, the SAME token as the ask page) so the URL
/// cannot be enumerated.
///
/// The submitted rating lands in the Event Hub ONLY (a <see cref="SessionEvaluation"/>
/// linked to the session) and is NEVER shown back publicly; it feeds the organizer
/// results dashboard. Anti-abuse is lightweight (not a login): a per-session cookie
/// de-dups one rating per attendee/session (a re-rate updates in place), plus a honeypot
/// (any non-empty value → silent 200, no write) and a soft IP-hash rate-limit — mirroring
/// the survey / session-question form. Mobile-first (~360px) + a11y.
/// </summary>
[AllowAnonymous]
public class EvaluateModel : PageModel
{
    /// <summary>How many evaluations one IP hash may submit per edition within the window.</summary>
    private const int RateLimitMax = 30;
    private static readonly TimeSpan RateLimitWindow = TimeSpan.FromHours(1);

    /// <summary>Per-session cookie name prefix carrying the one-per-attendee voter token.</summary>
    private const string VoterCookiePrefix = "ceh_eval_";

    private readonly SessionEvaluationService _svc;
    private readonly TimeProvider _clock;
    private readonly ILogger<EvaluateModel> _log;

    public EvaluateModel(SessionEvaluationService svc, TimeProvider clock, ILogger<EvaluateModel> log)
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
    public int? SubmittedRating { get; private set; }
    public string? ErrorMessage { get; private set; }

    // --- Form binding -----------------------------------------------------
    [BindProperty] public int Rating { get; set; }
    [BindProperty] public string? Comment { get; set; }

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
                "Session-evaluation honeypot tripped for token from {Ip}",
                HttpContext.Connection.RemoteIpAddress);
            SubmittedOk = true;
            return Page();
        }

        if (Rating < SessionEvaluationService.MinRating || Rating > SessionEvaluationService.MaxRating)
        {
            ErrorMessage = "Please tap a rating before sending.";
            return Page();
        }

        var ipHash = HashIp(HttpContext.Connection.RemoteIpAddress?.ToString());

        // Soft rate-limit (never PII'd back to the IP). Pretend success so a flooder
        // gets no signal, but write nothing.
        var recent = await _svc.CountRecentByIpHashAsync(
            Session.EventId, ipHash, _clock.GetUtcNow() - RateLimitWindow, ct);
        if (recent >= RateLimitMax)
        {
            _log.LogInformation(
                "Session-evaluation soft rate-limit hit ({Count}) for event {EventId}",
                recent, Session.EventId);
            SubmittedOk = true;
            return Page();
        }

        // One-per-attendee/session: reuse the per-session voter cookie if present,
        // otherwise mint one and set it so a re-rate updates the same row.
        var voterKey = GetOrSetVoterCookie(Session.Id);

        try
        {
            var saved = await _svc.SubmitPublicEvaluationAsync(
                Token, Rating, Comment, voterKey, ipHash, ct);
            if (saved is null) return NotFound();

            _log.LogInformation(
                "Session evaluation saved: id={Id} session={SessionId} rating={Rating} event={EventId}",
                saved.Id, saved.SessionId, saved.Rating, saved.EventId);
        }
        catch (SessionEvaluationValidationException ex)
        {
            ErrorMessage = ex.Message;
            return Page();
        }

        SubmittedOk = true;
        SubmittedRating = Rating;
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

    /// <summary>
    /// Return the per-session voter token from the cookie, minting + setting one when
    /// absent. Lightweight de-dup only (not identity): a same-device re-rate finds the
    /// cookie and the service updates the existing row instead of stacking duplicates.
    /// </summary>
    private string GetOrSetVoterCookie(int sessionId)
    {
        var name = VoterCookiePrefix + sessionId;
        if (Request.Cookies.TryGetValue(name, out var existing) && !string.IsNullOrWhiteSpace(existing))
            return existing;

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        Response.Cookies.Append(name, token, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,                 // anti-abuse, not tracking
            SameSite = SameSiteMode.Lax,
            Secure = Request.IsHttps,
            Expires = _clock.GetUtcNow().AddDays(180),
        });
        return token;
    }

    private static string? HashIp(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return null;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(ip));
        return Convert.ToHexString(bytes)[..32];
    }
}
