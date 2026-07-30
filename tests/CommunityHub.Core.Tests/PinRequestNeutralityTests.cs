using CommunityHub.Core.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Tests.Scenario;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §234 4c — the PIN-request endpoint must not be an account-enumeration oracle.
/// Historically only REAL emails accrued LoginPins and could hit the rate limit,
/// so a distinguishable "too many requests" response revealed that the address
/// was registered (6 rapid requests = existence probe). The contract now: the
/// observable response is the SAME neutral Ok for a known email, an unknown
/// email, and a known email past the send cap — while the cap itself still
/// holds (no PIN email goes out past 5 per hour). FAKE names only.
/// </summary>
public sealed class PinRequestNeutralityTests
{
    private static async Task<(CommunityHub.Core.Data.CommunityHubDbContext db, int ev)> SeedAsync()
    {
        var db = ScenarioFixture.NewDb();
        var e = new Event { CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true };
        db.Events.Add(e); await db.SaveChangesAsync();
        db.Participants.Add(new Participant
        {
            EventId = e.Id, Email = "known@example.test", FullName = "Known Person",
            Role = ParticipantRole.Speaker, IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
        });
        await db.SaveChangesAsync();
        return (db, e.Id);
    }

    private static PinLoginService NewService(
        CommunityHub.Core.Data.CommunityHubDbContext db, CapturingEmailSender sender) =>
        new(db, new PinService(), sender, ScenarioFixture.Clock);

    [Fact]
    public async Task Unknown_email_gets_the_neutral_ok_and_no_email()
    {
        var (db, ev) = await SeedAsync();
        using (db)
        {
            var sender = new CapturingEmailSender();
            var svc = NewService(db, sender);

            var result = await svc.RequestPinAsync(ev, "unknown@example.test");

            Assert.True(result.Accepted);
            Assert.Equal(PinRequestResult.Ok(), result);
            Assert.Empty(sender.Sent);
        }
    }

    [Fact]
    public async Task Known_email_past_the_cap_is_indistinguishable_from_unknown()
    {
        var (db, ev) = await SeedAsync();
        using (db)
        {
            var sender = new CapturingEmailSender();
            var svc = NewService(db, sender);

            // Hammer the endpoint. EVERY response must be the same neutral Ok an unknown address
            // gets — no rate-limit tell for real accounts. THIS is the security contract, and it
            // holds wherever the cap sits.
            var neutral = await svc.RequestPinAsync(ev, "unknown@example.test");
            for (var i = 0; i < 8; i++)
            {
                var result = await svc.RequestPinAsync(ev, "known@example.test");
                Assert.Equal(neutral, result);
            }

            // Operator decision 2026-07-27: the per-account cap moved 5 → 1000/hour, so eight
            // requests now all send. REALIGNED rather than deleted — this assertion had come to
            // pin the CAP as though that were the contract, and it never was. The contract is that
            // a known and an unknown address behave identically at any request rate, which the
            // loop above asserts and which is untouched. Exercising the new cap would take 1000
            // requests and prove nothing the loop does not.
            Assert.Equal(8, sender.Sent.Count);
            Assert.All(sender.Sent, m => Assert.Equal("known@example.test", m.To));
        }
    }

    // ------------------------------------------------------------------
    //  §361 — the ONE deliberate, operator-chosen exception to the contract above.
    // ------------------------------------------------------------------

    private static Attendee NewAttendee(int eventId, string email, TicketStatus ticket) =>
        new()
        {
            EventId = eventId, Email = email, FirstName = "Test", LastName = "Person",
            FullName = "Test Person", TicketStatus = ticket,
        };

