using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Tests;

/// <summary>A TimeProvider whose "now" can be set by the test.</summary>
internal sealed class FixedClock : TimeProvider
{
    private DateTimeOffset _now;
    public FixedClock(DateTimeOffset now) => _now = now;
    public void Set(DateTimeOffset now) => _now = now;
    public override DateTimeOffset GetUtcNow() => _now;
}

internal static class TestDb
{
    /// <summary>Fresh in-memory DbContext, unique store per call.</summary>
    public static CommunityHubDbContext New()
    {
        var options = new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"aq-tests-{Guid.NewGuid():N}")
            .EnableSensitiveDataLogging()
            .Options;
        return new CommunityHubDbContext(options);
    }

    /// <summary>Seed one event (with optional lock date) + one participant; returns their ids.</summary>
    public static async Task<(int eventId, int participantId)> SeedEventAndPersonAsync(
        CommunityHubDbContext db, DateOnly? lockDate)
    {
        var evt = new Event
        {
            Code = "TEST27",
            CommunityName = "Test Community",
            DisplayName = "Test Community 2027",
            StartDate = new DateOnly(2027, 2, 9),
            EndDate = new DateOnly(2027, 2, 10),
            IsActive = true,
            LockDate = lockDate,
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();

        var person = new Participant
        {
            EventId = evt.Id,
            FullName = "Test Person",
            Email = "person@example.test",
            Role = ParticipantRole.Speaker,
            IsActive = true,
        };
        db.Participants.Add(person);
        await db.SaveChangesAsync();

        return (evt.Id, person.Id);
    }
}
