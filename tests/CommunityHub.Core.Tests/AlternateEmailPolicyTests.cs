using CommunityHub.Core.Email;
using CommunityHub.Core.Participants;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §422 — one address, one meaning.
///
/// <para><b>The bug this pins.</b> The operator added an alternate address in the speaker Get
/// Started wizard and then could not find it anywhere (2026-07-27: <i>"i just added an
/// alternative email … but i dont see it in in my hub profile"</i>, and on the tasks page
/// <i>"it appears as the alternative email didn't pick up here as well, as it still shows
/// primary email here"</i>). No save had failed. Three columns had grown for one human fact —
/// <c>SpeakerProfile.CalendarEmail</c> (wizard), <c>Participant.AlternateEmail</c> (profile,
/// read by no mail code at all), <c>Participant.SecondaryEmail</c> (the real CC, organizer-only)
/// — and each screen touched a different one.</para>
///
/// <para>The most misleading part was that the tasks banner offered "add an alternative email →",
/// linked to the profile, and then went on ignoring the address you added there. These tests fix
/// the resolution order so that cannot silently come apart again.</para>
/// </summary>
public sealed class AlternateEmailPolicyTests
{
    [Fact]
    public void The_participants_own_alternate_is_used_when_no_organizer_secondary_is_set()
    {
        // THE regression. Before §422 this returned nothing: the CC read SecondaryEmail only,
        // which no participant-facing screen can write — so setting your own alternate address
        // copied mail precisely nowhere, while the banner said it would.
        Assert.Equal("alt@x.dk", AlternateEmailPolicy.CcFor(secondaryEmail: null, alternateEmail: "alt@x.dk"));
    }

    [Fact]
    public void The_organizer_set_secondary_wins_when_both_are_present()
    {
        // Resolution, not union — deliberately. An organizer may have entered a shared or PA
        // inbox, and a participant editing their own profile must not silently displace it.
        Assert.Equal("pa@x.dk", AlternateEmailPolicy.CcFor("pa@x.dk", "alt@x.dk"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("   ", "  ")]
    public void No_address_means_no_cc(string? secondary, string? alternate)
    {
        Assert.Null(AlternateEmailPolicy.CcFor(secondary, alternate));
        Assert.Empty(AlternateEmailPolicy.CcListFor(secondary, alternate));
    }

    [Fact]
    public void Whitespace_is_trimmed_so_the_sender_never_receives_a_padded_address()
    {
        Assert.Equal("alt@x.dk", AlternateEmailPolicy.CcFor(null, "  alt@x.dk  "));
        Assert.Equal(new[] { "alt@x.dk" }, AlternateEmailPolicy.CcListFor("  alt@x.dk  ", null));
    }

    [Fact]
    public void Normalize_matches_what_a_sign_in_lookup_will_compare_against()
    {
        // The address doubles as a login identity, so what is STORED has to be what the lookup
        // normalises an entered address to — otherwise it is accepted here and never matches.
        Assert.Equal("alt@x.dk", AlternateEmailPolicy.Normalize("  ALT@X.dk "));
        Assert.Null(AlternateEmailPolicy.Normalize("   "));
        Assert.Null(AlternateEmailPolicy.Normalize(null));
    }

    [Fact]
    public void Participant_mail_routing_copies_the_participants_own_alternate()
    {
        // The same rule, through the seam every participant-addressed template goes out on.
        var (to, cc) = ParticipantEmailService.ResolveRouting(
            identityEmail: "me@x.dk", speakerOverride: null,
            secondaryEmail: null, alternateEmail: "alt@x.dk");

        Assert.Equal("me@x.dk", to);
        Assert.Equal(new[] { "alt@x.dk" }, cc);
    }

    [Fact]
    public void An_alternate_address_never_becomes_the_To_address()
    {
        // It is additive. Only the speaker ContactEmailOverride moves the To — an alternate
        // address silently REPLACING someone's primary would be a different, worse bug.
        var (to, cc) = ParticipantEmailService.ResolveRouting(
            identityEmail: "me@x.dk", speakerOverride: "speaker@x.dk",
            secondaryEmail: null, alternateEmail: "alt@x.dk");

        Assert.Equal("speaker@x.dk", to);
        Assert.Equal(new[] { "alt@x.dk" }, cc);
    }
}
