using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1307 — panel speakers, a volunteer who joins a panel, and "two people count as one".
/// </summary>
/// <remarks>
/// Operator 2026-09-26: <i>"sessions categorized as panel, don't have all tasks for the linked speakers like
/// preview/final presentation tasks"</i> · a confirmed volunteer on a panel should get <i>"speaker emails, welcome mails
/// as speaker, etc. But i don't her counted double"</i> · linked people must <i>"count as one for party, lunch, etc."</i>
/// </remarks>
public sealed class PanelAndSpeakerHatTests : IDisposable
{
    private const int EventId = 1;
    private const string Preview = "Upload preview presentation";
    private const string Final = "Upload final presentation";
    private const string Promote = "Help to promote your session(s)";

    private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"speaker-deadlines-{Guid.NewGuid():N}.json");

    public PanelAndSpeakerHatTests() =>
        File.WriteAllText(_configPath, """{"deadlines":[{"title":"Hotel","description":"x","dueDate":"2026-10-01"}]}""");

    public void Dispose() => File.Delete(_configPath);

    [Fact]
    public async Task A_panel_only_speaker_gets_no_presentation_upload_tasks()
    {
        await using var db = NewDb();
        var pid = await PersonAsync(db, ParticipantRole.Speaker, profile: true);
        await SessionAsync(db, pid, SessionType.PanelDiscussion);

        await Seeder(db).SeedAsync(EventId);

        var titles = await TitlesAsync(db, pid);
        Assert.DoesNotContain(Preview, titles);
        Assert.DoesNotContain(Final, titles);
        Assert.Contains(Promote, titles);   // a panel still needs an audience
    }

    [Fact]
    public async Task A_speaker_with_a_talk_AND_a_panel_still_uploads_the_talks_deck()
    {
        await using var db = NewDb();
        var pid = await PersonAsync(db, ParticipantRole.Speaker, profile: true);
        await SessionAsync(db, pid, SessionType.PanelDiscussion);
        await SessionAsync(db, pid, SessionType.TechnicalSession);

        await Seeder(db).SeedAsync(EventId);

        var titles = await TitlesAsync(db, pid);
        Assert.Contains(Preview, titles);
        Assert.Contains(Final, titles);
    }

    [Fact]
    public async Task Upload_tasks_seeded_before_a_session_became_a_panel_are_retired_not_deleted()
    {
        await using var db = NewDb();
        var pid = await PersonAsync(db, ParticipantRole.Speaker, profile: true);
        var session = await SessionAsync(db, pid, SessionType.TechnicalSession);
        await Seeder(db).SeedAsync(EventId);

        session.Type = SessionType.PanelDiscussion;
        await db.SaveChangesAsync();
        await Seeder(db).SeedAsync(EventId);

        var uploads = await db.Tasks.Where(t => t.AssignedParticipantId == pid && (t.Title == Preview || t.Title == Final)).ToListAsync();
        Assert.Equal(2, uploads.Count);                                         // kept for the record (§1082)
        Assert.All(uploads, t => Assert.Equal(TaskClosedReason.RetiredFromCatalog, t.ClosedReason));
    }

    [Fact]
    public async Task A_session_unmarked_as_panel_gives_the_upload_tasks_back_but_never_touches_real_completions()
    {
        await using var db = NewDb();
        var pid = await PersonAsync(db, ParticipantRole.Speaker, profile: true);
        var session = await SessionAsync(db, pid, SessionType.TechnicalSession);
        await Seeder(db).SeedAsync(EventId);

        // The speaker really uploaded the preview: a PERSON's completion (no ClosedReason).
        var preview = await db.Tasks.SingleAsync(t => t.AssignedParticipantId == pid && t.Title == Preview);
        preview.State = TaskState.Done;
        preview.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        session.Type = SessionType.PanelDiscussion;          // marked a panel by mistake …
        await db.SaveChangesAsync();
        await Seeder(db).SeedAsync(EventId);
        session.Type = SessionType.TechnicalSession;         // … and corrected
        await db.SaveChangesAsync();
        await Seeder(db).SeedAsync(EventId);

        var final = await db.Tasks.SingleAsync(t => t.AssignedParticipantId == pid && t.Title == Final);
        Assert.Equal(TaskState.Open, final.State);           // the system's closure is undone
        Assert.Null(final.ClosedReason);
        preview = await db.Tasks.SingleAsync(t => t.AssignedParticipantId == pid && t.Title == Preview);
        Assert.Equal(TaskState.Done, preview.State);         // the person's upload is still done
        Assert.Null(preview.ClosedReason);
    }

    [Fact]
    public async Task A_volunteer_who_speaks_gets_the_session_tasks_but_no_second_set_of_logistics()
    {
        await using var db = NewDb();
        var pid = await PersonAsync(db, ParticipantRole.Volunteer, profile: true);
        await SessionAsync(db, pid, SessionType.TechnicalSession);

        // Twice: the ex-speaker sweep must not retire her speaker tasks because her ROLE is Volunteer.
        await Seeder(db).SeedAsync(EventId);
        await Seeder(db).SeedAsync(EventId);

        var open = await db.Tasks.Where(t => t.AssignedParticipantId == pid && t.State == TaskState.Open)
            .Select(t => t.Title).ToListAsync();
        Assert.Contains(Promote, open);
        Assert.Contains(Preview, open);
        Assert.Contains(Final, open);
        Assert.DoesNotContain("Hotel", open);   // her volunteer role already covers hotel, dinner, swag, lunch
        Assert.DoesNotContain("Appreciation Dinner", open);
    }

    [Fact]
    public async Task A_volunteer_on_a_panel_only_gets_promote()
    {
        await using var db = NewDb();
        var pid = await PersonAsync(db, ParticipantRole.Volunteer, profile: true);
        await SessionAsync(db, pid, SessionType.PanelDiscussion);

        await Seeder(db).SeedAsync(EventId);

        Assert.Equal(new[] { Promote }, (await TitlesAsync(db, pid)).ToArray());
    }

    [Fact]
    public async Task A_volunteer_without_a_speaker_profile_gets_no_speaker_tasks()
    {
        await using var db = NewDb();
        var pid = await PersonAsync(db, ParticipantRole.Volunteer, profile: false);

        await Seeder(db).SeedAsync(EventId);

        Assert.Empty(await TitlesAsync(db, pid));
    }

    [Fact]
    public async Task A_volunteer_who_speaks_gets_the_speaker_welcome_once_under_its_own_key()
    {
        await using var db = NewDb();
        var pid = await PersonAsync(db, ParticipantRole.Volunteer, profile: true);
        // Her volunteer welcome is already on record.
        db.SentReminders.Add(new SentReminder
        {
            EventId = EventId, RecipientEmail = "p@example.test", ReminderType = "welcome",
            OccasionKey = $"welcome:{pid}", SentAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        var sender = new CapturingEmailSender();
        var welcome = new WelcomeEmailService(db, Templates(), sender, TimeProvider.System);

        Assert.True(await welcome.SendSpeakerHatWelcomeAsync(pid));
        Assert.False(await welcome.SendSpeakerHatWelcomeAsync(pid));   // once, ever

        Assert.Single(sender.Sent);
        Assert.True(await db.SentReminders.AnyAsync(s => s.OccasionKey == $"welcome-speaker-hat:{pid}"));
        Assert.True(await db.SentReminders.AnyAsync(s => s.OccasionKey == $"welcome:{pid}"));   // untouched
    }

    [Fact]
    public async Task No_speaker_welcome_before_the_organizer_sets_a_category()
    {
        await using var db = NewDb();
        var pid = await PersonAsync(db, ParticipantRole.Volunteer, profile: true, category: null);
        var sender = new CapturingEmailSender();

        Assert.False(await new WelcomeEmailService(db, Templates(), sender, TimeProvider.System).SendSpeakerHatWelcomeAsync(pid));
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task A_linked_duplicate_does_not_count_for_the_party()
    {
        await using var db = NewDb();
        var primary = await PersonAsync(db, ParticipantRole.Volunteer, profile: false, email: "a@example.test");
        var duplicate = await PersonAsync(db, ParticipantRole.Speaker, profile: false, email: "b@example.test");
        db.PartyRsvps.Add(new PartyRsvp { EventId = EventId, ParticipantId = primary, Name = "A", Email = "a@example.test", Attending = true });
        db.PartyRsvps.Add(new PartyRsvp { EventId = EventId, ParticipantId = duplicate, Name = "B", Email = "b@example.test", Attending = true });
        await db.SaveChangesAsync();
        Assert.Equal((2, 2), await new PartyRsvpService(db).CountsAsync(EventId));

        (await db.Participants.FindAsync(duplicate))!.SamePersonAsId = primary;
        await db.SaveChangesAsync();

        var svc = new PartyRsvpService(db);
        Assert.Equal((1, 1), await svc.CountsAsync(EventId));
        Assert.Single(await svc.GetAllAsync(EventId));   // the list and the count still agree
    }

    // ---- fixture ----------------------------------------------------------

    private SpeakerDeadlineSeeder Seeder(CommunityHubDbContext db) =>
        new(db, new SpeakerDeadlineOptions { ConfigPath = _configPath }, TimeProvider.System);

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>().UseInMemoryDatabase($"panel-hat-{Guid.NewGuid():N}").Options);

    private static EmailTemplateProvider Templates() => new(Options.Create(new EmailTemplateOptions
    {
        TemplateDirectory = RepoPaths.EmailTemplates(),
        PrivateTemplateDirectory = Path.Combine(Path.GetTempPath(), "ceh-no-private-welcome"),
    }));

    private static async Task<int> PersonAsync(
        CommunityHubDbContext db, ParticipantRole role, bool profile,
        SpeakerCategory? category = SpeakerCategory.Community, string email = "p@example.test")
    {
        if (!await db.Events.AnyAsync(e => e.Id == EventId))
            db.Events.Add(new Event { Id = EventId, Code = "TEST", DisplayName = "Test 2027", CommunityName = "C", IsActive = true });
        var p = new Participant { EventId = EventId, Email = email, FullName = "Pat Lee", Role = role, IsActive = true };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        if (profile)
        {
            db.SpeakerProfiles.Add(new SpeakerProfile { EventId = EventId, ParticipantId = p.Id, SessionizeSpeakerId = $"s{p.Id}", Category = category });
            await db.SaveChangesAsync();
        }
        return p.Id;
    }

    private static async Task<Session> SessionAsync(CommunityHubDbContext db, int participantId, SessionType type)
    {
        var s = new Session { EventId = EventId, Title = $"{type} {Guid.NewGuid():N}", SessionizeId = Guid.NewGuid().ToString("N"), Type = type };
        s.SessionSpeakers.Add(new SessionSpeaker { Session = s, ParticipantId = participantId });
        db.Sessions.Add(s);
        await db.SaveChangesAsync();
        return s;
    }

    private static Task<List<string>> TitlesAsync(CommunityHubDbContext db, int pid) =>
        db.Tasks.Where(t => t.AssignedParticipantId == pid && t.SourceKey != null && t.SourceKey.StartsWith("speakerdl:"))
            .Select(t => t.Title).ToListAsync();

    [Fact]
    public async Task An_organizer_who_speaks_stays_outside_the_speaker_programme()
    {
        // §1255g stays in force on PROD: no speaker tasks and no speaker welcome for organizers who speak.
        await using var db = NewDb();
        var pid = await PersonAsync(db, ParticipantRole.Organizer, profile: true);
        await SessionAsync(db, pid, SessionType.TechnicalSession);
        var sender = new CapturingEmailSender();

        await Seeder(db).SeedAsync(EventId);

        Assert.Empty(await TitlesAsync(db, pid));
        Assert.False(await new WelcomeEmailService(db, Templates(), sender, TimeProvider.System).SendSpeakerHatWelcomeAsync(pid));
        Assert.Empty(sender.Sent);
    }
}