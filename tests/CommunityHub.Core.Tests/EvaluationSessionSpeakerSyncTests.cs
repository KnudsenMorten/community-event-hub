using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Evaluation;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §743 C3 — mirroring a session's SPEAKERS into the evaluation model
/// (<c>EvaluationSessionSpeaker</c>, the brief's <c>SessionSpeaker</c>).
/// </summary>
/// <remarks>
/// <para>These rows are a <b>grouping and mailing</b> key, never a credential: §743.3 put the
/// speaker's results inside CEH, so who may READ a session is decided by CEH's own
/// <c>MineAsSpeaker</c>. What breaks if these are wrong is quieter and slower — the C7 speaker-history
/// section, the C0 report-ready notification, and <c>scope=speaker</c> aggregates.</para>
///
/// <para>🔑 The headline test is <see cref="ONLY_the_primary_email_is_exported_never_the_other_address_columns"/>.
/// It is the one that cannot be written later: PROD has 0 contact overrides and 1 secondary today, so
/// picking the wrong address column would pass every other test in this file and stay invisible until
/// somebody sets one.</para>
///
/// <para>EF Core InMemory + a fixed clock; FAKE names and addresses only.</para>
/// </remarks>
public sealed class EvaluationSessionSpeakerSyncTests
{
    private const int EventId = 1;
    private static readonly DateTimeOffset Now = new(2027, 2, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Day = new(2027, 2, 9, 0, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static async Task<CommunityHubDbContext> SeedAsync()
    {
        var db = ScenarioFixture.NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "T", Code = "T27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        });
        await db.SaveChangesAsync();
        return db;
    }

    private static Session CehSession(string title, string room, int startHour, int endHour) =>
        new()
        {
            EventId = EventId, Title = title, Room = room,
            StartsAt = Day.AddHours(startHour), EndsAt = Day.AddHours(endHour),
        };

    private static Participant Speaker(string email, string name) =>
        new()
        {
            EventId = EventId, Email = email, FullName = name,
            Role = ParticipantRole.Speaker, IsActive = true,
        };

    private static EvaluationSessionSyncService NewSync(CommunityHubDbContext db) =>
        new(db, new FixedClock());

    // ---- the mirror ---------------------------------------------------------------------------

    [Fact]
    public async Task A_sessions_speakers_are_mirrored_with_their_primary_email_and_name()
    {
        using var db = await SeedAsync();
        var s = CehSession("Keeping identity boring", "Room 1", 9, 10);
        var p = Speaker("ada@example.test", "Ada Lovelace");
        db.Sessions.Add(s); db.Participants.Add(p);
        await db.SaveChangesAsync();
        db.SessionSpeakers.Add(new SessionSpeaker { SessionId = s.Id, ParticipantId = p.Id });
        await db.SaveChangesAsync();

        var result = await NewSync(db).SyncAsync(EventId);

        Assert.Equal(1, result.SpeakerLinksAdded);
        var link = db.EvaluationSessionSpeakers.Single();
        Assert.Equal("ada@example.test", link.SpeakerEmail);
        Assert.Equal("Ada Lovelace", link.DisplayName);
        Assert.Equal(p.Id, link.CehParticipantId);
        Assert.Contains(result.Changes, c => c.Contains("Speaker linked") && c.Contains("ada@example.test"));
    }

    /// <summary>
    /// 🔒 §743.2's build constraint, and the trap that would otherwise stay hidden.
    /// <c>ContactEmailOverride</c>, <c>SecondaryEmail</c> and <c>AlternateEmail</c> all look like
    /// "the speaker's email" in this schema. Only <c>Participant.Email</c> is what CEH signs a
    /// session in with, and therefore the only one that groups and mails correctly.
    /// </summary>
    [Fact]
    public async Task ONLY_the_primary_email_is_exported_never_the_other_address_columns()
    {
        using var db = await SeedAsync();
        var s = CehSession("Keynote", "Room 1", 9, 10);
        var p = Speaker("primary@example.test", "Grace Hopper");
        p.SecondaryEmail = "secondary@example.test";     // a DELIVERY address
        p.AlternateEmail = "alternate@example.test";     // a sign-in DOORWAY, not the identity
        db.Sessions.Add(s); db.Participants.Add(p);
        await db.SaveChangesAsync();
        db.SessionSpeakers.Add(new SessionSpeaker { SessionId = s.Id, ParticipantId = p.Id });
        await db.SaveChangesAsync();

        await NewSync(db).SyncAsync(EventId);

        var link = db.EvaluationSessionSpeakers.Single();
        Assert.Equal("primary@example.test", link.SpeakerEmail);
        Assert.DoesNotContain("secondary", link.SpeakerEmail);
        Assert.DoesNotContain("alternate", link.SpeakerEmail);
    }

    /// <summary>
    /// 🔒 Sign-in compares against <c>NormalizeEmail</c>. Storing a mixed-case export would fail to
    /// match for exactly the people whose address was typed with capitals.
    /// </summary>
    [Fact]
    public async Task The_email_is_stored_normalised_the_same_way_the_login_normalises_it()
    {
        using var db = await SeedAsync();
        var s = CehSession("Keynote", "Room 1", 9, 10);
        var p = Speaker("  Firstname.Lastname@Example.TEST  ", "Mixed Case");
        db.Sessions.Add(s); db.Participants.Add(p);
        await db.SaveChangesAsync();
        db.SessionSpeakers.Add(new SessionSpeaker { SessionId = s.Id, ParticipantId = p.Id });
        await db.SaveChangesAsync();

        await NewSync(db).SyncAsync(EventId);

        var link = db.EvaluationSessionSpeakers.Single();
        Assert.Equal("firstname.lastname@example.test", link.SpeakerEmail);
        Assert.Equal(Auth.PinLoginService.NormalizeEmail(p.Email), link.SpeakerEmail);
    }

    [Fact]
    public async Task A_session_with_several_speakers_gets_a_row_each()
    {
        using var db = await SeedAsync();
        var s = CehSession("Panel", "Room 1", 9, 10);
        var a = Speaker("a@example.test", "A"); var b = Speaker("b@example.test", "B");
        db.Sessions.Add(s); db.Participants.AddRange(a, b);
        await db.SaveChangesAsync();
        db.SessionSpeakers.AddRange(
            new SessionSpeaker { SessionId = s.Id, ParticipantId = a.Id },
            new SessionSpeaker { SessionId = s.Id, ParticipantId = b.Id });
        await db.SaveChangesAsync();

        var result = await NewSync(db).SyncAsync(EventId);

        Assert.Equal(2, result.SpeakerLinksAdded);
        Assert.Equal(2, db.EvaluationSessionSpeakers.Count());
    }

    /// <summary>
    /// 🔑 The shape C7's speaker-history section and <c>scope=speaker</c> both rely on: one address
    /// reaching several sessions. Without this the history section has nothing to aggregate.
    /// </summary>
    [Fact]
    public async Task One_speaker_across_two_sessions_yields_two_rows_under_ONE_email()
    {
        using var db = await SeedAsync();
        var s1 = CehSession("Talk one", "Room 1", 9, 10);
        var s2 = CehSession("Talk two", "Room 2", 11, 12);
        var p = Speaker("prolific@example.test", "Prolific Speaker");
        db.Sessions.AddRange(s1, s2); db.Participants.Add(p);
        await db.SaveChangesAsync();
        db.SessionSpeakers.AddRange(
            new SessionSpeaker { SessionId = s1.Id, ParticipantId = p.Id },
            new SessionSpeaker { SessionId = s2.Id, ParticipantId = p.Id });
        await db.SaveChangesAsync();

        await NewSync(db).SyncAsync(EventId);

        var rows = db.EvaluationSessionSpeakers.ToList();
        Assert.Equal(2, rows.Count);
        Assert.Single(rows.Select(r => r.SpeakerEmail).Distinct());
        Assert.Equal(2, rows.Select(r => r.EvaluationSessionId).Distinct().Count());
    }

    // ---- keeping it current -------------------------------------------------------------------

    [Fact]
    public async Task Re_running_the_sync_adds_nothing_and_leaves_one_row_per_speaker()
    {
        using var db = await SeedAsync();
        var s = CehSession("Keynote", "Room 1", 9, 10);
        var p = Speaker("ada@example.test", "Ada");
        db.Sessions.Add(s); db.Participants.Add(p);
        await db.SaveChangesAsync();
        db.SessionSpeakers.Add(new SessionSpeaker { SessionId = s.Id, ParticipantId = p.Id });
        await db.SaveChangesAsync();

        await NewSync(db).SyncAsync(EventId);
        var second = await NewSync(db).SyncAsync(EventId);

        Assert.Equal(0, second.SpeakerLinksAdded);
        Assert.Equal(0, second.SpeakerLinksRemoved);
        Assert.Single(db.EvaluationSessionSpeakers);
    }

    /// <summary>
    /// 🔑 A speaker dropped in CEH stops getting that session's report — correct, and exactly the
    /// silent change that surfaces later as "I never received my results". So it is LOGGED.
    /// </summary>
    [Fact]
    public async Task A_speaker_removed_in_CEH_is_unlinked_AND_the_removal_is_logged()
    {
        using var db = await SeedAsync();
        var s = CehSession("Keynote", "Room 1", 9, 10);
        var a = Speaker("stays@example.test", "Stays"); var b = Speaker("goes@example.test", "Goes");
        db.Sessions.Add(s); db.Participants.AddRange(a, b);
        await db.SaveChangesAsync();
        var linkA = new SessionSpeaker { SessionId = s.Id, ParticipantId = a.Id };
        var linkB = new SessionSpeaker { SessionId = s.Id, ParticipantId = b.Id };
        db.SessionSpeakers.AddRange(linkA, linkB);
        await db.SaveChangesAsync();
        await NewSync(db).SyncAsync(EventId);

        db.SessionSpeakers.Remove(linkB);
        await db.SaveChangesAsync();
        var result = await NewSync(db).SyncAsync(EventId);

        Assert.Equal(1, result.SpeakerLinksRemoved);
        Assert.Equal("stays@example.test", db.EvaluationSessionSpeakers.Single().SpeakerEmail);
        Assert.Contains(result.Changes,
            c => c.Contains("Speaker unlinked") && c.Contains("goes@example.test"));
    }

    [Fact]
    public async Task A_speaker_added_in_CEH_later_is_picked_up_by_the_next_sync()
    {
        using var db = await SeedAsync();
        var s = CehSession("Keynote", "Room 1", 9, 10);
        var a = Speaker("first@example.test", "First");
        db.Sessions.Add(s); db.Participants.Add(a);
        await db.SaveChangesAsync();
        db.SessionSpeakers.Add(new SessionSpeaker { SessionId = s.Id, ParticipantId = a.Id });
        await db.SaveChangesAsync();
        await NewSync(db).SyncAsync(EventId);

        var b = Speaker("cospeaker@example.test", "Co Speaker");
        db.Participants.Add(b);
        await db.SaveChangesAsync();
        db.SessionSpeakers.Add(new SessionSpeaker { SessionId = s.Id, ParticipantId = b.Id });
        await db.SaveChangesAsync();
        var result = await NewSync(db).SyncAsync(EventId);

        Assert.Equal(1, result.SpeakerLinksAdded);
        Assert.Equal(2, db.EvaluationSessionSpeakers.Count());
    }

    /// <summary>
    /// A rename in CEH follows silently: the name is display-only and nothing keys on it, so logging
    /// every rename would bury the link/unlink changes that actually matter.
    /// </summary>
    [Fact]
    public async Task A_renamed_speaker_updates_the_display_name_without_relinking()
    {
        using var db = await SeedAsync();
        var s = CehSession("Keynote", "Room 1", 9, 10);
        var p = Speaker("ada@example.test", "Ada Lovelace");
        db.Sessions.Add(s); db.Participants.Add(p);
        await db.SaveChangesAsync();
        db.SessionSpeakers.Add(new SessionSpeaker { SessionId = s.Id, ParticipantId = p.Id });
        await db.SaveChangesAsync();
        await NewSync(db).SyncAsync(EventId);

        p.FullName = "Ada King";
        await db.SaveChangesAsync();
        var result = await NewSync(db).SyncAsync(EventId);

        Assert.Equal(0, result.SpeakerLinksAdded);
        Assert.Equal(0, result.SpeakerLinksRemoved);
        Assert.Equal("Ada King", db.EvaluationSessionSpeakers.Single().DisplayName);
    }

    // ---- refusing correctly -------------------------------------------------------------------

    /// <summary>
    /// 🔒 A blank key would be stored, would group every emailless speaker together, and would mail
    /// nobody — while looking like a successful link. Reported instead.
    /// </summary>
    [Fact]
    public async Task A_speaker_with_no_primary_email_is_WARNED_not_stored_as_a_blank_key()
    {
        using var db = await SeedAsync();
        var s = CehSession("Keynote", "Room 1", 9, 10);
        var p = Speaker("   ", "No Address");
        db.Sessions.Add(s); db.Participants.Add(p);
        await db.SaveChangesAsync();
        db.SessionSpeakers.Add(new SessionSpeaker { SessionId = s.Id, ParticipantId = p.Id });
        await db.SaveChangesAsync();

        var result = await NewSync(db).SyncAsync(EventId);

        Assert.Equal(0, result.SpeakerLinksAdded);
        Assert.Empty(db.EvaluationSessionSpeakers);
        Assert.Contains(result.Warnings,
            w => w.Contains("No Address") && w.Contains("will not receive their report"));
    }

    /// <summary>
    /// A session CEH never mirrored — no room, or a service session — has nothing to link speakers
    /// to. It must not throw, and must not invent a session to hang them off.
    /// </summary>
    [Fact]
    public async Task A_session_that_was_not_mirrored_contributes_no_speaker_rows()
    {
        using var db = await SeedAsync();
        var s = new Session
        {
            EventId = EventId, Title = "Service", Room = "Room 1",
            StartsAt = Day.AddHours(9), EndsAt = Day.AddHours(10), IsServiceSession = true,
        };
        var p = Speaker("crew@example.test", "Crew");
        db.Sessions.Add(s); db.Participants.Add(p);
        await db.SaveChangesAsync();
        db.SessionSpeakers.Add(new SessionSpeaker { SessionId = s.Id, ParticipantId = p.Id });
        await db.SaveChangesAsync();

        var result = await NewSync(db).SyncAsync(EventId);

        Assert.Equal(0, result.SpeakerLinksAdded);
        Assert.Empty(db.EvaluationSessionSpeakers);
    }

    [Fact]
    public async Task An_event_with_no_speakers_linked_anywhere_syncs_cleanly()
    {
        using var db = await SeedAsync();
        db.Sessions.Add(CehSession("Keynote", "Room 1", 9, 10));
        await db.SaveChangesAsync();

        var result = await NewSync(db).SyncAsync(EventId);

        Assert.Equal(1, result.SessionsCreated);
        Assert.Equal(0, result.SpeakerLinksAdded);
        Assert.Empty(db.EvaluationSessionSpeakers);
    }
}
