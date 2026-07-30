using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Participants;
using CommunityHub.Forms;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §173e: <see cref="WizardStepTaskSeeder"/> makes My-Tasks MIRROR the Get-Started journey —
/// for every step a role's wizard shows, a matching task is ensured (idempotent), reusing the
/// existing SourceKeys, and creating NEW tasks for the four steps that had none (Calendar,
/// Speaker details, Profile, Code of Conduct). Dedup keeps it to one task per step (speaker
/// logistics stay owned by the speakerdl deadline task; the party step by PartyTaskSeeder).
/// </summary>
public sealed class WizardStepTaskSeederTests
{
    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"wizstep-{Guid.NewGuid()}").Options);

    private static SignalGroupsProvider Signal() => new(new SignalGroupsConfig
    {
        BroadcastLabel = "B", BroadcastUrl = "https://signal.group/#b",
        Roles = new()
        {
            ["Speaker"] = new() { ChatLabel = "Speakers", ChatUrl = "https://signal.group/#spk" },
            ["Volunteer"] = new() { ChatLabel = "Vols", ChatUrl = "https://signal.group/#vol" },
        },
    });

    private static WizardStepTaskSeeder Seeder(CommunityHubDbContext db) =>
        new(db, new SpeakerWizardService(db, Signal()), new RoleWizardService(db, Signal()), TimeProvider.System);

    private static async Task<(int ev, int pid)> SeedAsync(
        CommunityHubDbContext db, ParticipantRole role, params OrderItem[] entitlements)
    {
        var ev = new Event { Code = "e", DisplayName = "E", CommunityName = "C", IsActive = true };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        var p = new Participant
        {
            EventId = ev.Id, FullName = "Person One", Email = "p@x.dk",
            Role = role, IsActive = true, LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        foreach (var item in entitlements)
            db.ParticipantOrderOverrides.Add(new ParticipantOrderOverride
            { EventId = ev.Id, ParticipantId = p.Id, Item = item, Include = true });
        await db.SaveChangesAsync();
        return (ev.Id, p.Id);
    }

    private static async Task<HashSet<string>> SourceKeysAsync(CommunityHubDbContext db, int ev, int pid) =>
        (await db.Tasks.Where(t => t.EventId == ev && t.AssignedParticipantId == pid)
            .Select(t => t.SourceKey!).ToListAsync()).ToHashSet();

    [Fact]
    public async Task Speaker_gets_step_tasks_but_never_calendar_promote_or_party()
    {
        using var db = NewDb();
        var (ev, pid) = await SeedAsync(db, ParticipantRole.Speaker);   // no logistics entitlements

        var created = await Seeder(db).EnsureForParticipantAsync(ev, pid, ParticipantRole.Speaker);
        var keys = await SourceKeysAsync(db, ev, pid);

        Assert.Contains(WizardStepTaskKeys.SpeakerDetails(pid), keys);
        Assert.Contains(WizardStepTaskKeys.Accept(pid), keys);
        Assert.Contains($"signal:{pid}", keys);
        // §313: the OPTIONAL Calendar-email step never has a pending task.
        Assert.DoesNotContain(WizardStepTaskKeys.Calendar(pid), keys);
        // §314: promote is a dated speaker deadline (SpeakerDeadlineSeeder), not a step task.
        Assert.DoesNotContain($"promote:{pid}", keys);
        // The party step is owned by PartyTaskSeeder — NOT created here.
        Assert.DoesNotContain(PartyTaskSeeder.SourceKeyFor(pid), keys);
        Assert.Equal(3, created);

        // Idempotent — a second run creates nothing.
        Assert.Equal(0, await Seeder(db).EnsureForParticipantAsync(ev, pid, ParticipantRole.Speaker));
    }

    [Fact]
    public async Task Leftover_open_calendar_and_promote_tasks_are_pruned()
    {
        // §313/§314: rows seeded before the two steps' tasks were retired must GO on the
        // next ensure (open rows only — Done rows keep their audit trail).
        using var db = NewDb();
        var (ev, pid) = await SeedAsync(db, ParticipantRole.Speaker);
        db.Tasks.AddRange(
            new ParticipantTask
            {
                EventId = ev, AssignedParticipantId = pid, Title = "Extra email (optional)",
                State = TaskState.Open, SourceKey = WizardStepTaskKeys.Calendar(pid),
            },
            new ParticipantTask
            {
                EventId = ev, AssignedParticipantId = pid, Title = "Help to promote your session(s)",
                State = TaskState.Open, SourceKey = $"promote:{pid}",
            });
        await db.SaveChangesAsync();

        await Seeder(db).EnsureForParticipantAsync(ev, pid, ParticipantRole.Speaker);

        var keys = await SourceKeysAsync(db, ev, pid);
        Assert.DoesNotContain(WizardStepTaskKeys.Calendar(pid), keys);
        Assert.DoesNotContain($"promote:{pid}", keys);
    }

    [Fact]
    public async Task Step_task_title_and_link_match_the_get_started_card()
    {
        using var db = NewDb();
        var (ev, pid) = await SeedAsync(db, ParticipantRole.Speaker);
        await Seeder(db).EnsureForParticipantAsync(ev, pid, ParticipantRole.Speaker);

        var details = await db.Tasks.FirstAsync(t => t.SourceKey == WizardStepTaskKeys.SpeakerDetails(pid));
        Assert.Equal("Speaker details", details.Title);                 // same human title as the card
        Assert.Null(details.DueDate);                                   // onboarding step, not a deadline
        // The My-Tasks deep-link resolves to the SAME surface the Get-Started card opens.
        // §352: that surface is now the INLINE wizard step, not the standalone page.
        Assert.Equal("/Forms/Wizard?step=details",
            ParticipantChecklistBuilder.LinkForSourceKey(details.SourceKey));
        Assert.Equal("/Forms/Wizard?step=calendar",
            ParticipantChecklistBuilder.LinkForSourceKey(WizardStepTaskKeys.Calendar(pid)));
    }

    [Fact]
    public async Task Completed_step_seeds_the_task_as_done()
    {
        using var db = NewDb();
        var (ev, pid) = await SeedAsync(db, ParticipantRole.Speaker);
        // A policy acceptance row ⇒ the Accept step is already done ⇒ the task seeds as Done.
        db.ParticipantPolicyAcceptances.Add(new ParticipantPolicyAcceptance
        { EventId = ev, ParticipantId = pid, AcceptedByEmail = "p@x.dk" });
        await db.SaveChangesAsync();

        await Seeder(db).EnsureForParticipantAsync(ev, pid, ParticipantRole.Speaker);

        var accept = await db.Tasks.FirstAsync(t => t.SourceKey == WizardStepTaskKeys.Accept(pid));
        Assert.Equal(TaskState.Done, accept.State);
        Assert.NotNull(accept.CompletedAt);
    }

    [Fact]
    public async Task Speaker_logistics_step_does_not_duplicate_an_existing_speakerdl_task()
    {
        using var db = NewDb();
        var (ev, pid) = await SeedAsync(db, ParticipantRole.Speaker, OrderItem.Hotel);
        // The speakerdl deadline task already owns the Hotel step.
        db.Tasks.Add(new ParticipantTask
        {
            EventId = ev, AssignedParticipantId = pid, Title = "Hotel",
            State = TaskState.Open, SourceKey = $"speakerdl:{pid}:hotel",
        });
        await db.SaveChangesAsync();

        await Seeder(db).EnsureForParticipantAsync(ev, pid, ParticipantRole.Speaker);

        var keys = await SourceKeysAsync(db, ev, pid);
        Assert.DoesNotContain($"hotel-form:{pid}", keys);   // no second Hotel task
        Assert.Contains($"speakerdl:{pid}:hotel", keys);
    }

    [Fact]
    public async Task Volunteer_gets_profile_and_accept_step_tasks()
    {
        using var db = NewDb();
        var (ev, pid) = await SeedAsync(db, ParticipantRole.Volunteer);

        await Seeder(db).EnsureForParticipantAsync(ev, pid, ParticipantRole.Volunteer);
        var keys = await SourceKeysAsync(db, ev, pid);

        Assert.Contains(WizardStepTaskKeys.Profile(pid), keys);
        Assert.Contains(WizardStepTaskKeys.Accept(pid), keys);
        Assert.Contains($"signal:{pid}", keys);
    }

    [Fact]
    public async Task Speaker_logistics_steps_are_never_seeded_even_before_the_speakerdl_rows_exist()
    {
        // §234 (7c): SpeakerDeadlineSeeder OWNS a speaker's logistics tasks. Seeding the
        // form-owned key (hotel-form:…) while the speakerdl rows didn't exist YET produced
        // a duplicate pair once SpeakerDeadlineSeeder ran — so the skip is unconditional.
        using var db = NewDb();
        var (ev, pid) = await SeedAsync(db, ParticipantRole.Speaker, OrderItem.Hotel);
        // NO speakerdl task exists yet — the old presence-based dedup would have seeded.

        await Seeder(db).EnsureForParticipantAsync(ev, pid, ParticipantRole.Speaker);

        var keys = await SourceKeysAsync(db, ev, pid);
        Assert.DoesNotContain($"hotel-form:{pid}", keys);
    }

    [Fact]
    public async Task Travel_task_is_not_resurrected_after_a_deliberate_opt_out()
    {
        // §234 (7b): a TravelReimbursement row with RequestReimbursement = false is a
        // COMPLETED decision ("I'm not claiming") — the seeder must not re-create the
        // travel task the participant already closed.
        using var db = NewDb();
        var (ev, pid) = await SeedAsync(db, ParticipantRole.Volunteer, OrderItem.TravelReimbursement);
        db.TravelReimbursements.Add(new TravelReimbursement
        { EventId = ev, ParticipantId = pid, RequestReimbursement = false });
        await db.SaveChangesAsync();

        await Seeder(db).EnsureForParticipantAsync(ev, pid, ParticipantRole.Volunteer);

        var keys = await SourceKeysAsync(db, ev, pid);
        Assert.DoesNotContain($"travel:submit-ticket-invoice:{pid}", keys);
    }

    [Fact]
    public async Task Travel_task_is_seeded_when_no_opt_out_exists()
    {
        using var db = NewDb();
        var (ev, pid) = await SeedAsync(db, ParticipantRole.Volunteer, OrderItem.TravelReimbursement);

        await Seeder(db).EnsureForParticipantAsync(ev, pid, ParticipantRole.Volunteer);

        var keys = await SourceKeysAsync(db, ev, pid);
        Assert.Contains($"travel:submit-ticket-invoice:{pid}", keys);
    }

    [Fact]
    public async Task Volunteer_availability_step_has_its_own_key_not_the_shifts_wizard_key()
    {
        // §234 (7a): the §148 per-day availability step gets availability:{pid}; the old
        // key (volunteer-form:{pid}) belongs to the SHIFTS wizard — reusing it cross-wired
        // two different live forms' tasks and completion signals.
        using var db = NewDb();
        var (ev, pid) = await SeedAsync(db, ParticipantRole.Volunteer);

        await Seeder(db).EnsureForParticipantAsync(ev, pid, ParticipantRole.Volunteer);

        var keys = await SourceKeysAsync(db, ev, pid);
        Assert.Contains(WizardStepTaskKeys.Availability(pid), keys);
        Assert.DoesNotContain($"volunteer-form:{pid}", keys);
    }

    [Fact]
    public async Task Sponsor_and_attendee_are_not_handled()
    {
        Assert.False(WizardStepTaskSeeder.Handles(ParticipantRole.Sponsor));
        Assert.False(WizardStepTaskSeeder.Handles(ParticipantRole.Attendee));
        Assert.True(WizardStepTaskSeeder.Handles(ParticipantRole.Speaker));
        Assert.True(WizardStepTaskSeeder.Handles(ParticipantRole.Volunteer));

        using var db = NewDb();
        var (ev, pid) = await SeedAsync(db, ParticipantRole.Attendee);
        Assert.Equal(0, await Seeder(db).EnsureForParticipantAsync(ev, pid, ParticipantRole.Attendee));
    }
}
