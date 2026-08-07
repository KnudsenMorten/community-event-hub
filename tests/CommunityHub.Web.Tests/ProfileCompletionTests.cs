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
        Assert.Equal(expected, ProfileCompletion.IsCompleteFor(fullName, email, phone));
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
        Assert.Equal(viaQuery, ProfileCompletion.IsCompleteFor(p.FullName, p.Email, p.Phone));
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
        Assert.Equal(ProfileCompletion.IsCompleteFor(fullName, p.Email, phone), viaStepService);
    }

    // ---------------------------------------------------------------- phone is mandatory, all roles

    /// <summary>
    /// §945 supersedes §262. Saving with no phone is REFUSED for every role — not only volunteers.
    /// 🔴 The dead end this removes: completion always tested phone for everyone, so a non-volunteer
    /// who saved without one was told the save succeeded while the step stayed incomplete for ever.
    /// </summary>
    [Theory]
    [InlineData(ParticipantRole.Volunteer)]
    [InlineData(ParticipantRole.Speaker)]
    [InlineData(ParticipantRole.Organizer)]
    [InlineData(ParticipantRole.Sponsor)]
    public async Task Saving_without_a_phone_is_refused_for_every_role(ParticipantRole role)
    {
        using var db = NewDb();
        var p = await SeedAsync(db, role, phone: null);
        var ms = new ModelStateDictionary();

        var outcome = await new ProfileFormService(db, new FixedClock()).SaveAsync(
            new ProfileFormModel { FullName = "Test Person", Phone = null },
            EventId, p.Id, role, ms, default);

        Assert.Equal(WizardStepOutcome.Invalid, outcome);
        Assert.True(ms.ContainsKey(nameof(ProfileFormModel.Phone)));
        Assert.False(await db.Participants.Where(x => x.Id == p.Id).AnyAsync(ProfileCompletion.IsComplete));
    }

    /// <summary>Whitespace is refused the same way — otherwise a space would satisfy "mandatory".</summary>
    [Fact]
    public async Task Saving_a_whitespace_phone_is_refused()
    {
        using var db = NewDb();
        var p = await SeedAsync(db, ParticipantRole.Speaker, phone: null);
        var ms = new ModelStateDictionary();

        var outcome = await new ProfileFormService(db, new FixedClock()).SaveAsync(
            new ProfileFormModel { FullName = "Test Person", Phone = "   " },
            EventId, p.Id, ParticipantRole.Speaker, ms, default);

        Assert.Equal(WizardStepOutcome.Invalid, outcome);
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
