using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// LIVE Zoho Backstage speaker writer (hub → Backstage), wrapping <see cref="ZohoClient"/>.
///
/// The Backstage v3 speakers API is <b>create-only</b> (verified live 2026-06-25 +
/// per the official docs — there is no update/edit endpoint; per-id POST/PUT/PATCH
/// all 404). So:
///   • a speaker NOT yet in Backstage → POST create (honouring the publish gate via
///     the <c>featured</c> flag);
///   • a speaker ALREADY in Backstage → we do NOT create a duplicate; we return
///     <see cref="BackstageSpeakerAction.ExistsBlocked"/> so the sync alerts a human
///     to update them in the Backstage UI.
/// <see cref="CanWrite"/> is true only when Zoho is enabled + the portal/event are
/// configured, so this stays a no-op (the Null writer's behaviour) otherwise.
/// </summary>
public sealed class LiveBackstageSpeakerBioApi : IBackstageSpeakerBioApi
{
    private readonly ZohoClient _zoho;
    private readonly ZohoOptions _options;
    private readonly ILogger<LiveBackstageSpeakerBioApi> _log;

    public LiveBackstageSpeakerBioApi(
        ZohoClient zoho, ZohoOptions options, ILogger<LiveBackstageSpeakerBioApi> log)
    {
        _zoho = zoho;
        _options = options;
        _log = log;
    }

    public bool CanWrite =>
        _options.Enabled
        && !string.IsNullOrWhiteSpace(_options.BackstagePortalId)
        && !string.IsNullOrWhiteSpace(_options.BackstageEventId);

    public async Task<BackstageSpeakerUpsertResult> UpsertSpeakerBioAsync(
        SpeakerBioRecord record, CancellationToken ct)
    {
        var token = await _zoho.GetAccessTokenAsync(ct);
        if (string.IsNullOrWhiteSpace(token))
            return new BackstageSpeakerUpsertResult(BackstageSpeakerAction.Failed, null, "Could not obtain a Zoho access token.");

        var email = (record.IdentityEmail ?? string.Empty).Trim();

        // Already in Backstage? (by stored id, or by email in the live index.) The API
        // can't update in place, so block rather than duplicate.
        if (!string.IsNullOrWhiteSpace(record.BackstageSpeakerId))
            return new BackstageSpeakerUpsertResult(BackstageSpeakerAction.ExistsBlocked, record.BackstageSpeakerId);

        var index = await _zoho.GetSpeakerIdsByEmailAsync(token!, ct);
        if (index.TryGetValue(email, out var existingId))
            return new BackstageSpeakerUpsertResult(BackstageSpeakerAction.ExistsBlocked, existingId);

        // New speaker → create. featured = the publish gate (only true when approved).
        var id = await _zoho.CreateSpeakerAsync(
            token!, email, record.FirstName, record.LastName, record.Country,
            record.Tagline, record.Biography, record.LinkedIn, record.Twitter,
            record.Skills, featured: record.PublishState == SpeakerPublishState.Public, ct);

        return id is null
            ? new BackstageSpeakerUpsertResult(BackstageSpeakerAction.Failed, null, "Backstage create-speaker POST failed.")
            : new BackstageSpeakerUpsertResult(BackstageSpeakerAction.Created, string.IsNullOrEmpty(id) ? null : id);
    }
}
