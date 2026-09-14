using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CommunityHub.Core.Email;

/// <summary>What a dry run found — the numbers an organizer must see before a campaign may send.</summary>
/// <param name="Total">Everyone the audience resolves to today.</param>
/// <param name="Suppressed">How many of those have unsubscribed or bounced.</param>
/// <param name="Sample">A handful of real addresses, so "all attendees" is not taken on trust.</param>
public sealed record MailCampaignPreview(
    int Total, int Suppressed, IReadOnlyList<string> Sample)
{
    /// <summary>Who would actually receive it.</summary>
    public int Sendable => Math.Max(0, Total - Suppressed);
}

/// <summary>Why a campaign may not send. Null <see cref="Reason"/> ⇒ it may.</summary>
public sealed record MailCampaignGate(string? Reason)
{
    public bool MaySend => Reason is null;
    public static readonly MailCampaignGate Ok = new((string?)null);
}

/// <summary>
/// §1080 stages 3–4 — previewing a campaign, approving it, and sending it in batches.
/// </summary>
/// <remarks>
/// <para>🔴 <b>Four things must ALL be true before one mail leaves.</b> They are separate on purpose:
/// each covers a different way this goes wrong.</para>
/// <list type="number">
///   <item>the <b>feature switch</b> is on (the caller checks it — the page must explain the refusal);</item>
///   <item>an organizer has <b>acknowledged a dry run</b> of THIS audience and template;</item>
///   <item>an <b>unsubscribe secret</b> is configured, so every mail carries a working way out;</item>
///   <item>the recipient is <b>not suppressed at this moment</b> — checked per batch, not per audience.</item>
/// </list>
///
/// <para>🔑 <b>Batched by design</b> (his words: <i>"so ip is not blocked"</i>). A shared sending IP
/// carries a reputation shared with everyone else on it, and a thousand messages in a minute is what
/// gets it throttled or listed.</para>
/// </remarks>
public sealed class MailCampaignService
{
    private readonly CommunityHubDbContext _db;
    private readonly MailAudienceResolver _audiences;
    private readonly MailSuppressionService _suppression;
    private readonly EmailTemplateProvider _templates;
    private readonly IEmailSender _email;
    private readonly IEmailContextAccessor _context;
    private readonly TimeProvider _clock;
    private readonly string _hubUrl;

    public MailCampaignService(
        CommunityHubDbContext db,
        MailAudienceResolver audiences,
        MailSuppressionService suppression,
        EmailTemplateProvider templates,
        IEmailSender email,
        IEmailContextAccessor context,
        TimeProvider? clock = null,
        IOptions<EmailTemplateOptions>? branding = null)
    {
        _db = db;
        _audiences = audiences;
        _suppression = suppression;
        _templates = templates;
        _email = email;
        _context = context;
        _clock = clock ?? TimeProvider.System;
        _hubUrl = (branding?.Value.HubUrl ?? string.Empty).TrimEnd('/');
    }

    // ---------------------------------------------------------------- preview

    /// <summary>
    /// The dry run: how many, how many are suppressed, and a sample of real addresses.
    /// </summary>
    /// <remarks>
    /// 🔑 The SAMPLE is the point. A count can be right about the wrong audience — "1,284 people" is
    /// equally true of attendees and of everyone who ever attended. Seeing five actual addresses is
    /// what catches "I picked the wrong one" before eight thousand strangers do.
    /// </remarks>
    public async Task<MailCampaignPreview> PreviewAsync(
        MailCampaign campaign, int sampleSize = 8, CancellationToken ct = default)
    {
        var people = await _audiences.ResolveAsync(campaign.EventId, campaign.Audience, ct);
        var suppressed = await _suppression.SuppressedSetAsync(campaign.EventId, ct);

        var sendable = people.Where(p => !suppressed.Contains(p.Email)).ToList();

        return new MailCampaignPreview(
            people.Count,
            people.Count - sendable.Count,
            sendable.Take(sampleSize).Select(p => p.Email).ToList());
    }

