using CommunityHub.Auth;
using CommunityHub.Forms.Steps;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Forms;

/// <summary>
/// Appreciation-dinner RSVP form. The participant submits or updates one DinnerSignup per
/// edition (RSVP yes/no/maybe + plus-one count + structured dietary + comments). Edits are
/// blocked after the edition lock date (Event.LockDate).
///
/// <para>REQUIREMENTS §148: this standalone page is now a thin SHELL — it renders the shared
/// <c>_DinnerFields</c> partial and delegates load + validate + persist + ALL side-effects
/// (structured dietary upsert, auto-task ensure+done, late-change alert, ICS calendar invite)
/// to <see cref="DinnerFormService"/>. The SAME service backs the inline wizard step
/// (<c>DinnerStepHandler</c>), so the standalone page and the wizard behave identically. The
/// page is still deep-linked from My Tasks / emails, so its behavior is unchanged.</para>
/// </summary>
[Authorize]
public class DinnerModel : PageModel
{
    private readonly DinnerFormService _dinner;
    private readonly ICurrentParticipantAccessor _participant;

    public DinnerModel(DinnerFormService dinner, ICurrentParticipantAccessor participant)
    {
        _dinner = dinner;
        _participant = participant;
    }

    /// <summary>The shared render+edit model rendered by the <c>_DinnerFields</c> partial. Bound
    /// with an EMPTY prefix in <see cref="OnPostAsync"/> so the partial's flat input names
    /// (Rsvp / PlusOneCount / Dietary.*) match — identical to the inline wizard step.</summary>
    public DinnerFormModel Form { get; private set; } = new();

    /// <summary>
    /// FEATURE B: the appreciation-dinner RSVP is gated by ENTITLEMENT
    /// (<see cref="CommunityHub.Core.Domain.OrderItem.AppreciationDinner"/>) for speakers; every
    /// non-speaker role keeps its historical access. Computed via
    /// <see cref="DinnerFormService.IsRelevantAsync"/>; the inverse drives the access-denied card.
    /// </summary>
    public bool AccessDenied { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        if (!await _dinner.IsRelevantAsync(me.EventId, me.ParticipantId, me.Role, ct))
        {
            AccessDenied = true;
            return Page();
        }

        Form = await _dinner.LoadAsync(me.EventId, me.ParticipantId, me.Role, me.Email, me.FullName, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        if (!await _dinner.IsRelevantAsync(me.EventId, me.ParticipantId, me.Role, ct))
        {
            AccessDenied = true;
            return Page();
        }

        // Bind the posted editable fields (empty prefix → flat names from the partial) into a
        // fresh model, then delegate validate + persist + all side-effects to the shared service;
        // it re-derives lock/context/message. Same flow as the inline wizard step.
        Form = new DinnerFormModel { Role = me.Role };
        await TryUpdateModelAsync(Form, name: string.Empty);
        await _dinner.SaveAsync(
            Form, me.EventId, me.ParticipantId, me.Email, me.FullName, me.Role, ModelState, ct);

        // The standalone page always re-renders (success shows the saved message, invalid the errors).
        return Page();
    }
}
