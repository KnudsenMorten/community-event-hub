using System.Globalization;
using CommunityHub.Core.Audit;
using CommunityHub.Core.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Sessions;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Settings;
using CommunityHub.Core.Tests.Scenario;
using CommunityHub.Jobs;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §169 "for ALL emails" coverage. Proves the previously-uncovered single-recipient,
/// participant-addressed senders now render the hub CTA (<c>{{hubUrl}}</c>) as the
/// addressed participant's personal <c>/go/{token}</c> auto-login magic-link, and
/// documents the one intentional exception: a body composed for MANY recipients
/// (broadcast / a non-participant audience such as Master-Class attendees) carries no
/// single participant, so it keeps the PLAIN hub URL.
///
/// Mirrors <see cref="EmailMagicLinkSeamTests"/> / <see cref="EmailMagicLinkServiceTests"/>:
/// EF in-memory + an ephemeral DataProtection provider + the REAL shipped templates,
/// with the real <see cref="IEmailMagicLinkService"/> wired into the template
/// provider's scope so the <c>/go</c> seam actually fires. FAKE names only.
/// </summary>
public class EmailMagicLinkSenderCoverageTests
{
    private const string Origin = "https://hub.example";

    private static readonly DateTimeOffset Now = new(2026, 6, 28, 9, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class StubEnv : IEnvironmentInfo
    {
        public bool IsDevelopment => true;
        public string EnvironmentName => "Development";
    }

    /// <summary>
    /// A DI container with the DbContext + the REAL §169 magic-link service, so the
    /// template provider's scope can mint/resolve <c>/go</c> links against the same
    /// in-memory store the senders seed into.
    /// </summary>
    private static ServiceProvider BuildServices()
    {
        // ONE shared in-memory store across every scope (the template provider opens its
        // own scope to mint the magic link); the name must be fixed, not re-evaluated per
        // DbContext, or the magic service would see an empty db and fail-safe to plain.
        var dbName = $"magiccover-{Guid.NewGuid():N}";
        return new ServiceCollection()
            .AddDbContext<CommunityHubDbContext>(o => o.UseInMemoryDatabase(dbName))
            .AddSingleton<IDataProtectionProvider>(DataProtectionProvider.Create(
                new DirectoryInfo(Path.Combine(Path.GetTempPath(), "ceh-magiccover"))))
            .AddSingleton<TimeProvider>(new FixedClock())
            .AddScoped<IEmailMagicLinkService, EmailMagicLinkService>()
            .BuildServiceProvider();
    }

    private static EmailTemplateProvider RealTemplatesWithMagic(ServiceProvider sp) =>
        new(
            Options.Create(new EmailTemplateOptions
            {
                // Render against the GENERIC shipped templates (their CTA is {{hubUrl}});
                // point the private layer at an absent dir so an edition's private copy
                // never shadows them (mirrors WelcomeWithLoginEmailServiceTests).
                TemplateDirectory = RepoPaths.EmailTemplates(),
                PrivateTemplateDirectory = Path.Combine(Path.GetTempPath(), "ceh-no-private-templates"),
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

    // ----------------------------------------------------------------------
    // TaskReminderBuilder — sponsor coordinator FAN-OUT is personalized per recipient
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Sponsor_task_reminder_fan_out_gives_each_coordinator_their_OWN_magic_link()
    {
        await using var sp = BuildServices();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();

        var ev = NewEvent();
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        // §1001 — open the speaker-notice quiet period; these tests are about the mail itself.
        db.SessionSourceSettings.Add(new SessionSourceSetting
        {
            EventId = ev.Id, Source = SessionSourceKinds.Default,
            SpeakerScheduleNoticeFrom = new DateOnly(2020, 1, 1),
        });
        await db.SaveChangesAsync();

        // Two coordinator contacts of the same sponsor company + the assigned sponsor.
        Participant Sponsor(string email, bool coordinator) => new()
        {
            EventId = ev.Id, Email = email, FullName = "Sample Person",
            Role = ParticipantRole.Sponsor, SponsorCompanyId = "ACME",
            IsEventCoordinator = coordinator, IsActive = true,
        };
        var coord1 = Sponsor("coord1@example.com", coordinator: true);
        var coord2 = Sponsor("coord2@example.com", coordinator: true);
        db.Participants.AddRange(coord1, coord2);
        await db.SaveChangesAsync();

        db.Tasks.Add(new ParticipantTask
        {
            EventId = ev.Id, AssignedParticipantId = coord1.Id,
            Title = "Upload your booth artwork",
            DueDate = DateOnly.FromDateTime(Now.UtcDateTime), // due today → fires
            State = TaskState.Open,
        });
        await db.SaveChangesAsync();

        var builder = new TaskReminderBuilder(
            db, RealTemplatesWithMagic(sp), new FixedClock(), new SponsorRecipientResolver(db));

        var due = await builder.BuildDueAsync(ev.Id);

        // One personalized body per coordinator (not one shared body for many).
        Assert.Equal(2, due.Count);
        var t1 = ExtractGoToken(due.Single(m => m.RecipientEmail == "coord1@example.com").HtmlBody);
        var t2 = ExtractGoToken(due.Single(m => m.RecipientEmail == "coord2@example.com").HtmlBody);

        // Each coordinator's CTA is a /go magic-link, and the two tokens DIFFER —
        // a coordinator can never sign in AS the other coordinator.
        Assert.NotEqual(t1, t2);

        // Each token is the standing reusable grant for THAT coordinator.
        var g1 = await db.MagicLinkGrants.SingleAsync(g => g.ParticipantId == coord1.Id);
        var g2 = await db.MagicLinkGrants.SingleAsync(g => g.ParticipantId == coord2.Id);
        Assert.Equal(EmailMagicLinkService.HashToken(t1), g1.TokenIdHash);
        Assert.Equal(EmailMagicLinkService.HashToken(t2), g2.TokenIdHash);
    }

    [Fact]
    public async Task Non_sponsor_task_reminder_carries_the_assignees_magic_link()
    {
        await using var sp = BuildServices();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();

        var ev = NewEvent();
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        // §1001 — open the speaker-notice quiet period; these tests are about the mail itself.
        db.SessionSourceSettings.Add(new SessionSourceSetting
        {
            EventId = ev.Id, Source = SessionSourceKinds.Default,
            SpeakerScheduleNoticeFrom = new DateOnly(2020, 1, 1),
        });
        await db.SaveChangesAsync();

        var speaker = new Participant
        {
            EventId = ev.Id, Email = "speaker@example.com", FullName = "Sample Speaker",
            Role = ParticipantRole.Speaker, IsActive = true,
        };
        db.Participants.Add(speaker);
        await db.SaveChangesAsync();

        db.Tasks.Add(new ParticipantTask
        {
            EventId = ev.Id, AssignedParticipantId = speaker.Id,
            Title = "Submit your slides",
            DueDate = DateOnly.FromDateTime(Now.UtcDateTime),
            State = TaskState.Open,
        });
        await db.SaveChangesAsync();

        var builder = new TaskReminderBuilder(
            db, RealTemplatesWithMagic(sp), new FixedClock(), new SponsorRecipientResolver(db));

        var msg = Assert.Single(await builder.BuildDueAsync(ev.Id));
        var token = ExtractGoToken(msg.HtmlBody);
        var grant = await db.MagicLinkGrants.SingleAsync(g => g.ParticipantId == speaker.Id);
        Assert.Equal(EmailMagicLinkService.HashToken(token), grant.TokenIdHash);
    }

    // ----------------------------------------------------------------------
    // WelcomeWithLoginEmailService — the generic hub CTA routes through §169 when the
    // welcome's OWN single-use auto-login is OFF; the hardened link is kept when ON.
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Welcome_with_login_routes_hub_cta_through_the_magic_link_when_autologin_off()
    {
        await using var sp = BuildServices();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();

        var ev = NewEvent();
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        // §1001 — open the speaker-notice quiet period; these tests are about the mail itself.
        db.SessionSourceSettings.Add(new SessionSourceSetting
        {
            EventId = ev.Id, Source = SessionSourceKinds.Default,
            SpeakerScheduleNoticeFrom = new DateOnly(2020, 1, 1),
        });
        await db.SaveChangesAsync();
        var p = new Participant
        {
            EventId = ev.Id, Email = "person@example.com", FullName = "Sample Person",
            Role = ParticipantRole.Speaker, IsActive = true,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        var participant = await db.Participants.Include(x => x.Event).SingleAsync(x => x.Id == p.Id);

        // Auto-login DISABLED (the operator default).
        var svc = new WelcomeWithLoginEmailService(
            db, RealTemplatesWithMagic(sp), new CapturingEmailSender(), autoLogin: null!,
            new StubEnv(), new FixedClock(), context: null,
            options: new WelcomeEmailOptions { AutoLoginEnabled = false });

        // loginUrl = the plain per-environment hub URL (auto-login off).
        var rendered = svc.RenderForUrl(participant, Origin);

        // The {{hubUrl}} CTA is the participant's §169 reusable magic-link…
        var token = ExtractGoToken(rendered.HtmlBody);
        var grant = await db.MagicLinkGrants.SingleAsync(g => g.ParticipantId == p.Id);
        Assert.Equal(EmailMagicLinkService.HashToken(token), grant.TokenIdHash);
        Assert.True(grant.MultiUse);
    }

    [Fact]
    public async Task Welcome_with_login_keeps_its_hardened_single_use_link_when_autologin_on()
    {
        await using var sp = BuildServices();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();

        var ev = NewEvent();
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        // §1001 — open the speaker-notice quiet period; these tests are about the mail itself.
        db.SessionSourceSettings.Add(new SessionSourceSetting
        {
            EventId = ev.Id, Source = SessionSourceKinds.Default,
            SpeakerScheduleNoticeFrom = new DateOnly(2020, 1, 1),
        });
        await db.SaveChangesAsync();
        var p = new Participant
        {
            EventId = ev.Id, Email = "person@example.com", FullName = "Sample Person",
            Role = ParticipantRole.Speaker, IsActive = true,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        var participant = await db.Participants.Include(x => x.Event).SingleAsync(x => x.Id == p.Id);

        var svc = new WelcomeWithLoginEmailService(
            db, RealTemplatesWithMagic(sp), new CapturingEmailSender(), autoLogin: null!,
            new StubEnv(), new FixedClock(), context: null,
            options: new WelcomeEmailOptions { AutoLoginEnabled = true });

        // Auto-login ON: the caller supplies the hardened SINGLE-USE link.
        var hardened = "https://dev.example/Login/Magic?token=HARDENED";
        var rendered = svc.RenderForUrl(participant, hardened);

        // The hub CTA stays the hardened single-use link — never downgraded to /go.
        Assert.Contains(hardened, rendered.HtmlBody);
        Assert.DoesNotContain($"{Origin}/go/", rendered.HtmlBody);
    }

    // ----------------------------------------------------------------------
    // The intentional EXCEPTION: a body for MANY recipients / a non-participant
    // audience carries no single participant → it keeps the PLAIN hub URL.
    // (Broadcast composes one body per free-text message; Master-Class emails address
    // Zoho-synced Attendees, which are NOT Participants and have no §169 grant.)
    // ----------------------------------------------------------------------

    [Fact]
    public void Mass_send_without_a_participant_keeps_the_plain_hub_url()
    {
        using var sp = BuildServices();
        var templates = RealTemplatesWithMagic(sp);

        var tokens = templates.NewTokenSet();   // no participant id → mass / non-participant

        Assert.Equal(Origin, tokens["hubUrl"]);          // plain origin, no /go token
        Assert.False(tokens.ContainsKey("magicHubUrl")); // and no magic link minted
    }

    // ----------------------------------------------------------------------
    // SyncDeltaQueueService — the "session schedule changed" email to a single
    // speaker now carries THAT speaker's magic-link (the session-time-location-changed
    // template has a {{hubUrl}} CTA, so the /go link renders in the body).
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Session_schedule_change_email_carries_the_speakers_magic_link()
    {
        await using var sp = BuildServices();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();

        var ev = NewEvent();
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        // §1001 — open the speaker-notice quiet period; these tests are about the mail itself.
        db.SessionSourceSettings.Add(new SessionSourceSetting
        {
            EventId = ev.Id, Source = SessionSourceKinds.Default,
            SpeakerScheduleNoticeFrom = new DateOnly(2020, 1, 1),
        });
        await db.SaveChangesAsync();

        var speaker = new Participant
        {
            EventId = ev.Id, Email = "speaker@example.com", FullName = "Sample Speaker",
            Role = ParticipantRole.Speaker, IsActive = true,
        };
        db.Participants.Add(speaker);
        await db.SaveChangesAsync();

        var start = Now;
        var session = new Session
        {
            EventId = ev.Id, SessionizeId = "sz-1", Title = "Keynote", BackstageSessionId = "bs-1",
            BackstageStartsAt = start, BackstageEndsAt = start.AddHours(1), BackstageRoom = "Room A",
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        db.SessionSpeakers.Add(new SessionSpeaker { SessionId = session.Id, ParticipantId = speaker.Id });
        await db.SaveChangesAsync();

        var sender = new CapturingEmailSender();
        var svc = new SyncDeltaQueueService(
            db, clock: new FixedClock(), audit: null, alerts: null, sender: sender,
            context: null, templates: RealTemplatesWithMagic(sp));

        var delta = await svc.EnqueueSessionUpdateAsync(
            ev.Id, session.Id, "Keynote", SessionSyncDirection.ZohoToCeh,
            SyncDeltaQueueService.BuildSessionChanges(
                start, start.AddHours(1), "Room A",
                start.AddHours(2), start.AddHours(3), "Room A"));

        var result = await svc.ApproveAsync(delta.Id, "olivia@example.com");
        Assert.True(result.Emailed);

        var m = Assert.Single(sender.Messages);
        Assert.Equal("speaker@example.com", m.To);
        var token = ExtractGoToken(m.Html);                          // the hub CTA is a /go link…
        var grant = await db.MagicLinkGrants.SingleAsync(g => g.ParticipantId == speaker.Id);
        Assert.Equal(EmailMagicLinkService.HashToken(token), grant.TokenIdHash); // …for THIS speaker.
    }

    // ----------------------------------------------------------------------
    // §997 — the two defects the operator found in that same email.
    // ----------------------------------------------------------------------

    /// <summary>
    /// Drives the real approve→apply→email path and returns the rendered body, so both §997
    /// assertions are made against the mail a speaker would actually receive.
    /// </summary>
    private static async Task<string> RenderScheduleChangeAsync(
        ServiceProvider sp, CommunityHubDbContext db,
        DateTimeOffset oldStart, DateTimeOffset oldEnd, string oldRoom,
        DateTimeOffset newStart, DateTimeOffset newEnd, string newRoom)
    {
        var ev = NewEvent();
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        // §1001 — open the speaker-notice quiet period; these tests are about the mail itself.
        db.SessionSourceSettings.Add(new SessionSourceSetting
        {
            EventId = ev.Id, Source = SessionSourceKinds.Default,
            SpeakerScheduleNoticeFrom = new DateOnly(2020, 1, 1),
        });
        await db.SaveChangesAsync();

        var speaker = new Participant
        {
            EventId = ev.Id, Email = "speaker@example.com", FullName = "Sample Speaker",
            Role = ParticipantRole.Speaker, IsActive = true,
        };
        db.Participants.Add(speaker);
        await db.SaveChangesAsync();

        var session = new Session
        {
            EventId = ev.Id, SessionizeId = "sz-1", Title = "ELDK27 Welcome",
            BackstageSessionId = "bs-1",
            BackstageStartsAt = oldStart, BackstageEndsAt = oldEnd, BackstageRoom = oldRoom,
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        db.SessionSpeakers.Add(new SessionSpeaker { SessionId = session.Id, ParticipantId = speaker.Id });
        await db.SaveChangesAsync();

        var sender = new CapturingEmailSender();
        var svc = new SyncDeltaQueueService(
            db, clock: new FixedClock(), audit: null, alerts: null, sender: sender,
            context: null, templates: RealTemplatesWithMagic(sp));

        var delta = await svc.EnqueueSessionUpdateAsync(
            ev.Id, session.Id, session.Title, SessionSyncDirection.ZohoToCeh,
            SyncDeltaQueueService.BuildSessionChanges(
                oldStart, oldEnd, oldRoom, newStart, newEnd, newRoom));
        await svc.ApproveAsync(delta.Id, "olivia@example.com");

        return Assert.Single(sender.Messages).Html;
    }

    /// <summary>
    /// 🔴 §997 — THE TIME MUST BE DANISH, NOT UTC.
    /// </summary>
    /// <remarks>
    /// Operator 2026-08-09: *"the time here is wrong as it is in wrong timezone. inside zoho the
    /// session is 8:30-8:50 Danish time - but the mail doesn't reflect that"*. It printed
    /// <c>07:30–07:50</c> — the raw UTC instant, one hour behind CET.
    /// <para>⚠️ February is CET (+01:00), deliberately: a summer date would pass on a +02:00 bug
    /// too, and this exact session is a February one.</para>
    /// </remarks>
    [Fact]
    public async Task Session_schedule_change_email_shows_the_time_in_DANISH_time_not_UTC()
    {
        await using var sp = BuildServices();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();

        // His case: Zoho holds 08:30–08:50 Danish on 10 Feb 2027 ⇒ 07:30–07:50 UTC (CET, +1).
        var html = await RenderScheduleChangeAsync(sp, db,
            oldStart: new DateTimeOffset(2027, 2, 10, 8, 0, 0, TimeSpan.Zero),
            oldEnd: new DateTimeOffset(2027, 2, 10, 9, 0, 0, TimeSpan.Zero),
            oldRoom: "Room-A1",
            newStart: new DateTimeOffset(2027, 2, 10, 7, 30, 0, TimeSpan.Zero),
            newEnd: new DateTimeOffset(2027, 2, 10, 7, 50, 0, TimeSpan.Zero),
            newRoom: "Room-A1");

        Assert.Contains("08:30–08:50", html);        // Danish wall time, as Zoho shows it
        Assert.DoesNotContain("07:30–07:50", html);  // 🔴 the UTC instant he was sent
        // The old value converts too — a half-converted table is worse than an unconverted one.
        Assert.Contains("09:00–10:00", html);
        // Said once, so the reader never has to guess which zone they are reading.
        Assert.Contains("Danish time", html);
    }

    /// <summary>
    /// 🔴 §997 — A ROW APPEARS ONLY WHEN THAT FIELD ACTUALLY CHANGED.
    /// </summary>
    /// <remarks>
    /// Operator, on the same mail: *"i also dont understand the where change and see no
    /// difference?"* — there was none. The template rendered both rows unconditionally, so an
    /// unchanged room was printed struck through and then repeated verbatim. That is the §594 trust
    /// failure aimed at a SPEAKER rather than the operator: an alert asserting a change that did not
    /// happen teaches the reader to stop believing the alert.
    /// </remarks>
    [Fact]
    public async Task A_time_only_change_does_not_print_a_Where_row()
    {
        await using var sp = BuildServices();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();

        var html = await RenderScheduleChangeAsync(sp, db,
            oldStart: new DateTimeOffset(2027, 2, 10, 8, 0, 0, TimeSpan.Zero),
            oldEnd: new DateTimeOffset(2027, 2, 10, 9, 0, 0, TimeSpan.Zero),
            oldRoom: "Room-A1 Keynote-Floor 0-Max 1500",
            newStart: new DateTimeOffset(2027, 2, 10, 7, 30, 0, TimeSpan.Zero),
            newEnd: new DateTimeOffset(2027, 2, 10, 7, 50, 0, TimeSpan.Zero),
            newRoom: "Room-A1 Keynote-Floor 0-Max 1500");   // unchanged

        Assert.Contains("When", html);
        Assert.DoesNotContain("Where", html);
        // And the room is not printed at all — not struck through, not repeated.
        Assert.DoesNotContain("Room-A1 Keynote-Floor 0-Max 1500", html);
    }

    /// <summary>The mirror case: a room-only move prints Where and NOT When.</summary>
    [Fact]
    public async Task A_room_only_change_does_not_print_a_When_row()
    {
        await using var sp = BuildServices();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();

        var start = new DateTimeOffset(2027, 2, 10, 8, 0, 0, TimeSpan.Zero);
        var html = await RenderScheduleChangeAsync(sp, db,
            oldStart: start, oldEnd: start.AddHours(1), oldRoom: "Room A",
            newStart: start, newEnd: start.AddHours(1), newRoom: "Room B");

        Assert.Contains("Where", html);
        Assert.Contains("Room B", html);
        Assert.DoesNotContain("When", html);
    }

    /// <summary>
    /// 🔒 The rows are a RAW-HTML token, so the single-pass renderer must not leave a nested
    /// <c>{{brandColor}}</c> as literal braces in a speaker's inbox (§726).
    /// </summary>
    [Fact]
    public async Task The_change_table_leaves_no_unsubstituted_tokens()
    {
        await using var sp = BuildServices();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();

        var start = new DateTimeOffset(2027, 2, 10, 8, 0, 0, TimeSpan.Zero);
        var html = await RenderScheduleChangeAsync(sp, db,
            oldStart: start, oldEnd: start.AddHours(1), oldRoom: "Room A",
            newStart: start.AddHours(2), newEnd: start.AddHours(3), newRoom: "Room B");

        Assert.DoesNotContain("{{", html);
        Assert.DoesNotContain("}}", html);
    }


    // ----------------------------------------------------------------------
    // SessionEvaluationMailService — the results mail to a speaker binds that
    // speaker's standing magic-link grant (the session-evaluation-results template
    // has no hub CTA today, so we assert the grant the seam minted, not a body link).
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Session_evaluation_results_bind_the_speakers_magic_link_grant()
    {
        await using var sp = BuildServices();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();

        var ev = NewEvent();
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        // §1001 — open the speaker-notice quiet period; these tests are about the mail itself.
        db.SessionSourceSettings.Add(new SessionSourceSetting
        {
            EventId = ev.Id, Source = SessionSourceKinds.Default,
            SpeakerScheduleNoticeFrom = new DateOnly(2020, 1, 1),
        });
        await db.SaveChangesAsync();
        var speaker = new Participant
        {
            EventId = ev.Id, Email = "speaker@example.com", FullName = "Sample Speaker",
            Role = ParticipantRole.Speaker, IsActive = true,
        };
        db.Participants.Add(speaker);
        await db.SaveChangesAsync();
        var session = new Session { EventId = ev.Id, SessionizeId = "sz-2", Title = "Workshop" };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        db.SessionSpeakers.Add(new SessionSpeaker { SessionId = session.Id, ParticipantId = speaker.Id });
        await db.SaveChangesAsync();

        var sender = new CapturingEmailSender();
        var svc = new SessionEvaluationMailService(
            db, sender, new FixedClock(), context: null, templates: RealTemplatesWithMagic(sp));

        var res = await svc.EmailResultsToSpeakersAsync(session.Id, "Mostly smiles!");
        Assert.True(res.Sent);

        var m = Assert.Single(sender.Messages);
        Assert.Equal("speaker@example.com", m.To);
        // The §169 seam minted the speaker's standing grant when their id was passed.
        var grant = await db.MagicLinkGrants.SingleAsync(g => g.ParticipantId == speaker.Id);
        Assert.Equal(EmailMagicLinkService.PurposeName, grant.Purpose);
        Assert.True(grant.MultiUse);
    }

    // ----------------------------------------------------------------------
    // SponsorLeadsJob — the leads digest is rendered PER coordinator so each gets
    // their OWN /go magic-link; an external (non-participant) recipient stays plain.
    // ----------------------------------------------------------------------

    private static async Task<SponsorLeadsJob> NewSponsorLeadsJobAsync(
        ServiceProvider sp, CommunityHubDbContext db, Event ev, CapturingEmailSender sender)
    {
        await new FeatureSettingsService(db, new FixedClock())
            .SetEnabledAsync(ev.Id, "sponsor-leads", true, "org@example.com");
        return new SponsorLeadsJob(
            db, sync: null!, new ZohoOptions(), RealTemplatesWithMagic(sp), sender,
            new FixedClock(), new FeatureGateService(db),
            new AuditTrailService(db, new FixedClock()),
            NullLogger<SponsorLeadsJob>.Instance);
    }

    private static SponsorLead FreshLead(int eventId) => new()
    {
        EventId = eventId, SponsorCompanyId = "ACME", FullName = "Sample Lead",
        Email = "lead@example.com", CapturedAt = Now, Status = SponsorLeadStatus.Open,
    };

    [Fact]
    public async Task Sponsor_leads_digest_gives_each_coordinator_their_OWN_magic_link()
    {
        await using var sp = BuildServices();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();

        var ev = NewEvent();
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        // §1001 — open the speaker-notice quiet period; these tests are about the mail itself.
        db.SessionSourceSettings.Add(new SessionSourceSetting
        {
            EventId = ev.Id, Source = SessionSourceKinds.Default,
            SpeakerScheduleNoticeFrom = new DateOnly(2020, 1, 1),
        });
        await db.SaveChangesAsync();

        Participant Coord(string email) => new()
        {
            EventId = ev.Id, Email = email, FullName = "Sample Person",
            Role = ParticipantRole.Sponsor, SponsorCompanyId = "ACME",
            IsEventCoordinator = true, IsActive = true,
        };
        var c1 = Coord("coord1@example.com");
        var c2 = Coord("coord2@example.com");
        db.Participants.AddRange(c1, c2);
        // Recipients empty ⇒ fallback to the company's active sponsor contacts (both).
        db.SponsorLeadNotificationPrefs.Add(new SponsorLeadNotificationPref
        {
            EventId = ev.Id, SponsorCompanyId = "ACME", Enabled = true,
            Cadence = SponsorLeadNotifyCadence.RealTime, Recipients = "",
        });
        db.SponsorLeads.Add(FreshLead(ev.Id));
        await db.SaveChangesAsync();

        var sender = new CapturingEmailSender();
        var job = await NewSponsorLeadsJobAsync(sp, db, ev, sender);
        await job.Run(new TimerInfo(), default);

        Assert.Equal(2, sender.Messages.Count);
        var t1 = ExtractGoToken(sender.Messages.Single(m => m.To == "coord1@example.com").Html);
        var t2 = ExtractGoToken(sender.Messages.Single(m => m.To == "coord2@example.com").Html);
        Assert.NotEqual(t1, t2);                                  // never sign in AS the other
        var g1 = await db.MagicLinkGrants.SingleAsync(g => g.ParticipantId == c1.Id);
        var g2 = await db.MagicLinkGrants.SingleAsync(g => g.ParticipantId == c2.Id);
        Assert.Equal(EmailMagicLinkService.HashToken(t1), g1.TokenIdHash);
        Assert.Equal(EmailMagicLinkService.HashToken(t2), g2.TokenIdHash);
    }

    [Fact]
    public async Task Sponsor_leads_digest_to_an_external_recipient_stays_plain()
    {
        await using var sp = BuildServices();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();

        var ev = NewEvent();
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        // §1001 — open the speaker-notice quiet period; these tests are about the mail itself.
        db.SessionSourceSettings.Add(new SessionSourceSetting
        {
            EventId = ev.Id, Source = SessionSourceKinds.Default,
            SpeakerScheduleNoticeFrom = new DateOnly(2020, 1, 1),
        });
        await db.SaveChangesAsync();

        // An explicit recipient that is NOT a Participant in this edition.
        db.SponsorLeadNotificationPrefs.Add(new SponsorLeadNotificationPref
        {
            EventId = ev.Id, SponsorCompanyId = "ACME", Enabled = true,
            Cadence = SponsorLeadNotifyCadence.RealTime, Recipients = "external@example.com",
        });
        db.SponsorLeads.Add(FreshLead(ev.Id));
        await db.SaveChangesAsync();

        var sender = new CapturingEmailSender();
        var job = await NewSponsorLeadsJobAsync(sp, db, ev, sender);
        await job.Run(new TimerInfo(), default);

        var m = Assert.Single(sender.Messages);
        Assert.Equal("external@example.com", m.To);
        Assert.DoesNotContain($"{Origin}/go/", m.Html);          // plain hub URL, no magic link
        Assert.Empty(db.MagicLinkGrants);                        // and none minted
    }
}
