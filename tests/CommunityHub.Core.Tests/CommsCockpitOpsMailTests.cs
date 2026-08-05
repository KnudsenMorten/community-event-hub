using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §818 — the Comms cockpit must show the mail that carries NO EDITION STAMP.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-04 (§815): <i>"why can I not see the emails sent here in a logged form"</i>.
/// <c>LoggingEmailSender</c> writes <c>EventId = ctx?.EventId ?? 0</c>, so any send whose ambient
/// <c>EmailContext</c> names no edition lands on event 0 — and every cockpit view is edition-scoped.
/// In PROD that hid 375 rows sent over six weeks.</para>
///
/// <para>🔒 The split these tests pin is by AUDIENCE, read from the mail's own TEMPLATE IDENTITY —
/// never from the recipient address. The address test was written first, passed every unit test, and
/// the first real render on DEV showed the section empty beside 25 live engine alerts: they go to
/// <c>mok@</c>, which is also the organizer's own participant address. Getting either side wrong is
/// a real regression — fold the alerts in and 252 of them bury the participant mail (§815.1), leave
/// the participant mail out and "did she get her evaluation results" stays unanswerable.</para>
/// </remarks>
public sealed class CommsCockpitOpsMailTests
{
    private const int EventId = 1;
    private static readonly DateTimeOffset Now = new(2027, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"comms-ops-{Guid.NewGuid():N}")
            .Options);

    private static CommsCockpitService NewSvc(CommunityHubDbContext db) =>
        new(db, new FixedClock(Now));

    /// <summary>Seeds one edition with one speaker; returns that speaker's participant id.</summary>
    private static async Task<int> SeedAsync(CommunityHubDbContext db)
    {
        db.Events.Add(new Event
        {
            Id = EventId, Code = "CX27", CommunityName = "Ops Test",
            DisplayName = "Ops Mail 2027",
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
            IsActive = true,
        });

        var speaker = new Participant
        {
            EventId = EventId,
            Email = "kim@example.org",
            SecondaryEmail = "kim.private@example.net",
            FullName = "Kim Speaker",
            Role = ParticipantRole.Speaker, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(speaker);
        await db.SaveChangesAsync();
        return speaker.Id;
    }

    private static void Log(
        CommunityHubDbContext db, int eventId, string to, string category,
        DateTimeOffset at, bool success = true, string? error = null, int? pid = null,
        string? subject = null, string? template = null)
        => db.EmailLogs.Add(new EmailLog
        {
            EventId = eventId, ToEmail = to, ActualToEmail = success ? to : string.Empty,
            Category = category, Subject = subject ?? $"{category} subject",
            TemplateName = template,
            Success = success, Error = error, SentAt = at, ParticipantId = pid,
        });

    [Fact]
    public async Task Engine_alerts_are_listed_in_their_own_section_and_nowhere_else()
    {
        using var db = NewDb();
        await SeedAsync(db);

        // The three shapes actually observed in PROD on 2026-08-04.
        Log(db, 0, "mok@expertslive.dk", "engine-alert", Now.AddHours(-2),
            subject: "[PROD] Engine FAILED: EvaluationReportPublishJob");
        Log(db, 0, "info@expertslive.dk", "engine-alert", Now.AddHours(-3),
            subject: "[PROD] e-conomic: 2 new draft invoice(s) created");
        Log(db, 0, "mok@expertslive.dk", "other", Now.AddHours(-4),
            subject: "Sponsor ERP/webshop reconcile — action needed");
        await db.SaveChangesAsync();

        var snap = await NewSvc(db).BuildAsync(EventId);

        Assert.Equal(3, snap.OpsMail.Count);
        Assert.Equal(3, snap.OpsMailTotal);
        Assert.Equal(3, snap.OpsMailSent);
        Assert.Equal(0, snap.OpsMailFailed);

        // 🔒 Never mixed into the participant views — the whole point of the split.
        Assert.Empty(snap.Timeline);
        Assert.Empty(snap.WhoGotWhat);
        Assert.Empty(snap.Campaigns);
        Assert.Empty(snap.ResendCandidates);
        Assert.Equal(0, snap.TotalEmails);

        // Newest first, like every other list on the page.
        Assert.Equal("[PROD] Engine FAILED: EvaluationReportPublishJob", snap.OpsMail[0].Title);
    }

    [Fact]
    public async Task An_ops_alert_that_did_not_arrive_is_counted_as_failed()
    {
        using var db = NewDb();
        await SeedAsync(db);

        Log(db, 0, "mok@expertslive.dk", "engine-alert", Now.AddHours(-1));
        Log(db, 0, "mok@expertslive.dk", "engine-alert", Now.AddHours(-2),
            success: false, error: "SMTP 550 mailbox unavailable");
        // 🔑 Ops mail is ring-EXEMPT by construction, so a "drop" on this side is not the system
        // obeying a ring (§650) — it is an alert nobody received, and it must read as a failure.
        Log(db, 0, "info@expertslive.dk", "engine-alert", Now.AddHours(-3),
            success: false, error: "Ring-dropped (recipient outside the released ring) — not sent.");
        await db.SaveChangesAsync();

        var snap = await NewSvc(db).BuildAsync(EventId);

        Assert.Equal(1, snap.OpsMailSent);
        Assert.Equal(2, snap.OpsMailFailed);
    }

    [Fact]
    public async Task Unstamped_mail_to_a_participant_lands_on_that_person_not_in_ops()
    {
        using var db = NewDb();
        var speakerId = await SeedAsync(db);

        // The real §818 case: eleven speakers were mailed their evaluation results with no
        // edition and no participant id on the context — but WITH the mail's template identity,
        // which is what tells the cockpit this is a person's mail and not an alert.
        Log(db, 0, "kim@example.org", "session-eval", Now.AddDays(-2),
            subject: "Your evaluation results are ready — Test Session",
            template: "session-evaluation-report-ready");
        // A stamped mail for the same person, so the fold has to MERGE rather than replace.
        Log(db, EventId, "kim@example.org", "welcome", Now.AddDays(-5), pid: speakerId);
        // Ops mail alongside it, to prove the partition rather than a blanket include.
        Log(db, 0, "info@expertslive.dk", "engine-alert", Now.AddDays(-1));
        await db.SaveChangesAsync();

        var snap = await NewSvc(db).BuildAsync(EventId);

        var row = Assert.Single(snap.WhoGotWhat);
        Assert.Equal("kim@example.org", row.Email);
        Assert.Equal(2, row.Sent);
        Assert.Contains(row.Messages, m => m.Category == "session-eval");

        Assert.Equal(2, snap.Timeline.Count);
        Assert.Equal(2, snap.TotalEmails);
        Assert.Contains(snap.Campaigns, c => c.Category == "session-eval");

        // The alert stayed on its own side.
        var ops = Assert.Single(snap.OpsMail);
        Assert.Equal("info@expertslive.dk", ops.Recipient);
    }

    [Fact]
    public async Task An_alert_sent_to_the_organizers_own_address_is_still_ops()
    {
        using var db = NewDb();
        var speakerId = await SeedAsync(db);

        // 🔒 THE REGRESSION THAT ONLY A RENDER FOUND. The first cut partitioned on the RECIPIENT
        // ADDRESS, every unit test passed, and DEV then showed the section empty beside 25 live
        // engine alerts — because they go to the operator's address, which is also his own
        // PARTICIPANT address. What a mail IS cannot depend on who happens to read it.
        Log(db, 0, "kim@example.org", "engine-alert", Now.AddHours(-1),
            subject: "[PROD] Engine FAILED: SponsorOrderPullJob");
        // …while a real mail to the same person, carrying a registered template, still folds.
        Log(db, 0, "kim@example.org", "session-eval", Now.AddHours(-2),
            template: "session-evaluation-report-ready");
        Log(db, EventId, "kim@example.org", "welcome", Now.AddHours(-3), pid: speakerId);
        await db.SaveChangesAsync();

        var snap = await NewSvc(db).BuildAsync(EventId);

        var ops = Assert.Single(snap.OpsMail);
        Assert.Equal("[PROD] Engine FAILED: SponsorOrderPullJob", ops.Title);

        var row = Assert.Single(snap.WhoGotWhat);
        Assert.Equal(2, row.Sent);                       // welcome + evaluation, NOT the alert
        Assert.DoesNotContain(row.Messages, m => m.Category == "engine-alert");
    }

    [Fact]
    public async Task A_secondary_address_is_no_longer_what_decides_it()
    {
        using var db = NewDb();
        await SeedAsync(db);

        // Routed to the participant's SECONDARY address and carrying its template identity —
        // still that person's mail. The address is incidental; the identity decides.
        Log(db, 0, "kim.private@example.net", "session-eval", Now.AddDays(-1),
            template: "session-evaluation-report-ready");
        await db.SaveChangesAsync();

        var snap = await NewSvc(db).BuildAsync(EventId);

        Assert.Empty(snap.OpsMail);
        Assert.Single(snap.WhoGotWhat);
    }

    [Fact]
    public async Task Folded_participant_mail_is_never_offered_for_resend()
    {
        using var db = NewDb();
        await SeedAsync(db);

        // 🔒 An unstamped row carries no ParticipantId. An ADDRESS match tells you whose mail it
        // is; it does not tell you who to send it to — and a resend needs a person to target.
        Log(db, 0, "kim@example.org", "session-eval", Now.AddDays(-1),
            success: false, error: "SMTP 550 mailbox unavailable",
            template: "session-evaluation-report-ready");
        await db.SaveChangesAsync();

        var snap = await NewSvc(db).BuildAsync(EventId);

        Assert.Single(snap.WhoGotWhat);
        Assert.Equal(1, snap.EmailsFailed);
        Assert.Empty(snap.ResendCandidates);
    }

    [Fact]
    public async Task Ops_mail_obeys_the_same_thirty_day_window_as_the_rest_of_the_page()
    {
        using var db = NewDb();
        await SeedAsync(db);

        Log(db, 0, "mok@expertslive.dk", "engine-alert", Now.AddDays(-2));
        Log(db, 0, "mok@expertslive.dk", "engine-alert", Now.AddDays(-45));
        await db.SaveChangesAsync();

        var snap = await NewSvc(db).BuildAsync(EventId);

        Assert.Equal(1, snap.OpsMailTotal);
        Assert.Single(snap.OpsMail);
    }

    [Fact]
    public async Task Reading_the_ops_section_writes_nothing()
    {
        using var db = NewDb();
        await SeedAsync(db);

        Log(db, 0, "mok@expertslive.dk", "engine-alert", Now.AddHours(-1));
        Log(db, 0, "kim@example.org", "session-eval", Now.AddHours(-2),
            template: "session-evaluation-report-ready");
        await db.SaveChangesAsync();

        var before = await db.EmailLogs.CountAsync();
        var svc = NewSvc(db);
        var first = await svc.BuildAsync(EventId);
        var second = await svc.BuildAsync(EventId);

        Assert.Equal(before, await db.EmailLogs.CountAsync());
        Assert.Equal(first.OpsMail.Count, second.OpsMail.Count);
        Assert.Equal(first.TotalEmails, second.TotalEmails);
    }
}
