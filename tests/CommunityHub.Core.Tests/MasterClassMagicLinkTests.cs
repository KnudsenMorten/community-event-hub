using CommunityHub.Core.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §169 for the MASTER CLASS attendee emails. A 2-day-ticket holder is provisioned a
/// login-capable, Attendee-role <see cref="Participant"/> (by Email + EventId) by
/// <see cref="AttendeeWelcomeProvisioningService"/>, so every Master Class email to that
/// attendee can carry the recipient's PERSONAL auto-login magic-link: passing the
/// resolved participant id to <see cref="EmailTemplateProvider.NewTokenSet"/> rewrites
/// the generic <c>{{hubUrl}}</c> CTA to <c>{HubUrl}/go/{token}</c> and mints the
/// participant's standing <see cref="MagicLinkGrant"/>.
///
/// <para>FAIL-SAFE: an attendee with no Participant yet keeps the PLAIN hub URL (no grant
/// minted) while the Master-Class-specific <c>selectionUrl</c> / self-service deep-link
/// still renders — a send never breaks on this layer. Mirrors
/// <see cref="EmailMagicLinkSenderCoverageTests"/> / <see cref="EmailMagicLinkSeamTests"/>:
/// EF in-memory + an ephemeral DataProtection provider + the REAL shipped templates,
/// with the real <see cref="IEmailMagicLinkService"/> wired into the template provider's
/// scope so the <c>/go</c> seam actually fires. FAKE names only.</para>
/// </summary>
public sealed class MasterClassMagicLinkTests
{
    private const string Origin = "https://hub.example";

