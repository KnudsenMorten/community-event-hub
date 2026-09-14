using CommunityHub.Core.Integrations.Graphics;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1132 — the SECOND, name-based copy of a speaker / volunteer photo.
///
/// <para>Operator 2026-08-25: <i>"when i need to find a photo file for a speaker and i dont know the
/// speaker i have to look their id first (=extra work) … i would love to have 1 extra file per
/// speaker on sharepoint with their name in … same folder, just 2 files"</i> · <i>"same for
/// volunteers that upload pics"</i>.</para>
///
/// <para>🔴 <b>The load-bearing tests are the two SAFETY ones at the bottom</b>, not the formatting.
/// The alias deliberately reuses the LEGACY <c>{prefix}{Name}-{id}</c> shape, and everything that
/// makes it safe follows from both parsers recognising it:</para>
/// <list type="bullet">
/// <item><b>The graphics builder must index it by ID, never by name</b> — a name key would beat the
/// real photo, because the name pass runs first. That is the §768.16 defect exactly.</item>
/// <item><b>Deactivation cleanup must delete it</b> — otherwise a photo of someone who left is
/// orphaned in SharePoint.</item>
/// </list>
/// </summary>
public sealed class PhotoNameAliasTests
{
    // ── Shape ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_speaker_alias_is_the_name_then_the_id()
    {
        Assert.Equal(
            "speaker-photo-Morten-Knudsen-73.jpg",
            SpeakerPhotoFileName.BuildAlias(73, "Morten Knudsen", ".jpg"));
    }

    [Fact]
    public void A_volunteer_alias_uses_the_same_rule()
    {
        Assert.Equal(
            "volunteer-photo-Morten-Knudsen-73.png",
            VolunteerPhotoFileName.BuildAlias(73, "Morten Knudsen", ".png"));
    }

    [Fact]
    public void The_id_file_is_unchanged()
    {
        // 🔒 He said "i dont want to break that". The authoritative file keeps its exact name.
        Assert.Equal("speaker-photo-73.jpg", SpeakerPhotoFileName.Build(73, ".jpg"));
        Assert.Equal("volunteer-photo-73.jpg", VolunteerPhotoFileName.Build(73, ".jpg"));
    }

