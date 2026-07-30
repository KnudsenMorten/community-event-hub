using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// RETIRED (§244, operator 2026-07-07: "drop this reminder 1 month before, it's too
/// confusing"). This job used to send the opt-in "~1 month before" Master Class calendar
/// reminder (REQUIREMENTS §6) daily at 08:00 UTC. The <c>[Function]</c> timer trigger has
/// been REMOVED so the Functions host never discovers or schedules it — the class is kept
/// compiling (and manually invokable) only so the send path and its tests remain intact.
/// The <c>masterclass-month-reminder</c> catalog entry was removed too; the template file
/// stays on disk for historic re-renders. The confirmed-seat email already carries the
/// full-day calendar invite (§210), which is the one calendar touchpoint attendees keep.
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

    // §244: NO [Function]/[TimerTrigger] attribute — the job is retired and must never
    // be scheduled. (Was: daily 08:00 UTC, "0 0 8 * * *".)
    public async Task Run(TimerInfo timer, CancellationToken ct)
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
