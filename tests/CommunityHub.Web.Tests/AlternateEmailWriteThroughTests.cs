using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Forms;
using CommunityHub.Forms.Steps;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §422 — the speaker Get Started "alternate email" step and My Hub Profile are two doors onto
/// ONE address, and each must show what the other saved.
///
/// <para><b>What went wrong.</b> The operator set an alternate address in the wizard and then
/// found his profile blank (2026-07-27: <i>"i just added an alternative email … but i dont see
/// it in in my hub profile"</i>). The wizard step wrote <c>SpeakerProfile.CalendarEmail</c>; the
/// profile page read <c>Participant.AlternateEmail</c>. Two columns, two screens, no overlap —
/// so the platform could hold two different answers to "what is your other email?" and show each
/// screen only its own.</para>
///
/// <para>These tests drive the real form services over an in-memory DB and assert the round-trip
/// in BOTH directions, because a one-way write-through fixes the reported symptom while leaving
/// the other screen free to disagree.</para>
/// </summary>
public sealed class AlternateEmailWriteThroughTests
{
    private const int EventId = 91;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"altmail-{Guid.NewGuid():N}")
            .Options);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-07-27T10:00:00Z");
    }

    private static async Task<Participant> SeedSpeakerAsync(CommunityHubDbContext db, string email)
    {
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        });
        var p = new Participant
        {
            EventId = EventId, Email = email, FullName = "Morten Knudsen",
            Role = ParticipantRole.Speaker, Phone = "+4512345678",
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p;
    }

    [Fact]
    public async Task Setting_it_in_the_speaker_wizard_makes_it_show_on_the_hub_profile()
    {
        // THE reported bug, end to end.
        using var db = NewDb();
        var p = await SeedSpeakerAsync(db, "mok@mortenknudsen.net");

        var calendar = new CalendarEmailFormService(db, new FixedClock());
        var outcome = await calendar.SaveAsync(
            new CalendarEmailFormModel { CalendarEmail = "test-speaker-alternativemail@expertslive.dk" },
            EventId, p.Id, p.Email, p.FullName, ParticipantRole.Speaker,
            new ModelStateDictionary(), default);
        Assert.Equal(WizardStepOutcome.Advance, outcome);

        var profile = new ProfileFormService(db, new FixedClock());
        var loaded = await profile.LoadAsync(EventId, p.Id, ParticipantRole.Speaker, default);

        Assert.Equal("test-speaker-alternativemail@expertslive.dk", loaded!.AlternateEmail);
    }

    [Fact]
    public async Task Setting_it_on_the_hub_profile_makes_it_show_in_the_speaker_wizard()
    {
        // The other direction. Without this the two screens can still disagree — just the
        // other way round, which is the same bug wearing a different hat.
        using var db = NewDb();
        var p = await SeedSpeakerAsync(db, "mok@mortenknudsen.net");

        var profile = new ProfileFormService(db, new FixedClock());
        var outcome = await profile.SaveAsync(
            new ProfileFormModel
            {
                FullName = "Morten Knudsen", Phone = "+4512345678",
                AlternateEmail = "test-speaker-alternativemail@expertslive.dk",
                IsSpeaker = true,
            },
            EventId, p.Id, ParticipantRole.Speaker, new ModelStateDictionary(), default);
        Assert.Equal(WizardStepOutcome.Advance, outcome);

        var calendar = new CalendarEmailFormService(db, new FixedClock());
        var loaded = await calendar.LoadAsync(
            EventId, p.Id, ParticipantRole.Speaker, p.Email, default);

        Assert.Equal("test-speaker-alternativemail@expertslive.dk", loaded.CalendarEmail);
    }

    [Fact]
    public async Task The_wizard_step_stores_it_where_calendar_invites_and_sign_in_both_read_it()
    {
        // Both columns, explicitly: CalendarEmail steers the invite To-address, AlternateEmail
        // is the sign-in identity and the reminder CC. One typed address, both consumers fed.
        using var db = NewDb();
        var p = await SeedSpeakerAsync(db, "mok@mortenknudsen.net");

        await new CalendarEmailFormService(db, new FixedClock()).SaveAsync(
            new CalendarEmailFormModel { CalendarEmail = "Alt@ExpertsLive.dk" },
            EventId, p.Id, p.Email, p.FullName, ParticipantRole.Speaker,
            new ModelStateDictionary(), default);

        var sp = await db.SpeakerProfiles.SingleAsync(x => x.ParticipantId == p.Id);
        var me = await db.Participants.SingleAsync(x => x.Id == p.Id);

        // Normalised on the way in, so a login lookup can actually match what was stored.
        Assert.Equal("alt@expertslive.dk", sp.CalendarEmail);
        Assert.Equal("alt@expertslive.dk", me.AlternateEmail);
    }

    [Fact]
    public async Task The_wizard_step_refuses_an_address_that_already_reaches_someone_else()
    {
        // The step now grants a sign-in identity, so it must apply the profile's clash check.
        // Without it, typing a colleague's address into an innocuous-looking "extra email" box
        // would hand you a way into their account.
        using var db = NewDb();
        var me = await SeedSpeakerAsync(db, "mok@mortenknudsen.net");
        db.Participants.Add(new Participant
        {
            EventId = EventId, Email = "kent@expertslive.dk", FullName = "Kent Agerlund",
            Role = ParticipantRole.Speaker,
        });
        await db.SaveChangesAsync();

        var modelState = new ModelStateDictionary();
        var outcome = await new CalendarEmailFormService(db, new FixedClock()).SaveAsync(
            new CalendarEmailFormModel { CalendarEmail = "kent@expertslive.dk" },
            EventId, me.Id, me.Email, me.FullName, ParticipantRole.Speaker, modelState, default);

        Assert.Equal(WizardStepOutcome.Invalid, outcome);
        Assert.False(modelState.IsValid);
        Assert.Null((await db.Participants.SingleAsync(x => x.Id == me.Id)).AlternateEmail);
    }

    [Fact]
    public async Task Clearing_it_on_either_screen_clears_it_on_both()
    {
        // Removing your alternate address has to actually remove it — including from the column
        // the OTHER screen reads, or it reappears the next time you open that screen.
        using var db = NewDb();
        var p = await SeedSpeakerAsync(db, "mok@mortenknudsen.net");
        var clock = new FixedClock();

        await new CalendarEmailFormService(db, clock).SaveAsync(
            new CalendarEmailFormModel { CalendarEmail = "alt@expertslive.dk" },
            EventId, p.Id, p.Email, p.FullName, ParticipantRole.Speaker,
            new ModelStateDictionary(), default);

        await new ProfileFormService(db, clock).SaveAsync(
            new ProfileFormModel
            {
                FullName = "Morten Knudsen", Phone = "+4512345678",
                AlternateEmail = null, IsSpeaker = true,
            },
            EventId, p.Id, ParticipantRole.Speaker, new ModelStateDictionary(), default);

        var sp = await db.SpeakerProfiles.SingleAsync(x => x.ParticipantId == p.Id);
        var me = await db.Participants.SingleAsync(x => x.Id == p.Id);
        Assert.Null(me.AlternateEmail);
        Assert.Null(sp.CalendarEmail);
    }

    [Fact]
    public async Task Leaving_the_wizard_step_blank_still_completes_it()
    {
        // §148: this is an OPTIONAL step that counts as done once saved, blank or not. The
        // §422 write-through must not have turned "no thanks" into an unfinishable step.
        using var db = NewDb();
        var p = await SeedSpeakerAsync(db, "mok@mortenknudsen.net");
        var svc = new CalendarEmailFormService(db, new FixedClock());

        var outcome = await svc.SaveAsync(
            new CalendarEmailFormModel { CalendarEmail = null },
            EventId, p.Id, p.Email, p.FullName, ParticipantRole.Speaker,
            new ModelStateDictionary(), default);

        Assert.Equal(WizardStepOutcome.Advance, outcome);
        Assert.True(await svc.IsDoneAsync(EventId, p.Id, default));
    }
}
