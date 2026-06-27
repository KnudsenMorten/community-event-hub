using CommunityHub.Assistant;
using CommunityHub.Core.Assistant;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using CommunityHub.Core.Participants;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Sponsors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// End-to-end role-gating for the AI Community Helper's ORGANIZER OPS MODE (REQUIREMENTS §133),
/// exercising the REAL <see cref="WebAiHelperOrganizerOpsProvider"/> over the EF in-memory
/// provider through the real <see cref="AiHelperGroundingBuilder"/>. Proves:
///   • an ORGANIZER's grounding gains the curated ops aggregates — speaker readiness /
///     missing slides (§134), sponsor missing deliverables (§135), master-class
///     non-selections (§6), and participation / attendee counts (§11);
///   • a NON-organizer's grounding does NOT — the provider is never invoked for them (the
///     gate lives in the builder, keyed on the SERVER-resolved role, never the prompt).
/// The aggregates are curated typed queries (no raw / text-to-SQL). FAKE names only.
/// </summary>
public sealed class AiHelperOrganizerOpsTests
{
    private const int EventId = 1;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"ai-helper-ops-{Guid.NewGuid():N}")
            .Options);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-06-27T10:00:00Z");
    }

    // A content provider that returns nothing, so the only ops content in the grounding comes
    // from the real organizer-ops provider (keeps the assertions unambiguous).
    private sealed class NoContentProvider : IAiHelperContentProvider
    {
        public string? GetContentMarkdown(string slug) => null;
    }

    private static WebAiHelperOrganizerOpsProvider NewOps(CommunityHubDbContext db)
    {
        var clock = new FixedClock();
        return new WebAiHelperOrganizerOpsProvider(
            db,
            new OrganizerOverviewService(db, clock),
            new SpeakerReadinessService(db),
            new SponsorDeliverablesService(db),
            new MasterClassSignupService(db),
            clock,
            NullLogger<WebAiHelperOrganizerOpsProvider>.Instance);
    }

    private static AiHelperGroundingBuilder NewBuilder(CommunityHubDbContext db) =>
        new(new NoContentProvider(),
            new WebAiHelperOwnDataProvider(db, new FixedClock(), new SponsorDeliverablesService(db)),
            NewOps(db));

    private static async Task SeedAsync(CommunityHubDbContext db)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "ELDK27", DisplayName = "Test Edition", CommunityName = "Test Community",
            StartDate = new DateOnly(2026, 9, 1), EndDate = new DateOnly(2026, 9, 2),
        });

        // A fresh speaker (nothing uploaded) → "missing slides".
        var speaker = new Participant
        {
            Id = 10, EventId = EventId, Email = "spk@x.test", FullName = "Spe Aker",
            Role = ParticipantRole.Speaker, IsActive = true,
        };
        db.Participants.Add(speaker);
        db.SpeakerProfiles.Add(new SpeakerProfile { EventId = EventId, ParticipantId = 10 });

        // An organizer + a volunteer (the two callers under test).
        db.Participants.Add(new Participant
        {
            Id = 1, EventId = EventId, Email = "org@x.test", FullName = "Or Ganizer",
            Role = ParticipantRole.Organizer, IsActive = true,
        });
        db.Participants.Add(new Participant
        {
            Id = 2, EventId = EventId, Email = "vol@x.test", FullName = "Vol Un",
            Role = ParticipantRole.Volunteer, IsActive = true,
        });

        // An exhibitor sponsor (Gold ⇒ HasBooth) with no booth materials → missing deliverable.
        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = EventId, SponsorCompanyId = "501", SponsorPackage = SponsorPackage.Gold,
            EventCoordinatorCompanyName = "Acme Sponsor",
        });

        // Two Master-Class-eligible (2-day) attendees; only one has made a selection.
        db.Attendees.Add(new Attendee
        {
            Id = 90, EventId = EventId, Email = "a1@x.test", FullName = "Att Endee One",
            TicketStatus = TicketStatus.TwoDay, MirrorState = MirrorState.Active,
        });
        db.Attendees.Add(new Attendee
        {
            Id = 91, EventId = EventId, Email = "a2@x.test", FullName = "Att Endee Two",
            TicketStatus = TicketStatus.TwoDay, MirrorState = MirrorState.Active,
        });
        db.MasterClassSignups.Add(new MasterClassSignup
        {
            EventId = EventId, SessionId = 1, AttendeeId = 90,
            Status = MasterClassSignupStatus.Confirmed,
        });

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Organizer_grounding_includes_curated_ops_aggregates()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var ctx = await NewBuilder(db).BuildAsync(
            EventId, participantId: 1, role: ParticipantRole.Organizer);
        var text = ctx.ToGroundingText();

        // §11 participation / attendee counts.
        Assert.Contains("Event ops overview (organizer)", text);
        Assert.Contains("Attendees (from Zoho orders): 2", text);
        // §134 speaker missing slides — names the speaker who hasn't uploaded.
        Assert.Contains("have not uploaded slides", text);
        Assert.Contains("Spe Aker", text);
        // §135 sponsor missing deliverables — names the exhibitor missing booth materials.
        Assert.Contains("missing booth materials", text);
        Assert.Contains("Acme Sponsor", text);
        // §6 master-class non-selections (1 of 2 eligible has not selected).
        Assert.Contains("Non-selections remaining: 1", text);
    }

    [Fact]
    public async Task Volunteer_grounding_excludes_all_ops_aggregates()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var ctx = await NewBuilder(db).BuildAsync(
            EventId, participantId: 2, role: ParticipantRole.Volunteer);
        var text = ctx.ToGroundingText();

        // None of the organizer-only ops sections leak to a volunteer.
        Assert.DoesNotContain("Event ops overview (organizer)", text);
        Assert.DoesNotContain("have not uploaded slides", text);
        Assert.DoesNotContain("missing booth materials", text);
        Assert.DoesNotContain("Non-selections remaining", text);
        Assert.DoesNotContain("Acme Sponsor", text);
    }

    [Fact]
    public async Task Ops_provider_directly_produces_named_sections()
    {
        using var db = NewDb();
        await SeedAsync(db);

        var sections = await NewOps(db).GetOpsAggregatesAsync(EventId);

        Assert.Contains(sections, s => s.Heading == "Event ops overview (organizer)");
        Assert.Contains(sections, s => s.Heading == "Speaker readiness (organizer)");
        Assert.Contains(sections, s => s.Heading == "Sponsor deliverables (organizer)");
        Assert.Contains(sections, s => s.Heading == "Master Class selections (organizer)");
    }
}
