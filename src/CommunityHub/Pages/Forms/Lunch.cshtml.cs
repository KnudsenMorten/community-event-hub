using CommunityHub.Auth;
using CommunityHub.Forms.Steps;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Forms;

/// <summary>
/// Lunch-logistics form: which lunches (Setup-day / Pre-day / Master Class) a participant
/// will join. One <c>LunchSignup</c> per participant per edition.
///
/// <para>REQUIREMENTS §148: this standalone page is now a thin SHELL — it renders the shared
/// <c>_LunchFields</c> partial and delegates load + validate + persist + ALL side-effects
/// (per-role visibility, day labels, auto-task ensure+done) to <see cref="LunchFormService"/>.
/// The SAME service backs the inline wizard step (<c>LunchStepHandler</c>), so the standalone
/// page and the wizard behave identically. The page is still deep-linked from My Tasks /
/// emails, so its behavior is unchanged.</para>
/// </summary>
[Authorize]
public class LunchModel : PageModel
{
    private readonly LunchFormService _lunch;
    private readonly ICurrentParticipantAccessor _participant;

    public LunchModel(LunchFormService lunch, ICurrentParticipantAccessor participant)
    {
        _lunch = lunch;
        _participant = participant;
    }

    /// <summary>The shared render+edit model rendered by the <c>_LunchFields</c> partial. Bound
    /// with an EMPTY prefix in <see cref="OnPostAsync"/> so the partial's flat input names
    /// (LunchEarlySetupDay / LunchPreDay / …) match — identical to the inline wizard step.</summary>
    public LunchFormModel Form { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        // Load resolves per-role visibility + day labels, ensures the auto-task, and hydrates
        // any existing signup; it sets Form.AccessDenied when no lunch day applies.
        Form = await _lunch.LoadAsync(me.EventId, me.ParticipantId, me.Role, me.FullName, me.Email, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        // Bind the posted editable fields (empty prefix → flat names from the partial) into a
        // fresh model, then delegate validate + persist + all side-effects to the shared service;
        // it re-derives visibility/labels and sets the saved message. Same flow as the inline
        // wizard step. The page always re-renders (success shows the message, invalid the errors,
        // not-relevant the access card).
        Form = new LunchFormModel { Role = me.Role };
        await TryUpdateModelAsync(Form, name: string.Empty);
        await _lunch.SaveAsync(
            Form, me.EventId, me.ParticipantId, me.FullName, me.Email, me.Role, ModelState, ct);
        return Page();
    }
}
