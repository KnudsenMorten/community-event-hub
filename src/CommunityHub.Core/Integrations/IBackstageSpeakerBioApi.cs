namespace CommunityHub.Core.Integrations;

/// <summary>
/// The publish/visibility state a speaker's bio is pushed to in Zoho Backstage.
///
/// THE HARD GATE: a speaker is only ever <see cref="Public"/> when the hub's
/// per-speaker <c>SpeakerProfile.SelectedForPublish</c> flag is explicitly true.
/// The lineup is not selected yet, so every speaker is <see cref="Draft"/> today
/// — the sync must NEVER expose an unselected speaker publicly.
/// </summary>
public enum SpeakerPublishState
{
    /// <summary>
    /// Draft / hidden — NOT visible on the public Backstage site. The default
    /// for every speaker whose <c>SelectedForPublish</c> is false (i.e. everyone,
    /// until the lineup is selected).
    /// </summary>
    Draft = 0,

    /// <summary>
    /// Public / visible on the Backstage site. ONLY allowed when the hub
    /// explicitly approved the speaker (<c>SelectedForPublish == true</c>).
    /// </summary>
    Public = 1,
}

/// <summary>
/// One speaker's bio profile to upsert into Zoho Backstage, plus the gated
/// visibility state the upsert must carry.
/// </summary>
/// <param name="IdentityEmail">
/// The Sessionize/community address Backstage matches the speaker on (the
/// onboarding identity — the idempotency key, never changes).
/// </param>
/// <param name="Tagline">Backstage <c>designation</c> (Sessionize tagline/headline).</param>
/// <param name="Biography">Backstage <c>description</c> (the speaker bio).</param>
/// <param name="Blog">Blog / website URL (may be null/blank).</param>
/// <param name="LinkedIn">LinkedIn URL/handle (may be null/blank).</param>
/// <param name="Twitter">X/Twitter handle (may be null/blank).</param>
/// <param name="PublishState">
/// THE GATE: <see cref="SpeakerPublishState.Draft"/> unless the hub explicitly
/// approved the speaker for publish. A live writer MUST honour this — never make
/// a speaker public when this is Draft.
/// </param>
public sealed record SpeakerBioRecord(
    string IdentityEmail,
    string? Tagline,
    string? Biography,
    string? Blog,
    string? LinkedIn,
    string? Twitter,
    SpeakerPublishState PublishState,
    string? FirstName = null,
    string? LastName = null,
    string? Country = null,
    string? Skills = null,
    string? BackstageSpeakerId = null);

/// <summary>What the Backstage write actually did (the v3 speakers API is create-only).</summary>
public enum BackstageSpeakerAction
{
    /// <summary>A new speaker was created (POST).</summary>
    Created = 0,
    /// <summary>
    /// The speaker already exists in Backstage. The v3 API has NO update endpoint
    /// (verified 2026-06-25: per-id POST/PUT/PATCH all 404), so we did NOT create a
    /// duplicate — the caller must alert a human to update them in the Backstage UI.
    /// </summary>
    ExistsBlocked = 1,
    /// <summary>The create attempt failed.</summary>
    Failed = 2,
}

/// <summary>Result of an upsert: what happened + the speaker id (when known).</summary>
public sealed record BackstageSpeakerUpsertResult(
    BackstageSpeakerAction Action, string? SpeakerId = null, string? Error = null);

/// <summary>
/// The Zoho Backstage <i>speaker bio</i> OUTBOUND write seam (hub → Backstage).
///
/// Mirrors the prior-year PowerShell job
/// (<c>tools/legacy-automation/scripts/Sync-Sessionize-Speakers-to-Zoho-Backstage.ps1</c>)
/// as a clean .NET slice, and follows the established repo pattern: a clean
/// interface with a no-op <see cref="NullBackstageSpeakerBioApi"/> default —
/// never a faked external call.
///
/// HONEST STATUS (◻ live wiring pending): the real Backstage portal/event ids,
/// endpoint codes and OAuth creds are operator config (gitignored / Key Vault)
/// that are not in this repo, so the default registration is the Null writer
/// (<see cref="CanWrite"/> = false) which performs NO Zoho call. The whole sync
/// is also INACTIVE by default (config flag <c>Backstage:SpeakerBioSync:Enabled</c>
/// defaults false; manual opt-in trigger only). When a live endpoint + creds are
/// wired AND the lineup is selected, swap in a live implementation whose
/// <see cref="CanWrite"/> is true — no caller changes.
/// </summary>
public interface IBackstageSpeakerBioApi
{
    /// <summary>
    /// Whether this implementation can actually perform a real Backstage write.
    /// False for the null default (no wired endpoint) — callers then build the
    /// request (in draft/dry-run) but make no Zoho call.
    /// </summary>
    bool CanWrite { get; }

    /// <summary>
    /// Upsert one speaker's bio into Backstage, carrying
    /// <see cref="SpeakerBioRecord.PublishState"/>. A live implementation MUST
    /// honour the gate: never publish/expose a speaker whose state is
    /// <see cref="SpeakerPublishState.Draft"/>. Only called when
    /// <see cref="CanWrite"/> is true.
    /// </summary>
    Task<BackstageSpeakerUpsertResult> UpsertSpeakerBioAsync(SpeakerBioRecord record, CancellationToken ct);
}

/// <summary>
/// Default no-op implementation: there is no wired Backstage speaker-bio endpoint
/// (creds/endpoints are operator config not in this repo), so this performs no
/// call and cannot write. The hub still computes the gated request; the live
/// wiring is ◻ (pending) and no Zoho call is ever faked.
/// </summary>
public sealed class NullBackstageSpeakerBioApi : IBackstageSpeakerBioApi
{
    public bool CanWrite => false;

    public Task<BackstageSpeakerUpsertResult> UpsertSpeakerBioAsync(SpeakerBioRecord record, CancellationToken ct) =>
        throw new InvalidOperationException(
            "No wired Zoho Backstage speaker endpoint and the sync is inactive by "
            + "default. Do not call UpsertSpeakerBioAsync when CanWrite is false.");
}
