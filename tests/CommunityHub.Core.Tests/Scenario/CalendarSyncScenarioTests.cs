using CommunityHub.Core.Config;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests.Scenario;

/// <summary>
/// SCENARIO: speakers, volunteers and organizers subscribe their reminders /
/// deadlines to their own calendar via the per-user iCal feed.
///
/// Backend proof of the GET /calendar/{token}.ics feature:
///  - the feed is a VALID RFC 5545 VCALENDAR (BEGIN/END, VERSION, VEVENTs);
///  - each role's own time-bound items appear (speaker milestones, volunteer
///    shifts, organizer tasks) and NOTHING that belongs to anyone else;
///  - the per-user token is unguessable, resolves to exactly one participant,
///    and regeneration (revoke) invalidates the old token;
///  - every VEVENT has a STABLE UID (so a re-fetch updates, never duplicates);
///  - deadline events carry VALARM reminders (7 & 1 days before).
/// </summary>
public sealed class CalendarSyncScenarioTests
{
    private const string UidHost = "hub.example.test";

    private static SpeakerDeadlineSeeder NewSeeder(Data.CommunityHubDbContext db) =>
        new(db,
            new SpeakerDeadlineOptions { ConfigPath = RepoPaths.SpeakerDeadlinesConfig() },
            ScenarioFixture.Clock);

    // ---- Token security ----------------------------------------------------

    [Fact]
    public async Task Token_is_minted_unguessable_and_idempotent()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = new CalendarFeedTokenService(db);

        var t1 = await svc.EnsureTokenAsync(seed.SpeakerOneId);
        var t2 = await svc.EnsureTokenAsync(seed.SpeakerOneId);