    /// <summary>
    /// Record that a person has seen the preview and approved it.
    /// </summary>
    /// <remarks>
    /// ⚠️ The acknowledgement is against a COUNT. If the audience grows before the send, the page
    /// shows both numbers — an approval of 1,284 people is not an approval of 9,000.
    /// </remarks>
    public async Task AcknowledgeAsync(
        MailCampaign campaign, string? byEmail, int recipientCount, CancellationToken ct = default)
    {
        campaign.DryRunAcknowledgedAt = _clock.GetUtcNow();
        campaign.DryRunAcknowledgedByEmail = byEmail;
        campaign.DryRunRecipientCount = recipientCount;
        campaign.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 🔴 Invalidate an acknowledgement — called whenever the audience or template changes. An
    /// approval of a different mailing is not an approval of this one.
    /// </summary>
    public void InvalidateAcknowledgement(MailCampaign campaign)
    {
        campaign.DryRunAcknowledgedAt = null;
        campaign.DryRunAcknowledgedByEmail = null;
        campaign.DryRunRecipientCount = 0;
    }

    // ------------------------------------------------------------------ gate

    /// <summary>Why this campaign may not send right now — the sentence the page prints.</summary>
    public MailCampaignGate Gate(MailCampaign campaign)
    {
        if (string.IsNullOrWhiteSpace(campaign.TemplateKey))
            return new MailCampaignGate("No template chosen.");

        if (campaign.DryRunAcknowledgedAt is null)
            return new MailCampaignGate(
                "Nobody has approved the recipient list yet — open the preview and confirm it.");

        // 🔴 §1080b — the unsubscribe link is PER CAMPAIGN, and required for exactly one audience.
        // Operator 2026-08-13: "no other templates must have that or no other emails must have
        // that". An opt-out on a mailing to this edition's own people would invite them to unsubscribe
        // from the operational mail they need — their ticket, their session, their booth.
        if (MailAudienceResolver.ReachesOutsideTheHub(campaign.Audience)
            && !campaign.IncludeUnsubscribeLink)
            return new MailCampaignGate(
                "This audience is people with no current relationship to this edition, so the mail "
                + "must carry an unsubscribe link. Turn it on for this campaign.");

        // 🔒 The secret is only needed by a campaign that actually carries the link.
        if (campaign.IncludeUnsubscribeLink && !_suppression.CanMakeLinks)
            return new MailCampaignGate(
                "This campaign carries an unsubscribe link but no secret is configured to sign it. "
                + "Set Email:UnsubscribeSecret before sending.");

        if (campaign.State == MailCampaignState.Sent)
            return new MailCampaignGate("This campaign has already finished.");

        return MailCampaignGate.Ok;
    }

    // ------------------------------------------------------------------ send

    /// <summary>
    /// Freeze the audience into per-recipient rows and put the campaign into sending.
    /// </summary>
    /// <remarks>
    /// 🔑 The audience is resolved ONCE, here. From then on the campaign works through a fixed list,
    /// so a person who joins tomorrow is not silently added to a mailing somebody approved today —
    /// and suppression is still re-checked per batch, so leaving is honoured immediately.
    /// </remarks>
    public async Task<int> StartAsync(MailCampaign campaign, CancellationToken ct = default)
    {
        if (!Gate(campaign).MaySend) return 0;

        var already = await _db.MailCampaignRecipients
            .AnyAsync(r => r.MailCampaignId == campaign.Id, ct);

        if (!already)
        {
            var people = await _audiences.ResolveAsync(campaign.EventId, campaign.Audience, ct);
            foreach (var p in people)
            {
                _db.MailCampaignRecipients.Add(new MailCampaignRecipient
                {
                    MailCampaignId = campaign.Id, Email = p.Email, Name = p.Name,
                });
            }
        }

        campaign.State = MailCampaignState.Sending;
        campaign.StartedAt ??= _clock.GetUtcNow();
        campaign.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);

        return await _db.MailCampaignRecipients.CountAsync(r => r.MailCampaignId == campaign.Id, ct);
    }

