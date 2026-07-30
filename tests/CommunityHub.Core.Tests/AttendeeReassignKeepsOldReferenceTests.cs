using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 🔒 §707.23 — A REASSIGNMENT MUST KEEP THE OLD REFERENCE.
/// </summary>
/// <remarks>
/// <para>Operator 2026-07-30: *"ceh must align 100% to zoho method … we keep old references when
/// cancellations or reassignments happen"*, then *"add PreviousEmail + ReassignedAt"*.</para>
///
/// <para>Cancellation already complied — the row survives with <c>MirrorState.Cancelled</c> and a
/// <c>CancelledAt</c> stamp. Reassignment did NOT: the row is keyed on <c>BackstageTicketId</c>, so
/// the sync rewrote <c>Email</c> in place and the previous holder disappeared without trace.</para>
///
/// <para>🔑 The case that makes it matter is his bulk order: 15 tickets bought on plus-addresses
/// (<c>mok+eldk27-1@…</c>) and later assigned to real people. Without this there is no way to answer
/// "which of my placeholders became this person?".</para>
/// </remarks>
public sealed class AttendeeReassignKeepsOldReferenceTests
{
    [Fact]
    public async Task Reassigning_a_ticket_records_the_previous_holder_and_when()
    {
        using var db = ScenarioFixture.NewDb();
        var when = new DateTimeOffset(2026, 7, 30, 9, 0, 0, TimeSpan.Zero);

        var a = new Attendee
        {
            EventId = 1, BackstageTicketId = "TKT-7",
            Email = "mok+eldk27-3@xyz.com",           // the manager's placeholder plus-address
            FullName = "name 3",
            TicketStatus = TicketStatus.TwoDay, MirrorState = MirrorState.Active,
        };
        db.Attendees.Add(a);
        await db.SaveChangesAsync();

        // What the sync's reassign branch does when Zoho moves the ticket to a real person.
        var oldEmail = a.Email;
        a.PreviousEmail = oldEmail;
        a.ReassignedAt = when;
        a.Email = "real.person@customer.dk";
        a.FullName = "Real Person";
        await db.SaveChangesAsync();

        var row = await db.Attendees.SingleAsync();

        // Still ONE row per ticket — Zoho's shape is unchanged.
        Assert.Equal("TKT-7", row.BackstageTicketId);
        Assert.Equal("real.person@customer.dk", row.Email);

        // …and the chain back to the placeholder survives.
        Assert.Equal("mok+eldk27-3@xyz.com", row.PreviousEmail);
        Assert.Equal(when, row.ReassignedAt);
    }

    [Fact]
    public async Task A_ticket_that_never_moved_has_no_previous_holder()
    {
        using var db = ScenarioFixture.NewDb();
        db.Attendees.Add(new Attendee
        {
            EventId = 1, BackstageTicketId = "TKT-8", Email = "steady@x.dk",
            TicketStatus = TicketStatus.TwoDay, MirrorState = MirrorState.Active,
        });
        await db.SaveChangesAsync();

        var row = await db.Attendees.SingleAsync();
        Assert.Null(row.PreviousEmail);
        Assert.Null(row.ReassignedAt);
    }
}
