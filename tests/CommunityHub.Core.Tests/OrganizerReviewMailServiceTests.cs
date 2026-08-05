using CommunityHub.Core.Settings;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §879 — the pending-approvals mail SPLIT IN TWO: speakers held from the Zoho flow (every 10
/// minutes, once per change) and volunteers awaiting review (weekly).
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-05: <i>"speaker held in the queue. i need that to run every 10 min and be
/// notified after 10 min. volunteers awaiting review should go out weekly. we need to split these as
/// they are very different"</i>.</para>
///
/// <para>The §759 invariant these tests inherited is kept intact: the count in the mail must equal
/// the number of rows the page lists, over a population deliberately full of rows belonging to
/// neither queue.</para>
/// </remarks>
public sealed class OrganizerReviewMailServiceTests
{
    private static (OrganizerReviewMailService Svc, CapturingEmailSender Sender) NewService(
        CommunityHubDbContext db, CommunityHub.Core.Organizer.SpeakerApprovalService? approval = null)
    {
        var sender = new CapturingEmailSender();
        var alerts = new EngineAlertSender(
            sender, new EmailContextAccessor(), TimeProvider.System,
            NullLogger<EngineAlertSender>.Instance);
        var svc = new OrganizerReviewMailService(
            db, alerts,
            Options.Create(new EmailTemplateOptions { HubUrl = "https://hub.example.test" }),
            approval);
        return (svc, sender);
    }

    private static async Task<int> NewEventAsync(CommunityHubDbContext db)
    {
        var evt = new Event
        {
            Code = "ELDK27", CommunityName = "C", DisplayName = "ELDK 2027",
            StartDate = new DateOnly(2027, 9, 1), EndDate = new DateOnly(2027, 9, 2), IsActive = true,
        };
        db.Events.Add(evt);
        await db.SaveChangesAsync();
        return evt.Id;
    }

    private static void AddQueued(
        CommunityHubDbContext db, int eventId, string name,
        ParticipantRole role, ParticipantQueueSource source,
        ParticipantLifecycleState state = ParticipantLifecycleState.Inactive)
        => db.Participants.Add(new Participant
        {
            EventId = eventId, FullName = name, Email = $"{name.Replace(' ', '.')}@example.test",
            Role = role, QueueSource = source, LifecycleState = state, IsActive = false,
            IsTestUser = true,
        });

    // -----------------------------------------------------------------------
    // The split itself
    // -----------------------------------------------------------------------

    /// <summary>
    /// 🔒 §879 — THE TWO MAILS ARE TWO JOBS, WITH THE TWO CADENCES HE NAMED.
    /// </summary>
    /// <remarks>
    /// This is the assertion that fails if anyone re-merges them, and the cadences are asserted with
    /// the interval-driven flag because "every 10 minutes" is only true if the operator's dial is
    /// what decides it — a clock-anchored cron would satisfy the number and silently fail the
    /// control (§869.3).
    /// </remarks>
    [Fact]
    public void The_two_mails_are_two_jobs_at_ten_minutes_and_weekly()
    {
        var speakers = CommunityHub.Core.Settings.JobCatalog.Find("SpeakersHeldJob");
        var volunteers = CommunityHub.Core.Settings.JobCatalog.Find("VolunteersAwaitingReviewJob");

        Assert.NotNull(speakers);
        Assert.NotNull(volunteers);

        Assert.Equal(10, speakers!.DefaultIntervalMinutes);        // "notified after 10 min"
        Assert.Equal(10080, volunteers!.DefaultIntervalMinutes);   // "should go out weekly"
        Assert.True(speakers.IsIntervalDriven);
        Assert.True(volunteers.IsIntervalDriven);

        // 🔒 §595/§642 — the FeatureKey is a live DB row and must not move when the wording does,
        // or the operator's switch silently detaches from the mails it governs.
        Assert.Equal("digest-emails", speakers.FeatureKey);
        Assert.Equal("digest-emails", volunteers.FeatureKey);

        // ⚰️ And the merged job is gone, not merely unused.
        Assert.Null(CommunityHub.Core.Settings.JobCatalog.Find("PendingApprovalsDigestJob"));
    }

    /// <summary>
    /// 🔒 §879.2 — "DIGEST" IS BANNED VOCABULARY (<i>"hate that word digest, dont use it and dont
    /// understand it"</i>). It must not survive in anything the operator reads on the Jobs page.
    /// </summary>
    [Fact]
    public void No_job_the_operator_can_see_is_called_a_digest()
    {
        var offenders = CommunityHub.Core.Settings.JobCatalog.All
            .Where(j => j.Title.Contains("digest", StringComparison.OrdinalIgnoreCase)
                        || j.What.Contains("digest", StringComparison.OrdinalIgnoreCase))
            .Select(j => j.FunctionName)
            .ToList();

        Assert.True(offenders.Count == 0,
            "These jobs still say 'digest' where the operator reads it: "
            + string.Join(", ", offenders));
    }

    // -----------------------------------------------------------------------
    // Speakers held — the content hash is what makes 10 minutes survivable
    // -----------------------------------------------------------------------

    /// <summary>
    /// 🔑 §879 — the fingerprint is WHO is held and WHY, so an unchanged pass is silent and a real
    /// change speaks. Without this the 10-minute cadence is ~144 mails/day (§878.6).
    /// </summary>
    [Fact]
    public void The_held_speaker_hash_is_stable_for_an_unchanged_set_and_moves_when_a_blocker_clears()
    {
        static CommunityHub.Core.Organizer.SpeakerApprovalService.PendingResult Result(
            params CommunityHub.Core.Organizer.SpeakerApprovalService.PendingSpeaker[] speakers)
            => new(Ring.Broad, true, speakers);

        static CommunityHub.Core.Organizer.SpeakerApprovalService.PendingSpeaker Held(
            int id, params string[] blockers)
            => new(id, $"s{id}@example.test", $"Speaker {id}", Ring.Broad, null, false,
                ParticipantLifecycleState.Preselected, blockers);

        var a = Result(Held(1, "no speaker category"), Held(2, "no speaker category"));
        var sameSetOtherOrder = Result(Held(2, "no speaker category"), Held(1, "no speaker category"));
        var oneBlockerCleared = Result(Held(1, "no speaker category"),
                                       Held(2, "no speaker category", "participant inactive"));
        var oneLeft = Result(Held(1, "no speaker category"));

        // Unchanged set ⇒ same hash ⇒ silence. Query order must NOT be part of the identity, or
        // "mail on change" quietly becomes "mail every pass" in the least visible way possible.
        Assert.Equal(OrganizerReviewMailService.HashOf(a),
                     OrganizerReviewMailService.HashOf(sameSetOtherOrder));

        // Someone ACTED (or something got worse) ⇒ the ask is different ⇒ worth one mail.
        Assert.NotEqual(OrganizerReviewMailService.HashOf(a),
                        OrganizerReviewMailService.HashOf(oneBlockerCleared));
        // Someone left the queue ⇒ also a change.
        Assert.NotEqual(OrganizerReviewMailService.HashOf(a),
                        OrganizerReviewMailService.HashOf(oneLeft));
    }

    /// <summary>
    /// §877 — the 10-minute mail carries the SAME body as the import mail it replaced, one-click
    /// approve-all buttons included. Two builders for one mail would have drifted immediately.
    /// </summary>
    [Fact]
    public async Task The_held_speaker_mail_carries_the_one_click_approve_buttons()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await NewEventAsync(db);

        var p = new Participant
        {
            EventId = eventId, FullName = "Held Speaker", Email = "held@example.test",
            Role = ParticipantRole.Speaker, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active, IsTestUser = true,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        db.SpeakerProfiles.Add(new SpeakerProfile
        {
            EventId = eventId, ParticipantId = p.Id, Category = null,   // ← the only blocker
        });
        await db.SaveChangesAsync();

        var approval = new CommunityHub.Core.Organizer.SpeakerApprovalService(
            db, new CommunityHub.Core.Settings.FeatureGateService(db), TimeProvider.System);
        var (svc, sender) = NewService(db, approval);

        var held = await svc.HeldSpeakersAsync(eventId);
        Assert.Equal(1, held.Count);
        Assert.NotNull(held.Hash);

        await svc.SendHeldSpeakersAsync(held);

        var mail = Assert.Single(sender.Messages);
        Assert.Equal(OrganizerReviewMailService.Recipient, mail.To);      // info@expertslive.dk
        Assert.Contains("held from the Zoho flow", mail.Subject);
        Assert.Contains("approveAll=community", mail.Html);
        // 🔒 And it must NOT carry the volunteer half — that is the whole point of the split.
        Assert.DoesNotContain("PreselectionQueue", mail.Html);
    }

    [Fact]
    public async Task Nothing_is_held_means_no_mail_and_no_fingerprint()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await NewEventAsync(db);
        var approval = new CommunityHub.Core.Organizer.SpeakerApprovalService(
            db, new CommunityHub.Core.Settings.FeatureGateService(db), TimeProvider.System);
        var (svc, sender) = NewService(db, approval);

        var held = await svc.HeldSpeakersAsync(eventId);

        Assert.False(held.Any);
        Assert.Null(held.Hash);       // ⇒ the job clears its stored hash, so a future hold is news
        await svc.SendHeldSpeakersAsync(held);
        Assert.Empty(sender.Messages);
    }

    // -----------------------------------------------------------------------
    // Volunteers awaiting review — weekly, and counted from the page's own query
    // -----------------------------------------------------------------------

    /// <summary>
    /// 🔒 §759 — THE MAIL AND THE PAGE MUST NEVER DISAGREE.
    /// </summary>
    /// <remarks>
    /// Operator 2026-08-01, on a PROD mail reading "10 awaiting review": <i>"this seems wrong as the
    /// preselection queue is empty"</i>. The mail had its own hand-copied WHERE clause commented
    /// "mirror GetQueueAsync's scope" — a mirror, not a shared definition, and it drifted. Both go
    /// through <c>PreselectionQueueService.QueueRows</c>; this compares the two numbers over a
    /// population full of rows belonging to neither queue.
    /// </remarks>
    [Fact]
    public async Task The_volunteer_count_equals_the_number_of_rows_the_page_lists()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await NewEventAsync(db);

        AddQueued(db, eventId, "Vol One", ParticipantRole.Volunteer, ParticipantQueueSource.VolunteerInterestForm);
        AddQueued(db, eventId, "Vol Two", ParticipantRole.Volunteer, ParticipantQueueSource.MediaTeamSignup,
            ParticipantLifecycleState.Preselected);
        AddQueued(db, eventId, "A Speaker", ParticipantRole.Speaker, ParticipantQueueSource.SessionizeSync);
        AddQueued(db, eventId, "An Attendee", ParticipantRole.Attendee, ParticipantQueueSource.Manual);
        AddQueued(db, eventId, "A Sponsor", ParticipantRole.Sponsor, ParticipantQueueSource.Manual);
        await db.SaveChangesAsync();

        var page = await new CommunityHub.Core.Organizer.PreselectionQueueService(db)
            .GetQueueAsync(eventId);
        var (svc, _) = NewService(db);
        var count = await svc.VolunteersAwaitingAsync(eventId);

        Assert.Equal(page.Count, count);
        Assert.Equal(2, count);                                   // the two volunteers, nothing else
        Assert.All(page, p => Assert.Equal(ParticipantRole.Volunteer, p.Role));
    }

    [Fact]
    public async Task The_volunteer_mail_says_it_is_the_weekly_volunteer_list_only()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await NewEventAsync(db);
        AddQueued(db, eventId, "Vol One", ParticipantRole.Volunteer, ParticipantQueueSource.VolunteerInterestForm);
        await db.SaveChangesAsync();

        var (svc, sender) = NewService(db);
        var count = await svc.VolunteersAwaitingAsync(eventId);
        Assert.True(await svc.SendVolunteersAwaitingAsync(count));

        var mail = Assert.Single(sender.Messages);
        Assert.Equal(OrganizerReviewMailService.Recipient, mail.To);
        Assert.Contains("Volunteers awaiting review", mail.Subject);
        Assert.Contains("https://hub.example.test/Organizer/PreselectionQueue", mail.Html);
        // 🔑 He must be able to tell at a glance that a held speaker is not hiding in here.
        Assert.Contains("separate mail", mail.Html);
        Assert.DoesNotContain("approveAll", mail.Html);
    }

    [Fact]
    public async Task An_empty_volunteer_queue_sends_nothing()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await NewEventAsync(db);
        // A sponsor in the queue is not a volunteer awaiting review.
        AddQueued(db, eventId, "Sponsor Contact", ParticipantRole.Sponsor, ParticipantQueueSource.Manual);
        await db.SaveChangesAsync();

        var (svc, sender) = NewService(db);
        var count = await svc.VolunteersAwaitingAsync(eventId);

        Assert.Equal(0, count);
        Assert.False(await svc.SendVolunteersAwaitingAsync(count));
        Assert.Empty(sender.Messages);
    }
}