    [Theory]
    [InlineData("Søren Kierkegaard", "speaker-photo-Søren-Kierkegaard-9.jpg")]  // Danish letters kept
    [InlineData("  Anne   Marie  ", "speaker-photo-Anne-Marie-9.jpg")]          // runs collapse
    [InlineData("Jean-Luc Picard", "speaker-photo-Jean-Luc-Picard-9.jpg")]      // existing dash kept single
    // SharePoint-illegal characters are REMOVED, not turned into separators: "A/B \ C:D" loses
    // / \ and : outright, leaving "AB  CD" → "AB-CD". Only real whitespace/dashes separate words.
    [InlineData("A/B \\ C:D", "speaker-photo-AB-CD-9.jpg")]
    public void Names_are_sanitised_without_losing_recognisability(string name, string expected) =>
        Assert.Equal(expected, SpeakerPhotoFileName.BuildAlias(9, name, ".jpg"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("///")]
    public void A_name_that_sanitises_to_nothing_writes_no_alias(string? name)
    {
        // Only the id file then exists — which is the pre-§1132 state, not a failure.
        Assert.Null(SpeakerPhotoFileName.BuildAlias(9, name, ".jpg"));
        Assert.Null(VolunteerPhotoFileName.BuildAlias(9, name, ".jpg"));
    }

    [Fact]
    public void Two_people_with_the_same_name_get_different_files()
    {
        // 🔒 The trailing id is the whole reason this is safe. `{Name}.jpg` alone would let one
        // person's photo silently overwrite another's — the defect §769.9 removed for volunteers.
        Assert.NotEqual(
            SpeakerPhotoFileName.BuildAlias(73, "Morten Knudsen", ".jpg"),
            SpeakerPhotoFileName.BuildAlias(74, "Morten Knudsen", ".jpg"));
    }

    // ── 🔴 SAFETY 1: the alias must never outrank the real photo ─────────────────────────

    [Fact]
    public void A_speaker_alias_parses_as_an_ARCHIVE_file_carrying_a_name()
    {
        // 🔑 SoMeBundleBuildService indexes anything TryParse accepts by ID ONLY and `continue`s
        // before the name-slug branch. So this returning TRUE — with a non-empty name part — is
        // exactly what stops the alias registering a name key and beating speaker-photo-73.jpg
        // through the name pass, which runs first.
        var alias = SpeakerPhotoFileName.BuildAlias(73, "Morten Knudsen", ".jpg")!;
        var bare = alias[..alias.LastIndexOf('.')];

        Assert.True(SpeakerPhotoFileName.TryParse(bare, out var id, out var namePart));
        Assert.Equal(73, id);
        Assert.Equal("Morten-Knudsen", namePart);
    }

    [Fact]
    public void The_id_file_still_parses_as_the_CURRENT_convention()
    {
        // An empty name part is what marks the authoritative file, and what makes it win the index.
        Assert.True(SpeakerPhotoFileName.TryParse("speaker-photo-73", out var id, out var namePart));
        Assert.Equal(73, id);
        Assert.Equal(string.Empty, namePart);
        Assert.True(SpeakerPhotoFileName.IsCurrentConvention("speaker-photo-73.jpg"));
    }

    [Fact]
    public void A_name_containing_digits_still_resolves_to_the_TRAILING_id()
    {
        // Real file from the folder: "speaker-photo-2linkit-speaker-firstname-lastname-100".
        Assert.True(SpeakerPhotoFileName.TryParse(
            "speaker-photo-2linkit-speaker-firstname-lastname-100", out var id, out _));
        Assert.Equal(100, id);
    }

    // ── 🔴 SAFETY 2: cleanup must delete the alias when someone is deactivated ───────────

    [Fact]
    public void A_volunteer_alias_is_recognised_by_id_so_cleanup_removes_it()
    {
        // 🔑 ParticipantPhotoCleanupService deletes a volunteer's photos by the id TryParse yields.
        // Without this branch the alias would survive the person's deactivation — a photo of
        // somebody who asked to be removed, left behind in SharePoint.
        var alias = VolunteerPhotoFileName.BuildAlias(73, "Morten Knudsen", ".jpg")!;

        Assert.True(VolunteerPhotoFileName.TryParse(alias, out var id));
        Assert.Equal(73, id);
    }

    [Fact]
    public void A_volunteer_alias_belongs_to_its_own_participant_only()
    {
        var alias = VolunteerPhotoFileName.BuildAlias(73, "Morten Knudsen", ".jpg")!;

        Assert.True(VolunteerPhotoFileName.Matches(alias, 73, "Morten Knudsen"));
        Assert.False(VolunteerPhotoFileName.Matches(alias, 74, "Morten Knudsen"));
    }

    [Fact]
    public void The_plain_id_volunteer_file_still_parses()
    {
        Assert.True(VolunteerPhotoFileName.TryParse("volunteer-photo-73.jpg", out var id));
        Assert.Equal(73, id);
    }

    [Theory]
    [InlineData("Morten Knudsen.jpg")]          // the LEGACY name-only file — must NOT parse as an id
    [InlineData("volunteer-photo-.jpg")]
    [InlineData("volunteer-photo-abc.jpg")]
    public void A_file_carrying_no_id_is_not_treated_as_id_named(string fileName)
    {
        // 🔒 A legacy {Full Name}.ext file must keep going down the AMBIGUOUS name branch, where
        // cleanup refuses to delete on a namesake collision. Parsing it as an id would delete an
        // active volunteer's photo — the one failure that cannot be re-run into correctness.
        Assert.False(VolunteerPhotoFileName.TryParse(fileName, out _));
    }
}
