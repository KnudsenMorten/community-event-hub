namespace CommunityHub.Core.Integrations;

/// <summary>
/// 🔴 §1312 — a sponsor's website, LinkedIn and X are always stored as <c>https://</c> (operator 2026-09-30: "change all
/// website references + linkedin + twitter to https:// … noone uses http:// today"). <c>http://</c> is upgraded, and an
/// address typed without a scheme ("identitystack.dk") gets <c>https://</c>. Blank stays null.
/// </summary>
public static class HttpsUrl
{
    public static string? Normalize(string? raw)
    {
        var s = raw?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return "https://" + s[7..];
        if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return "https://" + s[8..];
        if (s.StartsWith("//", StringComparison.Ordinal)) return "https:" + s;
        return s.Contains("://", StringComparison.Ordinal) ? s : "https://" + s;
    }
}
