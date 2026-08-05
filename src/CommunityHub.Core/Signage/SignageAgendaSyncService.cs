using CommunityHub.Core.Data;
using CommunityHub.Core.Domain.Signage;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Signage;

/// <summary>
/// §754 deliverable 1 — mirrors the COMPLETE Zoho Backstage agenda into
/// <see cref="AgendaActivity"/> every 5 minutes, so the signage screens render from a CEH table and
/// never from Zoho at request time.
/// </summary>
/// <remarks>
/// <para><b>Why a cache at all.</b> 15 screens re-reading a view every few seconds would put the
/// event's most visible surface behind a third-party API's availability and rate limits. The poll
/// interval is the operator's (§4): a change made in Backstage — new activity, moved room, changed
/// time, cancellation — reaches the wall within one cycle.</para>
///
/// <para>🔒 <b>THE FAIL-SAFE, which is the whole design.</b> A failed read and a genuinely-emptied
/// agenda are indistinguishable from here — the §301b contract, with a worse consequence: 15 blank
/// screens in the middle of a conference. So:</para>
/// <list type="number">
///   <item>the pull uses the STRICT pager, where any non-2xx page THROWS instead of ending the
///   enumeration quietly (the §585 silent-empty-list trap);</item>
///   <item>ANY failure — no token, HTTP, parse — writes NOTHING. The last-good rows and their
///   <see cref="AgendaActivity.LastSyncedAt"/> stay exactly as they were, so the screens keep
///   showing a slightly stale agenda instead of nothing;</item>
///   <item>🔒 <b>an EMPTY pull never empties a non-empty table.</b> It is reported and alerted as
///   suspicious and the rows are kept. A real "we deleted the whole agenda" is not something to
///   action within 5 minutes unattended; a spurious one during the event is unrecoverable.</item>
/// </list>
///
/// <para>⚠️ <b>An activity with no start time or no duration is SKIPPED, not guessed.</b> Every slot
/// decision in §5 is an overlap test, so a fabricated end time does not produce a slightly-wrong
/// card — it drops the activity out of the hour it belongs to, which on a wall is indistinguishable
/// from a cancellation. The count is surfaced instead, so a broken Backstage record is visible as a
/// number rather than as a missing card nobody notices.</para>
/// </remarks>
public sealed class SignageAgendaSyncService
{
    private readonly CommunityHubDbContext _db;
    private readonly ZohoClient _zoho;
    private readonly ZohoOptions _options;
    private readonly EngineAlertSender? _alerts;
    private readonly TimeProvider _clock;
    private readonly ILogger<SignageAgendaSyncService>? _log;

    // Overridable pull seam (default = the real Zoho call), mirroring the §38e engine: tests drive
    // the upsert/removal/fail-safe logic without HTTP.
    private readonly Func<CancellationToken, Task<IReadOnlyList<ZohoClient.BackstageAgendaActivity>>>? _pullOverride;

    public SignageAgendaSyncService(
        CommunityHubDbContext db, ZohoClient zoho, ZohoOptions options,
        EngineAlertSender? alerts = null, TimeProvider? clock = null,
        ILogger<SignageAgendaSyncService>? log = null,
        Func<CancellationToken, Task<IReadOnlyList<ZohoClient.BackstageAgendaActivity>>>? pullOverride = null)
    {
        _db = db; _zoho = zoho; _options = options; _alerts = alerts;
        _clock = clock ?? TimeProvider.System;
        _log = log;
        _pullOverride = pullOverride;
    }

    /// <summary>The outcome of one sync pass — for the job log, the admin sync-health panel and tests.</summary>
    /// <remarks>
    /// <see cref="Ok"/> false means NOTHING was written. <see cref="RefusedToEmpty"/> is a distinct
    /// outcome from a plain failure: the read succeeded and returned nothing, and we declined to act
    /// on it. Collapsing the two would hide the one case a human should look at.
    /// </remarks>
    public sealed record Result(
        bool Ok,
        string? FailureReason,
        int Pulled,
        int Added,
        int Updated,
        int Removed,
        int SkippedUnusable,
        bool RefusedToEmpty = false)
    {
        public static Result Failed(string reason) => new(false, reason, 0, 0, 0, 0, 0);

        /// <summary>The pull worked and returned nothing while the table holds rows — kept, not applied.</summary>
        public static Result KeptOnEmptyPull(int existing) =>
            new(false,
                $"Zoho returned an EMPTY agenda while {existing} activities are cached. "
                + "Kept the cached agenda — an empty pull is never applied.",
                0, 0, 0, 0, 0, RefusedToEmpty: true);

        /// <summary>True when this pass changed the cached agenda in any way.</summary>
        public bool ChangedAnything => Added > 0 || Updated > 0 || Removed > 0;
    }

