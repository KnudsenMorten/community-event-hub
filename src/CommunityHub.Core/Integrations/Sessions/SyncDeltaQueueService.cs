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

    public SyncDeltaQueueService(
        CommunityHubDbContext db,
        TimeProvider? clock = null,
        IAuditTrail? audit = null,
        EngineAlertSender? alerts = null,
        IEmailSender? sender = null,
        IEmailContextAccessor? context = null,
        EmailTemplateProvider? templates = null,
        SessionBackstagePushService? sessionPush = null,
        SpeakerBackstagePushService? speakerPush = null)
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

        if (delta.ChangeKind == SyncDeltaChangeKind.Update && applied)
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
        await _alerts.AlertAsync(subject, html, ct, throttleKey: $"sync-queue-{eventId}");
        return true;
    }

    private static string BuildNotifyHtml(int eventId, IReadOnlyList<SyncDelta> pending)
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
        sb.Append("<p><a href=\"/Organizer/SyncQueue\">Open the sync approval queue</a></p>");
        sb.Append("<p>(CEH never auto-applies a sync change or auto-deletes — REQUIREMENTS §59.)</p>");
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
            _ => (false, false, $"No apply handler for {delta.EntityType}/{delta.ChangeKind} yet."),
        };
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
        var emailedCount = 0;
        if (_sender is not null && (timeChanged || roomChanged))
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

        var msg = $"Applied to the session; {emailedCount} speaker email(s) sent.";
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
        var scope = _context?.Set(new EmailContext(
            EmailCategory, eventId, null, fullName,
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

    private static string FormatRange(DateTimeOffset? start, DateTimeOffset? end)
    {
        // §83: no synced time yet → a clear placeholder, never an empty cell.
        if (start is null) return "TBD";
        var s = start.Value.ToString("ddd dd MMM yyyy, HH:mm", CultureInfo.InvariantCulture);
        return end is { } e
            ? $"{s}–{e.ToString("HH:mm", CultureInfo.InvariantCulture)}"
            : s;
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
