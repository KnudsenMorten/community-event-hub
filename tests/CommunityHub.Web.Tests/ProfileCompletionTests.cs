using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Forms;
using CommunityHub.Forms;
using CommunityHub.Forms.Steps;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §945 — WHEN IS "Your Profile" DONE. Operator 2026-08-07: *"the state of Your Profile (step in get
/// organized) should be counted as completed, if full name, email, phone is set. Phone is mandatory to
/// fill out. if these fields are filled out, then it is completed"*.
///
/// <para>🔴 Two things were wrong. (1) The rule tested <b>phone alone</b>, so a pre-staged person with
/// an email and no name scored complete the moment a phone was typed. (2) The rule existed
/// <b>TWICE</b> — <c>RoleWizardService</c> and <c>ProfileFormService.IsDoneAsync</c> each carried their
/// own copy. They agreed by coincidence; two answers to "am I finished?" is the §939 defect, where a
/// volunteer who had completed everything was shown 90% and went looking for work that did not
/// exist.</para>
///
/// <para>🔴 And a dead end underneath both: completion has ALWAYS required a phone from every role,
/// while §262 validation demanded one only from volunteers. A speaker could save their profile with no
/// phone, be told it saved, and watch the step stay incomplete for ever with nothing explaining
/// why.</para>
/// </summary>
public sealed class ProfileCompletionTests
{
    private const int EventId = 945;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"profiledone-{Guid.NewGuid():N}")
            .Options);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-08-07T10:00:00Z");
    }

    private static async Task<Participant> SeedAsync(
        CommunityHubDbContext db, ParticipantRole role,
        string? fullName = "Test Person", string? phone = "+4512345678",
        string email = "p@example.test")
    {
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        });
        var p = new Participant
        {
            EventId = EventId, Email = email, FullName = fullName!, Role = role, Phone = phone,
            IsActive = true,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p;
    }

    // ---------------------------------------------------------------- the rule

    [Theory]
    // fullName,        email,              phone,          complete?
    [InlineData("Test Person", "p@example.test", "+4512345678", true)]
    [InlineData(null, "p@example.test", "+4512345678", false)]   // pre-staged: no name yet
    [InlineData("", "p@example.test", "+4512345678", false)]
    [InlineData("Test Person", "p@example.test", null, false)]   // the field people actually add
    [InlineData("Test Person", "p@example.test", "", false)]
    [InlineData("Test Person", "", "+4512345678", false)]
    // 🔒 Whitespace is not an answer. A single space satisfies `!= ""`, and is not a phone number.
    [InlineData("Test Person", "p@example.test", "   ", false)]
    [InlineData("   ", "p@example.test", "+4512345678", false)]
    public void The_rule_requires_name_email_and_phone(
        string? fullName, string? email, string? phone, bool expected)
    {
        Assert.Equal(expected, ProfileCompletion.IsCompleteFor(fullName, email, phone, ParticipantRole.Volunteer));
    }

    // ------------------------------------------------ §945a — phone is a VOLUNTEER rule

    /// <summary>
    /// 🔴 §945a (operator 2026-08-07, correcting §945): <i>"i enforced only for volunteers the phone
    /// otherwise disable so it is not mandatory and a shared form"</i>.
    /// </summary>
    /// <remarks>
    /// <para>📊 <b>Measured, not argued.</b> 51 real active people on PROD had no phone — 20 speakers,
    /// 24 sponsors, 3 organizers, 3 other, and <b>exactly ONE volunteer</b>. Under §945's
    /// every-role rule all 51 carried an open profile step and would have been chased by the
    /// get-started digest the day the e-mail rings opened. This rule makes that one person.</para>
    ///
    /// <para>⚠️ Name and e-mail stay required of everyone — the narrowing is only about phone.</para>
    /// </remarks>
    [Theory]
    [InlineData(ParticipantRole.Volunteer, false)]   // must give a phone — reachable on the day
    [InlineData(ParticipantRole.Speaker, true)]
    [InlineData(ParticipantRole.Sponsor, true)]
    [InlineData(ParticipantRole.Organizer, true)]
    [InlineData(ParticipantRole.Attendee, true)]
    public void Only_a_volunteer_needs_a_phone_to_be_complete(ParticipantRole role, bool completeWithoutPhone)
    {
        Assert.Equal(completeWithoutPhone,
            ProfileCompletion.IsCompleteFor("Robin Solberg", "p@example.test", null, role));

        // Everyone is complete WITH a phone, and nobody is complete without a name.
        Assert.True(ProfileCompletion.IsCompleteFor("Robin Solberg", "p@example.test", "+4512345678", role));
        Assert.False(ProfileCompletion.IsCompleteFor("", "p@example.test", "+4512345678", role));
    }

    /// <summary>
    /// 🔒 The SQL-translatable expression must narrow with the in-memory helper. Narrowing one alone
    /// is §945's original defect in mirror image — the progress bar and the form disagreeing about
    /// whether somebody is finished.
    /// </summary>
    [Theory]
    [InlineData(ParticipantRole.Volunteer, false)]
    [InlineData(ParticipantRole.Speaker, true)]
    [InlineData(ParticipantRole.Sponsor, true)]
    public async Task The_query_narrows_with_the_helper(ParticipantRole role, bool expected)
    {
        using var db = NewDb();
        var p = await SeedAsync(db, role, "Robin Solberg", phone: null);

        var viaQuery = await db.Participants
            .Where(x => x.Id == p.Id && x.EventId == EventId)
            .AnyAsync(ProfileCompletion.IsComplete);

        Assert.Equal(expected, viaQuery);
        Assert.Equal(viaQuery, ProfileCompletion.IsCompleteFor(p.FullName, p.Email, p.Phone, p.Role));
    }

    /// <summary>
    /// 🔒 The two spellings of the rule — the SQL-translatable expression and the in-memory helper —
    /// must agree. They exist together so a query and a validation cannot drift apart; this is what
    /// makes "together" mean something.
    /// </summary>
    /// <remarks>
    /// ⚠️ The no-name case is seeded as <c>""</c>, not <c>null</c>: <c>Participant.FullName</c> is
    /// non-nullable in the schema, so the state a pre-staged person is REALLY in is the empty string.
    /// A rule that only tested for null would pass a unit test and miss every real row.
    /// </remarks>
    [Theory]
    [InlineData("Test Person", "+4512345678", true)]
    [InlineData("", "+4512345678", false)]
    [InlineData("Test Person", null, false)]
    [InlineData("Test Person", "  ", false)]
    public async Task The_query_and_the_in_memory_rule_agree(string? fullName, string? phone, bool expected)
    {
        using var db = NewDb();
        var p = await SeedAsync(db, ParticipantRole.Volunteer, fullName, phone);

        var viaQuery = await db.Participants
            .Where(x => x.Id == p.Id && x.EventId == EventId)
            .AnyAsync(ProfileCompletion.IsComplete);

        Assert.Equal(expected, viaQuery);
        Assert.Equal(viaQuery, ProfileCompletion.IsCompleteFor(p.FullName, p.Email, p.Phone, p.Role));
    }

    // ---------------------------------------------------------------- one rule, not two

    /// <summary>
    /// 🔒 THE §939 GUARD. The Get Started progress (`RoleWizardService`) and the step's own view
    /// (`ProfileFormService.IsDoneAsync`) must give the SAME answer for the same person — including in
    /// the half-filled states, which is where two separate copies would first disagree.
    /// </summary>
    [Theory]
    [InlineData("Test Person", "+4512345678")]
    [InlineData("", "+4512345678")]
    [InlineData("Test Person", null)]
    [InlineData("", null)]
    public async Task The_wizard_and_the_step_service_never_disagree(string? fullName, string? phone)
    {
        using var db = NewDb();
        var p = await SeedAsync(db, ParticipantRole.Volunteer, fullName, phone);

        var viaStepService = await new ProfileFormService(db, new FixedClock())
            .IsDoneAsync(EventId, p.Id, default);
        var viaWizard = await db.Participants
            .Where(x => x.Id == p.Id && x.EventId == EventId)
            .AnyAsync(ProfileCompletion.IsComplete);

        Assert.Equal(viaWizard, viaStepService);
        Assert.Equal(ProfileCompletion.IsCompleteFor(fullName, p.Email, phone, p.Role), viaStepService);
    }

    // ------------------------------------------- §945a — phone is mandatory for VOLUNTEERS only

    /// <summary>
    /// §945a (operator 2026-08-07) narrows §945 back to §262's scope: <i>"i enforced only for
    /// volunteers the phone otherwise disable so it is not mandatory and a shared form"</i>.
    /// </summary>
    /// <remarks>
    /// 🔴 <b>This test used to assert the opposite</b> (<c>..._for_every_role</c>) and was rewritten
    /// rather than deleted, because the pair of facts below is the point: a volunteer is refused, and
    /// everyone else is <b>saved AND immediately complete</b>. The second half is what stops §945's
    /// dead end reappearing in mirror image — a save that succeeds while the step stays incomplete.
    /// </remarks>
    [Theory]
    [InlineData(ParticipantRole.Volunteer, true)]    // refused — must be reachable on the day (§262)
    [InlineData(ParticipantRole.Speaker, false)]
    [InlineData(ParticipantRole.Organizer, false)]
    [InlineData(ParticipantRole.Sponsor, false)]
    public async Task Saving_without_a_phone_is_refused_for_volunteers_only(
        ParticipantRole role, bool expectRefused)
    {
        using var db = NewDb();
        var p = await SeedAsync(db, role, phone: null);
        var ms = new ModelStateDictionary();

        var outcome = await new ProfileFormService(db, new FixedClock()).SaveAsync(
            new ProfileFormModel { FullName = "Test Person", Phone = null },
            EventId, p.Id, role, ms, default);

        if (expectRefused)
        {
            Assert.Equal(WizardStepOutcome.Invalid, outcome);
            Assert.True(ms.ContainsKey(nameof(ProfileFormModel.Phone)));
            Assert.False(await db.Participants.Where(x => x.Id == p.Id).AnyAsync(ProfileCompletion.IsComplete));
        }
        else
        {
            Assert.NotEqual(WizardStepOutcome.Invalid, outcome);
            Assert.False(ms.ContainsKey(nameof(ProfileFormModel.Phone)));
            // 🔑 Saved AND complete — the form and the progress bar agreeing is the whole guarantee.
            Assert.True(await db.Participants.Where(x => x.Id == p.Id).AnyAsync(ProfileCompletion.IsComplete));
        }
    }

    /// <summary>
    /// Whitespace is not a phone number — but only where a phone number is demanded. A volunteer is
    /// refused; a speaker typing a stray space is not blocked over a field that is optional for them.
    /// </summary>
    [Theory]
    [InlineData(ParticipantRole.Volunteer, true)]
    [InlineData(ParticipantRole.Speaker, false)]
    public async Task A_whitespace_phone_is_refused_only_where_a_phone_is_required(
        ParticipantRole role, bool expectRefused)
    {
        using var db = NewDb();
        var p = await SeedAsync(db, role, phone: null);
        var ms = new ModelStateDictionary();

        var outcome = await new ProfileFormService(db, new FixedClock()).SaveAsync(
            new ProfileFormModel { FullName = "Test Person", Phone = "   " },
            EventId, p.Id, role, ms, default);

        Assert.Equal(expectRefused, outcome == WizardStepOutcome.Invalid);
    }

    /// <summary>The whole point: fill all three in and the step is complete.</summary>
    [Fact]
    public async Task Filling_name_and_phone_completes_the_step()
    {
        using var db = NewDb();
        var p = await SeedAsync(db, ParticipantRole.Speaker, fullName: "", phone: null);
        var svc = new ProfileFormService(db, new FixedClock());

        Assert.False(await svc.IsDoneAsync(EventId, p.Id, default));

        var outcome = await svc.SaveAsync(
            new ProfileFormModel { FullName = "Test Person", Phone = "+4512345678" },
            EventId, p.Id, ParticipantRole.Speaker, new ModelStateDictionary(), default);

        Assert.Equal(WizardStepOutcome.Advance, outcome);
        Assert.True(await svc.IsDoneAsync(EventId, p.Id, default));
    }
}
