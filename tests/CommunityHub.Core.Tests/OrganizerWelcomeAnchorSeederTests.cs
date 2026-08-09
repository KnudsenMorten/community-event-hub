using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §994 — *"organizers should not get welcome mail but seed the field, so he is chased if not
/// filled out"* (operator 2026-08-09).
/// </summary>
/// <remarks>
/// 🔴 The dead end: <c>WelcomeVariants.TemplateKeyFor</c> returns null for Organizer (no welcome, by
/// design since 2026-06-22) and §738's digest gate is *never welcomed ⇒ never chased*. Together they
/// made an organizer permanently unchaseable — which is how the operator found it: he is one, he had
/// filled in nothing, and nothing ever reminded him. §985a fixed the four that existed **by hand**;
/// this makes it self-maintaining so the next organizer added is not silently unchaseable again.
/// </remarks>
public sealed class OrganizerWelcomeAnchorSeederTests
{
    private const int EventId = 1;
    private static readonly DateTimeOffset Created = new(2026, 6, 15, 8, 0, 0, TimeSpan.Zero);

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"org-anchor-{Guid.NewGuid():N}").Options);

    private static Participant P(
        ParticipantRole role, DateTimeOffset? anchor = null, bool active = true,
        DateTimeOffset? created = null) =>
        new()
        {
            EventId = EventId,
            Email = $"{role}-{Guid.NewGuid():N}@example.test",
            FullName = "A B",
            Role = role,
            IsActive = active,
            CreatedAt = created ?? Created,
            WelcomeWithLoginSentAt = anchor,
        };

    [Fact]
    public async Task An_unanchored_organizer_is_seeded_from_CreatedAt()
    {
        using var db = NewDb();
        db.Participants.Add(P(ParticipantRole.Organizer));
        await db.SaveChangesAsync();

        Assert.Equal(1, await new OrganizerWelcomeAnchorSeeder(db).RunAsync(EventId));

        // 🔒 CreatedAt, NOT "now" — §985a. Anchoring at "now" would restart the 7-day cadence for
        // somebody who has been in the system for months and delay their first chase by a week.
        Assert.Equal(Created, db.Participants.Single().WelcomeWithLoginSentAt);
    }

    /// <summary>
    /// ⚠️ Only ever fills a NULL. Overwriting would reset the cadence on every 5-minute run and
    /// re-chase people who were just chased.
    /// </summary>
    [Fact]
    public async Task An_existing_anchor_is_never_overwritten()
    {
        var existing = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        using var db = NewDb();
        db.Participants.Add(P(ParticipantRole.Organizer, anchor: existing));
        await db.SaveChangesAsync();

        Assert.Equal(0, await new OrganizerWelcomeAnchorSeeder(db).RunAsync(EventId));
        Assert.Equal(existing, db.Participants.Single().WelcomeWithLoginSentAt);
    }

    /// <summary>
    /// 🔒 ORGANIZERS ONLY. Every other role has a real welcome mail that stamps this field when it
    /// is actually sent; seeding one here would fake a welcome that never went out and start
    /// chasing somebody who has never been given their sign-in link.
    /// </summary>
    [Theory]
    [InlineData(ParticipantRole.Speaker)]
    [InlineData(ParticipantRole.Sponsor)]
    [InlineData(ParticipantRole.Volunteer)]
    [InlineData(ParticipantRole.Attendee)]
    [InlineData(ParticipantRole.Media)]
    [InlineData(ParticipantRole.EventPartner)]
    public async Task No_other_role_is_touched(ParticipantRole role)
    {
        using var db = NewDb();
        db.Participants.Add(P(role));
        await db.SaveChangesAsync();

        Assert.Equal(0, await new OrganizerWelcomeAnchorSeeder(db).RunAsync(EventId));
        Assert.Null(db.Participants.Single().WelcomeWithLoginSentAt);
    }

    [Fact]
    public async Task An_inactive_organizer_is_not_seeded()
    {
        using var db = NewDb();
        db.Participants.Add(P(ParticipantRole.Organizer, active: false));
        await db.SaveChangesAsync();

        Assert.Equal(0, await new OrganizerWelcomeAnchorSeeder(db).RunAsync(EventId));
        Assert.Null(db.Participants.Single().WelcomeWithLoginSentAt);
    }

    [Fact]
    public async Task Another_editions_organizer_is_not_touched()
    {
        using var db = NewDb();
        var other = P(ParticipantRole.Organizer);
        other.EventId = 99;
        db.Participants.Add(other);
        await db.SaveChangesAsync();

        Assert.Equal(0, await new OrganizerWelcomeAnchorSeeder(db).RunAsync(EventId));
        Assert.Null(db.Participants.Single().WelcomeWithLoginSentAt);
    }

    /// <summary>Idempotent — it runs before every digest pass (every 5 minutes).</summary>
    [Fact]
    public async Task Running_twice_seeds_once()
    {
        using var db = NewDb();
        db.Participants.Add(P(ParticipantRole.Organizer));
        await db.SaveChangesAsync();

        var seeder = new OrganizerWelcomeAnchorSeeder(db);
        Assert.Equal(1, await seeder.RunAsync(EventId));
        Assert.Equal(0, await seeder.RunAsync(EventId));
    }
}
