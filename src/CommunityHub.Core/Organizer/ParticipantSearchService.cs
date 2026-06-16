using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Organizer;

/// <summary>
/// The status slice a participant search is scoped to. "Active" everywhere means
/// the lifecycle-correct rule (<see cref="ParticipantActivation.IsActiveExpr"/> —
/// <c>IsActive AND LifecycleState == Active</c>), the SAME gate the PIN login
/// enforces, so the grid never disagrees with who can actually sign in.
/// </summary>
public enum ParticipantStatusFilter
{
    /// <summary>Lifecycle-active people only (the default).</summary>
    Active = 0,

    /// <summary>People who are NOT lifecycle-active (withdrawn or not-yet-activated).</summary>
    Inactive = 1,

    /// <summary>Everyone in the edition, regardless of status.</summary>
    All = 2,
}

/// <summary>The column a participant grid is sorted on.</summary>
public enum ParticipantSortColumn
{
    Name = 0,
    Email = 1,
    Persona = 2,
    Status = 3,
}

/// <summary>
/// A normalized, server-trusted participant-search request. The page binds raw
/// query strings; <see cref="ParticipantSearchService.Parse"/> turns them into
/// this so the service never re-parses loose strings and a hand-edited query
/// can't smuggle anything past the (server-side) role gate the page enforces.
/// </summary>
/// <param name="Text">Free-text match on name + email (trimmed; null/blank = no text filter).</param>
/// <param name="Role">Restrict to one <see cref="ParticipantRole"/>, or null for all roles.</param>
/// <param name="Persona">Restrict to one persona group (collapses related roles), or null for all.</param>
/// <param name="Status">Active (default) / Inactive / All.</param>
/// <param name="SponsorCompanyId">Restrict to one sponsor company id, or null for all.</param>
/// <param name="Sort">Sort column (default name).</param>
/// <param name="Descending">Sort direction.</param>
public sealed record ParticipantSearchRequest(
    string? Text,
    ParticipantRole? Role,
    PersonaGroup? Persona,
    ParticipantStatusFilter Status,
    string? SponsorCompanyId,
    ParticipantSortColumn Sort,
    bool Descending);

/// <summary>One global-search hit — the minimum needed to recognise and jump to a person.</summary>
public sealed record PersonHit(
    int ParticipantId,
    string FullName,
    string Email,
    ParticipantRole Role,
    bool IsActive,
    string? SponsorCompanyId);

/// <summary>
/// The single, server-side authority for organizer participant
/// search / filter / sort and the cross-edition "find a person fast" global
/// search (REQUIREMENTS §20 Organizer). It is the ONE place the
/// free-text + role/persona + status + sort rules live, so the participant grid
/// and the global-search box never drift apart.
///
/// Pure + side-effect free (read-only queries, never writes); every method is
/// <b>event-scoped</b> by the <c>eventId</c> the caller passes, and "active"
/// always resolves through <see cref="ParticipantActivation.IsActiveExpr"/> so
/// the search agrees with the login gate. Role enforcement is the page's job
/// (organizer-only, server-checked) — this service deliberately holds no
/// authorization, exactly like <see cref="OnboardingService"/> /
/// <see cref="ParticipantBulkOperationService"/>, so it stays unit-testable
/// against the in-memory provider.
/// </summary>
public sealed class ParticipantSearchService
{
    private readonly CommunityHubDbContext _db;

    public ParticipantSearchService(CommunityHubDbContext db) => _db = db;

    /// <summary>Default hits returned by the global "find a person" box.</summary>
    public const int DefaultGlobalLimit = 20;

    /// <summary>Hard upper bound on global-search hits so a big query stays cheap.</summary>
    public const int MaxGlobalLimit = 50;

    /// <summary>
    /// Normalize loosely-typed query-string values (the ones a Razor page binds)
    /// into a trusted <see cref="ParticipantSearchRequest"/>: unknown sort keys
    /// fall back to name, an unparseable status falls back to Active, and blank
    /// text/company become null. Pure — no DB access — so the page can call it
    /// before issuing the query and tests can assert the mapping directly.
    /// </summary>
    public static ParticipantSearchRequest Parse(
        string? text,
        ParticipantRole? role,
        PersonaGroup? persona,
        string? status,
        string? sponsorCompanyId,
        string? sort,
        bool descending)
    {
        var statusValue = (status?.Trim().ToLowerInvariant()) switch
        {
            "inactive" => ParticipantStatusFilter.Inactive,
            "all" => ParticipantStatusFilter.All,
            _ => ParticipantStatusFilter.Active,
        };

        var sortValue = (sort?.Trim().ToLowerInvariant()) switch
        {
            "email" => ParticipantSortColumn.Email,
            "persona" => ParticipantSortColumn.Persona,
            "status" => ParticipantSortColumn.Status,
            _ => ParticipantSortColumn.Name,
        };

        return new ParticipantSearchRequest(
            string.IsNullOrWhiteSpace(text) ? null : text.Trim(),
            role,
            persona,
            statusValue,
            string.IsNullOrWhiteSpace(sponsorCompanyId) ? null : sponsorCompanyId.Trim(),
            sortValue,
            descending);
    }

