using CommunityHub.Core.Email;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Tests for the reusable broadcast templates and the {Token} substitution an
/// organizer's broadcast uses to personalize the subject and body per recipient.
/// Pure and offline.
/// </summary>
public class BroadcastTemplateTests
{
    private static Dictionary<string, string> Values(
        string firstName = "Alex", string eventName = "Demo 2027") =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["FirstName"] = firstName,
            ["EventName"] = eventName,
        };

    // ----------------------------------------------------------------------
    // Token substitution
    // ----------------------------------------------------------------------

    [Fact]
    public void Substitute_replaces_known_tokens()
    {
        var result = BroadcastTemplates.Substitute(
            "Hi {FirstName}, welcome to {EventName}.", Values());
        Assert.Equal("Hi Alex, welcome to Demo 2027.", result);
    }

    [Fact]
    public void Substitute_is_case_insensitive_on_the_token_name()
    {
        var result = BroadcastTemplates.Substitute("Hi {firstname}!", Values());
        Assert.Equal("Hi Alex!", result);
    }

    [Fact]
    public void Substitute_replaces_every_occurrence()
    {
        var result = BroadcastTemplates.Substitute(
            "{FirstName} {FirstName} {FirstName}", Values(firstName: "Sam"));
        Assert.Equal("Sam Sam Sam", result);
    }

    [Fact]
    public void Substitute_leaves_unknown_tokens_untouched()
    {
        // A mistyped/unknown token survives verbatim rather than vanishing.
        var result = BroadcastTemplates.Substitute(
            "Hi {FirstName}, your {Mystery} is ready.", Values());
        Assert.Equal("Hi Alex, your {Mystery} is ready.", result);
    }

    [Fact]
    public void Substitute_handles_empty_and_tokenless_text()
    {
        Assert.Equal("", BroadcastTemplates.Substitute("", Values()));
        Assert.Equal("No tokens here.",
            BroadcastTemplates.Substitute("No tokens here.", Values()));
    }

    // ----------------------------------------------------------------------
    // Built-in templates
    // ----------------------------------------------------------------------

    [Fact]
    public void Built_in_set_has_blank_plus_the_three_named_templates()
    {
        var keys = BroadcastTemplates.BuiltIn.Select(t => t.Key).ToList();
        Assert.Contains("blank", keys);
        Assert.Contains("generic", keys);
        Assert.Contains("reminder", keys);
        Assert.Contains("welcome", keys);
    }

    [Fact]
    public void Built_in_keys_are_unique_and_have_display_names()
    {
        Assert.Equal(
            BroadcastTemplates.BuiltIn.Count,
            BroadcastTemplates.BuiltIn.Select(t => t.Key).Distinct().Count());
        Assert.All(BroadcastTemplates.BuiltIn,
            t => Assert.False(string.IsNullOrWhiteSpace(t.DisplayName)));
    }

    [Fact]
    public void Blank_template_is_empty()
    {
        var blank = BroadcastTemplates.Find("blank");
        Assert.NotNull(blank);
        Assert.Equal("", blank!.Subject);
        Assert.Equal("", blank.Body);
    }

    [Fact]
    public void Find_is_case_insensitive_and_null_for_unknown()
    {
        Assert.NotNull(BroadcastTemplates.Find("REMINDER"));
        Assert.Null(BroadcastTemplates.Find("does-not-exist"));
        Assert.Null(BroadcastTemplates.Find(null));
        Assert.Null(BroadcastTemplates.Find(""));
    }

    [Fact]
    public void Named_templates_substitute_their_tokens_end_to_end()
    {
        var reminder = BroadcastTemplates.Find("reminder")!;
        var subject = BroadcastTemplates.Substitute(reminder.Subject, Values());
        var body = BroadcastTemplates.Substitute(reminder.Body, Values());

        Assert.Contains("Demo 2027", subject);
        Assert.Contains("Demo 2027", body);
        // No leftover token braces after substitution of the known tokens.
        Assert.DoesNotContain("{EventName}", body);
    }
}
