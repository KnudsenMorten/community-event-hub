using System.Globalization;

namespace CommunityHub.Core.Config;

/// <summary>
/// The single, pure authority for showing a moment in the EVENT's local time
/// instead of raw UTC (REQUIREMENTS §21 — "MasterClass / public pages shown in
/// local time, not UTC"). The edition timezone is plain operator config
/// (<c>event.&lt;edition&gt;.json -&gt; dates.timezone</c>, an IANA id such as
/// <c>Europe/Copenhagen</c>), surfaced via <see cref="EditionDates.Timezone"/>.
///
/// Public timestamps (when logistics were last updated, when the latest survey
/// response landed) read naturally to an attendee when shown in the venue's wall
/// time. This helper is the one place that resolves the zone + formats, so the
/// rule can never drift between pages.
///
/// Resolution is defensive by design: it accepts an IANA id (works on Linux and
/// on .NET 8 Windows via ICU) AND the legacy Windows id (<c>Romance Standard
/// Time</c>), tries an IANA↔Windows swap if the first lookup misses, and falls
/// back to plain UTC (never throwing) when the zone is blank/unknown so a bad
/// config can never 500 a public page.
/// </summary>
public static class EventLocalTime
{
    /// <summary>The label shown when no usable timezone resolves — honest, not a guess.</summary>
    public const string UtcLabel = "UTC";

    /// <summary>
    /// Resolve an edition timezone id (IANA or Windows) to a <see cref="TimeZoneInfo"/>.
    /// Returns <see cref="TimeZoneInfo.Utc"/> for a blank/unknown id (never throws).
    /// </summary>
    public static TimeZoneInfo Resolve(string? timezoneId)
    {
        if (string.IsNullOrWhiteSpace(timezoneId))
            return TimeZoneInfo.Utc;

        var id = timezoneId.Trim();

        if (TryFind(id, out var tz)) return tz!;

        // The platform may only know the OTHER family of id (IANA vs Windows).
        // .NET 8 ships the conversion both ways — try a swap before giving up.
        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var winId)
            && TryFind(winId!, out tz))
            return tz!;

        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var ianaId)
            && TryFind(ianaId!, out tz))
            return tz!;

        return TimeZoneInfo.Utc;
    }

    private static bool TryFind(string id, out TimeZoneInfo? tz)
    {
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (TimeZoneNotFoundException) { tz = null; return false; }
        catch (InvalidTimeZoneException) { tz = null; return false; }
    }

    /// <summary>
    /// Convert <paramref name="when"/> into the edition's local wall time.
    /// </summary>
    public static DateTimeOffset ToLocal(DateTimeOffset when, string? timezoneId)
        => TimeZoneInfo.ConvertTime(when, Resolve(timezoneId));

    /// <summary>
    /// A short zone label for the resolved moment — the IANA/Windows id is too
    /// noisy for the UI, so we show the GMT offset (e.g. <c>UTC+02:00</c>), or
    /// the plain <see cref="UtcLabel"/> when the offset is zero or the zone is
    /// unknown. Honest and locale-independent.
    /// </summary>
    public static string ZoneLabel(DateTimeOffset when, string? timezoneId)
    {
        var local = ToLocal(when, timezoneId);
        var offset = local.Offset;
        if (offset == TimeSpan.Zero) return UtcLabel;
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var abs = offset.Duration();
        return $"UTC{sign}{abs.Hours:00}:{abs.Minutes:00}";
    }

    /// <summary>
    /// Format a moment in edition-local time using <paramref name="format"/>
    /// (invariant culture), suffixed with the resolved <see cref="ZoneLabel"/>
    /// — e.g. <c>2027-02-03 14:05 UTC+01:00</c>. The one call public pages use
    /// so the timestamp + its zone stay together.
    /// </summary>
    public static string Format(
        DateTimeOffset when, string? timezoneId, string format = "yyyy-MM-dd HH:mm")
    {
        var local = ToLocal(when, timezoneId);
        return $"{local.ToString(format, CultureInfo.InvariantCulture)} {ZoneLabel(when, timezoneId)}";
    }
}
