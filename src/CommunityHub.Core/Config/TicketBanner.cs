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
public sealed record TicketBannerView(
    bool Visible, string Message, string? Href, bool Suppressed);

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
    /// <summary>The "before sale" message format. {0} = date, {1} = time, {2} = zone label.</summary>
    public const string BeforeFormat = "Tickets on sale {0} at {1} ({2})";

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
            // BEFORE the sale opens — announce the date/time in edition-local
            // wall time. Plain text (no link until the sale is actually open).
            var local = EventLocalTime.ToLocal(opensAt, timezoneId);
            var date = local.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
            var time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
            var zone = EventLocalTime.ZoneLabel(opensAt, timezoneId);
            var message = string.Format(
                CultureInfo.InvariantCulture, BeforeFormat, date, time, zone);
            return new TicketBannerView(
                Visible: true, Message: message, Href: null, Suppressed: false);
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
