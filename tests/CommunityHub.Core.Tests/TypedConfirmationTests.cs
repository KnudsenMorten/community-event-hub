using CommunityHub.Core.Organizer;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §334 — the server-side half of the typed-confirmation guard on irreversible organizer
/// actions. The whole point of the guard is that it FAILS CLOSED: a handler that forgets to
/// bind the field, a scripted POST, or a form replayed with JavaScript off must all be refused
/// rather than silently allowed.
/// </summary>
public class TypedConfirmationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("delet")]
    [InlineData("DELETED")]
    [InlineData("CONFIRM")]     // the OTHER phrase must not satisfy this one
    [InlineData("yes")]
    public void Fails_closed_on_anything_that_is_not_the_phrase(string? typed)
    {
        Assert.False(TypedConfirmation.Matches(typed, TypedConfirmation.DeletePhrase));
    }

    [Theory]
    [InlineData("DELETE")]
    [InlineData("delete")]      // caps-lock state is not the point of the guard
    [InlineData("Delete")]
    [InlineData("  DELETE  ")]  // nor is a stray space from a paste
    public void Accepts_the_phrase_case_insensitively_and_trimmed(string typed)
    {
        Assert.True(TypedConfirmation.Matches(typed, TypedConfirmation.DeletePhrase));
    }

    [Fact]
    public void The_two_phrases_are_distinct_so_one_cannot_arm_the_other()
    {
        Assert.NotEqual(TypedConfirmation.DeletePhrase, TypedConfirmation.ConfirmPhrase);
        Assert.False(TypedConfirmation.Matches(
            TypedConfirmation.DeletePhrase, TypedConfirmation.ConfirmPhrase));
        Assert.False(TypedConfirmation.Matches(
            TypedConfirmation.ConfirmPhrase, TypedConfirmation.DeletePhrase));
    }

    [Fact]
    public void Rejection_message_names_the_phrase_and_the_action_so_it_is_actionable()
    {
        var msg = TypedConfirmation.Rejection(TypedConfirmation.ConfirmPhrase, "withdraw Acme");

        Assert.Contains("CONFIRM", msg);
        Assert.Contains("withdraw Acme", msg);
        Assert.Contains("Nothing was changed", msg);
    }
}