    /// <summary>
    /// Build the event-scoped, filtered + sorted participant query (deferred —
    /// not yet executed) for a request. Callers add paging
    /// (<c>.Skip(...).Take(...)</c>) after taking a <c>CountAsync()</c>. Sort
    /// always carries an <c>Id</c> tiebreak so paging is deterministic. The
    /// status / role / persona / company / free-text rules all live here.
    /// </summary>
    public IQueryable<Participant> Query(int eventId, ParticipantSearchRequest request)
    {
        var query = _db.Participants.Where(p => p.EventId == eventId);

        query = request.Status switch
        {
            ParticipantStatusFilter.Active => query.Where(ParticipantActivation.IsActiveExpr),
            ParticipantStatusFilter.Inactive =>
                query.Where(p => !(p.IsActive && p.LifecycleState == ParticipantLifecycleState.Active)),
            _ => query, // All
        };

        if (request.Role is not null)
        {
            query = query.Where(p => p.Role == request.Role.Value);
        }

        // Persona collapses several roles into one audience (speaker ⇒ Speaker +
        // MasterclassSpeaker, media-team ⇒ Video + Camera, …). Resolve to the set
        // of roles in that persona and filter to them; an explicit Role filter
        // (above) is the finer-grained control and both can apply together.
        if (request.Persona is not null)
        {
            var roles = RolesFor(request.Persona.Value);
            query = query.Where(p => roles.Contains(p.Role));
        }

        if (!string.IsNullOrWhiteSpace(request.SponsorCompanyId))
        {
            query = query.Where(p => p.SponsorCompanyId == request.SponsorCompanyId);
        }

        if (!string.IsNullOrWhiteSpace(request.Text))
        {
            var s = request.Text;
            query = query.Where(p => p.FullName.Contains(s) || p.Email.Contains(s));
        }

        return ApplySort(query, request.Sort, request.Descending);
    }

    /// <summary>
    /// Cross-the-edition "find a person fast" global search: a free-text match on
    /// name + email returning at most <paramref name="limit"/> recognisable hits
    /// (clamped to <see cref="MaxGlobalLimit"/>), ordered by name. Event-scoped
    /// and read-only. A blank query returns no hits (the box only searches once a
    /// term is typed). Includes inactive people so an organizer can still find a
    /// withdrawn participant — the hit carries the lifecycle-correct
    /// <see cref="PersonHit.IsActive"/> so the UI can flag them.
    /// </summary>
    public async Task<IReadOnlyList<PersonHit>> GlobalSearchAsync(
        int eventId, string? text, int limit = DefaultGlobalLimit,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<PersonHit>();

        var s = text.Trim();
        var take = limit <= 0 ? DefaultGlobalLimit : Math.Min(limit, MaxGlobalLimit);

        var rows = await _db.Participants
            .Where(p => p.EventId == eventId
                        && (p.FullName.Contains(s) || p.Email.Contains(s)))
            .OrderBy(p => p.FullName).ThenBy(p => p.Id)
            .Take(take)
            .Select(p => new
            {
                p.Id, p.FullName, p.Email, p.Role,
                p.IsActive, p.LifecycleState, p.SponsorCompanyId,
            })
            .ToListAsync(ct);

        return rows
            .Select(p => new PersonHit(
                p.Id, p.FullName, p.Email, p.Role,
                p.IsActive && p.LifecycleState == ParticipantLifecycleState.Active,
                p.SponsorCompanyId))
            .ToList();
    }

    /// <summary>The roles that make up a persona group (the inverse of <see cref="OnboardingEmailSets.PersonaFor"/>).</summary>
    public static IReadOnlyList<ParticipantRole> RolesFor(PersonaGroup persona) => persona switch
    {
        PersonaGroup.Organizer => new[] { ParticipantRole.Organizer },
        PersonaGroup.Speaker => new[] { ParticipantRole.Speaker, ParticipantRole.MasterclassSpeaker },
        PersonaGroup.Volunteer => new[] { ParticipantRole.Volunteer },
        PersonaGroup.MediaTeam => new[] { ParticipantRole.Video, ParticipantRole.Camera },
        PersonaGroup.Sponsor => new[] { ParticipantRole.Sponsor },
        _ => Array.Empty<ParticipantRole>(),
    };

    /// <summary>Stable ordering for the chosen column (Id tiebreak = deterministic paging).</summary>
    private static IQueryable<Participant> ApplySort(
        IQueryable<Participant> q, ParticipantSortColumn sort, bool desc) => (sort, desc) switch
    {
        (ParticipantSortColumn.Email, false) => q.OrderBy(p => p.Email).ThenBy(p => p.Id),
        (ParticipantSortColumn.Email, true) => q.OrderByDescending(p => p.Email).ThenByDescending(p => p.Id),
        (ParticipantSortColumn.Persona, false) => q.OrderBy(p => p.Role).ThenBy(p => p.FullName).ThenBy(p => p.Id),
        (ParticipantSortColumn.Persona, true) => q.OrderByDescending(p => p.Role).ThenBy(p => p.FullName).ThenBy(p => p.Id),
        (ParticipantSortColumn.Status, false) => q.OrderBy(p => p.IsActive).ThenBy(p => p.FullName).ThenBy(p => p.Id),
        (ParticipantSortColumn.Status, true) => q.OrderByDescending(p => p.IsActive).ThenBy(p => p.FullName).ThenBy(p => p.Id),
        (_, true) => q.OrderByDescending(p => p.FullName).ThenByDescending(p => p.Id),
        _ => q.OrderBy(p => p.FullName).ThenBy(p => p.Id),
    };
}
