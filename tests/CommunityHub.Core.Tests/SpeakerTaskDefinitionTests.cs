using System.Text.Json;
using CommunityHub.Core.Config;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using CommunityHub.Core.Tasks.Definitions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §708 — the build-failing gate over the migrated SPEAKER task set.
/// </summary>
/// <remarks>
/// <para><c>TaskBodyCatalogTests</c> already sweeps every shipped definition for a body that exists,
/// parses, renders non-empty and names only resolvable placeholders. What it cannot check is what is
/// SPEAKER-specific and what the migration could break silently: the SourceKey identity, the date
/// source, and the audience gates.</para>
///
/// <para>🔒 Each of these three has a failure mode that a green build would otherwise hide —
/// §707.43, §707.54a and §708.3 were all exactly that.</para>
/// </remarks>
public class SpeakerTaskDefinitionTests
{
    private static IReadOnlyList<TaskDefinition> Speaker =>
        TaskDefinitionRegistry.Shipped.All
            .Where(d => d.Audience.Roles.Contains(ParticipantRole.Speaker))
            .ToList();

    private static SpeakerDeadlineConfig LoadConfig()
    {
        var path = ConfigPaths.Resolve("config/speaker-deadlines.eldk27.json");
        Assert.True(File.Exists(path), $"speaker-deadlines config not found at {path}.");
        return JsonSerializer.Deserialize<SpeakerDeadlineConfig>(
            File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("speaker-deadlines config did not deserialize.");
    }

    [Fact]
    public void All_eight_speaker_tasks_are_registered()
    {
        // §707.57b — eight, deliberately: Party and Master Class stay OUT ("they are ok as is").
        Assert.Equal(8, Speaker.Count);
    }

    /// <summary>
    /// 🔒 THE SOURCEKEY IDENTITY. A migrated definition's title must slug to exactly what the
    /// JSON-seeded row already carries, or the old row is pruned as an orphan, a fresh one is
    /// created, and the reminder ledger treats a months-old task as never chased (§684.21/§684.23).
    /// </summary>
    /// <remarks>
    /// These eight slugs are the ones OBSERVED ON PROD and recorded in <c>SpeakerDeadlineSeeder</c>'s
    /// §340-F note, so this is a check against reality rather than against the code restating itself.
    /// </remarks>
    [Fact]
    public void Every_speaker_title_slugs_to_the_key_prod_already_holds()
    {
        var expected = new[]
        {
            "hotel",
            "appreciation-dinner",
            "swag--speaker-gift",
            "preday-lunch",
            "submit-travel-reimbursement",
            "help-to-promote-your-sessions",
            "upload-preview-presentation",
            "upload-final-presentation",
        };

        var actual = Speaker.Select(d => SponsorTaskKeys.Slug(d.Title)).OrderBy(s => s).ToArray();

        Assert.Equal(expected.OrderBy(s => s).ToArray(), actual);
    }

    /// <summary>
    /// 🔒 THE DATE SOURCE. An unresolved key leaves the task with NO due date, which drops it out of
    /// the due-day chase entirely — a silent loss of the deadline, and the reason §708 forbade
    /// <c>TaskDue.Fixed</c> and keyed the lookup instead of matching on the title.
    /// </summary>
    [Fact]
    public void Every_speaker_definition_resolves_a_date_from_the_shipped_config()
    {
        var dates = LoadConfig().DueDatesByKey();

        foreach (var definition in Speaker)
        {
            var due = Assert.IsType<TaskDue.FromSpeakerConfig>(definition.Due);
            Assert.True(
                dates.ContainsKey(due.DeadlineKey),
                $"'{definition.Key}' names deadline key '{due.DeadlineKey}', which is not in "
                + "config/speaker-deadlines.eldk27.json. The task would seed with no due date and "
                + "never be chased.");
        }
    }

    /// <summary>
    /// 🔒 <b>Every migrated title must still have its row in the config</b> — the SourceKey is
    /// derived from the title, and the config's title is what the legacy path would seed. If the two
    /// drift, one speaker population ends up on each.
    /// </summary>
    [Fact]
    public void Every_speaker_definition_title_matches_its_config_entry()
    {
        var config = LoadConfig();

        foreach (var definition in Speaker)
        {
            var due = Assert.IsType<TaskDue.FromSpeakerConfig>(definition.Due);
            var entry = config.Deadlines.SingleOrDefault(d => d.Key == due.DeadlineKey);

            Assert.NotNull(entry);
            Assert.Equal(definition.Title, entry!.Title);
        }
    }

    /// <summary>
    /// 🔒 §684.16 — ONE source of truth. A migrated entry must not keep a description in config: the
    /// page would render the .md body while anything still reading the column served the old copy.
    /// </summary>
    [Fact]
    public void No_migrated_config_entry_still_carries_a_description()
    {
        var config = LoadConfig();
        var migratedKeys = Speaker
            .Select(d => ((TaskDue.FromSpeakerConfig)d.Due).DeadlineKey)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var entry in config.Deadlines.Where(e => e.Key is not null && migratedKeys.Contains(e.Key)))
        {
            Assert.True(
                string.IsNullOrWhiteSpace(entry.Description),
                $"Deadline '{entry.Key}' is migrated to the registry but still carries a "
                + "description in config — two sources of truth for one task's prose (§684.16).");
        }
    }

