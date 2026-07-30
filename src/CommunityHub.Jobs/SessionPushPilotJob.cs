using CommunityHub.Core.Data;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations.Sessions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Jobs;

/// <summary>
/// §299 stage-2 PILOT / BULK-CREATE (operator 2026-07-23): push ONE OR MORE explicitly named
/// CEH sessions into the Zoho Backstage agenda — first the single-session pilot, now also the
/// operator-approved bulk-create (e.g. the remaining 8 master classes + their halls).
/// Admin-triggered only, following the §252-F1 EnableEmailFeaturesJob pattern: the yearly
/// "parking" cron keeps the <c>[Function]</c> invocation path alive for
/// POST /admin/functions/SessionPushPilotJob, while <see cref="Run"/> is HARD-GUARDED on
/// TWO deliberate app settings — <c>StageTwoPilot:Allowed</c> == "true" and the session
/// id(s): <c>StageTwoPilot:SessionId</c> (a single CEH session id) and/or
/// <c>StageTwoPilot:SessionIds</c> (comma-separated CEH session ids). Without both it logs
/// and returns. The push itself refuses test/service/untitled/already-linked sessions (see
/// <see cref="SessionBackstagePushService.CreateOneAsync"/>), so a stale setting can never
/// double-create or leak a test session.
///
/// Each create runs with <c>suppressNotify: true</c> and the job sends ONE batched
/// <see cref="ZohoChangeNotifier"/> mail ("Agenda / sessions") covering every session AND
/// every hall created in the run — never one mail per session (operator 2026-07-23 rule).
/// </summary>
public sealed class SessionPushPilotJob
{
    private readonly SessionBackstagePushService _push;
    private readonly CommunityHubDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<SessionPushPilotJob> _log;
    private readonly ZohoChangeNotifier? _zohoChanges;

    public SessionPushPilotJob(
        SessionBackstagePushService push, CommunityHubDbContext db,
        IConfiguration config, ILogger<SessionPushPilotJob> log,
        ZohoChangeNotifier? zohoChanges = null)
    {
        _push = push;
        _db = db;
        _config = config;
        _log = log;
        _zohoChanges = zohoChanges;
    }

    [Function("SessionPushPilotJob")]
    public async Task Run([TimerTrigger("0 0 0 1 1 *")] TimerInfo timer, CancellationToken ct)
    {
        if (!string.Equals(_config["StageTwoPilot:Allowed"], "true", StringComparison.OrdinalIgnoreCase))
        {
            _log.LogWarning(
                "SessionPushPilotJob: guarded off — set StageTwoPilot:Allowed=true + "
                + "StageTwoPilot:SessionId=<id> (or StageTwoPilot:SessionIds=<id,id,…>) and "
                + "POST /admin/functions/SessionPushPilotJob.");
            return;
        }

        // The session id(s): the single-pilot SessionId AND/OR the bulk SessionIds
        // (comma-separated), de-duplicated in order.
        var ids = new List<int>();
        if (int.TryParse(_config["StageTwoPilot:SessionId"], out var single)) ids.Add(single);
        foreach (var part in (_config["StageTwoPilot:SessionIds"] ?? string.Empty)
                     .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, out var id)) ids.Add(id);
        }
        ids = ids.Distinct().ToList();
        if (ids.Count == 0)
        {
            _log.LogWarning(
                "SessionPushPilotJob: StageTwoPilot:SessionId / StageTwoPilot:SessionIds is "
                + "missing or carries no valid int — nothing pushed.");
            return;
        }

        var eventId = await _db.Events
            .Where(e => e.IsActive).Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);
        if (eventId is null)
        {
            _log.LogWarning("SessionPushPilotJob: no active edition — nothing pushed.");
            return;
        }

        // Create each session with the per-call ops mail SUPPRESSED, collecting every Zoho
        // write line so the run ends with ONE batched change mail (sessions + created halls).
        var allChanges = new List<string>();
        var created = 0;
        var failed = 0;
        foreach (var sessionId in ids)
        {
            var (ok, message, changes) = await _push.CreateOneAsync(
                eventId.Value, sessionId, suppressNotify: true, ct);
            allChanges.AddRange(changes);
            if (ok)
            {
                created++;
                _log.LogInformation(
                    "SessionPushPilotJob: session {SessionId} pushed — {Message}", sessionId, message);
            }
            else
            {
                failed++;
                _log.LogWarning(
                    "SessionPushPilotJob: session {SessionId} NOT pushed — {Message}", sessionId, message);
            }
        }

        // Operator 2026-07-23: ONE batched mail for the whole run (empty batch ⇒ no mail).
        if (_zohoChanges is not null)
            await _zohoChanges.NotifyAsync("Agenda / sessions", allChanges, ct);

        _log.LogInformation(
            "SessionPushPilotJob: run finished — {Created} created, {Failed} not pushed, "
            + "{Changes} Zoho change(s) in the batched ops mail.",
            created, failed, allChanges.Count);
    }
}
