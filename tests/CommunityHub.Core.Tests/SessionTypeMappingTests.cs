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