    // ── THE AUDIENCE MATRIX (§708.3) ────────────────────────────────────────────────────────
    //
    // The gate §708's plan undercounted. Expressed in the seeder as slug substring matching
    // ("contains final AND presentation"), which a rename breaks silently and permissively.

    private static TaskAudienceFacts SponsorCategorySpeaker(params TaskAudiencePredicate[] extra) =>
        TaskAudienceFacts.For(
            ParticipantRole.Speaker,
            extra.Append(TaskAudiencePredicate.IsSponsorCategorySpeaker).ToArray());

    [Fact]
    public void A_sponsor_category_speaker_owns_the_FINAL_deck_and_nothing_else_non_logistics()
    {
        // §456 (operator 2026-07-27): "only task relevant for a sponsor (exhibitor) speaker is the
        // task for upload final presentation". §458: they must NOT get Help Promote.
        var mine = TaskDefinitionRegistry.Shipped.For(SponsorCategorySpeaker())
            .Select(d => d.Key)
            .ToList();

        Assert.Contains("speaker.final-presentation", mine);
        Assert.DoesNotContain("speaker.preview-presentation", mine);
        Assert.DoesNotContain("speaker.promote", mine);
    }

    [Fact]
    public void An_ordinary_speaker_keeps_promote_and_both_decks()
    {
        var mine = TaskDefinitionRegistry.Shipped
            .For(TaskAudienceFacts.For(ParticipantRole.Speaker))
            .Select(d => d.Key)
            .ToList();

        Assert.Contains("speaker.promote", mine);
        Assert.Contains("speaker.preview-presentation", mine);
        Assert.Contains("speaker.final-presentation", mine);
    }

    [Fact]
    public void A_speaker_with_no_entitlements_gets_no_logistics_tasks()
    {
        // P12 — a sponsor-self-funded or organizer-funded speaker must not be handed a hotel or
        // swag task they cannot act on.
        var mine = TaskDefinitionRegistry.Shipped
            .For(TaskAudienceFacts.For(ParticipantRole.Speaker))
            .Select(d => d.Key)
            .ToList();

        foreach (var key in new[]
                 { "speaker.hotel", "speaker.dinner", "speaker.swag", "speaker.lunch", "speaker.travel" })
        {
            Assert.DoesNotContain(key, mine);
        }
    }

    [Fact]
    public void An_entitled_non_danish_speaker_gets_the_travel_task()
    {
        // §143 + §253 G10 — BOTH gates. Outside Denmark AND actually entitled to reimbursement.
        var entitledAbroad = TaskAudienceFacts.For(
            ParticipantRole.Speaker,
            TaskAudiencePredicate.NonDenmark,
            TaskAudiencePredicate.EntitledToTravelReimbursement);

        Assert.Contains(
            "speaker.travel",
            TaskDefinitionRegistry.Shipped.For(entitledAbroad).Select(d => d.Key));

        // Entitled but Danish ⇒ no travel task (a Danish speaker does not travel).
        var entitledDane = TaskAudienceFacts.For(
            ParticipantRole.Speaker, TaskAudiencePredicate.EntitledToTravelReimbursement);

        Assert.DoesNotContain(
            "speaker.travel",
            TaskDefinitionRegistry.Shipped.For(entitledDane).Select(d => d.Key));
    }

