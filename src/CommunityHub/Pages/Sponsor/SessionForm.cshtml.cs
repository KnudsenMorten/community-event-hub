using CommunityHub.Auth;
using CommunityHub.Core.Domain;
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

    /// <summary>
    /// §783.7 — remove a speaker from this sponsor's session.
    /// </summary>
    /// <remarks>
    /// <para>🔴 <b>The bug this fixes.</b> Operator 2026-08-03: <i>"When I click REMOVE button
    /// speaker is not removed from list below of speaker. I also tried to refresh page, but speaker
    /// is still linked to the session."</i> The removal SERVICE was fine and the button was fine —
    /// this page simply had no <c>RemoveSpeaker</c> handler. Razor Pages does not fault an unmatched
    /// named handler: it runs NO handler and renders the page, so the POST looked like a successful
    /// round-trip that changed nothing. That is why it survived a refresh and why nothing was
    /// logged.</para>
    ///
    /// <para>🔒 <b>The general trap.</b> <c>_SponsorSessionFields</c> is SHARED with
    /// <c>/Forms/Wizard</c>, whose host DOES implement this handler (§734). A fields-partial that
    /// posts to a NAMED handler silently makes that handler part of the contract for every page
    /// hosting the partial — so adding such a button is never a local change. §651 added this page
    /// as "a thin shell over the SAME partial", and the shell was thinner than the partial required.
    /// Pinned by a test that asserts every handler the shared partial posts to exists on BOTH
    /// hosts.</para>
    ///
    /// <para>Sponsor-only by construction: the service resolves the session from the ACTOR's own
    /// <c>SponsorCompanyId</c>, so a posted address can only ever remove a speaker from the
    /// caller's own session — there is no session id on the wire to tamper with.</para>
    /// </remarks>
    [CommunityHub.Audit.Audit("Removed a speaker from the sponsor session",
        Action = "speaker.remove", TargetType = "SponsorSessionSpeaker")]
    public async Task<IActionResult> OnPostRemoveSpeakerAsync(
        string speakerEmail, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Sponsor) return Forbid();

        var removed = await _session.RemoveSpeakerAsync(
            me.EventId, me.ParticipantId, speakerEmail, ct);

        TempData["SponsorSessionMessage"] = removed is null
            ? "That speaker was not found on your session."
            : $"{removed} was removed from your session. We have told the organizers so Zoho can be "
              + "tidied up.";

        // POST→redirect→GET, so the reload the operator reached for shows the new list rather than
        // re-posting the removal.
        return RedirectToPage();
    }
}
