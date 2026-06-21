using CommunityHub.Auth;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using CommunityHub.Pages;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// Public-site [L]/[M] polish (REQUIREMENTS §21): the MasterClass + Survey-results
/// timestamps now read in the EVENT's local time (not raw UTC), and the
/// Contributors page renders each person's role/photo/initials. Drives the real
/// page models / pure helpers over a fake DB + a temp edition-config file with a
/// known timezone. FAKE names only.
/// </summary>
public sealed class PublicSitePolishTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    /// <summary>An accessor for the anonymous read path — no current participant.</summary>
    private sealed class AnonymousAccessor : ICurrentParticipantAccessor
    {
        public CurrentParticipant? Current => null;
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"polish-{Guid.NewGuid():N}")
            .Options);

    /// <summary>Write a minimal event config JSON carrying a dates.timezone, return its path.</summary>
    private EventConfigOptions ConfigWithTimezone(string ianaZone)
    {
        var path = Path.Combine(Path.GetTempPath(), $"event-{Guid.NewGuid():N}.json");
        File.WriteAllText(path,
            $$"""{ "edition": { "code": "TST" }, "dates": { "timezone": "{{ianaZone}}" } }""");
        _tempFiles.Add(path);
        return new EventConfigOptions { EventConfigPath = path };
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
        {
            try { File.Delete(f); } catch { /* best-effort temp cleanup */ }
        }
    }

    // --- MasterClass "last updated" in local time --------------------------

    [Fact]
    public async Task MasterClass_last_updated_renders_in_event_local_time_not_utc()
    {
        using var db = NewDb();
        // A winter UTC instant so Copenhagen is a deterministic UTC+1 (no DST).
        var updated = new DateTimeOffset(2027, 2, 3, 13, 0, 0, TimeSpan.Zero);

        db.Events.Add(new Event { Id = 1, Code = "TST", CommunityName = "Test", IsActive = true });
        db.Sessions.Add(new Session
        {
            Id = 10, EventId = 1, Title = "Hands-on Master Class",
            Type = SessionType.CommunityMasterClass, PublicSlug = "mc-abc",
            LogisticsText = "Bring a charged laptop.", LogisticsUpdatedAt = updated,
        });
        await db.SaveChangesAsync();

        // The clock is only used on write paths; the public read path never reads it.
        var logistics = new MasterClassLogisticsService(db, TimeProvider.System);
        var model = new CommunityHub.Pages.MasterClass.IndexModel(
            logistics,
            new AnonymousAccessor(),            // anonymous read path only
            new EventEditionConfigLoader(),
            ConfigWithTimezone("Europe/Copenhagen"),
            NullLogger<CommunityHub.Pages.MasterClass.IndexModel>.Instance);

        var result = await model.OnGetAsync("mc-abc", default);

        Assert.IsType<PageResult>(result);
        Assert.Equal("Europe/Copenhagen", model.TimezoneId);
        // 13:00Z ⇒ 14:00 local, zone suffix present, never the raw "13:00 ... UTC".
        Assert.Equal("2027-02-03 14:00 UTC+01:00", model.UpdatedLocal);
    }

    [Fact]
    public async Task MasterClass_falls_back_to_utc_when_config_has_no_timezone()
    {
        using var db = NewDb();
        var updated = new DateTimeOffset(2027, 2, 3, 13, 0, 0, TimeSpan.Zero);

        db.Events.Add(new Event { Id = 1, Code = "TST", CommunityName = "Test", IsActive = true });
        db.Sessions.Add(new Session
        {
            Id = 10, EventId = 1, Title = "MC", Type = SessionType.CommunityMasterClass,
            PublicSlug = "mc-xyz", LogisticsUpdatedAt = updated,
        });
        await db.SaveChangesAsync();

        // Point at a non-existent config path ⇒ loader returns empty ⇒ blank zone.
        var model = new CommunityHub.Pages.MasterClass.IndexModel(
            new MasterClassLogisticsService(db, TimeProvider.System),
            new AnonymousAccessor(),
            new EventEditionConfigLoader(),
            new EventConfigOptions { EventConfigPath = "does/not/exist.json" },
            NullLogger<CommunityHub.Pages.MasterClass.IndexModel>.Instance);

        await model.OnGetAsync("mc-xyz", default);

        Assert.Equal("2027-02-03 13:00 UTC", model.UpdatedLocal);
    }

    // --- Contributors page roles / photos / initials -----------------------

    [Fact]
    public void Contributors_initials_handles_one_two_and_blank_names()
    {
        Assert.Equal("AB", ContributorsModel.Initials("Alpha Bravo"));
        Assert.Equal("AC", ContributorsModel.Initials("Alpha Bravo Charlie")); // first + last
        Assert.Equal("A", ContributorsModel.Initials("Alpha"));
        Assert.Equal("?", ContributorsModel.Initials("   "));
        Assert.Equal("?", ContributorsModel.Initials(null));
    }

    [Fact]
    public void Contributors_model_carries_a_role_for_every_person()
    {
        var model = new ContributorsModel();

        Assert.NotEmpty(model.Organizers);
        Assert.All(model.Organizers, c => Assert.False(string.IsNullOrWhiteSpace(c.Role)));
        Assert.All(model.Contributors, c => Assert.False(string.IsNullOrWhiteSpace(c.Role)));
        // Photo is optional (null today) so the avatar falls back to initials.
        Assert.All(model.Organizers, c => Assert.Null(c.PhotoUrl));
    }

    [Fact]
    public void Contributors_record_accepts_an_optional_photo_url_without_schema_change()
    {
        var withPhoto = new ContributorsModel.Contributor(
            "Test Person", "Helper", LinkedIn: null, Tag: "Contributor",
            PhotoUrl: "https://example.test/headshot.jpg");

        Assert.Equal("https://example.test/headshot.jpg", withPhoto.PhotoUrl);
    }
}