    private static readonly DateTimeOffset Now = new(2026, 6, 28, 9, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class NoOpContext : IEmailContextAccessor
    {
        public EmailContext? Current => null;
        private sealed class D : IDisposable { public void Dispose() { } }
        public IDisposable Set(EmailContext c) => new D();
    }

    /// <summary>
    /// A DI container with the DbContext + the REAL §169 magic-link service over ONE
    /// shared in-memory store (the template provider opens its own scope to mint the
    /// link, so the name must be fixed or the magic service would see an empty db).
    /// </summary>
    private static ServiceProvider BuildServices()
    {
        var dbName = $"mcmagic-{Guid.NewGuid():N}";
        return new ServiceCollection()
            .AddDbContext<CommunityHubDbContext>(o => o.UseInMemoryDatabase(dbName))
            .AddSingleton<IDataProtectionProvider>(DataProtectionProvider.Create(
                new DirectoryInfo(Path.Combine(Path.GetTempPath(), "ceh-mcmagic"))))
            .AddSingleton<TimeProvider>(new FixedClock())
            .AddScoped<IEmailMagicLinkService, EmailMagicLinkService>()
            .BuildServiceProvider();
    }

    private static EmailTemplateProvider RealTemplatesWithMagic(ServiceProvider sp) =>
        new(
            Options.Create(new EmailTemplateOptions
            {
                // GENERIC shipped templates (their CTA is {{hubUrl}}); point the private
                // layer at an absent dir so an edition's private copy never shadows them.
                TemplateDirectory = RepoPaths.EmailTemplates(),
                PrivateTemplateDirectory = Path.Combine(Path.GetTempPath(), "ceh-no-private-mcmagic"),
                HubUrl = Origin,
            }),
            sp.GetRequiredService<IServiceScopeFactory>(),
            emailContext: null);

    private static Event NewEvent() => new()
    {
        Code = "TC27",
        CommunityName = "Test Community",
        DisplayName = "Test Community 2027",
        StartDate = new DateOnly(2027, 2, 9),
        EndDate = new DateOnly(2027, 2, 10),
        IsActive = true,
    };

    /// <summary>The §169 standing grant minted for a participant (or null when none was).</summary>
    private static async Task<MagicLinkGrant?> GrantForAsync(CommunityHubDbContext db, int participantId) =>
        await db.MagicLinkGrants.SingleOrDefaultAsync(g => g.ParticipantId == participantId);

    /// <summary>The <c>/go/{token}</c> magic path that follows <see cref="Origin"/> in a rendered CTA.</summary>
    private static string ExtractGoToken(string html)
    {
        var marker = $"{Origin}/go/";
        var i = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(i >= 0, $"expected a {marker} magic-link in the rendered body");
        var start = i + marker.Length;
        var end = start;
        while (end < html.Length && html[end] is not ('"' or '/' or '?' or '<')) end++;
        return html[start..end];
    }

    /// <summary>A provisioned login-capable, Attendee-role participant for the email (mirrors AttendeeWelcomeProvisioningService).</summary>
    private static Participant AttendeeParticipant(int eventId, string email) => new()
    {
        EventId = eventId, Email = email, FullName = "Sample Attendee",
        Role = ParticipantRole.Attendee, IsActive = true,
        LifecycleState = ParticipantLifecycleState.Active,
    };

    // ----------------------------------------------------------------------
    // Selection invite — the prime attendee email.
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Selection_invite_to_a_provisioned_attendee_mints_the_personal_magic_link()
    {
        await using var sp = BuildServices();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();

        var ev = NewEvent();
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        var att = new Attendee
        {
            EventId = ev.Id, Email = "attendee@example.com", FirstName = "Sample", LastName = "Attendee",
            TicketStatus = TicketStatus.TwoDay,
        };
        db.Attendees.Add(att);
        var p = AttendeeParticipant(ev.Id, "attendee@example.com");
        db.Participants.Add(p);
        await db.SaveChangesAsync();

        var sender = new CapturingEmailSender();
        var svc = new MasterClassEmailService(
            db, sender, new NoOpContext(), new MasterClassSignupService(db), RealTemplatesWithMagic(sp));

        Assert.True(await svc.SendSelectionInviteAsync(att.Id, Origin));

        // The send still happens normally (ring-gate / EmailLog path unaffected): the
        // recipient + subject are exactly the attendee's invite.
        var m = Assert.Single(sender.Messages);
        Assert.Equal("attendee@example.com", m.To);
        Assert.Contains("Choose your Master Class", m.Subject);

        // The Master-Class self-service deep-link (selectionUrl) STILL renders…
        Assert.Contains("MyMasterClass?t=", m.Html);
        // …and the participant's standing §169 magic-link grant was minted for the hub CTA.
        var grant = await GrantForAsync(db, p.Id);
        Assert.NotNull(grant);
        Assert.Equal(EmailMagicLinkService.PurposeName, grant!.Purpose);
        Assert.True(grant.MultiUse);
    }

    [Fact]
    public async Task Selection_invite_without_a_participant_falls_back_to_plain_and_keeps_selection_url()
    {
        await using var sp = BuildServices();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();

        var ev = NewEvent();
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        var att = new Attendee
        {
            EventId = ev.Id, Email = "noparticipant@example.com", FirstName = "Sample", LastName = "Attendee",
            TicketStatus = TicketStatus.TwoDay,
        };
        db.Attendees.Add(att);            // NO Participant provisioned for this email yet.
        await db.SaveChangesAsync();

        var sender = new CapturingEmailSender();
        var svc = new MasterClassEmailService(
            db, sender, new NoOpContext(), new MasterClassSignupService(db), RealTemplatesWithMagic(sp));

        // Never throws despite the missing participant.
        Assert.True(await svc.SendSelectionInviteAsync(att.Id, Origin));

        var m = Assert.Single(sender.Messages);
        Assert.Contains("MyMasterClass?t=", m.Html);     // self-service deep-link still renders
        Assert.DoesNotContain($"{Origin}/go/", m.Html);  // no magic hub link surfaced
        Assert.Empty(db.MagicLinkGrants);                // and no grant minted (fail-safe)
    }

    // ----------------------------------------------------------------------
    // PART B: re-enabled provisioning CLOSES the §169 gap. AttendeeBackstageSyncJob
    // now calls AttendeeWelcomeProvisioningService.ProvisionAsync before the attendee
    // emails, so a 2-day holder gets a login-capable Attendee Participant and the
    // SAME selection invite that was PLAIN now carries their /go magic-link. Idempotent.
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Provisioning_a_two_day_holder_makes_the_selection_invite_bind_the_magic_link_idempotently()
    {
        await using var sp = BuildServices();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();

        var ev = NewEvent();
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        var att = new Attendee
        {
            EventId = ev.Id, Email = "attendee@example.com", FirstName = "Sample", LastName = "Attendee",
            TicketStatus = TicketStatus.TwoDay,
        };
        db.Attendees.Add(att);            // NO Participant yet (mirrors a fresh Zoho sync).
        await db.SaveChangesAsync();

        // Provision (what the re-enabled job now does after the sync) → exactly one
        // ACTIVE, login-capable, Attendee-role Participant for the 2-day holder.
        var provisioning = new AttendeeWelcomeProvisioningService(
            db, new FixedClock(), NullLogger<AttendeeWelcomeProvisioningService>.Instance);
        var created = await provisioning.ProvisionAsync(ev.Id);
        var pid = Assert.Single(created);
        var p = await db.Participants.SingleAsync(x => x.Id == pid);
        Assert.Equal(ParticipantRole.Attendee, p.Role);
        Assert.True(p.IsActive);
        Assert.Equal(ParticipantLifecycleState.Active, p.LifecycleState);

        // The selection invite now carries the attendee's personal /go magic-link.
        var sender = new CapturingEmailSender();
        var svc = new MasterClassEmailService(
            db, sender, new NoOpContext(), new MasterClassSignupService(db), RealTemplatesWithMagic(sp));
        Assert.True(await svc.SendSelectionInviteAsync(att.Id, Origin));

        var m = Assert.Single(sender.Messages);
        Assert.Contains("MyMasterClass?t=", m.Html);     // self-service deep-link still renders
        // The §169 seam minted the now-provisioned attendee's standing magic-link grant
        // (the same grant the hub CTA resolves to) — the gap that the plain fail-safe hit
        // before provisioning is now closed.
        var grant = await GrantForAsync(db, pid);
        Assert.NotNull(grant);
        Assert.True(grant!.MultiUse);

        // Idempotent: a second provisioning pass creates nothing (no duplicate Participant).
        Assert.Empty(await provisioning.ProvisionAsync(ev.Id));
        Assert.Equal(1, await db.Participants.CountAsync(x => x.EventId == ev.Id));
    }

    // ----------------------------------------------------------------------
    // Confirmed-seat email — keeps the .ics + self-service deep-link.
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Confirmed_email_to_a_provisioned_attendee_mints_the_magic_link_and_keeps_ics()
    {
        await using var sp = BuildServices();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();

        var ev = NewEvent();
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        var session = new Session { EventId = ev.Id, Title = "Deep Dive MC", Type = SessionType.MasterClass, MasterClassCapacity = 5 };
        db.Sessions.Add(session);
        var att = new Attendee
        {
            EventId = ev.Id, Email = "attendee@example.com", FirstName = "Sample", LastName = "Attendee",
            TicketStatus = TicketStatus.TwoDay,
        };
        db.Attendees.Add(att);
        var p = AttendeeParticipant(ev.Id, "attendee@example.com");
        db.Participants.Add(p);
        await db.SaveChangesAsync();

        var sender = new CapturingEmailSender();
        var signups = new MasterClassSignupService(db);
        await signups.SignUpAsync(ev.Id, att.Id, session.Id);
        var signupId = (await signups.SignupIdAsync(ev.Id, att.Id, session.Id))!.Value;

        var svc = new MasterClassEmailService(db, sender, new NoOpContext(), signups, RealTemplatesWithMagic(sp));
        await svc.SendConfirmedAsync(signupId, Origin);

        var m = Assert.Single(sender.Messages);
        Assert.Contains("MyMasterClass.ics", m.Html);    // .ics download still renders
        var grant = await GrantForAsync(db, p.Id);        // hub CTA = personal magic-link
        Assert.NotNull(grant);
        Assert.True(grant!.MultiUse);
    }

    // ----------------------------------------------------------------------
    // Promotion email (MasterClassPromotionEmailService) — magic when known, plain when not.
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Promotion_email_mints_the_magic_link_for_a_provisioned_attendee_else_falls_back()
    {
        await using var sp = BuildServices();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();

        var ev = NewEvent();
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        var session = new Session { EventId = ev.Id, Title = "Deep Dive MC", Type = SessionType.MasterClass, MasterClassCapacity = 1 };
        db.Sessions.Add(session);
        var a1 = new Attendee { EventId = ev.Id, Email = "holder@example.com", FirstName = "Hold", LastName = "Er", TicketStatus = TicketStatus.TwoDay };
        var a2 = new Attendee { EventId = ev.Id, Email = "waiter@example.com", FirstName = "Wait", LastName = "Er", TicketStatus = TicketStatus.TwoDay };
        db.Attendees.AddRange(a1, a2);
        // Only the waitlisted attendee (the one who gets promoted + emailed) has a Participant.
        var pWaiter = AttendeeParticipant(ev.Id, "waiter@example.com");
        db.Participants.Add(pWaiter);
        await db.SaveChangesAsync();

        var signups = new MasterClassSignupService(db);
        await signups.SignUpAsync(ev.Id, a1.Id, session.Id);   // confirmed
        await signups.SignUpAsync(ev.Id, a2.Id, session.Id);   // waitlisted
        var promo = await signups.RemoveAsync(ev.Id, a1.Id, session.Id); // a2 promoted
        Assert.NotNull(promo!.PromotedSignupId);

        var sender = new CapturingEmailSender();
        var svc = new MasterClassPromotionEmailService(db, sender, new NoOpContext(), signups, RealTemplatesWithMagic(sp));

        Assert.True(await svc.SendPromotionAsync(promo.PromotedSignupId!.Value, Origin));
        var msg = Assert.Single(sender.Sent);
        Assert.Equal("waiter@example.com", msg.To);

        // The promoted attendee (provisioned) got their personal magic-link grant…
        Assert.NotNull(await GrantForAsync(db, pWaiter.Id));
        // …and idempotency is preserved (PromotionNotifiedAt stamped → no second send/grant churn).
        Assert.False(await svc.SendPromotionAsync(promo.PromotedSignupId!.Value, Origin));
        Assert.Single(sender.Sent);
        Assert.Single(db.MagicLinkGrants);   // exactly one standing grant, not one per email
    }

    // ----------------------------------------------------------------------
    // The pending-selection chaser email — the master-class email whose template
    // actually surfaces {{hubUrl}} as its CTA, so the magic link is delivered + visible.
    // Mirrors how AttendeeBackstageSyncJob.ChaseUnselectedTwoDayAsync builds the body.
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Pending_selection_email_hub_cta_is_the_recipients_magic_link_when_provisioned()
    {
        await using var sp = BuildServices();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();

        var ev = NewEvent();
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        var p = AttendeeParticipant(ev.Id, "attendee@example.com");
        db.Participants.Add(p);
        await db.SaveChangesAsync();

        var templates = RealTemplatesWithMagic(sp);

        // WITH a participant (the job resolves the id from the email): the {{hubUrl}}
        // "Select your Master Class" CTA becomes the recipient's /go magic-link.
        var withTokens = templates.NewTokenSet(p.Id);
        withTokens["firstName"] = "Sample";
        withTokens["eventDisplayName"] = ev.DisplayName;
        var withBody = templates.Render("pending-master-class-selection", withTokens).HtmlBody;

        var token = ExtractGoToken(withBody);
        var grant = await GrantForAsync(db, p.Id);
        Assert.NotNull(grant);
        Assert.Equal(EmailMagicLinkService.HashToken(token), grant!.TokenIdHash);

        // WITHOUT a participant (fail-safe): the same CTA keeps the plain hub URL.
        var plainTokens = templates.NewTokenSet();
        plainTokens["firstName"] = "Sample";
        plainTokens["eventDisplayName"] = ev.DisplayName;
        var plainBody = templates.Render("pending-master-class-selection", plainTokens).HtmlBody;
        Assert.Contains($"href=\"{Origin}\"", plainBody);
        Assert.DoesNotContain($"{Origin}/go/", plainBody);
    }
}
