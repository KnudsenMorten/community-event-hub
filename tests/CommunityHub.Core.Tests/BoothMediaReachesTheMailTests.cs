using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §804 — a booth VIDEO or COLLATERAL must reach the hand-entry mail, because nothing else can
/// deliver it.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-04: *"i must manually handle those and it must be included in the email so
/// i am informed of any urls (video) and booth colleteral (including files) to upload"*, and
/// *"no api for that in zoho"*.</para>
///
/// <para>🔴 <b>Re-proven the same day (§804.1), not inherited:</b> every candidate sub-resource under
/// the exhibitor 404s, and the exhibitor PUT answers <c>{"message":"Extra key found"}</c> for
/// <c>videos</c> / <c>collaterals</c> / <c>documents</c>. The object has a CLOSED schema and no media
/// field, so the mail is the ONLY way these ever reach Backstage.</para>
///
/// <para>🔒 Which makes <c>ManualReportHash</c> the delivery guarantee: the mail is stamp-gated, so a
/// material the stamp does not cover is a material he is <b>never told about</b> — silently, and for
/// ever. These tests pin that the stamp moves for media alone.</para>
/// </remarks>
public sealed class BoothMediaReachesTheMailTests
{
    private static SponsorInfo Info() => new()
    {
        EventId = 1,
        SponsorCompanyId = "1001",
        CompanyDescription = "A description that is not changing in these tests.",
        WebsiteUrl = "https://example.test",
        LinkedInUrl = "https://www.linkedin.com/company/example",
    };

    private static SponsorBoothMaterial Video(string url = "https://youtu.be/abc123") => new()
    {
        EventId = 1, SponsorCompanyId = "1001", Kind = BoothMaterialKind.Video, Url = url,
    };

    private static SponsorBoothMaterial Collateral(
        string url = "https://sharepoint.test/one-pager.pdf", string file = "one-pager.pdf") => new()
    {
        EventId = 1, SponsorCompanyId = "1001", Kind = BoothMaterialKind.Collateral,
        Url = url, FileName = file,
    };

    /// <summary>
    /// 🔴 THE ONE THAT MATTERS: a sponsor adds a video and changes NOTHING else. The stamp must move,
    /// or the mail is suppressed and he never learns there is a video to upload.
    /// </summary>
    [Fact]
    public void Adding_a_video_alone_changes_the_stamp()
    {
        var info = Info();

        var before = SponsorZohoSyncService.ManualReportHash(info);
        var after = SponsorZohoSyncService.ManualReportHash(info, new[] { Video() });

        Assert.NotNull(before);
        Assert.NotEqual(before, after);
    }

    /// <summary>The same for an uploaded file — "including files" was explicit.</summary>
    [Fact]
    public void Adding_a_collateral_file_alone_changes_the_stamp()
    {
        var info = Info();

        var before = SponsorZohoSyncService.ManualReportHash(info);
        var after = SponsorZohoSyncService.ManualReportHash(info, new[] { Collateral() });

        Assert.NotEqual(before, after);
    }

    /// <summary>A SECOND video is a change too — the mail lists the whole set, so the set must hash.</summary>
    [Fact]
    public void Adding_a_second_video_changes_the_stamp_again()
    {
        var info = Info();
        var one = SponsorZohoSyncService.ManualReportHash(info, new[] { Video() });
        var two = SponsorZohoSyncService.ManualReportHash(
            info, new[] { Video(), Video("https://youtu.be/second") });

        Assert.NotEqual(one, two);
    }

    /// <summary>
    /// ⚠️ And the other half: an UNCHANGED set must hash the same however it is ordered, or the mail
    /// would fire on every pass — §302's 70-mail night, from the opposite direction.
    /// </summary>
    [Fact]
    public void The_same_materials_in_a_different_order_do_not_re_fire_the_mail()
    {
        var info = Info();
        var a = SponsorZohoSyncService.ManualReportHash(
            info, new[] { Video(), Collateral() });
        var b = SponsorZohoSyncService.ManualReportHash(
            info, new[] { Collateral(), Video() });

        Assert.Equal(a, b);
    }

    /// <summary>Re-hashing the identical input is stable — the stamp is a fact, not a timestamp.</summary>
    [Fact]
    public void The_stamp_is_stable_across_reads()
    {
        var info = Info();
        var materials = new[] { Video(), Collateral() };

        var first = SponsorZohoSyncService.ManualReportHash(info, materials);
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(first, SponsorZohoSyncService.ManualReportHash(info, materials));
        }
    }

    /// <summary>
    /// A renamed collateral file is a different thing to upload, so it must re-report — the mail
    /// carries the file NAME next to the URL precisely so he knows which one it is.
    /// </summary>
    [Fact]
    public void Renaming_a_collateral_file_changes_the_stamp()
    {
        var info = Info();
        var a = SponsorZohoSyncService.ManualReportHash(
            info, new[] { Collateral(file: "one-pager.pdf") });
        var b = SponsorZohoSyncService.ManualReportHash(
            info, new[] { Collateral(file: "one-pager-v2.pdf") });

        Assert.NotEqual(a, b);
    }

    /// <summary>
    /// 🔒 The social pages are in the stamp too — they are the other thing only he can do (Zoho
    /// accepts the PUT and stores nothing, §803.3), so they must not be able to go quiet either.
    /// </summary>
    [Fact]
    public void Changing_the_linkedin_url_alone_changes_the_stamp()
    {
        var a = SponsorZohoSyncService.ManualReportHash(Info());
        var info = Info();
        info.LinkedInUrl = "https://www.linkedin.com/company/example-changed";
        var b = SponsorZohoSyncService.ManualReportHash(info);

        Assert.NotEqual(a, b);
    }

    /// <summary>Nothing to say at all ⇒ no stamp, so an empty company never mails.</summary>
    [Fact]
    public void A_company_with_nothing_recorded_produces_no_stamp()
    {
        var empty = new SponsorInfo { EventId = 1, SponsorCompanyId = "1002" };
        Assert.Null(SponsorZohoSyncService.ManualReportHash(empty));
    }
}
