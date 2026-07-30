using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Xunit;
using TR = CommunityHub.Core.Reminders.AttendeeTicketSyncService.TicketRow;

namespace CommunityHub.Core.Tests;

/// <summary>
/// REQUIREMENTS §209 — the ticket lifecycle extends the sync so a REASSIGNMENT and a
/// CANCELLATION also reset the party signup (and, for a 2-day ticket, the Master Class):
/// <list type="bullet">
/// <item>2-day REASSIGNMENT: the confirmed MC seat is KEPT + transferred to the new holder
/// (held, never released — no double-count); the new holder is flagged to validate it; the
/// PREVIOUS holder's party reservation is cancelled (no longer counts) and the new holder
/// starts UNANSWERED.</item>
/// <item>2-day CANCELLATION: the MC seat is RELEASED (frees capacity / promotes the
/// waitlist) and the party signup is cleared; the row goes inactive (MirrorState Cancelled).</item>
/// <item>1-day CANCELLATION / REASSIGNMENT: the party is reset exactly the same way, but
/// there is no Master Class.</item>
/// </list>
/// Reconciled by the STABLE Backstage ticket id (not email — email changes on reassignment);
/// idempotent (a re-run double-applies nothing); fail-safe. EF in-memory.
/// </summary>
public class AttendeeTicketSyncLifecycleTests
{
    private static async Task<(int ev, int mc)> SeedAsync(
        CommunityHub.Core.Data.CommunityHubDbContext db, int cap)
    {
        var e = new Event
        {
            CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        };
        db.Events.Add(e); await db.SaveChangesAsync();
        var s = new Session { EventId = e.Id, Title = "MC", Type = SessionType.MasterClass, MasterClassCapacity = cap };
        db.Sessions.Add(s); await db.SaveChangesAsync();
        return (e.Id, s.Id);
    }

    private static async Task<int> SeedTicketAsync(
        CommunityHub.Core.Data.CommunityHubDbContext db, int ev, string ticketId, string email, TicketStatus status)
    {
        var a = new Attendee
        {
            EventId = ev, BackstageTicketId = ticketId, Email = email,
            FirstName = "Old", LastName = "Holder", TicketStatus = status,
        };
        db.Attendees.Add(a); await db.SaveChangesAsync(); return a.Id;
    }

    /// <summary>Seed an ATTENDING party RSVP for an email (the "they will come" state).</summary>
    private static async Task SeedPartyYesAsync(
        CommunityHub.Core.Data.CommunityHubDbContext db, int ev, string email, string name = "Party Person")
    {
        var now = DateTimeOffset.UtcNow;
        db.PartyRsvps.Add(new PartyRsvp
        {
            EventId = ev, Name = name, Email = email, Attending = true, CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();
    }

    private static PartyRsvp? Party(CommunityHub.Core.Data.CommunityHubDbContext db, int ev, string email) =>
        db.PartyRsvps.FirstOrDefault(r => r.EventId == ev && r.Email.ToLower() == email.ToLowerInvariant());

    // --- 2-day REASSIGNMENT --------------------------------------------------

    [Fact]
    public async Task TwoDay_reassignment_transfers_seat_holds_it_and_resets_party()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mc) = await SeedAsync(db, cap: 1);                  // capacity 1 → proves no double-count
        var aid = await SeedTicketAsync(db, ev, "T1", "old@x.dk", TicketStatus.TwoDay);
        var mcSvc = new MasterClassSignupService(db);
        await mcSvc.SignUpAsync(ev, aid, mc);                        // old holder confirmed
        var sigId = (await mcSvc.SignupIdAsync(ev, aid, mc))!.Value;
        await SeedPartyYesAsync(db, ev, "Old@X.dk");                // note casing → case-insensitive match

        var sync = new AttendeeTicketSyncService(db, mcSvc);
        var r = await sync.SyncAsync(ev, new[]
        {
            new TR("T1", "New", "Person", "new@x.dk", TicketStatus.TwoDay, "2-day"),
        });

        Assert.Equal(1, r.Reassigned);
        var att = db.Attendees.Find(aid)!;
        Assert.Equal("new@x.dk", att.Email);                        // same row, new identity
        Assert.Equal(MirrorState.Active, att.MirrorState);          // the (new) holder is active
        Assert.NotNull(att.MasterClassInviteSentAt);                // "validate inherited MC" flag raised

        // MC seat KEPT + transferred + still HELD (never released): exactly one signup, Confirmed,
        // same attendee id, and the seat is still occupied (no double-count).
        var sig = Assert.Single(db.MasterClassSignups);
        Assert.Equal(sigId, sig.Id);
        Assert.Equal(aid, sig.AttendeeId);
        Assert.Equal(MasterClassSignupStatus.Confirmed, sig.Status);
        var re = Assert.Single(r.Reassignments);
        Assert.Equal("MC", re.InheritedMcTitle);

        // PREVIOUS holder's party reservation cancelled (no longer counts) — row KEPT (not deleted).
        var oldParty = Party(db, ev, "old@x.dk")!;
        Assert.NotNull(oldParty);
        Assert.False(oldParty.Attending);
        // NEW holder starts UNANSWERED: no party row under the new email.
        Assert.Null(Party(db, ev, "new@x.dk"));

        // The transferred seat does NOT double-count: capacity 1 is full, so a fresh
        // attendee can only WAITLIST (proves the seat stayed held by the one row).
        var other = await SeedTicketAsync(db, ev, "T2", "other@x.dk", TicketStatus.TwoDay);
        var res = await mcSvc.SignUpAsync(ev, other, mc);
        Assert.True(res.Ok);
        Assert.Equal(MasterClassSignupStatus.Waitlisted, res.Signup!.Status);
    }

