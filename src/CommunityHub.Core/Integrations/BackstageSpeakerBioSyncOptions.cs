namespace CommunityHub.Core.Integrations;

/// <summary>
/// Operator config for the OUTBOUND speaker-bio sync to Zoho Backstage.
///
/// Bound from <c>Backstage:SpeakerBioSync</c>. Defaults keep the integration
/// INACTIVE: <see cref="Enabled"/> is false, so nothing runs unless an operator
/// opts in (organizer action / CLI). The real portal/event ids + OAuth creds are
/// operator config (gitignored <c>config/</c> or Key Vault); committed/public
/// files carry placeholders only.
/// </summary>
public sealed class BackstageSpeakerBioSyncOptions
{
    public const string SectionName = "Backstage:SpeakerBioSync";

    /// <summary>
    /// Master on/off for the whole sync. DEFAULTS FALSE — no automatic or
    /// scheduled run; a manual opt-in trigger is the only way to invoke it.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Backstage portal id (operator config; placeholder in public files).</summary>
    public string? PortalId { get; set; }

    /// <summary>Backstage event id (operator config; placeholder in public files).</summary>
    public string? EventId { get; set; }
}
