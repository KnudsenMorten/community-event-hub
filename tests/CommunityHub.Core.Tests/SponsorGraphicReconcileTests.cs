using CommunityHub.Core.Integrations.Graphics;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1143 — a GraphicAsset row asserts that a file exists. When it does not, the row is a lie, and
/// two different consumers believe it.
///
/// <para>Operator 2026-08-28: <i>"if i delete a sharepoint file, it must detect it is gone and reset
/// the state"</i> — after finding a sponsor whose graphic file was present but whose posts were not
/// planned, and reasoning correctly that <i>"someone must tell the planner it exists"</i>.</para>
///
/// <para>🔑 <b>The file is a side effect; the row is the record.</b> `StoreAndUpsertAsync` uploads
/// to SharePoint FIRST and writes the row SECOND with no transaction across the two, so the pair can
/// diverge in both directions. A row without a file was the damaging one: `SoMeScheduleService`
/// plans a sponsor purely because a row exists, and the sweep skips the rebuild while `InputHash`
/// still matches — so a deleted file was PERMANENT, and the campaign kept scheduling posts for
/// artwork that was gone.</para>
/// </summary>
public class SponsorGraphicReconcileTests
{
    private static IReadOnlySet<string> Missing(string[] present, string[] recorded) =>
        SoMeBundleBuildService.MissingGraphicFileNames(present, recorded);

    [Fact]
    public void A_deleted_file_is_detected()
    {
        var missing = Missing(
            present: new[] { "sponsor-12.png", "sponsor-18.png" },
            recorded: new[] { "sponsor-12.png", "sponsor-18.png", "sponsor-36.png" });

        Assert.Single(missing);
        Assert.Contains("sponsor-36.png", missing);
    }

    [Fact]
    public void Nothing_is_reset_while_every_file_is_present()
    {
        var missing = Missing(
            present: new[] { "sponsor-12.png", "sponsor-36.png" },
            recorded: new[] { "sponsor-36.png", "sponsor-12.png" });

        Assert.Empty(missing);
    }

    /// <summary>
    /// 🔴 THE ONE THAT PREVENTS A SELF-INFLICTED OUTAGE.
    /// </summary>
    /// <remarks>
    /// An empty listing and "every file was deleted" are the same value and completely different
    /// facts. Acting on the first as if it were the second clears the file fields off every row in
    /// the edition and un-plans the entire sponsor campaign because one Graph call came back thin —
    /// far worse than the stale row the sweep set out to clean up. A short read is not a deletion.
    /// </remarks>
    [Fact]
    public void An_EMPTY_listing_resets_NOTHING()
    {
        var missing = Missing(
            present: Array.Empty<string>(),
            recorded: new[] { "sponsor-12.png", "sponsor-18.png", "sponsor-36.png" });

        Assert.Empty(missing);
    }

    [Fact]
    public void A_row_with_no_file_name_is_not_counted_as_missing()
    {
        // Already reset by an earlier pass — it is awaiting rebuild, not a fresh discovery.
        var missing = SoMeBundleBuildService.MissingGraphicFileNames(
            new[] { "sponsor-12.png" },
            new string?[] { "sponsor-12.png", null, "" });

        Assert.Empty(missing);
    }

    [Fact]
    public void Comparison_ignores_case_because_SharePoint_does()
    {
        var missing = Missing(
            present: new[] { "Sponsor-36.PNG" },
            recorded: new[] { "sponsor-36.png" });

        // A case-sensitive compare would report a file that is plainly there as deleted, and the
        // sweep would rebuild it on every single run.
        Assert.Empty(missing);
    }

    [Fact]
    public void Blank_entries_in_the_listing_do_not_make_the_folder_look_populated()
    {
        // 🔒 The empty-listing guard must not be defeated by a listing of nothing-but-blanks: that
        // is still "I learned nothing", and it must still reset nothing.
        var missing = SoMeBundleBuildService.MissingGraphicFileNames(
            new string?[] { null, "", "   " },
            new[] { "sponsor-36.png" });

        Assert.Empty(missing);
    }
}
