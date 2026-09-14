using System.Net;
using System.Text;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CommunityHub.Core.Email;

/// <summary>
/// §1077 stage 4 — THE WEEKLY REMINDER: chase a company that was invited and has not finished,
/// <b>until they complete it or say no</b>.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-11: <i>"Weekly reminders until completed or 'no interest'"</i> — and
/// <i>"stopping on decline is part of the requirement, not an afterthought"</i>.</para>
///
/// <para>🔴 <b>Stopping is the feature.</b> A reminder loop that keeps running after somebody has
/// answered is not a nag, it is a broken promise — the wizard offers "we do not want to participate"
/// and says we will stop asking. Four independent conditions end it, and each is a separate reason
/// rather than one flag somebody might clear: the wizard is COMPLETE, the company DECLINED, the
/// invitation was never sent, or the link is no longer live.</para>
///
/// <para>🔒 <b>It writes to ONE person: the approver</b> — never the coordinator, never an attendee.
/// The compliance rule stands: CEH does not mail a company's people about the photo.</para>
///
/// <para>⚠️ <b>Ring-governed</b>, like the invitation and for the same reason: this reaches a real
/// person at a customer.</para>
/// </remarks>
public sealed class VolumePackageReminderService
{
    /// <summary>🔒 Default OFF — it is outbound mail to a customer, on a schedule.</summary>
    public const string FeatureKey = "volume-package-reminders";

    /// <summary>
    /// A week, minus a few hours of slack. ⚠️ A strict 7×24 comparison against a job that runs at a
    /// slightly different minute each week silently becomes an 8-day cadence — the reminder that
    /// "goes out weekly" then skips every other Monday and nobody can say why.
    /// </summary>
    public static readonly TimeSpan Interval = TimeSpan.FromDays(7) - TimeSpan.FromHours(4);

    private readonly CommunityHubDbContext _db;
    private readonly IEmailSender _email;
    private readonly IEmailContextAccessor _context;
    private readonly TimeProvider _clock;
    private readonly string _hubUrl;

    public VolumePackageReminderService(
        CommunityHubDbContext db, IEmailSender email, IEmailContextAccessor context,
        TimeProvider? clock = null, IOptions<EmailTemplateOptions>? branding = null)
    {
        _db = db;
        _email = email;
        _context = context;
        _clock = clock ?? TimeProvider.System;
        _hubUrl = (branding?.Value.HubUrl ?? string.Empty).TrimEnd('/');
    }

    /// <summary>What one pass did.</summary>
    public sealed record ReminderResult(int Reminded, int Skipped)
    {
        public override string ToString() => $"{Reminded} reminded, {Skipped} left alone";
    }

    /// <summary>
    /// The companies still owed a nudge. Public so the organizer page can show the same list the
    /// job will act on — an operator should never have to guess who is about to be written to.
    /// </summary>
    public async Task<IReadOnlyList<VolumePackageCompany>> DueAsync(
        int eventId, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();

        var candidates = await _db.VolumePackageCompanies
            .Where(c => c.EventId == eventId
                        // Only somebody we have already invited: a reminder to a company that never
                        // received the first mail is not a reminder, it is a surprise.
                        && c.WizardInvitedAt != null
                        && c.WizardCompletedAt == null
                        && c.DeclinedAt == null)
            .ToListAsync(ct);

        return candidates
            .Where(c => c.WizardTokenIsActive(now))          // a dead link makes the mail useless
            .Where(c => !string.IsNullOrWhiteSpace(c.ApproverEmail))
            .Where(c => (c.WizardRemindedAt ?? c.WizardInvitedAt!.Value) + Interval <= now)
            .OrderBy(c => c.CustomName)
            .ToList();
    }

    /// <summary>Send this week's reminders. The CALLER checks the feature switch.</summary>
    public async Task<ReminderResult> RunAsync(int eventId, CancellationToken ct = default)
    {
        var due = await DueAsync(eventId, ct);
        if (due.Count == 0) return new ReminderResult(0, 0);

        var now = _clock.GetUtcNow();
        var sent = 0;

        foreach (var company in due)
        {
            using (_context.Set(new EmailContext(
                       Category: "volume-package-reminder",
                       EventId: company.EventId,
                       RecipientName: company.ApproverName,
                       FeatureKey: FeatureKey)))
            {
                await _email.SendAsync(
                    company.ApproverEmail!,
                    $"{company.CustomName}: your volume package is still waiting",
                    BuildHtml(company),
                    ct);
            }

            company.WizardRemindedAt = now;
            company.WizardReminderCount++;
            sent++;
        }

        await _db.SaveChangesAsync(ct);
        return new ReminderResult(sent, 0);
    }

    private string BuildHtml(VolumePackageCompany company)
    {
        var link = string.IsNullOrEmpty(_hubUrl)
            ? $"/volume-package/{company.WizardToken}"
            : $"{_hubUrl}/volume-package/{company.WizardToken}";

        var sb = new StringBuilder();
        sb.Append($"<p>Hello{(string.IsNullOrWhiteSpace(company.ApproverName) ? string.Empty : " " + E(company.ApproverName))},</p>");
        sb.Append($"<p>We have not heard back about the volume package for "
                + $"<strong>{E(company.CustomName)}</strong> — the keynote mention, the "
                + "social-media announcement and the group photo. It takes about a minute.</p>");
        sb.Append($"<p><a href=\"{E(link)}\" style=\"font-weight:600;\">Open your volume package page</a></p>");
        // 🔑 The way OUT is in every reminder, not just the first invitation. A person who does not
        // want this must never have to hunt for how to stop being asked.
        sb.Append("<p style=\"color:#6b7280;font-size:13px;\">If your company would rather not take "
                + "part, the same page has a “we do not want to participate” option — one click and "
                + "we stop asking.</p>");
        return sb.ToString();
    }

    private static string E(string? raw) => WebUtility.HtmlEncode(raw ?? string.Empty);
}
