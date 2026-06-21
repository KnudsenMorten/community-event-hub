using CommunityHub.Core.Domain;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for <see cref="ScheduleRoles"/> — the single source of truth for
/// mapping a schedule entry's role-keyword CSV to participant roles (used by the
/// Key-dates display, the iCal feed and the organizer editor, so visibility is
/// identical everywhere). Pure logic, no DB.
/// </summary>
public sealed class ScheduleRolesTests
{
    [Theory]
    [InlineData(ParticipantRole.Organizer, "organizer")]
    [InlineData(ParticipantRole.Volunteer, "volunteer")]
    [InlineData(ParticipantRole.Speaker, "speaker")]
    [InlineData(ParticipantRole.MasterclassSpeaker, "speaker")] // master-class folds into speaker
    [InlineData(ParticipantRole.Video, "media")]                // video crew  -> media
    [InlineData(ParticipantRole.Camera, "media")]               // photo crew  -> media
    [InlineData(ParticipantRole.Attendee, "attendee")]
    [InlineData(ParticipantRole.Sponsor, "sponsor")]
    public void ViewerKeyword_maps_each_role(ParticipantRole role, string expected) =>
        Assert.Equal(expected, ScheduleRoles.ViewerKeyword(role));

    [Theory]
    [InlineData("")]            // empty  = everyone
    [InlineData("  ")]          // blank  = everyone
    [InlineData("all")]         // all    = everyone
    [InlineData("speaker,all")] // 'all' anywhere wins
    public void Applies_shows_to_everyone_when_unscoped(string csv)
    {
        foreach (ParticipantRole r in System.Enum.GetValues(typeof(ParticipantRole)))
            Assert.True(ScheduleRoles.Applies(csv, r));
    }

    [Fact]
    public void Applies_filters_by_tagged_role()
    {
        // "Group photo" — all except sponsors.
        const string csv = "organizer,volunteer,speaker,media,attendee";
        Assert.True(ScheduleRoles.Applies(csv, ParticipantRole.Organizer));
        Assert.True(ScheduleRoles.Applies(csv, ParticipantRole.Camera));   // photo crew -> media
        Assert.False(ScheduleRoles.Applies(csv, ParticipantRole.Sponsor)); // excluded
    }

    [Fact]
    public void Applies_speaker_never_sees_a_move_in_tagged_for_crew_only()
    {
        const string csv = "organizer,volunteer"; // move-in day
        Assert.False(ScheduleRoles.Applies(csv, ParticipantRole.Speaker));
        Assert.False(ScheduleRoles.Applies(csv, ParticipantRole.MasterclassSpeaker));
        Assert.True(ScheduleRoles.Applies(csv, ParticipantRole.Volunteer));
    }

    [Fact]
    public void Applies_null_role_is_public_fallback_shows_everything()
    {
        // A non-authenticated / role-less viewer sees all entries.
        Assert.True(ScheduleRoles.Applies("organizer", null));
    }

    [Fact]
    public void Parse_dedupes_trims_and_lowercases()
    {
        var parsed = ScheduleRoles.Parse(" Speaker, MEDIA ,speaker, ");
        Assert.Equal(new[] { "speaker", "media" }, parsed);
    }

    [Fact]
    public void WhoLabel_reads_human_friendly()
    {
        Assert.Equal("everyone", ScheduleRoles.WhoLabel(""));
        Assert.Equal("everyone", ScheduleRoles.WhoLabel("all"));
        Assert.Equal("organizers + volunteers", ScheduleRoles.WhoLabel("organizer,volunteer"));
        Assert.Equal("media", ScheduleRoles.WhoLabel("media"));
    }
}
