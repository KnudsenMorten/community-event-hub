using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §964 — THE "RESEND WELCOME" BUTTON MUST ACTUALLY RE-SEND.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-07: <i>"verify resend welcome button logic in participants works as
/// expected"</i>, then <i>"see no emails"</i>. It did not work. <c>EditParticipant</c> called
/// <see cref="WelcomeEmailService.SendWelcomeAsync"/> <b>without <c>force</c></b>, so the once-ever
/// <c>SentReminder</c> ledger short-circuited every click on a person who had already been welcomed:
/// nothing was sent, and because the send never reached the transport there was <b>no EmailLog row
/// either</b> — so it was invisible in the Email Log as well. The helper text under the button even
/// said <i>"this will not send again"</i>, directly under a button labelled <b>Resend</b>.</para>
///
/// <para>🔑 <b>The service was already correct.</b> <c>force</c> existed, a forced resend
/// deliberately adds no duplicate ledger row, and §234 makes a ring-dropped send return
/// <c>false</c> so the ledger is not written and a later reconcile can retry. Only the caller was
/// wrong — which is why these tests pin the SERVICE contract the page now depends on.</para>
///
/// <para>FAKE names and addresses only.</para>
/// </remarks>
public sealed class WelcomeResendForcesASecondSendTests
{
    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-08-07T09:00:00Z");
    }

    private static WelcomeEmailService NewService(
        CommunityHub.Core.Data.CommunityHubDbContext db, CapturingEmailSender sender)
    {
        var templates = new EmailTemplateProvider(Options.Create(new EmailTemplateOptions
        {
            TemplateDirectory = RepoPaths.EmailTemplates(),
            PrivateTemplateDirectory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "ceh-no-private-welcome-964"),
        }));
        return new WelcomeEmailService(db, templates, sender, new FixedClock());
    }

    private static async Task<int> SeedAsync(
        CommunityHub.Core.Data.CommunityHubDbContext db,
        ParticipantRole role = ParticipantRole.Volunteer)
    {
        var ev = new Event { CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        var p = new Participant
        {
            EventId = ev.Id, Email = "pat.lee@example.test", FullName = "Pat Lee",
            Role = role, IsActive = true,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    /// <summary>
    /// 🔴 THE REPORTED BUG, as a test: a second click WITHOUT force sends nothing. This is the
    /// behaviour the page had, kept here so the regression is recognisable rather than mysterious.
    /// </summary>
    [Fact]
    public async Task Without_force_a_second_send_does_nothing()
    {
        using var db = ScenarioFixture.NewDb();
        var id = await SeedAsync(db);
        var sender = new CapturingEmailSender();
        var svc = NewService(db, sender);

        Assert.True(await svc.SendWelcomeAsync(id));
        var afterFirst = sender.Sent.Count;

        Assert.False(await svc.SendWelcomeAsync(id));          // the "Resend" click, as it was
        Assert.Equal(afterFirst, sender.Sent.Count);            // nothing left the building
    }

    /// <summary>✅ THE FIX: with force, the mail is actually sent a second time.</summary>
    [Fact]
    public async Task With_force_the_welcome_is_sent_again()
    {
        using var db = ScenarioFixture.NewDb();
        var id = await SeedAsync(db);
        var sender = new CapturingEmailSender();
        var svc = NewService(db, sender);

        Assert.True(await svc.SendWelcomeAsync(id));
        var afterFirst = sender.Sent.Count;

        Assert.True(await svc.SendWelcomeAsync(id, default, force: true));
        Assert.Equal(afterFirst + 1, sender.Sent.Count);
        Assert.Equal("pat.lee@example.test", sender.Sent[^1].To);
    }

    /// <summary>
    /// 🔒 A forced resend must NOT add a second ledger row. The ledger answers "has this person ever
    /// been welcomed?", and a resend does not change that answer — duplicating it would corrupt the
    /// question for every other reader (the reconcile job, the page's own badge).
    /// </summary>
    [Fact]
    public async Task A_forced_resend_adds_no_duplicate_ledger_row()
    {
        using var db = ScenarioFixture.NewDb();
        var id = await SeedAsync(db);
        var svc = NewService(db, new CapturingEmailSender());

        await svc.SendWelcomeAsync(id);
        var after1 = await db.SentReminders.CountAsync();

        await svc.SendWelcomeAsync(id, default, force: true);
        await svc.SendWelcomeAsync(id, default, force: true);

        Assert.Equal(after1, await db.SentReminders.CountAsync());
        Assert.Equal(1, after1);
    }

    /// <summary>
    /// 🔒 FORCE IS NOT A BYPASS OF EVERYTHING — only of the once-ever guard. A deactivated
    /// participant is still refused, so "resend" can never mail somebody who has been switched off.
    /// </summary>
    [Fact]
    public async Task Force_does_not_override_the_deactivated_check()
    {
        using var db = ScenarioFixture.NewDb();
        var id = await SeedAsync(db);
        var sender = new CapturingEmailSender();
        var svc = NewService(db, sender);

        await svc.SendWelcomeAsync(id);
        var afterFirst = sender.Sent.Count;

        var p = await db.Participants.FindAsync(id);
        p!.IsActive = false;
        await db.SaveChangesAsync();

        Assert.False(await svc.SendWelcomeAsync(id, default, force: true));
        Assert.Equal(afterFirst, sender.Sent.Count);
    }

    /// <summary>
    /// 🔒 And force does not turn an attendee into a legacy-welcome recipient — they get the Master
    /// Class selection invite instead, and a double welcome is exactly what that skip prevents.
    /// </summary>
    [Fact]
    public async Task Force_does_not_override_the_attendee_skip()
    {
        using var db = ScenarioFixture.NewDb();
        var id = await SeedAsync(db, ParticipantRole.Attendee);
        var sender = new CapturingEmailSender();
        var svc = NewService(db, sender);

        Assert.False(await svc.SendWelcomeAsync(id, default, force: true));
        Assert.Empty(sender.Sent);
    }
}