    /// <summary>
    /// Send ONE batch. Returns how many were actually mailed.
    /// </summary>
    /// <remarks>
    /// <para>🔒 Suppression is read ONCE per batch and applied per recipient — an unsubscribe that
    /// arrived since the campaign started is honoured on the very next batch.</para>
    ///
    /// <para>⚠️ A failure is RECORDED against the recipient and the batch continues. One bad address
    /// must not stop a mailing to three thousand people, and the row is what lets an organizer see
    /// which ones to look at.</para>
    /// </remarks>
    public async Task<int> SendBatchAsync(MailCampaign campaign, CancellationToken ct = default)
    {
        if (!Gate(campaign).MaySend) return 0;

        var pending = await _db.MailCampaignRecipients
            .Where(r => r.MailCampaignId == campaign.Id
                        && r.State == MailCampaignRecipientState.Pending)
            .OrderBy(r => r.Id)
            .Take(Math.Max(1, campaign.BatchSize))
            .ToListAsync(ct);

        if (pending.Count == 0)
        {
            campaign.State = MailCampaignState.Sent;
            campaign.CompletedAt ??= _clock.GetUtcNow();
            await _db.SaveChangesAsync(ct);
            return 0;
        }

        var suppressed = await _suppression.SuppressedSetAsync(campaign.EventId, ct);
        var ev = await _db.Events.FirstOrDefaultAsync(e => e.Id == campaign.EventId, ct);
        var now = _clock.GetUtcNow();
        var sent = 0;

        foreach (var r in pending)
        {
            if (suppressed.Contains(r.Email))
            {
                r.State = MailCampaignRecipientState.Suppressed;
                campaign.SuppressedCount++;
                continue;
            }

            try
            {
                var tokens = _templates.NewTokenSet(null);
                tokens["recipientName"] = r.Name ?? string.Empty;
                tokens["eventDisplayName"] = ev?.DisplayName ?? string.Empty;
                var rendered = _templates.Render(campaign.TemplateKey, tokens);

                // 🔴 §1080b — the footer goes on ONLY the campaigns that declare it (in practice:
                // the previous-attendees group). Operator 2026-08-13: "no other templates must have
                // that or no other emails must have that". ⚠️ It is still appended by the SENDER
                // rather than written into the template, so the one mailing that must carry it
                // cannot lose it by somebody editing the template text.
                var body = campaign.IncludeUnsubscribeLink
                    ? rendered.HtmlBody + _suppression.FooterHtml(
                        _hubUrl, campaign.EventId, r.Email, ev?.CommunityName ?? "the organizers")
                    : rendered.HtmlBody;

                using (_context.Set(new EmailContext(
                           Category: "campaign",
                           EventId: campaign.EventId,
                           RecipientName: r.Name,
                           TemplateName: campaign.TemplateKey,
                           FeatureKey: MailCampaignFeature.Key)))
                {
                    await _email.SendAsync(r.Email, rendered.Subject, body, ct);
                }

                r.State = MailCampaignRecipientState.Sent;
                r.SentAt = now;
                campaign.SentCount++;
                sent++;
            }
            catch (Exception ex)
            {
                r.State = MailCampaignRecipientState.Failed;
                r.Error = ex.Message.Length > 400 ? ex.Message[..400] : ex.Message;
                campaign.FailedCount++;
            }
        }

        campaign.LastBatchAt = now;
        campaign.UpdatedAt = now;

        // 🔴 SAVE FIRST, then ask what is left. A query reads the STORE, not the change tracker, so
        // counting before the save sees this batch's rows as still pending — and the campaign never
        // reaches Sent, which means the job keeps waking it up for ever and an organizer watching the
        // page never sees it finish.
        await _db.SaveChangesAsync(ct);

        var remaining = await _db.MailCampaignRecipients
            .CountAsync(r => r.MailCampaignId == campaign.Id
                             && r.State == MailCampaignRecipientState.Pending, ct);
        if (remaining == 0)
        {
            campaign.State = MailCampaignState.Sent;
            campaign.CompletedAt ??= now;
            await _db.SaveChangesAsync(ct);
        }

        return sent;
    }

    /// <summary>
    /// The campaigns a job should act on now: sending (and due a batch), or scheduled and due.
    /// </summary>
    public async Task<IReadOnlyList<MailCampaign>> DueAsync(
        int eventId, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();

        var candidates = await _db.MailCampaigns
            .Where(c => c.EventId == eventId
                        && (c.State == MailCampaignState.Sending
                            || c.State == MailCampaignState.Scheduled))
            .ToListAsync(ct);

        return candidates
            .Where(c => c.State != MailCampaignState.Scheduled
                        || (c.ScheduledFor is { } when && when <= now))
            // ⚠️ The interval is honoured per campaign: it is the whole reason batches exist.
            .Where(c => c.LastBatchAt is not { } last
                        || last.AddMinutes(Math.Max(1, c.BatchIntervalMinutes)) <= now)
            .ToList();
    }
}

/// <summary>The campaign feature key, in one place so the page, the job and the service agree.</summary>
public static class MailCampaignFeature
{
    public const string Key = "mail-campaigns";
}
