using CommunityHub.Core.Content;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Forms;
using CommunityHub.Forms.Steps;
using CommunityHub.Pages.Forms;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §680 — the WELCOME step: step 1 of the Get-Started wizard for every role that has copy.
/// </summary>
/// <remarks>
/// <para>These prove the three things the feature actually stands on: it is FIRST in all four
/// wizard services, a first-run participant LANDS on it (without which it would be built and never
/// seen — it is marked Done, and the host's landing rule picks the first INCOMPLETE step), and the
/// rendered copy carries the participant's own name and event.</para>
///
/// <para>The store reads the REAL <c>config/welcome/eldk27/*.md</c> — a fixture would only agree
/// with itself, and the packaging of those files is half of what can go wrong (see the csproj).</para>
/// </remarks>
public sealed class WelcomeStepTests
{
    private static readonly WelcomeCopyStore Copy = new();

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"welcome-{Guid.NewGuid()}").Options);

    private static async Task<(CommunityHubDbContext db, int eventId, int pid)> SeedAsync(
        ParticipantRole role, string fullName = "Person One")
    {
        var db = NewDb();
        var ev = new Event
        {
            Code = "ELDK27", DisplayName = "Experts Live Denmark 2027",
            CommunityName = "C", IsActive = true,
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();

        var p = new Participant
        {
            EventId = ev.Id, FullName = fullName, Email = "p@x.dk",
            Role = role, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();

        return (db, ev.Id, p.Id);
    }

    // ----- the step is FIRST, in every wizard ------------------------------

    [Fact]
    public async Task Speaker_wizard_opens_with_the_welcome_step()
    {
        var (db, eventId, pid) = await SeedAsync(ParticipantRole.Speaker);
        var view = await new SpeakerWizardService(db, signal: null, welcome: Copy)
            .BuildAsync(eventId, pid);

        Assert.Equal(WelcomeCopyStore.StepKey, view.Steps[0].Key);
        // Done by construction: it asks nothing, so it must never hold the bar below 100%.
        Assert.True(view.Steps[0].Done);
    }

    [Theory]
    [InlineData(ParticipantRole.Volunteer)]
    [InlineData(ParticipantRole.Media)]
    [InlineData(ParticipantRole.EventPartner)]
    public async Task Role_wizard_opens_with_the_welcome_step(ParticipantRole role)
    {
        var (db, eventId, pid) = await SeedAsync(role);
        var view = await new RoleWizardService(db, signal: null, welcome: Copy)
            .BuildAsync(eventId, pid);

        Assert.Equal(WelcomeCopyStore.StepKey, view.Steps[0].Key);
        Assert.True(view.Steps[0].Done);
    }

    [Fact]
    public async Task Organizer_gets_no_welcome_step()
    {
        // Staff get no welcome mail and no welcome step — WelcomeCopyStore.SlugFor returns null,
        // so the wizard service never offers it. The rest of their wizard is untouched.
        var (db, eventId, pid) = await SeedAsync(ParticipantRole.Organizer);
        var view = await new RoleWizardService(db, signal: null, welcome: Copy)
            .BuildAsync(eventId, pid);

        Assert.DoesNotContain(view.Steps, s => s.Key == WelcomeCopyStore.StepKey);
        Assert.Equal("profile", view.Steps[0].Key);
    }

    [Fact]
    public async Task Attendee_wizard_opens_with_the_welcome_step()
    {
        var (db, eventId, pid) = await SeedAsync(ParticipantRole.Attendee);
        var view = await new AttendeeWizardService(db, welcome: Copy).BuildAsync(eventId, pid);

        Assert.Equal(WelcomeCopyStore.StepKey, view.Steps[0].Key);
        Assert.True(view.Steps[0].Done);
    }

    [Fact]
    public async Task Without_the_store_no_wizard_grows_a_welcome_step()
    {
        // The store is an OPTIONAL constructor argument, so every existing unit test that builds a
        // wizard service directly keeps its exact previous plan. This pins that back-compat.
        var (db, eventId, pid) = await SeedAsync(ParticipantRole.Volunteer);

        var role = await new RoleWizardService(db).BuildAsync(eventId, pid);
        var attendee = await new AttendeeWizardService(db).BuildAsync(eventId, pid);

        Assert.DoesNotContain(role.Steps, s => s.Key == WelcomeCopyStore.StepKey);
        Assert.DoesNotContain(attendee.Steps, s => s.Key == WelcomeCopyStore.StepKey);
    }

    // ----- the first-run landing rule --------------------------------------

    private static WizardModel.WizardPlan Plan(params WizardModel.PlanStep[] steps) =>
        new() { ResxPrefix = "RoleWiz", Steps = steps };

    private static WizardModel.PlanStep Step(string key, bool done) =>
        new(key, "/x", done);

    [Fact]
    public void First_run_lands_on_the_welcome_step()
    {
        // 🔒 The whole point. The welcome is Done: true and the host lands on the first INCOMPLETE
        // step — so WITHOUT this rule the step would be skipped past on the one visit it exists for.
        Assert.True(WizardModel.IsFirstRun(Plan(
            Step(WelcomeCopyStore.StepKey, true),
            Step("profile", false),
            Step("accept", false))));
    }

    [Fact]
    public void A_returning_participant_is_not_re_welcomed()
    {
        // One real step answered ⇒ resume where they left off. The welcome stays reachable from
        // the step rail; it just stops being where an unqualified /Forms/Wizard lands.
        Assert.False(WizardModel.IsFirstRun(Plan(
            Step(WelcomeCopyStore.StepKey, true),
            Step("profile", true),
            Step("accept", false))));
    }

    [Fact]
    public void The_other_informational_step_does_not_count_as_progress()
    {
        // §400's deadlines step is Done: true by construction too. Counting it as "they have
        // answered something" would suppress the welcome for everyone who has any dated task —
        // i.e. almost every speaker and sponsor, on their very first visit.
        Assert.True(WizardModel.IsFirstRun(Plan(
            Step(WelcomeCopyStore.StepKey, true),
            Step("profile", false),
            Step("deadlines", true))));
    }

    [Fact]
    public void A_plan_that_does_not_start_with_the_welcome_is_never_first_run()
    {
        Assert.False(WizardModel.IsFirstRun(Plan(Step("profile", false))));
        Assert.False(WizardModel.IsFirstRun(Plan()));
        Assert.False(WizardModel.IsFirstRun(null));
    }

    // ----- the rendered copy ----------------------------------------------

    [Fact]
    public async Task The_step_renders_the_role_copy_with_the_participants_own_name_and_event()
    {
        var (db, eventId, pid) = await SeedAsync(ParticipantRole.Speaker, "Hans Hansen");
        var model = await new WelcomeFormService(db, Copy)
            .LoadAsync(eventId, pid, ParticipantRole.Speaker, CancellationToken.None);

        Assert.Contains("Hans", model.Html, StringComparison.Ordinal);
        Assert.Contains("Experts Live Denmark 2027", model.Html, StringComparison.Ordinal);
        Assert.Contains(" (ELDK27)", model.Html, StringComparison.Ordinal);
        // Markdown was RENDERED, not dumped: the bullet list is real markup.
        Assert.Contains("<li>", model.Html, StringComparison.Ordinal);
        // No token survives to the page.
        Assert.DoesNotContain("{{", model.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_participant_with_no_name_is_greeted_generically_not_blankly()
    {
        // WelcomeEmailService's fallback verbatim — "Hi , welcome aboard" is worse than generic.
        var (db, eventId, pid) = await SeedAsync(ParticipantRole.Volunteer, fullName: " ");
        var model = await new WelcomeFormService(db, Copy)
            .LoadAsync(eventId, pid, ParticipantRole.Volunteer, CancellationToken.None);

        Assert.Contains("Hi there", model.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_role_with_no_copy_renders_nothing_rather_than_throwing()
    {
        var (db, eventId, pid) = await SeedAsync(ParticipantRole.Organizer);
        var model = await new WelcomeFormService(db, Copy)
            .LoadAsync(eventId, pid, ParticipantRole.Organizer, CancellationToken.None);

        Assert.Equal(string.Empty, model.Html);
    }
}
