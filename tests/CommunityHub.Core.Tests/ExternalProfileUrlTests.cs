using CommunityHub.Core.Volunteers;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1152 — a person-typed profile address becomes a safe link, or none at all.
///
/// <para>Operator 2026-08-28: <i>"fix this, when a person forgets to include https:// in front. it
/// must be a url like the others"</i> — a volunteer typed <c>www.linkedin.com/in/…</c> and §1149
/// correctly declined to link a scheme-less value, so it sat on the queue as plain text.</para>
///
/// <para>🔑 Leaving out "https://" is how people write a web address, not a mistake worth punishing.
/// 🔒 But the safety is unchanged: a scheme is only ever ADDED when there is none, so a value that
/// already carries one is judged on it and <c>javascript:</c> is still refused.</para>
///
/// <para>⚠️ This also closes the gap recorded under §1148/§1149: the http(s)-only rule lived in
/// Razor and was untested. It is now a pure function, and the dangerous cases are pinned.</para>
/// </summary>
public class ExternalProfileUrlTests
{
    // ---- the fix -------------------------------------------------------------------------

    [Theory]
    [InlineData("www.linkedin.com/in/mostafa-dawas", "https://www.linkedin.com/in/mostafa-dawas")]
    [InlineData("linkedin.com/in/ada", "https://linkedin.com/in/ada")]
    [InlineData("  www.linkedin.com/in/ada  ", "https://www.linkedin.com/in/ada")]
    public void A_scheme_less_address_gets_https(string raw, string expected) =>
        Assert.Equal(expected, ExternalProfileUrl.TryNormalize(raw));

    [Theory]
    [InlineData("https://www.linkedin.com/in/ada")]
    [InlineData("http://www.linkedin.com/in/ada")]
    public void An_address_that_already_has_a_scheme_is_left_alone(string raw) =>
        Assert.Equal(raw, ExternalProfileUrl.TryNormalize(raw));

    // ---- the safety, which must NOT have loosened ----------------------------------------

    /// <summary>
    /// 🔴 THE ONE THAT MATTERS.
    /// </summary>
    /// <remarks>
    /// This value renders on an organizer's authenticated page. A narrower "does it start with
    /// http?" test would treat <c>javascript:</c> as scheme-less and helpfully prepend
    /// <c>https://</c>, turning the attack into a working link — which is precisely the mistake the
    /// deliberately broad scheme check exists to prevent.
    /// </remarks>
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JavaScript:alert(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("mailto:someone@example.test")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.test/x")]
    public void A_non_http_scheme_is_REFUSED_never_prefixed(string raw)
    {
        Assert.Null(ExternalProfileUrl.TryNormalize(raw));
        Assert.False(ExternalProfileUrl.IsLinkable(raw));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,x")]
    public void A_refused_value_is_never_silently_turned_into_https(string raw)
    {
        // Belt and braces on the above: whatever comes back, it must not be the attack with a
        // scheme bolted on the front.
        var result = ExternalProfileUrl.TryNormalize(raw);
        Assert.Null(result);
        Assert.DoesNotContain("javascript", result ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    // ---- values that are not addresses at all ---------------------------------------------

    [Theory]
    [InlineData("linkedin")]
    [InlineData("my profile")]
    [InlineData("localhost")]
    public void A_bare_word_does_not_become_a_link(string raw)
    {
        // 🔒 A host with no dot is somebody typing a note, not an address. Prefixing it would put a
        // live link on the page pointing nowhere useful.
        Assert.Null(ExternalProfileUrl.TryNormalize(raw));
    }

    [Theory]
    [InlineData("www.linkedin.com/in/ a")]
    [InlineData("www.linkedin.com\n/in/a")]
    [InlineData("java\tscript:alert(1)")]
    public void Whitespace_or_control_characters_are_refused(string raw)
    {
        // Embedded whitespace is never a real address and is a classic way to smuggle a scheme past
        // a naive check.
        Assert.Null(ExternalProfileUrl.TryNormalize(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_typed_is_nothing_linked(string? raw) =>
        Assert.Null(ExternalProfileUrl.TryNormalize(raw));
}
