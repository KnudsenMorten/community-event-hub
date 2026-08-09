using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 🔴 §1021 — a speaker who changes their e-mail in Sessionize is the SAME PERSON: the stable
/// Sessionize id decides, and the address is just an attribute that moved.
/// </summary>
/// <remarks>
/// <para><b>The incident, measured in PROD 2026-08-09.</b> One speaker had become TWO participants
/// sharing Sessionize id <c>a09f9626…</c> — <c>#41 thomas@impnd.com</c> (organizer-DEACTIVATED on
/// 4 Aug, and still the row holding session 15) and <c>#110 thomas.martinsen@hey.com</c> (active, no
/// sessions). He then renamed himself in Sessionize <b>back</b> to <c>thomas@impnd.com</c>.</para>
///
/// <para>The importer looked that address up, hit the <b>deactivated</b> #41, updated a name and
/// stopped. The live row never learned the address, nothing reached Zoho, and the operator's report
/// was simply *"it doesn't happen"*.</para>
///
/// <para>🔑 <b>§827 had already written the rule — and the code did the opposite.</b> Its comment
/// says *"e-mail is a MUTABLE ATTRIBUTE of a person; the Sessionize id IS the person"*, then tested
/// e-mail FIRST, so any stale row holding an address could pre-empt the id match.</para>
/// </remarks>
public sealed class SessionizeEmailRenameIdWinsTests
{
    private const string SzId = "a09f9626-0ae6-417e-85fa-7e651297b946";

    private static SessionizeImportService NewImporter(CommunityHubDbContext db)
    {
        var templates = new EmailTemplateProvider(
            Options.Create(new EmailTemplateOptions { TemplateDirectory = RepoPaths.EmailTemplates() }));
        var welcome = new WelcomeEmailService(db, templates, new CapturingEmailSender(), ScenarioFixture.Clock);
        return new SessionizeImportService(db, welcome, ScenarioFixture.Clock);
    }

    private static SessionizeSpeaker Speaker(string email, string szId = SzId) =>
        new(email, "Thomas", "Martinsen", TagLine: null, SessionizeId: szId);

    private static async Task<Participant> SeedSpeakerAsync(
        CommunityHubDbContext db, int eventId, string email, string szId,
        bool deactivated = false)
    {
        var p = new Participant
        {
            EventId = eventId, Email = email, FullName = "Thomas Martinsen",
            Role = ParticipantRole.Speaker, IsActive = !deactivated,
            LifecycleState = ParticipantLifecycleState.Active,
            DeactivatedByOrganizerAt = deactivated ? DateTimeOffset.UtcNow : null,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        db.SpeakerProfiles.Add(new SpeakerProfile
        {
            EventId = eventId, ParticipantId = p.Id,
            FirstName = "Thomas", LastName = "Martinsen", SessionizeSpeakerId = szId,
        });
        await db.SaveChangesAsync();
        return p;
    }

    /// <summary>
    /// 🔴 THE HEADLINE: an ordinary rename moves the address on the EXISTING row rather than
    /// forking a second participant. This is the case that created the PROD pair in the first place.
    /// </summary>
    [Fact]
    public async Task A_changed_email_renames_the_person_instead_of_creating_a_second_row()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var before = await SeedSpeakerAsync(db, seed.EventId, "old@x.dk", SzId);

        await NewImporter(db).ImportSpeakersAsync(
            seed.EventId, new[] { Speaker("new@x.dk") }, Array.Empty<string>(), sendWelcome: false);

        var rows = await db.Participants
            .Where(p => p.EventId == seed.EventId && p.FullName == "Thomas Martinsen")
            .ToListAsync();
        var row = Assert.Single(rows);                 // ONE person, not two
        Assert.Equal(before.Id, row.Id);               // …and it is the same row
        Assert.Equal("new@x.dk", row.Email);
    }

    /// <summary>
    /// 🔴 THE EXACT PROD SEQUENCE. A DEACTIVATED row holds the address Sessionize is renaming TO,
    /// while the live row carries the same Sessionize id. E-mail-first matching hit the dead row and
    /// did nothing; the live row must not be silently left behind.
    /// </summary>
    [Fact]
    public async Task A_deactivated_row_holding_the_target_address_does_not_hijack_the_match()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var dead = await SeedSpeakerAsync(db, seed.EventId, "thomas@impnd.com", SzId, deactivated: true);
        var live = await SeedSpeakerAsync(db, seed.EventId, "thomas.martinsen@hey.com", SzId);

        await NewImporter(db).ImportSpeakersAsync(
            seed.EventId, new[] { Speaker("thomas@impnd.com") }, Array.Empty<string>(), sendWelcome: false);

        // 🔒 NO THIRD ROW. The old code's failure was silent; the danger in "fixing" it carelessly
        // is a fork, which is worse — that is what produced this pair to begin with.
        Assert.Equal(2, await db.Participants.CountAsync(
            p => p.EventId == seed.EventId && p.FullName == "Thomas Martinsen"));

        // 🔒 The deactivated row is NOT resurrected and NOT stripped of its address: an organizer
        // deactivated it deliberately, and it still holds the session in PROD.
        var deadAfter = await db.Participants.SingleAsync(p => p.Id == dead.Id);
        Assert.Equal("thomas@impnd.com", deadAfter.Email);
        Assert.NotNull(deadAfter.DeactivatedByOrganizerAt);

        // 🔒 And the live row keeps its own address rather than colliding on a unique key. Two
        // records for one person is a MERGE — which sessions, logins and Zoho ids survive is a
        // decision an importer must never infer.
        var liveAfter = await db.Participants.SingleAsync(p => p.Id == live.Id);
        Assert.Equal("thomas.martinsen@hey.com", liveAfter.Email);
    }

    /// <summary>
    /// 🔒 The id only decides when we HAVE one. A speaker whose Sessionize id CEH has never seen
    /// still matches on e-mail, which is every ordinary import.
    /// </summary>
    [Fact]
    public async Task An_unknown_sessionize_id_still_matches_on_email()
    {
        using var db = ScenarioFixture.NewDb();
        var seed = await ScenarioSeed.SeedAsync(db);
        var existing = await SeedSpeakerAsync(db, seed.EventId, "sam@x.dk", "some-other-id");

        await NewImporter(db).ImportSpeakersAsync(
            seed.EventId, new[] { new SessionizeSpeaker("sam@x.dk", "Sam", "Speaker", null, SessionizeId: "brand-new-id") },
            Array.Empty<string>(), sendWelcome: false);

        var rows = await db.Participants.Where(p => p.EventId == seed.EventId && p.Email == "sam@x.dk").ToListAsync();
        Assert.Single(rows);
        Assert.Equal(existing.Id, rows[0].Id);
    }
}
