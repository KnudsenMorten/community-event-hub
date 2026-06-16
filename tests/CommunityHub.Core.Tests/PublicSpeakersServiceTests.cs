using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Tests for the PUBLIC speaker-lineup read service (<see cref="PublicSpeakersService"/>,
/// REQUIREMENTS § 6 — "never publish an unselected speaker"). The service backs the
/// anonymous <c>/Speakers</c> page. Proves:
///  - the HARD GATE: ONLY speakers with <c>SelectedForPublish == true</c> appear,
///  - the "lineup coming soon" empty state when NONE are selected (today's default),
///  - withdrawn (IsActive == false) and non-speaker rows never leak even if selected,
///  - photo + tagline + linked session titles surface; the monogram fallback initials,
///  - event scoping: another edition's selected speakers never leak,
///  - no active event → null.
///
/// In-memory DbContext; synthetic ids + example.test — no real names.
/// </summary>
public sealed class PublicSpeakersServiceTests
{
    private static readonly DateTimeOffset Day1 =
        new(2027, 2, 9, 9, 0, 0, TimeSpan.Zero);

    private static Event NewEvent(bool active, string code = "PUB27") => new()
    {
        Code = code, CommunityName = "Public Community",
        DisplayName = "Public Community 2027",
        StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        IsActive = active,
    };

    private static Participant Spk(
        CommunityHubDbContext db, int eventId, string name, string email,
        bool active = true, ParticipantRole role = ParticipantRole.Speaker)
    {
        var p = new Participant
        {
            EventId = eventId, FullName = name, Email = email,
            Role = role, IsActive = active,
        };
        db.Participants.Add(p);
        return p;
    }

    private static SpeakerProfile Profile(
        CommunityHubDbContext db, int eventId, Participant p,
        bool selected, string? tagline = null, string? photo = null)
    {
        var sp = new SpeakerProfile
        {
            EventId = eventId, Participant = p,
            SelectedForPublish = selected, Tagline = tagline, PhotoUrl = photo,
        };
        db.SpeakerProfiles.Add(sp);
        return sp;
    }

    [Fact]
    public async Task No_active_event_returns_null()
    {
        using var db = TestDb.New();
        var svc = new PublicSpeakersService(db);

        Assert.Null(await svc.BuildAsync());
    }

    [Fact]
    public async Task Empty_state_when_no_speaker_is_selected()
    {
        // The real "today" case: speakers exist with profiles, but NONE are
        // SelectedForPublish (the default). The page must render zero speakers.
        using var db = TestDb.New();
        var evt = NewEvent(active: true);
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        var a = Spk(db, evt.Id, "Alice Adams", "alice@example.test");
        var b = Spk(db, evt.Id, "Bob Brown", "bob@example.test");
        await db.SaveChangesAsync();
        Profile(db, evt.Id, a, selected: false);
        Profile(db, evt.Id, b, selected: false);
        await db.SaveChangesAsync();

        var svc = new PublicSpeakersService(db);
        var view = await svc.BuildAsync();

        Assert.NotNull(view);                       // active event → not null
        Assert.Equal("Public Community 2027", view!.EventDisplayName);
        Assert.Empty(view.Speakers);                // gate: nobody selected → empty
    }

    [Fact]
    public async Task Only_selected_speakers_appear()
    {
        using var db = TestDb.New();
        var evt = NewEvent(active: true);
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        var selected = Spk(db, evt.Id, "Selected Sam", "sam@example.test");
        var hidden = Spk(db, evt.Id, "Hidden Hanna", "hanna@example.test");
        await db.SaveChangesAsync();
        Profile(db, evt.Id, selected, selected: true, tagline: "Cloud nerd",
            photo: "https://cdn.example.test/sam.jpg");
        Profile(db, evt.Id, hidden, selected: false);
        await db.SaveChangesAsync();

        var svc = new PublicSpeakersService(db);
        var view = await svc.BuildAsync();

        var only = Assert.Single(view!.Speakers);
        Assert.Equal("Selected Sam", only.Name);
        Assert.Equal("Cloud nerd", only.Tagline);
        Assert.Equal("https://cdn.example.test/sam.jpg", only.PhotoUrl);
        Assert.DoesNotContain(view.Speakers, s => s.Name == "Hidden Hanna");
    }

    [Fact]
    public async Task Withdrawn_or_non_speaker_never_leaks_even_if_selected()
    {
        using var db = TestDb.New();
        var evt = NewEvent(active: true);
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        // Selected but withdrawn (IsActive == false) → must NOT appear.
        var withdrawn = Spk(db, evt.Id, "Withdrawn Will", "will@example.test", active: false);
        // Selected but not a speaker role (e.g. role changed) → must NOT appear.
        var organizer = Spk(db, evt.Id, "Org Olivia", "olivia@example.test",
            role: ParticipantRole.Organizer);
        // The one legitimate published speaker.
        var ok = Spk(db, evt.Id, "Good Grace", "grace@example.test");
        await db.SaveChangesAsync();
        Profile(db, evt.Id, withdrawn, selected: true);
        Profile(db, evt.Id, organizer, selected: true);
        Profile(db, evt.Id, ok, selected: true);
        await db.SaveChangesAsync();

        var svc = new PublicSpeakersService(db);
        var view = await svc.BuildAsync();

        var only = Assert.Single(view!.Speakers);
        Assert.Equal("Good Grace", only.Name);
    }

    [Fact]
    public async Task Linked_sessions_surface_in_order_excluding_service_sessions()
    {
        using var db = TestDb.New();
        var evt = NewEvent(active: true);
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        var sam = Spk(db, evt.Id, "Session Sam", "sam@example.test");
        await db.SaveChangesAsync();
        Profile(db, evt.Id, sam, selected: true);

        Session Sess(string id, string title, DateTimeOffset? start, bool service = false)
        {
            var s = new Session
            {
                EventId = evt.Id, SessionizeId = id, Title = title, StartsAt = start,
                IsServiceSession = service,
            };
            s.SessionSpeakers.Add(new SessionSpeaker { Session = s, Participant = sam });
            db.Sessions.Add(s);
            return s;
        }
        Sess("s2", "Second Talk", Day1.AddHours(2));
        Sess("s1", "First Talk", Day1);
        Sess("brk", "Coffee Break", Day1.AddHours(1), service: true); // excluded
        await db.SaveChangesAsync();

        var svc = new PublicSpeakersService(db);
        var view = await svc.BuildAsync();

        var only = Assert.Single(view!.Speakers);
        // Ordered by StartsAt; the service session is excluded.
        Assert.Equal(new[] { "First Talk", "Second Talk" },
            only.Sessions.Select(s => s.Title).ToArray());
    }

    [Fact]
    public async Task Initials_fallback_is_two_uppercase_letters()
    {
        using var db = TestDb.New();
        var evt = NewEvent(active: true);
        db.Events.Add(evt);
        await db.SaveChangesAsync();
        var p = Spk(db, evt.Id, "alice adams", "alice@example.test");
        await db.SaveChangesAsync();
        Profile(db, evt.Id, p, selected: true);  // no PhotoUrl → monogram path
        await db.SaveChangesAsync();

        var svc = new PublicSpeakersService(db);
        var view = await svc.BuildAsync();

        var only = Assert.Single(view!.Speakers);
        Assert.Null(only.PhotoUrl);
        Assert.Equal("AA", only.Initials);
    }

    [Fact]
    public async Task GetById_returns_detail_for_a_published_speaker()
    {
        using var db = TestDb.New();
        var evt = NewEvent(active: true);
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        var sam = Spk(db, evt.Id, "Detail Sam", "sam@example.test");
        await db.SaveChangesAsync();
        Profile(db, evt.Id, sam, selected: true, tagline: "Cloud nerd",
            photo: "https://cdn.example.test/sam.jpg");
        // A linked session for cross-linking back to the session-detail page.
        var s = new Session { EventId = evt.Id, SessionizeId = "s1", Title = "Sam's Talk" };
        s.SessionSpeakers.Add(new SessionSpeaker { Session = s, Participant = sam });
        db.Sessions.Add(s);
        await db.SaveChangesAsync();

        var detail = await new PublicSpeakersService(db).GetByIdAsync(sam.Id);

        Assert.NotNull(detail);
        Assert.Equal("Detail Sam", detail!.Name);
        Assert.Equal("Cloud nerd", detail.Tagline);
        Assert.Equal("https://cdn.example.test/sam.jpg", detail.PhotoUrl);
        var only = Assert.Single(detail.Sessions);
        Assert.Equal("Sam's Talk", only.Title);
        Assert.Equal(s.Id, only.SessionId);
    }

    [Fact]
    public async Task GetById_enforces_the_hard_gate()
    {
        using var db = TestDb.New();
        var evt = NewEvent(active: true);
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        var unselected = Spk(db, evt.Id, "Unselected Ulla", "ulla@example.test");
        var withdrawn = Spk(db, evt.Id, "Withdrawn Will", "will@example.test", active: false);
        var organizer = Spk(db, evt.Id, "Org Olivia", "olivia@example.test",
            role: ParticipantRole.Organizer);
        await db.SaveChangesAsync();
        Profile(db, evt.Id, unselected, selected: false);
        Profile(db, evt.Id, withdrawn, selected: true);   // selected but withdrawn
        Profile(db, evt.Id, organizer, selected: true);   // selected but not a speaker
        await db.SaveChangesAsync();

        var svc = new PublicSpeakersService(db);

        // None of these resolve — the gate keeps every unselectable id 404.
        Assert.Null(await svc.GetByIdAsync(unselected.Id));
        Assert.Null(await svc.GetByIdAsync(withdrawn.Id));
        Assert.Null(await svc.GetByIdAsync(organizer.Id));
        Assert.Null(await svc.GetByIdAsync(999999));   // unknown id
    }

    [Fact]
    public async Task Selected_speakers_from_another_edition_never_leak()
    {
        using var db = TestDb.New();
        var active = NewEvent(active: true);
        var other = NewEvent(active: false, code: "OLD26");
        other.DisplayName = "Old 2026";
        db.Events.AddRange(active, other);
        await db.SaveChangesAsync();

        var here = Spk(db, active.Id, "Active Anna", "anna@example.test");
        var there = Spk(db, other.Id, "Other Otto", "otto@example.test");
        await db.SaveChangesAsync();
        Profile(db, active.Id, here, selected: true);
        Profile(db, other.Id, there, selected: true);
        await db.SaveChangesAsync();

        var svc = new PublicSpeakersService(db);
        var view = await svc.BuildAsync();

        Assert.Equal("Public Community 2027", view!.EventDisplayName);
        var only = Assert.Single(view.Speakers);
        Assert.Equal("Active Anna", only.Name);
    }
}
