using CommunityHub.Auth;
using CommunityHub.Forms.Steps;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages;

/// <summary>
/// The participant's own profile page (REQUIREMENTS §1): every signed-in role can view and
/// edit their own basics — display name, phone, alternate sign-in email — and see the
/// read-only facts that identify them (email = login identity; role = admin-set).
///
/// <para>REQUIREMENTS §148: this standalone page is now a thin SHELL — it renders the shared
/// <c>_ProfileFields</c> partial and delegates load + validate + persist + the speaker-only
/// speaking-days side-effect to <see cref="ProfileFormService"/>. The SAME service backs the
/// inline wizard step (<c>ProfileStepHandler</c>), so the standalone page and the wizard
/// behave identically. The page stays deep-linked from My Tasks / emails, so its behavior is
/// unchanged. Editing is scoped to the signed-in participant's own row only.</para>
/// </summary>
[Authorize]
public class ProfileModel : PageModel
{
    private readonly ProfileFormService _profile;
    private readonly ICurrentParticipantAccessor _participant;

    public ProfileModel(ProfileFormService profile, ICurrentParticipantAccessor participant)
    {
        _profile = profile;
        _participant = participant;
    }

    /// <summary>The shared render+edit model rendered by the <c>_ProfileFields</c> partial. Bound
    /// with an EMPTY prefix in <see cref="OnPostAsync"/> so the partial's flat input names
    /// (FullName / Phone / AlternateEmail) match — identical to the inline wizard step.</summary>
    public ProfileFormModel Form { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        var form = await _profile.LoadAsync(me.EventId, me.ParticipantId, me.Role, ct);
        if (form is null) return RedirectToPage("/Login");   // signed in but no participant row
        Form = form;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        // Bind the posted editable fields (empty prefix → flat names from the partial) into a
        // fresh model, then delegate validate + persist + the speaker side-effect to the shared
        // service; it re-derives the read-only identity facts + banner. Same flow as the inline
        // wizard step. The standalone page always re-renders (success shows the saved message,
        // invalid the errors).
        Form = new ProfileFormModel { Role = me.Role };
        await TryUpdateModelAsync(Form, name: string.Empty);
        await _profile.SaveAsync(Form, me.EventId, me.ParticipantId, me.Role, ModelState, ct);
        return Page();
    }
}
