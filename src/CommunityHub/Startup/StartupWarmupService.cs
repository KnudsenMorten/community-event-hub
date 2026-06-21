using CommunityHub.Core.Data;
using CommunityHub.Core.Participants;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Startup;

/// <summary>
/// Self-warms the AUTHENTICATED hot path on every app start (deploys AND platform
/// recycles), in the background, so the FIRST real sign-in is not slow.
///
/// Why this exists: the infra is already always-on (App Service "Always On" +
/// always-on DTU SQL — nothing auto-pauses), yet the first sign-in after a restart
/// hung ~30s. The cause is COLD-START of the authenticated code path: EF Core
/// compiles a query plan the first time each distinct LINQ shape runs, and the
/// hub/login queries (Tasks/Hotel/Dinner/Volunteer/Attendee/MasterClass + the
/// participant-by-email login lookup) are NOT exercised by the App Service
/// keep-alive ping (which only hits the anonymous "/"). On the small SKUs (B1 app /
/// Basic-S0 SQL) compiling + first-running ~12 query shapes serially costs tens of
/// seconds — and that bill landed on whichever human signed in first.
///
/// Running those exact queries here, once, at startup moves that cost off the user.
/// It is READ-ONLY, best-effort (never throws — a warm-up failure must not crash the
/// app or block readiness) and idempotent. On a slot-swap deploy the warmed worker
/// process is the one swapped into production, so production starts warm too.
/// </summary>
public sealed class StartupWarmupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<StartupWarmupService> _logger;

    public StartupWarmupService(
        IServiceScopeFactory scopes, ILogger<StartupWarmupService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Don't compete with the app's own first-request startup work; let routing
        // come up first, then warm the query plans in the background.
        try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
        catch (OperationCanceledException) { return; }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();
            var checklist = scope.ServiceProvider.GetRequiredService<ParticipantChecklistBuilder>();

            // The single active edition (CONTEXT.md §3). No event ⇒ nothing to warm.
            var eventId = await db.Events
                .Where(e => e.IsActive).Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);
            if (eventId is null)
            {
                _logger.LogInformation("Startup warm-up: no active event; skipped.");
                return;
            }
            var ev = eventId.Value;

            // A representative participant to drive the per-participant queries. The
            // SQL SHAPE is what we are compiling, so any one participant warms the
            // plan for every later sign-in. None ⇒ warm only the edition-scoped shapes.
            var rep = await db.Participants
                .Where(p => p.EventId == ev)
                .Select(p => new { p.Id, p.Email })
                .FirstOrDefaultAsync(ct);

            // --- Login path (PIN lookup by email) ---
            var probeEmail = rep?.Email ?? "warmup@example.invalid";
            _ = await db.Participants
                .Where(p => p.EventId == ev && p.Email == probeEmail)
                .Select(p => new { p.Id, p.Role, p.Email })
                .FirstOrDefaultAsync(ct);

            // --- Hub path (mirror Index.OnGetAsync's queries) ---
            int pid = rep?.Id ?? 0;
            _ = await db.Participants
                .Where(p => p.Id == pid).Select(p => p.WelcomeShownAt).FirstOrDefaultAsync(ct);
            _ = await db.Events
                .Where(e => e.Id == ev)
                .Select(e => new { e.CommunityName, e.DisplayName, e.CalendarSyncEnabled })
                .FirstOrDefaultAsync(ct);
            _ = await checklist.BuildAsync(ev, pid, ct);
            _ = await db.HotelBookings.AnyAsync(
                h => h.EventId == ev && h.ParticipantId == pid, ct);
            _ = await db.DinnerSignups.AnyAsync(
                d => d.EventId == ev && d.ParticipantId == pid, ct);
            _ = await db.VolunteerAvailabilities.AnyAsync(
                v => v.EventId == ev && v.ParticipantId == pid, ct);
            _ = await db.VolunteerTaskAssignments.CountAsync(
                a => a.EventId == ev && a.ParticipantId == pid, ct);
            _ = await db.VolunteerCategories.AnyAsync(
                c => c.EventId == ev && c.SupervisorParticipantId == pid, ct);
            _ = await db.Attendees
                .Where(a => a.EventId == ev && a.Email == probeEmail)
                .Select(a => (int?)a.BookingStatus).FirstOrDefaultAsync(ct);

            // --- Master Class path (attendee self-service + roster shapes) ---
            _ = await db.MasterClassSignups
                .Where(s => s.EventId == ev).Select(s => s.Id).FirstOrDefaultAsync(ct);
            _ = await db.Sessions
                .Where(s => s.EventId == ev).Select(s => s.Id).FirstOrDefaultAsync(ct);

            sw.Stop();
            _logger.LogInformation(
                "Startup warm-up complete in {Ms} ms (event {EventId}).",
                sw.ElapsedMilliseconds, ev);
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            // Best-effort: a warm-up failure must never take the app down or block
            // serving. The first user just pays the (un-warmed) cold cost as before.
            _logger.LogWarning(ex, "Startup warm-up failed (non-fatal).");
        }
    }
}
