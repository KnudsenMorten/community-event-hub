using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1125 — the webshop OWNS the sponsor website, which means it wins on CHANGE, not merely when CEH
/// happens to be blank.
///
/// <para>Operator 2026-08-25: <i>"a sponsor surveil has fixed the url in the webshop from
/// http://surveil.co to https://surveil.co but it is not syncing into ceh"</i> ·
/// <i>"it must always pull from sponsor webshop"</i>.</para>
///
/// <para>🔑 The regression this guards is subtle and was invisible for months: §41b's FILL-BLANK
/// rule and §1081's AUTHORITATIVE rule behave IDENTICALLY until somebody edits the value a second
/// time. Every test that only checks "a blank CEH field gets filled" passes under both. The test
/// that matters is the one below with a non-blank CEH value.</para>
/// </summary>
public sealed class WebshopOwnedWebsiteTests
{
    private static SponsorInfo Info(string? website) => new() { WebsiteUrl = website };

    // ── The reported bug, exactly ────────────────────────────────────────────────────────

    [Fact]
    public void A_scheme_correction_in_the_webshop_reaches_CEH()
    {
        var info = Info("http://surveil.co");

        var changed = WebshopOwnedFields.ApplyWebsite(info, "https://surveil.co");

        Assert.True(changed);
        Assert.Equal("https://surveil.co", info.WebsiteUrl);
    }

    [Fact]
    public void Any_later_change_also_reaches_CEH_not_just_the_first()
    {
        // 🔑 The heart of it: authority is about the SECOND edit. Under the old fill-blank rule the
        // first assignment below would stick for ever and this assert would fail.
        var info = Info(null);

        WebshopOwnedFields.ApplyWebsite(info, "http://surveil.co");
        WebshopOwnedFields.ApplyWebsite(info, "https://surveil.co");
        WebshopOwnedFields.ApplyWebsite(info, "https://www.surveil.co");

        Assert.Equal("https://www.surveil.co", info.WebsiteUrl);
    }

    [Fact]
    public void A_blank_CEH_field_is_still_filled()
    {
        var info = Info(null);

        Assert.True(WebshopOwnedFields.ApplyWebsite(info, "https://surveil.co"));
        Assert.Equal("https://surveil.co", info.WebsiteUrl);
    }

    // ── What must NOT happen ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_blank_webshop_value_does_not_erase_the_CEH_website()
    {
        // ⚠️ "Always pull from the webshop" governs what the webshop SAYS. Blanking CEH because the
        // webshop field is empty would remove a live link from the public event site as a side
        // effect of a sync — invisible to the sponsor it affects, and asked for by nobody.
        var info = Info("https://surveil.co");

        Assert.False(WebshopOwnedFields.ApplyWebsite(info, null));
        Assert.False(WebshopOwnedFields.ApplyWebsite(info, "   "));
        Assert.Equal("https://surveil.co", info.WebsiteUrl);
    }

    [Fact]
    public void An_unchanged_value_reports_no_change()
    {
        // The caller persists on `true`, so a false positive here would write the row on every
        // sync pass and make the change-detection downstream meaningless.
        var info = Info("https://surveil.co");

        Assert.False(WebshopOwnedFields.ApplyWebsite(info, "https://surveil.co"));
        Assert.False(WebshopOwnedFields.ApplyWebsite(info, "  https://surveil.co  "));
    }

    [Fact]
    public void The_incoming_value_is_trimmed()
    {
        var info = Info("http://surveil.co");

        Assert.True(WebshopOwnedFields.ApplyWebsite(info, "  https://surveil.co "));
        Assert.Equal("https://surveil.co", info.WebsiteUrl);
    }

    [Fact]
    public void Comparison_is_ordinal_so_a_scheme_or_host_case_fix_counts()
    {
        // A case-insensitive compare would have masked exactly the class of correction being made
        // here (http→https is caught either way, but HTTP→http and Host→host are not).
        var info = Info("HTTPS://Surveil.co");

        Assert.True(WebshopOwnedFields.ApplyWebsite(info, "https://surveil.co"));
        Assert.Equal("https://surveil.co", info.WebsiteUrl);
    }

    // ── §1126 — LinkedIn and Twitter now follow the SAME rule ────────────────────────────
    //
    // ⚠️ This section REPLACES a test that asserted the opposite (that LinkedIn/Twitter kept
    // fill-blank, per §1081). That test was correct when written and is now wrong: operator
    // 2026-08-25 — *"linkedin + twitter is also coming from webshop"* · *"it must also overwrite as
    // webshop is authoritative"*. Recorded here so the reversal reads as a decision, not a
    // deleted assertion.

    [Fact]
    public void A_LinkedIn_change_in_the_webshop_overwrites_CEH()
    {
        var info = new SponsorInfo { LinkedInUrl = "https://linkedin.com/company/old" };

        Assert.True(WebshopOwnedFields.ApplyLinkedIn(info, "https://linkedin.com/company/surveil-cloud"));
        Assert.Equal("https://linkedin.com/company/surveil-cloud", info.LinkedInUrl);
    }

    [Fact]
    public void A_Twitter_change_in_the_webshop_overwrites_CEH()
    {
        var info = new SponsorInfo { TwitterUrl = "http://twitter.com/old" };

        Assert.True(WebshopOwnedFields.ApplyTwitter(info, "https://x.com/surveil"));
        Assert.Equal("https://x.com/surveil", info.TwitterUrl);
    }

    [Fact]
    public void A_blank_webshop_value_does_not_erase_LinkedIn_or_Twitter_either()
    {
        var info = new SponsorInfo
        {
            LinkedInUrl = "https://linkedin.com/company/surveil-cloud",
            TwitterUrl = "https://x.com/surveil",
        };

        Assert.False(WebshopOwnedFields.ApplyLinkedIn(info, null));
        Assert.False(WebshopOwnedFields.ApplyTwitter(info, "  "));
        Assert.Equal("https://linkedin.com/company/surveil-cloud", info.LinkedInUrl);
        Assert.Equal("https://x.com/surveil", info.TwitterUrl);
    }

    [Fact]
    public void ApplyAll_covers_all_three_and_reports_a_change_if_any_moved()
    {
        // 🔒 The callers duplicate this block, so they call ApplyAll rather than the three
        // individually — a caller that remembers two of the three is §1125 repeating.
        var info = new SponsorInfo
        {
            WebsiteUrl = "http://surveil.co",
            LinkedInUrl = "https://linkedin.com/company/surveil-cloud",
            TwitterUrl = "https://x.com/surveil",
        };

        // Only the website differs ⇒ still reports a change.
        Assert.True(WebshopOwnedFields.ApplyAll(
            info, "https://surveil.co",
            "https://linkedin.com/company/surveil-cloud", "https://x.com/surveil"));
        Assert.Equal("https://surveil.co", info.WebsiteUrl);

        // Nothing differs ⇒ no change, so the caller does not write the row every pass.
        Assert.False(WebshopOwnedFields.ApplyAll(
            info, "https://surveil.co",
            "https://linkedin.com/company/surveil-cloud", "https://x.com/surveil"));
    }
}
