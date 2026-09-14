using System.Globalization;
using CommunityHub.Core.Audit;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations.Sessions;

/// <summary>
/// The DELTA-APPROVAL QUEUE service (REQUIREMENTS §59). Sync engines ENQUEUE detected
/// changes here instead of auto-applying them; the operator approves/rejects each one in
/// <c>/Organizer/SyncQueue</c>. Approving an <see cref="SyncDeltaChangeKind.Update"/>
/// APPLIES the change to CEH (and emails the affected party); approving a
/// <see cref="SyncDeltaChangeKind.Disappeared"/> only ACKNOWLEDGES it (CEH never
/// auto-deletes — §58/§56). Rejecting keeps the current CEH value untouched.
///
/// <para>This is the shared framework: today it wires the §38e session change-detection
/// engine + Sessionize disappearances. Volunteer and CEH→Zoho deltas reuse the same
/// enqueue/approve/reject/notify surface (the apply step is per
/// <see cref="SyncDeltaEntityType"/> + <see cref="SyncDeltaChangeKind"/>, extensible).</para>
///
/// <para>Every mutating operation is recorded through <see cref="IAuditTrail"/> (best-effort,
/// never breaks the action). New pending deltas trigger a THROTTLED ops notification via the
/// ring-exempt <see cref="EngineAlertSender"/> — only when the pending count actually
/// increases, so a quiet re-detection run never spams the operator.</para>
/// </summary>
public sealed class SyncDeltaQueueService
{
    /// <summary>Logical field names used in a Session Update delta's diff list.</summary>
    public const string FieldStartsAt = "StartsAt";
    public const string FieldEndsAt = "EndsAt";
    public const string FieldRoom = "Room";

    private const string EmailCategory = "session-change";
    private const string TemplateName = "session-time-location-changed";

    /// <summary>Logical field names used in a CehToZoho Session/Speaker push Update delta.</summary>
    public const string FieldTitle = "Title";
    public const string FieldAbstract = "Abstract";
    public const string FieldTrack = "Track";
    public const string FieldName = "Name";

    /// <summary>Logical field names used in a §58 ZohoToCeh SPEAKER change-detection Update delta.</summary>
    public const string FieldTagline = "Tagline";
    public const string FieldBio = "Biography";
    public const string FieldCountry = "Country";
    public const string FieldLinkedIn = "LinkedIn";
    public const string FieldTwitter = "Twitter";

    /// <summary>§559 — the dead external id carried by a <see cref="SyncDeltaChangeKind.StaleLink"/>.</summary>
    public const string FieldBackstageId = "BackstageId";

    /// <summary>
    /// §737.2 — the <see cref="SyncDelta.DecidedByEmail"/> marker for a delta that was applied
    /// WITHOUT asking anyone: an inbound (<see cref="SessionSyncDirection.ZohoToCeh"/>) Update,
    /// where Zoho owns the field and the hub is only catching up.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-07-31, naming the exact row: *"2 examples of which should be
    /// auto-approved … Update — Speaker: Thomas Naunheim"*. The row is <b>not hidden</b> — it goes
    /// straight to <see cref="SyncDeltaStatus.Applied"/> carrying this marker, so the queue's
    /// recently-decided list still shows what changed AND that nobody was asked. The notify mail
    /// then stops listing it for free, because that mail lists PENDING only.</para>
    ///
    /// <para>🔒 <b>The policy lives at the DETECTION call sites, never in
    /// <see cref="EnqueueAsync"/>.</b> Auto-applying inside the enqueue helper turned 12 tests red
    /// — not because they were stale, but because they use <c>EnqueueAsync</c> as their ARRANGE
    /// step and then approve/reject/list. <c>EnqueueAsync</c> stays a pure "put this in the queue";
    /// the engine that detected a real inbound diff is what decides not to ask.</para>
    ///
    /// <para>Deletes are NEVER auto-applied (<c>Disappeared</c> / <c>New</c> / <c>StaleLink</c>
    /// still wait for a human — §299 *"CEH never auto-deletes"*), and neither is a
    /// <c>CehToZoho</c> Update: the hub CANNOT apply one, because the Backstage API is create-only.
    /// That is exactly why the ops mail + the manual publish exist (§569/§737).</para>
    /// </remarks>
    public const string AutoDecider = "(auto)";

    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;
    private readonly IAuditTrail? _audit;
    private readonly EngineAlertSender? _alerts;
    private readonly IEmailSender? _sender;
    private readonly IEmailContextAccessor? _context;
    private readonly EmailTemplateProvider? _templates;
    // §57/§58 stage-2 CehToZoho Update apply: on approve, PUSH the current CEH values to Zoho
    // via the existing push services. Optional — a null pair (stage-1 / read-only wiring or a
    // unit test that doesn't exercise the Zoho push) makes the CehToZoho Update apply a no-op
    // that reports "no push service wired" rather than throwing.
    private readonly SessionBackstagePushService? _sessionPush;
    private readonly SpeakerBackstagePushService? _speakerPush;
    private readonly EmailOptions? _emailOptions;

    public SyncDeltaQueueService(
        CommunityHubDbContext db,
        TimeProvider? clock = null,
        IAuditTrail? audit = null,
        EngineAlertSender? alerts = null,
        IEmailSender? sender = null,
        IEmailContextAccessor? context = null,
        EmailTemplateProvider? templates = null,
        SessionBackstagePushService? sessionPush = null,
        SpeakerBackstagePushService? speakerPush = null,
        // §1124 — the shared speaker/session audience.
        Microsoft.Extensions.Options.IOptions<EmailOptions>? emailOptions = null)
    {
        _db = db;
        _clock = clock ?? TimeProvider.System;
        _audit = audit;
        _alerts = alerts;
        _sender = sender;
        _context = context;
        _templates = templates;
        _sessionPush = sessionPush;
        _speakerPush = speakerPush;
        _emailOptions = emailOptions?.Value;
    }

    // -------------------------------------------------------------------------
    // ENQUEUE
    // -------------------------------------------------------------------------

    /// <summary>
    /// Enqueue a detected change. DEDUPE: if a PENDING row already exists for the same
    /// (EventId, EntityType, EntityId, ChangeKind) it is UPDATED in place (refreshed
    /// label + changes + timestamp) rather than duplicated, so a repeated detection run
    /// collapses to one queue item. Returns the (new or refreshed) row.
    /// </summary>
    public async Task<SyncDelta> EnqueueAsync(SyncDelta delta, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(delta);

        var existing = await _db.SyncDeltas.FirstOrDefaultAsync(
            d => d.EventId == delta.EventId
                 && d.Status == SyncDeltaStatus.Pending
                 && d.EntityType == delta.EntityType
                 && d.EntityId == delta.EntityId
                 && d.ChangeKind == delta.ChangeKind,
            ct);

        if (existing is not null)
        {
            // Refresh the pending row with the latest detected state (the newest
            // upstream value is what an Approve should apply).
            existing.EntityLabel = delta.EntityLabel;
            existing.ChangesJson = delta.ChangesJson;
            existing.Source = delta.Source;
            existing.CreatedAt = _clock.GetUtcNow();
            await _db.SaveChangesAsync(ct);
            return existing;
        }

        delta.Status = SyncDeltaStatus.Pending;
        delta.CreatedAt = _clock.GetUtcNow();
        delta.DecidedAt = null;
        delta.DecidedByEmail = null;
        delta.AppliedAt = null;
        _db.SyncDeltas.Add(delta);
        await _db.SaveChangesAsync(ct);
        return delta;
    }

