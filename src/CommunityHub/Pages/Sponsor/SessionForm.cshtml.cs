using CommunityHub.Auth;
using CommunityHub.Forms;
using CommunityHub.Forms.Steps;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Sponsor;

/// <summary>
/// §651 — the STANDALONE "Submit session description" form, reached from the sponsor task.
/// </summary>
/// <remarks>
/// <para>🔴 <b>Why this page had to exist.</b> §648 pointed the task at
/// <c>/Forms/Wizard?step=session</c>, and §495's comment promised *"the /Forms/Wizard?step=session
/// PAGE stays routable so the task's deep link still works"*. <b>It does not.</b> §495 removed the
/// session step from the sponsor wizard's step PLAN, and the wizard host can only select a step that
/// is in that plan — so the deep link silently fell through to whatever step was incomplete, and the
/// sponsor landed on the wrong form. Operator: *"bug: it takes to wrong form in get started"*.</para>
///
/// <para>That is a documented-but-unverified assumption failing exactly the way §637 did. The fix is
/// not to put the step back — §495's reasoning stands, and the operator restated it: *"a sponsor 6
/// months out don't know who will present a session"*. The form simply needs a home of its own.</para>
///
/// <para><b>Nothing is duplicated.</b> This page is a thin shell over the SAME
/// <see cref="SponsorSessionFormService"/> and the SAME <c>_SponsorSessionFields</c> partial the
/// wizard step used — the §148 pattern every other standalone form here follows. A second copy of
/// the form would drift from the first, which is the whole reason §537 disliked the e-mail route.</para>
/// </remarks>
[Authorize]
public class SessionFormModel : PageModel
{
    private readonly SponsorSessionFormService _session;
    private readonly ICurrentParticipantAccessor _participant;

    public SessionFormModel(SponsorSessionFormService session, ICurrentParticipantAccessor participant)
    {
        _session = session;
        _participant = participant;
    }

    /// <summary>
    /// The shared render+edit model rendered by <c>_SponsorSessionFields</c>. Bound with an EMPTY
    /// prefix so the partial's flat input names match — identical to the old wizard step.
    /// </summary>
    public SponsorSessionModel Form { get; private set; } = new();

    /// <summary>True when this participant is not a sponsor contact with a speaking session.</summary>
    public bool AccessDenied { get; private set; }

    /// <summary>Set after a clean save, so the page can confirm rather than look inert.</summary>
    public bool Saved { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        Form = await _session.LoadAsync(me.EventId, me.ParticipantId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        await TryUpdateModelAsync(Form, name: string.Empty);

        var outcome = await _session.SaveAsync(
            Form, me.EventId, me.ParticipantId, me.Email, ModelState, ct);

        if (outcome == WizardStepOutcome.NotRelevant)
        {
            AccessDenied = true;
            return Page();
        }

        // §648: a clean save also CLOSES the sponsor task (inside the service), so the confirmation
        // has to say that — otherwise the sponsor returns to the task list expecting to tick
        // something off and finds nothing to tick.
        Saved = outcome == WizardStepOutcome.Advance;

        // Re-load so the page shows what was actually stored (saved speakers, existing photo URLs),
        // rather than only what was typed — a file input cannot be re-filled, and §356 added
        // CurrentPhotoUrls precisely so a sponsor does not re-upload on every visit.
        if (Saved) Form = await _session.LoadAsync(me.EventId, me.ParticipantId, ct);

        return Page();
    }
}
