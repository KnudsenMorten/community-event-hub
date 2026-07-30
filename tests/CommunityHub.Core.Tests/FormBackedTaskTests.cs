using System.Text.Json;
using CommunityHub.Core.Config;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using CommunityHub.Core.Tasks.Definitions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §648 / §537 — the sponsor "Submit session description" task now OPENS A FORM instead of asking
/// for an e-mail.
/// </summary>
/// <remarks>
/// <para>Operator 2026-07-29, looking at the live task: <i>"in the new solution i need to have the
/// forms + ability to enter up to 3 names+linkedin+session description+upload picture. we have this
/// as part of get started at some point"</i>.</para>
///
/// <para><b>He was right that it already existed.</b> The Get Started step (§292/§356) already
/// captured title, abstract, track and three speakers with LinkedIn and a photo each. What was
/// missing was any way for a TASK to point at a form — so the task still read <i>"Email it all to
/// {{supportEmail}}"</i>, which is §537's original complaint: nothing validates, nothing lands in
/// CEH, and somebody retypes it all.</para>
/// </remarks>
public class FormBackedTaskTests
{
    private static ParticipantTask Task(string? formStepKey, string title = "Submit session description") =>
        new() { Id = 1, EventId = 1, Title = title, FormStepKey = formStepKey };

    // ---------- completion is DERIVED, never self-declared ----------

    /// <summary>
    /// 🔒 The same rule §603 applied to uploads: a task whose evidence is a submission must not
    /// offer a tick-box, or a sponsor can declare it done having filled in nothing.
    /// </summary>
    [Fact]
    public void A_form_backed_task_offers_NO_manual_complete()
    {
        Assert.False(TaskArtefactRules.AllowsManualCompletion(Task("session")));
    }

    [Fact]
    public void An_ordinary_task_KEEPS_its_manual_complete()
    {
        // A task with no form and no artefact has no other way to finish — removing the control
        // there would strand it forever.
        Assert.True(TaskArtefactRules.AllowsManualCompletion(Task(null, "Bring a prize for the app game")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_form_key_is_treated_as_NO_form(string key)
    {
        Assert.True(TaskArtefactRules.AllowsManualCompletion(Task(key, "Something manual")));
    }

    // ---------- the config actually wires it up ----------

    // ---------- the shipped catalogue actually wires it up ----------
    //
    // §686.2 — these three used to read config/sponsor.eldk27.json `taskSets`, which no longer
    // exists: the session task migrated to the code registry with its body in
    // config/tasks/eldk27/sponsor/session-description.md. They now read the REGISTRY, so §648's
    // guarantees follow the task instead of dying with the file it used to live in. Deleting them
    // would have been the easy read of a red test and would have quietly given up the guarantee.

    private static readonly TaskBodyStore Bodies = new();

    private static TaskDefinition SessionDefinition =>
        TaskDefinitionRegistry.Shipped.All.Single(
            d => d.Title.Contains("session description", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The end-to-end guarantee: the shipped catalogue must point the session task at the session
    /// form. Without this, everything above is dormant and the task still says "e-mail us".
    /// </summary>
    [Fact]
    public void The_shipped_session_task_points_at_the_session_form()
    {
        var completion = Assert.IsType<TaskCompletion.Form>(SessionDefinition.Completion);

        Assert.Equal("session", completion.StepKey);
    }

    /// <summary>
    /// 🔒 And the copy must no longer tell them to e-mail it. A form behind a description that still
    /// says "Email it all to …" is worse than either alone — the sponsor does the wrong one.
    /// </summary>
    [Fact]
    public void The_session_task_no_longer_asks_for_an_EMAIL()
    {
        var body = File.ReadAllText(Bodies.ResolvePath(SessionDefinition.BodyRef));

        Assert.DoesNotContain("Email it all", body, StringComparison.OrdinalIgnoreCase);

        // §688.16 — the body must POINT AT THE BUTTON BY ITS REAL NAME. This used to assert the
        // word "form", which passed while the body said "Open the session form" and the button
        // actually read "Review or update your answers" — the assertion held and the instruction
        // still pointed at a control that did not exist under that name. Pin the label instead.
        Assert.Contains("Update Session Details", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// §607 — the due date and its reminder cadence survive the conversion. That was his explicit
    /// constraint when this work started, and a task losing its deadline is a silent regression that
    /// only shows up when somebody misses it.
    /// </summary>
    /// <remarks>
    /// 🔒 §684.11 — the definition names a RULE, and the rule still lives in the edition config, so
    /// the DATES stay editable without a deploy even though the definitions no longer do. This
    /// asserts the two still meet: a definition naming a rule that does not exist would create the
    /// task with no due date at all, and therefore no reminder.
    /// </remarks>
    [Fact]
    public void The_session_task_KEEPS_its_deadline_rule()
    {
        var definition = SessionDefinition;
        var due = Assert.IsType<TaskDue.FromConfig>(definition.Due);

        Assert.Equal("session", due.RuleName);
        Assert.True(definition.IsMandatory);
        Assert.Equal(TaskReminderCadence.Standard, definition.Reminders);
        Assert.True(
            LoadSponsorConfig().DeadlineRules().ContainsKey(due.RuleName),
            "The task's deadline rule must exist in the edition config, or the task would be "
            + "created with no due date at all — and a task with no due date is never chased.");
    }

    private static SponsorConfig LoadSponsorConfig()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CommunityHub.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var path = Path.Combine(dir!.FullName, "config", "sponsor.eldk27.json");
        Assert.True(File.Exists(path), $"Expected the shipped sponsor config at {path}");

        var cfg = JsonSerializer.Deserialize<SponsorConfig>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(cfg);
        return cfg!;
    }
}
