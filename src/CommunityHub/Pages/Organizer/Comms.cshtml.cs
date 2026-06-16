using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Organizer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Comms cockpit (REQUIREMENTS §20 Organizer — "Comms cockpit"): the single place
/// that schedules / sends / tracks all email + SoMe. It consolidates fragmented
/// outreach so nothing is missed or double-sent:
///   <list type="bullet">
///   <item>a unified <b>timeline</b> over email (<c>EmailLog</c>) + the LinkedIn
///   scheduled-post queue (<c>SoMePost</c>) — what went out and what is going out;</item>
///   <item><b>who-got-what</b> + per-campaign delivery views sourced from the real
///   <c>EmailLog</c>, so they reflect the actual outcome (sent / dropped-by-allowlist
///   / failed), never optimistic;</item>
///   <item><b>resend</b> a message to a participant whose mail did not reach them,
///   via the existing per-person send path (<c>ParticipantEmailService</c>);</item>
///   <item>the upcoming-scheduled call-out (the next reminders / posts due).</item>
///   </list>
/// All aggregation is read-only (<see cref="CommsCockpitService"/>); the only write
/// is the resend, which reuses the Email Center's proven per-person send. The page
/// still fans out to the existing comms tools (Email Center / Log / Broadcast /
/// invitations / welcome / reminders) — it extends them, never duplicates them.
/// Auth mirrors the rest of /Organizer/*: signed-in Organizer only, else AccessDenied.
/// </summary>
[Authorize]
public class CommsModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly CommsCockpitService _cockpit;
    private readonly ParticipantEmailService _participantEmail;

    public CommsModel(
        ICurrentParticipantAccessor participant,
        CommsCockpitService cockpit,
        ParticipantEmailService participantEmail)
    {
        _participant = participant;
        _cockpit = cockpit;
        _participantEmail = participantEmail;
    }

    public bool AccessDenied { get; private set; }

    /// <summary>The cockpit snapshot rendered on the page (null only on access-denied).</summary>
    public CommsCockpitSnapshot? Snapshot { get; private set; }

    /// <summary>A friendly outcome banner after a resend (rendered via role="status").</summary>
    [BindProperty(SupportsGet = true)] public string? Msg { get; set; }
    public bool MsgIsError { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        Snapshot = await _cockpit.BuildAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>
    /// Resend a previously-undelivered message to one participant. Reuses the
    /// Email Center's per-person send (<c>ParticipantEmailService</c>) so the
    /// routing (effective To + secondary CC), branding and EmailLog auditing all
    /// behave identically — and the resend is itself logged (category "manual-resend")
    /// so its real outcome shows back up on this same cockpit.
    /// </summary>
    public async Task<IActionResult> OnPostResendAsync(
        int participantId, string template, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) return Forbid();

        string msg;
        if (participantId <= 0 || string.IsNullOrWhiteSpace(template))
        {
            msg = "Pick a person and a template to resend.";
        }
        else
        {
            try
            {
                var to = await _participantEmail.SendTemplateToParticipantAsync(
                    me.EventId, participantId, template.Trim(),
                    category: "manual-resend", extraTokens: null, ct);
                msg = to is null
                    ? "That person was not found in this edition."
                    : $"Resent '{template.Trim()}' to {to}.";
            }
            catch (Exception ex)
            {
                msg = $"Resend failed: {ex.Message}";
            }
        }
        return RedirectToPage(new { Msg = msg });
    }
}
