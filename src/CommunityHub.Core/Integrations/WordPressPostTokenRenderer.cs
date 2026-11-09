using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace CommunityHub.Core.Integrations;

/// <summary>§1379.8 — one speaker in a per-track list; <paramref name="Url"/> null ⇒ the name is not linked.</summary>
public sealed record WordPressPostSpeaker(string Name, string? Url);

/// <summary>§1379.8 — one track and its speakers, in display order.</summary>
public sealed record WordPressPostTrack(string Track, IReadOnlyList<WordPressPostSpeaker> Speakers);

/// <summary>§1379.8 — one sponsor/exhibitor company: the public name (DESIGN §6) and an optional website.</summary>
public sealed record WordPressPostCompany(string Name, string? WebsiteUrl);

/// <summary>
/// §1379.8 — every value a refreshed post can show, computed from CEH data at render time
/// (<see cref="WordPressPostDataService"/>). Never typed: operator 2026-11-07 <i>"remember the count must
/// be dynamic"</i>.
/// </summary>
public sealed record WordPressPostData(
    int SpeakerCount,
    int CountryCount,
    int MvpCount,
    int RdCount,
    int MicrosoftEmployeeCount,
    int MicrosoftExpertCount,
    int SessionCount,
    int MasterClassCount,
    int PanelCount,
    int TrackCount,
    int SponsorCount,
    int ExhibitorCount,
    IReadOnlyList<WordPressPostTrack> SpeakersByTrack,
    IReadOnlyList<WordPressPostCompany> Sponsors,
    IReadOnlyList<WordPressPostCompany> Exhibitors,
    // 🔴 Operator 2026-11-07: "it must show as merged like 61 mvp/rd" — the ONE figure for MVPs and RDs together,
    // each person once (an MVP who is also an RD counts once). Split MVP/RD counts are never shown.
    int MvpOrRdCount = 0,
    // Operator 2026-11-07: female vs male, "calc each week" — from the gender the speaker gave on the speaker form.
    int FemaleSpeakerCount = 0,
    int MaleSpeakerCount = 0);

/// <summary>The rendered HTML and every <c>{{Token}}</c> the renderer did not recognise (left in place).</summary>
public sealed record WordPressPostRender(string Html, IReadOnlyList<string> UnknownTokens);

/// <summary>
/// §1379.8 — replaces <c>{{Token}}</c> placeholders in a post's body template with values from
/// <see cref="WordPressPostData"/>. Pure: no I/O, so every token is unit-testable.
/// </summary>
/// <remarks>
/// An unknown token is LEFT UNTOUCHED and reported, never blanked — a typo must show up in the preview
/// rather than silently delete a sentence from a live page.
/// </remarks>
public static class WordPressPostTokenRenderer
{
    private static readonly Regex TokenPattern = new(@"\{\{\s*([A-Za-z0-9_]+)\s*\}\}", RegexOptions.Compiled);

    /// <summary>The tokens this renderer understands, for the organizer page's help text.</summary>
    public static readonly IReadOnlyList<string> SupportedTokens = new[]
    {
        "SpeakerCountRounded", "SpeakerCount", "CountryCount", "MvpRdCount", "MvpCount", "RdCount",
        "FemaleSpeakerCount", "MaleSpeakerCount",
        "MvpRdCountPercent", "MicrosoftExpertCountPercent", "MicrosoftCountPercent",
        "FemaleSpeakerCountPercent", "MaleSpeakerCountPercent",
        "MicrosoftCount", "MicrosoftExpertCount",
        "SessionCount", "MasterClassCount", "PanelCount", "TrackCount",
        "SponsorCount", "ExhibitorCount",
        "SpeakersByTrack", "SponsorList", "ExhibitorList",
    };

    /// <summary>
    /// The headline form: rounded DOWN to tens with a plus ("92" → "90+"), because the number only grows
    /// and an exact figure on a weekly-refreshed page is stale by Tuesday. Below ten it is the exact number.
    /// </summary>
    public static string Rounded(int n) => n < 10 ? n.ToString() : $"{n / 10 * 10}+";

    public static WordPressPostRender Render(string? template, WordPressPostData data)
    {
        var unknown = new List<string>();
        if (string.IsNullOrEmpty(template)) return new WordPressPostRender(string.Empty, unknown);

        var html = TokenPattern.Replace(template, m =>
        {
            var name = m.Groups[1].Value;
            var value = Resolve(name, data);
            if (value is null)
            {
                if (!unknown.Contains(name, StringComparer.Ordinal)) unknown.Add(name);
                return m.Value;
            }
            return value;
        });

        return new WordPressPostRender(html, unknown);
    }

