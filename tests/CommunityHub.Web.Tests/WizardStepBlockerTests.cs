using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Forms;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §949 — A PENDING STEP MUST SAY *WHICH FIELD* WOULD COMPLETE IT.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-07, having reported it once already: <i>"bug: i still dont see that 'User
/// Profile' is completed in the get started wizard. i reported this earlier."</i></para>
///
/// <para>🔴 <b>The rule was never wrong.</b> Measured on PROD, his phone was blank, so §945's rule
/// (name + email + phone) was being applied exactly as specified and the step was correctly
/// incomplete. The defect was that the screen showed a filled-in name, a filled-in e-mail, and a chip
/// with no tick — and the phone field sat below the fold. <b>"9 of 10" is a scoreboard, not a
/// diagnosis</b>, and a correct answer with no reason attached costs as much time as a wrong one,
/// because the person still cannot act on it.</para>
///
/// <para>🔑 <b>What these tests pin, beyond "a message appears".</b> That the reason is derived from
/// the SAME shared rule that decides done-ness — a reason computed by a second, parallel test of the
/// fields is §945's two-copies defect wearing a new hat, and it would drift into telling somebody to
/// fill in a field that is already full.</para>
///
/// <para>FAKE names and addresses only.</para>
/// </remarks>
public sealed class WizardStepBlockerTests
{
    private const int EventId = 949;

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"blocker-{Guid.NewGuid():N}")
            .Options);

    private static async Task<Participant> SeedAsync(
        CommunityHubDbContext db, string? fullName, string? phone, string email = "p@example.test")
    {
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        });
        var p = new Participant
        {
            EventId = EventId, Email = email, FullName = fullName!, Phone = phone,
            Role = ParticipantRole.Volunteer, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p;
    }

    private static async Task<RoleWizardStep> ProfileStepAsync(CommunityHubDbContext db, int participantId)
    {
        var view = await new RoleWizardService(db).BuildAsync(EventId, participantId);
        return view.Steps.Single(s => s.Key == "profile");
    }

    /// <summary>
    /// 🔴 THE REPORTED CASE, exactly: name and e-mail present, phone blank. Before §949 this rendered
    /// as a chip with no tick and nothing else.
    /// </summary>
    [Fact]
    public async Task A_missing_phone_is_named_and_nothing_else_is()
    {
        using var db = NewDb();
        var p = await SeedAsync(db, "Robin Solberg", phone: null);

        var step = await ProfileStepAsync(db, p.Id);

        Assert.False(step.Done);
        Assert.Equal(new[] { "Phone" }, step.MissingFields);
    }

    [Fact]
    public async Task Whitespace_is_not_a_phone_number()
    {
        using var db = NewDb();
        // §945 trims deliberately — a single space satisfies != "" and is not a phone number. The
        // REASON has to agree with that, or the step says "add your phone" to somebody who can see
        // something in the box.
        var p = await SeedAsync(db, "Robin Solberg", phone: "   ");

        var step = await ProfileStepAsync(db, p.Id);

        Assert.False(step.Done);
        Assert.Contains("Phone", step.MissingFields!);
    }

    /// <summary>
    /// A pre-staged person (§941) can exist with an e-mail and nothing else, so more than one field
    /// can be outstanding — the step must name ALL of them, not just the first.
    /// </summary>
    [Fact]
    public async Task Several_missing_fields_are_all_named()
    {
        using var db = NewDb();
        var p = await SeedAsync(db, fullName: "", phone: null);

        var step = await ProfileStepAsync(db, p.Id);

        Assert.False(step.Done);
        Assert.Contains("FullName", step.MissingFields!);
        Assert.Contains("Phone", step.MissingFields!);
        Assert.DoesNotContain("Email", step.MissingFields!);   // the one field that IS filled in
    }

    /// <summary>
    /// 🔒 A COMPLETED step carries NO reason. A stale "add your phone number" under a ticked step is
    /// worse than silence: it contradicts the tick, and the reader cannot tell which is right.
    /// </summary>
    [Fact]
    public async Task A_completed_step_names_nothing()
    {
        using var db = NewDb();
        var p = await SeedAsync(db, "Robin Solberg", "+4512345678");

        var step = await ProfileStepAsync(db, p.Id);

        Assert.True(step.Done);
        Assert.True(step.MissingFields is null || step.MissingFields.Count == 0);
    }

    /// <summary>
    /// 🔴 THE GUARD THAT MATTERS. Done-ness and the reason must never disagree: a step is incomplete
    /// **if and only if** it names at least one outstanding field. Anything else sends somebody to a
    /// form to fix a field that is already filled in, or leaves them at a chip with no tick again —
    /// which is the whole bug.
    /// </summary>
    [Theory]
    [InlineData("Robin Solberg", "+4512345678")]
    [InlineData("Robin Solberg", null)]
    [InlineData("", "+4512345678")]
    [InlineData("", null)]
    [InlineData("   ", "   ")]
    public async Task Incomplete_always_means_at_least_one_named_field(string? fullName, string? phone)
    {
        using var db = NewDb();
        var p = await SeedAsync(db, fullName, phone);

        var step = await ProfileStepAsync(db, p.Id);

        var named = step.MissingFields?.Count ?? 0;
        Assert.Equal(step.Done, named == 0);
    }
}
