using System.Net;
using System.Text;

namespace CommunityHub.Core.Integrations;

/// <summary>The kind of content to generate (REQUIREMENTS §31).</summary>
public enum ContentKind
{
    /// <summary>A ticket-sales momentum update (counts, %, dates, ticket link).</summary>
    TicketSales = 0,

    /// <summary>A master-class announcement (each class: title, speaker(s), abstract).</summary>
    MasterClasses = 1,
}

/// <summary>The channel a generated post is shaped for.</summary>
public enum ContentChannel
{
    /// <summary>WordPress blog post (HTML body). v1 = draft only.</summary>
    WordPress = 0,

    /// <summary>LinkedIn company-page post (short plain text). v1 = held for validation.</summary>
    LinkedIn = 1,
}

/// <summary>
/// Event-level facts the templates fold into copy (configured, since the Event
/// entity carries no dates/URLs). Kept out of the engine so it stays pure.
/// </summary>
/// <param name="EventName">Display name, e.g. "Experts Live Denmark 2027".</param>
/// <param name="DatesText">Human dates, e.g. "9–10 June 2027".</param>
/// <param name="VenueText">Venue line, e.g. "Bella Center, Copenhagen".</param>
/// <param name="TicketsUrl">Where to buy tickets.</param>
/// <param name="AgendaUrl">The agenda / speaker profiles page.</param>
public sealed record ContentContext(
    string EventName,
    string DatesText,
    string? VenueText,
    string? TicketsUrl,
    string? AgendaUrl);

/// <summary>One speaker on a master class (already resolved from the profile).</summary>
public sealed record MasterClassSpeaker(string Name, string? Tagline, string? Company);

/// <summary>One master class to announce.</summary>
public sealed record MasterClassContentItem(
    string Title, string? Abstract, string? Track,
    IReadOnlyList<MasterClassSpeaker> Speakers, string? PublicUrl);

/// <summary>
/// A rendered post ready for a channel: a WordPress draft (Title + BodyHtml) AND
/// the short LinkedIn text + tags. The operator picks which channel(s) to use.
/// </summary>
public sealed record GeneratedContent(
    ContentKind Kind,
    string Title,
    string BodyHtml,
    string ShortText,
    IReadOnlyList<string> Tags);

/// <summary>
/// Pure, deterministic content generator for §31. Turns CEH/Zoho data into a
/// WordPress draft body + a LinkedIn short text, in the professional,
/// community-focused tone of expertslive.dk (no hard-sell). No I/O — callers pass
/// the data in, so this is fully unit-testable. v1 produces DRAFTS only.
/// </summary>
public sealed class ContentTemplateEngine
{
    /// <summary>
    /// A ticket-sales momentum update. Uses the aggregate attendee telemetry
    /// (count, countries, % on a 2-day ticket, top countries) folded into copy
    /// with the event dates + a tickets link.
    /// </summary>
    public GeneratedContent BuildTicketSales(AttendeeTelemetry t, ContentContext ctx)
    {
        var sold = t.TotalAll;
        var countries = t.Tables
            .FirstOrDefault(x => x.Title.Contains("from", StringComparison.OrdinalIgnoreCase))
            ?.Slices.Count(s => s.Label != "—") ?? 0;
        var twoDayPct = t.SegmentKey == "all" ? t.Pct2DayInSegment : 0;

        var title = $"{ctx.EventName} — registrations are building ahead of {ctx.DatesText}";

        var lead =
            $"Excitement is building for {ctx.EventName} on {ctx.DatesText}"
            + (string.IsNullOrWhiteSpace(ctx.VenueText) ? "" : $" at {ctx.VenueText}")
            + $". So far {sold:N0} community members"
            + (countries > 1 ? $" from {countries} countries" : "")
            + " have secured their seat"
            + (twoDayPct > 0 ? $", and {twoDayPct}% of them are joining us for the full two-day experience including the pre-day Master Classes" : "")
            + ".";

        // ---- WordPress (HTML) ----
        var html = new StringBuilder();
        html.Append("<!-- wp:paragraph -->\n<p>").Append(Enc(lead)).Append("</p>\n<!-- /wp:paragraph -->\n");

        var topCountries = t.Tables
            .FirstOrDefault(x => x.Title.Contains("from", StringComparison.OrdinalIgnoreCase))
            ?.Slices.Where(s => s.Label != "—").Take(5).ToList();
        if (topCountries is { Count: > 0 })
        {
            html.Append("<!-- wp:paragraph -->\n<p><strong>Where the community is coming from:</strong></p>\n<!-- /wp:paragraph -->\n");
            html.Append("<!-- wp:list -->\n<ul>\n");
            foreach (var c in topCountries)
                html.Append("<li>").Append(Enc(c.Label)).Append(" — ").Append(c.Count.ToString("N0")).Append("</li>\n");
            html.Append("</ul>\n<!-- /wp:list -->\n");
        }

        if (!string.IsNullOrWhiteSpace(ctx.TicketsUrl))
            html.Append("<!-- wp:paragraph -->\n<p>Don't miss out — <a href=\"")
                .Append(Enc(ctx.TicketsUrl)).Append("\">secure your ticket here</a>. We can't wait to see you there!</p>\n<!-- /wp:paragraph -->\n");

        // ---- LinkedIn (short text) ----
        var li = new StringBuilder();
        li.Append("📣 ").Append(sold.ToString("N0")).Append(" community members")
          .Append(countries > 1 ? $" from {countries} countries" : "")
          .Append(" are already registered for ").Append(ctx.EventName).Append("!\n\n");
        li.Append("📅 ").Append(ctx.DatesText);
        if (!string.IsNullOrWhiteSpace(ctx.VenueText)) li.Append(" · ").Append(ctx.VenueText);
        li.Append('\n');
        if (twoDayPct > 0) li.Append("🎟️ ").Append(twoDayPct).Append("% are joining the full two-day experience (incl. pre-day Master Classes).\n");
        if (!string.IsNullOrWhiteSpace(ctx.TicketsUrl)) li.Append("\nSecure your seat: ").Append(ctx.TicketsUrl);

        return new GeneratedContent(ContentKind.TicketSales, title, html.ToString().TrimEnd(),
            li.ToString().TrimEnd(), new[] { "#ExpertsLive", "#ExpertsLiveDenmark", "#community" });
    }

