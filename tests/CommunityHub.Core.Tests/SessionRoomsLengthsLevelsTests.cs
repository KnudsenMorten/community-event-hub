using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Reminders;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §299 batch 3 (b5 room model + b7 session schema) tests:
///  - <b>config parse</b> of the sessionLengths quick-picks (+ max), sessionLevels
///    (numeric codes) and the sessionRooms registry (venue + expo entries),
///  - <b>room registry</b> exact-name lookup incl. the EXPO null capacity (never 0),
///  - <b>warn-only room validation</b>: unknown imported rooms surface as import
///    WARNINGS and never block,
///  - <b>LevelCode derivation</b> from "Expert (400)"-style labels + the numeric sort,
///  - <b>integer-minutes length</b>: custom 37 and full-day 420 accepted;
///    0 / negative / above-max rejected,
///  - <b>❓OPEN-20</b>: a manual schedule edit sets IsDateOverridden and the
///    re-import respects it (skips the StartsAt/EndsAt refresh),
///  - <b>hub-add day prompt</b>: the required Pre-day / Main day choice stamps the
///    right date (09:00) and marks the schedule manually owned,
///  - the Sessionize parser carrying a "Tags" category group into Session.Tags.
///
/// Synthetic ids + example.test only — no real data.
/// </summary>
public sealed class SessionRoomsLengthsLevelsTests
{
    private static readonly DateTimeOffset Now =
        new(2027, 1, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Parse an event-config JSON fragment through the REAL loader path
    /// (missing shipped file + override fragment = the fragment alone).</summary>
    private static EventEditionConfig Cfg(string json) =>
        new EventEditionConfigLoader().Load("does-not-exist-config.json", json);

    /// <summary>A representative config: quick-picks incl. the 420 full day, the
    /// three ELDK levels, and a registry mixing venue (per-day) + expo entries.</summary>
    private const string ConfigJson = /*lang=json*/ """
    {
      "sessionLengths": [
        { "label": "15 min", "minutes": 15 },
        { "label": "60 min", "minutes": 60 },
        { "label": "Full day (Master Class)", "minutes": 420 },
        { "label": "garbage", "minutes": 0 },
        { "label": "", "minutes": 30 }
      ],
      "sessionLengthMaxMinutes": 600,
      "sessionLevels": [
        { "label": "Black Belt", "code": 500 },
        { "label": "Advanced", "code": 300 },
        { "label": "Expert", "code": 400 }
      ],
      "sessionRooms": [
        { "name": "Room-20-Floor 1-Max 54-MC", "floor": "1", "capacity": 54, "preDay": true },
        { "name": "Room-20-Floor 1-Max 118", "floor": "1", "capacity": 118, "preDay": false },
        { "name": "Ask The Experts Live - table 1", "capacity": null, "expo": true },
        { "name": "Expo-Stage", "capacity": null, "expo": true }
      ]
    }
    """;

    // ------------------------------------------------------------ config parse ----

    [Fact]
    public void SessionLengths_quickpicks_parse_and_drop_garbage_rows()
    {
        var cfg = Cfg(ConfigJson);
        var opts = new SessionOptionsService(cfg);

        // Garbage rows (blank label / non-positive minutes) are dropped; order kept.
        Assert.Equal(new[] { 15, 60, 420 }, opts.LengthQuickPicks.Select(q => q.Minutes));
        Assert.Equal("Full day (Master Class)",
            opts.LengthQuickPicks.Single(q => q.Minutes == 420).Label);
        Assert.Equal(600, opts.MaxMinutes);
    }

    [Fact]
    public void SessionLengthMax_falls_back_to_shipped_default_when_absent()
    {
        var opts = new SessionOptionsService(Cfg("""{ "sessionLengths": [] }"""));
        Assert.Equal(EventEditionConfig.DefaultSessionLengthMaxMinutes, opts.MaxMinutes);
        Assert.True(opts.IsValidLength(420));   // the full day always passes
        Assert.False(opts.IsValidLength(0));
        Assert.False(opts.IsValidLength(601));
    }

    [Fact]
    public void SessionLevels_sort_by_numeric_code_never_alphabetically()
    {
        var opts = new SessionOptionsService(Cfg(ConfigJson));

        // Alphabetical would put "Black Belt" FIRST; the numeric sort puts it LAST.
        Assert.Equal(new[] { "Advanced", "Expert", "Black Belt" },
            opts.Levels.Select(l => l.Label));
        Assert.Equal(new[] { 300, 400, 500 }, opts.Levels.Select(l => l.Code));
    }

    // ------------------------------------------------------------ room registry ----

    [Fact]
    public void RoomRegistry_exact_lookup_capacity_and_expo_null()
    {
        var reg = new RoomRegistryService(Cfg(ConfigJson));

        Assert.True(reg.HasEntries);
        // The SAME physical room appears per day with DIFFERENT capacity = two
        // distinct records (Room-20: 54 pre-day vs 118 main).
        Assert.Equal(54, reg.CapacityOf("Room-20-Floor 1-Max 54-MC"));
        Assert.Equal(118, reg.CapacityOf("Room-20-Floor 1-Max 118"));
        Assert.True(reg.Find("Room-20-Floor 1-Max 54-MC")!.PreDay);
        Assert.False(reg.Find("Room-20-Floor 1-Max 118")!.PreDay);

        // EXPO locations: known, capacity NULL by design (never 0).
        Assert.True(reg.IsKnown("Expo-Stage"));
        Assert.Null(reg.CapacityOf("Expo-Stage"));
        Assert.Null(reg.CapacityOf("Ask The Experts Live - table 1"));
        Assert.True(reg.Find("Expo-Stage")!.Expo);

        // Lookup is EXACT (byte-identical names are the whole point): a case or
        // spacing drift is exactly what must surface as unknown.
        Assert.False(reg.IsKnown("room-20-floor 1-max 118"));
        Assert.False(reg.IsKnown("Room-20-Floor 1-Max 119"));
        Assert.Null(reg.CapacityOf("Room-20-Floor 1-Max 119"));

        // Blank = nothing to validate.
        Assert.True(reg.IsKnown(null));
        Assert.True(reg.IsKnown("  "));
    }

    [Fact]
    public void RoomRegistry_without_config_keeps_validation_quiet()
    {
        var reg = new RoomRegistryService(new EventEditionConfig());
        Assert.False(reg.HasEntries);
        Assert.True(reg.IsKnown("Anything At All"));   // validation off, never warn
        Assert.Null(reg.CapacityOf("Anything At All"));
    }

    // --------------------------------------------------------- level derivation ----

    [Theory]
    [InlineData("Expert (400)", 400)]     // config label match
    [InlineData("Black Belt", 500)]       // config label match without digits
    [InlineData("black belt (500)", 500)] // case-insensitive
    [InlineData("Intermediate (200)", 200)] // not configured → "(NNN)" digits parse
    [InlineData("Mystery Level", null)]   // unknown → null (string-only behaviour)
    [InlineData("", null)]
    [InlineData(null, null)]
    public void DeriveLevelCode_matches_config_label_then_digits(string? label, int? expected)
    {
        var opts = new SessionOptionsService(Cfg(ConfigJson));
        Assert.Equal(expected, opts.DeriveLevelCode(label));
    }

    // ------------------------------------------------- import: warnings + fields ----

    private static SessionizeSession Src(
        string id, string title, string? room = null, string? level = null,
        string? tags = null, DateTimeOffset? start = null, DateTimeOffset? end = null) =>
        new(id, title, null, room, null, start, end, false,
            Array.Empty<string>(), Category: null, Level: level, Tags: tags);

    [Fact]
    public async Task Import_surfaces_unknown_rooms_as_warnings_and_never_blocks()
    {
        using var db = TestDb.New();
        var (eventId, _) = await TestDb.SeedEventAndPersonAsync(db, lockDate: null);
        var cfg = Cfg(ConfigJson);
        var svc = new SessionImportService(db, new FixedClock(Now),
            rooms: new RoomRegistryService(cfg), options: new SessionOptionsService(cfg));

        var result = await svc.ImportSessionsAsync(eventId, new[]
        {
            Src("s-ok", "Known Room Talk", room: "Room-20-Floor 1-Max 118"),
            Src("s-typo", "Typo Room Talk", room: "Room-20-Floor 1-Max 119"),
            Src("s-none", "Roomless Talk"),
        }, Array.Empty<SessionizeSpeaker>(), Array.Empty<string>());

        // Nothing blocked — all three sessions imported.
        Assert.Equal(3, result.Created);
        Assert.Null(result.Error);
        // Exactly the drifted name warns (blank/registered rooms stay quiet).
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("Room-20-Floor 1-Max 119", warning);
        Assert.Contains("registry", warning);
    }

    [Fact]
    public async Task Import_without_registry_config_stays_quiet_about_rooms()
    {
        using var db = TestDb.New();
        var (eventId, _) = await TestDb.SeedEventAndPersonAsync(db, lockDate: null);
        var svc = new SessionImportService(db, new FixedClock(Now));   // no registry wired

        var result = await svc.ImportSessionsAsync(eventId,
            new[] { Src("s-1", "Any Room Talk", room: "Anything") },
            Array.Empty<SessionizeSpeaker>(), Array.Empty<string>());

        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Import_derives_level_code_and_carries_tags()
    {
        using var db = TestDb.New();
        var (eventId, _) = await TestDb.SeedEventAndPersonAsync(db, lockDate: null);
        var cfg = Cfg(ConfigJson);
        var svc = new SessionImportService(db, new FixedClock(Now),
            options: new SessionOptionsService(cfg));

        await svc.ImportSessionsAsync(eventId, new[]
        {
            Src("s-ex", "Deep Dive", level: "Expert (400)", tags: "Cloud, Security"),
            Src("s-unk", "Odd Level", level: "Mystery Level"),
            Src("s-plain", "No Extras"),
        }, Array.Empty<SessionizeSpeaker>(), Array.Empty<string>());

        var ex = await db.Sessions.SingleAsync(s => s.SessionizeId == "s-ex");
        Assert.Equal(400, ex.LevelCode);
        Assert.Equal("Cloud, Security", ex.Tags);

        // Unknown label keeps string-only behaviour (null code); no tags stays null.
        var unk = await db.Sessions.SingleAsync(s => s.SessionizeId == "s-unk");
        Assert.Equal("Mystery Level", unk.Level);
        Assert.Null(unk.LevelCode);
        Assert.Null((await db.Sessions.SingleAsync(s => s.SessionizeId == "s-plain")).Tags);
    }

    [Fact]
    public void Sessionize_parser_carries_a_tags_category_group()
    {
        // A minimal All-view payload whose categories include a "Tags" GROUP.
        const string json = /*lang=json*/ """
        {
          "sessions": [
            {
              "id": "s-1", "title": "Tagged Talk", "description": "",
              "speakers": [], "categoryItems": [ 11, 21, 22 ]
            }
          ],
          "speakers": [],
          "categories": [
            { "title": "Level", "items": [ { "id": 11, "name": "Expert (400)" } ] },
            { "title": "Tags", "items": [
                { "id": 21, "name": "Cloud" },
                { "id": 22, "name": "Security" } ] }
          ],
          "rooms": []
        }
        """;

        var result = SessionizeApiClient.ParseSessions(json);

        Assert.Null(result.Error);
        var s = Assert.Single(result.Sessions);
        Assert.Equal("Expert (400)", s.Level);
        Assert.Equal("Cloud, Security", s.Tags);
    }

    // ------------------------------------------------- length: minutes validation ----

    [Fact]
    public async Task Custom_and_fullday_lengths_accepted_invalid_rejected()
    {
        using var db = TestDb.New();
        var (eventId, _) = await TestDb.SeedEventAndPersonAsync(db, lockDate: null);
        var mgmt = new SessionManagementService(db, new NullRoomQrProvider(),
            new FixedClock(Now), new SessionOptionsService(Cfg(ConfigJson)));

        // A custom, non-quick-pick 37 is as valid as any pick; the bucket derives.
        var custom = await mgmt.AddHubSessionAsync(
            eventId, "Lightning 37", SessionType.TechnicalSession, 37,
            day: HubSessionDay.MainDay);
        Assert.Equal(37, custom.LengthMinutes);
        Assert.Equal(SessionLength.FiftyMin, custom.Length);   // nearest display bucket

        // 420 (full-day master class) must pass and derive the FullDay bucket.
        var fullDay = await mgmt.AddHubSessionAsync(
            eventId, "Azure Master Class", SessionType.MasterClass, 420,
            day: HubSessionDay.PreDay);
        Assert.Equal(420, fullDay.LengthMinutes);
        Assert.Equal(SessionLength.FullDay, fullDay.Length);

        // 0 / negative / above the configured max are rejected with honest messages.
        await Assert.ThrowsAsync<ArgumentException>(() => mgmt.AddHubSessionAsync(
            eventId, "Zero", SessionType.TechnicalSession, 0, day: HubSessionDay.MainDay));
        await Assert.ThrowsAsync<ArgumentException>(() => mgmt.AddHubSessionAsync(
            eventId, "Negative", SessionType.TechnicalSession, -5, day: HubSessionDay.MainDay));
        await Assert.ThrowsAsync<ArgumentException>(() => mgmt.AddHubSessionAsync(
            eventId, "Too long", SessionType.TechnicalSession, 601, day: HubSessionDay.MainDay));

        // The same validation guards the edit path.
        await Assert.ThrowsAsync<ArgumentException>(() => mgmt.UpdateSessionAsync(
            eventId, custom.Id, SessionType.TechnicalSession, 0, null, null));
    }

    // ------------------------------------------------------- hub-add day choice ----

    [Fact]
    public async Task HubAdd_day_choice_stamps_date_at_0900_and_sets_override()
    {
        using var db = TestDb.New();
        var (eventId, _) = await TestDb.SeedEventAndPersonAsync(db, lockDate: null);
        var evt = await db.Events.SingleAsync(e => e.Id == eventId);
        evt.PreDayDate = new DateOnly(2027, 2, 8);
        await db.SaveChangesAsync();

        var mgmt = new SessionManagementService(db, new NullRoomQrProvider(), new FixedClock(Now));

        var pre = await mgmt.AddHubSessionAsync(
            eventId, "Pre-day MC", SessionType.MasterClass, 420, day: HubSessionDay.PreDay);
        Assert.Equal(new DateTimeOffset(2027, 2, 8, 9, 0, 0, TimeSpan.Zero), pre.StartsAt);
        Assert.Equal(pre.StartsAt!.Value.AddMinutes(420), pre.EndsAt);
        Assert.True(pre.IsDateOverridden);   // imports must never touch this schedule

        var main = await mgmt.AddHubSessionAsync(
            eventId, "Main-day Talk", SessionType.TechnicalSession, 60, day: HubSessionDay.MainDay);
        Assert.Equal(new DateTimeOffset(2027, 2, 9, 9, 0, 0, TimeSpan.Zero), main.StartsAt);
        Assert.True(main.IsDateOverridden);

        // The legacy bucket overload (no day) still adds without a schedule stamp.
        var legacy = await mgmt.AddHubSessionAsync(
            eventId, "Legacy Add", SessionType.TechnicalSession, SessionLength.SixtyMin);
        Assert.Null(legacy.StartsAt);
        Assert.False(legacy.IsDateOverridden);
        Assert.Equal(60, legacy.LengthMinutes);
    }

    // ------------------------------------------------------------- ❓OPEN-20 ----

    [Fact]
    public async Task Manual_schedule_edit_sets_override_and_reimport_respects_it()
    {
        using var db = TestDb.New();
        var (eventId, _) = await TestDb.SeedEventAndPersonAsync(db, lockDate: null);
        var import = new SessionImportService(db, new FixedClock(Now));
        var mgmt = new SessionManagementService(db, new NullRoomQrProvider(), new FixedClock(Now));

        var srcStart = new DateTimeOffset(2027, 2, 9, 10, 0, 0, TimeSpan.Zero);
        var srcEnd = srcStart.AddMinutes(60);
        await import.ImportSessionsAsync(eventId,
            new[] { Src("s-1", "Scheduled Talk", start: srcStart, end: srcEnd) },
            Array.Empty<SessionizeSpeaker>(), Array.Empty<string>());
        var s = await db.Sessions.SingleAsync(x => x.SessionizeId == "s-1");
        Assert.False(s.IsDateOverridden);

        // Posting back the UNCHANGED schedule is NOT a manual override.
        await mgmt.UpdateSessionAsync(eventId, s.Id, s.Type, 60, s.Room, null,
            startsAt: srcStart, endsAt: srcEnd, applySchedule: true);
        s = await db.Sessions.SingleAsync(x => x.SessionizeId == "s-1");
        Assert.False(s.IsDateOverridden);

        // A REAL manual date change sets the flag (TypeIsManualOverride pattern).
        var manualStart = new DateTimeOffset(2027, 2, 10, 13, 0, 0, TimeSpan.Zero);
        await mgmt.UpdateSessionAsync(eventId, s.Id, s.Type, 60, s.Room, null,
            startsAt: manualStart, endsAt: manualStart.AddMinutes(60), applySchedule: true);
        s = await db.Sessions.SingleAsync(x => x.SessionizeId == "s-1");
        Assert.True(s.IsDateOverridden);
        Assert.Equal(manualStart, s.StartsAt);

        // The re-import refreshes everything EXCEPT the manually-owned schedule.
        await import.ImportSessionsAsync(eventId,
            new[] { Src("s-1", "Scheduled Talk RENAMED", start: srcStart, end: srcEnd) },
            Array.Empty<SessionizeSpeaker>(), Array.Empty<string>());
        s = await db.Sessions.SingleAsync(x => x.SessionizeId == "s-1");
        Assert.Equal("Scheduled Talk RENAMED", s.Title);   // other fields refreshed
        Assert.Equal(manualStart, s.StartsAt);             // manual date survived
        Assert.Equal(manualStart.AddMinutes(60), s.EndsAt);

        // A session WITHOUT the flag keeps getting its schedule from the source.
        await import.ImportSessionsAsync(eventId,
            new[] { Src("s-2", "Auto Talk", start: srcStart, end: srcEnd) },
            Array.Empty<SessionizeSpeaker>(), Array.Empty<string>());
        var auto = await db.Sessions.SingleAsync(x => x.SessionizeId == "s-2");
        Assert.Equal(srcStart, auto.StartsAt);
        Assert.False(auto.IsDateOverridden);
    }
}
