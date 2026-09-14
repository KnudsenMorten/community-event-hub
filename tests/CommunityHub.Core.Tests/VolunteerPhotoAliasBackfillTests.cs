using CommunityHub.Core.Integrations.Graphics;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1145 — the pre-§1132 volunteers who only ever got one file.
///
/// <para>Operator 2026-08-28: <i>"the volunteers that signed up before that change only have one
/// file with id. do we have a service that fixes that"</i>.</para>
///
/// <para>🔴 <b>The shape of the oversight.</b> §1132 introduced the
/// <c>volunteer-photo-{Name}-{id}</c> alias but wrote it only at UPLOAD time, while the speaker side
/// got a <c>HasAlias</c> back-fill that heals the whole roster every sweep. So a convention
/// introduced on 2026-08-25 applied only to people who signed up after it.</para>
///
/// <para>NO real names — Ada / Grace only.</para>
/// </summary>
public class VolunteerPhotoAliasBackfillTests
{
    private static IReadOnlyList<int> Missing(
        string[] files, params (int Id, string? Name)[] people) =>
        VolunteerPhotoAliasBackfillService.ParticipantsMissingAlias(
            files, people.ToDictionary(p => p.Id, p => p.Name));

    [Fact]
    public void An_ID_ONLY_photo_is_reported_as_missing_its_alias()
    {
        // The live folder on 2026-08-28: 209 has only the id file while its neighbours have both.
        var missing = Missing(
            new[]
            {
                "volunteer-photo-209.jpg",
                "volunteer-photo-217.jpeg", "volunteer-photo-Ada-Lovelace-217.jpeg",
            },
            (209, "Grace Hopper"), (217, "Ada Lovelace"));

        Assert.Single(missing);
        Assert.Equal(209, missing[0]);
    }

    [Fact]
    public void A_volunteer_that_already_has_both_files_is_left_alone()
    {
        var missing = Missing(
            new[] { "volunteer-photo-217.jpeg", "volunteer-photo-Ada-Lovelace-217.jpeg" },
            (217, "Ada Lovelace"));

        Assert.Empty(missing);
    }

    /// <summary>
    /// 🔑 THE ONE THAT STOPS THE SWEEP RUNNING FOR EVER.
    /// </summary>
    /// <remarks>
    /// The alias carries whatever extension the volunteer uploaded (.jpg / .jpeg / .png), so
    /// comparing against a guessed extension reports every alias missing and re-copies the entire
    /// folder on every tick — the mistake <see cref="SpeakerPhotoArchiveService"/> already had to
    /// correct. Matching on the stem is what makes the sweep converge.
    /// </remarks>
    [Fact]
    public void The_alias_is_matched_WITHOUT_its_extension()
    {
        var missing = Missing(
            new[] { "volunteer-photo-220.png", "volunteer-photo-Ada-Lovelace-220.jpeg" },
            (220, "Ada Lovelace"));

        Assert.Empty(missing);
    }

    [Fact]
    public void A_volunteer_whose_name_sanitises_to_nothing_is_skipped()
    {
        // There is no alias to write, so it must not be reported for ever.
        var missing = Missing(new[] { "volunteer-photo-300.jpg" }, (300, "   "));
        Assert.Empty(missing);

        var alsoMissing = Missing(new[] { "volunteer-photo-301.jpg" }, (301, null));
        Assert.Empty(alsoMissing);
    }

    [Fact]
    public void A_photo_whose_participant_is_not_in_this_edition_is_ignored()
    {
        // 🔒 The folder is shared; a row we cannot name is not ours to copy.
        var missing = Missing(new[] { "volunteer-photo-999.jpg" }, (209, "Grace Hopper"));
        Assert.Empty(missing);
    }

    [Fact]
    public void Files_that_are_not_volunteer_photos_are_ignored()
    {
        var missing = Missing(
            new[] { "speaker-photo-209.jpg", "notes.txt", "volunteer-photo-209.jpg" },
            (209, "Grace Hopper"));

        Assert.Single(missing);
        Assert.Equal(209, missing[0]);
    }

    [Fact]
    public void Two_volunteers_sharing_a_name_keep_separate_aliases()
    {
        // 🔒 §1132's trailing id is what prevents one overwriting the other, so BOTH are still
        // reported as missing rather than one being satisfied by the other's file.
        var missing = Missing(
            new[] { "volunteer-photo-401.jpg", "volunteer-photo-402.jpg" },
            (401, "Ada Lovelace"), (402, "Ada Lovelace"));

        Assert.Equal(2, missing.Count);
        Assert.Contains(401, missing);
        Assert.Contains(402, missing);
    }

    [Fact]
    public void An_empty_folder_asks_for_nothing()
    {
        Assert.Empty(Missing(Array.Empty<string>(), (209, "Grace Hopper")));
    }
}
