using CommunityHub.Auth;
using CommunityHub.Core.Config;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Participants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Speaker;

/// <summary>
/// The signed-in speaker's own "am I ready?" page (REQUIREMENTS §134): a single
/// readiness score ("4 of 7 done") with the what's-missing checklist, each missing item
/// deep-linking to the page that fixes it. Pure rollup of EXISTING data via
/// <see cref="SpeakerReadinessService"/> — no new source of truth.
///
/// <para>Only Speakers see the content; any other role gets a friendly "not a speaker"
/// notice (matches the Speaker hub), not a 403, so the nav stays simple. Before reading,
/// it seeds the speaker's deadline tasks (idempotent) and reconciles already-submitted
/// form data onto open tasks, so the score reflects the true state on first visit.</para>
/// </summary>
[Authorize]
public class ReadinessModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SpeakerReadinessService _readiness;
    private readonly SpeakerDeadlineSeeder _speakerDeadlines;
    private readonly FormTaskReconciler _formTaskReconciler;
    private readonly ILogger<ReadinessModel> _logger;

    public ReadinessModel(
        ICurrentParticipantAccessor participant,
        SpeakerReadinessService readiness,
        SpeakerDeadlineSeeder speakerDeadlines,
        FormTaskReconciler formTaskReconciler,
        ILogger<ReadinessModel> logger)
    {
        _participant = participant;
        _readiness = readiness;
        _speakerDeadlines = speakerDeadlines;
        _formTaskReconciler = formTaskReconciler;
        _logger = logger;
    }

    public bool AccessDenied { get; private set; }
    public ParticipantRole Role { get; private set; }
    public string FirstName { get; private set; } = "there";

    /// <summary>The speaker's readiness rollup, or null when their speaker profile is missing.</summary>
    public SpeakerReadiness? Readiness { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        Role = me.Role;
        FirstName = me.FirstName;
        if (me.Role != ParticipantRole.Speaker)
        {
            AccessDenied = true;
            return Page();
        }

        // Make sure the speaker's deadline tasks exist + reflect already-submitted form
        // data before we score them (idempotent; never fails the page).
        try { await _speakerDeadlines.SeedAsync(me.EventId, ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "Readiness: deadline seeding failed for event {EventId}", me.EventId); }
        try { await _formTaskReconciler.ReconcileAsync(me.EventId, me.ParticipantId, ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "Readiness: form-task reconcile failed for participant {Pid}", me.ParticipantId); }

        Readiness = await _readiness.BuildForSpeakerAsync(me.EventId, me.ParticipantId, ct);
        return Page();
    }
}