    /// <summary>Convenience enqueue for a SESSION time/location Update delta (§38e).</summary>
    public Task<SyncDelta> EnqueueSessionUpdateAsync(
        int eventId, int sessionId, string title, SessionSyncDirection source,
        IReadOnlyList<SyncFieldChange> changes, CancellationToken ct = default) =>
        EnqueueAsync(new SyncDelta
        {
            EventId = eventId,
            EntityType = SyncDeltaEntityType.Session,
            EntityId = sessionId.ToString(CultureInfo.InvariantCulture),
            EntityLabel = string.IsNullOrWhiteSpace(title) ? "(untitled session)" : title,
            Source = source,
            ChangeKind = SyncDeltaChangeKind.Update,
            Changes = changes,
        }, ct);

    /// <summary>
    /// Convenience enqueue for a VOLUNTEER availability-EDIT delta (§45/§59): a volunteer
    /// who ALREADY submitted availability is CHANGING it. The first-ever submission applies
    /// directly (no queue) — only a later edit is enqueued here. <paramref name="changes"/>
    /// is one <see cref="SyncFieldChange"/> per day whose availability changed, built by
    /// <see cref="BuildVolunteerAvailabilityChanges"/> (NewValue carries the machine payload
    /// an Approve will apply; OldValue/NewValue both carry a human label for the queue UI).
    /// </summary>
    public Task<SyncDelta> EnqueueVolunteerAvailabilityUpdateAsync(
        int eventId, int participantId, string volunteerName,
        IReadOnlyList<SyncFieldChange> changes, CancellationToken ct = default) =>
        EnqueueAsync(new SyncDelta
        {
            EventId = eventId,
            EntityType = SyncDeltaEntityType.Volunteer,
            EntityId = participantId.ToString(CultureInfo.InvariantCulture),
            EntityLabel = string.IsNullOrWhiteSpace(volunteerName) ? "(unnamed volunteer)" : volunteerName,
            // The edit originates inside CEH (the volunteer's own form), not from an
            // external sync; reuse the CehToZoho stage label as "originated in CEH".
            Source = SessionSyncDirection.CehToZoho,
            ChangeKind = SyncDeltaChangeKind.Update,
            Changes = changes,
        }, ct);

    /// <summary>Convenience enqueue for a DISAPPEARED entity (never auto-deleted).</summary>
    public Task<SyncDelta> EnqueueDisappearanceAsync(
        int eventId, SyncDeltaEntityType entityType, string entityId, string label,
        SessionSyncDirection source, CancellationToken ct = default) =>
        EnqueueAsync(new SyncDelta
        {
            EventId = eventId,
            EntityType = entityType,
            EntityId = entityId,
            EntityLabel = string.IsNullOrWhiteSpace(label) ? "(unnamed)" : label,
            Source = source,
            ChangeKind = SyncDeltaChangeKind.Disappeared,
            Changes = Array.Empty<SyncFieldChange>(),
        }, ct);

    /// <summary>
    /// §559 — enqueue a confirmed-STALE external link for approval: CEH holds an id that a COMPLETE
    /// live read says is gone. Approving CLEARS the id so the next pass re-creates the record.
    /// </summary>
    /// <remarks>
    /// <para>Operator: *"you can use this queue for this purpose"*, after *"no clue where to find
    /// the place to approve"*. Until now the §555 detection could only write a mail that admitted
    /// **an approve button is not built yet** — so 9 sessions sat neither updatable nor
    /// re-creatable.</para>
    ///
    /// <para>🔒 The dead id is carried in the change list as <c>BackstageId</c> old → (blank), so
    /// the queue row SHOWS exactly what approving will erase, and the apply can verify it is
    /// clearing the id it was enqueued against rather than whatever the row holds by then.</para>
    /// </remarks>
    public Task<SyncDelta> EnqueueStaleLinkAsync(
        int eventId, SyncDeltaEntityType entityType, string entityId, string label,
        string deadExternalId, SessionSyncDirection source, CancellationToken ct = default) =>
        EnqueueAsync(new SyncDelta
        {
            EventId = eventId,
            EntityType = entityType,
            EntityId = entityId,
            EntityLabel = string.IsNullOrWhiteSpace(label) ? "(unnamed)" : label,
            Source = source,
            ChangeKind = SyncDeltaChangeKind.StaleLink,
            Changes = new[] { new SyncFieldChange(FieldBackstageId, deadExternalId, "") },
        }, ct);

    // -------------------------------------------------------------------------
    // READ
    // -------------------------------------------------------------------------

    /// <summary>All PENDING deltas for an edition, newest first.</summary>
    public async Task<IReadOnlyList<SyncDelta>> ListPendingAsync(int eventId, CancellationToken ct = default) =>
        await _db.SyncDeltas
            .Where(d => d.EventId == eventId && d.Status == SyncDeltaStatus.Pending)
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync(ct);

    /// <summary>The most recently DECIDED deltas for an edition (for the audit section).</summary>
    public async Task<IReadOnlyList<SyncDelta>> ListRecentlyDecidedAsync(
        int eventId, int take = 25, CancellationToken ct = default) =>
        await _db.SyncDeltas
            .Where(d => d.EventId == eventId && d.Status != SyncDeltaStatus.Pending)
            .OrderByDescending(d => d.DecidedAt)
            .Take(take)
            .ToListAsync(ct);

    /// <summary>The current count of pending deltas for an edition.</summary>
    public Task<int> CountPendingAsync(int eventId, CancellationToken ct = default) =>
        _db.SyncDeltas.CountAsync(
            d => d.EventId == eventId && d.Status == SyncDeltaStatus.Pending, ct);

    /// <summary>One delta by id, or null.</summary>
    public Task<SyncDelta?> GetAsync(int id, CancellationToken ct = default) =>
        _db.SyncDeltas.FirstOrDefaultAsync(d => d.Id == id, ct);

    // -------------------------------------------------------------------------
    // DECIDE
    // -------------------------------------------------------------------------

    /// <summary>The outcome of an approve/reject for the page banner + tests.</summary>
    public sealed record DecisionResult(bool Found, bool Applied, bool Emailed, string Message);

