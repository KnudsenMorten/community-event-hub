using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// §842.2 — HOW OFTEN EACH POST TYPE IS ANNOUNCED, editable without a deploy.
///
/// <para>Operator 2026-08-05: <i>"i need a smart way to manage cadence, frequency per post type as
/// organizer interface"</i>, serving the goal in §842.4: <i>"full control of when,what comes on
/// linkedin so we mix speaker,sponsor,event messages and also mix times"</i>.</para>
///
/// <para>🔴 <b>Sponsors are refused below twice</b> (§842.5) — the guard lives in
/// <see cref="SoMeCadenceService"/>, not here, so no page or job can route around it.</para>
/// </summary>
[Authorize]
public class SoMeCadenceModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SoMeCadenceService _cadence;

    public SoMeCadenceModel(ICurrentParticipantAccessor participant, SoMeCadenceService cadence)
    {
        _participant = participant;
        _cadence = cadence;
    }

    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }
    public bool MessageIsError { get; private set; }

    public IReadOnlyList<SoMeCadence> Rows { get; private set; } = Array.Empty<SoMeCadence>();

    [BindProperty] public int KindValue { get; set; }
    [BindProperty] public bool Enabled { get; set; }
    [BindProperty] public int Occurrences { get; set; }

    public static string Label(SoMeTemplateKind k) => SoMeCadenceService.Label(k);
    public static bool IsLegal(SoMeTemplateKind k) => SoMeCadenceService.IsLegallyObligated(k);
    public static int DefaultFor(SoMeTemplateKind k) => SoMeCadenceService.DefaultOccurrences(k);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        Rows = await _cadence.GetAllAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var kind = (SoMeTemplateKind)KindValue;

        try
        {
            await _cadence.SaveAsync(me.EventId, kind, Enabled, Occurrences, me.Email, ct);
            Message = $"Saved. {Label(kind)} will be announced {Occurrences} time(s) per subject "
                    + "from the next planning run — posts already scheduled are untouched.";
        }
        catch (InvalidOperationException ex)
        {
            // 🔴 §842.5 — the contractual refusal, shown in his words rather than as a 500.
            Message = ex.Message;
            MessageIsError = true;
        }

        Rows = await _cadence.GetAllAsync(me.EventId, ct);
        return Page();
    }
}
