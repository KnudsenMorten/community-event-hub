using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Navigation;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Xunit;
using TR = CommunityHub.Core.Reminders.AttendeeTicketSyncService.TicketRow;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1255 — ONE HUMAN, SEVERAL ROLES: access follows the CAPABILITY, not <c>Participant.Role</c>.
///
/// <para>The single-valued <c>Role</c> enum decides who may open the speaker hub, while the
/// Sessionize import deliberately never overwrites an existing row's role. So an organizer,
/// volunteer or attendee who is ALSO a speaker gets a <see cref="SpeakerProfile"/>, linked
/// sessions and seeded deadlines — and is then refused the pages that hold them. Measured on
/// PROD 2026-09-20: four live rows in exactly that state (2 organizers, 1 volunteer,
/// 1 attendee).</para>
///
/// <para>These tests pin both halves of the fix: the access predicate + its nav consequence
/// (§1255), and the refusal to deactivate a login that is still needed for another reason
/// (§1255b).</para>
/// </summary>
public class MultiRoleCapabilityAccessTests
{
    private static async Task<int> SeedEventAsync(CommunityHubDbContext db)
    {
        var e = new Event
        {
            CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        };
        db.Events.Add(e);
        await db.SaveChangesAsync();
        return e.Id;
    }

    private static async Task<int> SeedParticipantAsync(
        CommunityHubDbContext db, int ev, ParticipantRole role, string email,
        string? sponsorCompanyId = null)
    {
        var p = new Participant
        {
            EventId = ev, Email = email.ToLowerInvariant(), FullName = "Multi Role",
            Role = role, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
            SponsorCompanyId = sponsorCompanyId,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    private static async Task SeedSpeakerProfileAsync(CommunityHubDbContext db, int ev, int pid)
    {
        db.SpeakerProfiles.Add(new SpeakerProfile { EventId = ev, ParticipantId = pid });
        await db.SaveChangesAsync();
    }

    // --- 1. The access predicate --------------------------------------------

    [Theory]
    [InlineData(ParticipantRole.Organizer)]
    [InlineData(ParticipantRole.Volunteer)]
    [InlineData(ParticipantRole.Attendee)]
    [InlineData(ParticipantRole.Sponsor)]
    public async Task A_speaker_profile_opens_the_speaker_hub_whatever_the_role_says(ParticipantRole role)
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        var pid = await SeedParticipantAsync(db, ev, role, "multi@x.dk");
        await SeedSpeakerProfileAsync(db, ev, pid);

        Assert.True(await ParticipantCapabilities.HasSpeakerHubAccessAsync(db, ev, pid, role));
    }

    [Fact]
    public async Task Without_a_profile_a_non_speaker_role_is_still_refused()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        var pid = await SeedParticipantAsync(db, ev, ParticipantRole.Volunteer, "helper@x.dk");

        Assert.False(await ParticipantCapabilities.HasSpeakerHubAccessAsync(
            db, ev, pid, ParticipantRole.Volunteer));
    }

    [Fact]
    public async Task Role_speaker_keeps_access_before_any_profile_exists()
    {
        // A speaker created by hand (or seen between the row and the import) must not be
        // locked out of her own hub just because the profile has not landed yet.
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        var pid = await SeedParticipantAsync(db, ev, ParticipantRole.Speaker, "new@x.dk");

        Assert.True(await ParticipantCapabilities.HasSpeakerHubAccessAsync(
            db, ev, pid, ParticipantRole.Speaker));
    }

    [Fact]
    public async Task A_profile_in_ANOTHER_edition_grants_nothing_here()
    {
        using var db = ScenarioFixture.NewDb();
        var thisEd = await SeedEventAsync(db);
        var otherEd = await SeedEventAsync(db);
        var pid = await SeedParticipantAsync(db, thisEd, ParticipantRole.Volunteer, "last-year@x.dk");
        await SeedSpeakerProfileAsync(db, otherEd, pid);

        Assert.False(await ParticipantCapabilities.HasSpeakerHubAccessAsync(
            db, thisEd, pid, ParticipantRole.Volunteer));
    }

    // --- 2. The nav consequence ---------------------------------------------

    [Fact]
    public void Nav_gives_a_capability_holder_a_door_into_the_speaker_hub()
    {
        var nav = NavBuilder.Build(ParticipantRole.Volunteer, hasSpeakerCapability: true);
        var hrefs = nav.Groups[0].Items.Select(i => i.Href).ToList();

        Assert.Contains("/Speaker", hrefs);
        Assert.Contains("/Speaker/Tasks", hrefs);
    }

    [Fact]
    public void Nav_is_unchanged_for_a_role_without_the_capability()
    {
        var withOut = NavBuilder.Build(ParticipantRole.Volunteer);
        Assert.DoesNotContain("/Speaker", withOut.Groups[0].Items.Select(i => i.Href));
    }

