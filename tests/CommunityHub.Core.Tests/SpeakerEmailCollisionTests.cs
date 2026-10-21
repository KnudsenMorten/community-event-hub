using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 🔴 §1252 — a Sessionize speaker whose real address ALREADY belongs to another participant must
/// not take the whole import down with them.
/// </summary>
/// <remarks>
/// <para>The real case, operator 2026-09-20: a speaker invited in Sessionize but not yet accepted
/// sits on a <c>…@no-email.sessionize.invalid</c> placeholder (§204). The same person is about to
/// become a sponsor's signer under her real address — she is already in the webshop and the ERP,
/// and *"once she signs the order, she will be synced to ceh"*. Which happens first is unknown:
/// *"whatever happens first…? dont know"*.</para>
///
/// <para>⚠️ <b>Why it had to be fixed rather than sequenced.</b> <c>(EventId, Email)</c> is UNIQUE.
/// §827's reconcile moves the real address onto the speaker row — correctly, since the Sessionize
/// id IS the person — but never considered the address being taken. Assigning it throws on
/// <c>SaveChanges</c>, and the import is ONE transaction, so it does not skip that speaker: it
/// fails the ENTIRE import, on every run, until a human diagnoses it.</para>
///
/// <para>✅ <b>The guard already existed</b> — §827 added it for the Thomas Martinsen case ("a
/// rename may not steal an address another participant still holds"), and it covers this scenario
/// unchanged. These tests are a REGRESSION PIN, not a new fix: the scenario sounded novel enough
/// that a duplicate guard was nearly added a second time, and this file is what makes the existing
/// behaviour visible instead.</para>
///
/// <para>◻ What it does NOT do is merge the two rows — that decides which sessions, logins and
/// Zoho ids survive, and an importer must never infer it. See REQUIREMENTS §1252.</para>
/// </remarks>
public sealed class SpeakerEmailCollisionTests
{
    private const int EventId = 1;
    private const string RealEmail = "michelleh@2pintsoftware.example";
    private const string SessionizeId = "683a3d06-6fea-42b1-9c7b-e32e3ec50ef1";

    private static async Task SeedAsync(CommunityHub.Core.Data.CommunityHubDbContext db)
    {
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 10), EndDate = new DateOnly(2027, 2, 10),
        });

        // 1. The SPEAKER row: invited in Sessionize, not accepted ⇒ placeholder address (§204).
        var speaker = new Participant
        {
            EventId = EventId,
            Email = SessionizeImportServiceFixture.PlaceholderEmail(SessionizeId),
            FullName = "Michelle H", Role = ParticipantRole.Speaker,
            IsActive = false, LifecycleState = ParticipantLifecycleState.Preselected,
        };
        db.Participants.Add(speaker);
        await db.SaveChangesAsync();
        db.SpeakerProfiles.Add(new SpeakerProfile
        {
            EventId = EventId, ParticipantId = speaker.Id,
            FirstName = "Michelle", LastName = "H", SessionizeSpeakerId = SessionizeId,
        });

        // 2. The SPONSOR row: the same human, signed the order first, under her real address.
        db.Participants.Add(new Participant
        {
            EventId = EventId, Email = RealEmail, FullName = "Michelle Hammarskjold",
            Role = ParticipantRole.Sponsor, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task The_import_SURVIVES_and_reports_both_rows()
    {
        using var db = ScenarioFixture.NewDb();
        await SeedAsync(db);
        var (import, _) = ScenarioFixture.NewImporter(db);

        // She accepts Sessionize: the pull now carries her REAL address for that speaker id.
        var result = await import.ImportSpeakersAsync(
            EventId,
            new[] { new SessionizeSpeaker(RealEmail, "Michelle", "Hammarskjold", null,
                                          SessionizeId: SessionizeId) },
            Array.Empty<string>(),
            sendWelcome: false);

        // 🔴 The import COMPLETED. `(EventId, Email)` is UNIQUE and the import is ONE transaction,
        // so had the reconcile written the address it would not have skipped this speaker — it
        // would have failed the ENTIRE run, every time, until a human diagnosed it.
        Assert.Null(result.Error);

        // Nothing was overwritten: two rows, each keeping its own address. The §827 guard abandons
        // the id match when another participant holds the address, and falls through to the e-mail
        // branch, which touches only that row's NAME.
        Assert.Equal(2, await db.Participants.CountAsync());
        Assert.NotNull(await db.Participants.FirstOrDefaultAsync(p => p.Email == RealEmail));
        Assert.NotNull(await db.Participants.FirstOrDefaultAsync(
            p => p.Email.EndsWith("no-email.sessionize.invalid")));

        // ◻ The two rows are still TWO PEOPLE as far as the hub is concerned. That is the §827
        // decision — a merge decides which sessions, logins and Zoho ids survive, and an importer
        // must never infer it — but it means nothing tells the organizer they need to merge. That
        // gap is real and is recorded in REQUIREMENTS §1252, not papered over here.
    }

    [Fact]
    public async Task A_normal_placeholder_reconcile_still_moves_the_address()
    {
        // 🔒 The guard must not break §204/§827: with NO competing row, accepting the invite still
        // moves the real address onto the speaker, which is the whole point of the reconcile.
        using var db = ScenarioFixture.NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 10), EndDate = new DateOnly(2027, 2, 10),
        });
        var speaker = new Participant
        {
            EventId = EventId,
            Email = SessionizeImportServiceFixture.PlaceholderEmail(SessionizeId),
            FullName = "Michelle H", Role = ParticipantRole.Speaker,
            IsActive = false, LifecycleState = ParticipantLifecycleState.Preselected,
        };
        db.Participants.Add(speaker);
        await db.SaveChangesAsync();
        db.SpeakerProfiles.Add(new SpeakerProfile
        {
            EventId = EventId, ParticipantId = speaker.Id,
            FirstName = "Michelle", LastName = "H", SessionizeSpeakerId = SessionizeId,
        });
        await db.SaveChangesAsync();

        var (import, _) = ScenarioFixture.NewImporter(db);
        var result = await import.ImportSpeakersAsync(
            EventId,
            new[] { new SessionizeSpeaker(RealEmail, "Michelle", "Hammarskjold", null,
                                          SessionizeId: SessionizeId) },
            Array.Empty<string>(),
            sendWelcome: false);

        Assert.Null(result.Error);
        Assert.Equal(RealEmail, (await db.Participants.SingleAsync()).Email);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("SAME PERSON"));
    }
}

/// <summary>Reaches the §204 placeholder-address rule without duplicating its format.</summary>
internal static class SessionizeImportServiceFixture
{
    public static string PlaceholderEmail(string sessionizeId) =>
        CommunityHub.Core.Reminders.SessionizeImportService.BuildPlaceholderEmail(sessionizeId);
}
