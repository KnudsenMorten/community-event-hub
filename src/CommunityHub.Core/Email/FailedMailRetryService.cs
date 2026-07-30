using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Email;

/// <summary>
/// §655 — re-sends mail that failed for a reason that FIXES ITSELF, and nothing else.
/// </summary>
/// <remarks>
/// <para>Operator 2026-07-29: *"do you include a retry 3 times every 10 min so it tries failed mails
/// again before it goes into failed state"* — and, on the scope below, *"i agree to your
/// recommendation"*.</para>
///
/// <para><b>What existed before.</b> `SendMaxAttempts = 3` retries inside a SINGLE send, over a few
/// seconds. That covers a momentary blip; it does not cover an app restart mid-send, or a Brevo
/// throttle that lasts longer than the backoff. Once those seconds were spent the mail was logged
/// Failed and never tried again — the operator's Resend button was the only second chance.</para>
///
/// <para>🔒 <b>THE SCOPE IS THE SAFETY.</b> Re-sending mail is an outward-facing act: get it wrong
/// and real people get duplicates, or a dead address is hammered until it costs sending reputation.
/// So this retries ONLY the two kinds that genuinely fix themselves — <see cref="EmailFailureKind.Throttled"/>
/// and <see cref="EmailFailureKind.Temporary"/> — and never a bad address, a credential failure, a
/// recipient rejection, or a deliberate ring drop.</para>
///
/// <para>⚠️ <b>The residual risk, stated rather than hidden.</b> A "canceled" send may in principle
/// have reached Brevo before the app died, in which case a retry duplicates that one message. The
/// alternative — never retrying — means it is silently lost, which is what happens today. Bounded at
/// 3 attempts inside a 24-hour window, a duplicate is the better failure, but it is a real trade and
/// the operator should know it exists.</para>
/// </remarks>
public sealed class FailedMailRetryService
{
    /// <summary>Attempts per failed message. His agreed number.</summary>
    public const int MaxRetries = 3;

    /// <summary>Minimum gap between attempts — 3 attempts then spread across roughly an hour.</summary>
    public static readonly TimeSpan MinGapBetweenAttempts = TimeSpan.FromMinutes(20);

    /// <summary>
    /// 🔒 How far back to look. A failure older than this is HISTORY, not a backlog: re-sending a
    /// three-day-old welcome would confuse the recipient more than the silence did.
    /// </summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    /// <summary>Most messages to retry in one pass, so a bad night cannot become a mail storm.</summary>
    public const int MaxPerRun = 50;

    private readonly CommunityHubDbContext _db;
    private readonly ParticipantEmailService _sender;
    private readonly TimeProvider _clock;
    private readonly ILogger<FailedMailRetryService>? _log;

    public FailedMailRetryService(
        CommunityHubDbContext db,
        ParticipantEmailService sender,
        TimeProvider clock,
        ILogger<FailedMailRetryService>? log = null)
    {
        _db = db; _sender = sender; _clock = clock; _log = log;
    }

    /// <summary>The outcome of one pass.</summary>
    public sealed record Result(int Examined, int Retried, int Succeeded, int Exhausted, int AlreadyDelivered = 0);

    /// <summary>
    /// Should this failed row be retried NOW? Pure, so the judgement can be tested without a
    /// database or a mail server — the part that must not be wrong.
    /// </summary>
    public static bool ShouldRetry(
        string? error, int retryCount, DateTimeOffset sentAt, DateTimeOffset? lastRetryAt,
        int? participantId, string? template, string? category, DateTimeOffset now,
        bool alreadyDeliveredLater = false)
    {
        // Nothing to retry: it succeeded.
        if (string.IsNullOrWhiteSpace(error)) return false;

        // 🔒 §655.3 — ALREADY DELIVERED. The in-send retry (SendMaxAttempts) writes a SEPARATE log
        // row per attempt, so a message that failed once and succeeded a second later leaves BOTH a
        // Failed and a Sent row. Retrying the Failed one would send a DUPLICATE of a message the
        // person already has.
        //
        // This is not hypothetical: it was the very first row this job touched in production — a
        // hotel calendar invite that failed at 13:42:34 and succeeded at 13:42:34, one second and
        // one attempt later. §655.2 flagged duplicates as the residual risk; this is that risk with
        // a name, and it turns out to be the COMMON case rather than the rare one.
        if (alreadyDeliveredLater) return false;

        // 🔒 Only the self-correcting kinds. A bad address must NEVER be retried (§650: it hurts our
        // sending reputation and cannot succeed), a credential failure needs a human, a recipient
        // rejection needs a conversation, and a ring drop was not a failure in the first place.
        var kind = EmailFailureReason.Classify(error).Kind;
        if (kind is not (EmailFailureKind.Throttled or EmailFailureKind.Temporary)) return false;

        // Budget spent.
        if (retryCount >= MaxRetries) return false;

        // Too old to be worth resurrecting.
        if (now - sentAt > Window) return false;

        // Space the attempts out — retrying instantly just re-hits whatever was throttling us.
        if (lastRetryAt is not null && now - lastRetryAt.Value < MinGapBetweenAttempts) return false;

        // We can only re-send through the per-participant path, so we need BOTH a person and
        // something that identifies the message. §644's rule: derive the template from the row,
        // never guess — a guessed template sent a real speaker an unrelated onboarding mail.
        if (participantId is null or <= 0) return false;
        if (string.IsNullOrWhiteSpace(template) && string.IsNullOrWhiteSpace(category)) return false;

        return true;
    }

