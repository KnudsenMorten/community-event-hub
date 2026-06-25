using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Settings;
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
    /// <summary>Created in Backstage in DRAFT/non-featured state (speaker NOT public).</summary>
    PushedDraft = 2,
    /// <summary>Created in Backstage as FEATURED/public (speaker explicitly approved).</summary>
    PushedPublic = 3,
    /// <summary>A push was attempted and failed.</summary>
    Failed = 4,
    /// <summary>Skipped: the speaker's release ring isn't active for the speaker-sync feature.</summary>
    RingGated = 5,
    /// <summary>
    /// The speaker already exists in Backstage. The v3 API has no update endpoint, so we
    /// did NOT create a duplicate — info@ was emailed to update them manually instead.
    /// </summary>
    BlockedNeedsManualUpdate = 6,
}

/// <summary>Per-speaker result, exposing the gated request that was (or would be) sent.</summary>
public sealed record SpeakerBioSyncResult(
    int ParticipantId,
    SpeakerBioSyncOutcome Outcome,
    SpeakerBioRecord Request,
    string? Error = null);

/// <summary>
/// OUTBOUND speaker sync: mirrors each hub speaker to a Zoho Backstage speaker record.
/// The Backstage v3 speakers API is CREATE-ONLY (no update endpoint, verified
/// 2026-06-25), so:
///   • a speaker not yet in Backstage is CREATED (featured = the publish gate);
///   • a speaker already there is NOT duplicated — info@expertslive.dk is emailed to
///     update them by hand in the Backstage UI.
/// Invariants: HARD publish gate (featured only when SelectedForPublish), INACTIVE by
/// default (Backstage:SpeakerBioSync:Enabled=false), and RING-GATED per speaker so a
/// locked (default-ring) speaker is never synced until promoted.
/// </summary>
public sealed class SpeakerBioBackstageSyncService
{
    /// <summary>Where "this speaker needs a manual Backstage update" alerts go.</summary>
    public const string AlertEmail = "info@expertslive.dk";
    private const string FeatureKey = "backstage-speaker-sync";

    private readonly CommunityHubDbContext _db;
    private readonly IBackstageSpeakerBioApi _backstage;
    private readonly BackstageSpeakerBioSyncOptions _options;
    private readonly IEmailSender _email;
    private readonly FeatureGateService _gate;
    private readonly RingResolver _rings;

    public SpeakerBioBackstageSyncService(
        CommunityHubDbContext db,
        IBackstageSpeakerBioApi backstage,
        IOptions<BackstageSpeakerBioSyncOptions> options,
        IEmailSender email,
        FeatureGateService gate,
        RingResolver rings)
    {
        _db = db;
        _backstage = backstage;
        _options = options.Value;
        _email = email;
        _gate = gate;
        _rings = rings;
    }

    public bool IsEnabled => _options.Enabled;

    /// <summary>
    /// Build the gated Backstage request from a hub profile. HARD GATE: Public only when
    /// SelectedForPublish. Skills = the speaker's Accreditation (the multi-select that maps
    /// to Zoho speaker Skills). Pure — no I/O.
    /// </summary>
    public static SpeakerBioRecord BuildRequest(string identityEmail, SpeakerProfile profile)
    {
        var state = profile.SelectedForPublish ? SpeakerPublishState.Public : SpeakerPublishState.Draft;
        return new SpeakerBioRecord(
            IdentityEmail: identityEmail,
            Tagline: profile.Tagline,
            Biography: profile.Biography,
            Blog: profile.Blog,
            LinkedIn: profile.LinkedIn,
            Twitter: profile.Twitter,
            PublishState: state,
            FirstName: profile.FirstName,
            LastName: profile.LastName,
            Country: profile.Country,
            Skills: profile.Accreditation,
            BackstageSpeakerId: profile.BackstageSpeakerId);
    }

