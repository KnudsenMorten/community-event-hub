using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Reminders;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// FEATURE 1: the standardized <see cref="SessionType"/> + the import mapping from a
/// source category / format label (with the duration fallback), and the rule that a
/// Sessionize / Backstage re-import RESPECTS an organizer's manual type override.
/// </summary>
public sealed class SessionTypeMappingTests
{
    private static readonly DateTimeOffset Now = new(2027, 1, 1, 9, 0, 0, TimeSpan.Zero);

    // ----------------------------------------------------- category mapping ----

    [Theory]
    [InlineData("Master Class", SessionType.MasterClass)]
    [InlineData("masterclass", SessionType.MasterClass)]
    [InlineData("Hands-on Workshop", SessionType.MasterClass)]
    [InlineData("Keynote", SessionType.Keynote)]
    [InlineData("Opening Keynote", SessionType.Keynote)]
    [InlineData("Ask the Experts", SessionType.AskTheExperts)]
    [InlineData("Panel", SessionType.PanelDiscussion)]
    [InlineData("Panel Discussion", SessionType.PanelDiscussion)]
    [InlineData("Welcome", SessionType.Welcome)]
    [InlineData("Breakout Session", SessionType.TechnicalSession)] // recognised-but-plain → tech
    public void MapType_from_category_label(string category, SessionType expected)
    {
        Assert.Equal(expected, SessionDefaultsMapper.MapType(category, SessionLength.FiftyMin));
    }

    [Fact]
    public void MapType_falls_back_to_duration_when_no_category()
    {
        // No label → full-day is a master class, anything else a technical session.
        Assert.Equal(SessionType.MasterClass, SessionDefaultsMapper.MapType(null, SessionLength.FullDay));
        Assert.Equal(SessionType.TechnicalSession, SessionDefaultsMapper.MapType("", SessionLength.FiftyMin));
        // The duration-only overload keeps working.
        Assert.Equal(SessionType.MasterClass, SessionDefaultsMapper.MapType(SessionLength.FullDay));
        Assert.Equal(SessionType.TechnicalSession, SessionDefaultsMapper.MapType(SessionLength.SixtyMin));
    }

    [Fact]
    public void MapType_category_wins_over_duration()
    {
        // A 50-min session explicitly categorised as a master class is a master class.
        Assert.Equal(SessionType.MasterClass,
            SessionDefaultsMapper.MapType("Master Class", SessionLength.FiftyMin));
        // A full-day session explicitly categorised as a keynote is a keynote.
        Assert.Equal(SessionType.Keynote,
            SessionDefaultsMapper.MapType("Keynote", SessionLength.FullDay));
    }

    // -------------------------------------------------- length from format -----

    [Theory]
    // The Sessionize agenda grid is usually unpublished (no start/end on the API), so the length
    // must come from the FORMAT label's own duration / master-class hint instead of defaulting to 60.
    [InlineData("Technical Session (60 min)", SessionLength.SixtyMin)]
    [InlineData("Technical Session (morning 07:20-08:10 - 50 min)", SessionLength.FiftyMin)]
    [InlineData("Lightning (20 min)", SessionLength.TwentyMin)]
    [InlineData("Master Class", SessionLength.FullDay)]
    [InlineData("Master Class | Azure | Expert (400)", SessionLength.FullDay)]
    [InlineData("Hands-on Workshop", SessionLength.FullDay)]
    [InlineData("Security", SessionLength.SixtyMin)]   // no duration + not a master class → safe default
    [InlineData(null, SessionLength.SixtyMin)]
    public void MapLength_from_category_when_untimed(string? category, SessionLength expected)
    {
        Assert.Equal(expected, SessionDefaultsMapper.MapLength(null, null, category));
    }

    [Fact]
    public void MapLength_prefers_real_times_over_label()
    {
        // When the grid IS published, the actual start/end wins over the label's "(60 min)".
        Assert.Equal(SessionLength.TwentyMin,
            SessionDefaultsMapper.MapLength(Now, Now.AddMinutes(20), "Technical Session (60 min)"));
    }

    // -------------------------------------------- numeric LengthMinutes (§154) --

