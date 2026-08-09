using System.Globalization;

namespace CommunityHub.Core.Config;

/// <summary>
/// The resolved site-wide topbar ticket banner to render: the message text, an
/// optional href (the public ticket URL once the sale is open), and whether the
/// banner is shown at all. Built by <see cref="TicketBannerBuilder"/> from the
/// edition's <see cref="TicketSaleConfig"/> + the edition timezone + the current
/// moment, so the copy (date/time + before/after-open state) is config-driven and
/// can never silently go stale the way the old <c>Layout.TicketInfo</c> resx
/// literal did.
/// </summary>
/// <param name="Visible">
/// True when the topbar should render this config-driven banner. False means the
/// caller should fall back to its static literal (config absent / disabled / no
/// open datetime) OR render nothing at all (sale open + <c>afterOpen=hide</c>) —
/// the two are distinguished by <see cref="Suppressed"/>.
/// </param>
/// <param name="Message">The banner copy (English). Empty when not visible.</param>
/// <param name="Href">The public ticket URL to link to, or null for plain text.</param>
/// <param name="Suppressed">
/// True only when the banner is intentionally hidden because the sale has opened
/// and the config asked to hide it (<c>afterOpen=hide</c>). The layout uses this
/// to render NOTHING (not the fallback literal). When false and not visible, the
/// config simply doesn't apply and the caller should use its fallback.
/// </param>
/// <param name="OpensAt">
/// §995 — the absolute moment the sale opens, set ONLY in the before-open state, so the layout can
/// tick a LIVE countdown from it. Null in every other state.
/// <para>🔑 The instant is handed to the browser rather than a rendered string because a
/// server-rendered countdown is stale the second it is sent, and this banner sits on a cached,
/// long-lived layout — "in 2 days" would still say that tomorrow. <see cref="TicketBannerView.Message"/>
/// carries the same value server-rendered, which is what a no-JS reader sees.</para>
/// </param>
public sealed record TicketBannerView(
    bool Visible, string Message, string? Href, bool Suppressed,
    DateTimeOffset? OpensAt = null);

/// <summary>
/// Pure (no DB / no I/O; clock + timezone passed in) builder for the site-wide
/// topbar ticket banner.
///
/// <para><b>Show / hide rule.</b> Given the configured open moment
/// (<see cref="TicketSaleConfig.OpensAtLocal"/>, interpreted in the edition
/// timezone) and "now":</para>
/// <list type="bullet">
///   <item><description><b>Before the open moment</b> — show
///   "<c>tickets on sale &lt;date&gt; at &lt;time&gt;</c>" (the date/time formatted
///   from config; plain text, no link yet).</description></item>
///   <item><description><b>At / after the open moment</b> — switch to the
///   "tickets on sale now" state (a link when a ticket URL is configured), OR
///   render nothing when <c>afterOpen=hide</c>.</description></item>
/// </list>
///
/// <para><b>Fallback contract.</b> Returns a non-visible, non-suppressed view
/// (caller keeps its static literal) when the config is null, disabled, or has no
/// parseable open datetime — so the change is purely additive and nothing breaks
/// if the block is absent.</para>
/// </summary>
public static class TicketBannerBuilder
{
    /// <summary>
    /// §995 — the "before sale" message: a COUNTDOWN. {0} = the remaining time, already formatted.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-09: *"i still dont see the count down in the header yet - it should
    /// replace the text Tickets on sale 11 Aug 2026 at 08:00 (UTC+02:00) - leave the button for
    /// Visit Event Site in the header"*.</para>
    ///
    /// <para>🔑 <b>A countdown answers the question the date did not.</b> "11 Aug 2026 at 08:00
    /// (UTC+02:00)" makes the reader work out how long that is, in a timezone that may not be
    /// theirs. The offset label was the tell: it is only there because an absolute time is
    /// meaningless without one. A duration needs no zone at all.</para>
    /// </remarks>
    public const string BeforeFormat = "Tickets on sale in {0}";

    /// <summary>The old absolute-date format, kept for the no-countdown edge case (see below).</summary>
    public const string BeforeDateFormat = "Tickets on sale {0} at {1} ({2})";

    /// <summary>The "sale is open" message (used for both the link + plain-text states).</summary>
    public const string OnSaleMessage = "Tickets are now on sale";

    /// <summary>The fallback view: config does not apply, caller keeps its static literal.</summary>
    public static readonly TicketBannerView Fallback =
        new(Visible: false, Message: string.Empty, Href: null, Suppressed: false);

