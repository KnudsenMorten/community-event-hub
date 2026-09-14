using CommunityHub.Core.Domain;
using CommunityHub.Core.Tasks.Definitions;
using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §708.11 — the GENERIC-ROLE task definitions (volunteer / organizer / media / event partner).
/// </summary>
/// <remarks>
/// These pin the two traps found while building the set, both of which fail SILENTLY in production
/// rather than loudly at build time — which is exactly why they are tests and not comments.
/// </remarks>
public class ParticipantTaskDefinitionTests
{
    private static IReadOnlyList<TaskDefinition> Participant => ParticipantTaskDefinitions.All;

    // ── TRAP 1: THE TITLE IS THE ONLY JOIN ──────────────────────────────────────────────────
    //
    // These rows do NOT key off their title (SourceKey is hotel-form:{pid}, profile:{pid}, …), so a
    // re-title cannot orphan them the way it would a speaker row. But TaskBodyService.DefinitionFor
    // matches a row to its definition BY TITLE SLUG. A title that differs from what
    // WizardStepTaskSeeder.MapStep writes by ONE character means the row never resolves a
    // definition, silently keeps its stored prose, and the authored body is never seen — on a green
    // build, with nothing on screen to indicate anything is wrong.

    [Theory]
    [InlineData("participant.profile", "Your profile")]
    [InlineData("participant.accept", "Code of Conduct & Privacy")]
    [InlineData("participant.signal", "Join Signal groups")]
    [InlineData("participant.availability", "Complete your day availability")]
    [InlineData("participant.hotel", "Complete the Hotel form")]
    [InlineData("participant.dinner", "Complete the Appreciation Dinner RSVP")]
    [InlineData("participant.lunch", "Complete the Lunch logistics form")]
    [InlineData("participant.swag", "Complete the Swag preferences form")]
    public void Title_is_VERBATIM_from_the_seeder_because_the_title_is_the_only_join(
        string key, string expectedTitle)
    {
        var definition = Participant.Single(d => d.Key == key);
        Assert.Equal(expectedTitle, definition.Title);
    }

    // ── TRAP 2: A DUPLICATE TITLE MAKES THE REGISTRY ANSWER FOR THE WRONG ROLE ──────────────
    //
    // DefinitionFor resolves by title slug across the WHOLE registry, so two definitions sharing a
    // title are two definitions answering to one row. The live instance: the generic travel task is
    // titled "Submit travel reimbursement" — identical to speaker.travel — so migrating it would let
    // a VOLUNTEER be shown the SPEAKER body. It is therefore deliberately NOT in this set.

    [Fact]
    public void Generic_travel_stays_OUT_because_its_title_collides_with_the_speaker_definition()
    {
        Assert.DoesNotContain(Participant, d => d.Key == "participant.travel");

        // And the collision it would have caused is real, not hypothetical — the speaker set owns
        // that exact title today. If the speaker task is ever renamed, this fails and the generic
        // one can be migrated.
        Assert.Contains(
            SpeakerTaskDefinitions.All, d => d.Title == "Submit travel reimbursement");
    }

    [Fact]
    public void No_two_SHIPPED_definitions_share_a_title_slug()
    {
        // The registry-wide invariant the travel exclusion protects. Any future duplicate title —
        // in ANY role's set — makes DefinitionFor's FirstOrDefault a coin toss between two bodies.
        var duplicates = TaskDefinitionRegistry.Shipped.All
            .GroupBy(d => SponsorTaskKeys.Slug(d.Title), StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(" | ", g.Select(d => d.Key))}")
            .ToList();

        Assert.Empty(duplicates);
    }

    // ── THE AUDIENCE MATRIX ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ParticipantRole.Volunteer)]
    [InlineData(ParticipantRole.Organizer)]
    [InlineData(ParticipantRole.Media)]
    [InlineData(ParticipantRole.EventPartner)]
    public void Every_generic_role_gets_profile_and_code_of_conduct(ParticipantRole role)
    {
        var mine = TaskDefinitionRegistry.Shipped
            .For(TaskAudienceFacts.For(role))
            .Select(d => d.Key)
            .ToList();

        Assert.Contains("participant.profile", mine);
        Assert.Contains("participant.accept", mine);
    }

    [Fact]
    public void Day_availability_is_VOLUNTEERS_ONLY()
    {
        // §234 (7a) — the per-day availability step exists only in the volunteer wizard. Handing it
        // to media or an event partner would chase them for a schedule they never fill in.
        foreach (var role in ParticipantTaskDefinitions.GenericRoles)
        {
            var mine = TaskDefinitionRegistry.Shipped
                .For(TaskAudienceFacts.For(role))
                .Select(d => d.Key);

            if (role == ParticipantRole.Volunteer)
                Assert.Contains("participant.availability", mine);
            else
                Assert.DoesNotContain("participant.availability", mine);
        }
    }

