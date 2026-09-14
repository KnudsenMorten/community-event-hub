using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1077 stage 4 — the group photo (linked to the EXISTING registration) and the weekly reminders
/// that stop the moment they are answered.
/// </summary>
/// <remarks>
/// <para>🔴 <b>Stopping is the feature being tested here, not the sending.</b> The wizard promises
/// <i>"one click and we stop asking"</i>; a loop that keeps running after a decline turns that
/// promise into a lie, weekly, in a customer's inbox.</para>
///
/// <para>🔑 The link tests exist because there were nearly TWO group-photo models. §25f built one in
/// June; §1077 describes one in August. Stage 4 joins them — §1076's lesson applied — so these pin
/// that one company gets one registration, one slot and one calendar entry.</para>
/// </remarks>
public sealed class VolumePackageStage4Tests
{
    private const int EventId = 42;
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"vp-s4-{Guid.NewGuid():N}").Options);

    private static async Task<VolumePackageCompany> SeedAsync(
        CommunityHubDbContext db, Action<VolumePackageCompany>? tweak = null, int attendees = 0,
        int twoDay = 0)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "ELDK27", CommunityName = "C", DisplayName = "D", IsActive = true,
            VenueName = "The Venue",
        });
        var company = new VolumePackageCompany
        {
            EventId = EventId, CustomName = "Globeteam", Domains = "globeteam.dk",
            ApproverEmail = "bea@globeteam.dk", ApproverName = "Bea Buyer",
            ApprovedGroupPhoto = true, LastQualifiedCount = 12, QualifiedNow = true,
            WizardToken = "tok-" + Guid.NewGuid().ToString("N"),
            WizardTokenExpiresAt = new DateTimeOffset(2027, 2, 15, 0, 0, 0, TimeSpan.Zero),
            WizardInvitedAt = Now.AddDays(-8),
        };
        tweak?.Invoke(company);
        db.VolumePackageCompanies.Add(company);

        for (var i = 1; i <= attendees; i++)
        {
            db.Attendees.Add(new Attendee
            {
                EventId = EventId, Email = $"p{i}@globeteam.dk",
                BackstageTicketId = Guid.NewGuid().ToString("N"), MirrorState = MirrorState.Active,
                TicketStatus = i <= twoDay ? TicketStatus.TwoDay : TicketStatus.Other,
            });
        }

        await db.SaveChangesAsync();
        return company;
    }

    private static VolumePackageGroupPhotoService NewPhoto(CommunityHubDbContext db) =>
        new(db, new VolumePackageQualificationService(db), new FixedClock(Now));

    private static (VolumePackageReminderService Svc, CapturingEmailSender Mail) NewReminders(
        CommunityHubDbContext db, DateTimeOffset? now = null)
    {
        var mail = new CapturingEmailSender();
        return (new VolumePackageReminderService(
            db, mail, new EmailContextAccessor(), new FixedClock(now ?? Now)), mail);
    }

    // =====================================================================
    //  The group photo — ONE registration, linked to the existing model
    // =====================================================================

    [Fact]
    public async Task Syncing_creates_one_registration_linked_to_the_company()
    {
        using var db = NewDb();
        var company = await SeedAsync(db);

        var first = await NewPhoto(db).SyncFromCompanyAsync(company.Id);
        var second = await NewPhoto(db).SyncFromCompanyAsync(company.Id);

        Assert.NotNull(first);
        Assert.Equal(first!.Id, second!.Id);          // refreshed, not duplicated
        Assert.Equal(company.Id, first.VolumePackageCompanyId);
        Assert.Equal("Globeteam", first.CompanyName);
        Assert.Single(db.GroupPhotoRegistrations);
    }

    /// <summary>
    /// 🔑 The ticket count is DERIVED now. The June entity says of it: <i>"entered manually by the
    /// organizer (there is no automated ticket-volume feed)"</i> — stage 1 built that feed.
    /// </summary>
    [Fact]
    public async Task The_ticket_count_comes_from_the_qualification_not_from_typing()
    {
        using var db = NewDb();
        var company = await SeedAsync(db);

        var reg = await NewPhoto(db).SyncFromCompanyAsync(company.Id);

        Assert.Equal(12, reg!.TicketCount);
        Assert.True(reg.Qualifies);
    }

    /// <summary>
    /// 🔴 The boundary the operator settled today — <i>"10 or more is correct"</i>. Exactly ten used
    /// to qualify for the volume package and be REFUSED the photo invite.
    /// </summary>
    [Fact]
    public async Task Exactly_ten_qualifies_for_the_photo_now()
    {
        using var db = NewDb();
        var company = await SeedAsync(db, c => c.LastQualifiedCount = 10);

        var reg = await NewPhoto(db).SyncFromCompanyAsync(company.Id);

        Assert.Equal(10, reg!.TicketCount);
        Assert.True(reg.Qualifies);
    }

    /// <summary>A photo nobody asked for is not scheduled.</summary>
    [Theory]
    [InlineData(false, false)]   // not approved
    [InlineData(true, true)]     // approved but declined afterwards
    public async Task A_company_that_did_not_ask_for_a_photo_gets_no_registration(
        bool approved, bool declined)
    {
        using var db = NewDb();
        var company = await SeedAsync(db, c =>
        {
            c.ApprovedGroupPhoto = approved;
            if (declined) c.DeclinedAt = Now;
        });

        Assert.Null(await NewPhoto(db).SyncFromCompanyAsync(company.Id));
        Assert.Empty(db.GroupPhotoRegistrations);
    }

    /// <summary>
    /// 🔒 A refresh must never move a slot. The photographer and the company have already been told
    /// a time; updating a contact name is not permission to change it.
    /// </summary>
    [Fact]
    public async Task Refreshing_never_moves_an_agreed_slot()
    {
        using var db = NewDb();
        var company = await SeedAsync(db);
        var reg = await NewPhoto(db).SyncFromCompanyAsync(company.Id);
        var slot = new DateTimeOffset(2027, 2, 10, 9, 30, 0, TimeSpan.Zero);
        reg!.ScheduledAtUtc = slot;
        reg.Location = "Main stage";
        await db.SaveChangesAsync();

        company.GroupPhotoContactName = "Cara Coordinator";
        company.GroupPhotoContactEmail = "cara@globeteam.dk";
        await db.SaveChangesAsync();
        var again = await NewPhoto(db).SyncFromCompanyAsync(company.Id);

        Assert.Equal(slot, again!.ScheduledAtUtc);
        Assert.Equal("Main stage", again.Location);
        Assert.Equal("Cara Coordinator", again.ContactName);
    }

    /// <summary>
    /// ⚠️ A 1-day holder is not at the pre-day, so a pre-day slot would photograph an empty space.
    /// The hint SUGGESTS: which company goes where is a scheduling decision this does not own.
    /// </summary>
    [Fact]
    public async Task The_day_hint_says_main_day_only_when_everybody_holds_a_one_day_ticket()
    {
        using var db = NewDb();
        var company = await SeedAsync(db, attendees: 11, twoDay: 0);

        var hint = await NewPhoto(db).DayHintAsync(company.Id);

        Assert.False(hint.PreDayPossible);
        Assert.True(hint.MainDayPossible);
        Assert.Contains("Main day only", hint.Advice);
    }

    [Fact]
    public async Task The_day_hint_offers_either_day_when_everybody_holds_a_two_day_ticket()
    {
        using var db = NewDb();
        var company = await SeedAsync(db, attendees: 11, twoDay: 11);

        var hint = await NewPhoto(db).DayHintAsync(company.Id);

        Assert.True(hint.PreDayPossible);
        Assert.Contains("Either day", hint.Advice);
    }

    /// <summary>
    /// 🔒 The coordinator's calendar file carries the SAME UID as the organizer's invite, so a moved
    /// slot updates an entry already forwarded rather than adding a second one to every colleague's
    /// calendar — and it names NO attendee, because it is meant to be passed on.
    /// </summary>
    [Fact]
    public async Task The_coordinators_calendar_file_shares_the_uid_and_invites_nobody()
    {
        using var db = NewDb();
        var company = await SeedAsync(db);
        var reg = await NewPhoto(db).SyncFromCompanyAsync(company.Id);
        reg!.ScheduledAtUtc = new DateTimeOffset(2027, 2, 10, 9, 30, 0, TimeSpan.Zero);
        await db.SaveChangesAsync();

        var ics = await NewPhoto(db).CoordinatorIcsAsync(company.Id);

        Assert.NotNull(ics);
        Assert.Contains($"group-photo-{EventId}-{reg.Id}@communityhub", ics);
        Assert.DoesNotContain("ATTENDEE", ics);
        Assert.Contains("Globeteam", ics);
    }

    [Fact]
    public async Task With_no_slot_there_is_no_calendar_file()
    {
        using var db = NewDb();
        var company = await SeedAsync(db);
        await NewPhoto(db).SyncFromCompanyAsync(company.Id);

        Assert.Null(await NewPhoto(db).CoordinatorIcsAsync(company.Id));
    }

    // =====================================================================
    //  The weekly reminder — and the four ways it stops
    // =====================================================================

    [Fact]
    public async Task An_invited_company_that_has_not_answered_is_reminded_after_a_week()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var (svc, mail) = NewReminders(db);

        var result = await svc.RunAsync(EventId);

        Assert.Equal(1, result.Reminded);
        var sent = Assert.Single(mail.Messages);
        Assert.Equal("bea@globeteam.dk", sent.To);
        // 🔑 The way OUT is in every reminder, not only the first invitation.
        Assert.Contains("do not want to participate", sent.Html);
    }

    /// <summary>
    /// 🔴 The four independent stops, each a separate REASON rather than one flag somebody could
    /// clear by accident.
    /// </summary>
    [Theory]
    [InlineData("completed")]
    [InlineData("declined")]
    [InlineData("never-invited")]
    [InlineData("link-revoked")]
    public async Task It_stops_for_every_reason_it_should(string why)
    {
        using var db = NewDb();
        await SeedAsync(db, c =>
        {
            switch (why)
            {
                case "completed": c.WizardCompletedAt = Now.AddDays(-1); break;
                case "declined": c.DeclinedAt = Now.AddDays(-1); break;
                case "never-invited": c.WizardInvitedAt = null; break;
                case "link-revoked": c.WizardTokenRevokedAt = Now.AddDays(-1); break;
            }
        });
        var (svc, mail) = NewReminders(db);

        Assert.Equal(0, (await svc.RunAsync(EventId)).Reminded);
        Assert.Empty(mail.Sent);
    }

    /// <summary>⚠️ Not yet a week since the invitation ⇒ nothing. Weekly means weekly.</summary>
    [Fact]
    public async Task It_does_not_chase_before_a_week_has_passed()
    {
        using var db = NewDb();
        await SeedAsync(db, c => c.WizardInvitedAt = Now.AddDays(-2));
        var (svc, mail) = NewReminders(db);

        Assert.Equal(0, (await svc.RunAsync(EventId)).Reminded);
        Assert.Empty(mail.Sent);
    }

    /// <summary>
    /// Running twice in one day sends ONE mail: the stamp moves, so a job that ticks daily (by
    /// design — see the job's remarks) does not chase daily.
    /// </summary>
    [Fact]
    public async Task Running_again_the_same_day_sends_nothing_more()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var (svc, mail) = NewReminders(db);

        await svc.RunAsync(EventId);
        await svc.RunAsync(EventId);

        Assert.Single(mail.Messages);
        var saved = await db.VolumePackageCompanies.SingleAsync();
        Assert.Equal(1, saved.WizardReminderCount);
        Assert.NotNull(saved.WizardRemindedAt);
    }

    /// <summary>…and a week after the reminder, it chases again — until answered.</summary>
    [Fact]
    public async Task A_week_after_the_reminder_it_chases_again()
    {
        using var db = NewDb();
        await SeedAsync(db);
        var (first, _) = NewReminders(db);
        await first.RunAsync(EventId);

        var (later, mail) = NewReminders(db, Now.AddDays(8));
        Assert.Equal(1, (await later.RunAsync(EventId)).Reminded);

        Assert.Equal(2, (await db.VolumePackageCompanies.SingleAsync()).WizardReminderCount);
        Assert.Single(mail.Messages);
    }
}
