namespace CommunityHub.Core.Integrations;

/// <summary>
/// 🔴 §1250 — strip the share-tracking noise off a LinkedIn / X profile URL, on the way IN.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-20, after four speaker pushes failed on malformed profile URLs and the
/// error mail asked him to chase the speakers: *"i propose that you fix this yourself in the
/// importer or inside ceh for linkedin and twitter. this is generic issues that is easy to fix"* ·
/// *"strip after ? etc"*. He is right — asking four people to retype a URL to satisfy a third
/// party's validator is work the hub can simply not create.</para>
///
/// <para>✅ <b>What it removes, and why each is safe:</b></para>
/// <list type="bullet">
///   <item><b>Everything from <c>?</c> onwards</b> — share tracking pasted from a mobile app
///   (<c>?utm_source=share&amp;utm_campaign=…</c>, <c>?s=09</c>, <c>?lang=de</c>). None of it is
///   identity; the profile resolves identically without it. It is also what pushed one URL to 127
///   characters against Zoho's 125 limit, so the length problem disappears with the cause.</item>
///   <item><b>A <c>#</c> fragment</b> — never part of a profile address.</item>
///   <item><b>An <c>@</c> in the PATH</b> — <c>twitter.com/@name</c>. The handle carries the @, the
///   URL does not.</item>
///   <item><b>A trailing <c>/</c></b> — cosmetic, and it keeps two spellings of the same profile
///   from looking like two different values.</item>
/// </list>
///
/// <para>🔒 <b>The host is never touched.</b> The <c>@</c> rule applies after the first path
/// separator only: an <c>@</c> before it would be credentials or a typo, and stripping it would
/// mangle a URL rather than repair one.</para>
///
/// <para>⚠️ <b>Applied at the IMPORT, not at the push</b> — deliberately. Cleaning only on the way
/// out to the event platform would leave the hub holding one value and the platform another, for
/// ever, and the hourly difference mail would chase a mismatch nobody could resolve. Cleaning on
/// the way in means there is one value, and everything downstream agrees with it.</para>
///
/// <para>🔑 <b>It never rewrites a speaker's own edit.</b> This runs on the value arriving FROM the
/// call-for-speakers tool. A URL the speaker typed into their hub profile is theirs and is left
/// exactly as written.</para>
/// </remarks>
public static class SocialUrlNormalizer
{
    /// <summary>
    /// The profile URL with tracking query, fragment, a path <c>@</c> and a trailing slash removed.
    /// Null/blank in ⇒ null out. Anything it does not recognise passes through untouched.
    /// </summary>
    public static string? Clean(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var value = url.Trim();

        var cut = value.IndexOfAny(new[] { '?', '#' });
        if (cut >= 0) value = value[..cut];

        // Find where the PATH starts, so the host is left alone.
        var scheme = value.IndexOf("//", StringComparison.Ordinal);
        var pathStart = scheme >= 0
            ? value.IndexOf('/', scheme + 2)
            : value.IndexOf('/');
        if (pathStart >= 0)
        {
            var host = value[..(pathStart + 1)];
            var path = value[(pathStart + 1)..].Replace("@", string.Empty);
            value = host + path;
        }

        value = value.TrimEnd('/');
        return value.Length == 0 ? null : value;
    }
}
