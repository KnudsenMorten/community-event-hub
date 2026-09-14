using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Participants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// §1085 — <b>ONE participant-status page for ALL roles, with a filter.</b>
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-13, having distrusted two separate sponsor boards:
/// <i>"in general we should consider one page for all roles with filter"</i> ·
/// <i>"instead of having 7 individual page"</i> · <i>"it makes no sense"</i>.</para>
///
/// <para>🔒 Every number on it comes from <see cref="ParticipantStatusBoardBuilder"/>, which reads
/// the WIZARD SERVICES. This page owns no arithmetic about completion — see
/// <see cref="CommunityHub.Forms.WizardProgressReader"/> for why a second opinion is a bug even when
/// it is right.</para>
///
/// <para>🔴 <b>The performance decision (§1085 asked for it explicitly).</b> A wizard build is
/// several queries per person, and the operator's objection to the completion sweep
/// (<i>"but dont impact performance necessary with this against sql"</i>) is about this exact shape.
/// Options (a) filter+paginate, (b) short cache, (c) a nightly snapshot table. <b>Chosen: (a) + (b).</b>
/// The role filter is applied in SQL before any wizard is built, and the built board is held in
/// <see cref="IMemoryCache"/> for <see cref="CacheTtl"/> so paging, re-sorting and flipping the
/// completion filter cost nothing. (c) was rejected: a snapshot table is a SECOND source of truth for
/// completion, which is the thing §1081 was reported for.</para>
///
/// <para>⚠️ The cache is per (edition, role) and short, and the page says how old the figures are
/// with a Refresh link — a board that silently shows five-minute-old work reads as broken to whoever
/// just finished a step.</para>
///
/// <para>Auth: organizer-gated, friendly notice rather than a 403 — same as the other organizer
/// pages. Read-only: no writes, no side effects, nothing to roll back.</para>
/// </remarks>
[Authorize]
public class ParticipantStatusModel : PageModel
{
    /// <summary>
    /// How long a built board stays warm. Long enough that paging and filtering are free, short
    /// enough that "I just finished that" is not contradicted for long.
    /// </summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(90);

    /// <summary>Rows per page — enough that the whole board is usually one or two pages.</summary>
    public const int PageSize = 50;

    private readonly ICurrentParticipantAccessor _participant;
    private readonly ParticipantStatusBoardBuilder _board;
    private readonly IMemoryCache _cache;
    private readonly TimeProvider _clock;
    private readonly ILogger<ParticipantStatusModel> _logger;

    public ParticipantStatusModel(
        ICurrentParticipantAccessor participant,
        ParticipantStatusBoardBuilder board,
        IMemoryCache cache,
        TimeProvider clock,
        ILogger<ParticipantStatusModel> logger,
        // §1211 — the coordinator addresses behind the sponsor rows.
        CommunityHub.Core.Data.CommunityHubDbContext db)
    {
        _participant = participant;
        _board = board;
        _cache = cache;
        _clock = clock;
        _logger = logger;
        _db = db;
    }

    private readonly CommunityHub.Core.Data.CommunityHubDbContext _db;

    /// <summary>
    /// §1211 — every address behind the rows the filters are showing, de-duplicated.
    /// </summary>
    public IReadOnlyList<string> ChaseEmails { get; private set; } = Array.Empty<string>();

    /// <summary>Semicolon-separated, for one paste into a BCC field.</summary>
    public string ChaseEmailLine => string.Join("; ", ChaseEmails);

    /// <summary>
    /// §854 — sponsor companies in the current filter with no event coordinator, so a short list is
    /// never a silent one. Nothing is chasing these at all: the reminders have no recipient either.
    /// </summary>
    public IReadOnlyList<string> CompaniesWithNoCoordinator { get; private set; } =
        Array.Empty<string>();

    public bool AccessDenied { get; private set; }

    /// <summary>Set when the build fails — an honest banner instead of a 500.</summary>
    public string? Error { get; private set; }

    // ---- filters (query string, so a filtered board is a shareable link) ----------------------

    [BindProperty(SupportsGet = true, Name = "role")]
    public ParticipantRole? RoleFilter { get; set; }

