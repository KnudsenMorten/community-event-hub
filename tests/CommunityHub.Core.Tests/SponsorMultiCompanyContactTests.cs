using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.EntityFrameworkCore;
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

    private const string Cmo = "cmo@group.test";
    private const string Coord = "coord@group.test";

    /// <summary>
    /// Seeds one company's roster the way the role sync leaves it: the SAME two humans serve every
    /// division; a person's first company is their primary, later ones only add a link.
    /// </summary>
    private static async Task LinkRosterAsync(
        CommunityHubDbContext db, int eventId, int companyId, string signer, string coordinator)
    {
        var members = new SponsorCompanyMembershipService(db, Scenario.ScenarioFixture.Clock);
        foreach (var (email, name) in new[] { (Cmo, "Global CMO"), (Coord, "Global Coordinator") })
        {
            var p = await db.Participants.SingleOrDefaultAsync(x => x.EventId == eventId && x.Email == email);
            var isNew = p is null;
            if (p is null)
            {
                p = new Participant
                {
                    EventId = eventId, Email = email, FullName = name, Role = ParticipantRole.Sponsor,
                    SponsorCompanyId = companyId.ToString(), IsActive = true,
                    IsSigner = email == signer, IsEventCoordinator = email == coordinator,
                };
                db.Participants.Add(p);
                await db.SaveChangesAsync();
            }
            await members.LinkAsync(eventId, p.Id, companyId.ToString(),
                isSigner: email == signer, isEventCoordinator: email == coordinator, isPrimary: isNew);
        }
        await db.SaveChangesAsync();
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
    /// 🔴 §1235 — THE COMPANY'S MAIL REACHES A COORDINATOR WHOSE PRIMARY COMPANY IS ANOTHER DIVISION.
    /// Without the link-aware audience, division B's coordinator mail resolved to NOBODY and was
    /// silently not sent — the exact loss the operator is protecting against.
    /// </summary>
    [Fact]
    public async Task Company_mail_reaches_a_shared_coordinator_linked_from_another_primary()
    {
        using var db = NewDb();
        var ev = await SeedAsync(db);

        await LinkRosterAsync(db, ev.Id, CompanyA, signer: Cmo, coordinator: Coord);
        await LinkRosterAsync(db, ev.Id, CompanyB, signer: Cmo, coordinator: Coord);

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

        // A: the coordinator coordinates — that is their person-level flag.
        await LinkRosterAsync(db, ev.Id, CompanyA, signer: Cmo, coordinator: Coord);
        // B: the coordinator only SIGNS; the CMO coordinates there.
        await LinkRosterAsync(db, ev.Id, CompanyB, signer: Coord, coordinator: Cmo);

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

        await LinkRosterAsync(db, ev.Id, CompanyA, signer: Cmo, coordinator: Coord);
        var members = new SponsorCompanyMembershipService(db, Scenario.ScenarioFixture.Clock);
        await LinkRosterAsync(db, ev.Id, CompanyB, signer: Cmo, coordinator: Coord);

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
