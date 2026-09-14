using CommunityHub.Core.Tests.Scenario;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §351-7 — OPERATOR RULE (2026-07-26, verbatim): <i>"all buttons in all emails MUST use magic
/// link (no manual logins). i want everyone to have as smooth experience as possible."</i>
///
/// <para>This pins the rule against the TEMPLATE FILES so it cannot rot. Every link that points
/// back INTO the hub must be built on <c>{{hubUrl}}</c> (which the sender rewrites to the
/// recipient's personal <c>/go/{token}</c> auto-login link) or <c>{{magicLink}}</c>. A hard-coded
/// hub path, or a token/self-service URL that bypasses the magic link, is a regression.</para>
///
/// <para>Why a test and not a convention: the shadow-page cleanup (§351-6) found SIX mails still
/// pointing at <c>/MyMasterClass?t=</c>, a second surface reached without the magic link. Nothing
/// stopped that drift for months.</para>
/// </summary>
public sealed class EmailMagicLinkOnlyTests
{
    /// <summary>Hrefs that legitimately do NOT go into the hub and so need no magic link.</summary>
    private static bool IsAllowedNonHubLink(string href) =>
        href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
        // External sites (the ELDK26 video subdomains, expertslive.dk, calendar providers…):
        // a magic link would be meaningless there.
        || (href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            && !href.Contains("eventhub.expertslive.dk", StringComparison.OrdinalIgnoreCase))
        // Anchors / template tokens that are themselves magic links.
        || href.StartsWith("#", StringComparison.Ordinal)
        || href.StartsWith("{{magicLink}}", StringComparison.Ordinal)
        || href.StartsWith("{{hubUrl}}", StringComparison.Ordinal)
        // §1127 — an EXTERNAL destination supplied as a token rather than a literal URL.
        //
        // 🔑 The allowlist above already permits any literal `http…` outside the hub; it simply had
        // no way to recognise the same thing when the address is configurable. `{{webshopUrl}}` is
        // the sponsor webshop (a different product, on a different domain), so a magic link there
        // would be meaningless — the same reason `expertslive.dk` is allowed two lines up.
        //
        // 🔒 Deliberately an EXACT-NAME exemption, not a blanket "any {{token}}". A wildcard would
        // let the next `{{somePageUrl}}` pointing INTO the hub slip past silently, which is the drift
        // §351-6 found and this test exists to stop.
        || href.StartsWith("{{webshopUrl}}", StringComparison.Ordinal);

    [Fact]
    public void Every_in_hub_email_link_uses_the_magic_link()
    {
        var dirs = new[] { RepoPaths.EmailTemplates(), RepoPaths.PrivateEmailTemplates() }
            .Where(Directory.Exists)
            .ToArray();
        Assert.NotEmpty(dirs);

        var offenders = new List<string>();

        foreach (var dir in dirs)
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.html"))
            {
                var html = File.ReadAllText(file);
                foreach (var m in System.Text.RegularExpressions.Regex.Matches(
                             html, "href=\"([^\"]*)\"").Cast<System.Text.RegularExpressions.Match>())
                {
                    var href = m.Groups[1].Value.Trim();
                    if (href.Length == 0 || IsAllowedNonHubLink(href)) continue;
                    offenders.Add($"{Path.GetFileName(file)} → {href}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "These e-mail links do NOT use the magic link, so the recipient would have to log in "
            + "manually (operator rule §351-7). Rebuild each on {{hubUrl}}/<path>:\n  "
            + string.Join("\n  ", offenders));
    }
}
