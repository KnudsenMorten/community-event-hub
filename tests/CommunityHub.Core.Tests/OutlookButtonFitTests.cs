using CommunityHub.Core.Email;
using CommunityHub.Core.Tests.Scenario;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1312 — operator 2026-09-27: "i have said this 18 times now. check all buttons and make sure the width is big enough for
/// the text. it fails all times and i keep repeating this finding" · "otherwise shorten the wording".
/// </summary>
public sealed class OutlookButtonFitTests
{
    private static string Vml(int width, string label, int fontPx = 16) =>
        $"<!--[if mso]><v:roundrect xmlns:v=\"urn:schemas-microsoft-com:vml\" href=\"https://x\" style=\"height:52px;v-text-anchor:middle;width:{width}px;\" "
        + $"arcsize=\"50%\" stroke=\"f\" fillcolor=\"#1565c0\"><w:anchorlock/><center style=\"color:#fff;font-size:{fontPx}px;font-weight:bold;\">{label}</center></v:roundrect><![endif]-->";

    /// <summary>The two labels the operator found cut off — widened when the mail is sent.</summary>
    [Theory]
    [InlineData(296, "Read and sign the contract")]
    [InlineData(380, "Register and complete your order")]
    public void A_cut_off_button_is_widened_to_fit_its_label(int written, string label)
    {
        var html = OutlookButtonFit.Apply(Vml(written, label));

        var b = Assert.Single(OutlookButtonFit.Buttons(html));
        Assert.True(b.Width > written);
        Assert.True(b.Width >= OutlookButtonFit.Needed(label));
        Assert.Contains(label, html);                                    // the label itself untouched
    }

    [Fact]
    public void A_wide_enough_button_is_left_as_written_and_entities_count_as_one_character()
    {
        var html = Vml(400, "Open &rarr;");
        Assert.Equal(html, OutlookButtonFit.Apply(html));
        Assert.Equal("Open →", Assert.Single(OutlookButtonFit.Buttons(html)).Label);
    }

    /// <summary>
    /// 🔴 §1312 — the non-Outlook pill becomes a BULLETPROOF button (operator 2026-09-30: "the button in the email for sponsors
    /// looks weird. normally it is wider button" — web / new Outlook drops a link's own width, line-height and padding, leaving
    /// the bare label on a tight background). The colour and padding sit on a table cell; the label never wraps; the link, its
    /// colours and an outline border are kept.
    /// </summary>
    [Fact]
    public void A_pill_link_becomes_a_bulletproof_table_cell_button()
    {
        const string pill = "<a href=\"https://x/Forms/Wizard\" style=\"background-color:#1565c0;border-radius:999px;color:#ffffff;display:inline-block;line-height:52px;text-decoration:none;width:296px;-webkit-text-size-adjust:none;\">Continue Get Started</a>";

        var html = OutlookButtonFit.Apply(pill);

        Assert.DoesNotContain("width:296px", html);
        Assert.Contains("<td align=\"center\" bgcolor=\"#1565c0\"", html);            // colour on the cell …
        Assert.Contains("padding:14px 32px", html);                                 // … and its size
        Assert.Contains("href=\"https://x/Forms/Wizard\"", html);
        Assert.Contains("white-space:nowrap", html);
        Assert.Contains(">Continue Get Started</a>", html);

        // A secondary (outlined) shop button keeps its white fill, blue text and border.
        var outlined = OutlookButtonFit.Apply("<a href=\"https://x/r\" style=\"background-color:#ffffff;border:2px solid #1565c0;border-radius:999px;color:#1565c0;display:inline-block;\">Ask for more time</a>");
        Assert.Contains("bgcolor=\"#ffffff\"", outlined);
        Assert.Contains("border:2px solid #1565c0", outlined);
        Assert.Contains("color:#1565c0", outlined);

        // Idempotent: a mail that passes through twice is not wrapped twice.
        Assert.Equal(html, OutlookButtonFit.Apply(html));
    }

    /// <summary>It runs on EVERY send: the one tail all four send paths share.</summary>
    [Fact]
    public void Every_send_path_fits_the_buttons()
    {
        var sent = BrevoEmailSender.ApplyDefaultFont("<p>x</p>" + Vml(296, "Read and sign the contract"));
        Assert.True(Assert.Single(OutlookButtonFit.Buttons(sent)).Width >= OutlookButtonFit.Needed("Read and sign the contract"));
    }

    /// <summary>
    /// 🔒 Every button in every shipped template (and every saved per-edition template) has a label that FITS the mail —
    /// otherwise no width can save it: shorten the label. A {{token}} counts as 12 characters.
    /// </summary>
    [Fact]
    public void Every_template_button_label_fits_the_mail()
    {
        var dirs = new[] { RepoPaths.EmailTemplates(), Path.Combine(RepoPaths.RepoRoot(), "config", "email-templates") }
            .Where(Directory.Exists);
        var tooLong = new List<string>();
        var seen = 0;
        foreach (var file in dirs.SelectMany(d => Directory.GetFiles(d, "*.html")))
        {
            foreach (var b in OutlookButtonFit.Buttons(File.ReadAllText(file)))
            {
                seen++;
                var label = System.Text.RegularExpressions.Regex.Replace(b.Label, @"\{\{\w+\}\}", new string('x', 12));
                if (OutlookButtonFit.Needed(label, b.FontPx) > OutlookButtonFit.MaxWidth)
                    tooLong.Add($"{Path.GetFileName(file)}: \"{b.Label}\"");
            }
        }
        Assert.True(seen > 20, $"only {seen} buttons found — the scan is not reading the templates");
        Assert.True(tooLong.Count == 0, "Button labels too long for the mail — shorten them: " + string.Join("; ", tooLong));
    }
}
