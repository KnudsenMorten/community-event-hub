using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 🔴 §1025 — link and REMOVE speakers on a session from inside CEH.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-09: *"i am missing abiity to link/remove speakers inside ceh - both for
/// existing + new session creation"*. §1005.3 delivered Title, Abstract, Track, Level and Tags and
/// left Speakers behind — it is the many-to-many, and it was the one that mattered for a hub-added
/// sponsor session, which arrived with nobody on it.</para>
/// </remarks>
public sealed class SessionSpeakerLinkingTests
{
    private const int EventId = 1;

    private sealed class NoOpRoomQr : IRoomQrProvider
    {
        public bool CanProvision => false;
        public Task<RoomQr> EnsureRoomQrAsync(string e, string r, string u, CancellationToken ct = default)
            => Task.FromResult(new RoomQr(u, null));
    }

    private static SessionManagementService NewService(CommunityHubDbContext db) =>
        new(db, new NoOpRoomQr(), TimeProvider.System);

    private static async Task<int> SeedSpeakerAsync(CommunityHubDbContext db, string email)
    {
        var p = new Participant
        {
            EventId = EventId, Email = email, FullName = "S " + email,
            Role = ParticipantRole.Speaker, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    private static async Task<Session> SeedSessionAsync(CommunityHubDbContext db, bool hubAdded = true)
    {
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 10), EndDate = new DateOnly(2027, 2, 10),
        });
        var s = new Session
        {
            EventId = EventId, SessionizeId = hubAdded ? "hub-x" : "sz-1",
            Title = "Talk A", LengthMinutes = 60, IsHubAdded = hubAdded,
        };
        db.Sessions.Add(s);
        await db.SaveChangesAsync();
        return s;
    }

    private static Task<int> LinkCount(CommunityHubDbContext db, int sessionId) =>
        db.Set<SessionSpeaker>().CountAsync(x => x.SessionId == sessionId);

    [Fact]
    public async Task Speakers_can_be_linked_to_an_existing_session()
    {
        using var db = ScenarioFixture.NewDb();
        var s = await SeedSessionAsync(db);
        var a = await SeedSpeakerAsync(db, "a@x.dk");
        var b = await SeedSpeakerAsync(db, "b@x.dk");

        await NewService(db).UpdateSessionAsync(
            EventId, s.Id, SessionType.TechnicalSession, 60, null, null,
            speakerParticipantIds: new[] { a, b });

        Assert.Equal(2, await LinkCount(db, s.Id));
    }

    /// <summary>
    /// 🔴 THE HALF THAT WAS ACTUALLY MISSING. Linking is easy; REMOVING needs an empty list to be
    /// distinguishable from "not supplied" — unticked boxes post nothing, so without the page's
    /// hidden marker a cleared picker would silently mean "leave them alone" and a speaker could
    /// never be taken off.
    /// </summary>
    [Fact]
    public async Task An_empty_list_REMOVES_every_speaker()
    {
        using var db = ScenarioFixture.NewDb();
        var s = await SeedSessionAsync(db);
        var a = await SeedSpeakerAsync(db, "a@x.dk");
        await NewService(db).UpdateSessionAsync(
            EventId, s.Id, SessionType.TechnicalSession, 60, null, null,
            speakerParticipantIds: new[] { a });
        Assert.Equal(1, await LinkCount(db, s.Id));

        await NewService(db).UpdateSessionAsync(
            EventId, s.Id, SessionType.TechnicalSession, 60, null, null,
            speakerParticipantIds: Array.Empty<int>());

        Assert.Equal(0, await LinkCount(db, s.Id));
    }

    /// <summary>
    /// 🔒 NULL means "this form did not carry speakers" — every call site that predates §1025 passes
    /// nothing, and none of them may wipe a session's line-up as a side effect of editing a room.
    /// </summary>
    [Fact]
    public async Task Not_supplying_speakers_leaves_the_links_untouched()
    {
        using var db = ScenarioFixture.NewDb();
        var s = await SeedSessionAsync(db);
        var a = await SeedSpeakerAsync(db, "a@x.dk");
        await NewService(db).UpdateSessionAsync(
            EventId, s.Id, SessionType.TechnicalSession, 60, null, null,
            speakerParticipantIds: new[] { a });

        // The pre-§1025 call shape: type, length, room, eval URL — and nothing else.
        await NewService(db).UpdateSessionAsync(
            EventId, s.Id, SessionType.TechnicalSession, 60, "Hall B", null);

        Assert.Equal(1, await LinkCount(db, s.Id));
    }

    /// <summary>
    /// 🔒 A participant from ANOTHER edition can never be attached: they cannot see the session and
    /// cannot be mailed about it, so a stray id must be dropped rather than trusted.
    /// </summary>
    [Fact]
    public async Task A_participant_from_another_edition_is_refused()
    {
        using var db = ScenarioFixture.NewDb();
        var s = await SeedSessionAsync(db);
        var mine = await SeedSpeakerAsync(db, "a@x.dk");
        var other = new Participant
        {
            EventId = 99, Email = "elsewhere@x.dk", FullName = "Other", Role = ParticipantRole.Speaker,
            IsActive = true, LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(other);
        await db.SaveChangesAsync();

        await NewService(db).UpdateSessionAsync(
            EventId, s.Id, SessionType.TechnicalSession, 60, null, null,
            speakerParticipantIds: new[] { mine, other.Id });

        Assert.Equal(1, await LinkCount(db, s.Id));
    }

    [Fact]
    public async Task A_new_hub_session_can_be_created_WITH_its_speakers()
    {
        // The service always accepted this; the form never offered it, so every new hub session
        // arrived empty and had to be edited straight afterwards.
        using var db = ScenarioFixture.NewDb();
        await SeedSessionAsync(db);
        var a = await SeedSpeakerAsync(db, "a@x.dk");

        var created = await NewService(db).AddHubSessionAsync(
            EventId, "Sponsor showcase", SessionType.TechnicalSession, 45,
            day: HubSessionDay.MainDay, speakerParticipantIds: new[] { a });

        Assert.Equal(1, await LinkCount(db, created.Id));
    }
}
