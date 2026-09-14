using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 🔴 §917 — THE PICTURE IS RESOLVED LATE, like the words.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-06: <i>"the picture … takes the current list of speaker - and not stamp
/// them at planning time. so i dont have to worry about wrong speaker assigned"</i>.</para>
///
/// <para>§901 already gave the WORDS that property. §915 accidentally took it away from the PICTURE
/// by copying the file name onto the post when it was planned — and for a SESSION that name is not
/// stable: one speaker renders <c>session-12.png</c>, two render <c>session-12.gif</c>, because the
/// extension carries single-vs-multi (§767). So a second speaker joining left the post pointing at
/// artwork that no longer showed the line-up.</para>
/// </remarks>
public sealed class SoMeSubjectGraphicTests
{
    private const int EventId = 1;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"subj-gfx-{Guid.NewGuid():N}").Options);

    private static SoMePost Post(SoMeTemplateKind kind, string subjectKey, string? imageRef) => new()
    {
        EventId = EventId, TemplateKind = kind, SubjectKey = subjectKey, ImageRef = imageRef,
    };

    /// <summary>🔴 The exact failure: the session flips PNG→GIF and the stamped name goes stale.</summary>
    [Fact]
    public async Task A_session_that_gained_a_second_speaker_resolves_to_the_NEW_graphic()
    {
        using var db = NewDb();
        // The graphic as it stands now — rebuilt as a GIF when the second speaker joined.
        db.GraphicAssets.Add(new GraphicAsset
        {
            EventId = EventId, Status = GraphicAssetStatus.Released, Type = GraphicAssetType.Session, SessionId = 12,
            StableKey = "session:12", FileName = "session-12.gif",
            CreatedAt = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
        });
        await db.SaveChangesAsync();

        // The post was planned back when the session had ONE speaker.
        var post = Post(SoMeTemplateKind.Session, "session:12", "session-12.png");

        Assert.Equal("session-12.gif", await new SoMeSubjectGraphic(db).CurrentFileNameAsync(post));
    }

    /// <summary>Newest wins when a subject has been rendered more than once.</summary>
    [Fact]
    public async Task The_most_recent_graphic_for_a_subject_wins()
    {
        using var db = NewDb();
        db.GraphicAssets.AddRange(
            new GraphicAsset
            {
                EventId = EventId, Status = GraphicAssetStatus.Released, Type = GraphicAssetType.Sponsor, SponsorCompanyId = "co-1",
                StableKey = "sponsor:co-1", FileName = "sponsor-co-1-old.png",
                CreatedAt = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
            },
            new GraphicAsset
            {
                EventId = EventId, Status = GraphicAssetStatus.Released, Type = GraphicAssetType.Sponsor, SponsorCompanyId = "co-1",
                StableKey = "sponsor:co-1", FileName = "sponsor-co-1-new.png",
                CreatedAt = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
            });
        await db.SaveChangesAsync();

        var post = Post(SoMeTemplateKind.Sponsor, "sponsor:co-1", "sponsor-co-1-old.png");

        Assert.Equal("sponsor-co-1-new.png", await new SoMeSubjectGraphic(db).CurrentFileNameAsync(post));
    }

    /// <summary>⚠️ The track name→slug asymmetry, on a name that slugs lossily.</summary>
    [Fact]
    public async Task A_track_is_matched_by_slugging_its_display_name()
    {
        using var db = NewDb();
        db.GraphicAssets.Add(new GraphicAsset
        {
            EventId = EventId, Status = GraphicAssetStatus.Released, Type = GraphicAssetType.TrackBundle,
            StableKey = "track:ai-for-makers-copilot-agents",
            FileName = "track-ai-for-makers-copilot-agents.gif",
            CreatedAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
        });
        await db.SaveChangesAsync();

        var post = Post(SoMeTemplateKind.SpeakerTracks, "track:AI for Makers (Copilot & Agents)", null);

        Assert.Equal(
            "track-ai-for-makers-copilot-agents.gif",
            await new SoMeSubjectGraphic(db).CurrentFileNameAsync(post));
    }

    /// <summary>
    /// 🔒 Type 5 owns its OWN picture — the deck names the file (§828.7). Overriding it would
    /// discard a choice somebody made deliberately.
    /// </summary>
    [Fact]
    public async Task An_event_post_keeps_the_picture_its_deck_named()
    {
        using var db = NewDb();
        db.GraphicAssets.Add(new GraphicAsset
        {
            EventId = EventId, Status = GraphicAssetStatus.Released, Type = GraphicAssetType.Session, SessionId = 12,
            StableKey = "session:12", FileName = "session-12.gif",
            CreatedAt = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
        });
        await db.SaveChangesAsync();

        var post = Post(SoMeTemplateKind.EventPost, "event:early-bird", "early-bird.png");

        Assert.Null(await new SoMeSubjectGraphic(db).CurrentFileNameAsync(post));
        Assert.False(SoMeSubjectGraphic.IsSubjectOwned(post));
    }

    /// <summary>A subject with no graphic yet resolves to nothing — an ordinary state.</summary>
    [Fact]
    public async Task A_subject_with_no_graphic_resolves_to_null()
    {
        using var db = NewDb();
        await db.SaveChangesAsync();

        var post = Post(SoMeTemplateKind.SpeakerTracks, "track:Azure", null);

        Assert.Null(await new SoMeSubjectGraphic(db).CurrentFileNameAsync(post));
    }
}
