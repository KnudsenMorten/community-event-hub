using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §889 — the column that makes a list of posts navigable.
/// </summary>
/// <remarks>
/// 🔑 <b>A body snippet is not enough on its own.</b> Types 1–4 all render from the shared template,
/// so sixteen track posts have sixteen identical snippets — the SUBJECT is the only thing that tells
/// them apart. Operator 2026-08-06: <i>"i cannot go through 72 not approved every time to find
/// number 33"</i>.
/// </remarks>
public sealed class SoMeSubjectLabellerTests
{
    private const int EventId = 1;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"some-subj-{Guid.NewGuid():N}").Options);

    private static SoMePost Post(int id, SoMeTemplateKind kind, string subjectKey) => new()
    {
        Id = id, EventId = EventId, TemplateKind = kind, SubjectKey = subjectKey,
    };

    [Fact]
    public async Task Each_type_is_labelled_with_words_a_person_recognises()
    {
        using var db = NewDb();
        db.Sessions.Add(new Session
        {
            Id = 50, EventId = EventId, SessionizeId = "s50",
            Title = "Persistence for fun & profit", Type = SessionType.TechnicalSession,
        });
        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = EventId, SponsorCompanyId = "co-12", CompanyName = "Robopack",
        });
        db.EventSoMePosts.Add(new EventSoMePost
        {
            EventId = EventId, Slug = "eldk27-early-bird-500",
            Title = "Early bird — 500 tickets", Body = "…",
        });
        await db.SaveChangesAsync();

        var posts = new[]
        {
            Post(1, SoMeTemplateKind.SpeakerTracks, "track:Azure"),
            Post(2, SoMeTemplateKind.Session, "session:50"),
            Post(3, SoMeTemplateKind.SponsorCategory, "tier:Gold"),
            Post(4, SoMeTemplateKind.Sponsor, "sponsor:co-12"),
            Post(5, SoMeTemplateKind.EventPost, "event:eldk27-early-bird-500"),
        };

        var labels = await new SoMeSubjectLabeller(db).LabelsForAsync(EventId, posts);

        Assert.Equal("Azure", labels[1]);
        Assert.Equal("Persistence for fun & profit", labels[2]);
        Assert.Equal("Gold sponsors", labels[3]);
        Assert.Equal("Robopack", labels[4]);
        Assert.Equal("Early bird — 500 tickets", labels[5]);
    }

    /// <summary>
    /// ⚠️ A subject that cannot be resolved falls back to the KEY, never to an empty cell — a blank
    /// column reads as "this post is about nothing", which is worse than an ugly key.
    /// </summary>
    [Fact]
    public async Task An_unresolvable_subject_falls_back_to_the_key()
    {
        using var db = NewDb();
        await db.SaveChangesAsync();

        var labels = await new SoMeSubjectLabeller(db)
            .LabelsForAsync(EventId, new[] { Post(9, SoMeTemplateKind.Session, "session:404") });

        Assert.Equal("session:404", labels[9]);
    }

    [Fact]
    public async Task An_empty_page_costs_no_queries_and_returns_nothing()
    {
        using var db = NewDb();

        Assert.Empty(await new SoMeSubjectLabeller(db).LabelsForAsync(EventId, Array.Empty<SoMePost>()));
    }
}
