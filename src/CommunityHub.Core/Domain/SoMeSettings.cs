namespace CommunityHub.Core.Domain;

/// <summary>
/// Per-edition organizer settings for the LinkedIn company-page social-media
/// (SoMe) scheduling queue (REQUIREMENTS §19). One row per edition (upserted).
///
/// <b>Secret-clean:</b> this row holds only OPERATOR CONFIG that is NOT a secret —
/// the on/off toggle, the company-page URL / organization id (plain config, like
/// the Sessionize endpoint id), the designated speaker pre-alert organizer, and
/// the notification-array. The LinkedIn OAuth <b>access token is a SECRET</b> and
/// is NEVER stored here — it is read from the existing secret/config mechanism
/// (Key Vault, secret name <c>linkedin-some-access-token</c>) by the live
/// publisher only; placeholders only in committed files.
/// </summary>
public class SoMeSettings
{
    public int Id { get; set; }

    /// <summary>The edition these settings belong to. One row per edition.</summary>
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>
    /// Master enable/disable for SoMe posting (REQUIREMENTS §19). When false the
    /// dispatcher publishes nothing even if the publisher seam is wired. Defaults
    /// false — nothing posts until an organizer turns it on.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The LinkedIn company-page URL OR organization id the queue posts to
    /// (operator config, NOT a secret — like the Sessionize endpoint id). Real
    /// value entered in the UI / private config; committed files keep a
    /// placeholder. The live publisher resolves the organization URN from this.
    /// </summary>
    public string? CompanyPageUrlOrOrgId { get; set; }

    /// <summary>
    /// The designated organizer who receives the T-5-minute speaker pre-alert
    /// (REQUIREMENTS §19) so they can manually insert the speaker's real LinkedIn
    /// handle before a Speaker post publishes (the API can't tag external
    /// speakers). A single email address; blank disables the pre-alert.
    /// </summary>
    public string? SpeakerPreAlertOrganizerEmail { get; set; }

    /// <summary>
    /// Comma/semicolon/newline-separated list of organizer emails who get a
    /// notification when a post publishes (the "SoMe notification email array").
    /// Empty = no publish notifications even when <see cref="NotifyOnPublish"/>
    /// is on.
    /// </summary>
    public string? NotificationEmails { get; set; }

    /// <summary>
    /// Toggle for the publish-notification array (REQUIREMENTS §19). When false,
    /// no "a post was published" email is sent regardless of
    /// <see cref="NotificationEmails"/>. The T-5-minute speaker pre-alert is a
    /// separate, always-on-when-an-organizer-is-set channel.
    /// </summary>
    public bool NotifyOnPublish { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? LastUpdatedByEmail { get; set; }

    /// <summary>The notification emails split into a clean, non-blank list.</summary>
    public IReadOnlyList<string> NotificationEmailList =>
        SplitAddresses(NotificationEmails);

    /// <summary>Split a comma/semicolon/newline-separated address list into clean entries.</summary>
    public static IReadOnlyList<string> SplitAddresses(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? Array.Empty<string>()
            : raw.Split(new[] { ',', ';', '\n', '\r' },
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Distinct(StringComparer.OrdinalIgnoreCase)
                 .ToArray();
}