    /// <summary>
    /// APPROVE a pending delta: mark Approved, APPLY it (per entity type + change kind),
    /// then — for an applied Update — mark Applied. A Disappeared approval is terminal at
    /// Approved (acknowledged, NEVER deletes). Audited. Idempotent on a non-pending row
    /// (returns Found=false-style no-op).
    /// </summary>
    public async Task<DecisionResult> ApproveAsync(int id, string byEmail, CancellationToken ct = default)
    {
        var delta = await _db.SyncDeltas.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (delta is null || delta.Status != SyncDeltaStatus.Pending)
        {
            return new DecisionResult(false, false, false, "That item is no longer pending.");
        }

        var now = _clock.GetUtcNow();
        delta.Status = SyncDeltaStatus.Approved;
        delta.DecidedAt = now;
        delta.DecidedByEmail = byEmail;

        var (applied, emailed, message) = await ApplyAsync(delta, ct);

        // §559: a StaleLink that actually cleared its id is APPLIED, not merely approved — the write
        // happened, and the audit section must not show it as a bare acknowledgement.
        if (applied && delta.ChangeKind is SyncDeltaChangeKind.Update or SyncDeltaChangeKind.StaleLink)
        {
            delta.Status = SyncDeltaStatus.Applied;
            delta.AppliedAt = now;
        }
        // Disappeared / New stay at Approved (acknowledged; no destructive apply).

        await _db.SaveChangesAsync(ct);

        await AuditAsync(delta, AuditActionApprove, byEmail,
            $"Approved {delta.ChangeKind} {delta.EntityType} '{delta.EntityLabel}'. {message}", ct);

        return new DecisionResult(true, applied, emailed, message);
    }

    /// <summary>
    /// REJECT a pending delta: mark Rejected with the operator's reason. The current CEH
    /// value is kept untouched (nothing applied, nothing deleted). Audited.
    /// </summary>
    public async Task<DecisionResult> RejectAsync(
        int id, string byEmail, string? reason, CancellationToken ct = default)
    {
        var delta = await _db.SyncDeltas.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (delta is null || delta.Status != SyncDeltaStatus.Pending)
        {
            return new DecisionResult(false, false, false, "That item is no longer pending.");
        }

        delta.Status = SyncDeltaStatus.Rejected;
        delta.DecidedAt = _clock.GetUtcNow();
        delta.DecidedByEmail = byEmail;
        delta.Notes = string.IsNullOrWhiteSpace(reason) ? delta.Notes : reason.Trim();
        await _db.SaveChangesAsync(ct);

        await AuditAsync(delta, AuditActionReject, byEmail,
            $"Rejected {delta.ChangeKind} {delta.EntityType} '{delta.EntityLabel}'"
            + (string.IsNullOrWhiteSpace(reason) ? "." : $": {reason!.Trim()}"), ct);

        return new DecisionResult(true, false, false, "Change rejected — the current value is kept.");
    }

    // -------------------------------------------------------------------------
    // NOTIFY
    // -------------------------------------------------------------------------

    /// <summary>
    /// Notify the operator that NEW pending deltas need approval — ONLY when the pending
    /// count has increased since <paramref name="previousPendingCount"/> (so a re-detection
    /// run that finds nothing new never emails). The mail lists the items and links to
    /// <c>/Organizer/SyncQueue</c>; it goes through the ring-exempt
    /// <see cref="EngineAlertSender"/> (the ops mailbox is not a ring-gated participant) and
    /// is throttled per edition. Best-effort: never throws. Returns true if it emailed.
    /// </summary>
    public async Task<bool> NotifyNewAsync(
        int eventId, int previousPendingCount, CancellationToken ct = default)
    {
        if (_alerts is null) return false;

        var pending = await ListPendingAsync(eventId, ct);
        if (pending.Count <= previousPendingCount) return false; // nothing newly pending

        // §108: show the edition Code/DisplayName, never the raw numeric event id.
        var ev = await _db.Events
            .Where(e => e.Id == eventId)
            .Select(e => new { e.Code, e.DisplayName })
            .FirstOrDefaultAsync(ct);
        var label =
            !string.IsNullOrWhiteSpace(ev?.Code) ? ev!.Code
            : !string.IsNullOrWhiteSpace(ev?.DisplayName) ? ev!.DisplayName
            : $"event {eventId}";

        var subject =
            $"[CEH] {pending.Count} sync change(s) need your approval ({label})";
        var html = BuildNotifyHtml(eventId, pending);

        // Throttle per edition so a still-pending set won't email on every detection tick.
        // §752.9 — DEV-silent: an approval queue over test sync deltas asks for work that does not
        // exist. The queue itself still fills and is still visible in the organiser UI either way —
        // what is suppressed is the mail, not the state (§707.42's rule).
        // 🔴 §1124 — THIS USED TO GO TO mok@ ALONE, and that was the misroute.
        //
        // Operator 2026-08-25: *"add info@expertslive.dk to the missing one as well and remove
        // mok@expertslive.dk. make it consitent"*.
        //
        // 🔑 It passed no `recipient`, so it fell through to EngineAlertSender's DEFAULT — the
        // DEVELOPER mailbox. But this is the literal pending-task list for speakers and sessions:
        // "N sync change(s) need your approval". §493 reserves mok@ for SYSTEM alerts, and §874
        // already settled the principle for the held-speaker mail — *"one person on holiday must not
        // stall a speaker's Zoho flow"*. A queue of approvals routed to one person is exactly that
        // failure, and it was invisible because nothing was broken: the mail sent fine, to the wrong
        // audience.
        await _alerts.AlertToAsync(
            _emailOptions?.SpeakerSessionRecipients(),
            subject, html, ct, throttleKey: $"sync-queue-{eventId}",
            devSilent: true);
        return true;
    }

    /// <summary>
    /// §737 — the ABSOLUTE queue URL for the mail, from the edition's configured hub origin.
    /// </summary>
    /// <remarks>
    /// 🔴 <b>A relative href in an e-mail is a broken link.</b> This said
    /// <c>href="/Organizer/SyncQueue"</c>, which has no base once it leaves the app: Brevo's click
    /// tracker resolved it against its OWN domain and the operator landed on Brevo's *"Page not
    /// found"* (2026-07-31). The sibling notice in <c>SessionBackstagePushService</c> already used
    /// an absolute URL — the two had simply drifted.
    ///
    /// <para>Read from <c>hubUrl</c> (the per-edition <c>EmailTemplateOptions.HubUrl</c>) rather
    /// than hard-coded, so this cannot rot on the next edition — the evergreen rule. With no origin
    /// configured we emit PLAIN TEXT instead of a link: a dead link is worse than none, because it
    /// tells the reader the page does not exist.</para>
    /// </remarks>
    private string QueueLinkHtml()
    {
        string? origin = null;
        try
        {
            if (_templates?.NewTokenSet().TryGetValue("hubUrl", out var h) == true
                && !string.IsNullOrWhiteSpace(h))
            {
                origin = h.TrimEnd('/');
            }
        }
        catch { /* token-set failure must never break the notice */ }

        return origin is null
            ? "<p>Open <strong>Organizer &rarr; Sync Approval Queue</strong> in the hub to decide.</p>"
            : $"<p><a href=\"{origin}/Organizer/SyncQueue\">Open the sync approval queue</a></p>";
    }

