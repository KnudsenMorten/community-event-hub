namespace CommunityHub.Core.Domain;

/// <summary>
/// State of one speaker's contact-email propagation to Zoho Backstage.
/// </summary>
public enum BackstageEmailSyncState
{
    /// <summary>
    /// The override changed (or was cleared) in the hub and the new effective
    /// address has not yet been pushed to Backstage. The queue/marker carries
    /// the desired address; a future live wiring drains pending rows.
    /// </summary>
    Pending = 0,

    /// <summary>The effective address was successfully written to Backstage.</summary>
    Synced = 1,

    /// <summary>A push was attempted and failed; <see cref="SpeakerBackstageEmailSync.LastError"/> has the reason.</summary>
    Failed = 2,
}

/// <summary>
/// Queue/marker row recording that a speaker's <c>EffectiveEmail</c> needs to be
/// (or has been) propagated to Zoho Backstage, where the speaker is also onboarded.
///
/// WHY a marker instead of a direct call: the repo has a Zoho Backstage write
/// path for <i>exhibitors</i> (<see cref="Integrations.IBackstageExhibitorApi"/>),
/// but Backstage exposes no documented <i>speaker</i> contact-email update
/// endpoint that is wired in this repo. Rather than fake a Zoho call, the hub
/// records the desired address here (one row per speaker per edition) and a
/// clean service seam (<see cref="Integrations.IBackstageSpeakerEmailApi"/>)
/// drains it once a real endpoint is wired. Until then the live wiring is ◻
/// (pending) and rows stay <see cref="BackstageEmailSyncState.Pending"/>.
///
/// The hub's OWN mail + calendar already use the override immediately and do not
/// depend on this propagation completing — this only keeps the external event
/// system in step.
/// </summary>
public class SpeakerBackstageEmailSync
{
    public int Id { get; set; }

    // --- Edition scope ------------------------------------------------------
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    public int ParticipantId { get; set; }
    public Participant Participant { get; set; } = null!;

    /// <summary>
    /// The Sessionize/community identity address — the key Backstage matches a
    /// speaker on (NOT what changes). Lets a future drainer find the speaker in
    /// Backstage by their onboarding identity, then set the desired contact.
    /// </summary>
    public string IdentityEmail { get; set; } = string.Empty;

    /// <summary>
    /// The address Backstage should use for this speaker — the resolved
    /// <c>EffectiveEmail</c> at the time of the change (override, or the
    /// identity address when the override was cleared).
    /// </summary>
    public string DesiredEmail { get; set; } = string.Empty;

    public BackstageEmailSyncState State { get; set; } = BackstageEmailSyncState.Pending;

    /// <summary>When the hub recorded the desired address (the override change).</summary>
    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the desired address was last pushed to Backstage (null until then).</summary>
    public DateTimeOffset? SyncedAt { get; set; }

    /// <summary>Last failure detail, when <see cref="State"/> is Failed.</summary>
    public string? LastError { get; set; }
}
