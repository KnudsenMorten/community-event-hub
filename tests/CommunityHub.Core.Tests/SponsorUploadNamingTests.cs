using CommunityHub.Uploads;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §768.14 — the sponsor upload NAMING contract, tested from both ends.
/// </summary>
/// <remarks>
/// <para>🔒 <b>Why this file exists.</b> Before §768.14 the name was BUILT in four hand-copied
/// writers and READ BACK by a fifth, separate heuristic in the graphics sweep. Nothing tested the
/// two against each other, and a mismatch is silent: the matcher finds nothing, the sweep logs a
/// healthy run, and every sponsor graphic quietly stops building. Round-tripping build→parse is the
/// assertion that could not be made while the rule lived in five places.</para>
/// </remarks>
public class SponsorUploadNamingTests
{
    [Theory]
    [InlineData("some", "2LINKIT", 3, ".png", "2linkit-logo-web-3.png")]
    [InlineData("print", "Acme Corp", 1, ".eps", "acme-corp-logo-print-1.eps")]
    [InlineData("wall", "Contoso A/S", 12, ".pdf", "contoso-a-s-exhibitor-wall-12.pdf")]
    public void The_name_follows_the_operators_convention(
        string kind, string sponsor, int version, string ext, string expected)
    {
        // The §768.8 convention, quoted from the operator's work order: {sponsorname}-logo-web-{version}.png.
        // Keyed on the NAME, not the company id (operator 2026-08-02).
        Assert.Equal(expected, SponsorUploadNaming.Build(kind, sponsor, version, ext));
    }

    [Fact]
    public void A_missing_dot_on_the_extension_is_tolerated()
    {
        // Callers get the extension from Path.GetExtension (dotted) or from a content type (not).
        // Both must produce the same file, or two writers disagree over one character.
        Assert.Equal("acme-logo-web-1.png", SponsorUploadNaming.Build("some", "Acme", 1, "png"));
        Assert.Equal("acme-logo-web-1.png", SponsorUploadNaming.Build("some", "Acme", 1, ".PNG"));
    }

    [Theory]
    [InlineData("some", "2LINKIT")]
    [InlineData("print", "Acme Corp A/S")]
    [InlineData("wall", "Æblegård & Sønner")]
    [InlineData("some", "Hyphen-Heavy-Name-Ltd")]
    public void What_the_writers_build_is_what_the_matcher_reads_back(string kind, string sponsor)
    {
        // 🔒 THE round-trip. This is the assertion whose absence let the writers and
        // ResolveNewestSponsorLogo drift: each side was individually plausible.
        var name = SponsorUploadNaming.Build(kind, sponsor, 7, ".png");

        Assert.True(SponsorUploadNaming.Matches(name, kind, sponsor, out var version));
        Assert.Equal(7, version);
    }

    [Fact]
    public void A_hyphenated_sponsor_survives_the_parse()
    {
        // The parser peels version and kind off the RIGHT for exactly this case. Reading
        // left-to-right splits "acme-corp" at its own hyphen and matches nothing.
        var name = SponsorUploadNaming.Build("some", "Acme-Corp A/S", 2, ".png");

        Assert.Equal("acme-corp-a-s-logo-web-2.png", name);
        Assert.True(SponsorUploadNaming.TryParse(name, "some", out var slug, out var version));
        Assert.Equal("acme-corp-a-s", slug);
        Assert.Equal(2, version);
    }

    [Fact]
    public void A_file_of_another_kind_does_not_match()
    {
        // The kind is carried in the NAME, not only in the folder. A print logo that landed in the
        // web folder must be ignored rather than rendered onto artwork as the company's web logo.
        var print = SponsorUploadNaming.Build("print", "Acme", 4, ".png");

        Assert.False(SponsorUploadNaming.Matches(print, "some", "Acme", out _));
        Assert.True(SponsorUploadNaming.Matches(print, "print", "Acme", out _));
    }

    [Fact]
    public void Another_companys_file_does_not_match()
    {
        var other = SponsorUploadNaming.Build("some", "OtherCo", 9, ".png");

        Assert.False(SponsorUploadNaming.Matches(other, "some", "Acme", out _));
    }

    [Theory]
    [InlineData("SoMeBrandingLogo_Acme_v2.png")]   // the retired convention
    [InlineData("ZohoLogo_Acme_v1.png")]           // the retired KIND
    [InlineData("acme-logo-web.png")]              // no version
    [InlineData("acme-logo-web-.png")]             // empty version
    [InlineData("logo-web-3.png")]                 // no sponsor
    [InlineData("acme-logo-web-x.png")]            // non-numeric version
    public void A_name_off_the_contract_is_refused(string fileName)
    {
        // 🔒 Deliberately strict. A tolerant matcher would resurrect superseded files left behind by
        // the §768 fresh start — the operator's ruling was "ignore old files and old paths", and a
        // silently-matched old logo is worse than no logo, because nothing reports it.
        Assert.False(SponsorUploadNaming.TryParse(fileName, "some", out _, out _));
    }

    [Fact]
    public void The_zoho_kind_no_longer_names_anything()
    {
        // §6.7 — one Web logo serves both purposes now. A stale caller must fail loudly rather than
        // write to a folder nothing reads.
        Assert.Null(SponsorUploadNaming.InfixFor("zoho"));
        Assert.Throws<ArgumentException>(() => SponsorUploadNaming.Build("zoho", "Acme", 1, ".png"));
        Assert.False(SponsorUploadNaming.Matches("acme-logo-web-1.png", "zoho", "Acme", out _));
    }

    [Fact]
    public void ParseVersion_still_reads_the_retired_form_for_display()
    {
        // ⚠️ Display only, and the ONE place tolerance is correct: SponsorUploadAudit rows written
        // before §768.14 carry _v{N} names and are shown back to the sponsor on Company Details.
        Assert.Equal(2, SponsorUploadNaming.ParseVersion("SoMeBrandingLogo_Acme_v2.png"));
        Assert.Equal(3, SponsorUploadNaming.ParseVersion("acme-logo-web-3.png"));
        Assert.Equal(1, SponsorUploadNaming.ParseVersion("something-unversioned.png"));
        Assert.Equal(1, SponsorUploadNaming.ParseVersion(null));
    }

    [Fact]
    public void A_nameless_sponsor_still_produces_a_usable_file()
    {
        // An upload must never fail because a company row has no name — the file lands under a
        // placeholder and is recoverable, rather than throwing mid-upload.
        Assert.Equal("sponsor-logo-web-1.png", SponsorUploadNaming.Build("some", "", 1, ".png"));
        Assert.Equal("sponsor-logo-web-1.png", SponsorUploadNaming.Build("some", null, 0, ".png"));
    }
}