    private string BuildNotifyHtml(int eventId, IReadOnlyList<SyncDelta> pending)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(
            "<p>The following sync changes were detected and are waiting for your "
            + "<strong>approval</strong>. They have <strong>NOT</strong> been applied yet — "
            + "approve or reject each one in the queue.</p><ul>");
        foreach (var d in pending.Take(20))
        {
            sb.Append(
                $"<li>{Enc(d.ChangeKind.ToString())} — {Enc(d.EntityType.ToString())}: "
                + $"<strong>{Enc(d.EntityLabel)}</strong></li>");
        }
        sb.Append("</ul>");
        if (pending.Count > 20) sb.Append($"<p>…and {pending.Count - 20} more.</p>");
        sb.Append(QueueLinkHtml());   // §737 — absolute, or plain text; never a relative href
        // §737.2: inbound (Zoho→CEH) UPDATES now auto-apply for sessions AND speakers — Zoho owns
        // those fields, so there is nothing to decide. What reaches this mail is what genuinely
        // needs you: outbound changes (a human must make them in Backstage) and anything that
        // would remove something.
        sb.Append("<p>(Zoho&rarr;CEH <em>updates</em> auto-apply — Zoho owns those fields (§299"
            + " OPEN-28, §737.2). What waits here is what CEH cannot apply for you: changes that"
            + " must be made manually in Zoho, and anything that would remove something — CEH"
            + " never auto-deletes.)</p>");
        return sb.ToString();
    }

    // -------------------------------------------------------------------------
    // APPLY (per EntityType + ChangeKind — extensible)
    // -------------------------------------------------------------------------

    private const string AuditActionApprove = "syncqueue.approve";
    private const string AuditActionReject = "syncqueue.reject";

    /// <summary>
    /// Apply an approved delta. Returns (applied, emailed, message). Dispatches on
    /// (EntityType, ChangeKind); add Speaker/Volunteer arms here later. A Disappeared item
    /// applies NOTHING (acknowledge only — never deletes).
    /// </summary>
    private async Task<(bool Applied, bool Emailed, string Message)> ApplyAsync(
        SyncDelta delta, CancellationToken ct)
    {
        if (delta.ChangeKind == SyncDeltaChangeKind.Disappeared)
        {
            // §58/§56: acknowledge only. Deletion stays manual.
            return (false, false, "Acknowledged — CEH never auto-deletes; remove it manually if appropriate.");
        }

        // A Session/Speaker Update is disambiguated by SOURCE:
        //  • CehToZoho (§57/§58 stage-2 push): on approve, PUSH the current CEH values TO Zoho.
        //  • ZohoToCeh (§38e change-detection): on approve, WRITE the upstream values TO CEH.
        // The Source decides the apply DIRECTION for the same (EntityType, ChangeKind) pair.
        return delta.EntityType switch
        {
            SyncDeltaEntityType.Session when delta.ChangeKind == SyncDeltaChangeKind.Update
                                             && delta.Source == SessionSyncDirection.CehToZoho
                => await ApplySessionPushToZohoAsync(delta, ct),
            SyncDeltaEntityType.Speaker when delta.ChangeKind == SyncDeltaChangeKind.Update
                                             && delta.Source == SessionSyncDirection.CehToZoho
                => await ApplySpeakerPushToZohoAsync(delta, ct),
            SyncDeltaEntityType.Speaker when delta.ChangeKind == SyncDeltaChangeKind.Update
                                             && delta.Source == SessionSyncDirection.ZohoToCeh
                => await ApplySpeakerUpdateFromZohoAsync(delta, ct),
            SyncDeltaEntityType.Session when delta.ChangeKind == SyncDeltaChangeKind.Update
                => await ApplySessionUpdateAsync(delta, ct),
            SyncDeltaEntityType.Volunteer when delta.ChangeKind == SyncDeltaChangeKind.Update
                => await ApplyVolunteerAvailabilityUpdateAsync(delta, ct),
            // §559 — clearing a dead link. The ONE irreversible act this queue performs.
            SyncDeltaEntityType.Session when delta.ChangeKind == SyncDeltaChangeKind.StaleLink
                => await ApplySessionStaleLinkAsync(delta, ct),
            _ => (false, false, $"No apply handler for {delta.EntityType}/{delta.ChangeKind} yet."),
        };
    }

    /// <summary>
    /// §559 — APPROVE a stale session link: clear the dead <c>BackstageSessionId</c> so the next
    /// push pass CREATES the session again.
    /// </summary>
    /// <remarks>
    /// <para>Operator: *"i can delete a session and we can resync after you fix it"* and *"reset
    /// zoho field for the deleted sessions so it recreates again, as the sync approver is still not
    /// build"* — he was doing this by hand because the button did not exist.</para>
    ///
    /// <para>🔒 <b>Approving is a CREATE, and it cannot be undone.</b> The Backstage sessions API
    /// has no delete, so a wrong clear duplicates the live agenda permanently. Two guards, both
    /// pinned by test:</para>
    /// <list type="number">
    /// <item><b>Clear ONLY the id this row was raised against.</b> If the session has since been
    /// re-linked to a DIFFERENT id, that id is live — erasing it would duplicate a session that is
    /// perfectly fine. Time passes between detection and approval, and §553's whole lesson is that
    /// a stale read must never drive a write.</item>
    /// <item><b>Already cleared ⇒ report success, change nothing.</b> He may well have done it by
    /// hand (he has, repeatedly). That is the desired end state, so the row closes cleanly instead
    /// of failing and leaving him to wonder which of the two acted.</item>
    /// </list>
    /// </remarks>
    private async Task<(bool Applied, bool Emailed, string Message)> ApplySessionStaleLinkAsync(
        SyncDelta delta, CancellationToken ct)
    {
        if (!int.TryParse(delta.EntityId, out var sessionId))
            return (false, false, $"'{delta.EntityId}' is not a session id.");

        var session = await _db.Sessions.FirstOrDefaultAsync(
            s => s.Id == sessionId && s.EventId == delta.EventId, ct);
        if (session is null)
            return (false, false, "That session no longer exists in CEH.");

        var deadId = delta.Changes.FirstOrDefault(c => c.Field == FieldBackstageId)?.OldValue;
        if (string.IsNullOrWhiteSpace(deadId))
            return (false, false, "This item does not record which Backstage id was dead, so nothing was cleared.");

        if (string.IsNullOrWhiteSpace(session.BackstageSessionId))
        {
            return (true, false,
                "The link was already cleared, so nothing needed changing — the session will be "
                + "re-created on the next push.");
        }

        // 🔒 Guard 1: a DIFFERENT id means it has been re-linked since this row was raised. That id
        // is live; clearing it would duplicate a healthy session in the agenda, permanently.
        if (!string.Equals(session.BackstageSessionId, deadId, StringComparison.OrdinalIgnoreCase))
        {
            return (false, false,
                $"This session now points at a DIFFERENT Backstage id ({session.BackstageSessionId}) "
                + $"than the dead one this item was raised for ({deadId}). Nothing was cleared — "
                + "re-creating it could duplicate a session that is currently fine.");
        }

        session.BackstageSessionId = null;
        await _db.SaveChangesAsync(ct);

        return (true, false,
            $"Cleared the dead Backstage link ({deadId}). The session will be CREATED in Backstage "
            + "on the next push pass.");
    }

    /// <summary>
    /// Apply an approved CehToZoho SESSION Update (§57 stage-2, §59): PUSH the session's current
    /// CEH values to Zoho via the existing push service. Distinct from the §38e ZohoToCeh arm
    /// (which writes the other direction). No-op with a clear message when no push service is
    /// wired (stage-1 / read-only configuration).
    /// </summary>
    private async Task<(bool Applied, bool Emailed, string Message)> ApplySessionPushToZohoAsync(
        SyncDelta delta, CancellationToken ct)
    {
        if (_sessionPush is null)
            return (false, false, "No session push service wired — cannot push to Zoho.");
        if (!int.TryParse(delta.EntityId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sessionId))
        {
            delta.Notes = "Apply failed: unparseable session id.";
            return (false, false, "Could not parse the session id.");
        }

        var (ok, message) = await _sessionPush.UpdateLinkedSessionAsync(delta.EventId, sessionId, ct);
        if (!ok) delta.Notes = message;
        return (ok, false, message);
    }

    /// <summary>
    /// Apply an approved CehToZoho SPEAKER Update (§58 stage-2, §59): push to Zoho via the
    /// speaker push service. The Backstage speaker API is create-only, so this acknowledges +
    /// reports that the in-place edit is manual (the push service returns that message). No-op
    /// with a clear message when no push service is wired.
    /// </summary>
    private async Task<(bool Applied, bool Emailed, string Message)> ApplySpeakerPushToZohoAsync(
        SyncDelta delta, CancellationToken ct)
    {
        if (_speakerPush is null)
            return (false, false, "No speaker push service wired — cannot push to Zoho.");
        if (!int.TryParse(delta.EntityId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var participantId))
        {
            delta.Notes = "Apply failed: unparseable speaker id.";
            return (false, false, "Could not parse the speaker id.");
        }

        var (ok, message) = await _speakerPush.UpdateLinkedSpeakerAsync(delta.EventId, participantId, ct);
        if (!ok) delta.Notes = message;
        return (ok, false, message);
    }

    /// <summary>
    /// Apply an approved §58 ZohoToCeh SPEAKER Update (REQUIREMENTS §38e/§58, §59): WRITE the
    /// upstream Zoho values (name/tagline/bio/country/linkedin/twitter, each carried in the
    /// delta's <see cref="SyncFieldChange.NewValue"/>) to the CEH <see cref="SpeakerProfile"/>,
    /// and refresh the stored <c>Backstage*</c> baseline so the next detection pass diffs
    /// against the now-applied value. DISTINCT from <see cref="ApplySpeakerPushToZohoAsync"/>
    /// (the CehToZoho push arm) — disambiguated by <c>delta.Source == ZohoToCeh</c>. The CEH
    /// name lives on the <see cref="Participant"/> (FullName) + the profile's First/LastName;
    /// only the profile bio fields are CEH-owned here, so the name is applied to the profile
    /// First/Last split (best-effort) and recorded on the Backstage* baseline. NEVER deletes.
    /// </summary>
    private async Task<(bool Applied, bool Emailed, string Message)> ApplySpeakerUpdateFromZohoAsync(
        SyncDelta delta, CancellationToken ct)
    {
        if (!int.TryParse(delta.EntityId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var participantId))
        {
            delta.Notes = "Apply failed: unparseable speaker id.";
            return (false, false, "Could not parse the speaker id.");
        }

        var profile = await _db.SpeakerProfiles
            .FirstOrDefaultAsync(p => p.ParticipantId == participantId && p.EventId == delta.EventId, ct);
        if (profile is null)
        {
            delta.Notes = "Apply failed: speaker not found in this edition.";
            return (false, false, "The speaker no longer exists in this edition.");
        }

        var changes = delta.Changes;

        // Each field is applied only when the delta carries it (a missing field keeps the
        // current CEH value — blank-keeps semantics matched to ApplyStringField).
        var newName = ApplyStringField(changes, FieldName, profile.BackstageName);
        var newTagline = ApplyStringField(changes, FieldTagline, profile.Tagline);
        var newBio = ApplyStringField(changes, FieldBio, profile.Biography);
        var newCountry = ApplyStringField(changes, FieldCountry, profile.Country);
        var newLinkedIn = ApplyStringField(changes, FieldLinkedIn, profile.LinkedIn);
        var newTwitter = ApplyStringField(changes, FieldTwitter, profile.Twitter);

        // Write the upstream values to the CEH speaker profile (the CEH-owned bio fields).
        profile.Tagline = newTagline;
        profile.Biography = newBio;
        profile.Country = newCountry;
        profile.LinkedIn = newLinkedIn;
        profile.Twitter = newTwitter;
        // Name: split the Zoho display name into First/Last (best-effort) when it changed.
        if (Changed(changes, FieldName) && !string.IsNullOrWhiteSpace(newName))
        {
            var (first, last) = SplitName(newName!);
            profile.FirstName = first;
            profile.LastName = last;
        }

        // Refresh the last-known Backstage* baseline so the next detection pass starts from
        // the applied value (no immediate re-detection of the same change).
        profile.BackstageName = newName;
        profile.BackstageTagline = newTagline;
        profile.BackstageBio = newBio;
        profile.BackstageCountry = newCountry;
        profile.BackstageLinkedIn = newLinkedIn;
        profile.BackstageTwitter = newTwitter;
        profile.BackstageChangeCheckedAt = _clock.GetUtcNow();
        profile.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);

        return (true, false, "Applied the Zoho speaker change to the CEH speaker profile.");
    }

    private static bool Changed(IReadOnlyList<SyncFieldChange> changes, string field) =>
        changes.Any(c => string.Equals(c.Field, field, StringComparison.OrdinalIgnoreCase));

    /// <summary>Split a display name into (first, last): everything before the LAST space is
    /// the first name(s), the final token is the last name. A single token → first only.</summary>
    private static (string First, string? Last) SplitName(string name)
    {
        var n = name.Trim();
        var i = n.LastIndexOf(' ');
        return i <= 0 ? (n, null) : (n[..i].Trim(), n[(i + 1)..].Trim());
    }

    /// <summary>
    /// Build the {Field, Old, New} diff list for a §58 ZohoToCeh SPEAKER change — only fields
    /// that actually differ between the stored CEH baseline (old) and the current Zoho values
    /// (new) are included. Shared by the detection engine + its tests so the enqueue + apply
    /// paths use the same field tokens.
    /// </summary>
    public static IReadOnlyList<SyncFieldChange> BuildSpeakerZohoChanges(
        string? oldName, string? oldTagline, string? oldBio, string? oldCountry, string? oldLinkedIn, string? oldTwitter,
        string? newName, string? newTagline, string? newBio, string? newCountry, string? newLinkedIn, string? newTwitter)
    {
        var list = new List<SyncFieldChange>();
        void Add(string field, string? oldV, string? newV)
        {
            if (!TextEquals(oldV, newV)) list.Add(new SyncFieldChange(field, oldV, newV));
        }
        Add(FieldName, oldName, newName);
        Add(FieldTagline, oldTagline, newTagline);
        Add(FieldBio, oldBio, newBio);
        Add(FieldCountry, oldCountry, newCountry);
        Add(FieldLinkedIn, oldLinkedIn, newLinkedIn);
        Add(FieldTwitter, oldTwitter, newTwitter);
        return list;
    }

    /// <summary>Trim + case-insensitive equality treating null/blank as equal (the
    /// change-detection comparison rule for speaker text fields).</summary>
    public static bool TextEquals(string? a, string? b) =>
        string.Equals(
            string.IsNullOrWhiteSpace(a) ? null : a.Trim(),
            string.IsNullOrWhiteSpace(b) ? null : b.Trim(),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Apply a SESSION Update delta: write the new time/location to the CEH session's stored
    /// <c>Backstage*</c> snapshot fields, then email the affected speaker(s) via the existing
    /// session-change template.
    /// </summary>
    private async Task<(bool Applied, bool Emailed, string Message)> ApplySessionUpdateAsync(
        SyncDelta delta, CancellationToken ct)
    {
        if (!int.TryParse(delta.EntityId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sessionId))
        {
            delta.Notes = "Apply failed: unparseable session id.";
            return (false, false, "Could not parse the session id.");
        }

        var session = await _db.Sessions
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.EventId == delta.EventId, ct);
        if (session is null)
        {
            delta.Notes = "Apply failed: session not found in this edition.";
            return (false, false, "The session no longer exists in this edition.");
        }

        var changes = delta.Changes;
        var oldStart = session.BackstageStartsAt;
        var oldEnd = session.BackstageEndsAt;
        var oldRoom = session.BackstageRoom;

        var newStart = ApplyDateField(changes, FieldStartsAt, session.BackstageStartsAt);
        var newEnd = ApplyDateField(changes, FieldEndsAt, session.BackstageEndsAt);
        var newRoom = ApplyStringField(changes, FieldRoom, session.BackstageRoom);

        var timeChanged = newStart != oldStart || newEnd != oldEnd;
        var roomChanged = !RoomEquals(newRoom, oldRoom);

        session.BackstageStartsAt = newStart;
        session.BackstageEndsAt = newEnd;
        session.BackstageRoom = newRoom;
        session.BackstageChangeCheckedAt = _clock.GetUtcNow();

        // §88: at stage 3 (Zoho→CEH) Zoho Backstage is the SOURCE of the schedule, and the
        // hub (My Sessions, the public session page, .ics) always READS the CEH display
        // fields (StartsAt/EndsAt/Room). So an approved Zoho change must land on those
        // display fields too — not only the Backstage* change-tracking snapshot — otherwise
        // the speaker is emailed a new time the hub never shows. UpdatedAt records the write.
        session.StartsAt = newStart;
        session.EndsAt = newEnd;
        session.Room = newRoom;
        session.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);

        // Email each speaker on the session. This is the same template the §38e engine used
        // to send inline — it now sends on APPROVE instead.
        // 🔴 §1001 — THE QUIET PERIOD. Before the configured date a schedule change is APPLIED but
        // NOT announced. Operator 2026-08-09: *"we make lots of schedule changes and we dont want
        // to make unnessary noice to speakers."*
        //
        // 🔒 A missing settings row means SILENT, not "notify" — a row nobody has saved must never
        // mail every speaker on the first agenda edit, which is the noise this removes.
        var noticeSetting = await _db.SessionSourceSettings
            .FirstOrDefaultAsync(s => s.EventId == delta.EventId, ct);
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        var mayNotify = noticeSetting?.MayNotifySpeakers(today) == true;

        var emailedCount = 0;
        if (_sender is not null && (timeChanged || roomChanged) && mayNotify)
        {
            var speakers = await _db.SessionSpeakers
                .Where(ss => ss.SessionId == session.Id)
                .Select(ss => new { ss.ParticipantId, ss.Participant.Email, ss.Participant.FullName })
                .ToListAsync(ct);

            foreach (var sp in speakers)
            {
                if (string.IsNullOrWhiteSpace(sp.Email)) continue;
                await SendChangeEmailAsync(
                    delta.EventId, sp.ParticipantId, sp.Email, sp.FullName, session.Title,
                    oldStart, oldEnd, oldRoom, newStart, newEnd, newRoom,
                    timeChanged, roomChanged, ct);
                emailedCount++;
            }
        }

        // §1001 — say WHY nobody was mailed. "0 emails sent" on a real change reads as a fault; the
        // quiet period is a decision, and the message names the date it ends so it is checkable.
        var msg = (timeChanged || roomChanged) && !mayNotify
            ? "Applied to the session; speakers NOT notified — "
              + (noticeSetting?.SpeakerScheduleNoticeFrom is { } from
                  ? $"schedule notices begin {from:d MMM yyyy} (§1001 quiet period)."
                  : "no notice-start date is set, so speaker notices are off (§1001).")
            : $"Applied to the session; {emailedCount} speaker email(s) sent.";
        return (true, emailedCount > 0, msg);
    }

    // -------------------------------------------------------------------------
    // VOLUNTEER availability edit (§45/§59)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Separator inside a Volunteer day change's <see cref="SyncFieldChange.NewValue"/>: the
    /// human label the queue shows, then the machine payload an Approve applies. ASCII unit
    /// separator (0x1F) — never appears in a label or a note.
    /// </summary>
    private const char VolUnitSep = '\u001F';

    /// <summary>
    /// Encode one day's NEW availability for a Volunteer delta: a human label (for the queue
    /// UI) plus the machine payload (Level + Note) that <see cref="ApplyVolunteerAvailabilityUpdateAsync"/>
    /// writes back on approve. Decoded by <see cref="DecodeVolunteerNew"/>.
    /// </summary>
    public static string EncodeVolunteerNew(string humanLabel, VolunteerAvailabilityLevel level, string? note)
        => $"{humanLabel}{VolUnitSep}{(int)level}{VolUnitSep}{note}";

    /// <summary>
    /// Decode a value produced by <see cref="EncodeVolunteerNew"/> into (Level, Note). Returns
    /// null if the value carries no machine payload (e.g. a plain display string) — apply then
    /// skips that field rather than corrupting the row.
    /// </summary>
    private static (VolunteerAvailabilityLevel Level, string? Note)? DecodeVolunteerNew(string? newValue)
    {
        if (string.IsNullOrEmpty(newValue)) return null;
        var parts = newValue.Split(VolUnitSep);
        if (parts.Length < 3) return null;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var lvl))
            return null;
        var note = parts[2];
        return ((VolunteerAvailabilityLevel)lvl, string.IsNullOrEmpty(note) ? null : note);
    }

    /// <summary>The human label half of an encoded Volunteer day value (for the queue UI).</summary>
    public static string VolunteerDisplay(string? encoded)
    {
        if (string.IsNullOrEmpty(encoded)) return string.Empty;
        var i = encoded.IndexOf(VolUnitSep);
        return i < 0 ? encoded : encoded[..i];
    }

    /// <summary>
    /// Build the {Field=day, OldValue=old label, NewValue=encoded new} diff list for a volunteer
    /// availability EDIT — one entry per day whose (Level, Note) actually changed. Each
    /// <paramref name="rows"/> tuple is one event day: its key (<c>yyyy-MM-dd</c>), a human day
    /// label, the old + new display labels, the new Level, and the new Note. Only changed days
    /// are included; an empty result means "nothing changed" (caller skips enqueue + applies
    /// nothing).
    /// </summary>
    public static IReadOnlyList<SyncFieldChange> BuildVolunteerAvailabilityChanges(
        IEnumerable<(string DayKey, string OldLabel, string NewLabel, VolunteerAvailabilityLevel NewLevel, string? NewNote)> rows)
    {
        var list = new List<SyncFieldChange>();
        foreach (var r in rows)
        {
            list.Add(new SyncFieldChange(
                r.DayKey,
                r.OldLabel,
                EncodeVolunteerNew(r.NewLabel, r.NewLevel, r.NewNote)));
        }
        return list;
    }

    /// <summary>
    /// Apply an approved VOLUNTEER availability edit: write each queued day's new (Level, Note)
    /// to the volunteer's <see cref="VolunteerDayAvailability"/> row (upsert by (event,
    /// participant, day)). Idempotent; NEVER deletes a volunteer or a day row.
    /// </summary>
    private async Task<(bool Applied, bool Emailed, string Message)> ApplyVolunteerAvailabilityUpdateAsync(
        SyncDelta delta, CancellationToken ct)
    {
        if (!int.TryParse(delta.EntityId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var participantId))
        {
            delta.Notes = "Apply failed: unparseable volunteer id.";
            return (false, false, "Could not parse the volunteer id.");
        }

        var existing = await _db.VolunteerDayAvailabilities
            .Where(x => x.EventId == delta.EventId && x.ParticipantId == participantId)
            .ToListAsync(ct);

        var applied = 0;
        foreach (var c in delta.Changes)
        {
            if (!DateOnly.TryParse(c.Field, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                continue;
            var decoded = DecodeVolunteerNew(c.NewValue);
            if (decoded is null) continue;

            var row = existing.FirstOrDefault(x => x.Day == day);
            if (row is null)
            {
                _db.VolunteerDayAvailabilities.Add(new VolunteerDayAvailability
                {
                    EventId = delta.EventId,
                    ParticipantId = participantId,
                    Day = day,
                    Level = decoded.Value.Level,
                    Note = decoded.Value.Note,
                    UpdatedAt = _clock.GetUtcNow(),
                });
            }
            else
            {
                row.Level = decoded.Value.Level;
                row.Note = decoded.Value.Note;
                row.UpdatedAt = _clock.GetUtcNow();
            }
            applied++;
        }

        await _db.SaveChangesAsync(ct);
        return (true, false, $"Applied the volunteer's availability change ({applied} day(s) updated).");
    }

    private static DateTimeOffset? ApplyDateField(
        IReadOnlyList<SyncFieldChange> changes, string field, DateTimeOffset? current)
    {
        var c = changes.FirstOrDefault(x => string.Equals(x.Field, field, StringComparison.OrdinalIgnoreCase));
        if (c is null) return current; // field not part of this delta — keep current
        if (string.IsNullOrWhiteSpace(c.NewValue)) return null;
        return DateTimeOffset.TryParse(
            c.NewValue, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)
            ? dt : current;
    }

    private static string? ApplyStringField(
        IReadOnlyList<SyncFieldChange> changes, string field, string? current)
    {
        var c = changes.FirstOrDefault(x => string.Equals(x.Field, field, StringComparison.OrdinalIgnoreCase));
        if (c is null) return current;
        return string.IsNullOrWhiteSpace(c.NewValue) ? null : c.NewValue;
    }

    private static bool RoomEquals(string? a, string? b) =>
        string.Equals(
            string.IsNullOrWhiteSpace(a) ? null : a.Trim(),
            string.IsNullOrWhiteSpace(b) ? null : b.Trim(),
            StringComparison.OrdinalIgnoreCase);

    // -------------------------------------------------------------------------
    // EMAIL (mirrors SessionChangeDetectionService.SendChangeEmailAsync)
    // -------------------------------------------------------------------------

    private async Task SendChangeEmailAsync(
        int eventId, int participantId, string email, string fullName, string title,
        DateTimeOffset? oldStart, DateTimeOffset? oldEnd, string? oldRoom,
        DateTimeOffset? newStart, DateTimeOffset? newEnd, string? newRoom,
        bool timeChanged, bool roomChanged, CancellationToken ct)
    {
        if (_sender is null) return;

        var firstName = FirstName(fullName);
        // §83: a When/Where cell must never render BLANK — fall back to a clear "TBD"
        // placeholder when a time/room is not set, so the "schedule changed" email always
        // shows something sensible in both cells.
        var oldTime = FormatRange(oldStart, oldEnd);
        var newTime = FormatRange(newStart, newEnd);
        var oldRoomText = string.IsNullOrWhiteSpace(oldRoom) ? "TBD" : oldRoom!;
        var newRoomText = string.IsNullOrWhiteSpace(newRoom) ? "TBD" : newRoom!;

        // This IS a participant email — keep it ring-gated (NOT RingExempt). Tag it with the
        // §38e feature key so the sender re-checks the recipient ring as a backstop.
        // §707.2b — the mail identity (the same const the renderer uses below), so this resolves its own
        // (mail × role) ring rather than the session-change-alerts FEATURE ring. `EmailCategory` stays
        // the ledger category; the two are different things.
        var scope = _context?.Set(new EmailContext(
            EmailCategory, eventId, null, fullName,
            TemplateName: TemplateName,
            FeatureKey: SessionChangeDetectionService.FeatureKey));
        try
        {
            if (_templates is not null)
            {
                // §169: the recipient IS the session's speaker Participant — pass their id
                // so the {{hubUrl}} CTA is their personal /go/{token} auto-login magic-link
                // (fail-safe: no participant / any error ⇒ plain hub URL, never throws).
                var tokens = _templates.NewTokenSet(participantId);
                tokens["firstName"] = firstName;
                tokens["sessionTitle"] = title;
                tokens["oldTime"] = oldTime;
                tokens["newTime"] = newTime;
                tokens["oldRoom"] = oldRoomText;
                tokens["newRoom"] = newRoomText;
                tokens["timeChanged"] = timeChanged ? "yes" : "no";
                tokens["roomChanged"] = roomChanged ? "yes" : "no";
                // 🔴 §997 — the table body, built here so ONLY the changed rows appear. The
                // template used to render both rows unconditionally, printing an unchanged room
                // struck through and repeated. brandColor is read back from the token set because
                // the renderer is single-pass (a {{token}} inside a value stays literal).
                tokens["changeRowsHtml"] = BuildChangeRowsHtml(
                    timeChanged, oldTime, newTime, roomChanged, oldRoomText, newRoomText,
                    tokens.TryGetValue("brandColor", out var bc) && !string.IsNullOrWhiteSpace(bc)
                        ? bc! : "#1565c0");
                var rendered = _templates.Render(TemplateName, tokens);
                await _sender.SendAsync(email, rendered.Subject, rendered.HtmlBody, ct);
            }
            else
            {
                var subject = $"Your session schedule changed: {title}";
                var html =
                    $"<p>Hi {Enc(firstName)},</p>" +
                    $"<p>The schedule for your session <strong>{Enc(title)}</strong> has changed:</p>" +
                    "<ul>" +
                    (timeChanged
                        ? $"<li>Time: <s>{Enc(oldTime)}</s> &rarr; <strong>{Enc(newTime)}</strong></li>"
                        : "") +
                    (roomChanged
                        ? $"<li>Location: <s>{Enc(oldRoomText)}</s> &rarr; <strong>{Enc(newRoomText)}</strong></li>"
                        : "") +
                    "</ul>" +
                    "<p>Please check your speaker hub for the latest details.</p>" +
                    "<p>The team</p>";
                await _sender.SendAsync(email, subject, html, ct);
            }
        }
        finally
        {
            scope?.Dispose();
        }
    }

    private async Task AuditAsync(SyncDelta delta, string action, string byEmail, string summary, CancellationToken ct)
    {
        if (_audit is null) return;
        await _audit.RecordAsync(new AuditEntry
        {
            EventId = delta.EventId,
            Category = AuditCategory.Admin,
            Action = action,
            ActorEmail = string.IsNullOrWhiteSpace(byEmail) ? "(unknown)" : byEmail,
            ActorRole = ParticipantRole.Organizer.ToString(),
            Source = AuditSource.Web,
            TargetType = "SyncDelta",
            TargetId = delta.Id.ToString(CultureInfo.InvariantCulture),
            Summary = summary,
        }, ct);
    }

    private static string Enc(string s) => System.Net.WebUtility.HtmlEncode(s);

    private static string FirstName(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return "there";
        var i = fullName.IndexOf(' ');
        return i > 0 ? fullName[..i] : fullName;
    }

    /// <summary>
    /// 🔴 §997 — the When cell, in EVENT-LOCAL (Danish) time.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-09: *"the time here is wrong as it is in wrong timezone. inside zoho
    /// the session is 8:30-8:50 Danish time - but the mail doesn't reflect that"*. It printed
    /// <c>07:30–07:50</c> — the UTC instant, exactly one hour behind CET.</para>
    ///
    /// <para>🔑 <b>The values were never wrong; the RENDERING was.</b> These are
    /// <c>DateTimeOffset</c>s read from Backstage, which returns UTC, and
    /// <c>DateTimeOffset.ToString</c> formats whatever offset the value carries. Nothing converted
    /// it to the zone the reader lives in.</para>
    ///
    /// <para>⚠️ This is <b>§305 in a new place</b> — that section is titled *"CRITICAL timezone
    /// bug"* and says *"we use Danish timezone always … you must store in the integration, if the
    /// different systems are presenting in different timezones."* §305 fixed the PARSE and the PUSH;
    /// this mail is a PRESENTATION site that was never brought along, and
    /// <see cref="EventTimezone"/> — the stated one authority — was sitting right there. The ops
    /// drift mail already used it (<c>ToEventLocalString</c>); the speaker mail did not.</para>
    ///
    /// <para>🔒 The zone is NOT repeated per value — the template prints "All times are Danish
    /// time" once under the table, so the two rows stay readable.</para>
    /// </remarks>
    private static string FormatRange(DateTimeOffset? start, DateTimeOffset? end)
    {
        // §83: no synced time yet → a clear placeholder, never an empty cell.
        if (start is null) return "TBD";

        var s = TimeZoneInfo.ConvertTime(start.Value, EventTimezone.Tz);
        var text = s.ToString("ddd dd MMM yyyy, HH:mm", CultureInfo.InvariantCulture);
        return end is { } e
            ? $"{text}–{TimeZoneInfo.ConvertTime(e, EventTimezone.Tz).ToString("HH:mm", CultureInfo.InvariantCulture)}"
            : text;
    }

    /// <summary>
    /// 🔴 §997 — the change table's rows, built server-side so ONLY the fields that actually
    /// changed appear.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-09, on a time-only change: *"i also dont understand the where change
    /// and see no difference?"* — he was right, and there was none. The template rendered the
    /// <b>When</b> and <b>Where</b> rows <b>unconditionally</b>, so a room that had not moved was
    /// printed struck-through and then immediately repeated verbatim. The <c>timeChanged</c> /
    /// <c>roomChanged</c> tokens were computed, passed, and never read.</para>
    ///
    /// <para>🔑 <b>This is the §594 trust failure in a participant mail.</b> An alert that asserts a
    /// change which did not happen teaches the reader that the alert cannot be believed — and this
    /// one goes to SPEAKERS, not to the operator who knows the system. A speaker seeing an
    /// unchanged room struck through has to work out whether the hub is wrong or their memory is.</para>
    ///
    /// <para>🔒 Built as a RAW-HTML token (<c>…Html</c> suffix ⇒
    /// <see cref="Email.EmailTemplateRenderer.RawHtmlTokens"/>) because the renderer substitutes in
    /// one pass with no conditionals. Every interpolated VALUE is HTML-encoded here; only the markup
    /// around them is ours.</para>
    /// </remarks>
    /// <param name="brandColor">
    /// ⚠️ The RESOLVED colour, never the <c>{{brandColor}}</c> token: the renderer substitutes in
    /// ONE pass, so a token inside a token VALUE reaches the reader as literal braces (§726).
    /// </param>
    private static string BuildChangeRowsHtml(
        bool timeChanged, string oldTime, string newTime,
        bool roomChanged, string oldRoom, string newRoom, string brandColor)
    {
        string Row(string label, string oldValue, string newValue) =>
            "<tr>"
            + "<td style=\"padding:10px 12px;border:1px solid #e5e7eb;background:#f9fafb;"
            + "font-weight:bold;width:34%;\">" + Enc(label) + "</td>"
            + "<td style=\"padding:10px 12px;border:1px solid #e5e7eb;\">"
            + "<span style=\"color:#9ca3af;text-decoration:line-through;\">" + Enc(oldValue) + "</span><br>"
            + "<strong style=\"color:" + Enc(brandColor) + ";\">" + Enc(newValue) + "</strong>"
            + "</td></tr>";

        var sb = new System.Text.StringBuilder();
        if (timeChanged) sb.Append(Row("When", oldTime, newTime));
        if (roomChanged) sb.Append(Row("Where", oldRoom, newRoom));
        return sb.ToString();
    }

    /// <summary>
    /// Build the {Field, Old, New} diff list for a SESSION time/location change — the same
    /// shape §38e enqueues. Only fields that actually differ are included.
    /// </summary>
    public static IReadOnlyList<SyncFieldChange> BuildSessionChanges(
        DateTimeOffset? oldStart, DateTimeOffset? oldEnd, string? oldRoom,
        DateTimeOffset? newStart, DateTimeOffset? newEnd, string? newRoom)
    {
        var list = new List<SyncFieldChange>();
        if (oldStart != newStart)
            list.Add(new SyncFieldChange(FieldStartsAt, Iso(oldStart), Iso(newStart)));
        if (oldEnd != newEnd)
            list.Add(new SyncFieldChange(FieldEndsAt, Iso(oldEnd), Iso(newEnd)));
        if (!RoomEquals(oldRoom, newRoom))
            list.Add(new SyncFieldChange(FieldRoom, oldRoom, newRoom));
        return list;
    }

    private static string? Iso(DateTimeOffset? dt) =>
        dt?.ToString("o", CultureInfo.InvariantCulture);
}
