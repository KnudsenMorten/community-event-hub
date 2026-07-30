using CommunityHub.Core.Email;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §563/§561 — every mail row on the Settings page must say WHO GETS IT.
/// </summary>
/// <remarks>
/// Operator, pointing at the cross-role section heading: <i>"this is very confusing - who is this"</i>,
/// and at a single narrow row: <i>"i dont understand why one single task becomes something in ring"</i>.
/// Both are the same complaint — a row showing a key and a ring number never said who it reaches, so
/// the audience had to be inferred, and a handful-of-sponsors mail looked as weighty as a persona
/// welcome.
/// </remarks>
public class EmailRecipientHintTests
{
    /// <summary>
    /// 🔒 The mechanical guarantee: a NEW template cannot ship as another unexplained row. Same
    /// shape as JobCatalogCompletenessTests, and the reason the Jobs page never drifted.
    /// </summary>
    [Fact]
    public void EVERY_catalog_template_has_its_own_recipient_hint()
    {
        var fallback = EmailTemplateCatalog.RecipientHint("a-key-that-does-not-exist");

        var missing = EmailTemplateCatalog.Map.Keys
            .Where(k => EmailTemplateCatalog.RecipientHint(k) == fallback)
            .OrderBy(k => k)
            .ToList();

        Assert.True(missing.Count == 0,
            "These templates fall back to the generic hint and would render as an unexplained row "
            + "on the Settings page — give each one a line saying who receives it: "
            + string.Join(", ", missing));
    }

    [Fact]
    public void Hints_read_as_sentences_not_as_labels()
    {
        foreach (var key in EmailTemplateCatalog.Map.Keys)
        {
            var hint = EmailTemplateCatalog.RecipientHint(key);
            Assert.EndsWith(".", hint);
            // A hint shorter than this is a label, and a label is what the page already had.
            Assert.True(hint.Length >= 25, $"'{key}' hint is too terse to answer 'who is this': {hint}");
        }
    }

    /// <summary>
    /// §563 — the bucket is the DEFAULT for anything unclassified, so a heading claiming a
    /// deliberate audience ("Everyone") was a lie about the grouping.
    /// </summary>
    [Fact]
    public void The_cross_role_section_no_longer_claims_to_be_EVERYONE()
    {
        var label = EmailTemplateCatalog.AudienceLabel(EmailAudience.Everyone);

        Assert.DoesNotContain("Everyone", label, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Any role", label, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// §561 — the row he singled out. Its hint must state the NARROWNESS, because "why is this in a
    /// ring at all" is answered by "it reaches very few people", not by a ring number.
    /// </summary>
    [Fact]
    public void The_app_game_gift_reminder_says_how_FEW_people_it_reaches()
    {
        var hint = EmailTemplateCatalog.RecipientHint("app-game-gift-reminder");

        Assert.Contains("not all sponsors", hint, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// §561/§563 — the task reminders are cross-role BY DESIGN (the recipient is the task's owner,
    /// whatever role that is). That is exactly why they cannot be filed under one role, and the hint
    /// has to say so or the bucket looks like a dumping ground again.
    /// </summary>
    [Theory]
    [InlineData("task-deadline-reminder")]
    [InlineData("task-manual-reminder")]
    public void Task_reminders_say_the_recipient_is_the_task_OWNER(string key)
    {
        Assert.Equal(EmailAudience.Everyone, EmailTemplateCatalog.AudienceFor(key));

        var hint = EmailTemplateCatalog.RecipientHint(key);
        Assert.Contains("owns the task", hint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("any role", hint, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 🔒 §515's rule, restated where it keeps being got wrong: audience is the RECIPIENT, not the
    /// subject matter. §561 was exactly this mistake — the group-photo invite filed under Speaker
    /// because it is about speakers.
    /// </summary>
    [Theory]
    [InlineData("hotel-cutoff-reminder")]
    [InlineData("group-photo-invite")]
    public void Organizer_ops_mail_says_plainly_that_it_is_NOT_sent_to_the_role_it_is_about(string key)
    {
        Assert.Equal(EmailAudience.Organizer, EmailTemplateCatalog.AudienceFor(key));
        Assert.Contains("Organizers only", EmailTemplateCatalog.RecipientHint(key));
    }
}
