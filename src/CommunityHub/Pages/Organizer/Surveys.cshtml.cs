using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Surveys;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// ORGANIZER survey management surface (REQUIREMENTS §24). Lists every survey
/// defined in JSON (App_Data/Surveys/), with its response count + open/closed
/// status, and offers per-survey actions:
///   • shareable links (submit + results URLs, copy-to-clipboard, JS-off visible);
///   • Activate / Close (gates whether the PUBLIC page accepts submissions);
///   • Reset responses (type-to-confirm, organizer-only, audited);
///   • inline results (the SAME aggregation the public dashboard shows, via the
///     shared <see cref="SurveySummaryService"/> — no logic duplicated) plus a
///     clear link out to the public results.
///
/// Organizer-gated server-side (cf. the other Organizer pages). State-changing
/// actions are POST + anti-forgery (Razor Pages emits the token on every form).
/// </summary>
[Authorize]
public class SurveysModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SurveyDefinitionProvider _definitions;
    private readonly SurveySummaryService _summary;

    public SurveysModel(
        ICurrentParticipantAccessor participant,
        SurveyDefinitionProvider definitions,
        SurveySummaryService summary)
    {
        _participant = participant;
        _definitions = definitions;
        _summary = summary;
    }

    public bool AccessDenied { get; private set; }
    public string? FlashMessage { get; private set; }
    public bool FlashIsError { get; private set; }

    /// <summary>One row per known survey for the list.</summary>
    public record SurveyRow(string Slug, string Title, int ResponseCount, bool IsOpen);

    public List<SurveyRow> Surveys { get; private set; } = new();

    /// <summary>The slug whose inline results are expanded (querystring), or null.</summary>
    [BindProperty(SupportsGet = true)] public string? View { get; set; }

    public CommunityHub.Core.Surveys.SurveyDefinition? ViewSurvey { get; private set; }
    public SurveySummaryService.SurveySummary? ViewSummary { get; private set; }

    [BindProperty(SupportsGet = true)] public string? Msg { get; set; }
    [BindProperty(SupportsGet = true)] public bool MsgErr { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        await LoadAsync(ct);

        if (!string.IsNullOrEmpty(Msg))
        {
            FlashMessage = Msg;
            FlashIsError = MsgErr;
        }
        return Page();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var slugs = _definitions.ListSlugs();
        var openStates = await _summary.GetOpenStatesAsync(slugs, ct);

        var rows = new List<SurveyRow>();
        foreach (var slug in slugs)
        {
            var def = _definitions.TryGet(slug);
            var title = string.IsNullOrWhiteSpace(def?.Title) ? slug : def!.Title;
            var count = await _summary.CountResponsesAsync(slug, ct);
            openStates.TryGetValue(slug, out var isOpen);
            rows.Add(new SurveyRow(slug, title, count, isOpen));
        }
        Surveys = rows;

        // Inline results for the expanded survey (reuses the shared aggregation).
        if (!string.IsNullOrWhiteSpace(View))
        {
            ViewSurvey = _definitions.TryGet(View);
            if (ViewSurvey is not null)
            {
                ViewSummary = await _summary.BuildSummaryAsync(
                    View, SurveyCatalog.From(ViewSurvey), ct);
            }
        }
    }

    /// <summary>
    /// Build the public submit URL for a slug (shareable link). Prefers the
    /// absolute URL from the URL helper; falls back to the well-known relative
    /// path (also used in unit tests where no URL helper is wired).
    /// </summary>
    public string SubmitUrl(string slug) =>
        Url?.PageLink(pageName: "/Survey/Index", values: new { slug }) ?? $"/survey/{slug}";

    /// <summary>Build the public results URL for a slug (shareable link).</summary>
    public string ResultsUrl(string slug) =>
        Url?.PageLink(pageName: "/Survey/Results", values: new { slug }) ?? $"/survey/{slug}/results";

    /// <summary>
    /// Activate or close a survey. POST + anti-forgery + organizer-gated. Toggles
    /// whether the PUBLIC page accepts submissions; results stay viewable either way.
    /// </summary>
    public async Task<IActionResult> OnPostToggleOpenAsync(string slug, bool open, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        if (string.IsNullOrWhiteSpace(slug) || _definitions.TryGet(slug) is null)
        {
            return RedirectToPage(new { msg = "That survey was not found.", msgErr = true });
        }

        await _summary.SetOpenAsync(slug, open, me.Email, ct);
        var msg = open
            ? $"Survey “{slug}” is now OPEN and accepting submissions."
            : $"Survey “{slug}” is now CLOSED. Results are still viewable.";
        return RedirectToPage(new { msg });
    }

    /// <summary>
    /// Reset (delete ALL responses + picks) for a slug. Requires the organizer to
    /// type the exact slug into <paramref name="confirmSlug"/> as a safety gate.
    /// POST + anti-forgery + organizer-gated; the deletion + count are audited in
    /// <see cref="SurveySummaryService.ResetResponsesAsync"/>.
    /// </summary>
    public async Task<IActionResult> OnPostResetAsync(string slug, string? confirmSlug, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        if (string.IsNullOrWhiteSpace(slug) || _definitions.TryGet(slug) is null)
        {
            return RedirectToPage(new { msg = "That survey was not found.", msgErr = true });
        }

        // Type-to-confirm gate: the typed value must match the slug exactly.
        if (!string.Equals(confirmSlug?.Trim(), slug, StringComparison.Ordinal))
        {
            return RedirectToPage(new
            {
                msg = $"Reset cancelled — the confirmation text did not match “{slug}”. Nothing was deleted.",
                msgErr = true,
            });
        }

        var deleted = await _summary.ResetResponsesAsync(slug, me.Email, ct);
        return RedirectToPage(new
        {
            msg = $"Reset complete — deleted {deleted} response(s) for “{slug}”. The survey is now empty.",
        });
    }
}
