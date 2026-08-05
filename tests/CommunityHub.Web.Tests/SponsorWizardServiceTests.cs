using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Erp;
using CommunityHub.Forms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §32 sponsor "Get started" wizard — steps from the Company Details sections, in
/// order, entitlement-aware (booth steps for exhibitors only), with hub-tracked
/// completion (SponsorInfo + booth members/materials). The ERP-contacts step is
/// tracked from e-conomic when that integration is configured, else shown as a
/// guided (undeterminable / untracked) link. Every step is numbered + counted so the
/// "Continue — step X of Y" line always matches the displayed list.
/// </summary>
public sealed class SponsorWizardServiceTests
{
    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"spwiz-{System.Guid.NewGuid()}").Options);

    /// <summary>An e-conomic contact client that is not configured (CanWrite=false).</summary>
    private sealed class OfflineErpClient : IEconomicContactAdminClient
    {
        public bool CanWrite => false;
        public Task<IReadOnlyList<EconomicCustomerRow>> ListCustomersAsync(string? s, int? g = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EconomicCustomerRow>>(new List<EconomicCustomerRow>());
        public Task<IReadOnlyList<EconomicContactRow>> ListContactsAsync(int c, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EconomicContactRow>>(new List<EconomicContactRow>());
        public Task<int> CreateContactAsync(int c, EconomicContactInput i, CancellationToken ct = default) => Task.FromResult(0);
        public Task UpdateContactAsync(int c, int n, EconomicContactInput i, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteContactAsync(int c, int n, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>
    /// Service with the e-conomic / Company Manager integration DISABLED, so the
    /// contacts step is left undeterminable (null) — the fail-soft path. The contacts
    /// check short-circuits on disabled options before touching the HTTP clients.
    /// </summary>
    private static SponsorWizardService OfflineSvc(CommunityHubDbContext db) =>
        new(db,
            new CompanyManagerClient(new HttpClient(), new CompanyManagerOptions { Enabled = false }),
            new CompanyManagerOptions { Enabled = false },
            new EconomicContactAdminService(new OfflineErpClient()),
            NullLogger<SponsorWizardService>.Instance);

    private static async Task<(CommunityHubDbContext db, int ev, int pid)> SeedAsync(
        SponsorInfo? info, string companyId = "c1")
    {
        var db = NewDb();
        var e = new Event { Code = "e", DisplayName = "E", CommunityName = "C", IsActive = true };
        db.Events.Add(e);
        await db.SaveChangesAsync();
        var p = new Participant
        {
            EventId = e.Id, FullName = "Sponsor One", Email = "s@x.dk",
            Role = ParticipantRole.Sponsor, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active, SponsorCompanyId = companyId,
        };
        db.Participants.Add(p);
        if (info is not null) { info.EventId = e.Id; info.SponsorCompanyId = companyId; db.SponsorInfos.Add(info); }
        await db.SaveChangesAsync();
        return (db, e.Id, p.Id);
    }

    [Fact]
    public async Task No_company_link_returns_null()
    {
        var (db, ev, pid) = await SeedAsync(info: null, companyId: "");
        Assert.Null(await OfflineSvc(db).BuildAsync(ev, pid));
    }

    [Fact]
    public async Task Non_exhibitor_has_four_steps_and_tracks_completion()
    {
        var (db, ev, pid) = await SeedAsync(new SponsorInfo
        {
            SponsorPackage = SponsorPackage.Silver,   // no booth
            WebsiteUrl = "https://2linkit.net",       // details done
            // no coordinator, no logo
        });

        var v = (await OfflineSvc(db).BuildAsync(ev, pid))!;

        // §228: every sponsor also gets the company party group sign-up step.
        Assert.Equal(new[] { "company", "contacts", "logos", "party" }, v.Steps.Select(s => s.Key).ToArray());
        Assert.True(v.Steps.Single(s => s.Key == "company").Done);
        // §597 — the coordinator step is gone; contacts is now the first post-company step.
        Assert.Null(v.Steps.Single(s => s.Key == "contacts").Done);   // e-conomic off → undeterminable
        Assert.False(v.Steps.Single(s => s.Key == "logos").Done);
        Assert.False(v.Steps.Single(s => s.Key == "party").Done);     // nobody answered yet
        // §410: this fixture has no tasks OUTSIDE the wizard, so the §400 deadlines step is
        // correctly withheld — an empty "your deadlines" step is worse than no step at all.
        Assert.DoesNotContain(v.Steps, s => s.Key == "deadlines");
        Assert.Equal(4, v.TotalSteps);                                // every step numbered/counted
        Assert.Equal(1, v.DoneCount);
        Assert.Equal("logos", v.NextStep!.Key);   // contacts is undeterminable, so it is skipped
        Assert.False(v.AllDone);
    }

    [Fact]
    public async Task Speaking_session_is_NOT_a_wizard_step_even_when_the_sponsor_bought_one()
    {
        // §495 — the sponsor's speaking session left Get Started for the same reason as §474's
        // booth members: a title, abstract and speaker line-up are rarely settled at onboarding,
        // so keeping it in the completion flow invites a placeholder abstract or parks the sponsor
        // short of 100% on something they cannot answer yet.
        //
        // The fixture DELIBERATELY sets HasSponsorSession = true — the flag that used to add the
        // step — so this pins the step's absence as the RULE rather than an empty-data accident.
        // The obligation survives as the "Submit session description" sponsor task, which §400/§410
        // surface on the deadlines step.
        var (db, ev, pid) = await SeedAsync(new SponsorInfo
        {
            SponsorPackage = SponsorPackage.Gold,
            HasSponsorSession = true,
            WebsiteUrl = "https://2linkit.net",
            EventCoordinatorEmail = "coord@x.dk",
        });

        var v = (await OfflineSvc(db).BuildAsync(ev, pid))!;

        Assert.DoesNotContain(v.Steps, s => s.Key == "session");
    }

    [Fact]
    public async Task Exhibitor_adds_booth_steps_with_data_backed_completion()
    {
        var (db, ev, pid) = await SeedAsync(new SponsorInfo
        {
            SponsorPackage = SponsorPackage.Gold,     // has booth
            WebsiteUrl = "https://2linkit.net",
            EventCoordinatorEmail = "coord@x.dk",
            LogoRasterPath = "/logo.png",
        });
        db.SponsorBoothMembers.Add(new SponsorBoothMember
        { EventId = ev, SponsorCompanyId = "c1", FirstName = "A", LastName = "B", Email = "a@b.dk" });
        // §297: the logos step is done only when ALL THREE logos are in the upload audit.
        foreach (var kind in new[] { "some", "print", "zoho" })
            db.SponsorUploadAudits.Add(new SponsorUploadAudit
            {
                EventId = ev, SponsorCompanyId = "c1", Kind = kind, FileName = $"{kind}.png",
                Version = 1, WebUrl = "/x", UploadedByEmail = "x@y.dk", UploadedAt = DateTimeOffset.UtcNow,
            });
        await db.SaveChangesAsync();

        var v = (await OfflineSvc(db).BuildAsync(ev, pid))!;

        // §229 adds booth-checkin for exhibitors; §228 adds the party group step for all.
        Assert.Equal(new[] { "company", "contacts", "logos", "booth-materials", "booth-checkin", "party" },
            v.Steps.Select(s => s.Key).ToArray());

        // §474 — booth-members is NOT a wizard step: a sponsor onboarding ~6 months out cannot know
        // who will staff the booth. Asserted explicitly, and the fixture DOES add a booth member
        // above, so this pins the step's absence as the rule rather than an empty-data accident.
        // The obligation itself survives as the `sponsor:…:register-booth-members` task on the
        // deadlines step (see SponsorDeliverablesServiceTests for the stage).
        Assert.DoesNotContain(v.Steps, s => s.Key == "booth-members");

        // 🔒 §732 — booth-materials is ALWAYS Done, because it is OPTIONAL. This assertion used to
        // be `False` ("none yet"), which is exactly the behaviour the operator rejected on
        // 2026-07-31: *"same with this task - which is also optional but it keeps coming back to it
        // as noncompleted"*. The step's own copy already said *"this is optional, and you can come
        // back later"* while holding the sponsor at 7 of 8 for not doing the optional thing.
        // A step nobody can be REQUIRED to finish must not gate the progress bar.
        Assert.True(v.Steps.Single(s => s.Key == "booth-materials").Done);
        Assert.False(v.Steps.Single(s => s.Key == "booth-checkin").Done);   // §229: unanswered
        Assert.False(v.Steps.Single(s => s.Key == "party").Done);           // §228: unanswered
        // details + logos done, plus booth-materials by §732. §410: no tasks outside the wizard in
        // this fixture, so no deadlines step. §597: the coordinator step is gone, so BOTH the total
        // and the done-count drop by one — it used to be counted as done via EventCoordinatorEmail.
        Assert.Equal(6, v.TotalSteps);
        Assert.Equal(3, v.DoneCount);
        // §732 moved the "Continue" target past the optional step to the first REQUIRED one.
        Assert.Equal("booth-checkin", v.NextStep!.Key);
    }

    [Fact]
    public async Task Booth_checkin_step_completes_on_any_answer_including_not_participating()
    {
        // §229: the step is Done once ANY slot is saved — incl. the pre-day opt-out.
        var (db, ev, pid) = await SeedAsync(new SponsorInfo
        {
            SponsorPackage = SponsorPackage.Gold,
            BoothCheckInSlot = BoothCheckInSlots.NotParticipating,
        });

        var v = (await OfflineSvc(db).BuildAsync(ev, pid))!;
        Assert.True(v.Steps.Single(s => s.Key == "booth-checkin").Done);
    }

    [Fact]
    public async Task Party_step_completes_for_every_contact_once_anyone_in_the_company_answered()
    {
        // §228: ONE group reservation covers the whole company — a second contact's wizard
        // shows the party step Done even though THEY never submitted anything.
        var (db, ev, pid) = await SeedAsync(new SponsorInfo { SponsorPackage = SponsorPackage.Silver });
        var colleague = new Participant
        {
            EventId = ev, FullName = "Sponsor Two", Email = "s2@x.dk",
            Role = ParticipantRole.Sponsor, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active, SponsorCompanyId = "c1",
        };
        db.Participants.Add(colleague);
        await db.SaveChangesAsync();
        db.PartyRsvps.Add(new PartyRsvp
        {
            EventId = ev, Name = "Sponsor Two", Email = "s2@x.dk",
            Attending = true, HeadCount = 4, ParticipantId = colleague.Id,
        });
        await db.SaveChangesAsync();

        // Build for the FIRST contact (pid) — who did not answer themselves.
        var v = (await OfflineSvc(db).BuildAsync(ev, pid))!;
        Assert.True(v.Steps.Single(s => s.Key == "party").Done);
    }

    /// <summary>
    /// Regression guard for the "next not completed is #3 but it refers to #4" bug:
    /// the "Continue" target's number MUST equal its 1-based position in the displayed
    /// list, and the denominator MUST be the total number of steps shown — not the old
    /// tracked-only count (which produced "step 4 of 3"-style mismatches when the
    /// untracked contacts step sat in the middle).
    /// </summary>
    [Fact]
    public async Task NextStepNumber_matches_list_position_and_total()
    {
        var (db, ev, pid) = await SeedAsync(new SponsorInfo
        {
            SponsorPackage = SponsorPackage.Silver,   // no booth → 4 steps
            WebsiteUrl = "https://2linkit.net",       // details done
            EventCoordinatorEmail = "coord@x.dk",     // coordinator done
            // contacts untracked (e-conomic off), logos NOT done
        });

        var v = (await OfflineSvc(db).BuildAsync(ev, pid))!;

        // §597 — the "coordinator" step is GONE (operator: "step event coordinator is not
        // relevant"); the "contacts" step already lists coordinators as a add/delete list.
        // Steps: 1 details(✓) 2 contacts(untracked) 3 logos(✗) 4 party(✗)
        //        5 deadlines(✓ — §400 read-only summary, always done).
        Assert.Equal("logos", v.NextStep!.Key);
        var listPosition = v.Steps.Select((s, i) => (s, i)).First(x => x.s.Key == v.NextStep.Key).i + 1;
        Assert.Equal(listPosition, v.NextStepNumber);   // number shown == list item the user sees
        Assert.Equal(3, v.NextStepNumber);
        Assert.Equal(4, v.TotalSteps);                  // "step 3 of 4", not "3 of 5"
        Assert.True(v.NextStepNumber <= v.TotalSteps);
    }
}
