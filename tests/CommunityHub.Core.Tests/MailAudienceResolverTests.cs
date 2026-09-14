using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1080 stage 1 — the twelve audiences the operator listed (2026-08-12).
/// </summary>
/// <remarks>
/// <para>🔴 <b>These definitions decide who receives a mass mailing</b>, so each one is asserted as
/// a SET rather than a count: "eleven people" is true of the right eleven and the wrong eleven
/// alike.</para>
///
/// <para>🔒 Nothing here sends. Stage 1 is deliberately the argument about who belongs in each
/// audience, held before anything can leave the building.</para>
/// </remarks>
public sealed class MailAudienceResolverTests
{
    private const int EventId = 42;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"audience-{Guid.NewGuid():N}").Options);

    private static Participant P(
        string email, ParticipantRole role, bool active = true, bool test = false,
        string? sponsorCompanyId = null) => new()
    {
        EventId = EventId, Email = email, FullName = email.Split('@')[0], Role = role,
        IsActive = active,
        LifecycleState = active ? ParticipantLifecycleState.Active : ParticipantLifecycleState.Inactive,
        IsTestUser = test, SponsorCompanyId = sponsorCompanyId,
    };

    private static Attendee A(string email, TicketStatus ticket = TicketStatus.TwoDay,
        MirrorState state = MirrorState.Active) => new()
    {
        EventId = EventId, Email = email, FullName = email.Split('@')[0],
        BackstageTicketId = Guid.NewGuid().ToString("N"), TicketStatus = ticket, MirrorState = state,
    };

    private static async Task<CommunityHubDbContext> SeedAsync()
    {
        var db = NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, Code = "ELDK27", CommunityName = "C", DisplayName = "D", IsActive = true,
        });

        db.Participants.AddRange(
            P("speaker@x.test", ParticipantRole.Speaker),
            P("mcspeaker@x.test", ParticipantRole.Speaker),
            P("volunteer@x.test", ParticipantRole.Volunteer),
            P("media@x.test", ParticipantRole.Media),
            P("partner@x.test", ParticipantRole.EventPartner),
            P("booth@x.test", ParticipantRole.Sponsor, sponsorCompanyId: "CO-EXHIBITOR"),
            P("digital@x.test", ParticipantRole.Sponsor, sponsorCompanyId: "CO-DIGITAL"),
            // ⚠️ Neither of these may ever appear: one left, one is a test row.
            P("left@x.test", ParticipantRole.Volunteer, active: false),
            P("testuser@x.test", ParticipantRole.Volunteer, test: true));

        db.SponsorInfos.AddRange(
            new SponsorInfo { EventId = EventId, SponsorCompanyId = "CO-EXHIBITOR", IsExhibitor = true },
            new SponsorInfo { EventId = EventId, SponsorCompanyId = "CO-DIGITAL", IsExhibitor = false });

        db.Attendees.AddRange(
            A("two@x.test"),
            A("one@x.test", TicketStatus.Other),
            // A cancelled ticket is not an attendee (§128 keeps the row with its status intact).
            A("cancelled@x.test", TicketStatus.TwoDay, MirrorState.Cancelled));

        db.ExternalRecipients.AddRange(
            new ExternalRecipient { EventId = EventId, Email = "old-friend@x.test", FullName = "Old Friend" },
            // 🔴 This one is BOTH on the imported list and attending now.
            new ExternalRecipient { EventId = EventId, Email = "two@x.test", FullName = "Two Dayer" });

        await db.SaveChangesAsync();
        return db;
    }

    private static async Task<string[]> ResolveAsync(CommunityHubDbContext db, MailAudience audience) =>
        (await new MailAudienceResolver(db).ResolveAsync(EventId, audience))
        .Select(r => r.Email).OrderBy(e => e).ToArray();

    [Fact]
    public async Task Role_audiences_are_exactly_their_role()
    {
        using var db = await SeedAsync();

        Assert.Equal(new[] { "media@x.test" }, await ResolveAsync(db, MailAudience.Media));
        Assert.Equal(new[] { "partner@x.test" }, await ResolveAsync(db, MailAudience.EventPartners));
        Assert.Equal(new[] { "volunteer@x.test" }, await ResolveAsync(db, MailAudience.Volunteers));
        Assert.Equal(
            new[] { "mcspeaker@x.test", "speaker@x.test" },
            await ResolveAsync(db, MailAudience.Speakers));
    }

    /// <summary>
    /// 🔴 A deactivated person left, and a test row is not a person. A campaign is not the moment to
    /// rediscover either.
    /// </summary>
    [Fact]
    public async Task Deactivated_and_test_participants_are_never_included()
    {
        using var db = await SeedAsync();

        var all = await ResolveAsync(db, MailAudience.AllParticipants);

        Assert.DoesNotContain("left@x.test", all);
        Assert.DoesNotContain("testuser@x.test", all);
        Assert.Contains("speaker@x.test", all);
    }

    /// <summary>
    /// §1034 — "exhibitor" is a flag on the sponsor COMPANY, not a role. A digital-only sponsor must
    /// not receive booth logistics.
    /// </summary>
    [Fact]
    public async Task Exhibitors_only_excludes_the_digital_only_sponsor()
    {
        using var db = await SeedAsync();

        Assert.Equal(new[] { "booth@x.test" }, await ResolveAsync(db, MailAudience.ExhibitorsOnly));
        Assert.Equal(
            new[] { "booth@x.test", "digital@x.test" },
            await ResolveAsync(db, MailAudience.SponsorsAndExhibitors));
    }

    [Fact]
    public async Task Ticket_audiences_split_by_ticket_and_ignore_cancellations()
    {
        using var db = await SeedAsync();

        Assert.Equal(new[] { "two@x.test" }, await ResolveAsync(db, MailAudience.TwoDayTicketHolders));
        Assert.Equal(new[] { "one@x.test" }, await ResolveAsync(db, MailAudience.OneDayTicketHolders));
        Assert.Equal(
            new[] { "one@x.test", "two@x.test" },
            await ResolveAsync(db, MailAudience.AllAttendees));
    }

    /// <summary>
    /// 🔴 The twelfth audience, and the whole reason campaigns needed their own switch: the imported
    /// list MINUS this edition's attendees. Somebody who bought a ticket yesterday drops out today,
    /// with nobody editing a list.
    /// </summary>
    [Fact]
    public async Task Previous_attendees_excludes_anyone_attending_this_edition()
    {
        using var db = await SeedAsync();

        var previous = await ResolveAsync(db, MailAudience.PreviousAttendeesNotThisEdition);

        Assert.Equal(new[] { "old-friend@x.test" }, previous);
        Assert.DoesNotContain("two@x.test", previous);
    }

    /// <summary>🔒 One address, one mail — whichever route found them.</summary>
    [Fact]
    public async Task An_address_appearing_twice_is_returned_once()
    {
        using var db = await SeedAsync();
        // The same person as a speaker AND (by address) an attendee.
        db.Attendees.Add(A("speaker@x.test"));
        await db.SaveChangesAsync();

        var all = await ResolveAsync(db, MailAudience.AllParticipants);

        Assert.Single(all, e => e == "speaker@x.test");
    }

    /// <summary>
    /// 🔴 Exactly ONE audience reaches people who are not participants — the one the ring gate would
    /// refuse. Asserted so a new audience cannot quietly join that category.
    /// </summary>
    [Fact]
    public void Only_the_previous_attendees_audience_reaches_outside_the_hub()
    {
        foreach (var a in Enum.GetValues<MailAudience>())
        {
            Assert.Equal(
                a == MailAudience.PreviousAttendeesNotThisEdition,
                MailAudienceResolver.ReachesOutsideTheHub(a));
        }
    }

    /// <summary>Every audience has a sentence an organizer can read before choosing it.</summary>
    [Fact]
    public void Every_audience_describes_itself()
    {
        foreach (var a in Enum.GetValues<MailAudience>())
        {
            var text = MailAudienceResolver.Describe(a);
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.NotEqual(a.ToString(), text);   // not just the enum name
        }
    }

    /// <summary>An empty edition resolves to nothing rather than throwing.</summary>
    [Fact]
    public async Task An_empty_edition_yields_no_recipients()
    {
        using var db = NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, Code = "T", CommunityName = "C", DisplayName = "D", IsActive = true,
        });
        await db.SaveChangesAsync();

        foreach (var a in Enum.GetValues<MailAudience>())
            Assert.Empty(await ResolveAsync(db, a));
    }
}