    /// <summary>Run one sync pass for an edition.</summary>
    public async Task<Result> RunAsync(int eventId, CancellationToken ct = default)
    {
        if (!_options.Enabled)
            return Result.Failed("Zoho is switched off, so the agenda was not pulled.");

        IReadOnlyList<ZohoClient.BackstageAgendaActivity> pulled;
        try
        {
            if (_pullOverride is not null)
            {
                pulled = await _pullOverride(ct);
            }
            else
            {
                var token = await _zoho.GetAccessTokenAsync(ct);
                if (string.IsNullOrWhiteSpace(token))
                    return await FailAsync(eventId, "No Zoho access token could be obtained.", ct);

                pulled = await _zoho.GetBackstageAgendaAsync(token!, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 🔒 The strict pager throwing is the DESIGNED path for a partial read, not an
            // unexpected error — catching it here is what keeps the screens on the last-good agenda.
            return await FailAsync(eventId, $"The Zoho agenda could not be read: {ex.Message}", ct);
        }

        var now = _clock.GetUtcNow();
        var existing = await _db.Set<AgendaActivity>()
            .Where(a => a.EventId == eventId)
            .ToListAsync(ct);

        // Only activities we can actually place in an hour slot are cacheable.
        var usable = pulled
            .Where(a => a.SessionId.Length > 0
                        && a.StartsAt is not null
                        && a.DurationMinutes is > 0)
            .ToList();
        var skipped = pulled.Count - usable.Count;

        // 🔒 THE FAIL-SAFE. Nothing usable came back but we hold a cached agenda ⇒ keep it.
        if (usable.Count == 0 && existing.Count > 0)
        {
            _log?.LogWarning(
                "SignageAgendaSync: EMPTY pull ({Pulled} raw, {Skipped} unusable) while {Existing} cached — kept.",
                pulled.Count, skipped, existing.Count);
            await AlertAsync(
                "Signage agenda: empty pull was NOT applied",
                $"<p>The Zoho agenda pull returned <b>{pulled.Count}</b> activities "
                + $"({skipped} of them unusable) while CEH holds <b>{existing.Count}</b> cached "
                + "activities for the signage screens.</p>"
                + "<p><b>The cached agenda was kept and the screens are unaffected.</b> CEH never "
                + "applies an empty pull, because a failed read and a genuinely-cleared agenda look "
                + "identical from here.</p>"
                + "<p>If the agenda really was cleared in Zoho, the screens will keep showing the old "
                + "one until an activity exists again.</p>",
                $"signage-agenda-empty-{eventId}", ct);
            return Result.KeptOnEmptyPull(existing.Count);
        }

        var byId = existing.ToDictionary(a => a.BackstageSessionId, StringComparer.OrdinalIgnoreCase);
        var live = new HashSet<string>(usable.Select(a => a.SessionId), StringComparer.OrdinalIgnoreCase);
        int added = 0, updated = 0;

        foreach (var a in usable)
        {
            var speakers = a.Speakers.Count > 0 ? string.Join(", ", a.Speakers) : null;
            var startsAt = a.StartsAt!.Value.ToUniversalTime();
            var endsAt = startsAt.AddMinutes(a.DurationMinutes!.Value);

            if (!byId.TryGetValue(a.SessionId, out var row))
            {
                _db.Set<AgendaActivity>().Add(new AgendaActivity
                {
                    EventId = eventId,
                    BackstageSessionId = a.SessionId,
                    Title = a.Title,
                    StartsAt = startsAt,
                    EndsAt = endsAt,
                    Room = a.Room,
                    Track = a.Track,
                    ActivityType = a.ActivityType,
                    Speakers = speakers,
                    DayIndex = a.DayIndex,
                    LastSyncedAt = now,
                });
                added++;
                continue;
            }

            // Count an UPDATE only when a rendered field really moved, so the sync-health panel
            // reports change rather than "15 activities touched" every 5 minutes forever.
            var changed = row.Title != a.Title
                || row.StartsAt != startsAt
                || row.EndsAt != endsAt
                || row.Room != a.Room
                || row.Track != a.Track
                || row.ActivityType != a.ActivityType
                || row.Speakers != speakers
                || row.DayIndex != a.DayIndex;

            row.Title = a.Title;
            row.StartsAt = startsAt;
            row.EndsAt = endsAt;
            row.Room = a.Room;
            row.Track = a.Track;
            row.ActivityType = a.ActivityType;
            row.Speakers = speakers;
            row.DayIndex = a.DayIndex;
            // Stamped on EVERY pass, changed or not — it answers "when did we last confirm this
            // against Zoho", which is the question the sync-health panel asks.
            row.LastSyncedAt = now;
            if (changed) updated++;
        }

        var removed = existing.Where(r => !live.Contains(r.BackstageSessionId)).ToList();
        if (removed.Count > 0) _db.Set<AgendaActivity>().RemoveRange(removed);

        await _db.SaveChangesAsync(ct);

        _log?.LogInformation(
            "SignageAgendaSync: pulled {Pulled}, added {Added}, updated {Updated}, removed {Removed}, skipped {Skipped}.",
            pulled.Count, added, updated, removed.Count, skipped);

        return new Result(true, null, pulled.Count, added, updated, removed.Count, skipped);
    }

    private async Task<Result> FailAsync(int eventId, string reason, CancellationToken ct)
    {
        _log?.LogWarning("SignageAgendaSync: {Reason}", reason);
        await AlertAsync(
            "Signage agenda sync failed",
            $"<p>The signage agenda could not be refreshed from Zoho.</p><p><b>{reason}</b></p>"
            + "<p>The screens keep displaying the last agenda CEH successfully pulled, so nothing "
            + "went blank. They will correct themselves on the next successful sync.</p>",
            $"signage-agenda-fail-{eventId}", ct);
        return Result.Failed(reason);
    }

    // devSilent (§752.9): on DEV this is a test agenda against a test Zoho edition, and a screen
    // wall nobody is looking at. 🔒 The failure is still LOGGED and still visible in the admin
    // sync-health panel in both environments — what is suppressed is the mail, never the state.
    private Task AlertAsync(string subject, string html, string throttleKey, CancellationToken ct) =>
        _alerts?.AlertAsync(subject, html, ct, throttleKey: throttleKey, devSilent: true)
        ?? Task.CompletedTask;
}
