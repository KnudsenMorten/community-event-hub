using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CommunityHub.Core.Integrations;

/// <summary>Outcome of one speaker's bio-sync attempt.</summary>
public enum SpeakerBioSyncOutcome
{
    /// <summary>The sync is disabled (Enabled=false) — nothing was done.</summary>
    Disabled = 0,

    /// <summary>The request was built but no live writer is wired (CanWrite=false) — no Zoho call made.</summary>
    BuiltOnly = 1,

    /// <summary>Pushed to Backstage in DRAFT/hidden state (speaker NOT public).</summary>
    PushedDraft = 2,

    /// <summary>Pushed to Backstage in PUBLIC state (speaker explicitly approved).</summary>
    PushedPublic = 3,

    /// <summary>A push was attempted and failed.</summary>
    Failed = 4,
}

/// <summary>Per-speaker result, exposing the gated request that was (or would be) sent.</summary>
public sealed record SpeakerBioSyncResult(
    int ParticipantId,
    SpeakerBioSyncOutcome Outcome,
    SpeakerBioRecord Request,
    string? Error = null);

/// <summary>
/// OUTBOUND speaker-bio sync: mirrors each hub speaker's bio profile to a Zoho
/// Backstage speaker record. This is the .NET replacement for the prior-year
/// PowerShell job (<c>Sync-Sessionize-Speakers-to-Zoho-Backstage.ps1</c>).
///
/// TWO INVARIANTS, enforced here regardless of the live writer:
///  1. HARD GATE — never publish an unselected speaker. A speaker's request
///     carries <see cref="SpeakerPublishState.Public"/> ONLY when
///     <c>SpeakerProfile.SelectedForPublish</c> is explicitly true. Everyone else
///     (the default today) is built as <see cref="SpeakerPublishState.Draft"/>.
///  2. INACTIVE BY DEFAULT — when <c>Backstage:SpeakerBioSync:Enabled</c> is
///     false (the default) the service does nothing (<see cref="SpeakerBioSyncOutcome.Disabled"/>);
///     a manual opt-in trigger is the only way to run it.
///
/// When no live writer is wired (<see cref="IBackstageSpeakerBioApi.CanWrite"/> =
/// false, the default Null) the gated request is still BUILT (so a dry-run /
/// 1-speaker test can assert it carries the not-public/draft state) but NO Zoho
/// call is made and nothing is faked.
/// </summary>
public sealed class SpeakerBioBackstageSyncService
{
    private readonly CommunityHubDbContext _db;
    private readonly IBackstageSpeakerBioApi _backstage;
    private readonly BackstageSpeakerBioSyncOptions _options;

    public SpeakerBioBackstageSyncService(
        CommunityHubDbContext db,
        IBackstageSpeakerBioApi backstage,
        IOptions<BackstageSpeakerBioSyncOptions> options)
    {
        _db = db;
        _backstage = backstage;
        _options = options.Value;
    }

    /// <summary>Whether the sync is allowed to run (the inactive-by-default switch).</summary>
    public bool IsEnabled => _options.Enabled;

    /// <summary>
    /// Build the gated Backstage request for one speaker from their hub profile,
    /// applying the HARD GATE: <see cref="SpeakerPublishState.Public"/> only when
    /// the profile is explicitly <c>SelectedForPublish</c>; otherwise
    /// <see cref="SpeakerPublishState.Draft"/>. Pure — no DB, no I/O — so a test
    /// can assert the state without any Zoho call.
    /// </summary>
    public static SpeakerBioRecord BuildRequest(string identityEmail, SpeakerProfile profile)
    {
        var state = profile.SelectedForPublish
            ? SpeakerPublishState.Public
            : SpeakerPublishState.Draft;

        return new SpeakerBioRecord(
            IdentityEmail: identityEmail,
            Tagline: profile.Tagline,
            Biography: profile.Biography,
            Blog: profile.Blog,
            LinkedIn: profile.LinkedIn,
            Twitter: profile.Twitter,
            PublishState: state);
    }

    /// <summary>
    /// Sync ONE speaker (by participant id). Honours the enabled flag and the
    /// publish gate. Returns the result incl. the exact request built (for tests /
    /// dry-run). When <paramref name="dryRun"/> is true, the request is built and
    /// the gate asserted but no live call is made even if a writer is wired.
    /// </summary>
    public async Task<SpeakerBioSyncResult> SyncOneAsync(
        int eventId, int participantId, bool dryRun = false, CancellationToken ct = default)
    {
        var profile = await _db.SpeakerProfiles
            .FirstOrDefaultAsync(p => p.EventId == eventId && p.ParticipantId == participantId, ct)
            ?? throw new InvalidOperationException(
                $"No speaker profile for participant {participantId} in event {eventId}.");

        var participant = await _db.Participants
            .FirstOrDefaultAsync(p => p.Id == participantId, ct)
            ?? throw new InvalidOperationException($"No participant {participantId}.");

        var request = BuildRequest(participant.Email, profile);

        // Inactive-by-default: do nothing unless an operator opted in.
        if (!_options.Enabled)
            return new SpeakerBioSyncResult(participantId, SpeakerBioSyncOutcome.Disabled, request);

        // Dry-run, or no live writer wired (◻): build only, never fake a call.
        if (dryRun || !_backstage.CanWrite)
            return new SpeakerBioSyncResult(participantId, SpeakerBioSyncOutcome.BuiltOnly, request);

        try
        {
            await _backstage.UpsertSpeakerBioAsync(request, ct);
            var outcome = request.PublishState == SpeakerPublishState.Public
                ? SpeakerBioSyncOutcome.PushedPublic
                : SpeakerBioSyncOutcome.PushedDraft;
            return new SpeakerBioSyncResult(participantId, outcome, request);
        }
        catch (Exception ex)
        {
            return new SpeakerBioSyncResult(participantId, SpeakerBioSyncOutcome.Failed, request, ex.Message);
        }
    }

    /// <summary>
    /// Sync every speaker in an edition (manual opt-in trigger). Each speaker is
    /// gated independently; unselected speakers go out as Draft, never public.
    /// </summary>
    public async Task<IReadOnlyList<SpeakerBioSyncResult>> SyncAllAsync(
        int eventId, bool dryRun = false, CancellationToken ct = default)
    {
        var ids = await _db.SpeakerProfiles
            .Where(p => p.EventId == eventId)
            .Select(p => p.ParticipantId)
            .ToListAsync(ct);

        var results = new List<SpeakerBioSyncResult>(ids.Count);
        foreach (var id in ids)
            results.Add(await SyncOneAsync(eventId, id, dryRun, ct));
        return results;
    }
}
