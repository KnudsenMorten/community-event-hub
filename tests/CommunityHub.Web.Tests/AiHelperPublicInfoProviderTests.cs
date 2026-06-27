using CommunityHub.Assistant;
using CommunityHub.Core.Assistant;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §149 (operator 2026-06-27): the AI Community Helper grounds PUBLIC speakers/sessions +
/// the event schedule/key times for EVERY role, so anyone can ask "who is speaking about
/// X?" or "when does lunch start?". These tests drive the real
/// <see cref="WebAiHelperPublicInfoProvider"/> (published-speaker HARD GATE, schedule not
/// role-filtered) and prove <see cref="AiHelperGroundingBuilder"/> adds the public sections
/// for a NON-organizer role (i.e. all roles). FAKE names only.
/// </summary>
public sealed class AiHelperPublicInfoProviderTests
{
    private static readonly DateTimeOffset Day1 = new(2027, 2, 9, 8, 0, 0, TimeSpan.Zero);

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"aihelp-pub-{Guid.NewGuid():N}")
            .Options);

    private static async Task<int> SeedEventAsync(CommunityHubDbContext db)
    {
        var evt = new Event
        {
            Code = "PUB27", CommunityName = "C", DisplayName = "Public 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();
        return evt.Id;
    }

    private static async Task<Participant> SpeakerAsync(
        CommunityHubDbContext db, int eventId, string name, string email,
        bool selected, string? tagline, bool active = true)
    {
        var p = new Participant
        {
            EventId = eventId, FullName = name, Email = email,
            Role = ParticipantRole.Speaker, IsActive = active,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        db.SpeakerProfiles.Add(new SpeakerProfile
        {
            EventId = eventId, ParticipantId = p.Id, SelectedForPublish = selected, Tagline = tagline,
        });
        await db.SaveChangesAsync();
        return p;
    }

    private static async Task<Session> SessionAsync(
        CommunityHubDbContext db, int eventId, string title, DateTimeOffset? startsAt,
        string? room, bool service = false, Participant? speaker = null)
    {
        var s = new Session
        {
            EventId = eventId, SessionizeId = $"sz-{Guid.NewGuid():N}", Title = title,
            StartsAt = startsAt, Room = room, IsServiceSession = service,
            Type = service ? SessionType.Other : SessionType.TechnicalSession,
        };
        db.Sessions.Add(s);
        await db.SaveChangesAsync();
        if (speaker is not null)
        {
            db.SessionSpeakers.Add(new SessionSpeaker { SessionId = s.Id, ParticipantId = speaker.Id });
            await db.SaveChangesAsync();
        }
        return s;
    }

    [Fact]
    public async Task Includes_published_speaker_skills_and_sessions_excludes_unpublished()
    {
        using var db = NewDb();
        var eventId = await SeedEventAsync(db);

        var pub = await SpeakerAsync(db, eventId, "Pat Public", "pat@example.test",
            selected: true, tagline: "Kubernetes & platform engineering");
        await SessionAsync(db, eventId, "Scaling Kubernetes", Day1.AddHours(2), "Hall A", speaker: pub);

        // Unselected speaker must NEVER leak (publish HARD GATE).
        var hidden = await SpeakerAsync(db, eventId, "Hidden Hanna", "hanna@example.test",
            selected: false, tagline: "secret stuff");
        await SessionAsync(db, eventId, "Hidden talk", Day1.AddHours(3), "Hall B", speaker: hidden);

        var sections = await new WebAiHelperPublicInfoProvider(db).GetPublicInfoAsync(eventId);
        var all = string.Join("\n", sections.Select(s => s.Heading + "\n" + s.Body));

        Assert.Contains("Pat Public", all);
        Assert.Contains("Kubernetes & platform engineering", all);     // skills
        Assert.Contains("Scaling Kubernetes", all);                    // their session
        Assert.DoesNotContain("Hidden Hanna", all);                    // gate
        Assert.DoesNotContain("Hidden talk", all);
    }

    [Fact]
    public async Task Includes_schedule_and_service_session_key_times_for_everyone()
    {
        using var db = NewDb();
        var eventId = await SeedEventAsync(db);

        db.ScheduleEntries.Add(new ScheduleEntry
        {
            EventId = eventId, Title = "Doors open", StartsAt = Day1, Roles = "all", Location = "Main entrance",
        });
        // A role-tagged entry must STILL surface (schedule is not role-filtered here).
        db.ScheduleEntries.Add(new ScheduleEntry
        {
            EventId = eventId, Title = "Appreciation Dinner", StartsAt = Day1.AddHours(11), Roles = "speaker",
        });
        await db.SaveChangesAsync();
        await SessionAsync(db, eventId, "Lunch", Day1.AddHours(4), "Foyer", service: true);

        var sections = await new WebAiHelperPublicInfoProvider(db).GetPublicInfoAsync(eventId);
        var schedule = Assert.Single(sections, s => s.Heading.Contains("schedule", StringComparison.OrdinalIgnoreCase));

        Assert.Contains("Doors open", schedule.Body);
        Assert.Contains("Lunch", schedule.Body);                       // service session time
        Assert.Contains("Appreciation Dinner", schedule.Body);         // role-tagged still shown
    }

    [Fact]
    public async Task GroundingBuilder_adds_public_info_for_a_non_organizer_role()
    {
        using var db = NewDb();
        var eventId = await SeedEventAsync(db);
        await SpeakerAsync(db, eventId, "Pat Public", "pat@example.test", selected: true, tagline: "Cloud");

        var builder = new AiHelperGroundingBuilder(
            new NoContent(), new NoOwnData(), organizerOps: null,
            publicInfo: new WebAiHelperPublicInfoProvider(db));

        // An ATTENDEE (lowest-privilege, non-organizer) still gets the public sections.
        var ctx = await builder.BuildAsync(eventId, participantId: 999, role: ParticipantRole.Attendee);

        Assert.Contains(ctx.Sections, s => s.Heading.Contains("Speakers", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Pat Public", ctx.ToGroundingText());
    }

    private sealed class NoContent : IAiHelperContentProvider
    {
        public string? GetContentMarkdown(string slug) => null;
    }

    private sealed class NoOwnData : IAiHelperOwnDataProvider
    {
        public Task<IReadOnlyList<AiHelperGroundingSection>> GetOwnDataAsync(
            int eventId, int participantId, ParticipantRole role, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AiHelperGroundingSection>>(Array.Empty<AiHelperGroundingSection>());
    }
}
