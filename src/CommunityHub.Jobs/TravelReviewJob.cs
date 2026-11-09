using CommunityHub.Core.Data;
using CommunityHub.Core.Travel;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// §1387 — runs the AI document check on every new speaker response and mails the organizers' mailbox
/// the recommendation (once per document set), plus a daily reminder of claims waiting more than 2 days.
/// The clock only; the rules are in <see cref="TravelReviewService"/>.
/// </summary>
/// <remarks>🔒 It never accepts, sends back or mails a speaker — those are organizer buttons.</remarks>
public sealed class TravelReviewJob
{
    private readonly CommunityHubDbContext _db;
    private readonly TravelReviewService _review;
    private readonly ILogger<TravelReviewJob> _log;
    private readonly CommunityHub.Core.Diagnostics.JobActivityReporter? _activity;

    public TravelReviewJob(CommunityHubDbContext db, TravelReviewService review, ILogger<TravelReviewJob> log,
        CommunityHub.Core.Diagnostics.JobActivityReporter? activity = null)
    {
        _db = db;
        _review = review;
        _log = log;
        _activity = activity;
    }

    // Base tick only — the real spacing is the §510 interval on the Jobs page.
    [Function("TravelReviewJob")]
    public async Task Run([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        var eventId = await _db.Events.Where(e => e.IsActive).Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);
        if (eventId is null)
        {
            _activity?.ReportInactive("No active event.");
            return;
        }

        var (checkedN, notified) = await _review.SweepAsync(eventId.Value, ct);
        // §1387 — once a day: the claims waiting for a decision more than 2 days.
        var reminded = await _review.RemindWaitingAsync(eventId.Value, ct);
        _log.LogInformation("TravelReviewJob: {Checked} claim(s) checked, {Notified} organizer notice(s), reminder {Reminded}.",
            checkedN, notified, reminded);
        if (checkedN == 0 && notified == 0 && !reminded) _activity?.ReportInactive("No new travel claim to check.");
        else _activity?.ReportWork();
    }
}
