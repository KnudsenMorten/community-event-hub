using System.Net;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Assistant;

/// <summary>The server-resolved origin of an intake message (REQUIREMENTS §137). NEVER
/// built from the request body — id + role come from the signed-in principal.</summary>
public sealed record FeedbackOrigin(int EventId, int ParticipantId, ParticipantRole Role, string? PageUrl = null);

/// <summary>The outcome of an intake attempt: whether a feed item was captured + the
/// confirmation to show the user.</summary>
public sealed record FeedbackIntakeResult(bool Captured, FeedbackKind? Kind, string ConfirmationMessage)
{
    public static readonly FeedbackIntakeResult None = new(false, null, string.Empty);
}

/// <summary>
/// The AiHelper INTAKE capability (REQUIREMENTS §137): detects bug/feature reports in a
/// user's message and forwards an explicit "contact the organizers" message — capturing a
/// durable <see cref="FeedbackItem"/> (the "CEH feed") AND emailing it to the right mailbox.
///
/// <para>Bug/feature reports go to <see cref="FeedbackIntakeOptions.BugFeatureEmailTo"/>
/// (the dev mailbox); organizer questions go to <see cref="FeedbackIntakeOptions.OrganizerEmailTo"/>
/// (the §136 replacement contact channel now that attendee 1:1 is disabled).</para>
///
/// <para>The DB capture is the SOURCE OF TRUTH and is saved BEFORE the email; the email is
/// best-effort (ring-EXEMPT like <see cref="EngineAlertSender"/>, since the mailboxes are
/// ops/organizer addresses, not ring-gated participants). A mail failure is logged and never
/// thrown — the feed item still records the report.</para>
/// </summary>
public sealed class FeedbackIntakeService
{
    private readonly CommunityHubDbContext _db;
    private readonly FeedbackIntakeDetector _detector;
    private readonly FeedbackIntakeOptions _options;
    private readonly IEmailSender _email;
    private readonly IEmailContextAccessor _ctx;
    private readonly TimeProvider _clock;
    private readonly ILogger<FeedbackIntakeService>? _log;

    public FeedbackIntakeService(
        CommunityHubDbContext db,
        FeedbackIntakeDetector detector,
        FeedbackIntakeOptions options,
        IEmailSender email,
        IEmailContextAccessor ctx,
        TimeProvider clock,
        ILogger<FeedbackIntakeService>? log = null)
    {
        _db = db;
        _detector = detector;
        _options = options;
        _email = email;
        _ctx = ctx;
        _clock = clock;
        _log = log;
    }

    private const string BugFeatureConfirmation = "Thanks — I've sent that to the team.";
    private const string OrganizerConfirmation =
        "Thanks — I've passed your message on to the organizers. They'll be in touch.";

    /// <summary>
    /// Run bug/feature detection on <paramref name="message"/>. When it reads as a bug or
    /// feature report, capture a feed item + email the dev mailbox and return
    /// <see cref="FeedbackIntakeResult.Captured"/> = true with a confirmation; otherwise
    /// return <see cref="FeedbackIntakeResult.None"/> (no capture, no email).
    /// </summary>
    public async Task<FeedbackIntakeResult> TryIntakeAsync(
        string message, FeedbackOrigin origin, CancellationToken ct = default)
    {
        if (!_options.Enabled) return FeedbackIntakeResult.None;

        var kind = _detector.Detect(message);
        if (kind is null) return FeedbackIntakeResult.None;

        await CaptureAndEmailAsync(kind.Value, _options.BugFeatureEmailTo, message, origin, ct);
        return new FeedbackIntakeResult(true, kind, BugFeatureConfirmation);
    }

    /// <summary>
    /// The explicit "send this to the organizers" path: always captures a
    /// <see cref="FeedbackKind.Question"/> feed item + emails the organizer mailbox.
    /// </summary>
    public async Task<FeedbackIntakeResult> SendToOrganizersAsync(
        string message, FeedbackOrigin origin, CancellationToken ct = default)
    {
        await CaptureAndEmailAsync(FeedbackKind.Question, _options.OrganizerEmailTo, message, origin, ct);
        return new FeedbackIntakeResult(true, FeedbackKind.Question, OrganizerConfirmation);
    }

