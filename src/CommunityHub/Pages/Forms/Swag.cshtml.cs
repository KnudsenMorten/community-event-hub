using CommunityHub.Auth;
using CommunityHub.Forms.Steps;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Forms;

/// <summary>
/// Swag-preferences form (polo / jacket / appreciation award / Credly badge). The participant
/// submits or updates one SwagPreference per edition.
///
/// <para>REQUIREMENTS §148: this standalone page is now a thin SHELL — it renders the shared
/// <c>_SwagFields</c> partial and delegates load + validate + persist + ALL side-effects to
/// <see cref="SwagFormService"/>. The SAME service backs the inline wizard step
/// (<c>SwagStepHandler</c>), so the standalone page and the wizard behave identically. The page
/// is still deep-linked from My Tasks / emails, so its behavior is unchanged.</para>
/// </summary>
[Authorize]
public class SwagModel : PageModel
{
    private readonly SwagFormService _swag;
    private readonly ICurrentParticipantAccessor _participant;

    public SwagModel(SwagFormService swag, ICurrentParticipantAccessor participant)
    {
        _swag = swag;
        _participant = participant;
    }

    /// <summary>The shared render+edit model rendered by the <c>_SwagFields</c> partial. Bound
    /// with an EMPTY prefix in <see cref="OnPostAsync"/> so the partial's flat input names
    /// (PoloChoice / WantsGift / …) match — identical to the inline wizard step.</summary>
    public SwagFormModel Form { get; private set; } = new();

    /// <summary>
    /// The swag/polo form is entitlement-gated (<c>OrderItem.Swag</c> OR <c>OrderItem.Polo</c>),
    /// with the historical non-speaker-role guarantee. Computed via
    /// <see cref="SwagFormService.IsRelevantAsync"/>; when false the page shows a "no access" card.
    /// </summary>
    public bool AccessDenied { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        // Seed display identity so the AccessDenied card has the role/name even when gated out.
        Form = new SwagFormModel { Role = me.Role, FullName = me.FullName, Email = me.Email };

        if (!await _swag.IsRelevantAsync(me.EventId, me.ParticipantId, me.Role, ct))
        {
            AccessDenied = true;
            return Page();
        }

        Form = await _swag.LoadAsync(me.EventId, me.ParticipantId, me.Role, me.FullName, me.Email, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        Form = new SwagFormModel { Role = me.Role, FullName = me.FullName, Email = me.Email };

        if (!await _swag.IsRelevantAsync(me.EventId, me.ParticipantId, me.Role, ct))
        {
            AccessDenied = true;
            return Page();
        }

        // Bind the posted editable fields (empty prefix → flat names from the partial), then
        // delegate validate + persist + all side-effects to the shared service. Same flow as the
        // inline wizard step. The standalone page always re-renders (success shows the saved
        // message, invalid the errors).
        await TryUpdateModelAsync(Form, name: string.Empty);
        await _swag.SaveAsync(
            Form, me.EventId, me.ParticipantId, me.FullName, me.Email, me.Role, ModelState, ct);

        return Page();
    }
}