    /// <summary>
    /// §644 — the template to re-send: the one that failed, falling back to the category (always
    /// set) because <see cref="EmailLog.TemplateName"/> is blank on older rows.
    /// </summary>
    public static string? TemplateFor(string? template, string? category) =>
        !string.IsNullOrWhiteSpace(template) ? template!.Trim()
        : !string.IsNullOrWhiteSpace(category) ? category!.Split(':')[0].Trim()
        : null;

    /// <summary>Run one pass. Never throws — a retry failure must not take the timer job down.</summary>
    public async Task<Result> RunAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var since = now - Window;

        // Pre-filter in SQL to the plausible rows; the real decision is the pure rule above.
        var candidates = await _db.EmailLogs
            .Where(l => l.Error != null && l.Error != ""
                        && l.SentAt >= since
                        && l.RetryCount < MaxRetries
                        && l.ParticipantId != null)
            .OrderBy(l => l.SentAt)
            .Take(MaxPerRun * 4)
            .ToListAsync(ct);


        // §655.3 — every SUCCESSFUL send in the window, so a failure that was already superseded by
        // a later success is never re-sent. Loaded once rather than queried per row.
        var delivered = (await _db.EmailLogs
                .Where(l => l.Error == null && l.SentAt >= since && l.ParticipantId != null)
                .Select(l => new { l.ParticipantId, l.Category, l.SentAt })
                .ToListAsync(ct))
            .Select(d => new SupersededSendDetector.Delivery(d.ParticipantId, d.Category, d.SentAt))
            .ToList();

        int retried = 0, succeeded = 0, exhausted = 0, superseded = 0;

        foreach (var row in candidates)
        {
            if (retried >= MaxPerRun) break;

            // §656 — the SHARED rule, so this job and the Comms page can never disagree about
            // whether a message actually reached someone.
            var already = SupersededSendDetector.IsSuperseded(
                row.ParticipantId, row.Category, row.SentAt, delivered);

            if (already) superseded++;

            if (!ShouldRetry(row.Error, row.RetryCount, row.SentAt, row.LastRetryAt,
                             row.ParticipantId, row.TemplateName, row.Category, now,
                             alreadyDeliveredLater: already))
            {
                continue;
            }

            var template = TemplateFor(row.TemplateName, row.Category);
            if (template is null) continue;

            row.RetryCount++;
            row.LastRetryAt = now;
            retried++;

            try
            {
                var to = await _sender.SendTemplateToParticipantAsync(
                    row.EventId, row.ParticipantId!.Value, template,
                    category: "auto-retry", extraTokens: null, ct);

                if (to is not null)
                {
                    succeeded++;
                    _log?.LogInformation(
                        "FailedMailRetry: re-sent '{Template}' to participant {Pid} (attempt {N}/{Max}).",
                        template, row.ParticipantId, row.RetryCount, MaxRetries);
                }
            }
            catch (Exception ex)
            {
                // The attempt is still SPENT — otherwise a permanently broken row would be retried
                // forever, which is the flooding failure this cap exists to prevent.
                _log?.LogWarning(ex,
                    "FailedMailRetry: attempt {N}/{Max} for participant {Pid} threw.",
                    row.RetryCount, MaxRetries, row.ParticipantId);
            }

            if (row.RetryCount >= MaxRetries) exhausted++;
        }

        if (retried > 0) await _db.SaveChangesAsync(ct);

        return new Result(candidates.Count, retried, succeeded, exhausted, superseded);
    }
}
