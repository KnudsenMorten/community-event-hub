using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §726 — the speaker welcome is split into three mails by <see cref="SpeakerCategory"/>.
/// </summary>
/// <remarks>
/// <para>Operator 2026-07-31: three types, because the header differs — a COMMUNITY speaker was
/// <i>selected</i> and is congratulated on it, while a sponsor-brought or hired guest speaker was
/// not, and congratulating them on a selection that never happened reads as a mistake.</para>
///
/// <para>🔑 <b>Three KEYS, one BODY.</b> The ring is per (mail × role), so three keys is what gives
/// each category its own ring — no new gating level was needed (the §707.11 precedent). They share
/// <c>welcome-speaker.html</c> because three copies of a long body is how copy drifts (§660/§719).
/// The transport picks the ring from <c>EmailContext.TemplateName</c>, not from the file rendered,
/// which is what makes one body with three identities work.</para>
/// </remarks>
public class SpeakerWelcomeCategoryVariantsTests
{
    [Theory]
    [InlineData(SpeakerCategory.Community, "welcome-speaker-community")]
    [InlineData(SpeakerCategory.Guest, "welcome-speaker-guest")]
    [InlineData(SpeakerCategory.Sponsor, "welcome-speaker-sponsor")]
    public void Each_category_routes_to_its_own_mail(SpeakerCategory category, string expected)
    {
        Assert.Equal(expected, WelcomeVariants.TemplateKeyFor(ParticipantRole.Speaker, category));
    }

    [Fact]
    public void A_speaker_with_no_category_falls_back_to_the_plain_speaker_welcome()
    {
        // Operator: a new speaker sits in the pending queue until ring + category are set and
        // approved, so this is a safety net. It fails to the mail that already exists rather than
        // inventing a category for someone.
        Assert.Equal("welcome-speaker", WelcomeVariants.TemplateKeyFor(ParticipantRole.Speaker, null));
    }

    [Fact]
    public void A_category_never_leaks_into_another_role()
    {
        // The category argument must be ignored for every non-speaker role — a sponsor CONTACT is
        // not a sponsor-brought SPEAKER, and they get different mail.
        Assert.Equal("welcome-sponsor",
            WelcomeVariants.TemplateKeyFor(ParticipantRole.Sponsor, SpeakerCategory.Community));
        Assert.Equal("welcome-volunteer",
            WelcomeVariants.TemplateKeyFor(ParticipantRole.Volunteer, SpeakerCategory.Guest));
        Assert.Null(WelcomeVariants.TemplateKeyFor(ParticipantRole.Organizer, SpeakerCategory.Community));
    }

    [Fact]
    public void All_three_variants_render_the_one_speaker_body()
    {
        foreach (var key in new[]
                 { "welcome-speaker-community", "welcome-speaker-guest", "welcome-speaker-sponsor" })
        {
            Assert.Equal("welcome-speaker", WelcomeVariants.TemplateFileKeyFor(key));
        }

        // A non-speaker key is left alone — it has its own file.
        Assert.Equal("welcome-sponsor", WelcomeVariants.TemplateFileKeyFor("welcome-sponsor"));
    }

    [Fact]
    public void Only_the_community_intro_congratulates_on_being_selected()
    {
        var community = WelcomeVariants.SpeakerIntroHtml(SpeakerCategory.Community, "Experts Live Denmark 2027");
        var guest = WelcomeVariants.SpeakerIntroHtml(SpeakerCategory.Guest, "Experts Live Denmark 2027");
        var sponsor = WelcomeVariants.SpeakerIntroHtml(SpeakerCategory.Sponsor, "Experts Live Denmark 2027");

        Assert.Contains("congratulations on being selected", community, StringComparison.Ordinal);
        Assert.DoesNotContain("congratulations", guest, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("congratulations", sponsor, StringComparison.OrdinalIgnoreCase);

        // Guest and sponsor share the neutral opening.
        Assert.Equal(guest, sponsor);
        Assert.Contains("thrilled to have you", guest, StringComparison.Ordinal);

        // The event name is interpolated HERE, not left as a nested {{token}} — the renderer
        // substitutes in ONE pass, so a token inside a token value would reach the reader as
        // literal braces.
        foreach (var intro in new[] { community, guest, sponsor })
        {
            Assert.Contains("Experts Live Denmark 2027", intro, StringComparison.Ordinal);
            Assert.DoesNotContain("{{", intro, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_three_variants_are_registered_so_each_gets_a_settings_row_and_a_ring()
    {
        // 🔒 A mail that is NOT registered cannot carry a ring: §705.2 fails it closed to the
        // innermost ring and BrevoEmailSender drops an unregistered identity outright.
        foreach (var key in new[]
                 { "welcome-speaker-community", "welcome-speaker-guest", "welcome-speaker-sponsor" })
        {
            Assert.True(EmailTemplateCatalog.Map.ContainsKey(key), $"{key} is not registered.");
            Assert.Equal(EmailAudience.Speaker, EmailTemplateCatalog.AudienceFor(key));
            Assert.Contains(ParticipantRole.Speaker, EmailTemplateCatalog.RecipientRolesFor(key));
            Assert.Contains(key, WelcomeVariants.AllTemplateKeys);
        }
    }
}
