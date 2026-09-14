using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1209 — "CAN YOU UPDATE THE TYPE COLUMN TO REFLECT THE 2a, 2b, 2c NAMES INSTEAD IF TYPE = 2".
///
/// <para>Operator 2026-09-12, reading the queue. 🔑 <b>One template kind, three schedules.</b>
/// §1187 split <c>SoMeTemplateKind.Session</c> into three CATEGORIES with three different rules —
/// master classes from 14 Sep, other technical sessions from 28 Sep, sponsor speaker sessions twice
/// and no later than two weeks out. A queue printing "Type 2" on all three cannot be read against
/// the settings page that governs the very dates he is looking at.</para>
///
/// <para>⚠️ The KIND alone cannot answer it — the SUBJECT decides. These pin that, including the
/// fallback, which must never guess the one category with its own earlier window.</para>
/// </summary>
public sealed class SoMeTypeLabelTests
{
    private static readonly IReadOnlySet<int> MasterClasses = new HashSet<int> { 46, 47 };

    [Theory]
    [InlineData(SoMeTemplateKind.SpeakerTracks, "track:Security", SoMeAnnouncementCategory.SpeakerTracks)]
    [InlineData(SoMeTemplateKind.SponsorCategory, "tier:Gold", SoMeAnnouncementCategory.SponsorTiers)]
    [InlineData(SoMeTemplateKind.Sponsor, "sponsor:co-1", SoMeAnnouncementCategory.Sponsors)]
    [InlineData(SoMeTemplateKind.EventPost, "event:levels", SoMeAnnouncementCategory.EventPosts)]
    public void Every_other_type_maps_to_its_own_category(
        SoMeTemplateKind kind, string key, SoMeAnnouncementCategory expected)
    {
        Assert.Equal(expected, SoMeCategoryRules.CategoryOf(kind, key, MasterClasses));
    }

    /// <summary>🔴 The three that shared one label.</summary>
    [Fact]
    public void A_master_class_post_is_2a()
    {
        Assert.Equal(
            SoMeAnnouncementCategory.MasterClasses,
            SoMeCategoryRules.CategoryOf(SoMeTemplateKind.Session, "session:46", MasterClasses));
    }

    [Fact]
    public void An_ordinary_session_post_is_2b()
    {
        Assert.Equal(
            SoMeAnnouncementCategory.TechnicalSessions,
            SoMeCategoryRules.CategoryOf(SoMeTemplateKind.Session, "session:999", MasterClasses));
    }

    /// <summary>⚠️ Not a Sessions row at all — its own key prefix is the only way to know.</summary>
    [Fact]
    public void A_sponsor_speaker_session_post_is_2c()
    {
        Assert.Equal(
            SoMeAnnouncementCategory.SponsorSpeakerSessions,
            SoMeCategoryRules.CategoryOf(
                SoMeTemplateKind.Session, SoMeSponsorSessionKey.For(12), MasterClasses));
    }

    /// <summary>
    /// 🔒 With no master-class list to hand, a session falls back to 2b. Guessing 2a would name the
    /// one category with its own earlier window and send him to the wrong rule.
    /// </summary>
    [Fact]
    public void Without_the_master_class_list_a_session_falls_back_to_2b()
    {
        Assert.Equal(
            SoMeAnnouncementCategory.TechnicalSessions,
            SoMeCategoryRules.CategoryOf(SoMeTemplateKind.Session, "session:46", null));
    }

    /// <summary>An ad-hoc post belongs to no category and must not claim one.</summary>
    [Fact]
    public void An_ad_hoc_post_has_no_category()
    {
        Assert.Null(SoMeCategoryRules.CategoryOf(null, null, MasterClasses));
    }

    /// <summary>🔑 The labels are the settings page's own, so the two screens cannot drift.</summary>
    [Fact]
    public void The_labels_are_the_ones_the_settings_page_uses()
    {
        Assert.StartsWith("Type 2a", SoMeCategoryRules.Label(SoMeAnnouncementCategory.MasterClasses),
            StringComparison.Ordinal);
        Assert.StartsWith("Type 2b",
            SoMeCategoryRules.Label(SoMeAnnouncementCategory.TechnicalSessions), StringComparison.Ordinal);
        Assert.StartsWith("Type 2c",
            SoMeCategoryRules.Label(SoMeAnnouncementCategory.SponsorSpeakerSessions),
            StringComparison.Ordinal);
    }
}
