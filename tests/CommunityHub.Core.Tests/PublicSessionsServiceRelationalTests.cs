using CommunityHub.Core.Attendees;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// RELATIONAL regression tests for the PUBLIC sessions overview read service
/// (<see cref="PublicSessionsService"/>), guarding the prod 500 that hit
/// <c>/Sessions</c> and <c>/Attendee/MyEvent</c>.
///
/// ROOT CAUSE: <see cref="PublicSessionsService.BuildAsync"/> projected each
/// session's speakers with a server-side nested
/// <c>s.SessionSpeakers.Select(...).OrderBy(...).ToList()</c> whose element carried a
/// correlated <c>_db.SpeakerProfiles.Any(...)</c> "is-published" subquery. The EF
/// in-memory provider tolerates that shape, but every RELATIONAL provider (SQL
/// Server in prod, SQLite here) shares one query-translation pipeline and throws
/// <c>could not be translated</c> at runtime — a 500 on both pages.
///
/// These tests run against the EF <b>SQLite</b> provider, which uses the SAME
/// relational translation pipeline as SQL Server, so the untranslatable shape is
/// reproduced offline (no SQLEXPRESS / secrets). They FAIL on the old query shape
/// and PASS once the speaker projection materializes the un-translatable part
/// client-side. Synthetic ids + example.test only — no real data.
/// </summary>
public sealed class PublicSessionsServiceRelationalTests : IDisposable
{
    private static readonly DateTimeOffset Day1 =
        new(2027, 2, 9, 9, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _conn;

    public PublicSessionsServiceRelationalTests()
    {
        // A shared in-memory SQLite db: the connection must stay open for the
        // lifetime of the test so the schema/data persist across DbContexts.
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
    }

    public void Dispose() => _conn.Dispose();

    private CommunityHubDbContext NewRelationalDb()
    {
        var options = new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseSqlite(_conn)
            .EnableSensitiveDataLogging()
            .Options;
        var db = new CommunityHubDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    /// <summary>
    /// Same representative seed as the in-memory suite: an active edition with three
    /// speakers, a master class (slug + token), a two-speaker tech session, a sponsor
    /// session, and a service session (excluded). Publishes Alice so the
    /// is-published gate has a true and a false branch to translate.
    /// </summary>
    private async Task<int> SeedAsync(CommunityHubDbContext db)
    {
        var evt = new Event
        {
            Code = "PUB27",
            CommunityName = "Public Community",
            DisplayName = "Public Community 2027",
            StartDate = new DateOnly(2027, 2, 9),
            EndDate = new DateOnly(2027, 2, 10),
            IsActive = true,
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        Participant Spk(string name, string email)
        {
            var p = new Participant
            {
                EventId = evt.Id, FullName = name, Email = email,
                Role = ParticipantRole.Speaker, IsActive = true,
            };
            db.Participants.Add(p);
            return p;
        }

        var alice = Spk("Alice Adams", "alice@example.test");
        var bob = Spk("Bob Brown", "bob@example.test");
        var carol = Spk("Carol Clark", "carol@example.test");
        await db.SaveChangesAsync();

        // Publish ONLY Alice → the is-published gate must yield true for Alice and
        // false for Bob/Carol on the same projection.
        db.SpeakerProfiles.Add(new SpeakerProfile
        { EventId = evt.Id, ParticipantId = alice.Id, SelectedForPublish = true });
        await db.SaveChangesAsync();

        Session Sess(string id, string title, SessionType type, SessionLength len,
            string? room, DateTimeOffset? start, params Participant[] speakers)
        {
            var s = new Session
            {
                EventId = evt.Id, SessionizeId = id, Title = title,
                Abstract = $"About {title}.", Type = type, Length = len,
                Room = room, StartsAt = start,
                EndsAt = start?.AddMinutes((int)len == 0 ? 480 : (int)len),
            };
            foreach (var sp in speakers)
                s.SessionSpeakers.Add(new SessionSpeaker { Session = s, Participant = sp });
            db.Sessions.Add(s);
            return s;
        }

        var mc = Sess("sess-mc", "Kubernetes Workshop",
            SessionType.CommunityMasterClass, SessionLength.FullDay, "Room A", Day1, alice);
        mc.PublicSlug = "mc-slug-123";
        mc.PublicToken = "ask-token-mc";

        var tech = Sess("sess-tech", "Intro to Bicep",
            SessionType.CommunityTechSession, SessionLength.FiftyMin, "Room B",
            Day1.AddHours(2), alice, bob);
        tech.PublicToken = "ask-token-tech";

        Sess("sess-sponsor", "Sponsor Showcase",
            SessionType.SponsorSession, SessionLength.TwentyMin, "Expo",
            Day1.AddHours(3), carol);

        Sess("sess-break", "Coffee Break",
            SessionType.CommunityTechSession, SessionLength.TwentyMin, "Foyer",
            Day1.AddHours(1)).IsServiceSession = true;

        await db.SaveChangesAsync();
        return evt.Id;
    }

    /// <summary>
    /// THE REGRESSION: BuildAsync must run end-to-end on a relational provider
    /// without throwing a translation error, and return the same projection (rows,
    /// ordered speakers, per-speaker published gate, slug/token, room facet) the
    /// in-memory suite asserts. On the old query shape this throws
    /// <c>InvalidOperationException: ... could not be translated</c>.
    /// </summary>
    [Fact]
    public async Task BuildAsync_translates_on_relational_provider_and_keeps_projection()
    {
        using var db = NewRelationalDb();
        await SeedAsync(db);
        var svc = new PublicSessionsService(db);

        // Must not throw a relational translation error.
        var view = await svc.BuildAsync();

        Assert.NotNull(view);
        Assert.Equal("Public Community 2027", view!.EventDisplayName);
        Assert.Equal(3, view.TotalCount);              // service session excluded
        Assert.Equal(3, view.Sessions.Count);
        Assert.DoesNotContain(view.Sessions, s => s.Title == "Coffee Break");

        // Speaker linkage + stable name ordering survives the materialization.
        var tech = view.Sessions.Single(s => s.Title == "Intro to Bicep");
        Assert.Equal(new[] { "Alice Adams", "Bob Brown" },
            tech.Speakers.Select(sp => sp.Name).ToArray());

        // The correlated is-published gate is preserved exactly: Alice published,
        // Bob not — on the SAME row.
        Assert.True(tech.Speakers.Single(sp => sp.Name == "Alice Adams").IsPublished);
        Assert.False(tech.Speakers.Single(sp => sp.Name == "Bob Brown").IsPublished);

        // Master-class slug/token preserved; room facet intact.
        var mc = view.Sessions.Single(s => s.Title == "Kubernetes Workshop");
        Assert.Equal("mc-slug-123", mc.PublicSlug);
        Assert.Equal("ask-token-mc", mc.AskToken);
        Assert.Equal(new[] { "Expo", "Room A", "Room B" }, view.Rooms);
    }

    /// <summary>
    /// The Type/Length/Room/search filters still narrow correctly when the data
    /// comes from a relational round-trip (the filtering itself runs in memory, but
    /// this proves the materialized rows feeding it are correct).
    /// </summary>
    [Fact]
    public async Task BuildAsync_filters_work_on_relational_provider()
    {
        using var db = NewRelationalDb();
        await SeedAsync(db);
        var svc = new PublicSessionsService(db);

        var byType = await svc.BuildAsync(type: SessionType.SponsorSession);
        Assert.Single(byType!.Sessions);
        Assert.Equal("Sponsor Showcase", byType.Sessions[0].Title);

        var bySpeaker = await svc.BuildAsync(search: "carol");
        Assert.Single(bySpeaker!.Sessions);
        Assert.Equal("Sponsor Showcase", bySpeaker.Sessions[0].Title);
    }

    /// <summary>
    /// GetByIdAsync carries the SAME nested-speaker + is-published shape, so it must
    /// also translate on a relational provider. Guards the public session-detail
    /// page (<c>/Sessions/{id}</c>).
    /// </summary>
    [Fact]
    public async Task GetByIdAsync_translates_on_relational_provider()
    {
        using var db = NewRelationalDb();
        await SeedAsync(db);
        var svc = new PublicSessionsService(db);

        var techId = (await svc.BuildAsync())!.Sessions
            .Single(s => s.Title == "Intro to Bicep").Id;

        var detail = await svc.GetByIdAsync(techId);

        Assert.NotNull(detail);
        Assert.Equal("Intro to Bicep", detail!.Title);
        Assert.True(detail.Speakers.Single(s => s.Name == "Alice Adams").IsPublished);
        Assert.False(detail.Speakers.Single(s => s.Name == "Bob Brown").IsPublished);
    }

    /// <summary>
    /// The /Attendee/MyEvent path: the page feeds the SAME relational
    /// <see cref="PublicSessionsService.BuildAsync"/> projection into
    /// <see cref="MyEventScheduleBuilder"/>. This proves the end-to-end attendee
    /// schedule build does not throw on a relational provider and highlights the
    /// attendee's booked Master Class.
    /// </summary>
    [Fact]
    public async Task MyEvent_schedule_builds_from_relational_public_projection()
    {
        using var db = NewRelationalDb();
        await SeedAsync(db);
        var svc = new PublicSessionsService(db);

        var view = await svc.BuildAsync();
        Assert.NotNull(view);

        var attendee = new Attendee { MasterClassName = "Kubernetes Workshop" };
        var schedule = MyEventScheduleBuilder.Build(view!.Sessions, attendee);

        Assert.Equal(3, schedule.Agenda.Count);
        Assert.Single(schedule.MySessions);
        Assert.Equal("Kubernetes Workshop", schedule.MySessions[0].Title);
        Assert.True(schedule.MySessions[0].IsMine);
    }
}
