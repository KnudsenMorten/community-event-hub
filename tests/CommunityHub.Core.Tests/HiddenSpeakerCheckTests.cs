using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Participants;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1385 — "No hidden speakers" (operator 2026-11-07: <i>"we cannot have hidden speakers we
/// forgot"</i>): the check behind the Speakers-page box and the weekly organizer notice.
/// Fake names only; in-memory DB.
/// </summary>
public sealed class HiddenSpeakerCheckTests
{
    // Monday 9 Nov 2026 — CET (UTC+1), so 08:00 local is 07:00 UTC.
    private static readonly DateTimeOffset MondayAfterEight = new(2026, 11, 9, 7, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset MondayBeforeEight = new(2026, 11, 9, 6, 30, 0, TimeSpan.Zero);

    private sealed record World(CommunityHubDbContext Db, int EventId);

    private static async Task<World> NewWorldAsync()
    {
        var db = TestDb.New();
        var ev = new Event { CommunityName = "C", DisplayName = "Community 2027", Code = "C27", IsActive = true };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        return new World(db, ev.Id);
    }

    private static async Task<Participant> AddPersonAsync(
        World w, string name, ParticipantRole role = ParticipantRole.Speaker,
        bool active = true, bool test = false, bool profile = true)
    {
        var p = new Participant
        {
            EventId = w.EventId, FullName = name, Role = role,
            Email = name.Replace(' ', '.').ToLowerInvariant() + "@example.test",
            IsActive = active, IsTestUser = test, LifecycleState = ParticipantLifecycleState.Active,
            CreatedAt = new DateTimeOffset(2026, 10, 22, 10, 0, 0, TimeSpan.Zero),
        };
        w.Db.Participants.Add(p);
        await w.Db.SaveChangesAsync();
        if (profile && role == ParticipantRole.Speaker)
        {
            w.Db.SpeakerProfiles.Add(new SpeakerProfile { EventId = w.EventId, ParticipantId = p.Id });
            await w.Db.SaveChangesAsync();
        }
        return p;
    }

    private static async Task<Session> AddSessionAsync(
        World w, string title, bool usedForTesting = false, bool isTestData = false,
        bool service = false, bool excludeFromSoMe = false, params Participant[] speakers)
    {
        var s = new Session
        {
            EventId = w.EventId, Title = title, UsedForTesting = usedForTesting,
            IsTestData = isTestData, IsServiceSession = service,
            ExcludeFromSoMeAnnouncements = excludeFromSoMe,
        };
        w.Db.Sessions.Add(s);
        await w.Db.SaveChangesAsync();
        foreach (var p in speakers)
            w.Db.SessionSpeakers.Add(new SessionSpeaker { SessionId = s.Id, ParticipantId = p.Id });
        await w.Db.SaveChangesAsync();
        return s;
    }

    private static Task<HiddenSpeakerCheckService.Result> CheckAsync(World w) =>
        new HiddenSpeakerCheckService(w.Db).CheckAsync(w.EventId);

    // ---------------------------------------------------------------- the check

    [Fact]
    public async Task A_speaker_on_no_session_is_found_with_when_they_were_created()
    {
        var w = await NewWorldAsync();
        var kine = await AddPersonAsync(w, "Karen Nobody");

        var r = await CheckAsync(w);

        var item = Assert.Single(r.SpeakersWithoutSession);
        Assert.Equal(kine.Id, item.ParticipantId);
        Assert.Equal("Karen Nobody", item.Name);
        Assert.Equal(kine.CreatedAt, item.CreatedAt);
        Assert.True(r.AnyProblem);
    }

    [Fact]
    public async Task A_speaker_only_on_a_TEST_session_counts_as_on_no_session()
    {
        var w = await NewWorldAsync();
        var a = await AddPersonAsync(w, "Alice Testonly");
        var b = await AddPersonAsync(w, "Bob Legacyflag");
        await AddSessionAsync(w, "Test Session", usedForTesting: true, speakers: a);
        await AddSessionAsync(w, "Legacy test", isTestData: true, speakers: b);

        var r = await CheckAsync(w);

        Assert.Equal(new[] { a.Id, b.Id }, r.SpeakersWithoutSession.Select(s => s.ParticipantId).OrderBy(i => i));
        // Test sessions are not "real", so they are never reported as sessions with no speakers either.
        Assert.Empty(r.SessionsWithoutSpeakers);
    }

    [Fact]
    public async Task Test_users_inactive_speakers_and_other_roles_are_not_reported()
    {
        var w = await NewWorldAsync();
        await AddPersonAsync(w, "Tess Testuser", test: true, profile: false);
        await AddPersonAsync(w, "Ivan Inactive", active: false, profile: false);
        await AddPersonAsync(w, "Vera Volunteer", role: ParticipantRole.Volunteer);

        var r = await CheckAsync(w);

        Assert.Empty(r.SpeakersWithoutSession);
        Assert.Empty(r.SpeakersWithoutProfile);
        Assert.False(r.AnyProblem);
    }

    [Fact]
    public async Task A_speaker_without_a_speaker_profile_is_found()
    {
        var w = await NewWorldAsync();
        var s = await AddPersonAsync(w, "Sam Noprofile", profile: false);
        await AddSessionAsync(w, "Real talk", speakers: s);

        var r = await CheckAsync(w);

        Assert.Empty(r.SpeakersWithoutSession);
        Assert.Equal(s.Id, Assert.Single(r.SpeakersWithoutProfile).ParticipantId);
        Assert.True(r.AnyProblem);
    }

    [Fact]
    public async Task A_real_session_with_no_speakers_is_found_but_service_and_test_sessions_are_not()
    {
        var w = await NewWorldAsync();
        var panel = await AddSessionAsync(w, "Panel: Security & Compliance");
        await AddSessionAsync(w, "Lunch", service: true);
        await AddSessionAsync(w, "Test Session", usedForTesting: true);

        var r = await CheckAsync(w);

        var item = Assert.Single(r.SessionsWithoutSpeakers);
        Assert.Equal(panel.Id, item.SessionId);
        Assert.False(item.ExpectedEmpty);
        Assert.Single(r.SessionsNeedingSpeakers);
        Assert.True(r.AnyProblem);
    }

    [Theory]
    [InlineData("[Placeholder] Keynote")]
    [InlineData("Sponsor Session TBD")]
    [InlineData("sponsor session tbd")]
    [InlineData("[PLACEHOLDER] slot")]
    public async Task Placeholder_and_TBD_sessions_are_listed_as_expected_empty_not_as_a_problem(string title)
    {
        var w = await NewWorldAsync();
        await AddSessionAsync(w, title);

        var r = await CheckAsync(w);

        var item = Assert.Single(r.SessionsWithoutSpeakers);
        Assert.True(item.ExpectedEmpty);
        Assert.Single(r.ExpectedEmptySessions);
        Assert.Empty(r.SessionsNeedingSpeakers);
        Assert.False(r.AnyProblem);
        Assert.Contains("(+ 1 expected empty)", r.Summary);
    }

    [Fact]
    public async Task A_speaker_linked_to_a_real_session_is_not_reported_even_when_it_is_kept_off_social_media()
    {
        // 🔑 ExcludeFromSoMeAnnouncements decides whether a session is ANNOUNCED, not whether it exists.
        var w = await NewWorldAsync();
        var s = await AddPersonAsync(w, "Rita Real");
        var t = await AddPersonAsync(w, "Sven Sponsorspeaker");
        await AddSessionAsync(w, "Real talk", speakers: s);
        await AddSessionAsync(w, "Sponsor talk", excludeFromSoMe: true, speakers: t);

        var r = await CheckAsync(w);

        Assert.Empty(r.SpeakersWithoutSession);
        Assert.Empty(r.SpeakersWithoutProfile);
        Assert.Empty(r.SessionsWithoutSpeakers);
        Assert.False(r.AnyProblem);
    }

    [Fact]
    public async Task Another_editions_people_and_sessions_are_never_counted()
    {
        var w = await NewWorldAsync();
        var other = new Event { CommunityName = "C", DisplayName = "Old", Code = "C26" };
        w.Db.Events.Add(other);
        await w.Db.SaveChangesAsync();
        w.Db.Participants.Add(new Participant
        {
            EventId = other.Id, FullName = "Old Speaker", Email = "old@example.test",
            Role = ParticipantRole.Speaker, IsActive = true, LifecycleState = ParticipantLifecycleState.Active,
        });
        w.Db.Sessions.Add(new Session { EventId = other.Id, Title = "Old talk" });
        await w.Db.SaveChangesAsync();

        var r = await CheckAsync(w);

        Assert.False(r.AnyProblem);
        Assert.Empty(r.SessionsWithoutSpeakers);
    }

    // ---------------------------------------------------------------- the weekly notice

    private static HiddenSpeakersReminderBuilder NewBuilder(World w, DateTimeOffset now) =>
        new(w.Db,
            new EmailTemplateProvider(Options.Create(new EmailTemplateOptions
            {
                TemplateDirectory = RepoPaths.EmailTemplates(),
                PrivateTemplateDirectory = Path.Combine(Path.GetTempPath(), $"no-overlay-{Guid.NewGuid():N}"),
                HubUrl = "https://hub.example.test",
            })),
            new FixedClock(now),
            new HiddenSpeakerCheckService(w.Db));

    [Fact]
    public async Task The_weekly_notice_goes_to_organizers_only_when_something_is_hidden()
    {
        var w = await NewWorldAsync();
        var org = await AddPersonAsync(w, "Olive Organizer", role: ParticipantRole.Organizer);
        await AddPersonAsync(w, "Inactive Organizer", role: ParticipantRole.Organizer, active: false);
        await AddPersonAsync(w, "Vera Volunteer", role: ParticipantRole.Volunteer);
        await AddPersonAsync(w, "Karen Nobody");   // a speaker on no session

        var due = await NewBuilder(w, MondayAfterEight).BuildDueAsync(w.EventId);

        var msg = Assert.Single(due);
        Assert.Equal(org.Email, msg.RecipientEmail);
        Assert.Equal(org.Id, msg.ParticipantId);
        Assert.Equal(HiddenSpeakersReminderBuilder.TemplateName, msg.MailKey);
        Assert.Equal("hidden-speakers-weekly", msg.MailKey);
        Assert.Equal($"{HiddenSpeakersReminderBuilder.ReminderType}:{org.Id}:2026w46", msg.OccasionKey);
        Assert.Contains("Karen Nobody", msg.HtmlBody);
        Assert.Contains("/Organizer/Speakers", msg.HtmlBody);
        Assert.Contains("hidden speakers", msg.Subject, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_weekly_notice_sends_nothing_when_nothing_is_hidden_or_only_placeholders_are_empty()
    {
        var w = await NewWorldAsync();
        await AddPersonAsync(w, "Olive Organizer", role: ParticipantRole.Organizer);
        var s = await AddPersonAsync(w, "Rita Real");
        await AddSessionAsync(w, "Real talk", speakers: s);
        await AddSessionAsync(w, "Sponsor Session TBD");

        var due = await NewBuilder(w, MondayAfterEight).BuildDueAsync(w.EventId);

        Assert.Empty(due);
    }

    [Fact]
    public async Task The_weekly_notice_is_not_due_before_Monday_0800_Danish_time()
    {
        var w = await NewWorldAsync();
        await AddPersonAsync(w, "Olive Organizer", role: ParticipantRole.Organizer);
        await AddPersonAsync(w, "Karen Nobody");

        Assert.Empty(await NewBuilder(w, MondayBeforeEight).BuildDueAsync(w.EventId));
        Assert.Null(HiddenSpeakersReminderBuilder.WeekKeyIfDue(MondayBeforeEight));
        Assert.Equal("2026w46", HiddenSpeakersReminderBuilder.WeekKeyIfDue(MondayAfterEight));
        // Later in the same week it is still due (a missed Monday run self-heals) under the SAME key,
        // so the ledger still allows only one per organizer per week.
        Assert.Equal("2026w46", HiddenSpeakersReminderBuilder.WeekKeyIfDue(MondayAfterEight.AddDays(3)));
    }

    [Fact]
    public void The_mail_is_registered_as_an_organizer_mail_with_its_own_ring()
    {
        Assert.True(EmailTemplateCatalog.Map.ContainsKey("hidden-speakers-weekly"));
        Assert.Equal(EmailAudience.Organizer, EmailTemplateCatalog.AudienceFor("hidden-speakers-weekly"));
        Assert.False(EmailTemplateCatalog.IsRingExempt("hidden-speakers-weekly"));
        Assert.Equal(new[] { ParticipantRole.Organizer }, EmailTemplateCatalog.RecipientRolesFor("hidden-speakers-weekly"));
        Assert.Contains("Organizers only", EmailTemplateCatalog.RecipientHint("hidden-speakers-weekly"));
    }
}
