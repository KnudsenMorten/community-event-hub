using CommunityHub.Core.Audit;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Graphics;

/// <summary>
/// The ONE place that mails a speaker "your session graphics are ready" with the
/// <b>Open Help Promote</b> button (template <c>speaker-graphics-ready</c>).
///
/// <para>§436 (operator 2026-07-27: <i>"when it detects, i expect also an email to arrive
/// that the graphics file has been released with a button to Help Promote"</i>). The mail
/// and the button already existed; what did not match was the TIMING — it was sent by a
/// DAILY 08:30 sweep, so a graphic released at 15:00 was announced the next morning. The
/// notification now rides the RELEASE ITSELF, because a released graphic is exactly the
/// moment the speaker can actually do something with it:</para>
/// <list type="bullet">
/// <item>the organizer's <c>/Organizer/Graphics</c> → <b>Release</b> click,</item>
/// <item>the quarter-hourly SharePoint sync, which pulls AND auto-releases (§435) — this
///   is the operator's <i>"when it detects"</i> path,</item>
/// <item>the daily sweep, which stays as the SAFETY NET for anything the two live paths
///   missed (an outage, a release made straight in the database).</item>
/// </list>
///
/// <para><b>Idempotency is load-bearing, and §664 changed WHAT it keys on.</b> Every path goes
/// through the same <see cref="ReminderEngine"/> ledger key, so the three triggers cannot between
/// them send a speaker two mails about the same graphics. The key used to be
/// <c>graphics-ready:{participantId}</c> — no graphic, no session, no date — which meant "this
/// speaker has been told about graphics" <b>permanently</b>. A speaker who gained a SECOND graphic
/// later (a new session, a master class) could never be told about it.</para>
///
/// <para>Operator 2026-07-29: <i>"i did not get any emails when graphics for my test master class
/// was released … i see the SoMe Promote when the file was detected on sharepoint, but did not get
/// email"</i>. Confirmed against PROD: the only <c>speaker-graphics-ready</c> rows in
/// <c>EmailLogs</c> were four, all 28-07 08:30, all successful — one of them his — and NOTHING on
/// the 29th, not even a ring-drop row. The suppression was the ledger, not a gate.</para>
///
/// <para>The key is now <see cref="GraphicsReadyOccasion.KeyFor"/> —
/// <c>graphics-ready:{participantId}:{hash of their released graphic ids}</c> — so re-runs about the
/// SAME graphics still collapse to one mail, while a genuinely new graphic is a new occasion.</para>
///
/// Ring-gated + off by default behind the <c>speaker-graphics-promote</c> feature; the
/// FeatureKey travels on the message so the per-recipient ring gate is this feature's ring
/// and not the transport's (§330).
/// </summary>
public sealed class SpeakerGraphicsReadyNotifier
{
    /// <summary>The feature key that both ENABLES this mail and supplies its ring.</summary>
    public const string FeatureKey = "speaker-graphics-promote";

    private readonly CommunityHubDbContext _db;
    private readonly ReminderEngine _engine;
    private readonly EmailTemplateProvider _templates;
    private readonly FeatureGateService _gate;
    private readonly IAuditTrail _audit;
    private readonly ILogger<SpeakerGraphicsReadyNotifier>? _log;

    public SpeakerGraphicsReadyNotifier(
        CommunityHubDbContext db,
        ReminderEngine engine,
        EmailTemplateProvider templates,
        FeatureGateService gate,
        IAuditTrail audit,
        ILogger<SpeakerGraphicsReadyNotifier>? log = null)
    {
        _db = db;
        _engine = engine;
        _templates = templates;
        _gate = gate;
        _audit = audit;
        _log = log;
    }

