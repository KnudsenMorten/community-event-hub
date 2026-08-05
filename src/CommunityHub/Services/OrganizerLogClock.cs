using CommunityHub.Core.Config;
using Microsoft.Extensions.Options;

namespace CommunityHub.Services;

/// <summary>
/// §751 — formats timestamps on the ORGANIZER LOG views in the event's local time instead of UTC
/// (operator 2026-08-01: <i>"can we make a global change which changes any logs presentation so
/// timer is in local time for viewer like i am in danish time now. logs shows in utc"</i>, scoped by
/// his follow-ups to <i>"only logs viewing"</i>, <i>"inside organizer interface"</i>).
/// </summary>
/// <remarks>
/// <para>🔴 <b>Why the logs showed UTC even though pages called <c>ToLocalTime()</c>.</b> That method
/// converts to the SERVER's zone, and the Azure Linux container runs in UTC — so it was a no-op in
/// production while looking correct on a developer's Danish machine. Twelve call sites had the same
/// silent bug. This resolves the EDITION's configured zone instead, which is the same value the
/// public pages and the calendar invites already use (<c>dates.timezone</c>, e.g.
/// <c>Europe/Copenhagen</c>), so a log line and an .ics for the same moment cannot disagree.</para>
///
/// <para>🔒 <b>Presentation only — storage stays UTC.</b> Every timestamp is persisted as a
/// <c>DateTimeOffset</c> in UTC and continues to be. Converting on the way out is reversible and
/// auditable; converting on the way in would corrupt the record permanently, and a log whose stored
/// instants depend on where the reader sat is not a log.</para>
///
/// <para>🔑 <b>The zone is always LABELLED</b> (<c>UTC+02:00</c>). A wall-clock time with no zone is
/// exactly what caused this complaint — a reader cannot tell a converted timestamp from an
/// unconverted one, so a silent conversion just moves the confusion rather than removing it.</para>
///
/// <para>Scoped, and the zone is resolved ONCE per request rather than per row: a log page renders
/// hundreds of timestamps and <c>TimeZoneInfo</c> lookup is not free.</para>
/// </remarks>
public sealed class OrganizerLogClock
{
    private readonly Lazy<string?> _timezoneId;

    public OrganizerLogClock(
        EventEditionConfigLoader loader,
        EventConfigOptions options,
        ILogger<OrganizerLogClock> log)
    {
        _timezoneId = new Lazy<string?>(() =>
        {
            try
            {
                return loader.Load(options.EventConfigPath).Dates?.Timezone;
            }
            catch (Exception ex)
            {
                // 🔒 A log page must never fail to render because a config file moved. Falling back
                // to UTC is honest: the label will say UTC, so the reader is told what they are
                // looking at rather than shown a wrong local time.
                log.LogWarning(ex, "Organizer logs: could not load the edition timezone; showing UTC.");
                return null;
            }
        });
    }

    /// <summary>The resolved edition timezone id, or null when it could not be loaded.</summary>
    public string? TimezoneId => _timezoneId.Value;

    /// <summary>
    /// The zone label shown once at the top of a log page — e.g. <c>UTC+02:00</c>.
    /// </summary>
    public string ZoneLabel => EventLocalTime.ZoneLabel(DateTimeOffset.UtcNow, TimezoneId);

    /// <summary>
    /// One log timestamp, in event-local time, WITHOUT the zone suffix — the page states the zone
    /// once in its header instead of repeating it on every row, which is unreadable in a table.
    /// </summary>
    public string Local(DateTimeOffset? when, string format = "dd MMM yyyy HH:mm")
        => when is null
            ? "—"
            : EventLocalTime.ToLocal(when.Value, TimezoneId)
                .ToString(format, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Same, with seconds — for logs where ordering within a minute matters.</summary>
    public string LocalPrecise(DateTimeOffset? when) => Local(when, "dd MMM yyyy HH:mm:ss");

    /// <summary>
    /// The standing note a log page shows so nobody has to guess. 🔑 Says what it IS and what it is
    /// NOT — "not UTC" is the half that answers the question the reader actually has.
    /// </summary>
    public string Note =>
        $"All times on this page are shown in event local time ({ZoneLabel}), not UTC. "
        + "Timestamps are stored in UTC and converted for display.";
}