    /// <summary>
    /// A master-class announcement. Mirrors the expertslive.dk announcement layout:
    /// an intro, then a block per master class with its speaker(s) + abstract, and a
    /// CTA to the agenda. Skips classes with no title.
    /// </summary>
    public GeneratedContent BuildMasterClasses(
        IReadOnlyList<MasterClassContentItem> items, ContentContext ctx)
    {
        var classes = items.Where(i => !string.IsNullOrWhiteSpace(i.Title)).ToList();

        var title = $"Master Classes at {ctx.EventName} — deep-dive learning on the pre-day";

        var lead =
            $"Ahead of {ctx.EventName} ({ctx.DatesText}), we're excited to reveal the "
            + $"{classes.Count} pre-day Master Class{(classes.Count == 1 ? "" : "es")} — full, hands-on "
            + "deep dives led by some of the community's most respected experts.";

        // ---- WordPress (HTML) ----
        var html = new StringBuilder();
        html.Append("<!-- wp:paragraph -->\n<p>").Append(Enc(lead)).Append("</p>\n<!-- /wp:paragraph -->\n");

        foreach (var c in classes)
        {
            html.Append("<!-- wp:heading -->\n<h2>").Append(Enc(c.Title)).Append("</h2>\n<!-- /wp:heading -->\n");

            var who = c.Speakers
                .Where(s => !string.IsNullOrWhiteSpace(s.Name))
                .Select(FormatSpeaker)
                .ToList();
            if (who.Count > 0)
                html.Append("<!-- wp:paragraph -->\n<p><strong>With ")
                    .Append(Enc(JoinHuman(who))).Append("</strong></p>\n<!-- /wp:paragraph -->\n");

            if (!string.IsNullOrWhiteSpace(c.Track))
                html.Append("<!-- wp:paragraph -->\n<p><em>Track: ").Append(Enc(c.Track!)).Append("</em></p>\n<!-- /wp:paragraph -->\n");

            if (!string.IsNullOrWhiteSpace(c.Abstract))
                html.Append("<!-- wp:paragraph -->\n<p>").Append(Enc(c.Abstract!)).Append("</p>\n<!-- /wp:paragraph -->\n");

            if (!string.IsNullOrWhiteSpace(c.PublicUrl))
                html.Append("<!-- wp:paragraph -->\n<p><a href=\"").Append(Enc(c.PublicUrl!))
                    .Append("\">Read more &amp; book your seat →</a></p>\n<!-- /wp:paragraph -->\n");
        }

        if (!string.IsNullOrWhiteSpace(ctx.AgendaUrl))
            html.Append("<!-- wp:paragraph -->\n<p>See the full agenda and all speaker profiles <a href=\"")
                .Append(Enc(ctx.AgendaUrl)).Append("\">here</a>. Master Classes are part of the two-day ticket — book early, seats are limited.</p>\n<!-- /wp:paragraph -->\n");

        // ---- LinkedIn (short text) ----
        var li = new StringBuilder();
        li.Append("🎓 Master Classes revealed for ").Append(ctx.EventName).Append("!\n\n");
        li.Append(classes.Count).Append(" hands-on pre-day deep dives:\n");
        foreach (var c in classes.Take(8))
        {
            var who = c.Speakers.Where(s => !string.IsNullOrWhiteSpace(s.Name)).Select(s => s.Name).ToList();
            li.Append("• ").Append(c.Title);
            if (who.Count > 0) li.Append(" — ").Append(JoinHuman(who));
            li.Append('\n');
        }
        if (!string.IsNullOrWhiteSpace(ctx.AgendaUrl)) li.Append("\nFull agenda: ").Append(ctx.AgendaUrl);

        return new GeneratedContent(ContentKind.MasterClasses, title, html.ToString().TrimEnd(),
            li.ToString().TrimEnd(), new[] { "#ExpertsLive", "#MasterClass", "#community" });
    }

    private static string FormatSpeaker(MasterClassSpeaker s)
    {
        var sb = new StringBuilder(s.Name);
        var role = !string.IsNullOrWhiteSpace(s.Tagline) ? s.Tagline
                 : !string.IsNullOrWhiteSpace(s.Company) ? s.Company : null;
        if (!string.IsNullOrWhiteSpace(role)) sb.Append(" (").Append(role).Append(')');
        return sb.ToString();
    }

    /// <summary>Join "A", "A and B", "A, B and C".</summary>
    private static string JoinHuman(IReadOnlyList<string> items) => items.Count switch
    {
        0 => string.Empty,
        1 => items[0],
        2 => $"{items[0]} and {items[1]}",
        _ => $"{string.Join(", ", items.Take(items.Count - 1))} and {items[^1]}",
    };

    private static string Enc(string s) => WebUtility.HtmlEncode(s);
}
