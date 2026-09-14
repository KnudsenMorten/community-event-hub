using CommunityHub.Core.Data;
using CommunityHub.Core.Email;
using CommunityHub.Core.Settings;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// §1080 stage 4 — sends one BATCH per due campaign, per tick.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-12: <i>"mass mail in batches so ip is not blocked"</i> and <i>"ability to
/// schedule mails"</i>.</para>
///
/// <para>🔴 <b>Gated by <see cref="MailCampaignFeature.Key"/>, default OFF.</b> This is the only job
/// in the hub that can write to thousands of people, so the switch is checked every tick — turning
/// it off stops a campaign mid-flight, and the ledger means it resumes exactly where it stopped
/// rather than starting again.</para>
///
/// <para>⚠️ <b>ONE batch per campaign per tick.</b> The pacing lives in the campaign's own interval,
/// not in this trigger — draining a campaign as fast as the job ticks would defeat the batching it
/// exists to provide.</para>
/// </remarks>
public sealed class MailCampaignJob
{
    private readonly CommunityHubDbContext _db;
    private readonly MailCampaignService _campaigns;
    private readonly FeatureGateService _gate;
    private readonly ILogger<MailCampaignJob> _log;

    public MailCampaignJob(
        CommunityHubDbContext db, MailCampaignService campaigns, FeatureGateService gate,
        ILogger<MailCampaignJob> log)
    {
        _db = db; _campaigns = campaigns; _gate = gate; _log = log;
    }

    [Function("MailCampaignJob")]
    public async Task Run([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        var eventId = await _db.Events.Where(e => e.IsActive).Select(e => (int?)e.Id)
            .FirstOrDefaultAsync(ct);
        if (eventId is null) return;

        if (!await _gate.IsFeatureEnabledAsync(MailCampaignFeature.Key, eventId.Value, ct))
        {
            _log.LogInformation(
                "MailCampaignJob: campaigns are switched off ({Key}) — nothing sent.",
                MailCampaignFeature.Key);
            return;
        }

        var due = await _campaigns.DueAsync(eventId.Value, ct);
        if (due.Count == 0) return;

        foreach (var campaign in due)
        {
            // A scheduled campaign becomes a sending one here — freezing its audience at the moment
            // it starts, not at the moment somebody wrote the schedule.
            if (campaign.State == Core.Domain.MailCampaignState.Scheduled)
                await _campaigns.StartAsync(campaign, ct);

            var sent = await _campaigns.SendBatchAsync(campaign, ct);
            _log.LogInformation(
                "MailCampaignJob: campaign {Id} ({Name}) — {Sent} sent this batch, state {State}.",
                campaign.Id, campaign.Name, sent, campaign.State);
        }
    }
}
