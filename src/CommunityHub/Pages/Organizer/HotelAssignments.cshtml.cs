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
        CommunityHub.Core.Settings.FeatureGateService gate, CommunityHub.Core.Settings.RingResolver rings)
    {
        _participant = participant;
        _hotels = hotels;
        _db = db;
        _gate = gate;
        _rings = rings;
    }

    /// <summary>§299 6.3 — each speaker profile's (category, organizer-entered guest nights), by participant id.</summary>
    public IReadOnlyDictionary<int, (SpeakerCategory? Category, int? GuestFundedNights)> SpeakerCategoryByPid { get; private set; }
        = new Dictionary<int, (SpeakerCategory?, int?)>();

    /// <summary>§299 C5 — derived presenting days per speaker (from linked sessions).</summary>
    public IReadOnlyDictionary<int, SpeakerDays> DaysBySpeaker { get; private set; }
        = new Dictionary<int, SpeakerDays>();

    /// <summary>
    /// §299 6.3 — ELDK-funded hotel nights for a SPEAKER occupant: Community = one
    /// night per DISTINCT presenting day (derived from linked sessions); Guest =
    /// the organizer-entered nights (individual agreement); Sponsor-category or
    /// uncategorized = 0. Null for non-speakers (the day-linked rule is
    /// speaker-specific).
    /// </summary>
    public int? FundedNights(HotelOccupant o)
    {
        if (o.Role != ParticipantRole.Speaker) return null;
        // A miss leaves the default (null, null) tuple = uncategorized ⇒ 0 nights.
        SpeakerCategoryByPid.TryGetValue(o.ParticipantId, out var v);
        var days = DaysBySpeaker.TryGetValue(o.ParticipantId, out var d) ? d : SpeakerDays.None;
        return SpeakerDayScope.FundedHotelNights(v.Category, days, v.GuestFundedNights);
    }

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
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

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
        SpeakerCategoryByPid = (await _db.SpeakerProfiles
                .Where(s => s.EventId == eventId)
                .Select(s => new { s.ParticipantId, s.Category, s.GuestFundedNights })
                .ToListAsync(ct))
            .ToDictionary(s => s.ParticipantId, s => (s.Category, s.GuestFundedNights));
        DaysBySpeaker = await SpeakerDayScope.DaysBySpeakerAsync(_db, eventId, ct);
    }
}
