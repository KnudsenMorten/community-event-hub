using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Entitlements;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer.Settings;

/// <summary>
/// §1296 — "included nights per rule": the per-edition hotel-coverage standards (operator
/// 2026-09-22: <i>"we need somewhere in the settings menu where i can define the included nights
/// per rule"</i>).
/// </summary>
/// <remarks>
/// <para>One row per <see cref="HotelCoverageClass"/>, each set to a night COUNT, the WHOLE STAY, or
/// NOTHING. A class with no saved row shows — and uses — the catalog default, and the page says
/// which is which: "default" and "set by you" are different facts, and a page that blurs them
/// invites somebody to trust a number nobody chose.</para>
///
/// <para>What ONE named person gets beyond their class is deliberately NOT here — it is the single
/// extra-nights field on the person's own page under People. Two controls for the same person's
/// nights is what the operator cut the absolute override to avoid.</para>
///
/// <para>Organizer-only (server-enforced for writes via <see cref="OrganizerAuth.IsRealOrganizer"/>),
/// mobile-first (~360px).</para>
/// </remarks>
[Authorize]
public class HotelNightsModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly HotelCoverageService _coverage;
    private readonly CommunityHub.Core.Data.CommunityHubDbContext _db;

    public HotelNightsModel(
        ICurrentParticipantAccessor participant,
        HotelCoverageService coverage,
        CommunityHub.Core.Data.CommunityHubDbContext db)
    {
        _participant = participant;
        _coverage = coverage;
        _db = db;
    }

    /// <summary>One row of the standards table, as the page renders it.</summary>
    /// <param name="IsCustomized">False ⇒ this is the shipped default, not a choice anybody made.</param>
    /// <param name="PeopleInClass">
    /// How many ACTIVE people currently fall in this class — so a change is never made blind. It is
    /// the difference between "2 nights" and "2 nights × 34 speakers".
    /// </param>
    public sealed record ClassRow(
        HotelCoverageClass Class,
        string Label,
        HotelCoverageRule Rule,
        bool IsCustomized,
        int PeopleInClass)
    {
        public string Summary => HotelCoverage.Describe(Rule);
    }

    public IReadOnlyList<ClassRow> Rows { get; private set; } = Array.Empty<ClassRow>();
    public bool AccessDenied { get; private set; }
    [TempData] public string? Message { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!me.Role.HasOrganizerAccess()) { AccessDenied = true; return Page(); }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    [CommunityHub.Audit.Audit("Changed a hotel coverage standard", TargetType = "HotelCoverageStandard")]
    public async Task<IActionResult> OnPostSetAsync(
        HotelCoverageClass cls, HotelCoverageMode mode, int nights, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        await _coverage.SetStandardAsync(me.EventId, cls, mode, nights, me.Email, ct);
        Message = $"{HotelCoverage.Label(cls)}: {HotelCoverage.Describe(
            mode == HotelCoverageMode.Nights ? HotelCoverageRule.OfNights(nights)
            : mode == HotelCoverageMode.WholeStay ? HotelCoverageRule.WholeStay
            : HotelCoverageRule.Nothing)}.";
        return RedirectToPage();
    }

    private async Task LoadAsync(int eventId, CancellationToken ct)
    {
        var standards = await _coverage.GetStandardsAsync(eventId, ct);
        var customized = await _coverage.GetCustomizedClassesAsync(eventId, ct);
        var counts = await CountByClassAsync(eventId, ct);

        Rows = HotelCoverage.All
            .Select(c => new ClassRow(
                c,
                HotelCoverage.Label(c),
                standards[c],
                customized.Contains(c),
                counts.TryGetValue(c, out var n) ? n : 0))
            .ToList();
    }

    /// <summary>
    /// How many active people sit in each class right now. Speakers are classified by their DERIVED
    /// presenting days, exactly as the resolver does it — so the count and the coverage can never
    /// disagree about who is a master-class speaker.
    /// </summary>
    private async Task<Dictionary<HotelCoverageClass, int>> CountByClassAsync(
        int eventId, CancellationToken ct)
    {
        var people = await _db.Participants
            .AsNoTracking()
            .Where(p => p.EventId == eventId && p.IsActive)
            .Select(p => new { p.Id, p.Role })
            .ToListAsync(ct);

        var categories = await _db.SpeakerProfiles
            .AsNoTracking()
            .Where(s => s.EventId == eventId)
            .Select(s => new { s.ParticipantId, s.Category })
            .ToDictionaryAsync(s => s.ParticipantId, s => s.Category, ct);

        var days = await SpeakerDayScope.DaysBySpeakerAsync(_db, eventId, ct);

        var result = new Dictionary<HotelCoverageClass, int>();
        foreach (var p in people)
        {
            var cls = HotelCoverage.ClassFor(
                p.Role,
                categories.TryGetValue(p.Id, out var c) ? c : null,
                days.TryGetValue(p.Id, out var d) ? d : SpeakerDays.None);
            if (cls is not HotelCoverageClass k) continue;
            result[k] = result.TryGetValue(k, out var n) ? n + 1 : 1;
        }
        return result;
    }
}
