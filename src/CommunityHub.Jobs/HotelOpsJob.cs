using CommunityHub.Core.Data;
using CommunityHub.Core.Organizer;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// §1386 — hotel operations: applies the edition's hotel contract seed once, then mails the hotel-ops
/// recipients the change log (with the updated .xls) and the early release / payment deadline warnings.
/// </summary>
/// <remarks>
/// 🔒 Internal mail only (<see cref="HotelOpsNotifier.RecipientsFor"/>): never the hotel, never a guest.
/// The clock and nothing else — the rules live in <see cref="HotelContractSeeder"/> and
/// <see cref="HotelOpsNotifier"/>.
/// </remarks>
public sealed class HotelOpsJob
{
    private readonly CommunityHubDbContext _db;
    private readonly HotelContractSeeder _seeder;
    private readonly HotelOpsNotifier _notifier;
    private readonly ILogger<HotelOpsJob> _log;
    private readonly CommunityHub.Core.Diagnostics.JobActivityReporter? _activity;

    public HotelOpsJob(
        CommunityHubDbContext db,
        HotelContractSeeder seeder,
        HotelOpsNotifier notifier,
        ILogger<HotelOpsJob> log,
        CommunityHub.Core.Diagnostics.JobActivityReporter? activity = null)
    {
        _db = db;
        _seeder = seeder;
        _notifier = notifier;
        _log = log;
        _activity = activity;
    }

    // Base tick only — the real spacing is the §510 interval on the Jobs page.
    [Function("HotelOpsJob")]
    public async Task Run([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        var eventId = await _db.Events.Where(e => e.IsActive).Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);
        if (eventId is null)
        {
            _log.LogWarning("HotelOpsJob: no active event.");
            _activity?.ReportInactive("No active event.");
            return;
        }

        var seeded = await _seeder.ApplyFromConfigAsync(eventId.Value, ct);
        if (seeded is not null) _log.LogWarning("HotelOpsJob: hotel contract seed applied — {Summary}", seeded);

        var r = await _notifier.RunAsync(eventId.Value, ct);
        _log.LogInformation(
            "HotelOpsJob: {Mails} change mail(s) covering {Entries} change(s), {Warnings} deadline warning(s), {Frozen} night(s) frozen{Waiting}.",
            r.ChangeMails, r.EntriesMailed, r.DeadlineWarnings, r.NightsFrozen, r.Waiting ? " (changes still settling)" : "");

        if (seeded is null && r.ChangeMails == 0 && r.DeadlineWarnings == 0 && r.NightsFrozen == 0)
            _activity?.ReportInactive(r.Waiting
                ? "Hotel changes are waiting for the quiet period before they are mailed."
                : "No hotel changes to mail and no deadline warning due.");
        else
            _activity?.ReportWork();
    }
}