    private static string? Resolve(string name, WordPressPostData d)
    {
        // Operator 2026-11-07: "include percent everywhere" — any SPEAKER-based count token + "Percent" gives its share of
        // all speakers, whole percent (e.g. {{MvpRdCountPercent}} → "63"). Session/sponsor counts have no speaker share.
        if (name.EndsWith("Percent", StringComparison.Ordinal) && PercentOfSpeakers.Contains(name[..^"Percent".Length]))
        {
            var count = Resolve(name[..^"Percent".Length], d);
            return int.TryParse(count, out var n) && d.SpeakerCount > 0
                ? Math.Round(100.0 * n / d.SpeakerCount, MidpointRounding.AwayFromZero).ToString("0")
                : null;
        }
        return ResolveCount(name, d);
    }

    /// <summary>The count tokens that are a share of the speakers, so "{{XPercent}}" is meaningful for them.</summary>
    private static readonly HashSet<string> PercentOfSpeakers = new(StringComparer.Ordinal)
    {
        "MvpRdCount", "MvpCount", "RdCount", "MicrosoftCount", "MicrosoftExpertCount", "FemaleSpeakerCount", "MaleSpeakerCount",
    };

    private static string? ResolveCount(string name, WordPressPostData d) => name switch
    {
        "SpeakerCountRounded" => Rounded(d.SpeakerCount),
        "FemaleSpeakerCount" => d.FemaleSpeakerCount.ToString(),
        "MaleSpeakerCount" => d.MaleSpeakerCount.ToString(),
        "SpeakerCount" => d.SpeakerCount.ToString(),
        "CountryCount" => d.CountryCount.ToString(),
        "MvpRdCount" => d.MvpOrRdCount.ToString(),   // the merged figure — use this one, e.g. "61 MVPs/RDs"
        "MvpCount" => d.MvpCount.ToString(),
        "RdCount" => d.RdCount.ToString(),
        "MicrosoftCount" => d.MicrosoftEmployeeCount.ToString(),
        "MicrosoftExpertCount" => d.MicrosoftExpertCount.ToString(),
        "SessionCount" => d.SessionCount.ToString(),
        "MasterClassCount" => d.MasterClassCount.ToString(),
        "PanelCount" => d.PanelCount.ToString(),
        "TrackCount" => d.TrackCount.ToString(),
        "SponsorCount" => d.SponsorCount.ToString(),
        "ExhibitorCount" => d.ExhibitorCount.ToString(),
        "SpeakersByTrack" => SpeakersByTrackHtml(d.SpeakersByTrack),
        "SponsorList" => CompanyListHtml(d.Sponsors),
        "ExhibitorList" => CompanyListHtml(d.Exhibitors),
        _ => null,
    };

    private static string Enc(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);

    internal static string SpeakersByTrackHtml(IReadOnlyList<WordPressPostTrack> tracks)
    {
        var sb = new StringBuilder();
        foreach (var t in tracks)
        {
            if (t.Speakers.Count == 0) continue;
            sb.Append("<h3>").Append(Enc(t.Track)).Append("</h3>\n<ul>\n");
            foreach (var s in t.Speakers)
            {
                sb.Append("<li>");
                if (IsHttpUrl(s.Url))
                    sb.Append("<a href=\"").Append(Enc(s.Url)).Append("\">").Append(Enc(s.Name)).Append("</a>");
                else
                    sb.Append(Enc(s.Name));
                sb.Append("</li>\n");
            }
            sb.Append("</ul>\n");
        }
        return sb.ToString();
    }

    internal static string CompanyListHtml(IReadOnlyList<WordPressPostCompany> companies)
    {
        if (companies.Count == 0) return string.Empty;
        var sb = new StringBuilder("<ul>\n");
        foreach (var c in companies)
        {
            sb.Append("<li>");
            if (IsHttpUrl(c.WebsiteUrl))
                sb.Append("<a href=\"").Append(Enc(c.WebsiteUrl)).Append("\" target=\"_blank\" rel=\"noopener\">")
                  .Append(Enc(c.Name)).Append("</a>");
            else
                sb.Append(Enc(c.Name));
            sb.Append("</li>\n");
        }
        sb.Append("</ul>\n");
        return sb.ToString();
    }

    private static bool IsHttpUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url, UriKind.Absolute, out var u)
        && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);
}
