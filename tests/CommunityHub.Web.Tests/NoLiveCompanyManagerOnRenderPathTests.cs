using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §443 (operator 2026-07-27): <i>"we should sync the data over using a sync routine. it must
/// newer pull data from company manager (cm) in the admin interface."</i>
///
/// <para>Five organizer pages used to call Company Manager — the WordPress plugin on the public
/// event site — once per sponsor company, sequentially, while RENDERING. Measured on PROD that was
/// 5.9–7.9 s warm per page view, and it scaled with the number of sponsor COMPANIES, so it grew
/// with the event. The names were already in CEH SQL the whole time, synced by
/// <c>SponsorOrderPullService</c>.</para>
///
/// <para>This guard is deliberately structural rather than a timing test: it fails if any
/// ORGANIZER page reintroduces a Company Manager call, whatever the shape. Timings drift with the
/// network; a forbidden dependency does not. Sponsor-facing pages are covered separately below,
/// and the one legitimate live call — the operator explicitly SAVING back to Company Manager —
/// is allowed by name.</para>
/// </summary>
public sealed class NoLiveCompanyManagerOnRenderPathTests
{
    private static string PagesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "CommunityHub", "Pages");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate src/CommunityHub/Pages");
    }

    /// <summary>
    /// The one page that legitimately talks to Company Manager live: it EDITS a CM company, so it
    /// reads that single company's editable fields and writes them back. Listed by name so the
    /// exemption is visible rather than implied by a loophole in the pattern.
    ///
    /// <para>⚠️ A file-level exemption is blunt, and it hid a real offender on the first pass: this
    /// page ALSO looped CM to label its company drop-down, which kept it at ~7 s after the other
    /// four were fixed. The list is now local; only the per-company edit load is live. The extra
    /// assertion below is what stops the exemption from silently covering a lookup again.</para>
    /// </summary>
    private static readonly string[] AllowedToCallLive = { "SponsorWebshopCompany.cshtml.cs" };

    /// <summary>
    /// Even the exempt page must not resolve a LIST of company names live — that is a display
    /// lookup, and display lookups come from CEH SQL.
    /// </summary>
    [Fact]
    public void Even_the_company_manager_editor_labels_its_picker_from_local_data()
    {
        var file = Path.Combine(PagesDir(), "Organizer", "SponsorWebshopCompany.cshtml.cs");
        var text = File.ReadAllText(file);

        var listMethod = Regex.Match(
            text, @"private async Task LoadCompanyListAsync\(.*?\n    \}", RegexOptions.Singleline);
        Assert.True(listMethod.Success, "LoadCompanyListAsync not found");

        Assert.DoesNotContain("_cm.GetCompanyAsync", listMethod.Value);
        Assert.Contains("SponsorCompanyNameService.ResolveFromLocalAsync", listMethod.Value);
    }

    [Fact]
    public void No_organizer_page_calls_company_manager_while_rendering()
    {
        var organizerDir = Path.Combine(PagesDir(), "Organizer");
        var offenders = Directory
            .EnumerateFiles(organizerDir, "*.cshtml.cs", SearchOption.AllDirectories)
            .Where(f => !AllowedToCallLive.Contains(Path.GetFileName(f)))
            .Where(f =>
            {
                var text = File.ReadAllText(f);
                // Any invocation on the Company Manager client, however the field is named.
                return Regex.IsMatch(text, @"_(cm|companyManager)\.\w+Async\(");
            })
            .Select(f => Path.GetFileName(f))
            .OrderBy(n => n)
            .ToList();

        Assert.True(offenders.Count == 0,
            "These organizer pages call Company Manager on the request path — the admin interface "
            + "must render from CEH SQL and let the sync routine talk to CM: "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// The replacement is a SHARED local resolver, not five more copies. Nine hand-rolled
    /// `ResolveCompanyNamesAsync` bodies are exactly how the old ones drifted apart (one page had
    /// already grown a local-name fallback the others lacked).
    /// </summary>
    [Fact]
    public void The_organizer_pages_resolve_names_through_the_shared_local_service()
    {
        var organizerDir = Path.Combine(PagesDir(), "Organizer");
        var resolvers = Directory
            .EnumerateFiles(organizerDir, "*.cshtml.cs", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("ResolveCompanyNamesAsync"))
            .ToList();

        Assert.NotEmpty(resolvers);
        foreach (var f in resolvers)
        {
            Assert.True(
                File.ReadAllText(f).Contains("SponsorCompanyNameService.ResolveFromLocalAsync"),
                $"{Path.GetFileName(f)} resolves company names without the shared local service.");
        }
    }
}
