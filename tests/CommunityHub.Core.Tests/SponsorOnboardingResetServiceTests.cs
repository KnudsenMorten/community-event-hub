using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §366 — the SPONSOR half of the §355 re-onboarding reset (operator 2026-07-26: <i>"the reset of
/// sponsors didn't reset correctly, as 2 tasks were not reset, one of them was initial onboarding of
/// sponsor + booth members"</i>).
///
/// <para>Two bugs are pinned here, and both were real:</para>
/// <list type="number">
///   <item>Sponsor tasks are COMPANY-scoped (<c>SourceKey = "sponsor:{id}:…"</c>,
///   <c>AssignedParticipantId = NULL</c>), so every assignee-based reset silently skipped them.</item>
///   <item>Re-opening those tasks is NOT enough — <c>SponsorOrderPullService</c> auto-closes three of
///   them from the DATA on file, so a task-only reset is undone on the next pull. The reset must
///   clear the backing data, or say plainly that it could not.</item>
/// </list>
/// FAKE company/person names only.
/// </summary>
public sealed class SponsorOnboardingResetServiceTests
{
    private const string CompanyId = "12";
    private const string ContactEmail = "sponsor-contact@example.test";

    private static async Task<(CommunityHub.Core.Data.CommunityHubDbContext Db, int EventId)>
        SeedAsync(string? zohoExhibitorId = null)
    {
        var db = ScenarioFixture.NewDb();
        var ev = new Event
        {
            Code = "T27", CommunityName = "C", DisplayName = "C 2027", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();

        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = ev.Id, SponsorCompanyId = CompanyId,
            CompanyDescription = "We do things with computers.",
            ZohoExhibitorId = zohoExhibitorId,
        });
        db.Participants.Add(new Participant
        {
            EventId = ev.Id, Email = ContactEmail, FullName = "Sponsor Contact",
            Role = ParticipantRole.Sponsor, SponsorCompanyId = CompanyId,
            IsActive = true, LifecycleState = ParticipantLifecycleState.Active,
            WelcomeWithLoginSentAt = DateTimeOffset.UtcNow,
        });
        db.SponsorBoothMembers.Add(new SponsorBoothMember
        {
            EventId = ev.Id, SponsorCompanyId = CompanyId,
            FirstName = "Booth", LastName = "Person", Email = "booth@example.test",
        });
        db.SentReminders.Add(new SentReminder
        {
            EventId = ev.Id, RecipientEmail = ContactEmail, ReminderType = "welcome",
            OccasionKey = "welcome:sponsor",
        });

        // The two tasks the operator named, both COMPANY-scoped with NO assignee — the exact shape
        // that made every earlier reset miss them.
        foreach (var slug in new[] { "initial-onboarding-of-sponsor", "register-booth-members" })
        {
            db.Tasks.Add(new ParticipantTask
            {
                EventId = ev.Id, AssignedParticipantId = null, Title = slug,
                SourceKey = $"sponsor:{CompanyId}:{slug}", State = TaskState.Done,
                CompletedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-40),
            });
        }
        await db.SaveChangesAsync();
        return (db, ev.Id);
    }

    private static Task<ParticipantTask> TaskBySlugAsync(
        CommunityHub.Core.Data.CommunityHubDbContext db, string slug) =>
        db.Tasks.FirstAsync(t => t.SourceKey == $"sponsor:{CompanyId}:{slug}");

    /// <summary>
    /// THE REPORTED BUG. A company-scoped task with no assignee must come back to Open — this is
    /// what silently did nothing before §366.
    /// </summary>
    [Fact]
    public async Task Company_scoped_tasks_with_no_assignee_are_reopened()
    {
        var (db, ev) = await SeedAsync();
        using (db)
        {
            var r = await new SponsorOnboardingResetService(db).ResetAsync(
                ev, CompanyId, resetWelcome: false, resetOverview: false,
                resetBoothMembers: false, resetAllTasks: true);

            Assert.True(r.Ok);
            Assert.Equal(2, r.TasksReopened);
            foreach (var slug in new[] { "initial-onboarding-of-sponsor", "register-booth-members" })
            {
                var t = await TaskBySlugAsync(db, slug);
                Assert.Equal(TaskState.Open, t.State);
                Assert.Null(t.CompletedAt);   // a Done time left behind still reads as finished (§332)
            }
        }
    }

    /// <summary>
    /// §358: a reset must re-stamp <c>CreatedAt</c>. Leaving a 40-day-old timestamp behind lets the
    /// chaser fire on its very next pass — that is the double-send the operator hit twice.
    /// </summary>
    [Fact]
    public async Task Reopened_tasks_are_restamped_so_the_chaser_waits_a_full_window()
    {
        var (db, ev) = await SeedAsync();
        using (db)
        {
            var before = DateTimeOffset.UtcNow.AddMinutes(-1);
            await new SponsorOnboardingResetService(db).ResetAsync(
                ev, CompanyId, false, false, false, resetAllTasks: true);

            var t = await TaskBySlugAsync(db, "register-booth-members");
            Assert.True(t.CreatedAt > before, "CreatedAt must be re-stamped to now, not left at -40 days.");
        }
    }

    /// <summary>
    /// The DEEPER bug: re-opening "initial onboarding" while the company description is still saved
    /// is undone by the next webshop pull, which auto-closes from that data. The overview switch must
    /// clear the description itself.
    /// </summary>
    [Fact]
    public async Task Overview_reset_clears_the_description_so_the_pull_cannot_reclose_it()
    {
        var (db, ev) = await SeedAsync();
        using (db)
        {
            var r = await new SponsorOnboardingResetService(db).ResetAsync(
                ev, CompanyId, resetWelcome: false, resetOverview: true,
                resetBoothMembers: false, resetAllTasks: false);

            Assert.True(r.OverviewCleared);
            var info = await db.SponsorInfos.FirstAsync(s => s.SponsorCompanyId == CompanyId);
            Assert.True(string.IsNullOrEmpty(info.CompanyDescription));
            Assert.Equal(TaskState.Open, (await TaskBySlugAsync(db, "initial-onboarding-of-sponsor")).State);
            // Scoped: the booth task was NOT selected, so it stays as it was.
            Assert.Equal(TaskState.Done, (await TaskBySlugAsync(db, "register-booth-members")).State);
        }
    }

    /// <summary>
    /// Booth members are HARD-deleted, not tombstoned. A tombstone is right when a sponsor removes a
    /// real person, but here it would block re-entering the same address and turn the reset into a
    /// dead end — the opposite of "let me test this again".
    /// </summary>
    [Fact]
    public async Task Booth_member_reset_hard_deletes_rather_than_tombstoning()
    {
        var (db, ev) = await SeedAsync();
        using (db)
        {
            var r = await new SponsorOnboardingResetService(db).ResetAsync(
                ev, CompanyId, resetWelcome: false, resetOverview: false,
                resetBoothMembers: true, resetAllTasks: false);

            Assert.Equal(1, r.BoothMembersRemoved);
            Assert.Empty(await db.SponsorBoothMembers.ToListAsync());   // gone, not DeletedAt-stamped
            Assert.Equal(TaskState.Open, (await TaskBySlugAsync(db, "register-booth-members")).State);
        }
    }

    /// <summary>The welcome switch re-arms the once-ever stamp AND clears the address-keyed ledger
    /// row (§340-A) — either one left behind would suppress the re-send.</summary>
    [Fact]
    public async Task Welcome_reset_rearms_the_stamp_and_clears_the_ledger()
    {
        var (db, ev) = await SeedAsync();
        using (db)
        {
            var r = await new SponsorOnboardingResetService(db).ResetAsync(
                ev, CompanyId, resetWelcome: true, resetOverview: false,
                resetBoothMembers: false, resetAllTasks: false);

            Assert.Equal(1, r.WelcomesRearmed);
            Assert.Equal(1, r.LedgerRowsRemoved);
            var p = await db.Participants.FirstAsync(x => x.Email == ContactEmail);
            Assert.Null(p.WelcomeWithLoginSentAt);
            Assert.Empty(await db.SentReminders.Where(s => s.ReminderType == "welcome").ToListAsync());
        }
    }

    /// <summary>
    /// The honesty contract. Re-opening everything while leaving the backing data in place is a
    /// reset that a sync pass will quietly undo — so it must WARN, naming both data-backed tasks.
    /// Silence here is what makes an operator report "the reset didn't reset" a second time.
    /// </summary>
    [Fact]
    public async Task All_tasks_reset_warns_when_the_backing_data_will_reclose_them()
    {
        var (db, ev) = await SeedAsync();
        using (db)
        {
            var r = await new SponsorOnboardingResetService(db).ResetAsync(
                ev, CompanyId, resetWelcome: false, resetOverview: false,
                resetBoothMembers: false, resetAllTasks: true);

            Assert.Contains(r.Warnings, w => w.Contains("Initial onboarding of sponsor"));
            Assert.Contains(r.Warnings, w => w.Contains("Register booth members"));
        }
    }

    /// <summary>…and stays QUIET when both switches were ticked, so the warning block means something.</summary>
    [Fact]
    public async Task No_warnings_when_the_backing_data_was_cleared_too()
    {
        var (db, ev) = await SeedAsync();
        using (db)
        {
            var r = await new SponsorOnboardingResetService(db).ResetAsync(
                ev, CompanyId, resetWelcome: true, resetOverview: true,
                resetBoothMembers: true, resetAllTasks: true);

            Assert.True(r.Ok);
            Assert.Empty(r.Warnings);
        }
    }

    /// <summary>
    /// A company with a Zoho exhibitor record gets an explicit warning: the booth sync pulls members
    /// back FROM Zoho, which re-closes the task. The reset must never delete people in Zoho itself —
    /// that is an irreversible outward-facing act, so it reports instead of reaching out.
    /// </summary>
    [Fact]
    public async Task Booth_reset_warns_that_zoho_will_reimport_and_touches_nothing_there()
    {
        var (db, ev) = await SeedAsync(zohoExhibitorId: "EXH-1");
        using (db)
        {
            var r = await new SponsorOnboardingResetService(db).ResetAsync(
                ev, CompanyId, resetWelcome: false, resetOverview: false,
                resetBoothMembers: true, resetAllTasks: false);

            Assert.Contains(r.Warnings, w => w.Contains("Zoho"));
            // The exhibitor link itself is untouched — a reset is not a disconnection.
            var info = await db.SponsorInfos.FirstAsync(s => s.SponsorCompanyId == CompanyId);
            Assert.Equal("EXH-1", info.ZohoExhibitorId);
        }
    }

    /// <summary>
    /// The sponsor-wall task cannot be reset from here (the signal is the file in SharePoint), so
    /// when such a file is on record the reset says so rather than appearing to have worked.
    /// </summary>
    [Fact]
    public async Task Wall_upload_on_file_produces_the_cannot_reset_warning()
    {
        var (db, ev) = await SeedAsync();
        using (db)
        {
            var loc = new SponsorUploadLocation
            {
                EventId = ev, SponsorCompanyId = CompanyId, CompanyName = "Fake Co",
                FolderKey = "SPONSORWALL", Subfolder = "wall", FolderPath = "/wall",
            };
            db.SponsorUploadLocations.Add(loc);
            await db.SaveChangesAsync();
            db.SponsorUploadFiles.Add(new SponsorUploadFile
            {
                SponsorUploadLocationId = loc.Id, FileName = "wall.ai", GraphItemId = "g1",
            });
            await db.SaveChangesAsync();

            var r = await new SponsorOnboardingResetService(db).ResetAsync(
                ev, CompanyId, false, resetOverview: true, resetBoothMembers: true, resetAllTasks: true);

            Assert.Contains(SponsorOnboardingResetService.WallUploadWarning, r.Warnings);
        }
    }

    /// <summary>Guard rails: an unknown company and an empty selection both refuse, and neither
    /// touches a thing — so a mis-click cannot quietly half-reset a real sponsor.</summary>
    [Fact]
    public async Task Unknown_company_or_empty_selection_is_refused_without_changes()
    {
        var (db, ev) = await SeedAsync();
        using (db)
        {
            var svc = new SponsorOnboardingResetService(db);

            var unknown = await svc.ResetAsync(ev, "999", true, true, true, true);
            Assert.False(unknown.Ok);

            var nothing = await svc.ResetAsync(ev, CompanyId, false, false, false, false);
            Assert.False(nothing.Ok);

            // Untouched: the description, the booth member and both Done tasks.
            var info = await db.SponsorInfos.FirstAsync(s => s.SponsorCompanyId == CompanyId);
            Assert.False(string.IsNullOrEmpty(info.CompanyDescription));
            Assert.Single(await db.SponsorBoothMembers.ToListAsync());
            Assert.Equal(TaskState.Done, (await TaskBySlugAsync(db, "register-booth-members")).State);
        }
    }

    /// <summary>Edition-scoped, like every other organizer action: another edition's identically-keyed
    /// sponsor tasks and data must not be collateral damage.</summary>
    [Fact]
    public async Task Another_edition_with_the_same_company_id_is_untouched()
    {
        var (db, ev) = await SeedAsync();
        using (db)
        {
            var other = new Event
            {
                Code = "T26", CommunityName = "C", DisplayName = "C 2026", IsActive = false,
            };
            db.Events.Add(other);
            await db.SaveChangesAsync();
            db.SponsorInfos.Add(new SponsorInfo
            {
                EventId = other.Id, SponsorCompanyId = CompanyId,
                CompanyDescription = "Last year's text.",
            });
            db.SponsorBoothMembers.Add(new SponsorBoothMember
            {
                EventId = other.Id, SponsorCompanyId = CompanyId,
                FirstName = "Old", LastName = "Booth", Email = "old@example.test",
            });
            await db.SaveChangesAsync();

            await new SponsorOnboardingResetService(db).ResetAsync(
                ev, CompanyId, true, true, resetBoothMembers: true, resetAllTasks: true);

            var kept = await db.SponsorInfos.FirstAsync(s => s.EventId == other.Id);
            Assert.Equal("Last year's text.", kept.CompanyDescription);
            Assert.Single(await db.SponsorBoothMembers.Where(m => m.EventId == other.Id).ToListAsync());
        }
    }
}
