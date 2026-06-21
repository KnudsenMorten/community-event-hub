using CommunityHub.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Email;

/// <summary>
/// The outcome of an organizer "re-send" of a logged email
/// (REQUIREMENTS §20 Participant "EmailLog resend-on-failure"). A pure value so
/// the page can render an honest flash (success / no-op-with-reason / failure)
/// through the shared <c>ActionResultSummarizer</c> shape.
/// </summary>
public enum EmailResendOutcome
{
    /// <summary>The mail was rendered + re-sent (addressed To the returned address).</summary>
    Sent = 0,

    /// <summary>The log row was not found in the organizer's edition.</summary>
    NotFound = 1,

    /// <summary>The row succeeded already — nothing to recover (re-sending a good mail is not a recovery).</summary>
    NotFailed = 2,

    /// <summary>The row has no template / participant captured (a raw/broadcast/PIN send) so it cannot be faithfully re-sent from the log.</summary>
    NotResendable = 3,

    /// <summary>The participant referenced by the row no longer exists in the edition.</summary>
    ParticipantGone = 4,

    /// <summary>The re-send itself threw (the new attempt failed too); see <see cref="EmailResendResult.Error"/>.</summary>
    Failed = 5,
}

/// <summary>Result of <see cref="EmailResendService.ResendAsync"/>.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="ToEmail">The address the re-send was addressed to (only when <see cref="EmailResendOutcome.Sent"/>).</param>
/// <param name="TemplateName">The template re-sent (when applicable), for the confirmation message.</param>
/// <param name="Error">The failure reason when <see cref="Outcome"/> is <see cref="EmailResendOutcome.Failed"/>.</param>
public sealed record EmailResendResult(
    EmailResendOutcome Outcome,
    string? ToEmail = null,
    string? TemplateName = null,
    string? Error = null);

/// <summary>
/// Re-sends a single FAILED <c>EmailLog</c> row on organizer demand
/// (REQUIREMENTS §20 Participant: "EmailLog resend-on-failure"). It does NOT add
/// a new send path: it re-uses the proven per-participant
/// <see cref="ParticipantEmailService"/> seam (effective To + secondary CC + the
/// allowlist-gated logging sender), so a re-send is logged + allowlist-gated
/// exactly like the original. Only rows that captured BOTH a participant AND a
/// template (the per-participant path: welcome, onboarding, task-deadline,
/// manual-resend, …) are resendable; raw/ad-hoc rows (broadcast, PIN) are not —
/// the caller is told why rather than a faked success.
///
/// Like the manual individual re-send (10a-2) this is deliberately NOT
/// idempotency-gated — the organizer explicitly chose to retry; the
/// <c>SentReminder</c> ledger is untouched (the dedup decision belongs to the
/// original auto-send job, not to a recovery). The recovery send writes its own
/// fresh <c>EmailLog</c> row via the logging decorator, so success/failure of
/// the retry is itself auditable.
/// </summary>
public sealed class EmailResendService
{
    private readonly CommunityHubDbContext _db;
    private readonly ParticipantEmailService _participantEmail;

    public EmailResendService(CommunityHubDbContext db, ParticipantEmailService participantEmail)
    {
        _db = db;
        _participantEmail = participantEmail;
    }

    /// <summary>
    /// A logged row is resendable from the Email Log only when the original came
    /// through the per-participant template path AND it actually failed/dropped.
    /// Pure so the view can show/hide the Resend affordance without a round-trip.
    /// </summary>
    public static bool IsResendable(Domain.EmailLog row) =>
        row is not null
        && !row.Success
        && row.ParticipantId is > 0
        && !string.IsNullOrWhiteSpace(row.TemplateName);

    /// <summary>
    /// Re-send the email recorded by log row <paramref name="emailLogId"/> within
    /// <paramref name="eventId"/>. Honest outcome — never a faked success.
    /// </summary>
    public async Task<EmailResendResult> ResendAsync(
        int eventId, int emailLogId, CancellationToken ct = default)
    {
        var row = await _db.EmailLogs
            .FirstOrDefaultAsync(e => e.Id == emailLogId && e.EventId == eventId, ct);

        if (row is null)
            return new EmailResendResult(EmailResendOutcome.NotFound);

        if (row.Success)
            return new EmailResendResult(EmailResendOutcome.NotFailed);

        if (row.ParticipantId is not > 0 || string.IsNullOrWhiteSpace(row.TemplateName))
            return new EmailResendResult(EmailResendOutcome.NotResendable);

        try
        {
            var to = await _participantEmail.SendTemplateToParticipantAsync(
                eventId, row.ParticipantId.Value, row.TemplateName!,
                category: "manual-resend", extraTokens: null, ct);

            return to is null
                ? new EmailResendResult(EmailResendOutcome.ParticipantGone, TemplateName: row.TemplateName)
                : new EmailResendResult(EmailResendOutcome.Sent, to, row.TemplateName);
        }
        catch (Exception ex)
        {
            return new EmailResendResult(
                EmailResendOutcome.Failed, TemplateName: row.TemplateName, Error: ex.Message);
        }
    }
}
