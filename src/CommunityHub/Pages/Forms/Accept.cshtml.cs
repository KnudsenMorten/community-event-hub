using CommunityHub.Auth;
using CommunityHub.Forms.Steps;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Forms;

/// <summary>
/// "I accept" Get-Started step (REQUIREMENTS §119): a required checkbox linking the
/// Code of Conduct + Privacy Policy. Ticking the box + submitting PERSISTS the
/// acceptance (who/when) as a <see cref="CommunityHub.Core.Domain.ParticipantPolicyAcceptance"/>
/// row — not a transient tick — so it is auditable. Applies across all roles' Get-Started flows.
///
/// <para>REQUIREMENTS §148: this standalone page is now a thin SHELL — it renders the shared
/// <c>_AcceptFields</c> partial and delegates load + validate + persist to
/// <see cref="AcceptFormService"/>. The SAME service backs the inline wizard step
/// (<c>AcceptStepHandler</c>), so the standalone page and the wizard behave identically. The
/// page is still deep-linked from My Tasks / emails, so its behavior is unchanged.</para>
/// </summary>
[Authorize]
public class AcceptModel : PageModel
{
    /// <summary>Back-compat aliases of the canonical URLs (now owned by <see cref="AcceptFormService"/>).</summary>
    public const string CodeOfConductUrl = AcceptFormService.CodeOfConductUrl;
    public const string PrivacyPolicyUrl = AcceptFormService.PrivacyPolicyUrl;

    private readonly AcceptFormService _accept;
    private readonly ICurrentParticipantAccessor _participant;

    public AcceptModel(AcceptFormService accept, ICurrentParticipantAccessor participant)
    {
        _accept = accept;
        _participant = participant;
    }

    /// <summary>The shared render+edit model rendered by the <c>_AcceptFields</c> partial. Bound
    /// with an EMPTY prefix in <see cref="OnPostAsync"/> so the partial's flat input name
    /// (Accept) matches — identical to the inline wizard step.</summary>
    public AcceptFormModel Form { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        Form = await _accept.LoadAsync(me.EventId, me.ParticipantId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        // Bind the posted checkbox (empty prefix → flat name from the partial) into a fresh
        // model, then delegate validate + persist to the shared service; it re-derives the
        // already-accepted state + message. Same flow as the inline wizard step.
        Form = new AcceptFormModel();
        await TryUpdateModelAsync(Form, name: string.Empty);
        await _accept.SaveAsync(Form, me.EventId, me.ParticipantId, me.Email, ModelState, ct);

        // The standalone page always re-renders (success shows the saved message, invalid the error).
        return Page();
    }
}
