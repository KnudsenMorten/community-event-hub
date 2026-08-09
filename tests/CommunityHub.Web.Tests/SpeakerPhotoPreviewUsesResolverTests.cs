using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §993 — NO PAGE MAY RENDER A SPEAKER PHOTO STRAIGHT FROM <c>PhotoUrl</c>.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-09: *"the photo links to sharepoint site url and doesnt show the picture,
/// maybe because the sharepoint site url is not open to speakers or the picture doesnt exist
/// anymore"*. It was the first: a sponsor-uploaded photo's <c>PhotoUrl</c> is a SharePoint
/// **document** URL, which needs an interactive Microsoft sign-in and is not an image endpoint. It
/// could never have rendered — signed in or not.</para>
///
/// <para>🔴 <b>The rule already existed and the call sites ignored it.</b> §665's
/// <c>SpeakerPhotoUrl.Resolve</c> prefers the hub's own stored copy and returns null for an
/// unfetchable SharePoint link, precisely so those profiles repair themselves with no data
/// migration. <c>SponsorSessionFormService</c> used it from the day it was written; the three
/// PREVIEW sites never did. That is the §972 shape — *"when one caller is right and the others are
/// wrong, it is a bug, not a design"* — and the broken icon sat directly above the caption saying a
/// copy had been saved.</para>
///
/// <para>🔑 <b>Why a SOURCE test rather than a rendering test.</b> The defect is not that one page
/// looked wrong; it is that the rule was bypassable and nothing said so. A rendering test would pin
/// the three pages that exist today and stay silent on the fourth — and the fourth is exactly how
/// this happened. Asking "does any view interpolate PhotoUrl into a src?" fails on a page that has
/// not been written yet, which is the property that matters.</para>
/// </remarks>
public sealed class SpeakerPhotoPreviewUsesResolverTests
{
    /// <summary>
    /// Matches <c>src="@…PhotoUrl"</c> in any form — the anti-pattern. `CurrentPhotoUrls` (the
    /// sponsor-session dictionary) is already RESOLVED before it reaches the view, so a `src` built
    /// from it is correct; it is excluded by name rather than by loosening the pattern.
    /// </summary>
    private static readonly Regex RawPhotoSrc = new(
        @"src\s*=\s*""@(?!.*CurrentPhotoUrls)[^""]*PhotoUrl", RegexOptions.Compiled);

    /// <summary>
    /// Views whose <c>PhotoUrl</c> is NOT a speaker photo, so §665 does not apply to them.
    /// ⚠️ A file earns a place here only because its photo comes from somewhere else entirely —
    /// never because the check is inconvenient.
    /// </summary>
    private static readonly string[] NotSpeakerPhotos =
    {
        // Operator-authored organizers.md — a plain image link the operator typed, parsed by
        // Contact.cshtml.cs. No SpeakerProfile, no SharePoint copy, nothing to resolve.
        "Contact.cshtml",
        // A curated, hardcoded contributor list (Contributors.cshtml.cs), same reasoning.
        "Contributors.cshtml",
    };

    [Fact]
    public void No_view_renders_a_speaker_photo_from_the_raw_PhotoUrl()
    {
        var pages = Path.Combine(FindRepoRoot(), "src", "CommunityHub", "Pages");
        Assert.True(Directory.Exists(pages), $"Pages folder not found at {pages}");

        var views = Directory.GetFiles(pages, "*.cshtml", SearchOption.AllDirectories);
        // The scan itself must not silently stop finding anything — a moved folder would otherwise
        // turn this whole test into a no-op that still reports green.
        Assert.True(views.Length > 20, $"only found {views.Length} views — did Pages move?");

        var offenders = views
            .Where(f => !NotSpeakerPhotos.Contains(
                Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
            .Select(f => (File: f, Text: File.ReadAllText(f)))
            .Where(v => RawPhotoSrc.IsMatch(v.Text))
            .Select(v => Path.GetRelativePath(pages, v.File))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.True(offenders.Count == 0,
            "These views render a speaker photo straight from PhotoUrl, which is unfetchable when a "
            + "sponsor uploaded it (a SharePoint document URL needs a Microsoft sign-in). Resolve it "
            + "through SpeakerPhotoUrl.Resolve(PhotoUrl, PhotoStoredPath) — §665/§993:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// The three preview sites must each pass the STORED path too — calling the resolver with a null
    /// second argument compiles, passes the test above, and still cannot repair a sponsor upload.
    /// </summary>
    [Theory]
    [InlineData("Speaker/_DetailsFields.cshtml")]
    [InlineData("Forms/Speaker.cshtml")]
    [InlineData("Forms/OnboardingWizard.cshtml")]
    public void The_preview_sites_resolve_with_the_stored_path(string relative)
    {
        var file = Path.Combine(
            FindRepoRoot(), "src", "CommunityHub", "Pages",
            relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(file), $"{relative} not found");

        var text = File.ReadAllText(file);
        Assert.Contains("SpeakerPhotoUrl", text, StringComparison.Ordinal);
        Assert.Contains("Model.PhotoStoredPath", text, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "docs"))
                && File.Exists(Path.Combine(dir.FullName, "docs", "FEATURES.md")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException($"repo root not found from {AppContext.BaseDirectory}");
    }
}
