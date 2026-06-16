using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// Propagates a speaker's <c>EffectiveEmail</c> to Zoho Backstage when the
/// speaker sets / changes / clears their contact-email override in the hub.
///
/// It always records the desired address as a durable marker in the
/// <see cref="SpeakerBackstageEmailSync"/> queue (one row per speaker per
/// edition, upserted). If a live Backstage speaker-email endpoint is wired
/// (<see cref="IBackstageSpeakerEmailApi.CanWrite"/> = true) it pushes
/// immediately and marks the row Synced; otherwise the row stays Pending for a
/// future drainer. A push failure marks the row Failed with the reason and is
/// swallowed — the hub's own mail/calendar already use the override, so external
/// propagation must never break saving the speaker form.
/// </summary>
public sealed class SpeakerEmailPropagationService
{
    private readonly CommunityHubDbContext _db;
    private readonly IBackstageSpeakerEmailApi _backstage;
    private readonly TimeProvider _clock;

    public SpeakerEmailPropagationService(
        CommunityHubDbContext db,
        IBackstageSpeakerEmailApi backstage,
        TimeProvider clock)
    {
        _db = db;
        _backstage = backstage;
        _clock = clock;
    }

    /// <summary>
    /// Record (and, when wired, push) the effective contact address for a
    /// speaker after their override changed. <paramref name="identityEmail"/> is
    /// the Sessionize/community address (the Backstage match key);
    /// <paramref name="contactEmailOverride"/> is the new override (null/blank =
    /// cleared, so the effective address falls back to the identity address).
    /// Returns the queue row's resulting state.
    /// </summary>
    public async Task<BackstageEmailSyncState> QueueAsync(
        int eventId,
        int participantId,
        string identityEmail,
        string? contactEmailOverride,
        CancellationToken ct = default)
    {
        var desired = SpeakerProfile.EffectiveEmailFor(identityEmail, contactEmailOverride);
        var now = _clock.GetUtcNow();

        var row = await _db.SpeakerBackstageEmailSyncs.FirstOrDefaultAsync(
            x => x.EventId == eventId && x.ParticipantId == participantId, ct);
        if (row is null)
        {
            row = new SpeakerBackstageEmailSync
            {
                EventId = eventId,
                ParticipantId = participantId,
            };
            _db.SpeakerBackstageEmailSyncs.Add(row);
        }

        row.IdentityEmail = identityEmail;
        row.DesiredEmail = desired;
        row.RequestedAt = now;
        row.State = BackstageEmailSyncState.Pending;
        row.SyncedAt = null;
        row.LastError = null;

        // ◻ Live Backstage speaker-email wiring is pending: the default
        // IBackstageSpeakerEmailApi cannot write, so the row stays Pending and
        // no Zoho call is faked. A live implementation drains it here.
        if (_backstage.CanWrite)
        {
            try
            {
                await _backstage.SetSpeakerEmailAsync(
                    new SpeakerEmailRecord(identityEmail, desired), ct);
                row.State = BackstageEmailSyncState.Synced;
                row.SyncedAt = now;
            }
            catch (Exception ex)
            {
                row.State = BackstageEmailSyncState.Failed;
                row.LastError = ex.Message;
            }
        }

        await _db.SaveChangesAsync(ct);
        return row.State;
    }
}
