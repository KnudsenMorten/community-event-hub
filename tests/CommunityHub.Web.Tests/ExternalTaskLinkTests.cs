using System.IO;
using CommunityHub.Branding;
using Microsoft.AspNetCore.Html;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §653/§654 — task buttons that LEAVE THE HUB say so, and headings carry emphasis inside them.
/// </summary>
/// <remarks>
/// <para>Operator 2026-07-29: <i>"i also need to have the external indicator (similar to zoho in
/// menu-item) for any button that opens up external urls in tasks"</i>, and <i>"i need this to be
/// bold and underline. make it consistent across ALL tasks. highlight important things with bold
/// like 2 chairs and name of chairs"</i>.</para>
/// </remarks>
public class ExternalTaskLinkTests
{
    private static string Render(string s)
    {
        using var w = new StringWriter();
        TaskTextLinkifier.Render(s).WriteTo(w, System.Text.Encodings.Web.HtmlEncoder.Default);
        return w.ToString();
    }

    // ---------- external links announce themselves ----------

    [Fact]
    public void An_external_task_button_gets_the_arrow_indicator_and_a_new_tab()
    {
        var html = Render("[Open the Sponsor Webshop](https://expertslive.dk/sponsor)");

        Assert.Contains("target=\"_blank\"", html);
        Assert.Contains("rel=\"noopener noreferrer\"", html);
        Assert.Contains("ext-ico", html);          // the same ↗ the nav uses
        Assert.Contains("&#8599;", html);
    }

    /// <summary>
    /// 🔒 Not decoration — a screen reader must hear it too. The nav has said "opens in a new tab"
    /// since §176; the task button was the one place that quietly did not.
    /// </summary>
    [Fact]
    public void The_indicator_is_announced_to_screen_readers()
    {
        var html = Render("https://example.com/spec.pdf");

        Assert.Contains("visually-hidden", html);
        Assert.Contains("opens in a new tab", html);
    }

    [Fact]
    public void An_IN_HUB_link_stays_in_the_same_tab_and_gets_NO_indicator()
    {
        // Promising "opens in a new tab" and then navigating in place is worse than saying nothing.
        var html = Render("[Open Company Details](/Sponsor/CompanyDetails)");

        Assert.DoesNotContain("target=\"_blank\"", html);
        Assert.DoesNotContain("ext-ico", html);
    }

    // ---------- the webshop hand-off ----------

    [Fact]
    public void The_SPONSOR_WEBSHOP_link_carries_the_hand_off_marker()
    {
        var html = Render("[Open the Sponsor Webshop](https://expertslive.dk/sponsor)");

        Assert.Contains("data-webshop-interstitial=\"1\"", html);
    }

    /// <summary>
    /// 🔒 Only a link with its own sign-in. §176 already learned that warning about an external
    /// login on a link that has none trains people to click through the warning without reading it.
    /// </summary>
    [Fact]
    public void An_ordinary_external_link_gets_NO_hand_off_marker()
    {
        var html = Render("[Wall specification](https://example.com/spec.pdf)");

        Assert.DoesNotContain("webshop-interstitial", html);
        Assert.DoesNotContain("zoho-interstitial", html);
        Assert.Contains("ext-ico", html);   // still flagged as external, just not warned about
    }

    // ---------- §672: the ZOHO hand-off ----------

    /// <summary>
    /// §672 — the leads/inquiries task buttons dropped the sponsor straight onto a Zoho sign-in
    /// screen with no explanation, while the Zoho MENU item had explained it since §487. The layout
    /// always had both handlers; only the webshop marker was ever emitted, because the linkifier
    /// asked "is this the webshop?" instead of "which hand-off does this need?".
    /// </summary>
    [Theory]
    [InlineData("https://eldk27.expertslive.dk/ELDK27-ExpertsLiveDenmark2027#/exhibitor-dashboard/lead-list?lang=en")]
    [InlineData("https://eldk27.expertslive.dk/ELDK27-ExpertsLiveDenmark2027#/exhibitor-dashboard/inquiry-list?lang=en")]
    public void A_ZOHO_exhibitor_dashboard_link_carries_the_zoho_hand_off_marker(string url)
    {
        var html = Render($"[Open the list]({url})");

        Assert.Contains("data-zoho-interstitial=\"1\"", html);
        // Separate storage keys back these two: dismissing one must not dismiss the other, because
        // they describe different systems with different sign-ins.
        Assert.DoesNotContain("data-webshop-interstitial", html);
    }

    /// <summary>
    /// 🔒 The Zoho match is on the <c>exhibitor-dashboard</c> ROUTE, not the host — a public
    /// Backstage page (venue / floor plan) needs no sign-in, so raising the dialog there would be
    /// exactly the §176 noise the rule exists to prevent.
    /// </summary>
    [Fact]
    public void A_PUBLIC_backstage_page_gets_no_zoho_hand_off()
    {
        var html = Render("[Venue floor plan](https://eldk27.expertslive.dk/ELDK27-ExpertsLiveDenmark2027#/venue?lang=en)");

        Assert.DoesNotContain("zoho-interstitial", html);
        Assert.Contains("ext-ico", html);
    }

    /// <summary>
    /// §671 — "Open Booth Members" must stay in the hub. Authored as an app-relative URL so it
    /// navigates in the SAME tab with no ↗: the button used to promise "you are leaving the hub"
    /// while (per its own surrounding sentence) managing booth staff in Company Details.
    /// </summary>
    [Fact]
    public void The_booth_members_button_stays_in_hub_with_no_external_promise()
    {
        var html = Render("[Open Booth Members](/Sponsor/CompanyDetails#booth-members)");

        Assert.DoesNotContain("target=\"_blank\"", html);
        Assert.DoesNotContain("ext-ico", html);
        Assert.DoesNotContain("interstitial", html);
        Assert.Contains("href=\"/Sponsor/CompanyDetails#booth-members\"", html);
    }

    // ---------- headings with emphasis inside them ----------

    /// <summary>
    /// The exact shape now used by every booth-furniture tier: an underlined heading with the item
    /// name bolded INSIDE it. The bold pass runs before the underline pass, so this only works
    /// because the underline pattern tolerates the tags the bold pass leaves behind — worth pinning
    /// rather than trusting.
    /// </summary>
    [Fact]
    public void A_heading_can_carry_BOLD_inside_an_UNDERLINE()
    {
        var html = Render("__Step 1 — choose your **High-chair bar chairs**__");

        Assert.Contains("<u>", html);
        Assert.Contains("<strong>High-chair bar chairs</strong>", html);
        // The underline must wrap the whole heading, bold included.
        Assert.Matches(@"<u>Step 1 .*<strong>High-chair bar chairs</strong></u>", html);
    }

    [Fact]
    public void The_quantity_a_sponsor_must_get_right_is_bolded()
    {
        var html = Render("You need **2 chairs**. Pick ONE of these two styles:");

        Assert.Contains("<strong>2 chairs</strong>", html);
    }

    /// <summary>
    /// 🔒 The property that must survive every formatting change: copy is authored in config and
    /// rendered into a sponsor-facing page, so raw HTML can never execute.
    /// </summary>
    [Fact]
    public void Raw_HTML_in_authored_copy_still_cannot_execute()
    {
        var html = Render("<script>alert('x')</script> **safe**");

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("<strong>safe</strong>", html);
    }
}
