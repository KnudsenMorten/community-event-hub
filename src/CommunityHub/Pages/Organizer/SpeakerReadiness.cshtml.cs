using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Participants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Organizer "Speaker readiness" roster (REQUIREMENTS §134): every speaker with their
/// readiness score and what's-still-missing, sorted LOWEST readiness first so the people
/// who need chasing are at the top. Read-only rollup of EXISTING data via
/// <see cref="SpeakerReadinessService"/> — no new source of truth, no writes.
///
/// <para>Auth: organizer-gated (a signed-in <see cref="ParticipantRole.Organizer"/>);
/// a non-organizer gets a friendly notice, not a 403 (matches the other organizer pages).
/// Scoped to the caller's edition.</para>
/// </summary>
[Authorize]
public class SpeakerReadinessModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SpeakerReadinessService _readiness;

    public SpeakerReadinessModel(
        ICurrentParticipantAccessor participant, SpeakerReadinessService readiness)
    {
        _participant = participant;
        _readiness = readiness;
    }

    /// <summary>True when the caller is not an organizer — render a friendly notice.</summary>
    public bool AccessDenied { get; private set; }

    /// <summary>Set when the data layer fails — an honest banner instead of a 500.</summary>
    public string? Error { get; private set; }

    /// <summary>Every speaker's readiness, lowest first.</summary>
    public IReadOnlyList<SpeakerReadiness> Speakers { get; private set; } = Array.Empty<SpeakerReadiness>();

    /// <summary>Count of speakers who are fully ready (for the summary line).</summary>
    public int ReadyCount => Speakers.Count(s => s.IsReady);

    /// <summary>Edition-wide average readiness percent (0 when there are no speakers).</summary>
    public int AveragePercent =>
        Speakers.Count == 0 ? 0 : (int)Math.Round(Speakers.Average(s => s.Percent));

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        try
        {
            Speakers = await _readiness.BuildRosterAsync(me.EventId, ct);
        }
        catch (Exception ex)
        {
            Error = "The speaker readiness data could not be loaded right now.";
            System.Diagnostics.Debug.WriteLine(ex);
        }
        return Page();
    }
}
