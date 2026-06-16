using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Tests for the broadcast audience filter and the reusable-template token
/// substitution — the two pieces the organizer email center's broadcast relies
/// on. All pure and offline: no DB, no app, no SMTP. The page loads rows and
/// hands them to <see cref="BroadcastAudienceFilter.Resolve"/>, so testing the
/// filter directly proves the previewed-list-equals-sent-list contract.
/// </summary>
public class BroadcastAudienceFilterTests
{
    private static Participant P(
        string email, ParticipantRole role, bool active = true,
        bool test = false, string name = "Test Person") =>
        new()
        {
            Email = email,
            FullName = name,
            Role = role,
            IsActive = active,
            IsTestUser = test,
        };

    private static BroadcastAudienceOptions Opts(
        IEnumerable<ParticipantRole> roles,
        BroadcastStatusFilter status = BroadcastStatusFilter.ActiveOnly,
        bool excludeTest = true,
        bool includeAttendees = false) =>
        new()
        {
            Roles = roles.ToArray(),
            Status = status,
            ExcludeTestUsers = excludeTest,
            IncludeAttendees = includeAttendees,
        };

    // ----------------------------------------------------------------------
    // Role filtering
    // ----------------------------------------------------------------------

    [Fact]
    public void Only_selected_roles_are_included()
    {
        var people = new[]
        {
            P("speaker@example.test", ParticipantRole.Speaker),
            P("sponsor@example.test", ParticipantRole.Sponsor),
            P("volunteer@example.test", ParticipantRole.Volunteer),
        };

        var result = BroadcastAudienceFilter.Resolve(
            Opts(new[] { ParticipantRole.Speaker, ParticipantRole.Volunteer }), people);

        Assert.Equal(
            new[] { "speaker@example.test", "volunteer@example.test" },
            result.Select(r => r.Email));
    }

    [Fact]
    public void No_roles_and_no_attendees_yields_no_recipients()
    {
        var people = new[] { P("speaker@example.test", ParticipantRole.Speaker) };
        var result = BroadcastAudienceFilter.Resolve(
            Opts(Array.Empty<ParticipantRole>()), people);
        Assert.Empty(result);
    }

    // ----------------------------------------------------------------------
    // IsTestUser exclusion
    // ----------------------------------------------------------------------

    [Fact]
    public void Test_users_are_excluded_by_default()
    {
        var people = new[]
        {
            P("real@example.test", ParticipantRole.Speaker, test: false),
            P("synthetic@example.test", ParticipantRole.Speaker, test: true),
        };

        var result = BroadcastAudienceFilter.Resolve(
            Opts(new[] { ParticipantRole.Speaker }, excludeTest: true), people);

        var only = Assert.Single(result);
        Assert.Equal("real@example.test", only.Email);
    }

    [Fact]
    public void Test_users_are_included_when_exclusion_is_turned_off()
    {
        var people = new[]
        {
            P("real@example.test", ParticipantRole.Speaker, test: false),
            P("synthetic@example.test", ParticipantRole.Speaker, test: true),
        };

        var result = BroadcastAudienceFilter.Resolve(
            Opts(new[] { ParticipantRole.Speaker }, excludeTest: false), people);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.Email == "synthetic@example.test" && r.IsTestUser);
    }

    // ----------------------------------------------------------------------
    // Status filtering
    // ----------------------------------------------------------------------

    [Theory]
    [InlineData(BroadcastStatusFilter.ActiveOnly, "active@example.test")]
    [InlineData(BroadcastStatusFilter.InactiveOnly, "inactive@example.test")]
    public void Status_filters_to_the_chosen_activity(
        BroadcastStatusFilter status, string expectedEmail)
    {
        var people = new[]
        {
            P("active@example.test", ParticipantRole.Volunteer, active: true),
            P("inactive@example.test", ParticipantRole.Volunteer, active: false),
        };

        var result = BroadcastAudienceFilter.Resolve(
            Opts(new[] { ParticipantRole.Volunteer }, status: status), people);

        var only = Assert.Single(result);
        Assert.Equal(expectedEmail, only.Email);
    }

    [Fact]
    public void Status_all_includes_both_active_and_inactive()
    {
        var people = new[]
        {
            P("active@example.test", ParticipantRole.Volunteer, active: true),
            P("inactive@example.test", ParticipantRole.Volunteer, active: false),
        };

        var result = BroadcastAudienceFilter.Resolve(
            Opts(new[] { ParticipantRole.Volunteer }, status: BroadcastStatusFilter.All),
            people);

        Assert.Equal(2, result.Count);
    }

    // ----------------------------------------------------------------------
    // Attendees + dedup
    // ----------------------------------------------------------------------

    [Fact]
    public void Attendees_are_included_only_when_requested()
    {
        var attendees = new[]
        {
            new Attendee { Email = "attendee@example.test", FirstName = "Curious" },
        };

        var without = BroadcastAudienceFilter.Resolve(
            Opts(Array.Empty<ParticipantRole>(), includeAttendees: false),
            Array.Empty<Participant>(), attendees);
        Assert.Empty(without);

        var with = BroadcastAudienceFilter.Resolve(
            Opts(Array.Empty<ParticipantRole>(), includeAttendees: true),
            Array.Empty<Participant>(), attendees);
        var only = Assert.Single(with);
        Assert.Equal("attendee@example.test", only.Email);
        Assert.Null(only.Role);            // attendees carry no participant role
    }

    [Fact]
    public void Recipients_are_deduplicated_by_email_case_insensitively()
    {
        var people = new[]
        {
            P("Person@Example.test", ParticipantRole.Speaker),
        };
        var attendees = new[]
        {
            new Attendee { Email = "person@example.test", FirstName = "Dup" },
        };

        var result = BroadcastAudienceFilter.Resolve(
            Opts(new[] { ParticipantRole.Speaker }, includeAttendees: true),
            people, attendees);

        var only = Assert.Single(result);
        // The participant wins over the attendee with the same address.
        Assert.Equal(ParticipantRole.Speaker, only.Role);
    }

    [Fact]
    public void Blank_emails_are_dropped()
    {
        var people = new[]
        {
            P("", ParticipantRole.Speaker),
            P("   ", ParticipantRole.Speaker),
            P("ok@example.test", ParticipantRole.Speaker),
        };

        var result = BroadcastAudienceFilter.Resolve(
            Opts(new[] { ParticipantRole.Speaker }), people);

        var only = Assert.Single(result);
        Assert.Equal("ok@example.test", only.Email);
    }

    [Fact]
    public void First_name_falls_back_to_there_when_full_name_is_blank()
    {
        var people = new[] { P("x@example.test", ParticipantRole.Speaker, name: "   ") };
        var result = BroadcastAudienceFilter.Resolve(
            Opts(new[] { ParticipantRole.Speaker }), people);
        Assert.Equal("there", Assert.Single(result).FirstName);
    }
}
