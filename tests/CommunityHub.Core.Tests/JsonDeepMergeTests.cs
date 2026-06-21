using System.Text.Json;
using CommunityHub.Core.Config;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for <see cref="JsonDeepMerge"/>, the reusable deep-merge that
/// backs the HYBRID config model (shipped JSON default + per-edition SQL override
/// fragment). Pins the merge rules the loaders depend on: scalar override, nested
/// object merge, array REPLACE, missing override (defaults pass through),
/// malformed override (fail-safe to default, no throw), and null/empty override.
/// </summary>
public sealed class JsonDeepMergeTests
{
    /// <summary>Parse + re-emit so two JSON strings compare regardless of formatting/key order.</summary>
    private static string Canonical(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(doc.RootElement);
    }

    private static void AssertJsonEqual(string expected, string actual) =>
        Assert.Equal(Canonical(expected), Canonical(actual));

    [Fact]
    public void Scalar_override_replaces_the_default_scalar()
    {
        var result = JsonDeepMerge.Merge(
            """{ "code": "ELDK27", "expectedAttendees": 400 }""",
            """{ "expectedAttendees": 550 }""");

        AssertJsonEqual("""{ "code": "ELDK27", "expectedAttendees": 550 }""", result);
    }

    [Fact]
    public void Nested_object_is_merged_key_by_key_not_wholesale_replaced()
    {
        var result = JsonDeepMerge.Merge(
            """{ "dates": { "day1": "2027-02-09", "day2": "2027-02-10", "timezone": "Europe/Copenhagen" } }""",
            """{ "dates": { "day2": "2027-02-11" } }""");

        // day1 + timezone survive from the default; only day2 is overridden.
        AssertJsonEqual(
            """{ "dates": { "day1": "2027-02-09", "day2": "2027-02-11", "timezone": "Europe/Copenhagen" } }""",
            result);
    }

    [Fact]
    public void Override_adds_a_key_absent_from_the_default()
    {
        var result = JsonDeepMerge.Merge(
            """{ "edition": { "code": "X" } }""",
            """{ "ticketSale": { "enabled": true } }""");

        AssertJsonEqual(
            """{ "edition": { "code": "X" }, "ticketSale": { "enabled": true } }""",
            result);
    }

    [Fact]
    public void Array_override_replaces_the_whole_array()
    {
        var result = JsonDeepMerge.Merge(
            """{ "sections": [ { "title": "A" }, { "title": "B" } ] }""",
            """{ "sections": [ { "title": "C" } ] }""");

        // The whole array is replaced, NOT element-merged or concatenated.
        AssertJsonEqual("""{ "sections": [ { "title": "C" } ] }""", result);
    }

    [Fact]
    public void Missing_override_passes_the_default_through_unchanged()
    {
        const string def = """{ "code": "ELDK27", "expectedAttendees": 400 }""";

        Assert.Equal(def, JsonDeepMerge.Merge(def, null));
        Assert.Equal(def, JsonDeepMerge.Merge(def, ""));
        Assert.Equal(def, JsonDeepMerge.Merge(def, "   "));
    }

    [Fact]
    public void Malformed_override_falls_back_to_the_default_and_does_not_throw()
    {
        const string def = """{ "code": "ELDK27" }""";

        // Not valid JSON — must be swallowed and the default returned verbatim.
        var result = JsonDeepMerge.Merge(def, "{ this is : not json ]");

        Assert.Equal(def, result);
    }

    [Fact]
    public void Empty_object_override_changes_nothing()
    {
        var result = JsonDeepMerge.Merge(
            """{ "code": "ELDK27", "expectedAttendees": 400 }""",
            "{}");

        AssertJsonEqual("""{ "code": "ELDK27", "expectedAttendees": 400 }""", result);
    }

    [Fact]
    public void Explicit_null_in_override_clears_the_value()
    {
        var result = JsonDeepMerge.Merge(
            """{ "ticketUrl": "https://example.test/buy" }""",
            """{ "ticketUrl": null }""");

        AssertJsonEqual("""{ "ticketUrl": null }""", result);
    }

    [Fact]
    public void Override_of_a_scalar_with_an_object_replaces_wholesale()
    {
        // Shapes differ (default scalar, override object) → override wins.
        var result = JsonDeepMerge.Merge(
            """{ "x": 5 }""",
            """{ "x": { "nested": true } }""");

        AssertJsonEqual("""{ "x": { "nested": true } }""", result);
    }

    [Fact]
    public void Deep_three_level_merge_preserves_untouched_branches()
    {
        var result = JsonDeepMerge.Merge(
            """{ "a": { "b": { "c": 1, "d": 2 }, "e": 9 } }""",
            """{ "a": { "b": { "c": 100 } } }""");

        AssertJsonEqual(
            """{ "a": { "b": { "c": 100, "d": 2 }, "e": 9 } }""",
            result);
    }

    [Fact]
    public void Inputs_are_not_mutated_returns_a_fresh_tree()
    {
        const string def = """{ "n": { "k": 1 } }""";
        const string ovr = """{ "n": { "k": 2 } }""";

        var first = JsonDeepMerge.Merge(def, ovr);
        var second = JsonDeepMerge.Merge(def, ovr);

        // Repeatable + the default text is never altered by a prior merge.
        AssertJsonEqual(first, second);
        AssertJsonEqual("""{ "n": { "k": 1 } }""", def);
    }
}
