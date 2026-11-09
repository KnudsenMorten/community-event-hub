using System.Text.RegularExpressions;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §1374 — operator 2026-10-28 on the visa form: <i>"all fields are way to small"</i>. The site styles
/// inputs BY TYPE (<c>input[type=text]</c> etc. get full width in <c>_LayoutStyles</c>), so an
/// &lt;input&gt; with no <c>type</c> falls back to the browser's ~20-character default. Every input on
/// the two visa pages must declare its type.
/// </summary>
public sealed class VisaFormFieldsAreStyledTests
{
    private static string PagesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "CommunityHub", "Pages");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate src/CommunityHub/Pages from " + AppContext.BaseDirectory);
    }

    [Theory]
    [InlineData("Forms/Visa.cshtml")]
    [InlineData("Organizer/VisaApplications.cshtml")]
    public void Every_input_declares_its_type(string page)
    {
        var html = File.ReadAllText(Path.Combine(PagesDir(), page));
        var inputs = Regex.Matches(html, "<input\\b[^>]*>").Select(m => m.Value).ToList();

        Assert.NotEmpty(inputs);
        Assert.All(inputs, i => Assert.Matches("\\btype=\"", i));
    }
}
