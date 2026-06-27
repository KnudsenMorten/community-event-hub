using CommunityHub.Auth;
using CommunityHub.Forms.Steps;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Forms;

/// <summary>
/// "Join Signal groups" Get-Started step (REQUIREMENTS §109). Renders the role-appropriate
/// Signal chat + broadcast invite buttons (from config/signal-groups.&lt;edition&gt;.json) and a
/// MANUAL "mark completed" toggle — joining is external, so completion is a manual mark-done,
/// exactly like the upload tasks. Completion is tracked on a per-participant <c>signal:</c>
/// <see cref="CommunityHub.Core.Domain.ParticipantTask"/> so it also surfaces in the
/// participant's task list / reminders.
///
/// <para>REQUIREMENTS §148: this standalone page is now a thin SHELL — it renders the shared
/// <c>_SignalFields</c> partial and delegates load + the on/off toggle to
/// <see cref="SignalFormService"/>. The SAME service backs the inline wizard step
/// (<c>SignalStepHandler</c>, whose Save&amp;next acts as mark-done), so the standalone page and
/// the wizard behave identically. The page is still deep-linked from My Tasks / emails, so its
/// behavior is unchanged.</para>
/// </summary>
[Authorize]
public class SignalModel : PageModel
{
    private readonly SignalFormService _signal;
    private readonly ICurrentParticipantAccessor _participant;

    public SignalModel(SignalFormService signal, ICurrentParticipantAccessor participant)
    {
        _signal = signal;
        _participant = participant;
    }

    /// <summary>The shared render model rendered by the <c>_SignalFields</c> partial —
    /// identical to the inline wizard step.</summary>
    public SignalFormModel Form { get; private set; } = new();

    /// <summary>True when the role has no Signal groups (the "not relevant" view, no toggle).</summary>
    public bool OutOfScope => Form.OutOfScope;

    /// <summary>True once this step is marked complete (drives the toggle button label).</summary>
    public bool Done => Form.Done;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        Form = await _signal.LoadAsync(me.EventId, me.ParticipantId, me.Role, ct);
        return Page();
    }

    /// <summary>Toggle the manual "Join Signal groups" completion (mark done / not done).</summary>
    public async Task<IActionResult> OnPostToggleAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        await _signal.ToggleAsync(me.EventId, me.ParticipantId, me.Role, ct);
        return RedirectToPage();
    }
}
