using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Forms.Steps;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Forms;

/// <summary>
/// "Calendar email (optional)" — the FIRST step of the speaker Get-Started wizard
/// (operator 2026-06-27, §141). Lets a speaker optionally enter an ALTERNATE email
/// used only for CALENDAR invites / notifications; many speakers don't use their
/// Sessionize/community email for their calendar. Left blank, calendar mail goes to
/// their primary (Sessionize) address. The value is stored on
/// <see cref="SpeakerProfile.CalendarEmail"/> and routed by
/// <see cref="SpeakerProfile.CalendarEmailFor"/>; saving the step (even blank)
/// stamps <see cref="SpeakerProfile.CalendarEmailSetAt"/> so the optional step can
/// count as DONE in the wizard. Speaker-only.
///
/// <para>REQUIREMENTS §148: this standalone page is now a thin SHELL — it renders the shared
/// <c>_CalendarEmailFields</c> partial and delegates load + validate + persist to
/// <see cref="CalendarEmailFormService"/>. The SAME service backs the inline wizard step
/// (<c>CalendarEmailStepHandler</c>), so the standalone page and the wizard behave identically.
/// The page is still deep-linked from My Tasks / emails, so its behavior is unchanged.</para>
/// </summary>
[Authorize]
public class CalendarEmailModel : PageModel
{
    private readonly CalendarEmailFormService _service;
    private readonly ICurrentParticipantAccessor _participant;

    public CalendarEmailModel(
        CalendarEmailFormService service,
        ICurrentParticipantAccessor participant)
    {
        _service = service;
        _participant = participant;
    }

    public bool AccessDenied { get; private set; }

    /// <summary>The shared render+edit model rendered by the <c>_CalendarEmailFields</c> partial. Bound
    /// with an EMPTY prefix in <see cref="OnPostAsync"/> so the partial's flat input name
    /// (CalendarEmail) matches — identical to the inline wizard step.</summary>
    public CalendarEmailFormModel Form { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Speaker) { AccessDenied = true; return Page(); }

        Form = await _service.LoadAsync(me.EventId, me.ParticipantId, me.Role, me.Email, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Speaker) { AccessDenied = true; return Page(); }

        // Bind the posted editable field (empty prefix → flat name from the partial) into a fresh
        // model, then delegate validate + persist to the shared service. Same flow as the wizard step.
        Form = new CalendarEmailFormModel { Role = me.Role, PrimaryEmail = me.Email };
        await TryUpdateModelAsync(Form, name: string.Empty);
        await _service.SaveAsync(
            Form, me.EventId, me.ParticipantId, me.Email, me.FullName, me.Role, ModelState, ct);

        // The standalone page always re-renders (success shows the saved message, invalid the errors).
        return Page();
    }
}
