using System.Text.RegularExpressions;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// 🔴 §1229 — sponsor upload mails name the SPONSOR, never its company number.
/// </summary>
/// <remarks>
/// Operator 2026-09-15, on "Sponsor upload — 102 — …Fellowmind.pdf": <i>"use displayname of sponsor for
/// these mails - we dont know their number"</i>. The booth-collateral path passed <c>companyId</c> as the
/// sponsor-name argument of <c>SponsorUploadKinds.NotifyAsync</c>, where every other upload path passed a
/// resolved name. Checked on the source because the five callers each need SharePoint to run, and the
/// defect was exactly one wrong argument.
/// </remarks>
public sealed class SponsorUploadMailNameTests
{
    [Fact]
    public void No_upload_notification_passes_the_company_id_as_the_sponsor_name()
    {
        var src = Path.Combine(FindRepoRoot(), "src");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;

            var text = File.ReadAllText(file);
            // NotifyAsync(<email>, <spec>, <sponsorName>, … — the THIRD argument is the name.
            foreach (Match m in Regex.Matches(text,
                         @"SponsorUploadKinds\.NotifyAsync\(\s*[^,]+,\s*[^,]+,\s*(?<name>[^,]+),", RegexOptions.Singleline))
            {
                var arg = m.Groups["name"].Value.Trim();
                if (Regex.IsMatch(arg, @"^(companyId|companyId!|cid|sponsorCompanyId)$", RegexOptions.IgnoreCase))
                    offenders.Add($"{Path.GetRelativePath(src, file)}: sponsor name argument is `{arg}`");
            }
        }

        Assert.True(offenders.Count == 0,
            "Upload notifications must pass the sponsor's display name, not its company number:\n"
            + string.Join("\n", offenders));
    }

    /// <summary>
    /// 🔴 §1229b — the booth collateral FILE NAME leads with the sponsor name ("use the sponsor name in the
    /// file name too"); the company number is only the fallback for a company with no synced name.
    /// </summary>
    [Fact]
    public void Booth_collateral_file_names_lead_with_the_sponsor_name()
    {
        var text = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "CommunityHub", "Forms", "Steps", "SponsorBoothMaterialsFormService.cs"));

        Assert.DoesNotContain("$\"{Sanitize(companyId)}_", text, StringComparison.Ordinal);
        Assert.Contains("$\"{Sanitize(filePrefix)}_", text, StringComparison.Ordinal);
        Assert.Contains("SponsorCompanyName.UnresolvedName(companyId)", text, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "docs", "FEATURES.md"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException($"repo root not found from {AppContext.BaseDirectory}");
    }
}
