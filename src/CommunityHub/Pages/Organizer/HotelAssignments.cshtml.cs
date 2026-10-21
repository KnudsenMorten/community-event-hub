using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Entitlements;
using CommunityHub.Core.Organizer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Organizer-gated page to assign each participant to a hotel, set the per-person
/// hotel confirmation number, and view everyone grouped by hotel (Hotel 1 list,
/// Hotel 2 list, … plus a "Not assigned" group) so room blocks can be managed per
/// hotel (REQUIREMENTS §3 multi-hotel management).
/// </summary>
[Authorize]
public class HotelAssignmentsModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly HotelManagementService _hotels;
    private readonly CommunityHubDbContext _db;
    private readonly CommunityHub.Core.Settings.FeatureGateService _gate;
    private readonly CommunityHub.Core.Settings.RingResolver _rings;

    public HotelAssignmentsModel(
        ICurrentParticipantAccessor participant, HotelManagementService hotels, CommunityHubDbContext db,
        CommunityHub.Core.Settings.FeatureGateService gate, CommunityHub.Core.Settings.RingResolver rings,
        CommunityHub.Core.Entitlements.HotelCoverageService coverage)
    {
        _participant = participant;
        _hotels = hotels;
        _db = db;
        _gate = gate;
        _rings = rings;
        _coverage = coverage;
    }

    private readonly CommunityHub.Core.Entitlements.HotelCoverageService _coverage;

    /// <summary>
    /// §1296 — the resolved hotel coverage for every occupant on the page, by participant id.
    /// </summary>
    /// <remarks>
    /// 🔑 Replaces the speaker-only <c>FundedNights</c> this page used to compute inline. That
    /// version returned <c>null</c> for volunteers, organizers, media and event partners — so the
    /// one page an organizer plans hotels on could not answer the question for four of the seven
    /// roles staying in the rooms.
    /// </remarks>
    public IReadOnlyDictionary<int, CommunityHub.Core.Entitlements.ParticipantHotelCoverage> CoverageByPid
    { get; private set; } = new Dictionary<int, CommunityHub.Core.Entitlements.ParticipantHotelCoverage>();

    /// <summary>The coverage for one occupant, or null when it does not apply to them.</summary>
    public CommunityHub.Core.Entitlements.ParticipantHotelCoverage? CoverageFor(HotelOccupant o) =>
        CoverageByPid.TryGetValue(o.ParticipantId, out var c) && c.Class is not null ? c : null;

    /// <summary>
    /// §1296 — true when this VOLUNTEER is still running on the class standard with nothing added.
    /// Volunteers are individual by nature (operator: <i>"some we cover 1 and some we cover 2 and
    /// some we cover more"</i>), so "nobody has set this one yet" is a state worth seeing BEFORE
    /// they book and are told they owe a night that was always going to be covered.
    /// </summary>
    public bool IsVolunteerOnStandard(HotelOccupant o) =>
        o.Role == ParticipantRole.Volunteer
        && CoverageByPid.TryGetValue(o.ParticipantId, out var c)
        && c.OnStandard;

    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }

    public IReadOnlyList<Hotel> AllHotels { get; private set; } = Array.Empty<Hotel>();
    public IReadOnlyList<HotelGroup> Groups { get; private set; } = Array.Empty<HotelGroup>();

    [BindProperty] public int ParticipantId { get; set; }
    [BindProperty] public int? HotelId { get; set; }
    [BindProperty] public string? ConfirmationNumber { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!me.Role.HasOrganizerAccess()) { AccessDenied = true; return Page(); }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    [CommunityHub.Audit.Audit("Assigned a hotel to a participant", TargetType = "Hotel")]
    public async Task<IActionResult> OnPostAssignAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        // 🗑 §705.14b — the ring check here is REMOVED. Operator 2026-07-29, agreeing explicitly:
        // *"agree - i decide - you decide who gets a hotel, not a ring."*
        //
        // It used to refuse the assignment when the participant sat above the hotel-assignment
        // feature's released ring ("That participant is above the … released ring (out of scope)").
        // That is a RING deciding what an ORGANIZER may do, which is the same category error §569
        // removed from the Zoho speaker/session push: an organizer's authority is their ROLE.
        //
        // 🔒 §705's rule: RINGS EXIST ONLY FOR EMAILS. `hotel-assignment` sends no mail at all — it
        // gates one Logistics tile — so it has no business narrowing an organizer's own action. The
        // guest mail is a SEPARATE key ("hotel-invite" / "Hotel confirmation email") and keeps its
        // own ring, so nothing about who RECEIVES mail changes here.
        var ok = await _hotels.AssignParticipantAsync(me.EventId, ParticipantId, HotelId, ct);
        Message = ok ? "Hotel assignment saved." : "Participant or hotel not found.";
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostConfirmAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var ok = await _hotels.SetConfirmationNumberAsync(me.EventId, ParticipantId, ConfirmationNumber, ct);
        Message = ok ? "Confirmation number saved." : "Participant not found.";
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    private async Task LoadAsync(int eventId, CancellationToken ct)
    {
        AllHotels = await _hotels.ListHotelsAsync(eventId, ct);
        Groups = await _hotels.GroupByHotelAsync(eventId, ct);

        // §1296 — coverage for EVERY occupant, not just speakers, resolved in one pass.
        var ids = Groups.SelectMany(g => g.Occupants).Select(o => o.ParticipantId).Distinct().ToList();
        CoverageByPid = await _coverage.ResolveManyAsync(eventId, ids, ct);
    }
}
