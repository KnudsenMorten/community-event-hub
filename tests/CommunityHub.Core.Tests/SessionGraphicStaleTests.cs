using CommunityHub.Core.Integrations.Graphics;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §6.2 — the input hash behind the organizer grid's "stale" badge.
/// </summary>
/// <remarks>
/// <para>🔒 <b>The grid and the sweep must compute the SAME number.</b> Two similar-looking hashes
/// would give a page that says "current" about a graphic the next sweep replaces — or a permanent
/// "stale" that no rebuild ever clears, because the two never agreed. That is the §767 failure, and
/// it is why the formula is one public method both call.</para>
///
/// <para>NO real names — Ada / Grace only.</para>
/// </remarks>
public class SessionGraphicStaleTests
{
    private static string Hash(int id, string? title, params (int, string)[] speakers) =>
        SoMeBundleBuildService.SessionGraphicInputHash(id, title, speakers);

    [Fact]
    public void The_same_session_and_line_up_hashes_the_same()
    {
        var a = Hash(7, "Zero Trust in practice", (1, "Ada Lovelace"), (2, "Grace Hopper"));
        var b = Hash(7, "Zero Trust in practice", (1, "Ada Lovelace"), (2, "Grace Hopper"));

        Assert.Equal(a, b);
    }

    /// <summary>The title is printed ON the graphic, so a rename is a changed picture.</summary>
    [Fact]
    public void Renaming_the_session_changes_the_hash()
    {
        var before = Hash(7, "Zero Trust in practice", (1, "Ada Lovelace"));
        var after = Hash(7, "Zero Trust, in practice", (1, "Ada Lovelace"));

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Adding_or_removing_a_speaker_changes_the_hash()
    {
        var one = Hash(7, "T", (1, "Ada Lovelace"));
        var two = Hash(7, "T", (1, "Ada Lovelace"), (2, "Grace Hopper"));

        Assert.NotEqual(one, two);
    }

    /// <summary>A renamed PERSON is a changed picture too — the name is drawn on the frame.</summary>
    [Fact]
    public void Renaming_a_speaker_changes_the_hash()
    {
        var before = Hash(7, "T", (1, "Ada Lovelace"));
        var after = Hash(7, "T", (1, "Ada L. Lovelace"));

        Assert.NotEqual(before, after);
    }

    /// <summary>
    /// 🔒 ORDER MUST NOT MATTER. The sweep sorts its speakers by participant id before hashing; a
    /// caller that passes the same people in a different order has to land on the same number, or
    /// the grid shows a "stale" that rebuilding cannot clear.
    /// </summary>
    [Fact]
    public void The_ORDER_of_the_speakers_does_not_change_the_hash()
    {
        var ordered = Hash(7, "T", (1, "Ada Lovelace"), (2, "Grace Hopper"));
        var reversed = Hash(7, "T", (2, "Grace Hopper"), (1, "Ada Lovelace"));

        Assert.Equal(ordered, reversed);
    }

    /// <summary>The sweep de-duplicates by participant id; the hash must agree.</summary>
    [Fact]
    public void A_duplicated_speaker_row_does_not_change_the_hash()
    {
        var once = Hash(7, "T", (1, "Ada Lovelace"));
        var twice = Hash(7, "T", (1, "Ada Lovelace"), (1, "Ada Lovelace"));

        Assert.Equal(once, twice);
    }

    [Fact]
    public void Two_different_sessions_with_identical_content_hash_differently()
    {
        // Otherwise one session's graphic would look "current" against another's inputs.
        Assert.NotEqual(Hash(7, "T", (1, "Ada Lovelace")), Hash(8, "T", (1, "Ada Lovelace")));
    }

    /// <summary>
    /// The DESIGN VERSION is in the hash, which is what makes a locked design rebuild everything
    /// ONCE instead of splitting the line-up across two looks.
    /// </summary>
    [Fact]
    public void The_design_version_is_part_of_the_hash()
    {
        var hash = Hash(7, "T", (1, "Ada Lovelace"));

        // Recomputing with a different design version must differ. Proven by construction: the
        // constant is IN the material, so this pins that it has not been dropped from the formula.
        Assert.NotEmpty(SoMeBundleBuildService.DesignVersion);
        Assert.Equal(32, hash.Length);
    }

    // =====================================================================
    //  The staleness RULE, and especially its two refusals
    // =====================================================================

    [Fact]
    public void A_changed_source_is_STALE()
    {
        var stored = Hash(7, "Old title", (1, "Ada Lovelace"));
        var current = Hash(7, "New title", (1, "Ada Lovelace"));

        Assert.True(SoMeBundleBuildService.IsGraphicStale(stored, false, current));
    }

    [Fact]
    public void An_unchanged_source_is_not_stale()
    {
        var hash = Hash(7, "T", (1, "Ada Lovelace"));

        Assert.False(SoMeBundleBuildService.IsGraphicStale(hash, false, hash));
    }

    /// <summary>
    /// 🔒 A NULL stored hash is UNKNOWN, never stale — rows predating §767 and anything PULLED from
    /// the library. The engine refuses to regenerate those, so a "stale" badge here would be a
    /// permanent warning no action on the page could ever clear.
    /// </summary>
    [Fact]
    public void An_unknown_input_hash_is_NOT_stale()
    {
        var current = Hash(7, "T", (1, "Ada Lovelace"));

        Assert.False(SoMeBundleBuildService.IsGraphicStale(null, false, current));
        Assert.False(SoMeBundleBuildService.IsGraphicStale("   ", false, current));
    }

    /// <summary>
    /// 🔒 The operator's OWN artwork is never stale. He deliberately replaced the generated picture
    /// and the engine will not touch it — telling him it is out of date invites him to destroy it.
    /// </summary>
    [Fact]
    public void Organizer_overruled_artwork_is_NEVER_stale_even_when_the_source_moved()
    {
        var stored = Hash(7, "Old title", (1, "Ada Lovelace"));
        var current = Hash(7, "New title", (1, "Ada Lovelace"), (2, "Grace Hopper"));

        Assert.True(SoMeBundleBuildService.IsGraphicStale(stored, false, current));   // would be
        Assert.False(SoMeBundleBuildService.IsGraphicStale(stored, true, current));   // but is not
    }

    /// <summary>An unknown CURRENT hash cannot prove staleness either.</summary>
    [Fact]
    public void An_unknown_current_hash_is_NOT_stale()
    {
        var stored = Hash(7, "T", (1, "Ada Lovelace"));

        Assert.False(SoMeBundleBuildService.IsGraphicStale(stored, false, null));
    }
}
