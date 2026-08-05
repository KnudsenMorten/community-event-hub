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

    /// <summary>
    /// §707.38 — lifecycle-active people <b>PLUS</b> the 1-day ticket holders, who are inactive
    /// <i>by design</i> (operator 2026-07-30).
    /// </summary>
    /// <remarks>
    /// 🔒 <b>A 1-day holder being INACTIVE is not a fault to be repaired.</b> Operator 2026-07-30:
    /// *"make a note that it is by design that 1-day ticket holders have their state as inactive as
    /// they dont require access to hub"*. They attend the main event only; the hub exists for
    /// Master-Class selection, tasks and onboarding, none of which apply to them. `attendee-1day-access`
    /// (§242, default OFF) is what holds that, reversibly.
    ///
    /// <para>So "Active only" legitimately hides them and "Inactive only" mixes them in with genuinely
    /// withdrawn people — neither answers *"everyone who is really coming"*. This filter does, without
    /// pretending they can sign in.</para>
    /// </remarks>
    ActivePlusOneDay = 3,
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
            // §707.38 — "Active + 1-day attendees".
            "active+1day" => ParticipantStatusFilter.ActivePlusOneDay,
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

            // §707.38 — active people PLUS 1-day ticket holders, who are inactive BY DESIGN.
            //
            // 🔒 The 1-day arm is keyed on an ACTIVE MIRROR ROW whose ticket is not 2-day
            // (`TicketStatus.Other` — the sync's own label for "an active ticket that is not 2-day"),
            // NOT on the participant being inactive. That distinction is the whole point: it lets a
            // genuinely WITHDRAWN person stay hidden while a 1-day holder shows, where a naive
            // "active OR inactive-attendee" rule would drag both in.
            ParticipantStatusFilter.ActivePlusOneDay =>
                query.Where(p => (p.IsActive && p.LifecycleState == ParticipantLifecycleState.Active)
                                 || _db.Attendees.Any(a =>
                                        a.EventId == p.EventId
                                        && a.MirrorState == MirrorState.Active
                                        && a.TicketStatus == TicketStatus.Other
                                        && a.Email.ToLower() == p.Email.ToLower())),

            _ => query, // All
        };

        if (request.Role is not null)
        {
            query = query.Where(p => p.Role == request.Role.Value);
        }

        // Persona collapses several roles into one audience (media-team ⇒ Media,
        // event-partner ⇒ organizer, …). Resolve to the set
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

            // §769.10 — SEARCH BY CEH ID (operator 2026-08-02: "i need to be able to search for
            // id"). The id is what names files in the document library now
            // (speaker-photo-{id}, volunteer-photo-{id}), so "who is 73?" is a question the grid
            // has to be able to answer.
            //
            // 🔑 A digits-only term matches the id EXACTLY **and** still matches name/email: ids
            // occur inside phone numbers and addresses, so an exact-only search would hide the
            // person somebody was actually looking for.
            query = int.TryParse(s, out var id)
                ? query.Where(p => p.Id == id || p.FullName.Contains(s) || p.Email.Contains(s))
                : query.Where(p => p.FullName.Contains(s) || p.Email.Contains(s));
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

        // §769.10 — the global "find a person fast" box searches the CEH id too, on the same rule
        // as the grid: an id hit does not suppress name/email hits.
        var isId = int.TryParse(s, out var idTerm);

        var rows = await _db.Participants
            .Where(p => p.EventId == eventId
                        && ((isId && p.Id == idTerm)
                            || p.FullName.Contains(s) || p.Email.Contains(s)))
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
        PersonaGroup.Speaker => new[] { ParticipantRole.Speaker },
        PersonaGroup.Volunteer => new[] { ParticipantRole.Volunteer },
        PersonaGroup.MediaTeam => new[] { ParticipantRole.Media },
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