        Assert.False(string.IsNullOrWhiteSpace(t1));
        Assert.True(t1.Length >= 40, "256-bit token should be long");
        // URL-safe: no +, /, or = so it is safe in a webcal:// URL.
        Assert.DoesNotContain('+', t1);
        Assert.DoesNotContain('/', t1);
        Assert.DoesNotContain('=', t1);
        Assert.Equal(t1, t2); // idempotent — same token on re-call
    }

    [Fact]
    public async Task Token_resolves_to_exactly_one_participant()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = new CalendarFeedTokenService(db);

        var speakerToken = await svc.EnsureTokenAsync(seed.SpeakerOneId);
        var volunteerToken = await svc.EnsureTokenAsync(seed.VolunteerId);

        Assert.NotEqual(speakerToken, volunteerToken);
        Assert.Equal(seed.SpeakerOneId, await svc.ResolveParticipantIdAsync(speakerToken));
        Assert.Equal(seed.VolunteerId, await svc.ResolveParticipantIdAsync(volunteerToken));
    }

    [Fact]
    public async Task Unknown_or_empty_token_resolves_to_null()
    {
        using var db = ScenarioFixture.NewDb();
        await ScenarioSeed.SeedAsync(db);
        var svc = new CalendarFeedTokenService(db);

        Assert.Null(await svc.ResolveParticipantIdAsync(null));
        Assert.Null(await svc.ResolveParticipantIdAsync(""));
        Assert.Null(await svc.ResolveParticipantIdAsync("not-a-real-token"));
    }

    [Fact]
    public async Task Regenerating_token_revokes_the_old_one()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = new CalendarFeedTokenService(db);

        var old = await svc.EnsureTokenAsync(seed.SpeakerOneId);
        var fresh = await svc.RegenerateTokenAsync(seed.SpeakerOneId);

        Assert.NotEqual(old, fresh);
        // Old token no longer resolves (revoked); new one resolves.
        Assert.Null(await svc.ResolveParticipantIdAsync(old));
        Assert.Equal(seed.SpeakerOneId, await svc.ResolveParticipantIdAsync(fresh));
    }

    [Fact]
    public async Task Deactivated_participant_token_does_not_resolve()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = new CalendarFeedTokenService(db);

        var token = await svc.EnsureTokenAsync(seed.SpeakerOneId);
        var p = await db.Participants.FirstAsync(x => x.Id == seed.SpeakerOneId);
        p.IsActive = false;
        await db.SaveChangesAsync();

        Assert.Null(await svc.ResolveParticipantIdAsync(token));
    }

    // ---- Organizer enable/disable gate -------------------------------------

    [Fact]
    public async Task Token_resolves_while_calendar_sync_is_enabled()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = new CalendarFeedTokenService(db);

        // Default is ON for a seeded edition.
        var token = await svc.EnsureTokenAsync(seed.SpeakerOneId);
        Assert.Equal(seed.SpeakerOneId, await svc.ResolveParticipantIdAsync(token));
    }

    [Fact]
    public async Task Disabling_calendar_sync_makes_the_feed_404_for_the_whole_edition()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var svc = new CalendarFeedTokenService(db);

        var token = await svc.EnsureTokenAsync(seed.SpeakerOneId);
        Assert.NotNull(await svc.ResolveParticipantIdAsync(token)); // on by default

        // Organizer turns calendar sync OFF for the edition.
        var ev = await db.Events.FirstAsync(e => e.Id == seed.EventId);
        ev.CalendarSyncEnabled = false;
        await db.SaveChangesAsync();

        // The token no longer resolves => the controller returns 404 (the token
        // value is unchanged, so re-enabling restores access without re-minting).
        Assert.Null(await svc.ResolveParticipantIdAsync(token));

        ev.CalendarSyncEnabled = true;
        await db.SaveChangesAsync();
        Assert.Equal(seed.SpeakerOneId, await svc.ResolveParticipantIdAsync(token));
    }

    // ---- Valid VCALENDAR output --------------------------------------------

    [Fact]
    public async Task Feed_is_a_valid_vcalendar()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        await NewSeeder(db).SeedAsync(seed.EventId);

        var ics = await new ParticipantCalendarBuilder(db)
            .BuildFeedAsync(seed.SpeakerOneId, UidHost);

        Assert.StartsWith("BEGIN:VCALENDAR", ics);
        Assert.Contains("VERSION:2.0", ics);
        Assert.Contains("PRODID:", ics);
        Assert.Contains("METHOD:PUBLISH", ics);
        Assert.EndsWith("END:VCALENDAR\r\n", ics);
        // RFC 5545 mandates CRLF line endings.
        Assert.Contains("\r\n", ics);
        // Balanced VEVENT blocks.
        Assert.Equal(
            CountOccurrences(ics, "BEGIN:VEVENT"),
            CountOccurrences(ics, "END:VEVENT"));
        Assert.True(CountOccurrences(ics, "BEGIN:VEVENT") > 0, "speaker has milestones");
    }

    // ---- Role coverage: speaker --------------------------------------------

    [Fact]
    public async Task Speaker_feed_contains_milestone_deadlines_with_valarm()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        await NewSeeder(db).SeedAsync(seed.EventId);

        var ics = await new ParticipantCalendarBuilder(db)
            .BuildFeedAsync(seed.SpeakerOneId, UidHost);

        // A plain speaker (not pre-/main-day) gets 6 milestone deadlines: Hotel,
        // Dinner, Swag (1 Oct) + the two presentation uploads + the §143 non-Denmark
        // travel-reimbursement task (the seed speaker has no country = non-DK). The
        // Pre-day Lunch is P12 entitlement-gated out for a non-pre-day speaker.
        Assert.Equal(6, CountOccurrences(ics, "BEGIN:VEVENT"));
        // VALARM reminders present (7 & 1 days before).
        Assert.True(CountOccurrences(ics, "BEGIN:VALARM") >= 2);
        Assert.Contains("TRIGGER:-P7D", ics);
        Assert.Contains("TRIGGER:-P1D", ics);
        // All-day deadlines use DATE values.
        Assert.Contains("DTSTART;VALUE=DATE:", ics);
    }

    [Fact]
    public async Task Masterclass_speaker_gets_the_full_milestone_set()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        await NewSeeder(db).SeedAsync(seed.EventId);

        var ics = await new ParticipantCalendarBuilder(db)
            .BuildFeedAsync(seed.MasterclassSpeakerId, UidHost);

        // A Master Class (pre-day) speaker is entitled to the Pre-day Lunch, so gets
        // the FULL set of 7 milestones (the 6 a plain non-DK speaker gets + Pre-day Lunch).
        Assert.Equal(7, CountOccurrences(ics, "BEGIN:VEVENT"));
    }

    // ---- Role coverage: volunteer ------------------------------------------

    [Fact]
    public async Task Volunteer_feed_contains_their_shifts()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);

        db.VolunteerAvailabilities.Add(new VolunteerAvailability
        {
            EventId = seed.EventId,
            ParticipantId = seed.VolunteerId,
            SelectedShifts = "Registration desk, A/V support",
            PreferredRole = "Registration",
        });
        await db.SaveChangesAsync();

        var ics = await new ParticipantCalendarBuilder(db)
            .BuildFeedAsync(seed.VolunteerId, UidHost);

        Assert.Contains("Volunteer: Registration desk", ics);
        Assert.Contains("Volunteer: A/V support", ics);
        Assert.Equal(2, CountOccurrences(ics, "BEGIN:VEVENT"));
        Assert.Contains($"@{UidHost}", ics); // stable UID host
    }

    // ---- Role coverage: organizer ------------------------------------------

    [Fact]
    public async Task Organizer_feed_contains_their_assigned_dated_tasks()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);

        db.Tasks.Add(new ParticipantTask
        {
            EventId = seed.EventId,
            AssignedParticipantId = seed.OrganizerId,
            Title = "Confirm venue catering numbers",
            DueDate = new DateOnly(2027, 1, 25),
            State = TaskState.Open,
            SourceKey = "org-task:catering",
        });
        await db.SaveChangesAsync();

        var ics = await new ParticipantCalendarBuilder(db)
            .BuildFeedAsync(seed.OrganizerId, UidHost);

        Assert.Contains("Confirm venue catering numbers", ics);
        Assert.Contains("BEGIN:VALARM", ics);
    }

    // ---- Per-user scoping (no leakage) -------------------------------------

    [Fact]
    public async Task Feed_is_scoped_to_the_participants_own_items_only()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        await NewSeeder(db).SeedAsync(seed.EventId);

        // Give speaker one a uniquely-titled task; it must NOT appear in
        // speaker two's feed.
        db.Tasks.Add(new ParticipantTask
        {
            EventId = seed.EventId,
            AssignedParticipantId = seed.SpeakerOneId,
            Title = "PRIVATE-SPEAKER-ONE-ITEM",
            DueDate = new DateOnly(2027, 1, 5),
            State = TaskState.Open,
            SourceKey = "scoping-test:s1",
        });
        await db.SaveChangesAsync();

        var builder = new ParticipantCalendarBuilder(db);
        var oneIcs = await builder.BuildFeedAsync(seed.SpeakerOneId, UidHost);
        var twoIcs = await builder.BuildFeedAsync(seed.SpeakerTwoId, UidHost);

        Assert.Contains("PRIVATE-SPEAKER-ONE-ITEM", oneIcs);
        Assert.DoesNotContain("PRIVATE-SPEAKER-ONE-ITEM", twoIcs);
    }

    // ---- Stable UIDs (updates, not duplicates) -----------------------------

    [Fact]
    public async Task Uids_are_stable_across_fetches_and_match_single_item_download()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        await NewSeeder(db).SeedAsync(seed.EventId);

        var builder = new ParticipantCalendarBuilder(db);

        var first = await builder.BuildFeedAsync(seed.SpeakerOneId, UidHost);
        var second = await builder.BuildFeedAsync(seed.SpeakerOneId, UidHost);

        var firstUids = ExtractUids(first);
        var secondUids = ExtractUids(second);
        Assert.NotEmpty(firstUids);
        Assert.Equal(firstUids, secondUids); // identical UIDs => calendar updates in place

        // The single-item download for one of those tasks reuses the same UID,
        // so download-then-subscribe never produces a duplicate.
        var oneTask = await db.Tasks
            .Where(t => t.AssignedParticipantId == seed.SpeakerOneId && t.DueDate != null)
            .OrderBy(t => t.Id)
            .FirstAsync();
        var single = await builder.BuildSingleTaskAsync(seed.SpeakerOneId, oneTask.Id, UidHost);
        Assert.NotNull(single);
        Assert.Contains($"UID:task:{oneTask.Id}@{UidHost}", single!);
        Assert.Contains($"task:{oneTask.Id}@{UidHost}", firstUids.First(u => u.Contains($"task:{oneTask.Id}")));
    }

    [Fact]
    public async Task Single_item_download_rejects_another_participants_task()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        await NewSeeder(db).SeedAsync(seed.EventId);

        var foreignTask = await db.Tasks
            .Where(t => t.AssignedParticipantId == seed.SpeakerOneId && t.DueDate != null)
            .FirstAsync();

        // Speaker two asking for speaker one's task id => null (no leak).
        var single = await new ParticipantCalendarBuilder(db)
            .BuildSingleTaskAsync(seed.SpeakerTwoId, foreignTask.Id, UidHost);
        Assert.Null(single);
    }

    // ---- Organizer "preview my feed" (REQUIREMENTS §21) --------------------

    [Fact]
    public async Task Preview_lists_the_same_dated_items_the_feed_contains()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);

        db.Tasks.Add(new ParticipantTask
        {
            EventId = seed.EventId,
            AssignedParticipantId = seed.OrganizerId,
            Title = "PREVIEW-ORGANIZER-TASK",
            DueDate = new DateOnly(2027, 1, 25),
            State = TaskState.Open,
            SourceKey = "preview-test:org",
        });
        await db.SaveChangesAsync();

        var preview = await new ParticipantCalendarBuilder(db)
            .BuildPreviewAsync(seed.OrganizerId);

        var row = Assert.Single(preview, r => r.Summary == "PREVIEW-ORGANIZER-TASK");
        Assert.Equal(new DateOnly(2027, 1, 25), row.Date);
        Assert.True(row.AllDay);
    }

    [Fact]
    public async Task Preview_is_scoped_to_the_participants_own_items_only()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);

        db.Tasks.Add(new ParticipantTask
        {
            EventId = seed.EventId,
            AssignedParticipantId = seed.SpeakerOneId,
            Title = "PREVIEW-SPEAKER-ONE-ONLY",
            DueDate = new DateOnly(2027, 1, 6),
            State = TaskState.Open,
            SourceKey = "preview-scoping:s1",
        });
        await db.SaveChangesAsync();

        var builder = new ParticipantCalendarBuilder(db);
        var onePreview = await builder.BuildPreviewAsync(seed.SpeakerOneId);
        var twoPreview = await builder.BuildPreviewAsync(seed.SpeakerTwoId);

        Assert.Contains(onePreview, r => r.Summary == "PREVIEW-SPEAKER-ONE-ONLY");
        Assert.DoesNotContain(twoPreview, r => r.Summary == "PREVIEW-SPEAKER-ONE-ONLY");
    }

    [Fact]
    public async Task Preview_is_date_ordered_and_empty_for_an_unknown_participant()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);

        foreach (var (title, due, key) in new[]
                 {
                     ("PREVIEW-LATER", new DateOnly(2027, 2, 10), "preview-order:b"),
                     ("PREVIEW-EARLIER", new DateOnly(2027, 1, 2), "preview-order:a"),
                 })
        {
            db.Tasks.Add(new ParticipantTask
            {
                EventId = seed.EventId,
                AssignedParticipantId = seed.OrganizerId,
                Title = title,
                DueDate = due,
                State = TaskState.Open,
                SourceKey = key,
            });
        }
        await db.SaveChangesAsync();

        var builder = new ParticipantCalendarBuilder(db);
        var preview = await builder.BuildPreviewAsync(seed.OrganizerId);

        var idxEarlier = preview.ToList().FindIndex(r => r.Summary == "PREVIEW-EARLIER");
        var idxLater = preview.ToList().FindIndex(r => r.Summary == "PREVIEW-LATER");
        Assert.True(idxEarlier >= 0 && idxLater >= 0);
        Assert.True(idxEarlier < idxLater, "preview rows are date-ordered (earliest first)");

        // Unknown participant => empty (never throws).
        Assert.Empty(await builder.BuildPreviewAsync(999999));
    }

    // ---- helpers -----------------------------------------------------------

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }

    private static List<string> ExtractUids(string ics)
    {
        var uids = new List<string>();
        foreach (var line in ics.Split("\r\n"))
        {
            if (line.StartsWith("UID:", StringComparison.Ordinal))
            {
                uids.Add(line.Substring("UID:".Length));
            }
        }
        return uids;
    }
}
