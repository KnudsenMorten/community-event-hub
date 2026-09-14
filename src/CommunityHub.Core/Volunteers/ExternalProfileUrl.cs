namespace CommunityHub.Core.Volunteers;

/// <summary>
/// §1152 — turn a person-typed profile address into a SAFE, clickable URL, or refuse it.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-28: <i>"fix this, when a person forgets to include https:// in front. it
/// must be a url like the others"</i> — a volunteer had typed
/// <c>www.linkedin.com/in/…</c>, which §1149 correctly declined to make clickable because it has no
/// scheme, and so it sat on the queue as plain text.</para>
///
/// <para>🔑 <b>Leaving out "https://" is the normal way people write a web address</b>, not a
/// mistake worth punishing — every browser address bar accepts it. Refusing to link it made the
/// safety rule look like a bug.</para>
///
/// <para>🔒 <b>The safety is UNCHANGED, and this is the important part.</b> A scheme is only ever
/// ADDED when the value has none. A string that already carries one keeps it and is judged on it, so
/// <c>javascript:alert(1)</c> is still refused — it is not scheme-less, and prepending nothing to it
/// leaves it exactly as unacceptable as before. The rule is "assume https for a bare address",
/// never "make anything clickable".</para>
///
/// <para>Shared by the sign-up (which normalises on SAVE, so new rows are stored properly) and the
/// pre-selection queue (which normalises on DISPLAY, so rows saved before this still link). One
/// definition — §759: a second copy of a security rule is the one that drifts.</para>
/// </remarks>
public static class ExternalProfileUrl
{
    /// <summary>
    /// The clickable form of <paramref name="raw"/>, or null when it must not be linked.
    /// </summary>
    public static string? TryNormalize(string? raw)
    {
        var value = raw?.Trim();
        if (string.IsNullOrWhiteSpace(value)) return null;

        // ⚠️ A control character or whitespace inside the value is never a real address and is a
        // classic way to smuggle a scheme past a naive check.
        if (value.Any(char.IsControl) || value.Any(char.IsWhiteSpace)) return null;

        // Already carries a scheme ⇒ judge it as-is, add nothing.
        if (HasScheme(value))
        {
            return Uri.TryCreate(value, UriKind.Absolute, out var asIs)
                   && (asIs.Scheme == Uri.UriSchemeHttp || asIs.Scheme == Uri.UriSchemeHttps)
                ? value
                : null;
        }

        // Scheme-less ⇒ assume https, then hold it to exactly the same test.
        var candidate = "https://" + value;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps) return null;

        // 🔒 A host with no dot ("linkedin", "localhost", a bare word someone typed by accident) is
        // not a profile address. Requiring a dot keeps a stray word from becoming a live link.
        if (!uri.Host.Contains('.')) return null;

        return candidate;
    }

    /// <summary>True when this value can be shown as a link.</summary>
    public static bool IsLinkable(string? raw) => TryNormalize(raw) is not null;

    /// <summary>
    /// Does the value already declare a scheme? Deliberately crude and deliberately BROAD.
    /// </summary>
    /// <remarks>
    /// ⚠️ Anything before a ':' that could be a scheme counts — so <c>javascript:…</c>,
    /// <c>data:…</c> and <c>mailto:…</c> all take the "already has a scheme" branch and are then
    /// rejected for not being http(s). A narrower test that only recognised http/https would treat
    /// <c>javascript:</c> as scheme-LESS and helpfully prepend <c>https://</c> to it, which is the
    /// exact mistake this method exists to avoid.
    /// </remarks>
    private static bool HasScheme(string value)
    {
        var colon = value.IndexOf(':');
        if (colon <= 0) return false;

        // A scheme is letters/digits/+/-/. and must start with a letter (RFC 3986).
        if (!char.IsLetter(value[0])) return false;
        for (var i = 1; i < colon; i++)
        {
            var c = value[i];
            if (!char.IsLetterOrDigit(c) && c != '+' && c != '-' && c != '.') return false;
        }
        return true;
    }
}