    // --- 2-day CANCELLATION --------------------------------------------------

    [Fact]
    public async Task TwoDay_cancellation_releases_seat_frees_capacity_and_clears_party()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mc) = await SeedAsync(db, cap: 1);
        var holder = await SeedTicketAsync(db, ev, "T1", "h@x.dk", TicketStatus.TwoDay);
        var mcSvc = new MasterClassSignupService(db);
        await mcSvc.SignUpAsync(ev, holder, mc);                    // confirmed (seat full)
        await SeedPartyYesAsync(db, ev, "h@x.dk");

        var sync = new AttendeeTicketSyncService(db, mcSvc);
        // §326as: Zoho marks T1 "not_attending" and KEEPS it in the feed ⇒ cancellation.
        var r = await sync.SyncAsync(ev,
            new[] { new TR("T1", "H", "H", "h@x.dk", TicketStatus.TwoDay, "2-day", CancelledUpstream: true) },
            System.Array.Empty<AttendeeTicketSyncService.OrderRow>());

        Assert.Equal(1, r.Cancelled);
        var h = db.Attendees.Find(holder)!;
        Assert.Equal(MirrorState.Cancelled, h.MirrorState);         // inactive (mirror Cancelled)
        Assert.NotNull(h.CancelledAt);
        Assert.Empty(db.MasterClassSignups.Where(s => s.AttendeeId == holder));   // seat RELEASED
        // Party signup cleared (no longer counts) — row kept.
        Assert.False(Party(db, ev, "h@x.dk")!.Attending);

        // Released seat FREES capacity: a fresh attendee can now CONFIRM the freed seat.
        var next = await SeedTicketAsync(db, ev, "T9", "n@x.dk", TicketStatus.TwoDay);
        var res = await mcSvc.SignUpAsync(ev, next, mc);
        Assert.True(res.Ok);
        Assert.Equal(MasterClassSignupStatus.Confirmed, res.Signup!.Status);
    }

    // --- 1-day CANCELLATION --------------------------------------------------

    [Fact]
    public async Task OneDay_cancellation_resets_party_and_goes_inactive_no_master_class()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, _) = await SeedAsync(db, cap: 5);
        var holder = await SeedTicketAsync(db, ev, "T1", "1day@x.dk", TicketStatus.Other);
        await SeedPartyYesAsync(db, ev, "1day@x.dk");
        var sync = new AttendeeTicketSyncService(db, new MasterClassSignupService(db));

        // §326as: cancellation arrives as Zoho's own "not_attending" status.
        var r = await sync.SyncAsync(ev,
            new[] { new TR("T1", "H", "H", "1day@x.dk", TicketStatus.Other, "1-day", CancelledUpstream: true) },
            System.Array.Empty<AttendeeTicketSyncService.OrderRow>());

        Assert.Equal(1, r.Cancelled);
        var h = db.Attendees.Find(holder)!;
        Assert.Equal(MirrorState.Cancelled, h.MirrorState);
        Assert.NotNull(h.CancelledAt);
        Assert.Empty(db.MasterClassSignups);                        // a 1-day holder never had an MC
        Assert.False(Party(db, ev, "1day@x.dk")!.Attending);        // party reset
    }

    // --- 1-day REASSIGNMENT --------------------------------------------------

    [Fact]
    public async Task OneDay_reassignment_cancels_old_party_and_new_holder_unanswered()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, _) = await SeedAsync(db, cap: 5);
        var aid = await SeedTicketAsync(db, ev, "T1", "old1@x.dk", TicketStatus.Other);
        await SeedPartyYesAsync(db, ev, "old1@x.dk");
        var sync = new AttendeeTicketSyncService(db, new MasterClassSignupService(db));

        var r = await sync.SyncAsync(ev, new[]
        {
            new TR("T1", "New", "One", "new1@x.dk", TicketStatus.Other, "1-day"),
        });

        Assert.Equal(1, r.Reassigned);
        var att = db.Attendees.Find(aid)!;
        Assert.Equal("new1@x.dk", att.Email);
        Assert.Equal(MirrorState.Active, att.MirrorState);
        Assert.Empty(db.MasterClassSignups);                        // no MC for a 1-day ticket
        Assert.False(Party(db, ev, "old1@x.dk")!.Attending);        // old party cancelled
        Assert.Null(Party(db, ev, "new1@x.dk"));                    // new holder unanswered
    }

    // --- idempotency ---------------------------------------------------------

    [Fact]
    public async Task Reassignment_then_cancellation_resync_is_idempotent()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mc) = await SeedAsync(db, cap: 5);
        var aid = await SeedTicketAsync(db, ev, "T1", "old@x.dk", TicketStatus.TwoDay);
        var mcSvc = new MasterClassSignupService(db);
        await mcSvc.SignUpAsync(ev, aid, mc);
        await SeedPartyYesAsync(db, ev, "old@x.dk");
        var sync = new AttendeeTicketSyncService(db, mcSvc);

        var pull = new[] { new TR("T1", "New", "Person", "new@x.dk", TicketStatus.TwoDay, "2-day") };
        await sync.SyncAsync(ev, pull);                             // reassign
        var r2 = await sync.SyncAsync(ev, pull);                    // SAME pull again → no-op

        Assert.Equal(0, r2.Reassigned);                            // not re-counted
        Assert.Equal(1, r2.Updated);                               // a plain update
        Assert.Single(db.MasterClassSignups);                      // seat unchanged
        Assert.Equal(MasterClassSignupStatus.Confirmed, db.MasterClassSignups.Single().Status);
        Assert.False(Party(db, ev, "old@x.dk")!.Attending);        // still cancelled, not re-touched

        // Now cancel (§326as: Zoho flags the SAME ticket not_attending and keeps sending
        // it), then re-run the identical pull — the second run is a no-op.
        var cancelledPull = new[] { pull[0] with { CancelledUpstream = true } };
        var c1 = await sync.SyncAsync(ev, cancelledPull, System.Array.Empty<AttendeeTicketSyncService.OrderRow>());
        var c2 = await sync.SyncAsync(ev, cancelledPull, System.Array.Empty<AttendeeTicketSyncService.OrderRow>());
        Assert.Equal(1, c1.Cancelled);
        Assert.Equal(0, c2.Cancelled);                             // already soft-cancelled → no-op
        Assert.Equal(MirrorState.Cancelled, db.Attendees.Find(aid)!.MirrorState);
    }

    // --- incremental (webhook) path parity -----------------------------------

    [Fact]
    public async Task SyncOrder_reassignment_resets_party_like_the_full_sync()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, mc) = await SeedAsync(db, cap: 5);
        var mcSvc = new MasterClassSignupService(db);
        var sync = new AttendeeTicketSyncService(db, mcSvc);

        await sync.SyncOrderAsync(ev, "O1", Ord("O1"), new[]
        {
            new TR("T1", "Old", "Holder", "old@x.dk", TicketStatus.TwoDay, "2-day", OrderId: "O1"),
        });
        var aid = db.Attendees.Single(a => a.BackstageTicketId == "T1").Id;
        await mcSvc.SignUpAsync(ev, aid, mc);
        await SeedPartyYesAsync(db, ev, "old@x.dk");

        var r = await sync.SyncOrderAsync(ev, "O1", Ord("O1"), new[]
        {
            new TR("T1", "New", "Person", "new@x.dk", TicketStatus.TwoDay, "2-day", OrderId: "O1"),
        });

        Assert.Equal(1, r.Reassigned);
        Assert.Single(db.MasterClassSignups);                      // MC transferred, held
        Assert.Equal(aid, db.MasterClassSignups.Single().AttendeeId);
        Assert.False(Party(db, ev, "old@x.dk")!.Attending);        // old party cancelled
        Assert.Null(Party(db, ev, "new@x.dk"));                    // new holder unanswered
    }

    [Fact]
    public async Task SyncOrder_cancellation_clears_party()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, _) = await SeedAsync(db, cap: 5);
        var sync = new AttendeeTicketSyncService(db, new MasterClassSignupService(db));

        await sync.SyncOrderAsync(ev, "O1", Ord("O1"), new[]
        {
            new TR("T1", "A", "A", "a@x.dk", TicketStatus.Other, "1-day", OrderId: "O1"),
        });
        await SeedPartyYesAsync(db, ev, "a@x.dk");

        var r = await sync.SyncOrderAsync(ev, "O1", order: null, ticketsForOrder: System.Array.Empty<TR>(), orderRemoved: true);

        Assert.True(r.OrderCancelled);
        Assert.Equal(1, r.Cancelled);
        Assert.Equal(MirrorState.Cancelled, db.Attendees.Single().MirrorState);
        Assert.False(Party(db, ev, "a@x.dk")!.Attending);
    }

    private static AttendeeTicketSyncService.OrderRow Ord(string id) =>
        new(id, BuyerName: "Buyer", BuyerEmail: "buyer@x.dk", CompanyName: null,
            Country: "Denmark", CountryCode: "DK", City: "CPH", Postcode: "1000",
            TaxId: "DK123", OrderStatus: "completed", SourceCreatedAt: null, RawJson: "{}");

    // --- §216 login lockout / restore ----------------------------------------

    /// <summary>Seed the login Participant the provisioning would create for an attendee email.</summary>
    private static async Task<int> SeedAttendeeParticipantAsync(
        CommunityHub.Core.Data.CommunityHubDbContext db, int ev, string email)
    {
        var p = new Participant
        {
            EventId = ev, Email = email.ToLowerInvariant(), FullName = "Login User",
            Role = ParticipantRole.Attendee, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        };
        db.Participants.Add(p); await db.SaveChangesAsync(); return p.Id;
    }

    [Fact]
    public async Task FullSync_cancellation_then_repurchase_locks_then_restores_login()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, _) = await SeedAsync(db, cap: 5);
        await SeedTicketAsync(db, ev, "T1", "h@x.dk", TicketStatus.Other);
        var pid = await SeedAttendeeParticipantAsync(db, ev, "h@x.dk");
        var sync = new AttendeeTicketSyncService(db, new MasterClassSignupService(db));

        // Cancel (§326as: Zoho flags the ticket not_attending) → login locked.
        await sync.SyncAsync(ev,
            new[] { new TR("T1", "H", "X", "h@x.dk", TicketStatus.Other, "1-day", CancelledUpstream: true) },
            System.Array.Empty<AttendeeTicketSyncService.OrderRow>());
        Assert.False(db.Participants.Find(pid)!.IsActive);

        // Re-purchase (same ticket id active) → login restored, SAME participant.
        await sync.SyncAsync(ev, new[] { new TR("T1", "H", "X", "h@x.dk", TicketStatus.Other, "1-day") });
        Assert.True(db.Participants.Find(pid)!.IsActive);
        Assert.Single(db.Participants);
    }

    [Fact]
    public async Task SyncOrder_cancellation_then_repurchase_locks_then_restores_login()
    {
        using var db = ScenarioFixture.NewDb();
        var (ev, _) = await SeedAsync(db, cap: 5);
        var sync = new AttendeeTicketSyncService(db, new MasterClassSignupService(db));

        await sync.SyncOrderAsync(ev, "O1", Ord("O1"), new[]
        {
            new TR("T1", "H", "X", "h@x.dk", TicketStatus.Other, "1-day", OrderId: "O1"),
        });
        var pid = await SeedAttendeeParticipantAsync(db, ev, "h@x.dk");

        // Whole-order cancellation → login locked (webhook path parity).
        await sync.SyncOrderAsync(ev, "O1", order: null, ticketsForOrder: System.Array.Empty<TR>(), orderRemoved: true);
        Assert.False(db.Participants.Find(pid)!.IsActive);

        // Order reappears active → login restored, SAME participant.
        await sync.SyncOrderAsync(ev, "O1", Ord("O1"), new[]
        {
            new TR("T1", "H", "X", "h@x.dk", TicketStatus.Other, "1-day", OrderId: "O1"),
        });
        Assert.True(db.Participants.Find(pid)!.IsActive);
        Assert.Single(db.Participants);
    }
}