    [Theory]
    [InlineData("Technical Session (60 min)", 60)]
    [InlineData("Technical Session (morning 07:20-08:10 - 50 min)", 50)] // last "min" wins, not the clock
    [InlineData("Lightning (20 min)", 20)]
    [InlineData("Master Class", null)]   // no "(NN min)" → full-day, no numeric figure
    [InlineData("Security", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParseLengthMinutes_from_format_label(string? category, int? expected)
    {
        Assert.Equal(expected, SessionDefaultsMapper.ParseLengthMinutes(category));
    }

    [Fact]
    public void MapLengthMinutes_prefers_real_times_over_label()
    {
        // Grid published → exact duration from start/end (not the label's "(60 min)").
        Assert.Equal(45,
            SessionDefaultsMapper.MapLengthMinutes(Now, Now.AddMinutes(45), "Technical Session (60 min)"));
        // Untimed → fall back to the label's "(NN min)".
        Assert.Equal(60,
            SessionDefaultsMapper.MapLengthMinutes(null, null, "Technical Session (60 min)"));
        // Untimed master class → null (full-day handled by the Length bucket).
        Assert.Null(SessionDefaultsMapper.MapLengthMinutes(null, null, "Master Class"));
    }

    [Fact]
    public async Task Import_persists_track_level_and_length_minutes()
    {
        using var db = TestDb.New();
        var eventId = await SeedEventAsync(db);
        var svc = new SessionImportService(db, new FixedClock(Now));

        // A source session carrying a clean Track, a Level and numeric minutes
        // (as the parser now produces from the Sessionize category groups).
        var src = new SessionizeSession(
            "s-tl", "Securing Identity", null, "Room A", "Security",
            null, null, false, Array.Empty<string>(),
            Category: "Technical Session (60 min)", Level: "Expert (400)", LengthMinutes: 60);
        await svc.ImportSessionsAsync(eventId, new[] { src },
            Array.Empty<SessionizeSpeaker>(), Array.Empty<string>());

        var stored = await db.Sessions.SingleAsync(s => s.SessionizeId == "s-tl");
        Assert.Equal("Security", stored.Track);
        Assert.Equal("Expert (400)", stored.Level);
        Assert.Equal(60, stored.LengthMinutes);
        Assert.Equal(SessionType.TechnicalSession, stored.Type);
    }

    // ------------------------------------------------------- import behaviour ---

    private static async Task<int> SeedEventAsync(CommunityHubDbContext db)
    {
        var evt = new Event
        {
            Code = "MAP27", CommunityName = "Map", DisplayName = "Map 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10), IsActive = true,
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();
        return evt.Id;
    }

    private static SessionizeSession Src(string id, string title, string? category,
        DateTimeOffset? start = null, DateTimeOffset? end = null) =>
        new(id, title, null, "Room A", null, start, end, false, Array.Empty<string>(), category);

    [Fact]
    public async Task Import_maps_type_from_category()
    {
        using var db = TestDb.New();
        var eventId = await SeedEventAsync(db);
        var svc = new SessionImportService(db, new FixedClock(Now));

        var sessions = new[]
        {
            Src("s-key", "Big Picture", "Keynote"),
            Src("s-mc", "Build It", "Master Class"),
            Src("s-ate", "Clinic", "Ask the Experts"),
            Src("s-plain", "Some Talk", null, Now, Now.AddMinutes(50)),
        };
        await svc.ImportSessionsAsync(eventId, sessions,
            Array.Empty<SessionizeSpeaker>(), Array.Empty<string>());

        Assert.Equal(SessionType.Keynote, (await db.Sessions.SingleAsync(s => s.SessionizeId == "s-key")).Type);
        Assert.Equal(SessionType.MasterClass, (await db.Sessions.SingleAsync(s => s.SessionizeId == "s-mc")).Type);
        Assert.Equal(SessionType.AskTheExperts, (await db.Sessions.SingleAsync(s => s.SessionizeId == "s-ate")).Type);
        Assert.Equal(SessionType.TechnicalSession, (await db.Sessions.SingleAsync(s => s.SessionizeId == "s-plain")).Type);
    }

    [Fact]
    public async Task Import_maps_length_from_format_label_when_untimed()
    {
        using var db = TestDb.New();
        var eventId = await SeedEventAsync(db);
        var svc = new SessionImportService(db, new FixedClock(Now));

        // No start/end (grid unpublished) — length must come from the format label, not default to 60.
        var sessions = new[]
        {
            Src("s-mc", "Azure Master Class", "Master Class | Azure | Expert (400)"),
            Src("s-50", "Morning Talk", "Technical Session (morning 07:20-08:10 - 50 min)"),
            Src("s-60", "Welcome", "Technical Session (60 min)"),
        };
        await svc.ImportSessionsAsync(eventId, sessions,
            Array.Empty<SessionizeSpeaker>(), Array.Empty<string>());

        Assert.Equal(SessionLength.FullDay, (await db.Sessions.SingleAsync(s => s.SessionizeId == "s-mc")).Length);
        Assert.Equal(SessionLength.FiftyMin, (await db.Sessions.SingleAsync(s => s.SessionizeId == "s-50")).Length);
        Assert.Equal(SessionLength.SixtyMin, (await db.Sessions.SingleAsync(s => s.SessionizeId == "s-60")).Length);
    }

    [Fact]
    public async Task Manual_type_override_survives_reimport()
    {
        using var db = TestDb.New();
        var eventId = await SeedEventAsync(db);
        var import = new SessionImportService(db, new FixedClock(Now));
        var mgmt = new SessionManagementService(db, new NullRoomQrProvider(), new FixedClock(Now));

        // First import: the source says "Keynote".
        await import.ImportSessionsAsync(eventId, new[] { Src("s-1", "Mislabelled", "Keynote") },
            Array.Empty<SessionizeSpeaker>(), Array.Empty<string>());
        var s = await db.Sessions.SingleAsync(x => x.SessionizeId == "s-1");
        Assert.Equal(SessionType.Keynote, s.Type);
        Assert.False(s.TypeIsManualOverride);

        // Organizer manually corrects it to a Panel Discussion (sets the override flag).
        await mgmt.UpdateSessionAsync(eventId, s.Id, SessionType.PanelDiscussion, s.Length, s.Room, null);
        s = await db.Sessions.SingleAsync(x => x.SessionizeId == "s-1");
        Assert.True(s.TypeIsManualOverride);
        Assert.Equal(SessionType.PanelDiscussion, s.Type);

        // Re-import (same source category) must NOT clobber the manual override...
        await import.ImportSessionsAsync(eventId, new[] { Src("s-1", "Mislabelled", "Keynote") },
            Array.Empty<SessionizeSpeaker>(), Array.Empty<string>());
        s = await db.Sessions.SingleAsync(x => x.SessionizeId == "s-1");
        Assert.Equal(SessionType.PanelDiscussion, s.Type);

        // ...but a session WITHOUT a manual override is still refreshed from the source.
        await import.ImportSessionsAsync(eventId, new[] { Src("s-2", "Auto", "Master Class") },
            Array.Empty<SessionizeSpeaker>(), Array.Empty<string>());
        Assert.Equal(SessionType.MasterClass,
            (await db.Sessions.SingleAsync(x => x.SessionizeId == "s-2")).Type);
    }
}