    // ── THE POINT OF THE MIGRATION (§707.57d) ───────────────────────────────────────────────

    [Theory]
    [InlineData("speakerdl:7:upload-preview-presentation", "preview")]
    [InlineData("speakerdl:7:upload-final-presentation", "final")]
    public void The_upload_tasks_are_artefact_backed_and_cannot_be_ticked_by_hand(
        string sourceKey, string expectedKind)
    {
        // 🔥 The photographed defect: "Upload final presentation ✓ done" beside /Speaker reading
        // "Final: not uploaded yet". It was possible only because the task was Manual and offered a
        // tick. Both buttons must be gone by construction.
        var task = new ParticipantTask { SourceKey = sourceKey, Title = "irrelevant" };

        Assert.Equal(expectedKind, TaskArtefactRules.UploadKindFor(task));
        Assert.True(TaskArtefactRules.IsArtefactBacked(task));
        Assert.False(TaskArtefactRules.AllowsManualCompletion(task));
    }

    [Fact]
    public void A_non_upload_speaker_task_still_allows_a_manual_tick()
    {
        // 🔒 The counter-case, and it matters: a false positive here would HIDE the completion
        // control from a task with no other way to finish. Travel is the one that must stay manual —
        // its own copy says "Not claiming? Just mark this task complete".
        var task = new ParticipantTask
        {
            SourceKey = "speakerdl:7:submit-travel-reimbursement",
            Title = "Submit travel reimbursement",
        };

        Assert.Null(TaskArtefactRules.UploadKindFor(task));
        Assert.True(TaskArtefactRules.AllowsManualCompletion(task));
    }

    [Fact]
    public void The_two_upload_definitions_declare_the_artefact_kinds_the_rules_return()
    {
        // The definition and the row-level rule are two different code paths reaching the same
        // conclusion; if they disagree, the row hides the tick for a task whose completion nothing
        // derives — permanently stuck. Pin them together.
        foreach (var (key, kind) in new[]
                 {
                     ("speaker.preview-presentation", "preview"),
                     ("speaker.final-presentation", "final"),
                 })
        {
            var definition = TaskDefinitionRegistry.Shipped.ByKey(key);
            Assert.NotNull(definition);

            var artefact = Assert.IsType<TaskCompletion.Artefact>(definition!.Completion);
            Assert.Equal(kind, artefact.Kind);
        }
    }

    [Fact]
    public void The_logistics_tasks_complete_from_their_form_not_a_tick()
    {
        foreach (var (key, step) in new[]
                 {
                     ("speaker.hotel", "hotel"),
                     ("speaker.dinner", "dinner"),
                     ("speaker.swag", "swag"),
                     ("speaker.lunch", "lunch"),
                 })
        {
            var definition = TaskDefinitionRegistry.Shipped.ByKey(key);
            Assert.NotNull(definition);

            var form = Assert.IsType<TaskCompletion.Form>(definition!.Completion);
            Assert.Equal(step, form.StepKey);

            // 🔒 §708.2a — and that step key must resolve to a canonical route, or the body's
            // button and the row's link would have nowhere to go.
            Assert.NotNull(CommunityHub.Core.Forms.FormRoutes.For(form.StepKey));
        }
    }

    [Fact]
    public void No_speaker_body_hardcodes_an_absolute_url()
    {
        // 🔒 §708.2a — "door 3 must go". The old descriptions carried
        // [Open the Hotel form](https://eldk27.eventhub.expertslive.dk/Forms/Hotel): absolute,
        // edition-specific, in config, unable to follow a route change, and breaking the evergreen
        // rule. Bodies name a placeholder or an app-relative route; never a host.
        var store = new TaskBodyStore();

        foreach (var definition in Speaker)
        {
            var raw = store.LoadRaw(definition.BodyRef);

            Assert.False(
                raw.Contains("http://", StringComparison.OrdinalIgnoreCase)
                || raw.Contains("https://", StringComparison.OrdinalIgnoreCase),
                $"'{definition.Key}' hardcodes an absolute URL. Use a canonical route "
                + "({{hotelFormUrl}} etc., from FormRoutes) or an app-relative path.");
        }
    }
}
