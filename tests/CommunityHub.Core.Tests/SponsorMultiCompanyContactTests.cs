using System.Net;
using System.Text;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 🔴 §1235 — ONE PERSON, SEVERAL SPONSOR COMPANIES.
///
/// <para>Operator 2026-09-16, about a sponsor group whose divisions are separate companies:
/// <i>"the global CMO and global event coordinator are the default signers and default event
/// coordinator"</i> · <i>"arrow can have same issue, as they share event coordinator, but different
/// signers"</i> · <i>"in my erp they will exist with 2 contacts under each account. contacts are the
/// same"</i> · <i>"the customer will create the truesec denmark company tomorrow"</i>.</para>
///
/// <para>⚠️ <b>What happened before.</b> The contact sync looked people up by e-mail with NO company
/// filter and then reassigned them, so with a roster pulled every ~15 minutes per company a shared
/// contact ping-ponged between divisions (last-synced-company wins) and the losing division silently
/// had no coordinator. Known and documented since 2026-07-07 as the "sponsor contact company-flap".</para>
/// </summary>
public sealed class SponsorMultiCompanyContactTests
{
    private const int EventId = 1;
    private const int CompanyA = 501;   // e.g. the group / first division
    private const int CompanyB = 502;   // the division created later

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"multico-{Guid.NewGuid():N}")
            .Options);

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<string, string> _byPath;
        public StubHandler(Func<string, string> byPath) => _byPath = byPath;
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    _byPath(request.RequestUri!.AbsolutePath), Encoding.UTF8, "application/json"),
            });
    }

    /// <summary>The SAME two humans are the users of BOTH companies — the real group shape.</summary>
    private static string UsersJson() => """
        [
          { "user_id": "68", "user_email": "cmo@group.test", "full_name": "Global CMO", "display_name": "CMO" },
          { "user_id": "77", "user_email": "coord@group.test", "full_name": "Global Coordinator", "display_name": "Coord" }
        ]
        """;

    private static string CompanyJson(int id, int signerId, int coordinatorId) => $$"""
        {
          "id": {{id}},
          "name": "Group Division {{id}}",
          "company_name_public": "Group {{id}}",
          "default_signer_id": {{signerId}},
          "event_coordination_default_contact_id": {{coordinatorId}}
        }
        """;

    private static (SponsorContactSyncService Svc, SponsorCompanyMembershipService Members) NewSync(
        CommunityHubDbContext db, int companyId, int signerId, int coordinatorId)
    {
        var options = new CompanyManagerOptions
        {
            Enabled = true,
            BaseUrl = "https://example.test/wp-json/company-manager/v1",
            Username = "u",
            Password = "p",
        };
        var http = new HttpClient(new StubHandler(path =>
            path.EndsWith("/users", StringComparison.Ordinal)
                ? UsersJson()
                : CompanyJson(companyId, signerId, coordinatorId)));
        var members = new SponsorCompanyMembershipService(db, Scenario.ScenarioFixture.Clock);
        var svc = new SponsorContactSyncService(
            db, new CompanyManagerClient(http, options), options, Scenario.ScenarioFixture.Clock,
            NullLogger<SponsorContactSyncService>.Instance, erpContacts: null, membership: members);
        return (svc, members);
    }

    private static async Task<Event> SeedAsync(CommunityHubDbContext db)
    {
        var ev = new Event
        {
            CommunityName = "Experts Live Denmark",
            DisplayName = "Experts Live Denmark 2027",
            Code = "ELDK27",
            IsActive = true,
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();

        foreach (var id in new[] { CompanyA, CompanyB })
        {
            db.SponsorInfos.Add(new SponsorInfo
            {
                EventId = ev.Id, SponsorCompanyId = id.ToString(),
                CompanyName = $"Group Division {id}", IsSponsor = true,
            });
        }
        await db.SaveChangesAsync();
        return ev;
    }

    /// <summary>
    /// 🔴 THE HEADLINE: the same person synced under a second company is ONE participant with TWO
    /// links — not a moved row, and not a duplicate (the e-mail is unique per edition).
    /// </summary>
    [Fact]
    public async Task A_contact_shared_by_two_companies_keeps_one_row_and_gains_a_link()
    {
        using var db = NewDb();
        var ev = await SeedAsync(db);

        var (syncA, members) = NewSync(db, CompanyA, signerId: 68, coordinatorId: 77);
        await syncA.SyncCompanyAsync(ev.Id, CompanyA);

        var (syncB, _) = NewSync(db, CompanyB, signerId: 68, coordinatorId: 77);
        await syncB.SyncCompanyAsync(ev.Id, CompanyB);

        var cmo = await db.Participants.SingleAsync(p => p.Email == "cmo@group.test");

        // 🔒 THE FLAP IS GONE: the first company stays primary; the second only ADDS a link.
        Assert.Equal(CompanyA.ToString(), cmo.SponsorCompanyId);

        var links = await members.ForParticipantAsync(ev.Id, cmo.Id);
        Assert.Equal(2, links.Count);
        Assert.Contains(links, l => l.SponsorCompanyId == CompanyA.ToString() && l.IsPrimary);
        Assert.Contains(links, l => l.SponsorCompanyId == CompanyB.ToString() && !l.IsPrimary);
        // Signing is per company, and this person signs for both.
        Assert.All(links, l => Assert.True(l.IsSigner));
    }

    /// <summary>Re-running both syncs (every ~15 min in production) changes nothing — no flap, no growth.</summary>
    [Fact]
    public async Task Repeated_syncs_are_idempotent_and_never_move_the_primary_company()
    {
        using var db = NewDb();
        var ev = await SeedAsync(db);

        for (var i = 0; i < 3; i++)
        {
            await NewSync(db, CompanyA, 68, 77).Svc.SyncCompanyAsync(ev.Id, CompanyA);
            await NewSync(db, CompanyB, 68, 77).Svc.SyncCompanyAsync(ev.Id, CompanyB);
        }

        var cmo = await db.Participants.SingleAsync(p => p.Email == "cmo@group.test");
        Assert.Equal(CompanyA.ToString(), cmo.SponsorCompanyId);
        Assert.Equal(2, await db.SponsorCompanyContacts.CountAsync(x => x.ParticipantId == cmo.Id));
        Assert.Equal(2, await db.Participants.CountAsync());   // two humans, not four rows
    }

    /// <summary>
    /// §1235 — ARROW's shape: the coordinator is shared, the signers differ. The flags must answer
    /// PER COMPANY, which is why they live on the link.
    /// </summary>
    [Fact]
    public async Task Flags_are_per_company_so_a_shared_coordinator_can_sign_for_only_one()
    {
        using var db = NewDb();
        var ev = await SeedAsync(db);

        // Division A: the CMO signs, the coordinator coordinates.
        await NewSync(db, CompanyA, signerId: 68, coordinatorId: 77).Svc.SyncCompanyAsync(ev.Id, CompanyA);
        // Division B: a DIFFERENT signer (the coordinator), same coordinator.
        await NewSync(db, CompanyB, signerId: 77, coordinatorId: 77).Svc.SyncCompanyAsync(ev.Id, CompanyB);

        var cmo = await db.Participants.SingleAsync(p => p.Email == "cmo@group.test");
        var linkA = await db.SponsorCompanyContacts.SingleAsync(
            x => x.ParticipantId == cmo.Id && x.SponsorCompanyId == CompanyA.ToString());
        var linkB = await db.SponsorCompanyContacts.SingleAsync(
            x => x.ParticipantId == cmo.Id && x.SponsorCompanyId == CompanyB.ToString());

        Assert.True(linkA.IsSigner);    // signs for A
        Assert.False(linkB.IsSigner);   // …but not for B
    }

    /// <summary>
    /// 🔴 §1235 — THE COMPANY'S MAIL REACHES A COORDINATOR WHOSE PRIMARY COMPANY IS ANOTHER DIVISION.
    /// Without the link-aware audience, division B's coordinator mail resolved to NOBODY and was
    /// silently not sent — the exact loss the operator is protecting against.
    /// </summary>
    [Fact]
    public async Task Company_mail_reaches_a_shared_coordinator_linked_from_another_primary()
    {
        using var db = NewDb();
        var ev = await SeedAsync(db);

        await NewSync(db, CompanyA, 68, 77).Svc.SyncCompanyAsync(ev.Id, CompanyA);
        await NewSync(db, CompanyB, 68, 77).Svc.SyncCompanyAsync(ev.Id, CompanyB);

        var resolver = new Email.SponsorRecipientResolver(db);
        var forB = await resolver.ResolveAsync(ev.Id, CompanyB.ToString());

        Assert.Contains(forB, r => r.Email == "coord@group.test");
    }

    /// <summary>
    /// 🔒 …and the person-level flag does NOT leak across companies: somebody who only SIGNS for this
    /// division is not mailed as its coordinator just because they coordinate their own.
    /// </summary>
    [Fact]
    public async Task A_signer_only_link_is_not_mailed_as_that_companys_coordinator()
    {
        using var db = NewDb();
        var ev = await SeedAsync(db);

        // A: the coordinator (77) coordinates — that is their person-level flag.
        await NewSync(db, CompanyA, signerId: 68, coordinatorId: 77).Svc.SyncCompanyAsync(ev.Id, CompanyA);
        // B: person 77 only SIGNS; 68 coordinates there.
        await NewSync(db, CompanyB, signerId: 77, coordinatorId: 68).Svc.SyncCompanyAsync(ev.Id, CompanyB);

        var forB = await new Email.SponsorRecipientResolver(db).ResolveAsync(ev.Id, CompanyB.ToString());

        Assert.Contains(forB, r => r.Email == "cmo@group.test");        // B's own coordinator
        Assert.DoesNotContain(forB, r => r.Email == "coord@group.test"); // signs for B, coordinates A
    }

    /// <summary>
    /// 🔴 §1235 — LEAVING ONE DIVISION IS NOT LEAVING. Unlinking must not switch off a person who
    /// still serves another company; the primary company moves to a surviving link instead.
    /// </summary>
    [Fact]
    public async Task Unlinking_one_company_keeps_the_person_active_for_the_others()
    {
        using var db = NewDb();
        var ev = await SeedAsync(db);

        await NewSync(db, CompanyA, 68, 77).Svc.SyncCompanyAsync(ev.Id, CompanyA);
        var (_, members) = NewSync(db, CompanyB, 68, 77);
        await NewSync(db, CompanyB, 68, 77).Svc.SyncCompanyAsync(ev.Id, CompanyB);

        var cmo = await db.Participants.SingleAsync(p => p.Email == "cmo@group.test");
        Assert.True(await members.UnlinkAsync(ev.Id, cmo.Id, CompanyA.ToString()));
        await db.SaveChangesAsync();

        var remaining = await members.ForParticipantAsync(ev.Id, cmo.Id);
        Assert.Equal(CompanyB.ToString(), Assert.Single(remaining).SponsorCompanyId);
        Assert.True(cmo.IsActive);
    }

    /// <summary>
    /// 🔴 §1235 — THE ERP CONTACT NUMBER IS PER ACCOUNT. e-conomic numbers are per-customer
    /// sequences, so two accounts both have a contact 6; the lookup must be scoped to the company or
    /// it returns the wrong person the moment two accounts overlap.
    /// </summary>
    [Fact]
    public async Task The_erp_contact_number_is_resolved_within_the_company_not_event_wide()
    {
        using var db = NewDb();
        var ev = await SeedAsync(db);
        var members = new SponsorCompanyMembershipService(db, Scenario.ScenarioFixture.Clock);

        var alice = new Participant
        {
            EventId = ev.Id, Email = "alice@a.test", FullName = "Alice",
            Role = ParticipantRole.Sponsor, SponsorCompanyId = CompanyA.ToString(), IsActive = true,
        };
        var bob = new Participant
        {
            EventId = ev.Id, Email = "bob@b.test", FullName = "Bob",
            Role = ParticipantRole.Sponsor, SponsorCompanyId = CompanyB.ToString(), IsActive = true,
        };
        db.Participants.AddRange(alice, bob);
        await db.SaveChangesAsync();

        // BOTH accounts have a contact number 6 — different humans.
        await members.LinkAsync(ev.Id, alice.Id, CompanyA.ToString(), erpContactNumber: 6, isPrimary: true);
        await members.LinkAsync(ev.Id, bob.Id, CompanyB.ToString(), erpContactNumber: 6, isPrimary: true);
        await db.SaveChangesAsync();

        Assert.Equal(alice.Id, await members.ParticipantIdByErpContactAsync(ev.Id, CompanyA.ToString(), 6));
        Assert.Equal(bob.Id, await members.ParticipantIdByErpContactAsync(ev.Id, CompanyB.ToString(), 6));
    }
}
