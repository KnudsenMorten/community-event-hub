using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Settings;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Email;

/// <summary>
/// Sends each SPEAKER a periodic EMAIL DIGEST of the OPEN (unanswered) attendee
/// questions on the sessions they are linked to (REQUIREMENTS §21 Participant
/// "Speaker Q&amp;A email digest on new questions"). Attendees can ask a question
/// for a session ahead of the event via the public ask page; those land as
/// <see cref="SessionQuestion"/> rows in the hub. Without a notification a speaker
/// only sees them if they happen to open the hub — this digest closes that gap.
///
/// Reuses the established seams — there is NO new mail path:
///  - routing (effective speaker-override To + secondary-email CC) and the audit
///    log go through <see cref="ParticipantEmailService"/>, so the digest is
///    subject to the SAME allowlist gating as every other participant mail (no
///    real outbound until an address passes the allowlist);
///  - <b>idempotent</b> via the <see cref="SentReminder"/> ledger. The occasion
///    key carries a CONTENT FINGERPRINT (the highest open-question id the speaker
///    can currently see). A run with the same open set is a no-op; a brand-new
///    question raises the fingerprint, which is a fresh occasion, so the next run
///    sends an updated digest exactly once. Answering/closing questions never
///    raises the fingerprint, so it never re-sends.
///
/// Pure + constructor-injected (db + participant-email seam + clock), so it is
/// unit-testable on the EF Core InMemory provider with a fixed clock.
/// </summary>
public sealed class SpeakerQuestionDigestService
{
    /// <summary>Reminder-ledger type for the speaker question digest.</summary>
    public const string ReminderType = "speaker-question-digest";

    /// <summary>The email content template name.</summary>
    public const string TemplateName = "speaker-question-digest";

    /// <summary>EmailLog category for the digest.</summary>
    public const string Category = "speaker-question-digest";

    private static readonly ParticipantRole[] SpeakerRoles =
    {
        ParticipantRole.Speaker,
        ParticipantRole.MasterclassSpeaker,
    };

    private readonly CommunityHubDbContext _db;
    private readonly ParticipantEmailService _participantEmail;
    private readonly TimeProvider _clock;
    private readonly FeatureGateService? _gate;
    private readonly RingResolver? _rings;

    /// <summary>The feature key whose released-to ring gates this per-speaker send (§23).</summary>
    public const string FeatureKey = "digest-emails";

    public SpeakerQuestionDigestService(
        CommunityHubDbContext db,
        ParticipantEmailService participantEmail,
        TimeProvider clock,
        FeatureGateService? gate = null,
        RingResolver? rings = null)
    {
        _db = db;
        _participantEmail = participantEmail;
        _clock = clock;
        // Optional ring-aware gate (§23). When BOTH are supplied (production DI),
        // a speaker only receives the digest when their effective ring ≤ the
        // released-to ring of 'digest-emails'. Left null in focused unit tests, so
        // the existing digest behaviour is unchanged (no ring filtering).
        _gate = gate;
        _rings = rings;
    }

    /// <summary>
    /// One speaker's pending digest: the speaker, how many open questions they
    /// have across all their sessions, how many distinct sessions are involved,
    /// and the content fingerprint (highest open-question id) that makes the
    /// occasion idempotent.
    /// </summary>
    public sealed record SpeakerDigest(
        int ParticipantId,
        int OpenQuestionCount,
        int SessionCount,
        long Fingerprint);

    /// <summary>
    /// Compute, per speaker in the edition, the digest of the OPEN questions on
    /// their linked sessions. Speakers with no open questions are omitted. Pure
    /// read — never sends or writes. Deterministic order (by participant id) so a
    /// caller can rely on it in tests.
    /// </summary>
    public async Task<List<SpeakerDigest>> BuildPendingDigestsAsync(
        int eventId, CancellationToken ct = default)
    {
        // All OPEN questions in the edition, joined to the speakers of each
        // question's session. One row per (open question, linked speaker).
        var rows = await (
            from q in _db.SessionQuestions
            where q.EventId == eventId && q.Status == SessionQuestionStatus.Open
            join ss in _db.SessionSpeakers on q.SessionId equals ss.SessionId
            join p in _db.Participants on ss.ParticipantId equals p.Id
            where p.EventId == eventId && p.IsActive
                  && (p.Role == ParticipantRole.Speaker
                      || p.Role == ParticipantRole.MasterclassSpeaker)
            select new
            {
                SpeakerId = p.Id,
                QuestionId = q.Id,
                q.SessionId,
            }).ToListAsync(ct);

        return rows
            .GroupBy(r => r.SpeakerId)
            .Select(g => new SpeakerDigest(
                ParticipantId: g.Key,
                OpenQuestionCount: g.Select(r => r.QuestionId).Distinct().Count(),
                SessionCount: g.Select(r => r.SessionId).Distinct().Count(),
                Fingerprint: g.Max(r => (long)r.QuestionId)))
            .OrderBy(d => d.ParticipantId)
            .ToList();
    }

