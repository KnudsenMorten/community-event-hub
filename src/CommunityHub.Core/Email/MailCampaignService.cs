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
    private readonly EmailTemplateRingService? _rings;
    private readonly IEmailDeliveryOutcome? _outcome;
    private readonly Integrations.DocLibrary.LogisticsRecipients? _recipients;

    /// <summary>
    /// 🔴 §1381 — the operator's rollout per mail: ring 0 → 1 → 2 are REVIEW sends (<see cref="SendReviewAsync"/>);
    /// the campaign itself goes to its whole audience ONLY once that mail is released to Broad. Operator 2026-11-07:
    /// <i>"make sure nothing goes out to everyone in the mail campaign. it must follow the rings + steps"</i>.
    /// ⚠️ Without this, a campaign started early sent into the ring gate, every recipient was DROPPED there — and the
    /// loop still marked them Sent, so the list was spent and nothing ever reached anyone.
    /// No ring service (legacy/test construction) ⇒ the check is skipped; both hosts register it.
    /// </summary>
    public async Task<bool> ReleasedToEveryoneAsync(MailCampaign campaign, CancellationToken ct = default) =>
        _rings is null
        || await _rings.GetEffectiveRingAsync(campaign.EventId, campaign.TemplateKey, ct) == Settings.Ring.Broad;

    public const string NotReleasedReason =
        "This mail's ring is not Broad yet. Do the review steps first (send to ring 0, 1, 2), then release the mail to Broad on the Email templates page — only then does the campaign go to its audience.";

    public MailCampaignService(
        CommunityHubDbContext db,
        MailAudienceResolver audiences,
        MailSuppressionService suppression,
        EmailTemplateProvider templates,
        IEmailSender email,
        IEmailContextAccessor context,
        TimeProvider? clock = null,
        IOptions<EmailTemplateOptions>? branding = null,
        // §1381 — the rollout guard (mail ring must be Broad before the real send) and honest drop recording.
        EmailTemplateRingService? rings = null,
        IEmailDeliveryOutcome? outcome = null,
        // §1388 — where the release plan's one-line step notices go (the organizers' mailbox).
        Integrations.DocLibrary.LogisticsRecipients? recipients = null)
    {
        _recipients = recipients;
        _rings = rings;
        _outcome = outcome;
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
        if (!await ReleasedToEveryoneAsync(campaign, ct)) return 0;   // 🔴 §1381 — step 5 only

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
        // 🔴 §1381 — re-checked per batch: lowering the mail's ring mid-campaign pauses it instead of draining the list.
        if (!await ReleasedToEveryoneAsync(campaign, ct)) return 0;

        // 🔴 §1388 — a release plan in its first wave sends ONLY up to the wave size; nothing before the wave has started,
        // and the rest waits for step 3 (or for Stop). Without a plan this is the whole batch, as before.
        var take = Math.Max(1, campaign.BatchSize);
        if (campaign.PlanEveryoneAt is not null && campaign.PlanEveryoneDoneAt is null)
        {
            if (campaign.PlanStoppedAt is not null || campaign.PlanWaveSize is not int waveSize) return 0;
            var handled = await _db.MailCampaignRecipients.CountAsync(
                r => r.MailCampaignId == campaign.Id && r.State != MailCampaignRecipientState.Pending, ct);
            if (handled >= waveSize) return 0;
            take = Math.Min(take, waveSize - handled);
        }

        var pending = await _db.MailCampaignRecipients
            .Where(r => r.MailCampaignId == campaign.Id
                        && r.State == MailCampaignRecipientState.Pending)
            .OrderBy(r => r.Id)
            .Take(take)
            .ToListAsync(ct);

        if (pending.Count == 0)
        {
            campaign.State = MailCampaignState.Sent;
            campaign.CompletedAt ??= _clock.GetUtcNow();
            await _db.SaveChangesAsync(ct);
            return 0;
        }

        var suppressed = await _suppression.SuppressedSetAsync(campaign.EventId, ct);
        // 🔴 §1381 — the audience is RECALCULATED for every batch, not trusted from StartAsync. Operator 2026-11-07:
        // "it is important that you recalculate the mailing list each time using the latest list of attendees, so fx
        // an attendee that bought a ticket 1 hr ago will NOT get the 5 'no ticket yet' emails". The attendee mirror
        // syncs every 5 minutes; a batch that trusted the list frozen at start would still mail somebody who bought a
        // ticket while the campaign was sending. Nobody is ADDED here (StartAsync's rule stands) — people only leave.
        var stillInAudience = (await _audiences.ResolveAsync(campaign.EventId, campaign.Audience, ct))
            .Select(p => p.Email)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
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

            if (!stillInAudience.Contains(r.Email))
            {
                r.State = MailCampaignRecipientState.LeftAudience;
                r.Error = "not sent: no longer in the audience at send time (e.g. bought a ticket)";
                continue;
            }

            try
            {
                var tokens = _templates.NewTokenSet(null);
                tokens["recipientName"] = r.Name ?? string.Empty;
                // §1381 — the campaign templates greet by first name; "there" when the list had no name.
                tokens["firstName"] = string.IsNullOrWhiteSpace(r.Name) ? "there" : r.Name.Trim().Split(' ')[0];
                tokens["eventDisplayName"] = ev?.DisplayName ?? string.Empty;
                var rendered = RenderInEdition(campaign, tokens);

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
                           FeatureKey: MailCampaignFeature.Key,
                           BrevoTag: TagFor(campaign))))
                {
                    await _email.SendAsync(r.Email, rendered.Subject, body, ct);
                }

                // 🔴 §1381 — a send the transport DROPPED (ring gate, kill switch, ceiling) is not "sent". Recording it
                // as sent spent the recipient while nothing reached them. It is recorded as failed with the reason.
                if (_outcome is not null && !_outcome.LastSendDelivered)
                {
                    r.State = MailCampaignRecipientState.Failed;
                    r.Error = $"not delivered: {_outcome.LastDropReason ?? "dropped"}";
                    campaign.FailedCount++;
                    continue;
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
    /// 🔴 §1381 — render the template INSIDE the campaign's edition, so the organizer's edited text (Email templates
    /// page, §25h per-edition override) is what goes out. <see cref="EmailTemplateProvider"/> reads the edition from the
    /// ambient <see cref="EmailContext"/>; rendering before any context was set made every campaign send, test and
    /// preview silently fall back to the shipped text — an organizer's edit was ignored without a word (found
    /// 2026-11-07 when the operator asked whether organizers can modify the text).
    /// </summary>
    private RenderedEmail RenderInEdition(MailCampaign campaign, Dictionary<string, string> tokens)
    {
        using (_context.Set(new EmailContext(
                   Category: "campaign-render",
                   EventId: campaign.EventId,
                   TemplateName: campaign.TemplateKey,
                   FeatureKey: MailCampaignFeature.Key)))
        {
            return _templates.Render(campaign.TemplateKey, tokens);
        }
    }

    /// <summary>
    /// §1381 — render the campaign exactly as a recipient would get it (subject + full HTML, unsubscribe footer included
    /// when the campaign carries one), for the preview pane and the test send. Sends nothing.
    /// </summary>
    public async Task<(string Subject, string Html)> RenderAsync(
        MailCampaign campaign, string recipientEmail, string? recipientName, CancellationToken ct = default)
    {
        var ev = await _db.Events.FirstOrDefaultAsync(e => e.Id == campaign.EventId, ct);
        var tokens = _templates.NewTokenSet(null);
        tokens["recipientName"] = recipientName ?? string.Empty;
        tokens["firstName"] = string.IsNullOrWhiteSpace(recipientName) ? "there" : recipientName.Trim().Split(' ')[0];
        tokens["eventDisplayName"] = ev?.DisplayName ?? string.Empty;
        var rendered = RenderInEdition(campaign, tokens);
        var body = campaign.IncludeUnsubscribeLink && _suppression.CanMakeLinks
            ? rendered.HtmlBody + _suppression.FooterHtml(_hubUrl, campaign.EventId, recipientEmail, ev?.CommunityName ?? "the organizers")
            : rendered.HtmlBody;
        return (rendered.Subject, body);
    }

    /// <summary>
    /// §1381 — "send a test to me": ONE mail to the signed-in organizer, subject prefixed [TEST]. Ring-exempt because
    /// the organizer asked for it themselves and is the only recipient — the same reasoning as the template editor's
    /// "Send test to this address". Never touches the recipient ledger or the campaign counters.
    /// </summary>
    public async Task SendTestAsync(MailCampaign campaign, string toEmail, string? toName, CancellationToken ct = default)
    {
        var (subject, html) = await RenderAsync(campaign, toEmail, toName, ct);
        using (_context.Set(new EmailContext(
                   Category: "campaign-test",
                   EventId: campaign.EventId,
                   RecipientName: toName,
                   TemplateName: campaign.TemplateKey,
                   FeatureKey: MailCampaignFeature.Key,
                   RingExempt: true,
                   BrevoTag: TagFor(campaign) + "-test")))
        {
            await _email.SendAsync(toEmail, "[TEST] " + subject, html, ct);
        }
    }

    /// <summary>
    /// §1381 — REVIEW send, the operator's steps 2–4: the campaign mail to the hub's own people in rings 0..<paramref name="upTo"/>
    /// (ring 0 = him, ring 1 = organizers, ring 2 = design partners), subject prefixed [PREVIEW]. NOT the campaign
    /// audience, no ledger, no counters. 🔒 NOT ring-exempt: it goes through the transport's ring gate with this mail's
    /// own ring, so ring N people receive it only after he has raised the mail to ring N — the order of his steps is
    /// enforced by the same gate as every other mail. Broad is refused here: that is the real send (step 5).
    /// </summary>
    /// <returns>(asked, delivered) — delivered counts only what the transport actually dispatched.</returns>
    public async Task<(int Asked, int Delivered)> SendReviewAsync(
        MailCampaign campaign, Settings.Ring upTo, CancellationToken ct = default, Settings.Ring fromRing = Settings.Ring.Ring0)
    {
        if (upTo == Settings.Ring.Broad)
            throw new InvalidOperationException("Broad is the real campaign send, not a review send.");

        // §1388 — fromRing lets the release plan send ring 2 ONLY at its step, so rings 0–1 do not get the mail twice.
        var people = await _db.Participants.AsNoTracking()
            .Where(p => p.EventId == campaign.EventId && p.IsActive && (int)p.Ring <= (int)upTo && (int)p.Ring >= (int)fromRing)
            .Select(p => new { p.Id, p.Email, p.FullName })
            .ToListAsync(ct);

        int asked = 0, delivered = 0;
        foreach (var p in people.Where(p => !string.IsNullOrWhiteSpace(p.Email)))
        {
            var (subject, html) = await RenderAsync(campaign, p.Email, p.FullName, ct);
            using (_context.Set(new EmailContext(
                       Category: "campaign-review",
                       EventId: campaign.EventId,
                       ParticipantId: p.Id,
                       RecipientName: p.FullName,
                       TemplateName: campaign.TemplateKey,
                       FeatureKey: MailCampaignFeature.Key,
                       BrevoTag: TagFor(campaign) + "-review")))
            {
                await _email.SendAsync(p.Email, $"[PREVIEW ring {(int)upTo}] {subject}", html, ct);
            }
            asked++;
            if (_outcome is null || _outcome.LastSendDelivered) delivered++;
        }
        return (asked, delivered);
    }

    /// <summary>§1381 — the Brevo tag a campaign's mails carry, and the key its stats are read back by.</summary>
    /// <summary>
    /// §1381 — the Brevo tag every mail of this campaign carries: WHO it was for + WHICH campaign, readable in Brevo,
    /// e.g. <c>ceh-previous-attendees-no-ticket-c12</c> vs <c>ceh-attendees-c13</c>. Operator 2026-11-07: <i>"flag them
    /// with a campaign name so we can separate them — 'previous attendees, no ELDK27 ticket' vs. ELDK27 attendee"</i>.
    /// One tag per campaign (the stats are read by it), so it stays unique through the id suffix.
    /// </summary>
    public static string TagFor(MailCampaign campaign) => $"ceh-{AudienceSlug(campaign.Audience)}-c{campaign.Id}";

    /// <summary>§1381 — a stable, readable slug per audience for <see cref="TagFor"/>. Never renamed once used: Brevo stats are keyed by it.</summary>
    public static string AudienceSlug(MailAudience audience) => audience switch
    {
        MailAudience.PreviousAttendeesNotThisEdition => "previous-attendees-no-ticket",
        MailAudience.AllAttendees => "attendees",
        MailAudience.TwoDayTicketHolders => "attendees-2day",
        MailAudience.OneDayTicketHolders => "attendees-1day",
        MailAudience.AllParticipants => "all-participants",
        MailAudience.SponsorsAndExhibitors => "sponsors-exhibitors",
        MailAudience.ExhibitorsOnly => "exhibitors",
        MailAudience.Volunteers => "volunteers",
        MailAudience.VolunteersPlusExtras => "volunteers-extras",
        MailAudience.Speakers => "speakers",
        MailAudience.MasterClassSpeakers => "masterclass-speakers",
        MailAudience.Media => "media",
        MailAudience.EventPartners => "event-partners",
        MailAudience.AppreciationDinner => "appreciation-dinner",
        MailAudience.PartyParticipants => "party",
        _ => "audience-" + (int)audience,
    };

    // ---------------------------------------------------------------- §1388 release plan

    public const string PlanStepMailKey = "campaign-release-step";

    /// <summary>
    /// §1388 — set the staged release plan (operator 2026-11-08: <i>"send to organizers at 8, send to 5% at 9 then rest at
    /// 10"</i> — previous attendees have no ring, so the waves are of the AUDIENCE). The campaign becomes Scheduled for the
    /// first wave. Returns null when set, else why not.
    /// </summary>
    public async Task<string?> SetPlanAsync(MailCampaign campaign, DateTimeOffset preview, DateTimeOffset wave, DateTimeOffset everyone,
        string? byEmail, CancellationToken ct = default, int wavePercent = 5)
    {
        if (campaign.State is MailCampaignState.Sending or MailCampaignState.Sent)
            return "This campaign has already started — a release plan is for before the first send.";
        if (!(preview < wave && wave < everyone)) return "The three times must be in order: organizers, then the first wave, then everyone.";
        if (preview <= _clock.GetUtcNow()) return "The first step must be in the future.";
        if (wavePercent is < 1 or > 50) return "The first wave is 1–50 % of the audience.";
        var gate = Gate(campaign);
        if (!gate.MaySend) return gate.Reason;

        campaign.PlanPreviewAt = preview;
        campaign.PlanWaveAt = wave;
        campaign.PlanEveryoneAt = everyone;
        campaign.PlanWavePercent = wavePercent;
        campaign.PlanWaveSize = null;
        campaign.PlanPreviewDoneAt = campaign.PlanWaveDoneAt = campaign.PlanEveryoneDoneAt = null;
        campaign.PlanStoppedAt = null;
        campaign.PlanStoppedByEmail = null;
        campaign.PlanSetByEmail = byEmail;
        campaign.ScheduledFor = wave;
        campaign.State = MailCampaignState.Scheduled;
        campaign.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>
    /// §1388 — the Stop button: no further plan step runs and the campaign goes back to Draft — also mid-wave, so the
    /// batches stop. Mails already sent stay sent; the frozen recipient list keeps who got it.
    /// </summary>
    public async Task StopPlanAsync(MailCampaign campaign, string? byEmail, CancellationToken ct = default)
    {
        campaign.PlanStoppedAt = _clock.GetUtcNow();
        campaign.PlanStoppedByEmail = byEmail;
        if (campaign.State is MailCampaignState.Scheduled or MailCampaignState.Sending) campaign.State = MailCampaignState.Draft;
        campaign.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        await NoticeAsync(campaign, $"STOPPED by {byEmail ?? "an organizer"} — no further step will run.", ct);
    }

    /// <summary>
    /// §1388 — run every campaign's DUE plan step, at most one step per campaign per call (so the order is kept even after
    /// a missed tick). Step 1: a [PREVIEW] to the organizers (ring 0). Step 2: the mail is released to Broad, the audience
    /// is frozen and the first <see cref="MailCampaign.PlanWavePercent"/> % is sent through the normal batches — they stop
    /// at the wave size (<see cref="SendBatchAsync"/>). Step 3: the cap is lifted and the rest follows in the normal batches.
    /// Returns one line per step taken.
    /// </summary>
    public async Task<IReadOnlyList<string>> RunPlanStepsAsync(int eventId, CancellationToken ct = default)
    {
        var done = new List<string>();
        if (_rings is null) return done;
        var now = _clock.GetUtcNow();
        var plans = await _db.MailCampaigns
            .Where(c => c.EventId == eventId && c.PlanEveryoneAt != null && c.PlanStoppedAt == null && c.PlanEveryoneDoneAt == null
                        && (c.State == MailCampaignState.Scheduled || c.State == MailCampaignState.Sending))
            .ToListAsync(ct);

        foreach (var c in plans)
        {
            var gate = Gate(c);
            if (!gate.MaySend) continue;   // e.g. the recipient list changed and needs approving again — the page says so

            if (c.PlanPreviewDoneAt is null && c.PlanPreviewAt <= now)
            {
                var (asked, delivered) = await SendReviewAsync(c, Settings.Ring.Ring0, ct);
                c.PlanPreviewDoneAt = now;
                await _db.SaveChangesAsync(ct);
                var line = $"Step 1 done: preview sent to the organizers ({delivered} of {asked} delivered). The first {c.PlanWavePercent}% of the audience follows at {Dk(c.PlanWaveAt)}.";
                await NoticeAsync(c, line, ct);
                done.Add($"{c.Name}: {line}");
            }
            else if (c.PlanPreviewDoneAt is not null && c.PlanWaveDoneAt is null && c.PlanWaveAt <= now)
            {
                await RaiseRingAsync(c, Settings.Ring.Broad, ct);
                var total = await StartAsync(c, ct);
                c.PlanWaveSize = Math.Max(1, (int)Math.Ceiling(total * c.PlanWavePercent / 100.0));
                c.PlanWaveDoneAt = now;
                await _db.SaveChangesAsync(ct);
                var line = $"Step 2 started: the real mail to the first {c.PlanWaveSize} of {total} recipients ({c.PlanWavePercent}%). The rest follows at {Dk(c.PlanEveryoneAt)} unless you press Stop.";
                await NoticeAsync(c, line, ct);
                done.Add($"{c.Name}: {line}");
            }
            else if (c.PlanWaveDoneAt is not null && c.PlanEveryoneAt <= now)
            {
                c.PlanEveryoneDoneAt = now;
                await _db.SaveChangesAsync(ct);
                var line = $"Step 3 done: the rest of the audience now follows in batches of {c.BatchSize} every {c.BatchIntervalMinutes} minutes.";
                await NoticeAsync(c, line, ct);
                done.Add($"{c.Name}: {line}");
            }
        }
        return done;
    }

    private static string Dk(DateTimeOffset? at)
    {
        if (at is not { } v) return "—";
        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Copenhagen"); }
        catch (TimeZoneNotFoundException) { tz = TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time"); }
        return TimeZoneInfo.ConvertTime(v, tz).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task RaiseRingAsync(MailCampaign c, Settings.Ring to, CancellationToken ct)
    {
        var current = await _rings!.GetEffectiveRingAsync(c.EventId, c.TemplateKey, ct);
        if ((int)current < (int)to)
            await _rings.SetRingAsync(c.EventId, c.TemplateKey, to, $"release plan (campaign {c.Id})", ct);
    }

    /// <summary>§1388 — the one-line notice per step to the organizers' mailbox. Never throws.</summary>
    private async Task NoticeAsync(MailCampaign c, string line, CancellationToken ct)
    {
        try
        {
            var tokens = _templates.NewTokenSet(null);
            tokens["campaignName"] = c.Name;
            tokens["campaignId"] = c.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            tokens["line"] = System.Net.WebUtility.HtmlEncode(line);
            var rendered = _templates.Render(PlanStepMailKey, tokens);
            var to = _recipients?.ReviewMailbox ?? Integrations.DocLibrary.LogisticsRecipients.DefaultReviewMailbox;
            using (_context.Set(new EmailContext(PlanStepMailKey, c.EventId, null, null, TemplateName: PlanStepMailKey, RingExempt: true)))
            {
                await _email.SendAsync(to, rendered.Subject, rendered.HtmlBody, ct);
            }
        }
        catch (Exception)
        {
            // A notice that fails must not stop or repeat a release step; the page shows the step times.
        }
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
