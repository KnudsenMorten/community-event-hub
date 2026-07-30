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

    // ---- §706 ATTENDEES -------------------------------------------------------------------------
    // His 2026-08-11 plan: synced attendees default Broad, he assigns a FEW to Ring 2 when ticket
    // sales open, tests attendee mail on them, then raises the mails to Broad once proven.
    //
    // 🔒 SEARCH-FIRST, not a full list. Attendees head for ~1500 rows, and rendering them all would
    // make the page unusable exactly when he needs it. Every other section on this page is small
    // enough to list whole; this one is not, so it behaves differently on purpose.

    /// <summary>§706 — attendees matching <see cref="AttendeeSearch"/> (capped at <see cref="AttendeePageSize"/>).</summary>
    public IReadOnlyList<RingResourceRow> Attendees { get; private set; }
        = Array.Empty<RingResourceRow>();

    /// <summary>§706 — how many attendees MATCH in total, so a capped list can admit it.</summary>
    public int AttendeeMatchCount { get; private set; }

    /// <summary>§706 — total attendees in the edition, shown so an empty search still says how many exist.</summary>
    public int AttendeeTotalCount { get; private set; }

    /// <summary>§706 — the name/email filter. Survives a ring POST so his place in the list is kept.</summary>
    [BindProperty(SupportsGet = true)]
    public string? AttendeeSearch { get; set; }

    /// <summary>Most rows he can usefully scan at once; the count line says when there are more.</summary>
    public const int AttendeePageSize = 50;

    /// <summary>True when the list is truncated — the view says so rather than looking complete.</summary>
    public bool AttendeesTruncated => AttendeeMatchCount > Attendees.Count;

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

        // §706 — attendees are SEARCH-first and capped (see AttendeePageSize). Both counts are read so
        // the view can distinguish "no attendees yet" from "none match that search", which are very
        // different answers on 2026-08-11 when he is hunting for a specific test recipient.
        Attendees = await _rings.GetParticipantsAsync(
            eventId, RingResourceKind.Attendee, ct, AttendeeSearch, AttendeePageSize);
        AttendeeMatchCount = await _rings.CountParticipantsAsync(
            eventId, RingResourceKind.Attendee, AttendeeSearch, ct);
        AttendeeTotalCount = string.IsNullOrWhiteSpace(AttendeeSearch)
            ? AttendeeMatchCount
            : await _rings.CountParticipantsAsync(eventId, RingResourceKind.Attendee, null, ct);
    }
}