    [Fact]
    public void Nav_never_duplicates_the_speaker_menu_for_a_real_speaker()
    {
        // The flag is ignored for Role=Speaker — that menu is already complete, and a second
        // "/Speaker" item would appear twice in the bar.
        var nav = NavBuilder.Build(ParticipantRole.Speaker, hasSpeakerCapability: true);
        Assert.Equal(1, nav.Groups[0].Items.Count(i => i.Href == "/Speaker"));
        Assert.Equal(1, nav.Groups[0].Items.Count(i => i.Href == "/Speaker/Tasks"));
    }

    // --- 3. §1255b — the deactivation guard ---------------------------------

    private static async Task SeedTicketAsync(CommunityHubDbContext db, int ev, string id, string email)
    {
        db.Attendees.Add(new Attendee
        {
            EventId = ev, BackstageTicketId = id, Email = email.ToLowerInvariant(),
            FirstName = "Tick", LastName = "Holder", TicketStatus = TicketStatus.Other,
        });
        await db.SaveChangesAsync();
    }

    private static Task CancelAsync(CommunityHubDbContext db, int ev, string id, string email)
    {
        var sync = new AttendeeTicketSyncService(db, new MasterClassSignupService(db));
        return sync.SyncAsync(ev,
            new[] { new TR(id, "Tick", "Holder", email.ToLowerInvariant(), TicketStatus.Other, "1-day", CancelledUpstream: true) },
            Array.Empty<AttendeeTicketSyncService.OrderRow>());
    }

    [Fact]
    public async Task Cancelling_a_ticket_still_locks_out_a_plain_attendee()
    {
        // The control: §216 is unchanged for the ordinary case.
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        await SeedTicketAsync(db, ev, "T1", "plain@x.dk");
        var pid = await SeedParticipantAsync(db, ev, ParticipantRole.Attendee, "plain@x.dk");

        await CancelAsync(db, ev, "T1", "plain@x.dk");

        Assert.False(db.Participants.Find(pid)!.IsActive);
    }

    [Fact]
    public async Task Cancelling_a_ticket_does_NOT_lock_out_an_attendee_who_also_speaks()
    {
        // The live case (PROD #263, 2026-09-20): bought a ticket, then became a speaker.
        // Cancelling the ticket must take the ticket, not her speaker hub.
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        await SeedTicketAsync(db, ev, "T1", "speaks@x.dk");
        var pid = await SeedParticipantAsync(db, ev, ParticipantRole.Attendee, "speaks@x.dk");
        await SeedSpeakerProfileAsync(db, ev, pid);

        await CancelAsync(db, ev, "T1", "speaks@x.dk");

        Assert.True(db.Participants.Find(pid)!.IsActive,
            "a cancelled ticket must not take away a login that is also a speaker's");
    }

    [Fact]
    public async Task Cancelling_a_ticket_does_NOT_lock_out_a_sponsor_contact()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        await SeedTicketAsync(db, ev, "T1", "signer@x.dk");
        var pid = await SeedParticipantAsync(
            db, ev, ParticipantRole.Attendee, "signer@x.dk", sponsorCompanyId: "CM-42");

        await CancelAsync(db, ev, "T1", "signer@x.dk");

        Assert.True(db.Participants.Find(pid)!.IsActive);
    }

    [Fact]
    public async Task Cancelling_a_ticket_does_NOT_lock_out_a_volunteer_with_a_shift()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        await SeedTicketAsync(db, ev, "T1", "helper@x.dk");
        var pid = await SeedParticipantAsync(db, ev, ParticipantRole.Attendee, "helper@x.dk");
        var task = new VolunteerTask { EventId = ev, Title = "Door duty" };
        db.VolunteerTasks.Add(task);
        await db.SaveChangesAsync();
        db.VolunteerTaskAssignments.Add(new VolunteerTaskAssignment
        {
            EventId = ev, TaskId = task.Id, ParticipantId = pid,
        });
        await db.SaveChangesAsync();

        await CancelAsync(db, ev, "T1", "helper@x.dk");

        Assert.True(db.Participants.Find(pid)!.IsActive);
    }

    [Fact]
    public async Task Re_purchasing_still_restores_a_locked_out_login()
    {
        // The guard must only ever block the DEACTIVATE direction — restoring access
        // after a re-purchase is untouched.
        using var db = ScenarioFixture.NewDb();
        var ev = await SeedEventAsync(db);
        await SeedTicketAsync(db, ev, "T1", "back@x.dk");
        var pid = await SeedParticipantAsync(db, ev, ParticipantRole.Attendee, "back@x.dk");

        await CancelAsync(db, ev, "T1", "back@x.dk");
        Assert.False(db.Participants.Find(pid)!.IsActive);

        var sync = new AttendeeTicketSyncService(db, new MasterClassSignupService(db));
        await sync.SyncAsync(ev, new[]
        {
            new TR("T1", "Tick", "Holder", "back@x.dk", TicketStatus.Other, "1-day"),
        });

        Assert.True(db.Participants.Find(pid)!.IsActive);
    }
}
