using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §1244 — the two intake queues are reachable from the hub you would naturally look on.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-20, after not finding them: *"can you also add these 2 under sessions &amp;
/// speakers + volunteers, where it makes sense to look also. keep both places"*.</para>
///
/// <para>🔑 <b>"Keep both places" is the assertion that matters.</b> The obvious tidy-up later is to
/// deduplicate a tile that appears on two hubs — which is exactly what he asked against. These are
/// the same page behind a second door, not a copy, and the same pattern §1146b already established
/// for the Pre-Selection tile on the Volunteers hub.</para>
///
/// <para>⚠️ Asserted against the razor SOURCE rather than a rendered page: these are static tile
/// lists, and a source check fails for the one reason that matters (somebody removed the tile)
/// without dragging a host and an authenticated organizer into the test.</para>
/// </remarks>
public sealed class IntakeQueueTilesTests
{
    private const string PendingSpeakers = "/Organizer/PendingSpeakers";
    private const string PreselectionQueue = "/Organizer/PreselectionQueue";

    private static string Hub(string page) =>
        File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "CommunityHub", "Pages", "Organizer", page + ".cshtml"));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CommunityHub.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root not found");
    }

    [Theory]
    [InlineData("People", PendingSpeakers)]          // the original home — must stay
    [InlineData("People", PreselectionQueue)]
    [InlineData("Content", PendingSpeakers)]         // Content.cshtml IS the Sessions & Speakers hub
    [InlineData("Content", PreselectionQueue)]
    [InlineData("Volunteers", PreselectionQueue)]    // §1146b
    public void The_queue_is_reachable_from_this_hub(string hub, string page)
    {
        Assert.Contains(page, Hub(hub), StringComparison.Ordinal);
    }

    [Fact]
    public void Pending_speakers_sits_with_the_import_that_creates_its_backlog()
    {
        // 🔑 Position is the point, not mere presence: the tile is directly after the Sessionize
        // import, because the place you run the import is the place you then look to approve what
        // it brought in. A tile shuffled to the bottom of a 12-tile hub is findable but not found.
        var content = Hub("Content");
        var import = content.IndexOf("/Organizer/SessionizeImport", StringComparison.Ordinal);
        var pending = content.IndexOf(PendingSpeakers, StringComparison.Ordinal);

        Assert.True(import >= 0 && pending > import,
            "Pending speakers should follow the Sessionize import tile on the Sessions & Speakers hub.");
    }
}
