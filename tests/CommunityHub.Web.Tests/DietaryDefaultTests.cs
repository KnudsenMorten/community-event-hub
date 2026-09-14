using CommunityHub.Core.Domain;
using CommunityHub.Pages.Shared;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §1086b — <b>THE APPRECIATION DINNER DEFAULTS TO "NO SPECIAL DIET", NOT VEGETARIAN.</b>
/// </summary>
/// <remarks>
/// <para>🔴 Operator 2026-08-14: <i>"did you fix so default option for appreciation dinner is no
/// special requirement and not vegetarian as it was one time"</i>. It was fixed on 2026-07-11 — and
/// nothing pinned it, which is why he had to ask a month later instead of reading it off a test.</para>
///
/// <para>🔑 Two halves, and BOTH have to hold: the pre-selected value on a fresh form, and the value
/// that PERSISTS when somebody never touches the dropdown. A blank default would silently store
/// "not stated"; a wrong first option would silently order the wrong meal for everyone who submits
/// without looking.</para>
///
/// <para>⚠️ The dinner is the ONLY place CEH captures dietary requirements — lunch options are
/// agreed with the venue in the ordering process (§1086b).</para>
/// </remarks>
public sealed class DietaryDefaultTests
{
    [Fact]
    public void A_fresh_dietary_input_defaults_to_no_special_diet()
    {
        Assert.Equal("None", new DietaryInput().DietChoice);
    }

    /// <summary>
    /// The dropdown renders <see cref="DietaryRequirement.DietChoices"/> in order, so the FIRST
    /// entry is what a browser pre-selects. It must be "None" — the one-time defect was Vegetarian
    /// sitting at the top of the list.
    /// </summary>
    [Fact]
    public void No_special_diet_is_the_first_choice_in_the_dropdown()
    {
        Assert.Equal("None", DietaryRequirement.DietChoices[0]);
        Assert.NotEqual("Vegetarian", DietaryRequirement.DietChoices[0]);
    }

    /// <summary>An untouched form stores "None", not null — "not stated" is not an answer.</summary>
    [Fact]
    public void An_untouched_form_persists_no_special_diet()
    {
        var row = new DietaryRequirement();
        new DietaryInput().ApplyTo(row);

        Assert.Equal("None", row.DietChoice);
    }
}
