using System.Net;
using System.Text;
using CommunityHub.Core.Audit;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Organizer;
using CommunityHub.Core.Settings;
using CommunityHub.Core.Tests.Scenario;
using CommunityHub.Core.Volunteers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §253 residual fixes (2026-07-08 doc-validation pass) — the gaps the scenario
/// matrix left open after G1–G16 landed:
///   • G7 completion — deactivating a volunteer SEEDS backfill drafts into the
///     acting organizer's allocation queue (proposals only, review-then-commit);
///   • silent-resurrection signal — re-activation reports the dormant dinner /
///     lunch / swag rows that silently rejoin the live counts;
///   • V6 — the draft→commit path re-checks IsActive (a target who dropped out
///     after being queued never becomes a ghost assignment) and AddDraft refuses
///     a deactivated volunteer outright;
///   • G17 email normalization — the sponsor contact sync stores lowercased,
///     trimmed emails (raw CM casing used to feed the Sessionize import's
///     in-memory dedup a key it could miss), and the Sessionize import matches
///     existing participants case-insensitively;
///   • G8d — refunded/cancelled Woo orders (invisible before: the pull reads
///     status=completed only) surface as ONE organizer action-queue item per
///     order, deduped forever by order id.
/// FAKE names/domains throughout.
/// </summary>
public sealed class Scenario253ResidualFixesTests
{
    private const int EventId = 1;
    private static readonly DateTimeOffset Now = new(2026, 7, 8, 9, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"residual253-{Guid.NewGuid():N}")
            .Options);

    private static VolunteerAllocationService Allocation(CommunityHubDbContext db) =>
        new(db, new FixedClock(), new FeatureGateService(db), new RingResolver(db));

    private static ParticipantDeactivationService Cascade(
        CommunityHubDbContext db, VolunteerAllocationService? allocation = null) =>
        new(db, new FixedClock(), new AuditTrailService(db, new FixedClock()), allocation);

    private static async Task SeedEventAsync(CommunityHubDbContext db)
    {
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "C 2027", Code = "C27",
            IsActive = true, StartDate = new DateOnly(2027, 2, 9),
        });
        await db.SaveChangesAsync();
    }

    private static async Task<Participant> SeedPersonAsync(
        CommunityHubDbContext db, ParticipantRole role, string email)
    {
        var p = new Participant
        {
            EventId = EventId, Email = email, FullName = email.Split('@')[0],
            Role = role, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p;
    }

    private static async Task<VolunteerTask> SeedShiftAsync(
        CommunityHubDbContext db, int resourcesNeeded = 1)
    {
        var cat = new VolunteerCategory { EventId = EventId, Name = "Registration" };
        db.VolunteerCategories.Add(cat);
        await db.SaveChangesAsync();
        var sub = new VolunteerSubcategory { EventId = EventId, CategoryId = cat.Id, Name = "Desk" };
        db.VolunteerSubcategories.Add(sub);
        await db.SaveChangesAsync();
        var shift = new VolunteerTask
        {
            EventId = EventId, SubcategoryId = sub.Id, Title = "Desk shift",
            ResourcesNeeded = resourcesNeeded,
        };
        db.VolunteerTasks.Add(shift);
        await db.SaveChangesAsync();
        return shift;
    }

    // ----- G7 completion: cascade seeds backfill drafts -----------------------

    [Fact]
    public async Task Deactivating_a_volunteer_seeds_backfill_drafts_for_the_acting_organizer()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        var org = await SeedPersonAsync(db, ParticipantRole.Organizer, "org@example.test");
        var leaver = await SeedPersonAsync(db, ParticipantRole.Volunteer, "leaver@example.test");
        var candidate = await SeedPersonAsync(db, ParticipantRole.Volunteer, "candidate@example.test");
        var shift = await SeedShiftAsync(db);
        db.VolunteerTaskAssignments.Add(new VolunteerTaskAssignment
        {
            EventId = EventId, TaskId = shift.Id, ParticipantId = leaver.Id,
        });
        await db.SaveChangesAsync();

        var result = await Cascade(db, Allocation(db)).DeactivateAsync(
            EventId, leaver.Id, "test", org.Email);

        Assert.Equal(1, result.ShiftAssignmentsVacated);
        Assert.Equal(1, result.BackfillDraftsSeeded);

        // A DRAFT proposal in the acting organizer's queue — never a real assignment.
        var draft = await db.TaskAllocationDrafts.SingleAsync();
        Assert.Equal(org.Id, draft.OwnerParticipantId);
        Assert.Equal(shift.Id, draft.TaskId);
        Assert.Equal(candidate.Id, draft.ParticipantId);
        Assert.Empty(await db.VolunteerTaskAssignments.ToListAsync());
    }

    [Fact]
    public async Task Cascade_skips_backfill_seeding_when_the_actor_is_not_an_organizer()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        var leaver = await SeedPersonAsync(db, ParticipantRole.Volunteer, "leaver@example.test");
        await SeedPersonAsync(db, ParticipantRole.Volunteer, "candidate@example.test");
        var shift = await SeedShiftAsync(db);
        db.VolunteerTaskAssignments.Add(new VolunteerTaskAssignment
        {
            EventId = EventId, TaskId = shift.Id, ParticipantId = leaver.Id,
        });
        await db.SaveChangesAsync();

        // System caller (no actor email) — shifts still vacate, no drafts appear.
        var result = await Cascade(db, Allocation(db)).DeactivateAsync(
            EventId, leaver.Id, "system sweep");

        Assert.Equal(1, result.ShiftAssignmentsVacated);
        Assert.Equal(0, result.BackfillDraftsSeeded);
        Assert.Empty(await db.TaskAllocationDrafts.ToListAsync());
    }

    // ----- silent resurrection: re-activation reports what rejoined -----------

    [Fact]
    public async Task Reactivation_reports_the_dormant_rows_that_rejoin_the_live_counts()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        var p = await SeedPersonAsync(db, ParticipantRole.Media, "media@example.test");
        db.DinnerSignups.Add(new DinnerSignup
        {
            EventId = EventId, ParticipantId = p.Id, Attending = true, PlusOneCount = 1,
        });
        db.LunchSignups.Add(new LunchSignup
        {
            EventId = EventId, ParticipantId = p.Id, LunchPreDay = true,
        });
        db.SwagPreferences.Add(new SwagPreference
        {
            EventId = EventId, ParticipantId = p.Id, WantsPolo = true, PoloSize = "L",
        });
        await db.SaveChangesAsync();

        var sut = Cascade(db);
        await sut.DeactivateAsync(EventId, p.Id, "test");
        var r = await sut.ReactivateAsync(EventId, p.Id, "org@example.test");

        Assert.True(r.Found);
        Assert.True(r.AnythingResurrected);
        Assert.Equal(1, r.DinnerSignupsBackInCounts);
        Assert.Equal(1, r.LunchSignupsBackInCounts);
        Assert.Equal(1, r.SwagPreferencesBackInCounts);
        Assert.Equal(0, r.MasterClassSeatsStillHeld);

        // The audit entry carries the signal (the organizer's paper trail).
        var audit = await db.AuditEntries.SingleAsync(
            a => a.Action == ParticipantDeactivationService.ActionReactivate);
        Assert.Contains("Back in the live counts", audit.Summary);
    }

    [Fact]
    public async Task Reactivation_of_a_person_without_dormant_rows_reports_nothing_resurrected()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        var p = await SeedPersonAsync(db, ParticipantRole.Volunteer, "clean@example.test");
        var sut = Cascade(db);
        await sut.DeactivateAsync(EventId, p.Id, "test");

        var r = await sut.ReactivateAsync(EventId, p.Id);

        Assert.True(r.Found);
        Assert.False(r.AnythingResurrected);
    }

    // ----- V6: draft → commit re-checks IsActive -------------------------------

    private static VolunteerStructureService.ActorContext OrganizerActor(int participantId) =>
        new(participantId, "org@example.test", ParticipantRole.Organizer, EventId);

    [Fact]
    public async Task Commit_consumes_and_skips_a_draft_whose_target_dropped_out()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        var org = await SeedPersonAsync(db, ParticipantRole.Organizer, "org@example.test");
        var vol = await SeedPersonAsync(db, ParticipantRole.Volunteer, "vol@example.test");
        var shift = await SeedShiftAsync(db);
        var sut = Allocation(db);
        var actor = OrganizerActor(org.Id);
        Assert.True(await sut.AddDraftAsync(actor, shift.Id, vol.Id));

        // The volunteer drops out AFTER being queued.
        vol.IsActive = false;
        await db.SaveChangesAsync();

        var result = await sut.CommitAsync(actor);

        Assert.Equal(0, result.Committed);
        Assert.Equal(1, result.SkippedInactive);
        Assert.Empty(await db.VolunteerTaskAssignments.ToListAsync());   // no ghost assignment
        Assert.Empty(await db.TaskAllocationDrafts.ToListAsync());       // stale draft consumed
    }

    [Fact]
    public async Task AddDraft_rejects_a_deactivated_volunteer()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        var org = await SeedPersonAsync(db, ParticipantRole.Organizer, "org@example.test");
        var vol = await SeedPersonAsync(db, ParticipantRole.Volunteer, "vol@example.test");
        vol.IsActive = false;
        await db.SaveChangesAsync();
        var shift = await SeedShiftAsync(db);

        await Assert.ThrowsAsync<VolunteerValidationException>(() =>
            Allocation(db).AddDraftAsync(OrganizerActor(org.Id), shift.Id, vol.Id));
    }

    // ----- G17: sponsor sync stores normalized email ---------------------------

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string> _respond;
        public StubHandler(Func<HttpRequestMessage, string> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_respond(request), Encoding.UTF8, "application/json"),
            });
    }

    [Fact]
    public async Task Sponsor_sync_stores_the_email_lowercased_and_trimmed()
    {
        using var db = NewDb();
        await SeedEventAsync(db);

        var options = new CompanyManagerOptions
        {
            Enabled = true,
            BaseUrl = "https://example.test/wp-json/company-manager/v1",
            Username = "u", Password = "p",
        };
        var cm = new CompanyManagerClient(new HttpClient(new StubHandler(req =>
            req.RequestUri!.AbsolutePath.EndsWith("/users", StringComparison.Ordinal)
                ? """[ { "user_id": "68", "user_email": "MiXeD@Corp.TEST", "full_name": "Mixed Case", "display_name": "Mixed" } ]"""
                : """{ "id": 42, "name": "Corp ApS", "default_signer_id": 0, "event_coordination_default_contact_id": 0 }""")), options);
        var sync = new SponsorContactSyncService(
            db, cm, options, new FixedClock(),
            NullLogger<SponsorContactSyncService>.Instance);

        var result = await sync.SyncCompanyAsync(EventId, 42);

        Assert.Equal(1, result.ParticipantsCreated);
        var contact = await db.Participants.SingleAsync();
        Assert.Equal("mixed@corp.test", contact.Email);   // normalized at write (§253 G17)
    }

    // ----- G17: Sessionize import matches case-insensitively -------------------

    [Fact]
    public async Task Sessionize_import_matches_an_existing_mixed_case_email_without_duplicating()
    {
        using var db = ScenarioFixture.NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "C 2027", Code = "C27",
            IsActive = true, StartDate = new DateOnly(2027, 2, 9),
        });
        // A legacy row stored with RAW casing (e.g. by the pre-fix sponsor sync).
        db.Participants.Add(new Participant
        {
            EventId = EventId, Email = "Jane.Speaker@Corp.TEST", FullName = "Old Name",
            Role = ParticipantRole.Speaker, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        });
        await db.SaveChangesAsync();

        var (import, _) = ScenarioFixture.NewImporter(db);
        var result = await import.ImportSpeakersAsync(
            EventId,
            new[] { new SessionizeSpeaker("jane.speaker@corp.test", "Jane", "Speaker", null) },
            Array.Empty<string>(),
            sendWelcome: false);

        // MATCHED the existing row (no near-duplicate participant), name refreshed.
        Assert.Equal(1, await db.Participants.CountAsync());
        var row = await db.Participants.SingleAsync();
        Assert.Equal("Jane Speaker", row.FullName);
        Assert.Equal(0, result.Created);
    }

    // ----- G8d: refunded Woo orders surface to the action queue ----------------

    private static SponsorOrderPullService NewPull(CommunityHubDbContext db, HttpMessageHandler wooHandler)
    {
        var wooOptions = new WooCommerceOptions
        {
            Enabled = true, BaseUrl = "https://shop.example.test",
            ConsumerKey = "k", ConsumerSecret = "s",
        };
        var cmOptions = new CompanyManagerOptions { Enabled = false };
        var root = RepoPaths.RepoRoot();
        return new SponsorOrderPullService(
            db,
            new WooCommerceClient(new HttpClient(wooHandler), wooOptions),
            new Core.Config.SponsorConfigLoader(),
            wooOptions,
            new Core.Config.SponsorConfigOptions
            {
                SponsorConfigPath = Path.Combine(root, "config", "sponsor.eldk27.json"),
            },
            new Core.Config.EventEditionConfigLoader(),
            new Core.Config.EventConfigOptions
            {
                EventConfigPath = Path.Combine(root, "config", "event.eldk27.json"),
            },
            new SponsorContactSyncService(
                db,
                new CompanyManagerClient(new HttpClient(new StubHandler(_ => "[]")), cmOptions),
                cmOptions, new FixedClock(),
                NullLogger<SponsorContactSyncService>.Instance),
            new SharePointUploadClient(new HttpClient(), new SharePointUploadOptions { Enabled = false }),
            NullLogger<SponsorOrderPullService>.Instance);
    }

    private const string RefundedOrderJson = """
        [ {
            "id": 9001, "status": "refunded",
            "billing": { "email": "buyer@corp.test", "company": "Corp ApS" },
            "meta_data": [ { "key": "_cm_company_id", "value": "42" } ],
            "line_items": [ { "product_id": 7, "name": "Booth E-01" } ],
            "date_created_gmt": "2026-07-01T10:00:00"
        } ]
        """;

    private static HttpMessageHandler WooRouting() =>
        new StubHandler(req =>
            req.RequestUri!.Query.Contains("cancelled", StringComparison.OrdinalIgnoreCase)
                ? RefundedOrderJson
                : "[]");   // completed orders + products: empty

    /// <summary>
    /// 🗑 §757 — a refunded/cancelled order raises NOTHING. This inverts §253 G8d.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-01: <i>"cancel/refund must not be approved. this is handled inside
    /// zoho billing and must not stop anything."</i></para>
    ///
    /// <para>G8d raised one queue item per refunded order "so a human decides the rollback". That
    /// decision is not CEH's to ask for — the money is settled in Zoho billing — and the item sat in
    /// a queue whose premise is "everything that needs a human decision", so it made that queue lie.
    /// The test is inverted rather than deleted, so the reversal is pinned instead of merely
    /// untested.</para>
    /// </remarks>
    [Fact]
    public async Task Refunded_order_raises_NOTHING_because_billing_owns_that_decision()
    {
        using var db = NewDb();
        await SeedEventAsync(db);
        var pull = NewPull(db, WooRouting());

        var first = await pull.RunAsync();
        Assert.True(first.RanToCompletion);

        Assert.Empty(await db.OrganizerActionItems.ToListAsync());

        // And it stays quiet on every subsequent run — no slow accumulation.
        await pull.RunAsync();
        Assert.Empty(await db.OrganizerActionItems.ToListAsync());
    }

    /// <summary>
    /// 🔒 §757 — an ALREADY-OPEN refund item closes itself. Operator: <i>"you must fix these and
    /// close them if they are orphaned - dont ask me to fix them."</i>
    /// </summary>
    [Fact]
    public async Task An_existing_open_refund_item_is_auto_resolved_not_left_for_him_to_tick_off()
    {
        using var db = NewDb();
        await SeedEventAsync(db);

        db.OrganizerActionItems.Add(new Core.Domain.OrganizerActionItem
        {
            EventId = EventId,
            Type = Core.Reminders.OrganizerActionItemService.TypeSponsorOrderRefunded,
            Summary = "Woo order 9001 (Corp ApS) was refunded",
        });
        await db.SaveChangesAsync();

        // A pull that returns at least one COMPLETED order, so the sweep is allowed to run.
        var pull = NewPull(db, new StubHandler(req =>
            req.RequestUri!.Query.Contains("cancelled", StringComparison.OrdinalIgnoreCase)
                ? RefundedOrderJson
                : """
                  [ {
                      "id": 9100, "status": "completed",
                      "billing": { "email": "buyer@corp.test", "company": "Corp ApS" },
                      "meta_data": [ { "key": "_cm_company_id", "value": "42" } ],
                      "line_items": [ { "product_id": 7, "name": "Booth E-01" } ],
                      "date_created_gmt": "2026-07-01T10:00:00"
                  } ]
                  """));

        await pull.RunAsync();

        var item = await db.OrganizerActionItems.SingleAsync();
        Assert.NotNull(item.ResolvedAt);                       // closed, not deleted
        Assert.Contains("Zoho billing", item.ResolvedNotes);   // and it says why
    }
}
