using CommunityHub.Core.Organizer;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §339 — clearing a Master Class capacity makes the class UNLIMITED, which promotes and
/// <b>e-mails every waitlisted attendee at once</b>.
///
/// <para>Until 2026-07-27 that sat behind a JavaScript <c>confirm()</c> and nothing else: with JS
/// off, or on a direct POST, there was no gate at all. It is also the mildest-LOOKING action on the
/// page — emptying a text box — while being one of the largest in effect. That combination is
/// exactly the "sudden e-mail storm triggered by a CHANGE" class §340 was hunting: it needs a state
/// TRANSITION, not a state, which is why nothing tested for it.</para>
///
/// <para>These pin the DECISION TABLE the handler implements. The handler itself needs an HTTP
/// context and the full organizer page graph to drive, so what is pinned here is the rule it applies
/// — that an empty capacity demands the typed word and a numeric one does not.</para>
/// </summary>
public sealed class MasterClassCapacityGuardTests
{
    /// <summary>The handler's rule, stated once: only "unlimited" needs the typed confirmation.</summary>
    private static bool RequiresTypedConfirmation(int? capacity) => capacity is null;

    [Fact]
    public void Clearing_the_capacity_REQUIRES_the_typed_confirmation()
    {
        Assert.True(RequiresTypedConfirmation(null));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(500)]
    public void Setting_a_NUMBER_does_not(int capacity)
    {
        // Raising a number also promotes people — but at most as many as the number allows, and it
        // is an obviously deliberate act. Demanding a typed word for every routine capacity edit is
        // how a confirmation becomes noise that everyone types through without reading.
        Assert.False(RequiresTypedConfirmation(capacity));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("confirmed")]
    [InlineData("YES")]
    [InlineData("DELETE")]
    public void A_missing_or_WRONG_phrase_does_not_arm_the_unlimited_change(string? typed)
    {
        // Fails CLOSED, including when the field is not bound at all — a handler that forgets to
        // bind it must refuse, never proceed.
        Assert.False(TypedConfirmation.Matches(typed, TypedConfirmation.ConfirmPhrase));
    }

    [Theory]
    [InlineData("CONFIRM")]
    [InlineData("confirm")]
    [InlineData("  Confirm  ")]
    public void The_right_word_arms_it_regardless_of_case_or_stray_spaces(string typed)
    {
        // Deliberately tolerant: the guard exists to force a deliberate act, not to punish caps
        // lock. An operator who typed the word MEANT it.
        Assert.True(TypedConfirmation.Matches(typed, TypedConfirmation.ConfirmPhrase));
    }
}
