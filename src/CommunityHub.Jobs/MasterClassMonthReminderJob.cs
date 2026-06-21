using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// Sends the opt-in "~1 month before" Master Class calendar reminder (REQUIREMENTS §6):
/// a confirmed attendee who chose it gets an email carrying the master-class .ics when
/// the class is within the next ~month. Ring-gated + idempotent (MonthReminderSentAt).
/// Runs daily at 08:00 UTC.
/// </summary>
public sealed class MasterClassMonthReminderJob
{
    private readonly MasterClassSignupService _svc;
    private readonly MasterClassEmailService _email;
    private readonly IConfiguration _config;
    private readonly TimeProvider _clock;
    private readonly ILogger<MasterClassMonthReminderJob> _log;

    public MasterClassMonthReminderJob(
        MasterClassSignupService svc, MasterClassEmailService email,
        IConfiguration config, TimeProvider clock, ILogger<MasterClassMonthReminderJob> log)
    {
        _svc = svc; _email = email; _config = config; _clock = clock; _log = log;
    }

    [Function("MasterClassMonthReminderJob")]
    public async Task Run([TimerTrigger("0 0 8 * * *")] TimerInfo timer, CancellationToken ct)
    {
        var due = await _svc.DueMonthReminderSignupIdsAsync(_clock.GetUtcNow(), windowDays: 31, ct: ct);
        if (due.Count == 0) return;

        var domain = _config["Hub:CustomDomain"];
        var host = string.IsNullOrWhiteSpace(domain) ? "eldk27.eventhub.expertslive.dk" : domain;

        var sent = 0;
        foreach (var id in due)
        {
            try { if (await _email.SendMonthReminderAsync(id, host, ct)) sent++; } catch { /* retry next run */ }
        }
        _log.LogInformation("MasterClassMonthReminderJob: {Due} due, {Sent} calendar reminder(s) sent.", due.Count, sent);
    }
}