    /// <summary>
    /// Build the banner view for the given config, edition timezone and current moment.
    /// </summary>
    /// <param name="ticket">The bound ticketSale config (may be null).</param>
    /// <param name="timezoneId">
    /// The edition timezone (IANA or Windows id, e.g. <c>Europe/Copenhagen</c>) the
    /// <see cref="TicketSaleConfig.OpensAtLocal"/> wall time is interpreted in.
    /// </param>
    /// <param name="now">The current moment (inject a fixed clock in tests).</param>
    public static TicketBannerView Build(
        TicketSaleConfig? ticket, string? timezoneId, DateTimeOffset now)
    {
        // Absent or explicitly off ⇒ config does not apply (caller keeps literal).
        if (ticket is null || !ticket.Enabled)
            return Fallback;

        // No parseable open moment ⇒ we cannot compute before/after; fall back.
        if (!TryParseOpensAt(ticket.OpensAtLocal, timezoneId, out var opensAt))
            return Fallback;

        var url = Trim(ticket.TicketUrl);

        if (now < opensAt)
        {
            // §995 BEFORE the sale opens — a COUNTDOWN, not an absolute date. Plain text (no link
            // until the sale is actually open). `OpensAt` rides along so the layout can tick it
            // live in the browser; this string is the server-rendered value a no-JS reader sees.
            var message = string.Format(
                CultureInfo.InvariantCulture, BeforeFormat, FormatCountdown(opensAt - now));
            return new TicketBannerView(
                Visible: true, Message: message, Href: null, Suppressed: false,
                OpensAt: opensAt);
        }

        // AT / AFTER the open moment.
        if (IsHide(ticket.AfterOpen))
        {
            // Config asks to remove the banner once the sale is open — render
            // NOTHING (suppressed), NOT the fallback literal.
            return new TicketBannerView(
                Visible: false, Message: string.Empty, Href: null, Suppressed: true);
        }

        // "On sale" state — a link when a ticket URL is configured, else plain text.
        return new TicketBannerView(
            Visible: true, Message: OnSaleMessage, Href: url, Suppressed: false);
    }

    /// <summary>
    /// §995 — a remaining duration as the topbar prints it: <c>2d 04:13:22</c>, or
    /// <c>04:13:22</c> inside the last day.
    /// </summary>
    /// <remarks>
    /// <para>🔒 <b>Seconds are always shown</b>, because the same string is ticked once per second
    /// in the browser: a countdown whose smallest unit is minutes looks frozen for 59 seconds at a
    /// time, which reads as broken rather than as a slow clock.</para>
    ///
    /// <para>⚠️ <b>Days are separated out rather than rolled into hours.</b> "52:13:22" is a number
    /// the reader has to divide; "2d 04:13:22" is one they can act on. The mobile topbar is ~360px,
    /// so this stays compact rather than spelling out "2 days, 4 hours".</para>
    ///
    /// <para>A non-positive span renders as all zeros — the caller is past the open moment and is
    /// in the on-sale state anyway; this is a guard, not a display case.</para>
    /// </remarks>
    public static string FormatCountdown(TimeSpan remaining)
    {
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;

        var hms = string.Format(
            CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}",
            remaining.Hours, remaining.Minutes, remaining.Seconds);

        return remaining.Days > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0}d {1}", remaining.Days, hms)
            : hms;
    }

    /// <summary>
    /// Parse the configured open wall time (ISO local, no offset) and anchor it to
    /// the edition timezone, returning the absolute moment. Returns false for a
    /// blank / unparseable value so the caller falls back to its static literal.
    /// </summary>
    private static bool TryParseOpensAt(
        string? opensAtLocal, string? timezoneId, out DateTimeOffset opensAt)
    {
        opensAt = default;
        var raw = Trim(opensAtLocal);
        if (raw is null) return false;

        // Parse as an UNZONED local wall time (RoundtripKind keeps any explicit
        // offset the operator wrote, but the shipped value is zone-less).
        if (!DateTime.TryParse(
                raw, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var dt))
            return false;

        if (dt.Kind == DateTimeKind.Utc)
        {
            opensAt = new DateTimeOffset(dt);
            return true;
        }

        // Unspecified wall time ⇒ interpret it in the edition timezone.
        var tz = EventLocalTime.Resolve(timezoneId);
        var unspecified = DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
        var offset = tz.GetUtcOffset(unspecified);
        opensAt = new DateTimeOffset(unspecified, offset);
        return true;
    }

    private static bool IsHide(string? afterOpen) =>
        string.Equals(Trim(afterOpen), "hide", StringComparison.OrdinalIgnoreCase);

    private static string? Trim(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