    /// <summary>
    /// Sync ONE speaker. Honours enabled flag, publish gate and the per-speaker ring gate.
    /// <paramref name="dryRun"/> builds the request but makes no live call.
    /// </summary>
    public async Task<SpeakerBioSyncResult> SyncOneAsync(
        int eventId, int participantId, bool dryRun = false, CancellationToken ct = default)
    {
        var profile = await _db.SpeakerProfiles
            .FirstOrDefaultAsync(p => p.EventId == eventId && p.ParticipantId == participantId, ct)
            ?? throw new InvalidOperationException($"No speaker profile for participant {participantId} in event {eventId}.");
        var participant = await _db.Participants
            .FirstOrDefaultAsync(p => p.Id == participantId, ct)
            ?? throw new InvalidOperationException($"No participant {participantId}.");

        var request = BuildRequest(participant.Email, profile);

        if (!_options.Enabled)
            return new SpeakerBioSyncResult(participantId, SpeakerBioSyncOutcome.Disabled, request);
        if (dryRun || !_backstage.CanWrite)
            return new SpeakerBioSyncResult(participantId, SpeakerBioSyncOutcome.BuiltOnly, request);

        // RING GATE (§26c): never sync a speaker whose release ring isn't active for the
        // speaker-sync feature — a locked default-ring speaker is held until promoted.
        if (!await _gate.IsFeatureActiveForParticipantAsync(FeatureKey, eventId, participantId, _rings, ct))
            return new SpeakerBioSyncResult(participantId, SpeakerBioSyncOutcome.RingGated, request);

        try
        {
            var r = await _backstage.UpsertSpeakerBioAsync(request, ct);
            switch (r.Action)
            {
                case BackstageSpeakerAction.Created:
                    if (!string.IsNullOrWhiteSpace(r.SpeakerId))
                    {
                        profile.BackstageSpeakerId = r.SpeakerId;
                        await _db.SaveChangesAsync(ct);
                    }
                    return new SpeakerBioSyncResult(participantId,
                        request.PublishState == SpeakerPublishState.Public
                            ? SpeakerBioSyncOutcome.PushedPublic : SpeakerBioSyncOutcome.PushedDraft,
                        request);

                case BackstageSpeakerAction.ExistsBlocked:
                    if (!string.IsNullOrWhiteSpace(r.SpeakerId) && string.IsNullOrWhiteSpace(profile.BackstageSpeakerId))
                    {
                        profile.BackstageSpeakerId = r.SpeakerId;
                        await _db.SaveChangesAsync(ct);
                    }
                    await AlertManualUpdateAsync(participant, ct);
                    return new SpeakerBioSyncResult(participantId, SpeakerBioSyncOutcome.BlockedNeedsManualUpdate, request);

                default:
                    return new SpeakerBioSyncResult(participantId, SpeakerBioSyncOutcome.Failed, request, r.Error);
            }
        }
        catch (Exception ex)
        {
            return new SpeakerBioSyncResult(participantId, SpeakerBioSyncOutcome.Failed, request, ex.Message);
        }
    }

    /// <summary>
    /// Sync every speaker in an edition (manual opt-in trigger). Each speaker is gated
    /// independently (publish gate + ring gate).
    /// </summary>
    public async Task<IReadOnlyList<SpeakerBioSyncResult>> SyncAllAsync(
        int eventId, bool dryRun = false, CancellationToken ct = default)
    {
        var ids = await _db.SpeakerProfiles
            .Where(p => p.EventId == eventId).Select(p => p.ParticipantId).ToListAsync(ct);
        var results = new List<SpeakerBioSyncResult>(ids.Count);
        foreach (var id in ids)
            results.Add(await SyncOneAsync(eventId, id, dryRun, ct));
        return results;
    }

    private async Task AlertManualUpdateAsync(Participant participant, CancellationToken ct)
    {
        try
        {
            string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);
            var html =
                $"<p><strong>{Enc(participant.FullName)}</strong> ({Enc(participant.Email)}) updated their speaker "
                + "details in the hub, but Zoho Backstage's speakers API is <strong>create-only</strong> — it has no "
                + "update endpoint, so the change could not be synced automatically.</p>"
                + "<p>Please update this speaker manually in the Backstage UI so the two stay in sync.</p>";
            await _email.SendAsync(AlertEmail, $"[ELDK27] Speaker needs a manual Backstage update — {participant.FullName}", html, ct);
        }
        catch { /* alert is best-effort; the sync result already records the blocked state */ }
    }
}
