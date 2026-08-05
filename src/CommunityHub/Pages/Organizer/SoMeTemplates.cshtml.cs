using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// §824.2C — the SoMe post-template editor: the five post types, edited per edition.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-04: <i>"SoMe post editor - where i can edit a post … must support
/// variables … must support emogees … must support SoMe post templates, where we have 5 templates
/// (type 1-5)"</i>.</para>
///
/// <para>🔑 <b>What this page is FOR: making the wording his, without a deploy.</b> Until now every
/// syllable of a post lived in code. The templates shipped in <c>SoMeTemplateCatalog</c> are a
/// starting point modelled on his own ELDK26 posts, not a house style he has to accept.</para>
///
/// <para>🔒 Organizer-only, and the writes require a REAL organizer (not an acting-as session) — the
/// same rule <c>/Organizer/LinkedInConnect</c> applies, for the same reason: this text goes out under
/// the community's name on a public company page.</para>
/// </remarks>
[Authorize]
public class SoMeTemplatesModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SoMeTemplateService _templates;

    public SoMeTemplatesModel(ICurrentParticipantAccessor participant, SoMeTemplateService templates)
    {
        _participant = participant;
        _templates = templates;
    }

    public bool AccessDenied { get; private set; }
    public IReadOnlyList<SoMeTemplateView> Templates { get; private set; } = Array.Empty<SoMeTemplateView>();

    /// <summary>The variables the editor lists, so he never has to remember one.</summary>
    public IReadOnlyList<string> KnownVariables => SoMeTemplateCatalog.KnownVariables;

    [BindProperty(SupportsGet = true)] public string? Msg { get; set; }
    public bool MsgIsWarning { get; private set; }

    /// <summary>Which template's preview is expanded, so a save returns to what he was editing.</summary>
    [BindProperty(SupportsGet = true)] public int? Focus { get; set; }

    public string Preview(string body) => SoMeTemplateService.PreviewWithSamples(body);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        Templates = await _templates.ListAsync(me.EventId, ct);
        // A warning survives the redirect: the save SUCCEEDED, and the wording is what needs saying.
        MsgIsWarning = Msg?.Contains('{') == true;
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(int kind, string body, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        if (!Enum.IsDefined(typeof(SoMeTemplateKind), kind))
        {
            return RedirectToPage(new { Msg = "That is not one of the five post types." });
        }

        var k = (SoMeTemplateKind)kind;
        var unknown = await _templates.SaveAsync(me.EventId, k, body, me.Email, ct);

        // ⚠️ Saved either way — he may be mid-edit, and a page that refuses to keep his work because
        // a token is half-typed loses the work. The warning names the tokens so the typo is
        // findable; the same check runs again before a post is queued, where refusing is cheap.
        var msg = unknown.Count == 0
            ? $"Saved “{SoMeTemplateCatalog.Title(k)}”."
            : $"Saved “{SoMeTemplateCatalog.Title(k)}” — but {string.Join(", ", unknown)} "
              + "matches no variable, so it will print exactly like that in the post.";

        return RedirectToPage(new { Msg = msg, Focus = kind });
    }

    public async Task<IActionResult> OnPostResetAsync(int kind, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        if (!Enum.IsDefined(typeof(SoMeTemplateKind), kind))
        {
            return RedirectToPage(new { Msg = "That is not one of the five post types." });
        }

        var k = (SoMeTemplateKind)kind;
        var removed = await _templates.ResetAsync(me.EventId, k, ct);

        return RedirectToPage(new
        {
            Msg = removed
                ? $"“{SoMeTemplateCatalog.Title(k)}” is back to the shipped wording."
                : $"“{SoMeTemplateCatalog.Title(k)}” was already the shipped wording — nothing changed.",
            Focus = kind,
        });
    }
}
