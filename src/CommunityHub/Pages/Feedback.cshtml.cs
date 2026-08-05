using CommunityHub.Core.Evaluation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages;

/// <summary>
/// §748 C6 — the attendee feedback page a session's QR code opens. PUBLIC, no login.
/// </summary>
/// <remarks>
/// <para>🔒 <b>The route is <c>/f/{token}</c> and it is deliberately short.</b> Every character is
/// encoded into a printed symbol that has to scan from a seat at the back of a room, and more
/// characters mean denser modules. The brief exempts this ONE route from the <c>/evaluation/v1</c>
/// convention for exactly that reason — do not "tidy" it into the namespace.</para>
///
/// <para>🔒 <b>Anonymous means anonymous.</b> The page must not require, and does not collect,
/// identity. The token authorises submission to its own session and nothing else. There is no voter
/// cookie: the old 1–5 page used one to update a rating in place, which the append-only response
/// model forbids — and a device lets the same person press twice too, so a cookie here would only
/// make the two channels behave differently.</para>
///
/// <para>🔒 <b>Not rate-limited, by decision</b> (brief; and §406 — the operator removed the old
/// per-IP limit: *"we can have 10000 pr hour easily"*). It was wrong for a venue, not merely tight:
/// everyone in the hall shares one wifi and hashes to ONE address, so a limit silently discarded
/// real ratings from the back of the queue while telling those people "thanks". The honeypot below
/// stays, because it costs nothing and catches the actual bots.</para>
/// </remarks>
[AllowAnonymous]
public class FeedbackModel : PageModel
{
    private readonly EvaluationQrService _qr;
    private readonly ILogger<FeedbackModel> _log;

    public FeedbackModel(EvaluationQrService qr, ILogger<FeedbackModel> log)
    {
        _qr = qr;
        _log = log;
    }

    public EvaluationQrService.Resolution? Resolution { get; private set; }
    public string Token { get; private set; } = string.Empty;
    public bool SubmittedOk { get; private set; }
    public int? SubmittedRating { get; private set; }
    public string? ErrorMessage { get; private set; }

    [BindProperty] public int Rating { get; set; }
    [BindProperty] public string? Comment { get; set; }

    /// <summary>
    /// Honeypot. Hidden off-screen; a human never sees it, a bot fills every input. Any value ⇒
    /// silent success with no write, so a scripted submitter learns nothing from the response.
    /// </summary>
    [BindProperty] public string? Website { get; set; }

    public int[] RatingOrder => EvaluationRatingLabels.Order;
    public static string LabelFor(int rating) => EvaluationRatingLabels.Label(rating);
    public static string ColourFor(int rating) => EvaluationRatingLabels.Colour(rating);

    public async Task<IActionResult> OnGetAsync(string token, CancellationToken ct)
    {
        Token = token ?? string.Empty;
        Resolution = await _qr.ResolveAsync(Token, ct);

        // 🔒 A deleted session's printed code must resolve to an honest "not found", never to a form
        // that quietly accepts orphan feedback (brief, C5).
        if (Resolution.State == EvaluationQrService.FeedbackState.NotFound) return NotFoundPage();

        return Page();
    }

    /// <summary>
    /// §748.4 — the honest "not found" the brief asks for: a rendered PAGE that still carries a real
    /// <c>404</c>.
    /// </summary>
    /// <remarks>
    /// <para>This used to be a bare <c>NotFound()</c>, which sends an EMPTY body. The person holding
    /// the phone is standing in a room having just scanned a sign, and they got a browser error page
    /// — no branding, no wording, no way to tell whether the fault is theirs, the code's, or the
    /// event's. Every other refusal state on this page renders properly; only this one did not.</para>
    ///
    /// <para>🔑 <b>The status code stays 404.</b> The page is for the human; the status is for
    /// everything else — crawlers, uptime probes, and the link checkers that would otherwise record a
    /// revoked code as a healthy URL. Returning 200 with an apology is the common mistake here.</para>
    /// </remarks>
    private IActionResult NotFoundPage()
    {
        Response.StatusCode = StatusCodes.Status404NotFound;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string token, CancellationToken ct)
    {
        Token = token ?? string.Empty;
        Resolution = await _qr.ResolveAsync(Token, ct);
        if (Resolution.State == EvaluationQrService.FeedbackState.NotFound) return NotFoundPage();

        if (!string.IsNullOrWhiteSpace(Website))
        {
            _log.LogInformation("QR feedback honeypot tripped from {Ip}.",
                HttpContext.Connection.RemoteIpAddress);
            SubmittedOk = true;
            return Page();
        }

        if (Rating < EvaluationQrService.MinRating || Rating > EvaluationQrService.MaxRating)
        {
            ErrorMessage = "Please choose one of the four options before sending.";
            return Page();
        }

        var result = await _qr.SubmitAsync(Token, Rating, Comment, ct);

        // 🔑 The window can close between rendering the form and posting it. The service re-checks,
        // and when it refuses the page must say so plainly rather than thank the attendee for
        // feedback it discarded.
        if (!result.Saved)
        {
            Resolution = Resolution with { State = result.State };
            return Page();
        }

        _log.LogInformation(
            "QR feedback saved: session={SessionId} rating={Rating} hasComment={HasComment}",
            Resolution.EvaluationSessionId, Rating, !string.IsNullOrWhiteSpace(Comment));

        SubmittedOk = true;
        SubmittedRating = Rating;
        return Page();
    }
}
