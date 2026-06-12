namespace CommunityHub.Core.Domain;

/// <summary>
/// One sponsor's participation in the attendee app game (README: App game
/// sponsor participation management). The organizer registers the sponsor +
/// the gift they committed; near the event a reminder mail asks them to
/// bring the gift. Reminder sends are recorded here (and routed through the
/// branded template system).
/// </summary>
public class AppGameParticipation
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>WooCommerce / Company Manager company id.</summary>
    public string SponsorCompanyId { get; set; } = string.Empty;

    /// <summary>Display name used in mails and on the grid.</summary>
    public string CompanyName { get; set; } = string.Empty;

    /// <summary>The gift the sponsor committed (e.g. "Lego set, ~500 DKK").</summary>
    public string GiftDescription { get; set; } = string.Empty;

    /// <summary>Set when the sponsor has confirmed the gift is sorted.</summary>
    public bool GiftConfirmed { get; set; }

    public string? Notes { get; set; }

    public DateTimeOffset? ReminderLastSentAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
