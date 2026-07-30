namespace CommunityHub.Core.Domain;

/// <summary>
/// §292 — a SPONSOR-purchased speaking session (the "Sponsor Sessions" webshop product category).
/// Unlike normal sessions this does NOT come from Sessionize — the sponsor enters the title +
/// abstract + one or more speakers in their Get-Started wizard, and it is STORED IN CEH for later
/// one-way sync to the Zoho Backstage agenda (CEH→Zoho). One session per sponsor company per edition.
/// </summary>
public class SponsorSession
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>The sponsor company that owns this session (Company Manager id, as stored on
    /// <see cref="Participant.SponsorCompanyId"/> / <see cref="SponsorInfo.SponsorCompanyId"/>).</summary>
    public string SponsorCompanyId { get; set; } = string.Empty;

    public string? Title { get; set; }
    public string? Abstract { get; set; }

    /// <summary>
    /// §356 — the agenda TRACK this session belongs to (operator 2026-07-26: <i>"a dropdown with all
    /// available tracks"</i>). Stored as the track's display NAME, not a Zoho track id: the id is
    /// per-edition and per-Zoho-event, so persisting it would go stale the moment the edition rolls
    /// — whereas the NAME is exactly what `SessionBackstagePushService.ResolveTrackId` already maps
    /// to an id at push time, which is the same route a Sessionize-imported session's track takes.
    /// Nullable: the track list comes from Zoho, so it can be unavailable when the sponsor fills the
    /// form, and a blocked form is worse than a session with no track yet.
    /// </summary>
    public string? Track { get; set; }

    // --- Zoho Backstage agenda sync (CEH→Zoho, one-way) --------------------
    /// <summary>When this session was last pushed to the Zoho Backstage agenda, or null = never.</summary>
    public DateTimeOffset? SyncedToZohoAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? LastUpdatedByEmail { get; set; }

    /// <summary>The session's speakers — each is a real Speaker participant (SponsorSelfFunded) so
    /// they get CEH access, welcome mail, dinner sign-up and the normal speaker details/photo.</summary>
    public ICollection<SponsorSessionSpeaker> Speakers { get; set; } = new List<SponsorSessionSpeaker>();
}

/// <summary>
/// §292 — one speaker on a <see cref="SponsorSession"/>. Carries the entered name + email AND a link
/// to the real Speaker <see cref="Participant"/> created for them (SponsorSelfFunded), so the sponsor
/// speaker behaves exactly like a normal speaker in CEH.
/// </summary>
public class SponsorSessionSpeaker
{
    public int Id { get; set; }

    public int SponsorSessionId { get; set; }
    public SponsorSession SponsorSession { get; set; } = null!;

    /// <summary>The Speaker participant created/linked for this sponsor speaker (null only transiently).</summary>
    public int? ParticipantId { get; set; }
    public Participant? Participant { get; set; }

    /// <summary>The name the sponsor entered (kept even if the participant name later differs).</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>The email the sponsor entered (lower-cased) — the participant match/create key.</summary>
    public string Email { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