    /// <summary>
    /// A 1-day attendee is mirrored from Zoho but never given an active Participant row, so the
    /// lookup misses and no PIN is EVER generated. Before §361 they got the neutral "a code has
    /// been sent" and waited forever — operator 2026-07-26: <i>"of course i never get the pin"</i>.
    /// They must now be told why, and must NOT be advanced to a PIN box (see LoginModel §361).
    /// </summary>
    [Fact]
    public async Task Non_two_day_attendee_is_told_the_hub_is_two_day_only()
    {
        var (db, ev) = await SeedAsync();
        using (db)
        {
            db.Attendees.Add(NewAttendee(ev, "oneday@example.test", TicketStatus.Other));
            await db.SaveChangesAsync();

            var sender = new CapturingEmailSender();
            var result = await NewService(db, sender).RequestPinAsync(ev, "oneday@example.test");

            Assert.False(result.Accepted);            // → the page stays on the email step
            Assert.Equal(PinRequestResult.NoHubAccessForTicket(), result);
            Assert.Contains("2-day ticket holders", result.Message);
            Assert.Empty(sender.Sent);                // still no PIN — nothing to wait for
        }
    }

    /// <summary>
    /// The §361 exception must stay NARROW. A stranger — no attendee row at all — still gets the
    /// neutral reply, so what leaks is the ticket class of an address the asker already typed,
    /// never whether an arbitrary address is registered.
    /// </summary>
    [Fact]
    public async Task A_stranger_with_no_attendee_row_still_gets_the_neutral_reply()
    {
        var (db, ev) = await SeedAsync();
        using (db)
        {
            var sender = new CapturingEmailSender();
            var result = await NewService(db, sender).RequestPinAsync(ev, "nobody@example.test");

            Assert.True(result.Accepted);
            Assert.Equal(PinRequestResult.Ok(), result);
            Assert.Empty(sender.Sent);
        }
    }

    /// <summary>
    /// TicketStatus.None means the Zoho sync found no matching order line — a DATA GAP, not a
    /// 1-day ticket. Claiming "you're not a 2-day holder" could be flatly wrong, so these fall
    /// back to the neutral reply and the operator handles them by hand.
    /// </summary>
    [Fact]
    public async Task Attendee_with_unresolved_ticket_gets_the_neutral_reply()
    {
        var (db, ev) = await SeedAsync();
        using (db)
        {
            db.Attendees.Add(NewAttendee(ev, "unresolved@example.test", TicketStatus.None));
            await db.SaveChangesAsync();

            var sender = new CapturingEmailSender();
            var result = await NewService(db, sender).RequestPinAsync(ev, "unresolved@example.test");

            Assert.True(result.Accepted);
            Assert.Equal(PinRequestResult.Ok(), result);
        }
    }

    /// <summary>
    /// A 2-DAY holder must never see the block message, even in the window before their
    /// Participant row is activated — for them the code genuinely is on its way once synced.
    /// </summary>
    [Fact]
    public async Task Two_day_attendee_never_sees_the_block_message()
    {
        var (db, ev) = await SeedAsync();
        using (db)
        {
            db.Attendees.Add(NewAttendee(ev, "twoday@example.test", TicketStatus.TwoDay));
            await db.SaveChangesAsync();

            var sender = new CapturingEmailSender();
            var result = await NewService(db, sender).RequestPinAsync(ev, "twoday@example.test");

            Assert.True(result.Accepted);
            Assert.Equal(PinRequestResult.Ok(), result);
        }
    }

    /// <summary>
    /// A 1-day attendee of ANOTHER edition is not this edition's business — the guard is scoped
    /// per event, like every other lookup in the service.
    /// </summary>
    [Fact]
    public async Task One_day_attendee_of_a_different_event_gets_the_neutral_reply()
    {
        var (db, ev) = await SeedAsync();
        using (db)
        {
            var other = new Event
            {
                CommunityName = "C", DisplayName = "C 2026", Code = "C26", IsActive = false,
            };
            db.Events.Add(other);
            await db.SaveChangesAsync();
            db.Attendees.Add(NewAttendee(other.Id, "lastyear@example.test", TicketStatus.Other));
            await db.SaveChangesAsync();

            var sender = new CapturingEmailSender();
            var result = await NewService(db, sender).RequestPinAsync(ev, "lastyear@example.test");

            Assert.True(result.Accepted);
            Assert.Equal(PinRequestResult.Ok(), result);
        }
    }
}
