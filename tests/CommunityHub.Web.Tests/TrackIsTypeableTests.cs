using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// 🔴 §1245 — the session Track must be TYPEABLE, not a closed dropdown.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-20: *"i am missing the Community track in CEH"*. The Track list is built
/// from the tracks sessions ALREADY use — there is no configured track registry the way there is
/// for rooms (<c>sessionRooms</c>) — so a track that exists only in the call-for-speakers tool was
/// unreachable from the hub:</para>
///
/// <para>🔑 <b>It was absent BECAUSE no session used it, and no session could use it BECAUSE it was
/// absent.</b> A closed list built from existing data cannot introduce a value. That is the bug,
/// and it would have recurred on every new track he ever added.</para>
///
/// <para>⚠️ The datalist is not decoration: it keeps the existing names one click away, which is
/// what stops a typo creating a near-duplicate track — and a track typo is expensive, because the
/// Backstage push CREATES a missing track by name on an API with no delete.</para>
/// </remarks>
public sealed class TrackIsTypeableTests
{
    private static string SessionsPage() =>
        File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "CommunityHub", "Pages", "Organizer", "Sessions.cshtml"));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CommunityHub.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root not found");
    }

    [Fact]
    public void The_track_field_is_an_input_not_a_select()
    {
        var page = SessionsPage();

        // The input exists, bound to the same name the handler already reads.
        Assert.Contains("<input id=\"EditTrack_@r.Id\" name=\"EditTrack\" type=\"text\"", page);

        // 🛑 And the closed dropdown is GONE. Asserted explicitly because the obvious "tidy-up"
        // later is to make Track look like Room again — but Room has a config registry and Track
        // does not, which is the whole reason one can be a select and the other cannot.
        Assert.DoesNotContain("""asp-items="Model.TrackOptions(""", page);
    }

    [Fact]
    public void The_input_offers_the_known_tracks_as_suggestions()
    {
        var page = SessionsPage();

        Assert.Contains("list=\"ceh-known-tracks\"", page);
        Assert.Contains("""<datalist id="ceh-known-tracks">""", page);
        Assert.Contains("Model.KnownTracks", page);
    }

    [Fact]
    public void The_placeholder_says_the_field_can_be_TYPED_into()
    {
        // ⚠️ §1245b — the field first used "— none —", which is exactly how a <select> renders its
        // empty option. The browser draws a datalist input with the same chevron as a dropdown, so
        // the placeholder is the ONLY signal that typing is allowed — and without it the operator
        // reported he "cannot select Community" when he could simply have typed it.
        var page = SessionsPage();

        Assert.Contains("Type a track, or pick one", page);
        Assert.DoesNotContain("placeholder=\"— none —\"", page);
    }

    [Fact]
    public void The_datalist_is_rendered_once_not_per_row()
    {
        // The grid can hold 70+ sessions. One shared list, not eight options seventy times.
        var page = SessionsPage();
        var occurrences = page.Split("<datalist id=\"ceh-known-tracks\">").Length - 1;

        Assert.Equal(1, occurrences);
    }
}
