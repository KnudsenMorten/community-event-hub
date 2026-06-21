using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// The organizer "assign a ring per resource" surface (REQUIREMENTS §23). Lets an
/// organizer set the release ring of a sponsor COMPANY (the default for its
/// contacts), a sponsor CONTACT, a SPEAKER and a VOLUNTEER. A resource's effective
/// ring then decides — together with each feature's released-to ring — which
/// advanced features that resource sees/has run.
///
/// Organizer-only (server-enforced), mobile-first (~360px), English.
/// </summary>
[Authorize]
public class ResourceRingsModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly ResourceRingService _rings;

    public ResourceRingsModel(
        ICurrentParticipantAccessor participant, ResourceRingService rings)
    {
        _participant = participant;
        _rings = rings;
    }

    public bool AccessDenied { get; private set; }
    public bool Saved { get; private set; }

    public IReadOnlyList<RingResourceRow> SponsorCompanies { get; private set; }
        = Array.Empty<RingResourceRow>();
    public IReadOnlyList<RingResourceRow> SponsorContacts { get; private set; }
        = Array.Empty<RingResourceRow>();
    public IReadOnlyList<RingResourceRow> Speakers { get; private set; }
        = Array.Empty<RingResourceRow>();
    public IReadOnlyList<RingResourceRow> Volunteers { get; private set; }
        = Array.Empty<RingResourceRow>();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>Set a participant's own ring (sponsor contact / speaker / volunteer).</summary>
    public async Task<IActionResult> OnPostParticipantRingAsync(
        int participantId, Ring ring, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        if (participantId > 0)
        {
            await _rings.SetParticipantRingAsync(me.EventId, participantId, ring, ct);
            Saved = true;
        }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>Set a sponsor COMPANY's default ring (fallback for its contacts).</summary>
    public async Task<IActionResult> OnPostCompanyRingAsync(
        string companyId, Ring ring, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        if (!string.IsNullOrWhiteSpace(companyId))
        {
            await _rings.SetSponsorCompanyRingAsync(me.EventId, companyId, ring, me.Email, ct);
            Saved = true;
        }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    private async Task LoadAsync(int eventId, CancellationToken ct)
    {
        SponsorCompanies = await _rings.GetSponsorCompaniesAsync(eventId, ct);
        SponsorContacts = await _rings.GetParticipantsAsync(eventId, RingResourceKind.SponsorContact, ct);
        Speakers = await _rings.GetParticipantsAsync(eventId, RingResourceKind.Speaker, ct);
        Volunteers = await _rings.GetParticipantsAsync(eventId, RingResourceKind.Volunteer, ct);
    }
}
