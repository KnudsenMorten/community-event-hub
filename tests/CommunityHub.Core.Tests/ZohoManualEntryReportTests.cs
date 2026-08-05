using System.Reflection;
using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §792.1 — the rule that decides whether a Zoho field goes on the hand-entry list.
/// </summary>
/// <remarks>
/// <para>Plan B (operator 2026-08-04) stopped CEH updating sponsors and exhibitors over the API —
/// <i>"Any api UPDATES related to sponsors and exhibitors must be sent to info@expertslive.dk as
/// mail, so I manually can update the records"</i> — so this predicate IS the feature. Get it wrong
/// in one direction and he retypes fields that already match; wrong in the other and Zoho quietly
/// keeps a stale value he was never told about.</para>
///
/// <para>🔴 The first cut asked only "is Zoho blank?", inherited from the old PUSH. He caught it:
/// <i>"web url etc must also be chk"</i>. A changed website is not blank.</para>
/// </remarks>
public sealed class ZohoManualEntryReportTests
{
    /// <summary>The private predicate under test, reached by reflection so it can stay private.</summary>
    private static bool NeedsManualEntry(string? inZoho, string? inCeh)
    {
        var m = typeof(SponsorZohoSyncService).GetMethod(
            "NeedsManualEntry", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("NeedsManualEntry not found.");
        return (bool)m.Invoke(null, new object?[] { inZoho, inCeh })!;
    }

    [Theory]
    // Nothing in CEH ⇒ nothing to paste, whatever Zoho holds. CEH is not authoritative for a
    // field it does not have, and blanking Zoho is never the instruction.
    [InlineData(null, null, false)]
    [InlineData("https://zoho.example", null, false)]
    [InlineData("https://zoho.example", "   ", false)]
    // Blank in Zoho + a value in CEH ⇒ report (the original §784.13 case).
    [InlineData(null, "https://ceh.example", true)]
    [InlineData("", "https://ceh.example", true)]
    [InlineData("   ", "https://ceh.example", true)]
    // 🔴 THE CASE HE CAUGHT: Zoho holds a STALE value. Not blank, and must still be reported.
    [InlineData("https://old.example", "https://new.example", true)]
    // A scheme change is a REAL mismatch he wants to fix, not formatting noise.
    [InlineData("http://surveil.co", "https://surveil.co", true)]
    // Equal ⇒ silent. Trailing slash, case and surrounding space are formatting, not difference.
    [InlineData("https://ceh.example", "https://ceh.example", false)]
    [InlineData("https://ceh.example/", "https://ceh.example", false)]
    [InlineData("HTTPS://CEH.EXAMPLE", "https://ceh.example", false)]
    [InlineData("  https://ceh.example  ", "https://ceh.example", false)]
    public void It_reports_a_field_when_Zoho_is_blank_or_different(
        string? inZoho, string? inCeh, bool expected)
        => Assert.Equal(expected, NeedsManualEntry(inZoho, inCeh));

    /// <summary>
    /// ⚠️ Zoho REFORMATS rich text. Without this tolerance the description would differ on every
    /// single pass for a value nobody touched — the §596 "70-mail night" rebuilt in mail form, and
    /// this time landing in a human's inbox.
    /// </summary>
    [Theory]
    [InlineData("<p>Great company</p>", "Great company", false)]
    [InlineData("Great  company", "Great company", false)]
    [InlineData("<p>Great company</p>\r\n", "Great company", false)]
    [InlineData("Great &amp; good", "Great & good", false)]
    // ...but a REAL edit still comes through.
    [InlineData("<p>Great company</p>", "Greater company", true)]
    public void Rich_text_reformatting_is_tolerated_but_a_real_edit_is_not(
        string inZoho, string inCeh, bool expected)
        => Assert.Equal(expected, NeedsManualEntry(inZoho, inCeh));
}
