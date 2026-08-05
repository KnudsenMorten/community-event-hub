using System.Text.RegularExpressions;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// §824.15 — reads what a LinkedIn URL can actually tell us: the vanity slug, and the numeric
/// organization id on the rare occasions one is present.
/// </summary>
/// <remarks>
/// <para>🔑 <b>Why this is deliberately small.</b> A real company mention needs
/// <c>urn:li:organization:{id}</c>. The obvious plan — take the sponsor's stored profile URL and pull
/// the id out of it — was measured on 2026-08-04 and does not work: <b>all 14 sponsor URLs on record
/// are vanity slugs</b> (<c>/company/glueckkanja</c>), and the API lookup that would translate one is
/// refused for this app (§824.14c). So this parser is not the id source; it is a convenience that
/// catches the case where somebody pasted a URL that already carries the number, which is what a
/// company's own admin view produces (<c>/company/98360537/admin/dashboard/</c>).</para>
///
/// <para>⚠️ <b>A slug that merely BEGINS with digits is not an id.</b> <c>/company/2linkitnet</c> would
/// pass a naive "starts with a number" test — a `LIKE '%/company/[0-9]%'` check reported exactly that
/// false positive while measuring §824.12a. The id must be <b>entirely</b> digits.</para>
/// </remarks>
public static class LinkedInUrlParser
{
    // /company/<segment> — captured whole, then judged. Trailing slash, query and fragment ignored.
    private static readonly Regex CompanySegment = new(
        @"linkedin\.com/company/(?<seg>[^/?#]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// The numeric organization id when the URL carries one, else null.
    /// </summary>
    public static string? TryReadOrganizationId(string? url)
    {
        var seg = TryReadCompanySegment(url);
        if (seg is null) return null;

        // 🔒 ENTIRELY digits. "2linkitnet" starts with a digit and is a slug, not an id — treating it
        // as one would build urn:li:organization:2 and mention a stranger's company in a real post.
        return seg.All(char.IsAsciiDigit) ? seg : null;
    }

    /// <summary>
    /// The vanity slug when the URL carries one (and it is not a numeric id), else null.
    /// </summary>
    public static string? TryReadVanityName(string? url)
    {
        var seg = TryReadCompanySegment(url);
        if (seg is null) return null;
        return seg.All(char.IsAsciiDigit) ? null : seg;
    }

    /// <summary>
    /// The <c>urn:li:organization:{id}</c> for a mention — from an explicitly stored id first, and
    /// only then from a URL that happens to carry one. Null when neither is available.
    /// </summary>
    /// <remarks>
    /// 🔒 <b>The stored id WINS over the URL, and that ordering is the point.</b> An id somebody has
    /// seen work is worth more than one re-derived on every post: a company can rename its vanity
    /// slug at any time without telling us, and the stored id survives that. See
    /// <see cref="Domain.SponsorInfo.LinkedInOrganizationId"/>.
    /// </remarks>
    public static string? TryBuildOrganizationUrn(string? storedOrganizationId, string? url)
    {
        var id = string.IsNullOrWhiteSpace(storedOrganizationId)
            ? TryReadOrganizationId(url)
            : storedOrganizationId.Trim();

        if (string.IsNullOrWhiteSpace(id)) return null;

        // An id pasted as a full urn, or with stray text, must not become "urn:li:organization:urn:li:…".
        var last = id.Split(':').Last().Trim();
        return last.Length > 0 && last.All(char.IsAsciiDigit)
            ? $"urn:li:organization:{last}"
            : null;
    }

    private static string? TryReadCompanySegment(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var m = CompanySegment.Match(url.Trim());
        if (!m.Success) return null;
        var seg = m.Groups["seg"].Value.Trim();
        return seg.Length == 0 ? null : seg;
    }
}
