using CommunityHub.Core.Domain;
using CommunityHub.Core.Tasks;
using CommunityHub.Core.Tests.Scenario;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1312 — operator 2026-09-27: "can we do that as well for mandatory tasks that is overdue for all roles … so they show up
/// with similar marking". The red "!" on "My tasks" / "Sponsor tasks" comes from a MANDATORY, unfinished task past its due
/// date — never an optional one, a done one, or one not yet due.
/// </summary>
public sealed class OverdueMandatoryTaskMarkTests
{
    private static readonly DateOnly Today = new(2026, 10, 1);

    private static async Task<(CommunityHub.Core.Data.CommunityHubDbContext Db, int EventId, int Pid)> SeedAsync()
    {
        var db = ScenarioFixture.NewDb();
        var ev = new Event
        {
            Code = "T27", CommunityName = "C", DisplayName = "C 2027", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        var p = new Participant
        {
            EventId = ev.Id, Email = "v@example.test", FullName = "Vol Unteer", Role = ParticipantRole.Volunteer,
            IsActive = true, LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return (db, ev.Id, p.Id);
    }

    [Fact]
    public async Task Only_an_overdue_unfinished_mandatory_task_marks_the_person()
    {
        var (db, ev, pid) = await SeedAsync();
        db.Tasks.AddRange(
            new ParticipantTask { EventId = ev, AssignedParticipantId = pid, Title = "optional, overdue", IsMandatory = false, DueDate = Today.AddDays(-3) },
            new ParticipantTask { EventId = ev, AssignedParticipantId = pid, Title = "done, overdue", State = TaskState.Done, DueDate = Today.AddDays(-3) },
            new ParticipantTask { EventId = ev, AssignedParticipantId = pid, Title = "due today", DueDate = Today },
            new ParticipantTask { EventId = ev, AssignedParticipantId = pid, Title = "no date" });
        await db.SaveChangesAsync();
        Assert.False(await OverdueTasks.AnyForPersonAsync(db, ev, pid, Today));

        db.Tasks.Add(new ParticipantTask { EventId = ev, AssignedParticipantId = pid, Title = "mandatory, overdue", State = TaskState.InProgress, DueDate = Today.AddDays(-1) });
        await db.SaveChangesAsync();
        Assert.True(await OverdueTasks.AnyForPersonAsync(db, ev, pid, Today));
    }

    [Fact]
    public async Task A_sponsor_company_is_marked_by_its_own_overdue_mandatory_task_only()
    {
        var (db, ev, _) = await SeedAsync();
        db.Tasks.Add(new ParticipantTask { EventId = ev, Title = "logo", SourceKey = "sponsor:logos:ACME", SponsorCompanyId = "ACME", DueDate = Today.AddDays(-2) });
        await db.SaveChangesAsync();

        Assert.True(await OverdueTasks.AnyForSponsorCompanyAsync(db, ev, "ACME", Today));
        Assert.False(await OverdueTasks.AnyForSponsorCompanyAsync(db, ev, "OTHER", Today));
    }
}
