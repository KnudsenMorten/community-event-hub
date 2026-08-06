using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// §894 — one place that turns whatever a human typed into an <b>ISO-3166-1 alpha-2</b> code.
/// </summary>
/// <remarks>
/// <para>🔑 <b>Why this exists.</b> e-conomic stores the country as free text and every spelling in
/// the book is in there — <c>Denmark</c>, <c>Danmark</c>, <c>USA</c>, <c>United States Of America</c>,
/// <c>United States (US)</c>, <c>United Kingdom (UK)</c>. Company Manager and Zoho both store a
/// 2-letter code. A first version refused anything that was not already 2 letters and reported it,
/// which produced <b>sixty "not a 2-letter code" lines in one e-mail</b> — a safety valve that became
/// the noise it was meant to prevent. Operator 2026-08-06: *"it is important that you make a Country
/// function mapper using these names and dont throw this at me. iso naming"*.</para>
///
/// <para>🔒 <b>Unknown still returns null, and that still means "do nothing".</b> Mapping is the fix
/// for names we recognise; it is not a licence to guess. An unmapped value writes nothing and says
/// nothing — §582: a false gap is worse than no gap.</para>
///
/// <para>Every name below was taken from the real e-conomic customer list, so the map is grounded in
/// the data rather than in an imagined world atlas. Danish spellings are included because the ERP is
/// Danish and half the rows use them.</para>
/// </remarks>
public static class CountryCodeMapper
{
    /// <summary>Trailing "(UK)" / "(US)" and the like — a hint for humans, noise for a lookup.</summary>
    private static readonly Regex Parenthetical = new(@"\s*\([^)]*\)\s*$", RegexOptions.Compiled);

    private static readonly Dictionary<string, string> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        // --- the ones actually present in the e-conomic customer list -----------------
        ["denmark"] = "DK", ["danmark"] = "DK",
        ["germany"] = "DE", ["tyskland"] = "DE", ["deutschland"] = "DE",
        ["sweden"] = "SE", ["sverige"] = "SE",
        ["norway"] = "NO", ["norge"] = "NO",
        ["finland"] = "FI", ["suomi"] = "FI",
        ["netherlands"] = "NL", ["the netherlands"] = "NL", ["holland"] = "NL", ["nederland"] = "NL",
        ["switzerland"] = "CH", ["schweiz"] = "CH", ["suisse"] = "CH",
        ["poland"] = "PL", ["polen"] = "PL",
        ["ireland"] = "IE", ["irland"] = "IE",
        ["canada"] = "CA", ["kanada"] = "CA",
        ["china"] = "CN", ["kina"] = "CN",
        ["united kingdom"] = "GB", ["great britain"] = "GB", ["england"] = "GB",
        ["storbritannien"] = "GB", ["uk"] = "GB",
        ["united states"] = "US", ["united states of america"] = "US", ["usa"] = "US",
        ["america"] = "US", ["forenede stater"] = "US",
        // --- near neighbours, so the next sponsor does not reopen this ----------------
        ["iceland"] = "IS", ["island"] = "IS",
        ["estonia"] = "EE", ["latvia"] = "LV", ["lithuania"] = "LT",
        ["belgium"] = "BE", ["belgien"] = "BE",
        ["austria"] = "AT", ["østrig"] = "AT", ["oesterreich"] = "AT",
        ["france"] = "FR", ["frankrig"] = "FR",
        ["spain"] = "ES", ["spanien"] = "ES",
        ["italy"] = "IT", ["italien"] = "IT",
        ["portugal"] = "PT", ["czech republic"] = "CZ", ["czechia"] = "CZ",
        ["slovakia"] = "SK", ["hungary"] = "HU", ["romania"] = "RO", ["bulgaria"] = "BG",
        ["greece"] = "GR", ["croatia"] = "HR", ["slovenia"] = "SI",
        ["luxembourg"] = "LU", ["luxembourg "] = "LU",
        ["australia"] = "AU", ["new zealand"] = "NZ", ["india"] = "IN",
        ["israel"] = "IL", ["japan"] = "JP", ["singapore"] = "SG",
        ["south africa"] = "ZA", ["brazil"] = "BR", ["mexico"] = "MX",
        ["faroe islands"] = "FO", ["færøerne"] = "FO", ["greenland"] = "GL", ["grønland"] = "GL",
    };

    /// <summary>
    /// The ISO-2 code for whatever was typed, or <b>null when we do not recognise it</b>.
    /// Accepts an existing code unchanged ("dk" ⇒ "DK") and tolerates "United Kingdom (UK)".
    /// </summary>
    public static string? ToIso2(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var raw = Parenthetical.Replace(value.Trim(), string.Empty).Trim();
        if (raw.Length == 0) return null;

        // Already a code — but only if it is one we know, so a stray two-letter word cannot become
        // a country. (§582: silence beats a confident wrong answer.)
        if (raw.Length == 2 && ByName.ContainsValue(raw.ToUpperInvariant()))
            return raw.ToUpperInvariant();

        if (ByName.TryGetValue(raw, out var direct)) return direct;

        // Fold accents so "Østrig"/"Oestrig"/"Ostrig" all land, then try again.
        return ByName.TryGetValue(Fold(raw), out var folded) ? folded : null;
    }

    /// <summary>
    /// Do two country values mean the same country? Unknown on either side ⇒ <b>true</b>, so an
    /// unrecognised spelling is never reported as a disagreement.
    /// </summary>
    public static bool SameCountry(string? a, string? b)
    {
        var x = ToIso2(a);
        var y = ToIso2(b);
        if (x is null || y is null) return true;
        return string.Equals(x, y, StringComparison.Ordinal);
    }

    private static string Fold(string s)
    {
        var decomposed = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(ch);
        }
        return sb.ToString().Replace("ø", "o").Replace("Ø", "O")
                 .Replace("æ", "a").Replace("Æ", "A").Trim();
    }
}
