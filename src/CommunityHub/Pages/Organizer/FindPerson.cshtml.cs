using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Organizer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Global "find a person fast" search — the organizer's quickest path from a
/// name or email fragment to the right person (REQUIREMENTS §20 Organizer
/// "Search / filter / sort across participants + global search"). A single box
/// runs an event-scoped free-text match on name + email via the shared
/// <see cref="ParticipantSearchService"/> (the same authority the Participants
/// grid uses) and lists the recognisable hits with a direct jump to each
/// person's editor / full grid.
///
/// Organizer-only and server-enforced: a real Organizer role that is NOT
/// currently acting-as (an impersonating session can never drive this), checked
/// on every request — never trusted from the client.
/// </summary>
[Authorize]
public class FindPersonModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly ParticipantSearchService _search;

    public FindPersonModel(
        ICurrentParticipantAccessor participant, ParticipantSearchService search)
    {
        _participant = participant;
        _search = search;
    }

    public bool AccessDenied { get; private set; }

    /// <summary>The free-text query (name or email fragment).</summary>
    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    /// <summary>True once a (non-blank) search has been run, so the empty/no-match copy is honest.</summary>
    public bool Searched { get; private set; }

    public IReadOnlyList<PersonHit> Hits { get; private set; } = Array.Empty<PersonHit>();

    /// <summary>Resolve a sponsor company id to its display name (fallback chain).</summary>
    public static string CompanyDisplayName(string companyId) =>
        SponsorCompanyName.Resolve(null, null, null, companyId);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        if (!string.IsNullOrWhiteSpace(Q))
        {
            Searched = true;
            Hits = await _search.GlobalSearchAsync(me.EventId, Q, ct: ct);
        }

        return Page();
    }

    /// <summary>
    /// A real organizer = role Organizer AND not currently acting-as, mirroring
    /// the Participants grid: an acting-as session (even one impersonating an
    /// organizer) must never reach the global person search.
    /// </summary>
    private static bool IsRealOrganizer(CurrentParticipant me) =>
        me.Role == ParticipantRole.Organizer && !me.IsActingAs;
}
