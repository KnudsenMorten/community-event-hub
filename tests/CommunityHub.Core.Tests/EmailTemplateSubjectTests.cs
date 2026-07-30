using CommunityHub.Core.Email;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §566 step 5 — the Settings page row is <c>Subject: "&lt;the real subject&gt;"</c> on line 1 and
/// <c>internal-key · Ring N</c> on line 2 (operator 2026-07-28: *"include both the internal jargon +
/// subject so it is 100% clear to everyone !"*).
///
/// <para>Line 1 is READ FROM THE TEMPLATE so it cannot drift from the mail that actually goes out.
/// That only works if every template HAS a <c>Subject:</c> line — so this fails the BUILD rather
/// than silently rendering a blank row. A page that quietly shows nothing is how the Settings page
/// became machine output nobody trusted (§563/§564).</para>
/// </summary>
public class EmailTemplateSubjectTests
{
    private static string TemplatesDir()
    {
        // Walk up from the test bin to the repo root, then into templates/emails.
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "templates", "emails");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException(
            "templates/emails not found by walking up from " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Every_shipped_template_declares_a_subject_line()
    {
        var missing = new List<string>();
        foreach (var file in Directory.GetFiles(TemplatesDir(), "*.html"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.StartsWith("_", StringComparison.Ordinal)) continue;   // _layout shell
            if (EmailTemplateProvider.ReadableSubject(File.ReadAllText(file)).Length == 0)
                missing.Add(name);
        }

        Assert.True(missing.Count == 0,
            "These templates have no 'Subject:' line, so the Settings page would render an empty "
            + "subject for them: " + string.Join(", ", missing));
    }

    [Fact]
    public void Tokens_in_the_subject_become_readable_bracketed_placeholders()
    {
        // The operator's own example: "Master Class cancelled: [Master Class]".
        Assert.Equal(
            "Master Class cancelled: [Master Class Title]",
            EmailTemplateProvider.ReadableSubject("Subject: Master Class cancelled: {{masterClassTitle}}"));

        // snake_case and whitespace inside the braces are handled the same way.
        Assert.Equal(
            "Hello [Full Name]",
            EmailTemplateProvider.ReadableSubject("Subject: Hello {{ full_name }}"));

        // A subject with no tokens is passed through untouched.
        Assert.Equal(
            "Your sign-in link",
            EmailTemplateProvider.ReadableSubject("Subject: Your sign-in link\n<p>body</p>"));
    }

    [Fact]
    public void Missing_or_blank_subject_yields_empty_string_rather_than_throwing()
    {
        // Non-throwing by design: a cosmetic gap must never 500 the Settings page. The
        // build-failing test above is what ensures the gap does not exist in the first place.
        Assert.Equal(string.Empty, EmailTemplateProvider.ReadableSubject(null));
        Assert.Equal(string.Empty, EmailTemplateProvider.ReadableSubject(""));
        Assert.Equal(string.Empty, EmailTemplateProvider.ReadableSubject("<p>no subject here</p>"));
        Assert.Equal(string.Empty, EmailTemplateProvider.ReadableSubject("Subject:   "));
    }

    [Fact]
    public void Subject_is_found_even_when_it_is_not_the_very_first_line()
    {
        // Tolerant of a stray leading blank line — blanking a real subject over whitespace would
        // be a silent, cosmetic-looking regression.
        Assert.Equal("Later subject",
            EmailTemplateProvider.ReadableSubject("\n\nSubject: Later subject\n<p>body</p>"));
    }
}
