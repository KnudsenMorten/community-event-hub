using CommunityHub.Core.Audit;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Settings;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// "Help Promote" notifier (§26c): once a speaker's promo graphics have been
/// RELEASED (organizer review gate), email the speaker pointing them to the Help
/// Promote page (/Speaker/Graphics) to share on LinkedIn. Idempotent — the
/// ReminderEngine ledger sends each speaker exactly once (occasion per participant);
/// ring-gated + off by default behind the 'speaker-graphics-promote' feature.
/// </summary>
public sealed class SpeakerGraphicsReadyJob
{
    private readonly CommunityHubDbContext _db;
    private readonly ReminderEngine _engine;
    private readonly EmailTemplateProvider _templates;
    private readonly FeatureGateService _gate;
    private readonly IAuditTrail _audit;
    private readonly ILogger<SpeakerGraphicsReadyJob> _log;

    public SpeakerGraphicsReadyJob(
        CommunityHubDbContext db,
        ReminderEngine engine,
        EmailTemplateProvider templates,
        FeatureGateService gate,
        IAuditTrail audit,
        ILogger<SpeakerGraphicsReadyJob> log)
    {
        _db = db;
        _engine = engine;
        _templates = templates;
        _gate = gate;
        _audit = audit;
        _log = log;
    }

    /// <summary>Daily at 08:30 UTC.</summary>
    [Function("SpeakerGraphicsReadyJob")]
    public async Task Run(
        [TimerTrigger("0 30 8 * * *")] TimerInfo timer,
        CancellationToken ct)
    {
        var activeEvent = await _db.Events
            .Where(e => e.IsActive)
            .Select(e => new { e.Id, e.DisplayName })
            .FirstOrDefaultAsync(ct);
        if (activeEvent is null) { _log.LogInformation("SpeakerGraphicsReadyJob: no active event."); return; }
        var eventId = activeEvent.Id;

        if (!await _gate.IsFeatureEnabledAsync("speaker-graphics-promote", eventId, ct))
        {
            _log.LogInformation("SpeakerGraphicsReadyJob: feature off for event {EventId}, skipped.", eventId);
            return;
        }

        // Speakers who have at least one RELEASED graphic.
        var speakerIds = await _db.GraphicAssets
            .Where(g => g.EventId == eventId
                        && g.Status == GraphicAssetStatus.Released
                        && g.ParticipantId != null)
            .Select(g => g.ParticipantId!.Value)
            .Distinct()
            .ToListAsync(ct);
        if (speakerIds.Count == 0) { _log.LogInformation("SpeakerGraphicsReadyJob: no released speaker graphics."); return; }

        // Resolve the active speakers + their effective contact email (override wins).
        var speakers = await _db.Participants
            .Where(p => p.EventId == eventId && speakerIds.Contains(p.Id)
                        && p.Role == ParticipantRole.Speaker
                        && p.IsActive && p.LifecycleState == ParticipantLifecycleState.Active)
            .Select(p => new { p.Id, p.Email, p.FullName })
            .ToListAsync(ct);

        var overrides = await _db.SpeakerProfiles
            .Where(sp => sp.EventId == eventId && speakerIds.Contains(sp.ParticipantId)
                         && sp.ContactEmailOverride != null)
            .Select(sp => new { sp.ParticipantId, sp.ContactEmailOverride })
            .ToDictionaryAsync(x => x.ParticipantId, x => x.ContactEmailOverride!, ct);

        var due = new List<ReminderMessage>();
        foreach (var s in speakers)
        {
            var tokens = _templates.NewTokenSet();
            tokens["firstName"] = string.IsNullOrWhiteSpace(s.FullName) ? "there" : s.FullName.Split(' ')[0];
            tokens["eventDisplayName"] = activeEvent.DisplayName;
            var rendered = _templates.Render("speaker-graphics-ready", tokens);
            var deliverTo = overrides.TryGetValue(s.Id, out var ov) && !string.IsNullOrWhiteSpace(ov) ? ov.Trim() : null;
            due.Add(new ReminderMessage(
                RecipientEmail: s.Email,
                ReminderType: "speaker-graphics-ready",
                OccasionKey: $"graphics-ready:{s.Id}",
                Subject: rendered.Subject,
                HtmlBody: rendered.HtmlBody,
                DeliverToEmail: deliverTo,
                ParticipantId: s.Id,
                RecipientName: s.FullName));
        }

        var sent = await _engine.SendDueAsync(eventId, due, ct);
        _log.LogInformation(
            "SpeakerGraphicsReadyJob: {Speakers} speaker(s) with released graphics, {Sent} notified.",
            speakers.Count, sent);

        if (sent > 0)
            await _audit.RecordAsync(new AuditEntry
            {
                EventId = eventId,
                Category = AuditCategory.Engine,
                Action = "speaker-graphics-ready",
                ActorEmail = "system",
                Source = AuditSource.Job,
                Outcome = AuditOutcome.Success,
                Summary = $"Help Promote: notified {sent} speaker(s) their promo graphics are ready.",
            }, ct);
    }
}
