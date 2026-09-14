using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1159 — the Zoho id is the identity; the company name is only a bootstrap.
///
/// <para>Operator 2026-08-31, after the public-name cleanup: <i>"fx ARROW ECS
/// Norway/Denmark/Finland must only be ARROW"</i> — four separate webshop companies, one public
/// name. Right for the sponsor wall, and fatal to every lookup that matched a CEH company to its
/// Zoho record by name.</para>
///
/// <para>🔑 <b>The failure it prevents is silent.</b> Two companies pointed at one Zoho record do
/// not error: each reconcile pushes its own website and description over the other's, so the record
/// flips every few minutes and the symptom reads as "the sync keeps reverting my edit" rather than
/// as a linking bug.</para>
///
/// <para>NO real customer names.</para>
/// </summary>
public sealed class ZohoRecordMatchTests
{
    private static IReadOnlySet<string> Claimed(params string[] ids) =>
        ids.ToHashSet(StringComparer.Ordinal);

    private static readonly (string Id, string Name)[] FourWithOneName =
    {
        ("Z-1", "ARROW"), ("Z-2", "ARROW"), ("Z-3", "ARROW"), ("Z-4", "ARROW"),
    };

    [Fact]
    public void One_record_with_the_name_is_the_match()
    {
        var r = ZohoRecordMatch.ByName(
            new[] { ("Z-1", "Contoso"), ("Z-2", "Fabrikam") }, "Contoso", Claimed());

        Assert.Equal("Z-1", r.Id);
        Assert.Null(r.RefusalReason);
    }

    [Fact]
    public void Matching_ignores_case_and_surrounding_space()
    {
        var r = ZohoRecordMatch.ByName(new[] { ("Z-1", " contoso ") }, "CONTOSO", Claimed());
        Assert.Equal("Z-1", r.Id);
    }

    /// <summary>
    /// 🔴 The ARROW case: four records share the name and none is linked yet.
    /// </summary>
    [Fact]
    public void Several_unclaimed_records_with_the_same_name_are_refused_not_guessed()
    {
        var r = ZohoRecordMatch.ByName(FourWithOneName, "ARROW", Claimed());

        Assert.Null(r.Id);
        Assert.NotNull(r.RefusalReason);
        Assert.Contains("4", r.RefusalReason!);
    }

    /// <summary>
    /// 🔑 Once the other three are linked, the remaining one is unambiguous.
    /// </summary>
    /// <remarks>
    /// This is what makes the refusal self-clearing rather than permanent: as each company acquires
    /// its record, the candidate set for the next one shrinks, and the last is resolved on its own.
    /// </remarks>
    [Fact]
    public void The_last_unclaimed_record_resolves_once_the_others_are_taken()
    {
        var r = ZohoRecordMatch.ByName(FourWithOneName, "ARROW", Claimed("Z-1", "Z-2", "Z-3"));

        Assert.Equal("Z-4", r.Id);
        Assert.Null(r.RefusalReason);
    }

    /// <summary>
    /// 🔒 A record another company holds is never handed out, however exactly the names agree.
    /// </summary>
    [Fact]
    public void A_record_claimed_by_another_company_is_not_a_match()
    {
        var r = ZohoRecordMatch.ByName(new[] { ("Z-1", "ARROW") }, "ARROW", Claimed("Z-1"));

        // Not a match AND not a refusal: this company simply has no record yet, so the caller
        // should CREATE one. Reporting an ambiguity here would be wrong — there is none.
        Assert.Null(r.Id);
        Assert.Null(r.RefusalReason);
    }

    [Fact]
    public void Every_candidate_taken_means_create_one_not_report_a_problem()
    {
        var r = ZohoRecordMatch.ByName(FourWithOneName, "ARROW", Claimed("Z-1", "Z-2", "Z-3", "Z-4"));

        Assert.Null(r.Id);
        Assert.Null(r.RefusalReason);
    }

    [Fact]
    public void No_record_with_that_name_is_simply_no_match()
    {
        var r = ZohoRecordMatch.ByName(new[] { ("Z-1", "Contoso") }, "Fabrikam", Claimed());

        Assert.Null(r.Id);
        Assert.Null(r.RefusalReason);
    }

    /// <summary>
    /// ⚠️ A blank wanted name matches NOTHING — it must never match a blank record name.
    /// </summary>
    /// <remarks>
    /// A company whose name failed to resolve arrives here blank. Matching it to a Zoho record whose
    /// name is also blank would link two unrelated things on the strength of two absences.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_name_never_matches(string? wanted)
    {
        var r = ZohoRecordMatch.ByName(new[] { ("Z-1", ""), ("Z-2", "Contoso") }, wanted, Claimed());

        Assert.Null(r.Id);
        Assert.Null(r.RefusalReason);
    }

    [Fact]
    public void A_record_with_no_id_is_ignored()
    {
        var r = ZohoRecordMatch.ByName(
            new[] { ("", "Contoso"), ("Z-2", "Contoso") }, "Contoso", Claimed());

        // 🔒 Without this the empty id would count as a second candidate and turn a perfectly
        // resolvable company into a reported ambiguity.
        Assert.Equal("Z-2", r.Id);
    }
}
