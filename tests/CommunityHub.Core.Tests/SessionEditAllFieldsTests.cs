using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 🔴 §1005.3 — the organizer can edit EVERY field CEH owns.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-09: *"i also need the ability to edit session and modify every all fields,
/// with dropdowns for fields whre relevant"*.</para>
///
/// <para>🔑 <b>Load-bearing, not cosmetic.</b> §999 made CEH the owner of date/time, room, track and
/// tags; §1000 made it the owner of the schedule outright. Sessionize's copy is only REPORTED now,
/// and the Backstage sessions API is create-only — so a field CEH owns but cannot edit is a field
/// that <b>cannot be corrected anywhere at all</b>.</para>
/// </remarks>
public sealed class SessionEditAllFieldsTests
{
    private const int EventId = 1;

    private static async Task<Session> SeedAsync(CommunityHub.Core.Data.CommunityHubDbContext db)
    {
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 10), EndDate = new DateOnly(2027, 2, 10),
        });
        var s = new Session
        {
            EventId = EventId, SessionizeId = "sz-1", Title = "Original title",
            Abstract = "Original abstract", Track = "Cloud", Level = "Expert (400)",
            Tags = "azure", Room = "Hall A", LengthMinutes = 60, Type = SessionType.TechnicalSession,
        };
        db.Sessions.Add(s);
        await db.SaveChangesAsync();
        return s;
    }

    private static SessionManagementService NewService(
        CommunityHub.Core.Data.CommunityHubDbContext db,
        CommunityHub.Core.Config.SessionOptionsService? options = null) =>
        new(db, new NoOpRoomQr(), TimeProvider.System, options);

    private sealed class NoOpRoomQr : IRoomQrProvider
    {
        public bool CanProvision => false;
        public Task<RoomQr> EnsureRoomQrAsync(
            string eventCode, string room, string targetUrl, CancellationToken ct = default) =>
            Task.FromResult(new RoomQr(targetUrl, null));
    }

    [Fact]
    public async Task Every_CEH_owned_field_can_be_edited()
    {
        using var db = ScenarioFixture.NewDb();
        var s = await SeedAsync(db);

        await NewService(db).UpdateSessionAsync(
            EventId, s.Id, SessionType.Keynote, 45, "Hall B", null,
            title: "New title", sessionAbstract: "New abstract",
            track: "Security", level: "Advanced (300)", tags: "entra, security");

        var after = await db.Sessions.SingleAsync();
        Assert.Equal("New title", after.Title);
        Assert.Equal("New abstract", after.Abstract);
        Assert.Equal("Security", after.Track);
        Assert.Equal("Advanced (300)", after.Level);
        Assert.Equal("entra, security", after.Tags);
        Assert.Equal("Hall B", after.Room);
        Assert.Equal(SessionType.Keynote, after.Type);
    }

    /// <summary>
    /// 🔒 A caller that does not RENDER a field must not be able to blank it. Every existing call
    /// site of this method predates §1005.3 and passes none of these — if null meant "clear", every
    /// one of them would silently wipe the title, abstract, track, level and tags of the session it
    /// edited.
    /// </summary>
    [Fact]
    public async Task A_field_that_is_not_supplied_is_left_untouched()
    {
        using var db = ScenarioFixture.NewDb();
        var s = await SeedAsync(db);

        // Exactly the pre-§1005.3 call: type, length, room, eval URL — and nothing else.
        await NewService(db).UpdateSessionAsync(EventId, s.Id, SessionType.TechnicalSession, 60, "Hall A", null);

        var after = await db.Sessions.SingleAsync();
        Assert.Equal("Original title", after.Title);
        Assert.Equal("Original abstract", after.Abstract);
        Assert.Equal("Cloud", after.Track);
        Assert.Equal("Expert (400)", after.Level);
        Assert.Equal("azure", after.Tags);
    }

    /// <summary>
    /// A supplied-but-BLANK value DOES clear — removing a wrong tag or level is a real edit, and a
    /// field that could only ever be set would be a trap.
    /// </summary>
    [Fact]
    public async Task A_supplied_blank_clears_the_field()
    {
        using var db = ScenarioFixture.NewDb();
        var s = await SeedAsync(db);

        await NewService(db).UpdateSessionAsync(
            EventId, s.Id, SessionType.TechnicalSession, 60, "Hall A", null,
            sessionAbstract: "", track: "", level: "", tags: "");

        var after = await db.Sessions.SingleAsync();
        Assert.Null(after.Abstract);
        Assert.Null(after.Track);
        Assert.Null(after.Level);
        Assert.Null(after.Tags);
    }

    /// <summary>
    /// 🔒 THE TITLE IS THE EXCEPTION AND NEVER BLANKS. Everything downstream identifies a session to
    /// a HUMAN by it — the ops mails, the public agenda, the speaker's own page — so an empty title
    /// reads as data loss rather than as an edit.
    /// </summary>
    [Fact]
    public async Task A_blank_title_is_refused_rather_than_saved()
    {
        using var db = ScenarioFixture.NewDb();
        var s = await SeedAsync(db);

        await NewService(db).UpdateSessionAsync(
            EventId, s.Id, SessionType.TechnicalSession, 60, "Hall A", null, title: "   ");

        Assert.Equal("Original title", (await db.Sessions.SingleAsync()).Title);
    }

    /// <summary>
    /// 🔴 §1022 — ENDS IS DERIVED from start + length; a posted end is ignored.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-09: *"ends should be a calculated field based on the length+start
    /// time"*.</para>
    ///
    /// <para>⚠️ <b>The hazard is not cosmetic.</b> <c>SessionBackstagePushService.DurationMinutes</c>
    /// prefers <b>end−start</b> over <c>LengthMinutes</c>, so the moment the two disagree the
    /// duration pushed to Zoho — and the duration the §1002 difference mail asks him to set over
    /// there — comes from the field he did not think he was editing. ✅ Measured 2026-08-09: no PROD
    /// session had them disagreeing yet, so this closes a latent trap rather than a live break.</para>
    /// </remarks>
    [Fact]
    public async Task Ends_is_recalculated_from_start_plus_length_and_a_posted_end_is_ignored()
    {
        using var db = ScenarioFixture.NewDb();
        var s = await SeedAsync(db);
        var start = new DateTimeOffset(2027, 2, 10, 8, 30, 0, TimeSpan.FromHours(1));

        await NewService(db).UpdateSessionAsync(
            EventId, s.Id, SessionType.Keynote, 20, "Hall A", null,
            startsAt: start,
            // A wildly wrong end, as the old free-text box allowed. It must not survive.
            endsAt: start.AddMinutes(90),
            applySchedule: true);

        var after = await db.Sessions.SingleAsync();
        Assert.Equal(start.AddMinutes(20), after.EndsAt);
        Assert.Equal(20, (int)(after.EndsAt!.Value - after.StartsAt!.Value).TotalMinutes);
    }

    /// <summary>
    /// 🔒 Changing ONLY the length must move the end with it — that is the whole point of the field
    /// being calculated, and it is how a 20-minute session stops claiming to run for 90.
    /// </summary>
    [Fact]
    public async Task Changing_the_length_moves_the_end()
    {
        using var db = ScenarioFixture.NewDb();
        var s = await SeedAsync(db);
        var start = new DateTimeOffset(2027, 2, 10, 8, 30, 0, TimeSpan.FromHours(1));
        await NewService(db).UpdateSessionAsync(
            EventId, s.Id, SessionType.Keynote, 20, "Hall A", null,
            startsAt: start, endsAt: null, applySchedule: true);

        await NewService(db).UpdateSessionAsync(
            EventId, s.Id, SessionType.Keynote, 45, "Hall A", null,
            startsAt: start, endsAt: null, applySchedule: true);

        var after = await db.Sessions.SingleAsync();
        Assert.Equal(start.AddMinutes(45), after.EndsAt);
    }

    /// <summary>
    /// §299.8/b7 — the numeric LevelCode is DERIVED, never typed, and every sort and comparison uses
    /// it (alphabetically "Black Belt" sorts before "Expert", which is wrong). Editing the label
    /// must re-derive it, or the session keeps sorting by its old level for ever.
    /// </summary>
    [Fact]
    public async Task Editing_the_level_re_derives_its_numeric_code()
    {
        using var db = ScenarioFixture.NewDb();
        var s = await SeedAsync(db);
        s.LevelCode = 400;
        await db.SaveChangesAsync();

        await NewService(db).UpdateSessionAsync(
            EventId, s.Id, SessionType.TechnicalSession, 60, "Hall A", null,
            level: "Advanced (300)");

        Assert.Equal(300, (await db.Sessions.SingleAsync()).LevelCode);

        // …and clearing the label clears the code, rather than leaving 300 behind a blank level.
        await NewService(db).UpdateSessionAsync(
            EventId, s.Id, SessionType.TechnicalSession, 60, "Hall A", null, level: "");
        Assert.Null((await db.Sessions.SingleAsync()).LevelCode);
    }
}
