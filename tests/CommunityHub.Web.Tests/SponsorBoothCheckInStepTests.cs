using CommunityHub.Core.Domain;
using CommunityHub.Forms;
using CommunityHub.Forms.Steps;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §285 Phase 2 / §229 — the sponsor Booth-check-in step now renders INLINE in the shared wizard.
/// These lock the shared <see cref="SponsorBoothCheckInFormService"/> that both the inline step and
/// the standalone Company Details section use: load the saved slot, validate, persist, and (§291)
/// make the step's done-signal (BoothCheckInSlot present) read true after save. EF InMemory.
/// </summary>
public sealed class SponsorBoothCheckInStepTests
{
    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);
    }

    private static CommunityHub.Core.Data.CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHub.Core.Data.CommunityHubDbContext>()
            .UseInMemoryDatabase($"booth-{Guid.NewGuid():N}").Options);

    private static async Task<int> SeedSponsorAsync(
        CommunityHub.Core.Data.CommunityHubDbContext db, bool hasBooth, string? slot = null)
    {
        var evt = new Event { Code = "T27", CommunityName = "C", DisplayName = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10) };
        db.Events.Add(evt);
        await db.SaveChangesAsync();
        var p = new Participant { EventId = evt.Id, Email = "sp@x.test", FullName = "Sp Onsor",
            Role = ParticipantRole.Sponsor, IsActive = true, LifecycleState = ParticipantLifecycleState.Active,
            SponsorCompanyId = "42" };
        db.Participants.Add(p);
        db.SponsorInfos.Add(new SponsorInfo { EventId = evt.Id, SponsorCompanyId = "42",
            SponsorPackage = hasBooth ? SponsorPackage.Gold : SponsorPackage.Silver, // HasBooth = >= Gold
            BoothCheckInSlot = slot });
        await db.SaveChangesAsync();
        return p.Id;
    }

    [Fact]
    public async Task Load_returns_the_saved_slot()
    {
        using var db = NewDb();
        var validSlot = BoothCheckInSlots.All[0];
        var pid = await SeedSponsorAsync(db, hasBooth: true, slot: validSlot);
        var svc = new SponsorBoothCheckInFormService(db, new FixedClock());

        var model = await svc.LoadAsync(1, pid, default);
        Assert.Equal(validSlot, model.CurrentSlot);
    }

    [Fact]
    public async Task Save_persists_a_valid_slot_and_marks_the_step_done()
    {
        using var db = NewDb();
        var pid = await SeedSponsorAsync(db, hasBooth: true);
        var svc = new SponsorBoothCheckInFormService(db, new FixedClock());
        var slot = BoothCheckInSlots.All[0];

        // §477: an attending slot now REQUIRES a head count, so a valid save supplies one.
        var model = new SponsorBoothCheckInModel { Slot = slot, MemberCount = 3 };
        var outcome = await svc.SaveAsync(model, 1, pid, "sp@x.test", new ModelStateDictionary(), default);

        Assert.Equal(WizardStepOutcome.Advance, outcome);
        var info = await db.SponsorInfos.FirstAsync();
        Assert.Equal(slot, info.BoothCheckInSlot);                 // §291: saved
        Assert.Equal(3, info.BoothCheckInMemberCount);
        Assert.Equal("sp@x.test", info.BoothCheckInSetByEmail);
        Assert.NotNull(info.BoothCheckInSetAt);
        Assert.Equal(slot, model.CurrentSlot);                     // done-signal now true
    }

    [Fact]
    public async Task Save_requires_a_member_count_when_the_team_IS_attending()
    {
        // §477 — the count feeds the pre-day LUNCH order, so a blank silently under-ordered
        // catering for a whole exhibitor team. Mandatory now, and enforced SERVER-side: the input
        // cannot carry HTML `required` (the not-participating option must post it empty), so this
        // check is the only thing holding.
        using var db = NewDb();
        var pid = await SeedSponsorAsync(db, hasBooth: true);
        var svc = new SponsorBoothCheckInFormService(db, new FixedClock());
        var ms = new ModelStateDictionary();

        var outcome = await svc.SaveAsync(
            new SponsorBoothCheckInModel { Slot = BoothCheckInSlots.All[0], MemberCount = null },
            1, pid, "sp@x.test", ms, default);

        Assert.Equal(WizardStepOutcome.Invalid, outcome);
        Assert.True(ms.ContainsKey(nameof(SponsorBoothCheckInModel.MemberCount)));
        Assert.Empty(await db.SponsorInfos.Where(s => s.BoothCheckInSlot != null).ToListAsync());
    }

    [Fact]
    public async Task Save_does_NOT_require_a_member_count_when_not_participating()
    {
        // §477 — "how many will check in" is unanswerable once they have said they are not coming,
        // so the opt-out stays exempt and any previously-saved count is cleared rather than kept.
        using var db = NewDb();
        var pid = await SeedSponsorAsync(db, hasBooth: true);
        var svc = new SponsorBoothCheckInFormService(db, new FixedClock());

        await svc.SaveAsync(
            new SponsorBoothCheckInModel { Slot = BoothCheckInSlots.All[0], MemberCount = 4 },
            1, pid, "sp@x.test", new ModelStateDictionary(), default);

        var outcome = await svc.SaveAsync(
            new SponsorBoothCheckInModel { Slot = BoothCheckInSlots.NotParticipating, MemberCount = null },
            1, pid, "sp@x.test", new ModelStateDictionary(), default);

        Assert.Equal(WizardStepOutcome.Advance, outcome);
        var info = await db.SponsorInfos.FirstAsync();
        Assert.Equal(BoothCheckInSlots.NotParticipating, info.BoothCheckInSlot);
        Assert.Null(info.BoothCheckInMemberCount);                  // the stale 4 is gone
    }

    [Fact]
    public async Task Save_rejects_an_invalid_slot_with_a_field_error()
    {
        using var db = NewDb();
        var pid = await SeedSponsorAsync(db, hasBooth: true);
        var svc = new SponsorBoothCheckInFormService(db, new FixedClock());
        var ms = new ModelStateDictionary();

        var outcome = await svc.SaveAsync(
            new SponsorBoothCheckInModel { Slot = "not-a-real-slot" }, 1, pid, "sp@x.test", ms, default);

        Assert.Equal(WizardStepOutcome.Invalid, outcome);
        Assert.True(ms.ContainsKey(nameof(SponsorBoothCheckInModel.Slot)));
        Assert.Null((await db.SponsorInfos.FirstAsync()).BoothCheckInSlot);   // nothing persisted
    }

    [Fact]
    public async Task Save_is_not_relevant_for_a_non_exhibitor()
    {
        using var db = NewDb();
        var pid = await SeedSponsorAsync(db, hasBooth: false);
        var svc = new SponsorBoothCheckInFormService(db, new FixedClock());

        var outcome = await svc.SaveAsync(
            new SponsorBoothCheckInModel { Slot = BoothCheckInSlots.All[0] }, 1, pid, "sp@x.test",
            new ModelStateDictionary(), default);

        Assert.Equal(WizardStepOutcome.NotRelevant, outcome);
        Assert.Null((await db.SponsorInfos.FirstAsync()).BoothCheckInSlot);
    }
}