    private async Task CaptureAndEmailAsync(
        FeedbackKind kind, string to, string message, FeedbackOrigin origin, CancellationToken ct)
    {
        var text = (message ?? string.Empty).Trim();

        // (1) Durable capture FIRST — the feed item is the source of truth.
        var item = new FeedbackItem
        {
            EventId = origin.EventId,
            ParticipantId = origin.ParticipantId,
            Role = origin.Role,
            Kind = kind,
            Message = text,
            PageUrl = string.IsNullOrWhiteSpace(origin.PageUrl) ? null : origin.PageUrl.Trim(),
            RoutedTo = to,
            CreatedAt = _clock.GetUtcNow(),
        };
        _db.Set<FeedbackItem>().Add(item);
        await _db.SaveChangesAsync(ct);

        // Resolve the asker's NAME + EMAIL server-side (REQUIREMENTS §137) — NEVER
        // from the request body — so the organizer sees who asked and can reply to
        // them directly. Scoped to the origin edition + id from the signed-in principal.
        var asker = await _db.Participants
            .AsNoTracking()
            .Where(p => p.Id == origin.ParticipantId && p.EventId == origin.EventId)
            .Select(p => new { p.FullName, p.Email })
            .FirstOrDefaultAsync(ct);
        var askerName = string.IsNullOrWhiteSpace(asker?.FullName) ? null : asker!.FullName.Trim();
        var askerEmail = string.IsNullOrWhiteSpace(asker?.Email) ? null : asker!.Email.Trim();
        // Subject uses the asker's name (fallback to the participant ref if unresolved);
        // NEVER the page path.
        var who = askerName ?? $"participant #{origin.ParticipantId}";

        // (2) Best-effort email — ring-exempt (ops/organizer mailbox, not a ring-gated
        // participant); never throws back to the caller.
        try
        {
            var subject = $"{_options.SubjectPrefix} AiHelper {KindWord(kind)} from {who}";
            // Reply-To = the asker, so an organizer hitting "Reply" reaches the person
            // (the To stays the configured dev/organizer mailbox). Null when unresolved.
            EmailReplyTo? replyTo = askerEmail is null ? null : new EmailReplyTo(askerEmail, askerName);
            using var _ = _ctx.Set(new EmailContext("feedback-intake", RingExempt: true));
            await _email.SendAsync(to, subject, BuildHtml(item, askerName, askerEmail), replyTo, ct);
            _log?.LogInformation("AiHelper intake ({Kind}) captured #{Id} + emailed {To}.", kind, item.Id, to);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "AiHelper intake email to {To} failed (item #{Id} still captured).", to, item.Id);
        }
    }

    private static string KindWord(FeedbackKind kind) => kind switch
    {
        FeedbackKind.Bug => "bug",
        FeedbackKind.Feature => "feature",
        FeedbackKind.Question => "question",
        _ => "message",
    };

    private static string BuildHtml(FeedbackItem item, string? askerName, string? askerEmail)
    {
        var when = item.CreatedAt.ToString("yyyy-MM-dd HH:mm 'UTC'");
        var page = string.IsNullOrWhiteSpace(item.PageUrl)
            ? "(not provided)"
            : WebUtility.HtmlEncode(item.PageUrl);

        // "From: <FullName> <<email>> (<Role>)" — name + email shown clearly so the
        // organizer knows who asked and can reply (Reply-To is set on the message).
        var name = string.IsNullOrWhiteSpace(askerName)
            ? $"participant #{item.ParticipantId}"
            : askerName;
        var from = WebUtility.HtmlEncode(name);
        if (!string.IsNullOrWhiteSpace(askerEmail))
        {
            from += $" &lt;{WebUtility.HtmlEncode(askerEmail)}&gt;";
        }
        from += $" ({item.Role})";

        return
            $"<p><strong>{KindWord(item.Kind)}</strong> via the AiHelper.</p>" +
            $"<p><strong>From:</strong> {from}</p>" +
            $"<p style=\"white-space:pre-wrap\">{WebUtility.HtmlEncode(item.Message)}</p>" +
            $"<hr><p style=\"color:#6b7a90;font-size:13px\">Page: {page}<br>When: {when}</p>";
    }
}