    [BindProperty(SupportsGet = true, Name = "state")]
    public ParticipantStatusFilter StateFilter { get; set; } = ParticipantStatusFilter.All;

    [BindProperty(SupportsGet = true, Name = "p")]
    public int PageNumber { get; set; } = 1;

    /// <summary>
    /// 🔴 §1212 — show TEST people and TEST companies. Off unless he asks.
    /// </summary>
    /// <remarks>
    /// Operator 2026-09-12: <i>"it shows test users also - i need to filter that out with option to
    /// include"</i>. A test account is not somebody to chase, and it was padding both the counts and
    /// the email export.
    /// </remarks>
    [BindProperty(SupportsGet = true, Name = "test")]
    public bool IncludeTest { get; set; }

    /// <summary>Every row in the ROLE scope (before the completion filter) — the summary counts.</summary>
    public IReadOnlyList<ParticipantStatusRow> AllRows { get; private set; } = Array.Empty<ParticipantStatusRow>();

    /// <summary>The rows on this page, after the completion filter.</summary>
    public IReadOnlyList<ParticipantStatusRow> Rows { get; private set; } = Array.Empty<ParticipantStatusRow>();

    /// <summary>Rows matching the completion filter, across all pages.</summary>
    public int MatchCount { get; private set; }

    public int TotalPages => MatchCount == 0 ? 1 : (int)Math.Ceiling(MatchCount / (double)PageSize);

    /// <summary>When the figures on screen were built (the cache stamp), for the freshness line.</summary>
    public DateTimeOffset BuiltAt { get; private set; }

    /// <summary>True when this request rebuilt the board rather than reading the cache.</summary>
    public bool WasRebuilt { get; private set; }

    // ---- summary (over the role scope, so the numbers do not move when the state filter does) --

    public int CompleteCount => AllRows.Count(r => r.IsComplete);
    public int InProgressCount => AllRows.Count(r => r.InProgress);
    public int NotStartedCount => AllRows.Count(r => r.NotStarted);
    public int OverdueCount => AllRows.Count(r => r.Overdue);

    /// <summary>Average completion across the rows that HAVE a wizard (the rest have no denominator).</summary>
    public int AveragePercent
    {
        get
        {
            var withWizard = AllRows.Where(r => r.HasWizard).ToList();
            return withWizard.Count == 0 ? 0 : (int)Math.Round(withWizard.Average(r => r.Percent));
        }
    }

    /// <summary>The roles the filter offers, in the order the operator thinks about them.</summary>
    public static readonly IReadOnlyList<ParticipantRole> Roles = new[]
    {
        ParticipantRole.Sponsor,
        ParticipantRole.Speaker,
        ParticipantRole.Attendee,
        ParticipantRole.Volunteer,
        ParticipantRole.Organizer,
        ParticipantRole.Media,
        ParticipantRole.EventPartner,
    };

    public async Task<IActionResult> OnGetAsync(bool refresh = false, CancellationToken ct = default)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        try
        {
            var (rows, builtAt, rebuilt) = await LoadAsync(me.EventId, RoleFilter, refresh, IncludeTest, ct);
            AllRows = rows;
            BuiltAt = builtAt;
            WasRebuilt = rebuilt;
        }
        catch (Exception ex)
        {
            Error = "The participant status board could not be loaded right now.";
            _logger.LogWarning(ex, "Participant status board failed for event {EventId}.", me.EventId);
            return Page();
        }

        var matching = AllRows.Where(r => r.Matches(StateFilter)).ToList();
        MatchCount = matching.Count;

        if (PageNumber < 1) PageNumber = 1;
        if (PageNumber > TotalPages) PageNumber = TotalPages;
        Rows = matching.Skip((PageNumber - 1) * PageSize).Take(PageSize).ToList();

        // 🔴 §1211 — the addresses for EVERY matching row, not just this page.
        await LoadChaseEmailsAsync(me.EventId, matching, ct);

