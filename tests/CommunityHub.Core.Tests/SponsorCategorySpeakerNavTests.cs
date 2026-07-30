using CommunityHub.Core.Domain;
using CommunityHub.Core.Navigation;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §457 (operator 2026-07-27: *"remove these menu-items for the speakercategory sponsor"*) — a
/// SPONSOR-category speaker does not run the ELDK speaker programme, so the programme's menu items
/// are hidden for them.
///
/// <para>The pairing matters more than either half: §458 also stops the "Help promote" TASK being
/// seeded and §461 hides the same actions on the My Sessions cards. Hiding a menu entry while the
/// task still e-mails reminders — for a page they can no longer reach — would be worse than leaving
/// both. These tests pin the menu half, and pin equally that everything NOT arrowed still shows:
/// a sponsor speaker still delivers a session, so their sessions, key dates, room A/V, evaluation
/// QR codes and ratings all stay.</para>
/// </summary>
public sealed class SponsorCategorySpeakerNavTests
{
    private static IReadOnlyList<string> LabelKeys(bool sponsorCategory) =>
        NavBuilder.Build(ParticipantRole.Speaker, speakerIsSponsorCategory: sponsorCategory)
            .Groups[0].Items
            // LabelKey for resx-driven items; Href identifies the content pages, which carry a
            // FallbackLabel from the page title rather than a key.
            .Select(i => i.LabelKey ?? i.Href)
            .ToList();

    /// <summary>The five the operator arrowed.</summary>
    [Fact]
    public void The_speaker_programme_items_are_hidden_for_a_sponsor_category_speaker()
    {
        var sponsor = LabelKeys(sponsorCategory: true);

        Assert.DoesNotContain("Nav.HelpPromote", sponsor);
        Assert.DoesNotContain("Nav.AttendeeSurveyResults", sponsor);
        // The two content pages are added by slug through AddSpeakerContent.
        Assert.DoesNotContain(sponsor, k => k.Contains("session-guidelines", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(sponsor, k => k.Contains("session-preview-final", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(sponsor, k => k.Contains("speaker-template", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The other half of the rule, and the easier one to get wrong: a sponsor speaker still
    /// DELIVERS a session, so everything about doing that stays.
    /// </summary>
    [Fact]
    public void Everything_the_operator_did_not_arrow_still_shows()
    {
        var sponsor = LabelKeys(sponsorCategory: true);

        Assert.Contains("Nav.MySessions", sponsor);
        Assert.Contains("Nav.AttendeeTelemetrySpeaker", sponsor);
        Assert.Contains("Nav.SpeakerEvalQr", sponsor);
        Assert.Contains("Nav.SpeakerEvaluations", sponsor);
        Assert.Contains("Nav.SlidesForSpeakers", sponsor);
    }

    /// <summary>
    /// A COMMUNITY / uncategorised speaker is untouched — the flag defaults false, so nobody is
    /// quietly stripped of their menu by this change.
    /// </summary>
    [Fact]
    public void A_non_sponsor_speaker_keeps_the_full_menu()
    {
        var normal = LabelKeys(sponsorCategory: false);

        Assert.Contains("Nav.HelpPromote", normal);
        Assert.Contains("Nav.AttendeeSurveyResults", normal);
        Assert.Contains(normal, k => k.Contains("session-guidelines", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(normal, k => k.Contains("speaker-template", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The hidden set must be a strict SUBSET removal — a sponsor speaker's menu is the normal one
    /// minus the programme items, never with something new or reordered appearing.
    /// </summary>
    [Fact]
    public void The_sponsor_menu_is_the_normal_menu_minus_the_programme_items()
    {
        var normal = LabelKeys(sponsorCategory: false);
        var sponsor = LabelKeys(sponsorCategory: true);

        Assert.True(sponsor.Count < normal.Count, "the sponsor menu should be strictly smaller");
        foreach (var item in sponsor)
        {
            Assert.Contains(item, normal);
        }
    }
}
