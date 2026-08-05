using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §733.1 — the standalone PARTY reminder cadence is retired: a Get-Started step is chased by the
/// wizard's own 14-day digest, not a second time per task row.
/// </summary>
/// <remarks>
/// <para>Operator 2026-07-31, after being shown the data: <i>"go with (a). get started wizard gets
/// remindes every 14 days. each of the entries dont have due dates. tasks lives outside of this with
/// due dates"</i>.</para>
///
/// <para>🔴 <b>§717 is what the second chase cost.</b> Speakers and sponsors received *"task still
/// open: Sign up for the Party"* about something they experience as a Get-Started STEP, and replied
/// asking what it was. Two cadences were chasing one obligation: this builder every 14 days, and
/// <c>getstarted-digest</c> — also every 14 days, and it links to the step the person can actually
/// see.</para>
///
/// <para>🔑 <b>Why this is safe:</b> the digest *"stops as soon as that person's Get Started wizard
/// is 100% complete"*, and §732 made the OPTIONAL steps count as complete — so nobody is chased
/// forever for something they cannot finish. Those two facts together are what let the per-row chase
/// go. <c>AttendeePartyReminderTests</c> is SKIPPED rather than deleted, so re-enabling the builder
/// restores its coverage intact.</para>
/// </remarks>
public class PartyCadenceRetiredTests
{
    [Fact]
    public async Task The_party_builder_sends_nothing_even_with_an_open_dated_party_task()
    {
        using var db = Scenario.ScenarioFixture.NewDb();

        var ev = new CommunityHub.Core.Domain.Event
        {
            CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();

        var p = new CommunityHub.Core.Domain.Participant
        {
            EventId = ev.Id, Email = "crew@x.dk", FullName = "Crew Person",
            Role = CommunityHub.Core.Domain.ParticipantRole.Speaker, IsActive = true,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();

        // Exactly the shape that used to mail: an OPEN party task, created long enough ago that the
        // 2-week cadence would have fired many times over.
        db.Tasks.Add(new CommunityHub.Core.Domain.ParticipantTask
        {
            EventId = ev.Id,
            AssignedParticipantId = p.Id,
            Title = "Sign up for the Party",
            SourceKey = $"{CommunityHub.Core.Config.PartyTaskSeeder.PartyTaskKey}:{p.Id}",
            State = CommunityHub.Core.Domain.TaskState.Open,
            CreatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
        });
        await db.SaveChangesAsync();

        var templates = new EmailTemplateProvider(Microsoft.Extensions.Options.Options.Create(
            new EmailTemplateOptions
            {
                TemplateDirectory = Scenario.RepoPaths.EmailTemplates(),
                PrivateTemplateDirectory = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "ceh-no-private-party-733"),
            }));

        var builder = new AttendeePartyReminderBuilder(db, templates, TimeProvider.System);

        Assert.Empty(await builder.BuildDueAsync(ev.Id));
    }
}