    /// <summary>
    /// Notify every speaker who has at least one RELEASED graphic and has not been
    /// notified yet.
    /// <paramref name="onlySpeakerIds"/> NARROWS the sweep to the speakers a specific
    /// release just affected (the organizer click / the sync run) — it is an optimisation,
    /// never a second idempotency rule: an empty (not null) collection means "nobody was
    /// released, do nothing", and null means the full sweep.
    /// Returns the number of mails actually sent.
    /// </summary>
    public async Task<int> NotifyAsync(
        int eventId,
        IReadOnlyCollection<int>? onlySpeakerIds = null,
        CancellationToken ct = default)
    {
        if (onlySpeakerIds is { Count: 0 }) return 0;

        var evt = await _db.Events
            .Where(e => e.Id == eventId)
            .Select(e => new { e.Id, e.DisplayName })
            .FirstOrDefaultAsync(ct);
        if (evt is null)
        {
            _log?.LogInformation("SpeakerGraphicsReady: event {EventId} not found.", eventId);
            return 0;
        }

        if (!await _gate.IsFeatureEnabledAsync(FeatureKey, eventId, ct))
        {
            _log?.LogInformation(
                "SpeakerGraphicsReady: feature off for event {EventId}, skipped.", eventId);
            return 0;
        }

        // Speakers who have at least one RELEASED graphic.
        var q = _db.GraphicAssets
            .Where(g => g.EventId == eventId
                        && g.Status == GraphicAssetStatus.Released
                        && g.ParticipantId != null);
        if (onlySpeakerIds is not null)
        {
            q = q.Where(g => onlySpeakerIds.Contains(g.ParticipantId!.Value));
        }
        // §664 — the released graphic IDS per speaker, not just the speaker ids: the occasion key
        // is built from the set, so a new graphic becomes a new occasion.
        var released = await q
            .Select(g => new { ParticipantId = g.ParticipantId!.Value, g.Id, g.ReleasedAt })
            .ToListAsync(ct);

        var graphicsBySpeaker = released
            .GroupBy(g => g.ParticipantId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToList());

        var speakerIds = graphicsBySpeaker.Keys.ToList();
        if (speakerIds.Count == 0)
        {
            _log?.LogInformation("SpeakerGraphicsReady: no released speaker graphics.");
            return 0;
        }

        // §664 — MUST run before the dedup decision below, or the first run after this change
        // re-announces to everyone who was notified under the old key shape.
        await BackfillLegacyLedgerAsync(eventId, speakerIds, released
            .Select(r => (r.ParticipantId, r.Id, r.ReleasedAt))
            .ToList(), ct);

        // Resolve the ACTIVE speakers + their effective contact email (override wins).
        var speakers = await _db.Participants
            .Where(p => p.EventId == eventId && speakerIds.Contains(p.Id)
                        && p.Role == ParticipantRole.Speaker
                        && p.IsActive && p.LifecycleState == ParticipantLifecycleState.Active)
            .Select(p => new { p.Id, p.Email, p.FullName })
            .ToListAsync(ct);
        if (speakers.Count == 0) return 0;

        var overrides = await _db.SpeakerProfiles
            .Where(sp => sp.EventId == eventId && speakerIds.Contains(sp.ParticipantId)
                         && sp.ContactEmailOverride != null)
            .Select(sp => new { sp.ParticipantId, sp.ContactEmailOverride })
            .ToDictionaryAsync(x => x.ParticipantId, x => x.ContactEmailOverride!, ct);

        var due = new List<ReminderMessage>();
        foreach (var s in speakers)
        {
            // §169: one body per speaker (a known participant) → their personal
            // auto-login magic-link on the {{hubUrl}}/Speaker/Graphics CTA. Fail-safe
            // to the plain hub URL when no magic-link service is wired.
            var tokens = _templates.NewTokenSet(s.Id);
            tokens["firstName"] = string.IsNullOrWhiteSpace(s.FullName) ? "there" : s.FullName.Split(' ')[0];
            tokens["eventDisplayName"] = evt.DisplayName;
            var rendered = _templates.Render("speaker-graphics-ready", tokens);
            var deliverTo = overrides.TryGetValue(s.Id, out var ov) && !string.IsNullOrWhiteSpace(ov)
                ? ov.Trim()
                : null;
            due.Add(new ReminderMessage(
                RecipientEmail: s.Email,
                ReminderType: "speaker-graphics-ready",
                // THE LEDGER KEY (§664) — now the speaker PLUS a hash of their released graphic
                // ids. Same set ⇒ same key, so the release click, the sync run and the daily
                // sweep still collapse to ONE mail. A NEW graphic ⇒ a new set ⇒ a new occasion,
                // which is what the old speaker-only key made impossible.
                OccasionKey: GraphicsReadyOccasion.KeyFor(s.Id, graphicsBySpeaker[s.Id]),
                Subject: rendered.Subject,
                HtmlBody: rendered.HtmlBody,
                DeliverToEmail: deliverTo,
                ParticipantId: s.Id,
                RecipientName: s.FullName,
                // §330: without this the per-recipient ring gate falls back to the
                // TRANSPORT's ring instead of this feature's, so switching the feature on
                // could mail a wider audience than the switch claims.
                FeatureKey: FeatureKey, MailKey: "speaker-graphics-ready"));
        }

        var sent = await _engine.SendDueAsync(eventId, due, ct);
        _log?.LogInformation(
            "SpeakerGraphicsReady: {Speakers} speaker(s) with released graphics, {Sent} notified.",
            speakers.Count, sent);

        if (sent > 0)
        {
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

        return sent;
    }

    /// <summary>
    /// REQUIREMENTS §664 — one-time, IDEMPOTENT migration of the pre-§664 ledger rows.
    ///
    /// <para><b>Why this is needed.</b> The old key was <c>graphics-ready:{participantId}</c>. Under
    /// the new key shape those rows match nothing, so without this every speaker who was already
    /// notified would be announced to again on the very first run — the exact regression the old
    /// code's comment warned about.</para>
    ///
    /// <para>🔒 <b>Why it reconstructs the set rather than stamping the CURRENT one.</b> Stamping
    /// today's graphics as "already told" would suppress precisely the graphics released since the
    /// old notification — i.e. it would re-implement the §664 bug and leave the operator's own
    /// master-class case still broken. Instead the row is keyed on the graphics released AT OR
    /// BEFORE the legacy notification's timestamp, which is what that mail could actually have been
    /// about. Anything released after it is genuinely new and correctly notifies.</para>
    ///
    /// <para>Runs inside the notifier rather than as an EF migration on purpose: it is idempotent
    /// and self-healing, and it is guaranteed to run BEFORE the dedup decision in both hosts,
    /// whatever order they deploy in. A migration could land after a job run.</para>
    /// </summary>
    private async Task BackfillLegacyLedgerAsync(
        int eventId,
        IReadOnlyCollection<int> speakerIds,
        IReadOnlyCollection<(int ParticipantId, int GraphicId, DateTimeOffset? ReleasedAt)> released,
        CancellationToken ct)
    {
        var legacyKeys = speakerIds.Select(GraphicsReadyOccasion.LegacyKeyFor).ToList();

        var legacyRows = await _db.SentReminders
            .Where(r => r.EventId == eventId
                        && r.ReminderType == GraphicsReadyOccasion.ReminderType
                        && legacyKeys.Contains(r.OccasionKey))
            .Select(r => new { r.OccasionKey, r.RecipientEmail, r.SentAt })
            .ToListAsync(ct);
        if (legacyRows.Count == 0) return;

        var added = 0;
        foreach (var speakerId in speakerIds)
        {
            var legacyKey = GraphicsReadyOccasion.LegacyKeyFor(speakerId);
            var legacy = legacyRows.FirstOrDefault(r => r.OccasionKey == legacyKey);
            if (legacy is null) continue;   // never notified under the old shape ⇒ nothing to carry

            // What that mail could have been about: graphics already released when it went out.
            var knownThen = released
                .Where(g => g.ParticipantId == speakerId
                            && g.ReleasedAt is not null
                            && g.ReleasedAt <= legacy.SentAt)
                .Select(g => g.GraphicId)
                .ToList();

            var carriedKey = GraphicsReadyOccasion.KeyFor(speakerId, knownThen);

            // Idempotent: a second run finds the row it wrote and does nothing.
            var exists = await _db.SentReminders.AnyAsync(
                r => r.EventId == eventId
                     && r.ReminderType == GraphicsReadyOccasion.ReminderType
                     && r.OccasionKey == carriedKey, ct);
            if (exists) continue;

            _db.SentReminders.Add(new SentReminder
            {
                EventId = eventId,
                ReminderType = GraphicsReadyOccasion.ReminderType,
                OccasionKey = carriedKey,
                RecipientEmail = legacy.RecipientEmail,
                SentAt = legacy.SentAt,
            });
            added++;
        }

        if (added > 0)
        {
            await _db.SaveChangesAsync(ct);
            _log?.LogInformation(
                "SpeakerGraphicsReady §664: carried {Count} legacy ledger row(s) onto the "
                + "graphic-set key, so nobody already notified is re-announced.", added);
        }
    }
}
