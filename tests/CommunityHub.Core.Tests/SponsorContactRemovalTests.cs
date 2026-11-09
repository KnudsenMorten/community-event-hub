using CommunityHub.Core.Audit;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Organizer;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 🔴 §1369 — REMOVING A CONTACT IN GET STARTED IS APPLIED TO CEH, AND DEACTIVATES BY DEFAULT.
///
/// <para>Operator 2026-10-26: <i>"build the §1369 fixes, deactivate as default"</i>. That day a sponsor
/// removed a colleague who had left the company: the step deleted her in e-conomic and left her active in
/// CEH as signer and coordinator, still mailed, and the next sync warned the organizers about it.</para>
/// </summary>
public sealed class SponsorContactRemovalTests
{
    private const int EventId = 1;
    private const string Company = "20";
    private const string Other = "21";
    private static readonly DateTimeOffset T0 = new(2026, 10, 26, 11, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => T0;
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"contact-removal-{Guid.NewGuid():N}").Options);

    private static SponsorContactRemovalService Sut(CommunityHubDbContext db) =>
        new(db, new SponsorCompanyMembershipService(db, new FixedClock()),
            new ParticipantDeactivationService(db, new FixedClock(), new AuditTrailService(db, new FixedClock())));

    private static async Task<Participant> PersonAsync(
        CommunityHubDbContext db, string email, ParticipantRole role = ParticipantRole.Sponsor,
        params (string Company, int? ErpContact)[] links)
    {
        var p = new Participant
        {
            EventId = EventId, Email = email, FullName = email.Split('@')[0], Role = role,
            SponsorCompanyId = links.Length > 0 ? links[0].Company : null,
            IsSigner = true, IsEventCoordinator = true,
            IsActive = true, LifecycleState = ParticipantLifecycleState.Active, CreatedAt = T0,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        foreach (var (company, erp) in links)
        {
            db.SponsorCompanyContacts.Add(new SponsorCompanyContact
            {
                EventId = EventId, ParticipantId = p.Id, SponsorCompanyId = company,
                IsSigner = true, IsEventCoordinator = true, ErpContactNumber = erp,
                IsPrimary = company == links[0].Company, CreatedAt = T0,
            });
        }
        await db.SaveChangesAsync();
        return p;
    }

    /// <summary>🔴 The reported case: she served only this company, so she is deactivated.</summary>
    [Fact]
    public async Task A_contact_who_served_only_this_company_is_deactivated()
    {
        using var db = NewDb();
        var actor = await PersonAsync(db, "steve@example.test", links: (Company, 1));
        var leaver = await PersonAsync(db, "lee@example.test", links: (Company, 7));

        var r = await Sut(db).ApplyAsync(EventId, Company, 7, "lee@example.test", actor.Id, actor.Email, "SquaredUp");

        Assert.Equal(SponsorContactRemovalOutcome.Deactivated, r.Outcome);
        var after = await db.Participants.SingleAsync(p => p.Id == leaver.Id);
        Assert.False(after.IsActive);
        Assert.False(after.IsSigner);
        Assert.False(after.IsEventCoordinator);
        Assert.False(await db.SponsorCompanyContacts.AnyAsync(x => x.ParticipantId == leaver.Id));
        // The actor is untouched.
        Assert.True((await db.Participants.SingleAsync(p => p.Id == actor.Id)).IsActive);
    }

    /// <summary>🔒 Someone who coordinates another division keeps their account; only this link goes.</summary>
    [Fact]
    public async Task A_contact_who_serves_another_company_only_loses_this_link()
    {
        using var db = NewDb();
        var actor = await PersonAsync(db, "steve@example.test", links: (Company, 1));
        var shared = await PersonAsync(db, "kim@example.test", links: [(Company, 7), (Other, 3)]);

        var r = await Sut(db).ApplyAsync(EventId, Company, 7, "kim@example.test", actor.Id, actor.Email, "SquaredUp");

        Assert.Equal(SponsorContactRemovalOutcome.RolesRemoved, r.Outcome);
        Assert.True((await db.Participants.SingleAsync(p => p.Id == shared.Id)).IsActive);
        var left = await db.SponsorCompanyContacts.Where(x => x.ParticipantId == shared.Id).Select(x => x.SponsorCompanyId).ToListAsync();
        Assert.Equal([Other], left);
    }

    /// <summary>🔒 A speaker who was also a company contact keeps their speaker account.</summary>
    [Fact]
    public async Task A_contact_with_another_role_is_not_deactivated()
    {
        using var db = NewDb();
        var actor = await PersonAsync(db, "steve@example.test", links: (Company, 1));
        var speaker = await PersonAsync(db, "sam@example.test", ParticipantRole.Speaker, (Company, 9));

        var r = await Sut(db).ApplyAsync(EventId, Company, 9, "sam@example.test", actor.Id, actor.Email, "SquaredUp");

        Assert.Equal(SponsorContactRemovalOutcome.RolesRemoved, r.Outcome);
        Assert.True((await db.Participants.SingleAsync(p => p.Id == speaker.Id)).IsActive);
    }

    /// <summary>🔒 Removing yourself never signs you out mid-session.</summary>
    [Fact]
    public async Task Removing_yourself_keeps_your_account()
    {
        using var db = NewDb();
        var actor = await PersonAsync(db, "steve@example.test", links: (Company, 1));

        var r = await Sut(db).ApplyAsync(EventId, Company, 1, "steve@example.test", actor.Id, actor.Email, "SquaredUp");

        Assert.Equal(SponsorContactRemovalOutcome.RemovedYourself, r.Outcome);
        Assert.True((await db.Participants.SingleAsync(p => p.Id == actor.Id)).IsActive);
    }

    /// <summary>A link without the e-conomic number is still found by e-mail — within this company only.</summary>
    [Fact]
    public async Task A_contact_is_matched_by_email_when_no_link_carries_the_number_but_never_across_companies()
    {
        using var db = NewDb();
        var actor = await PersonAsync(db, "steve@example.test", links: (Company, 1));
        var unnumbered = await PersonAsync(db, "lee@example.test", links: (Company, null));
        var elsewhere = await PersonAsync(db, "pat@example.test", links: (Other, null));

        var found = await Sut(db).ApplyAsync(EventId, Company, 7, "LEE@example.test", actor.Id, actor.Email, null);
        var notHere = await Sut(db).ApplyAsync(EventId, Company, 8, "pat@example.test", actor.Id, actor.Email, null);

        Assert.Equal(SponsorContactRemovalOutcome.Deactivated, found.Outcome);
        Assert.Equal(unnumbered.Id, found.ParticipantId);
        Assert.Equal(SponsorContactRemovalOutcome.NotInCeh, notHere.Outcome);
        Assert.True((await db.Participants.SingleAsync(p => p.Id == elsewhere.Id)).IsActive);
    }
}
