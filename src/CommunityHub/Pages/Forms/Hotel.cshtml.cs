using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Forms.Steps;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Forms;

/// <summary>
/// Hotel-preference form (CONTEXT.md section 9). The participant submits or updates one
/// HotelBooking per edition. Edits are blocked after the edition lock date (Event.LockDate).
///
/// <para>REQUIREMENTS §148: this standalone page is now a thin SHELL — it renders the shared
/// <c>_HotelFields</c> partial and delegates load + validate + persist + ALL side-effects to
/// <see cref="HotelFormService"/>. The SAME service backs the inline wizard step
/// (<c>HotelStepHandler</c>), so the standalone page and the wizard behave identically. The
/// page is still deep-linked from My Tasks / emails, so its behavior is unchanged.</para>
/// </summary>
[Authorize]
public class HotelModel : PageModel
{
    private readonly HotelFormService _hotel;
    private readonly ICurrentParticipantAccessor _participant;

    public HotelModel(HotelFormService hotel, ICurrentParticipantAccessor participant)
    {
        _hotel = hotel;
        _participant = participant;
    }

    /// <summary>The shared render+edit model rendered by the <c>_HotelFields</c> partial. Bound
    /// with an EMPTY prefix in <see cref="OnPostAsync"/> so the partial's flat input names
    /// (NeedsRoom / CheckInDate / …) match — identical to the inline wizard step.</summary>
    public HotelFormModel Form { get; private set; } = new();

    /// <summary>
    /// Hotel is arranged + covered by us for crew/speakers/organizers; sponsors and attendees
    /// arrange their own accommodation. Additionally entitlement-gated (OrderItem.Hotel) so a
    /// self-funded speaker is "not relevant". Computed via <see cref="HotelFormService.IsRelevantAsync"/>.
    /// </summary>
    public bool HotelRelevant { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        HotelRelevant = await _hotel.IsRelevantAsync(me.EventId, me.ParticipantId, me.Role, ct);
        if (!HotelRelevant) return Page();   // not entitled / sponsors / attendees: "not relevant" view, no form

        Form = await _hotel.LoadAsync(me.EventId, me.ParticipantId, me.Role, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        HotelRelevant = await _hotel.IsRelevantAsync(me.EventId, me.ParticipantId, me.Role, ct);
        if (!HotelRelevant) return Page();   // not entitled / sponsors / attendees can't book through us

        // Bind the posted editable fields (empty prefix → flat names from the partial) into a
        // fresh model, then delegate validate + persist + all side-effects to the shared service;
        // it re-derives lock/placement/message. Same flow as the inline wizard step.
        Form = new HotelFormModel { Role = me.Role };
        await TryUpdateModelAsync(Form, name: string.Empty);
        await _hotel.SaveAsync(
            Form, me.EventId, me.ParticipantId, me.Email, me.FullName, me.Role, ModelState, ct);

        // The standalone page always re-renders (success shows the saved message, invalid the errors).
        return Page();
    }
}
