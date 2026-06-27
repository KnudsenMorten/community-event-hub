using System.Globalization;
using System.Text.Encodings.Web;
using CommunityHub.Core.Resources;
using Microsoft.AspNetCore.Mvc.Localization;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// Regression guard for the long-standing HTTP 500 on the PUBLIC (anonymous)
/// <c>/volunteer/signup</c> page (and the four other public/self-service forms
/// that share the same live character-counter markup: Sponsor/CaptureLead,
/// Sessions/Evaluate, Sessions/Ask, Forms/Speaker).
///
/// ROOT CAUSE — the views rendered the raw counter template with
/// <c>@Localizer["Common.CharCount"]</c>, where <c>Localizer</c> is the
/// <see cref="IHtmlLocalizer{T}"/> injected in <c>_ViewImports</c>. The
/// <see cref="IHtmlLocalizer{T}"/> INDEXER treats the resolved value as a
/// composite format string and runs <c>string.Format</c> over it — even with
/// ZERO arguments. Because <c>Common.CharCount</c> = <c>"{0} / {1} characters"</c>
/// carries placeholders, <c>string.Format("{0} / {1} characters")</c> threw
/// <see cref="FormatException"/> ("Index (zero based) must be ... less than the
/// size of the argument list") at view-render time → unhandled → HTTP 500.
/// (The client-side counter in _Layout substitutes {0}/{1} itself, so the RAW
/// template must reach the attribute verbatim — it must NOT be server-formatted.)
///
/// The fix emits the RAW localized value via <c>Localizer.GetString(key).Value</c>,
/// which routes through <see cref="IStringLocalizer"/> and does NOT run
/// <c>string.Format</c> for the zero-argument case.
///
/// These tests exercise the EXACT localizer type the Razor views use
/// (<see cref="HtmlLocalizer{T}"/> over the real <see cref="SharedResource"/>
/// resx), so they reproduce the render-time crash and lock in the fix.
/// </summary>
public sealed class PublicVolunteerSignupRenderTests
{
    // Mirrors Program.cs AddLocalization()/AddViewLocalization(): ResourcesPath
    // empty so SharedResource's full type name == the embedded .resources base name.
    private static IHtmlLocalizer<SharedResource> MakeHtmlLocalizer()
    {
        var options = Options.Create(new LocalizationOptions { ResourcesPath = "" });
        var stringFactory = new ResourceManagerStringLocalizerFactory(
            options, NullLoggerFactory.Instance);
        var htmlFactory = new HtmlLocalizerFactory(stringFactory);
        return new HtmlLocalizer<SharedResource>(htmlFactory);
    }

    private static T WithCulture<T>(string culture, Func<T> body)
    {
        var prevUi = CultureInfo.CurrentUICulture;
        var prev = CultureInfo.CurrentCulture;
        try
        {
            var ci = new CultureInfo(culture);
            CultureInfo.CurrentUICulture = ci;
            CultureInfo.CurrentCulture = ci;
            return body();
        }
        finally
        {
            CultureInfo.CurrentUICulture = prevUi;
            CultureInfo.CurrentCulture = prev;
        }
    }

    // Renders an IHtmlContent the way Razor writes `@expr` to the page — this is
    // the path where LocalizedHtmlString runs string.Format over the template.
    private static string Render(Microsoft.AspNetCore.Html.IHtmlContent content)
    {
        using var sw = new StringWriter();
        content.WriteTo(sw, HtmlEncoder.Default);
        return sw.ToString();
    }

    [Theory]
    [InlineData("en")]
    public void Old_indexer_path_on_a_placeholder_template_throws_FormatException(string culture)
    {
        // Documents the bug: the IHtmlLocalizer indexer returns a LocalizedHtmlString
        // that, WHEN RENDERED (what `@Localizer["Common.CharCount"]` does in a view),
        // runs string.Format over "{0} / {1} characters" with ZERO args →
        // FormatException → unhandled → HTTP 500.
        var loc = MakeHtmlLocalizer();

        var ex = Assert.Throws<FormatException>(() =>
            WithCulture(culture, () => Render(loc["Common.CharCount"])));

        Assert.Contains("Index", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("en")]
    public void Fixed_GetString_path_renders_the_raw_template_without_throwing(string culture)
    {
        // The fix: GetString(key).Value returns the RAW value (no string.Format),
        // so the placeholders survive for the client-side counter and nothing throws
        // when the attribute is rendered.
        var loc = MakeHtmlLocalizer();

        var raw = WithCulture(culture, () => loc.GetString("Common.CharCount").Value);

        Assert.False(string.IsNullOrWhiteSpace(raw));
        Assert.Contains("{0}", raw);   // used
        Assert.Contains("{1}", raw);   // max

        // And the encoded attribute value itself renders cleanly (no FormatException).
        var rendered = WithCulture(culture,
            () => HtmlEncoder.Default.Encode(loc.GetString("Common.CharCount").Value));
        Assert.Contains("{0}", rendered);
        Assert.Contains("{1}", rendered);
    }

    [Fact]
    public void Signup_view_does_not_use_the_crashing_indexer_counter()
    {
        // The 3-step signup wizard (operator 2026-06-23) no longer has a free-text
        // char-counter field, so it must simply never use the crashing
        // `@Localizer["Common.CharCount"]` indexer form (which 500'd at render).
        var view = File.ReadAllText(WebRepoPaths.SignupCshtml);
        Assert.DoesNotContain("@Localizer[\"Common.CharCount\"]", view);
    }

    [Fact]
    public void All_public_counter_views_use_the_safe_GetString_form()
    {
        // The same defect lived on every page that emits the live counter template.
        // Guard them all so none can regress to the crashing indexer form.
        foreach (var path in WebRepoPaths.CounterViews)
        {
            var view = File.ReadAllText(path);
            Assert.DoesNotContain("data-ceh-counter=\"@Localizer[\"Common.CharCount\"]\"", view);
            Assert.Contains("@Localizer.GetString(\"Common.CharCount\").Value", view);
        }
    }
}

/// <summary>
/// Resolves the repo's view files from the test bin directory (walks up to the
/// repo root) so the source-level guards run on any build agent.
/// </summary>
internal static class WebRepoPaths
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CommunityHub.sln")))
            dir = dir.Parent;
        if (dir is null)
            throw new InvalidOperationException("Could not locate the repo root (CommunityHub.sln).");
        return dir.FullName;
    }

    private static string Pages(params string[] parts) =>
        Path.Combine(new[] { RepoRoot(), "src", "CommunityHub", "Pages" }.Concat(parts).ToArray());

    public static string SignupCshtml => Pages("Volunteer", "Signup.cshtml");

    // NOTE: Volunteer/Signup dropped — the 3-step wizard has no char-counter field.
    public static string[] CounterViews =>
    [
        Pages("Sponsor", "CaptureLead.cshtml"),
        Pages("Sessions", "Evaluate.cshtml"),
        // Sessions/Ask dropped — 1:1 questions disabled (§136), the page no longer has a counter field.
        Pages("Forms", "Speaker.cshtml"),
    ];
}