    /// <summary>
    /// Build the per-speaker digests and email each one, skipping any whose exact
    /// occasion (type + identity address + fingerprint) is already in the ledger.
    /// Returns the number of digests actually sent. Safe to call repeatedly — a
    /// re-run with no new questions sends nothing.
    /// </summary>
    public async Task<int> SendPendingAsync(int eventId, CancellationToken ct = default)
    {
        var digests = await BuildPendingDigestsAsync(eventId, ct);
        if (digests.Count == 0) return 0;

        var sent = 0;
        foreach (var digest in digests)
        {
            if (await SendOneAsync(eventId, digest, ct)) sent++;
        }
        return sent;
    }

    /// <summary>
    /// Send one speaker's digest if its occasion is not already in the ledger.
    /// Returns true when a mail was sent (and the ledger row written), false when
    /// skipped as a duplicate or when the speaker has no resolvable identity.
    /// </summary>
    private async Task<bool> SendOneAsync(
        int eventId, SpeakerDigest digest, CancellationToken ct)
    {
        var p = await _db.Participants
            .FirstOrDefaultAsync(x => x.Id == digest.ParticipantId && x.EventId == eventId, ct);
        if (p is null || string.IsNullOrWhiteSpace(p.Email)) return false;

        // RING GATE (§23 progressive rollout): when the ring-aware gate is wired,
        // a speaker only receives the digest when their EFFECTIVE ring is at or
        // below the released-to ring of this feature. A speaker still in a later
        // (higher) ring than the rollout has reached is skipped — the SAME rule the
        // GUI applies, so the scheduler never mails out-of-ring recipients.
        if (_gate is not null && _rings is not null)
        {
            var effectiveRing = await _rings.GetEffectiveRingAsync(p.Id, ct);
            if (!await _gate.IsFeatureActiveForRingAsync(FeatureKey, eventId, effectiveRing, ct))
            {
                return false;
            }
        }

        var occasion = OccasionKey(digest.Fingerprint);

        // Idempotency keys on the IDENTITY address (a speaker's contact-override
        // can change without affecting dedup), mirroring CalendarInviteEmailService.
        var already = await _db.SentReminders.AnyAsync(
            s => s.EventId == eventId
                 && s.RecipientEmail == p.Email
                 && s.ReminderType == ReminderType
                 && s.OccasionKey == occasion,
            ct);
        if (already) return false;

        var tokens = new Dictionary<string, string>
        {
            ["openCount"] = digest.OpenQuestionCount.ToString(),
            ["sessionCount"] = digest.SessionCount.ToString(),
            // "question" / "questions" + "session" / "sessions" so the copy reads
            // naturally without the template knowing about pluralization.
            ["openCountNoun"] = Pluralize(digest.OpenQuestionCount, "question", "questions"),
            ["sessionCountNoun"] = Pluralize(digest.SessionCount, "session", "sessions"),
        };

        var to = await _participantEmail.SendTemplateToParticipantAsync(
            eventId, p.Id, TemplateName, category: Category, extraTokens: tokens, ct);
        if (to is null) return false;

        _db.SentReminders.Add(new SentReminder
        {
            EventId = eventId,
            RecipientEmail = p.Email,   // idempotency keys on identity
            ReminderType = ReminderType,
            OccasionKey = occasion,
            SentAt = _clock.GetUtcNow(),
        });
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>The occasion key for a digest fingerprint (highest open-question id).</summary>
    public static string OccasionKey(long fingerprint) => $"upto:{fingerprint}";

    private static string Pluralize(int n, string one, string many) => n == 1 ? one : many;
}
