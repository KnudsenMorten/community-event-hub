namespace CommunityHub.Core.Integrations;

/// <summary>
/// One speaker's desired contact address in Zoho Backstage.
/// </summary>
/// <param name="IdentityEmail">
/// The Sessionize/community address Backstage matches the speaker on (the
/// onboarding identity — never changes).
/// </param>
/// <param name="DesiredEmail">
/// The address Backstage should use for this speaker — the hub's resolved
/// <c>EffectiveEmail</c> (override, or the identity address when cleared).
/// </param>
public sealed record SpeakerEmailRecord(string IdentityEmail, string DesiredEmail);

/// <summary>
/// The Zoho Backstage <i>speaker</i> contact-email write seam.
///
/// HONEST STATUS (◻ not yet wired): Backstage exposes no documented speaker
/// contact-email update endpoint that is implemented in this repo (the existing
/// <see cref="IBackstageExhibitorApi"/> covers <i>exhibitors</i> only). So the
/// default registration is <see cref="NullBackstageSpeakerEmailApi"/>, which
/// reports <see cref="CanWrite"/> = false and performs NO Zoho call. The hub
/// records the desired address in the <c>SpeakerBackstageEmailSync</c> queue and
/// leaves the row Pending. When a real endpoint is wired, swap in a live
/// implementation whose <see cref="CanWrite"/> is true and the same queue is
/// drained — no caller changes.
///
/// This mirrors the established pattern in the repo: a clean interface with a
/// no-op default, never a faked external call.
/// </summary>
public interface IBackstageSpeakerEmailApi
{
    /// <summary>
    /// Whether this implementation can actually perform a real Backstage write.
    /// False for the null default (no wired endpoint) — callers then only queue
    /// the desired address as Pending.
    /// </summary>
    bool CanWrite { get; }

    /// <summary>
    /// Push one speaker's desired contact address to Backstage. Only called when
    /// <see cref="CanWrite"/> is true.
    /// </summary>
    Task SetSpeakerEmailAsync(SpeakerEmailRecord record, CancellationToken ct);
}

/// <summary>
/// Default no-op implementation: there is no wired Backstage speaker
/// contact-email endpoint, so this performs no call and cannot write. The hub
/// still records the desired address in the propagation queue (marker), so the
/// intent is durable and a future live wiring can drain it.
/// </summary>
public sealed class NullBackstageSpeakerEmailApi : IBackstageSpeakerEmailApi
{
    public bool CanWrite => false;

    public Task SetSpeakerEmailAsync(SpeakerEmailRecord record, CancellationToken ct) =>
        throw new InvalidOperationException(
            "No wired Zoho Backstage speaker contact-email endpoint. The desired "
            + "address is queued (SpeakerBackstageEmailSync) for a future live "
            + "wiring; do not call SetSpeakerEmailAsync when CanWrite is false.");
}
