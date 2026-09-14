using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1164 — a record the engine correctly refused to create is NOT a failure.
///
/// <para>Operator 2026-08-31, on receiving the drift alert for exactly that: <i>"still getting
/// errors"</i>. It was not an error — §1163 had just declined to re-create an exhibitor record whose
/// order he had cancelled, which is the fix working. The alert fired because the decline was counted
/// as <c>Skipped</c>, and <c>Skipped &gt; 0</c> is what raises it.</para>
///
/// <para>🔑 <b>Why this matters more than it looks.</b> Mailing him every fifteen minutes about the
/// system behaving correctly is how an operator learns to ignore the alert that will one day matter.
/// It is the §1154 complaint — <i>"this mail is not relevant"</i> — arriving in a new place.</para>
/// </summary>
public sealed class ProvisionDeclinedNotSkippedTests
{
    private static SponsorZohoProvisionService.ProvisionResult Result(
        int skipped = 0, int declined = 0) =>
        new(Enabled: true, SponsorsCreated: 0, SponsorsLinked: 0,
            ExhibitorsCreated: 0, ExhibitorsRequested: 0, ExhibitorsLinked: 0,
            Skipped: skipped, Notes: new List<string>(),
            Declined: declined, DeclinedNotes: new List<string>());

    /// <summary>
    /// 🔴 The production case: one deliberate decline, nothing skipped ⇒ no alert.
    /// </summary>
    /// <remarks>
    /// The alert condition in <c>WooCommercePullJob</c> is <c>Skipped &gt; 0</c>, so this assertion
    /// is the whole fix: a decline must leave that number at zero.
    /// </remarks>
    [Fact]
    public void A_deliberate_decline_does_not_raise_the_skipped_count()
    {
        var r = Result(declined: 1);

        Assert.Equal(0, r.Skipped);
        Assert.Equal(1, r.Declined);
    }

    /// <summary>
    /// 🔒 A genuine failure still counts, and still alerts.
    /// </summary>
    /// <remarks>
    /// The point of separating them is NOT to quieten the alert — it is to make it mean something.
    /// A create that actually failed must stay as loud as it was.
    /// </remarks>
    [Fact]
    public void A_real_failure_still_counts_as_skipped()
    {
        var r = Result(skipped: 1);

        Assert.Equal(1, r.Skipped);
        Assert.Equal(0, r.Declined);
    }

    [Fact]
    public void The_two_are_counted_independently()
    {
        var r = Result(skipped: 2, declined: 3);

        Assert.Equal(2, r.Skipped);
        Assert.Equal(3, r.Declined);
    }

    /// <summary>
    /// 🔒 The new fields are OPTIONAL, so every existing construction still compiles and means
    /// exactly what it meant before.
    /// </summary>
    /// <remarks>
    /// Three early-return paths in the provisioner build this record positionally with eight
    /// arguments (not enabled / nothing in scope / no token). Had the new fields been required,
    /// those would have had to change too — and a required "declined" on a path that declined
    /// nothing is an invitation to pass the wrong number.
    /// </remarks>
    [Fact]
    public void The_new_fields_default_to_nothing_declined()
    {
        var legacy = new SponsorZohoProvisionService.ProvisionResult(
            true, 0, 0, 0, 0, 0, 0, new List<string>());

        Assert.Equal(0, legacy.Declined);
        Assert.Null(legacy.DeclinedNotes);
    }
}