    [Fact]
    public void Logistics_tasks_need_the_matching_entitlement()
    {
        // Nobody entitled to anything sees no logistics task at all...
        var bare = TaskDefinitionRegistry.Shipped
            .For(TaskAudienceFacts.For(ParticipantRole.Volunteer))
            .Select(d => d.Key)
            .ToList();

        Assert.DoesNotContain("participant.hotel", bare);
        Assert.DoesNotContain("participant.dinner", bare);
        Assert.DoesNotContain("participant.lunch", bare);
        Assert.DoesNotContain("participant.swag", bare);

        // ...and the entitlement, and ONLY that entitlement, turns its own task on.
        var withHotel = TaskDefinitionRegistry.Shipped
            .For(TaskAudienceFacts.For(
                ParticipantRole.Volunteer, TaskAudiencePredicate.EntitledToHotel))
            .Select(d => d.Key)
            .ToList();

        Assert.Contains("participant.hotel", withHotel);
        Assert.DoesNotContain("participant.dinner", withHotel);
    }

    [Fact]
    public void Signal_follows_the_CONFIG_predicate_not_a_role_list()
    {
        // §109 membership lives in config and the operator changes it there. Freezing today's answer
        // into Roles would stop tracking it — the evergreen rule inverted.
        var outOfScope = TaskDefinitionRegistry.Shipped
            .For(TaskAudienceFacts.For(ParticipantRole.Volunteer))
            .Select(d => d.Key);
        Assert.DoesNotContain("participant.signal", outOfScope);

        var inScope = TaskDefinitionRegistry.Shipped
            .For(TaskAudienceFacts.For(
                ParticipantRole.Volunteer, TaskAudiencePredicate.IsSignalInScope))
            .Select(d => d.Key);
        Assert.Contains("participant.signal", inScope);
    }

    [Fact]
    public void A_sponsor_or_speaker_never_picks_up_a_generic_role_task()
    {
        // The generic set is additive: it must not widen what the two migrated roles already see.
        foreach (var role in new[] { ParticipantRole.Speaker, ParticipantRole.Sponsor })
        {
            var mine = TaskDefinitionRegistry.Shipped
                .For(TaskAudienceFacts.For(role))
                .Select(d => d.Key);

            Assert.DoesNotContain(mine, k => k.StartsWith("participant.", StringComparison.Ordinal));
        }
    }

    // ── §686.10 — THE COPY IS CARRIED VERBATIM ──────────────────────────────────────────────
    //
    // Operator 2026-07-30: "verifi textbfrom old text from tasks are in the new". These are the
    // EXACT description strings WizardStepTaskSeeder.MapStep wrote before §708.11, minus the
    // trailing "Open the … form" markdown button — which §708.2a says must not sit above a form
    // that is now embedded in the same row.
    //
    // 🔒 The point of pinning them is that the migration must not be a rewrite. A body edit is
    // allowed, but it has to be a DELIBERATE act that turns this test red, not something that
    // drifts in while the copy was being moved between files.
    public static TheoryData<string, string> CarriedCopy => new()
    {
        { "profile", "Check your name and add a phone number so we can reach you." },
        { "accept", "Review and accept our Code of Conduct and Privacy Policy." },
        { "signal", "Join the ELDK27 Signal group(s), then mark this done." },
        { "availability", "Tell us which days and times you can help so we can schedule you fairly." },
        { "hotel", "Tell us if you need a hotel room and pick your check-in/check-out dates." },
        { "dinner", "RSVP yes/no (+ plus-one count + allergies)." },
        { "lunch", "Tell us which lunches you'll join (Pre-day / Master Class -- plus the Packing/Setup days if your role helps before the event)." },
        { "swag", "Pick your polo size (or 'I wear my own clothes')." },
    };

    [PrivateContentTheory] // the migration-history pin is the upstream event's own wording
    [MemberData(nameof(CarriedCopy))]
    public void Body_carries_the_OLD_task_text_verbatim(string step, string originalDescription)
    {
        var path = Path.Combine(
            RepoRoot(), "config", "tasks", "eldk27", "participant", $"{step}.md");
        Assert.True(File.Exists(path), $"Body file missing: {path}");

        var body = File.ReadAllText(path);

        // The old prose, word for word.
        Assert.Contains(originalDescription, body, StringComparison.Ordinal);

        // And the form is embedded rather than linked to — the whole reason the button came out.
        Assert.Contains(":::embed stepForm", body, StringComparison.Ordinal);
        Assert.DoesNotContain("](/Forms/", body, StringComparison.Ordinal);
    }

    /// <summary>Walk up from the test binary to the repo root (where <c>config/</c> lives).</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "config")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    // ── THE BODY CONTRACT ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Every_definition_embeds_its_step_form_and_completes_from_the_data()
    {
        foreach (var definition in Participant)
        {
            Assert.Equal($"participant/{definition.Key.Split('.')[1]}", definition.BodyRef);

            // Signal is the ONE manual task: joining happens in Signal, outside the hub, so there is
            // nothing to observe. Every other generic task derives its state from the saved form.
            if (definition.Key == "participant.signal")
                Assert.IsType<TaskCompletion.Manual>(definition.Completion);
            else
                Assert.IsType<TaskCompletion.Form>(definition.Completion);
        }
    }
}