        return Page();
    }

    /// <summary>
    /// 🔴 §1211 — WHO TO EMAIL, for exactly the rows the filters are showing.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-09-12: <i>"i need a quick way to export contcts email for event
    /// coordiantors to hunt. this is taking too long"</i> · <i>"why would you fx not have a filter
    /// here where i can export email or similar"</i>.</para>
    ///
    /// <para>🔴 <b>He had to collect the addresses by hand while looking at the board that knows
    /// them.</b> The page filters to "sponsors, in progress" and then names every company — and the
    /// one thing you do next, mail them, was the one thing it would not give you.</para>
    ///
    /// <para>🔑 <b>Scoped to the FILTER, not the page.</b> The list follows role + status across all
    /// pages, so "every sponsor still in progress" is one copy rather than one per 50 rows. Exporting
    /// only the visible page would be a quiet way to miss people.</para>
    ///
    /// <para>🔒 <b>Sponsors export their EVENT COORDINATORS</b> (§7c) — the company answers through
    /// its coordinator, and the row's own <c>Email</c> is one arbitrary contact of several. Every
    /// other role is a person, so it is their own address.</para>
    /// </remarks>
    private async Task LoadChaseEmailsAsync(
        int eventId, IReadOnlyList<ParticipantStatusRow> matching, CancellationToken ct)
    {
        var emails = new List<string>();

        // Non-sponsor rows ARE people: their own address is the answer.
        emails.AddRange(matching
            .Where(r => r.Role != ParticipantRole.Sponsor)
            .Select(r => r.Email)
            .Where(e => !string.IsNullOrWhiteSpace(e))!);

        var companyIds = matching
            .Where(r => r.Role == ParticipantRole.Sponsor && r.SponsorCompanyId != null)
            .Select(r => r.SponsorCompanyId!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (companyIds.Count > 0)
        {
            // ONE query for the whole board (§889), never one per company.
            var coordinators = await _db.Participants.AsNoTracking()
                .Where(p => p.EventId == eventId
                            && p.Role == ParticipantRole.Sponsor
                            && p.IsActive
                            && p.IsEventCoordinator
                            && p.SponsorCompanyId != null
                            && companyIds.Contains(p.SponsorCompanyId))
                .Select(p => new { p.Email, p.SponsorCompanyId })
                .ToListAsync(ct);

            emails.AddRange(coordinators.Select(c => c.Email).Where(e => !string.IsNullOrWhiteSpace(e)));

            // 🛑 §854 — companies with NO coordinator are named, not silently absent from the paste.
            // Nobody is chasing them at all, and a shorter list with no explanation hides that.
            var covered = coordinators
                .Select(c => c.SponsorCompanyId!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            CompaniesWithNoCoordinator = matching
                .Where(r => r.Role == ParticipantRole.Sponsor
                            && r.SponsorCompanyId != null
                            && !covered.Contains(r.SponsorCompanyId))
                .Select(r => r.CompanyName ?? r.Name)
                .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        ChaseEmails = emails
            .Select(e => e!.Trim())
            .Where(e => e.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The board for this (edition, role), from cache unless it is cold or Refresh was pressed.
    /// <para>🔑 Keyed by ROLE because that is the scope that was BUILT — a board built for speakers
    /// cannot answer a question about sponsors, and caching it under one key would let the second
    /// question read the first one's answer.</para>
    /// </summary>
    private async Task<(IReadOnlyList<ParticipantStatusRow> Rows, DateTimeOffset BuiltAt, bool Rebuilt)>
        LoadAsync(int eventId, ParticipantRole? role, bool refresh, bool includeTest, CancellationToken ct)
    {
        // 🔴 §1212 — the test switch is part of the KEY. Without it, ticking the box would read back
        // the board built without test rows, and the page would answer the previous question.
        var key = $"participant-status:{eventId}:{(role is null ? "all" : role.Value.ToString())}"
                + $":{(includeTest ? "withtest" : "notest")}";

        if (!refresh
            && _cache.TryGetValue(key, out (IReadOnlyList<ParticipantStatusRow> Rows, DateTimeOffset At) hit))
        {
            return (hit.Rows, hit.At, false);
        }

        var rows = await _board.BuildAsync(eventId, role, ct, includeTest);
        var now = _clock.GetUtcNow();
        _cache.Set(key, (rows, now), CacheTtl);
        return (rows, now, true);
    }
}
