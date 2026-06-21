using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// Syncs attendees from a Backstage ticket pull keyed on the STABLE ticket id
/// (REQUIREMENTS §6, operator-critical). Because a company can reassign a ticket
/// (same id, new name/email), keying on the ticket id — not email — means a
/// reassignment <b>updates the same attendee row</b>, so their Master Class
/// selection (linked to the AttendeeId) <b>transfers to the new holder</b> instead
/// of orphaning. Detects: NEW tickets, plain UPDATES, REASSIGNMENTS (email changed
/// for the same ticket id → the new holder must be emailed to validate their
/// inherited MC), and CANCELLATIONS (ticket gone from the pull → their MC seat is
/// released so the waitlist promotes). Returns the events the caller emails.
/// </summary>
public sealed class AttendeeTicketSyncService
{
    private readonly CommunityHubDbContext _db;
    private readonly MasterClassSignupService _mc;

    public AttendeeTicketSyncService(CommunityHubDbContext db, MasterClassSignupService mc)
    {
        _db = db;
        _mc = mc;
    }

    public sealed record TicketRow(
        string TicketId, string FirstName, string LastName, string Email,
        TicketStatus Status, string? TicketClassName,
        string? OrderId = null, string? CompanyName = null, string? JobTitle = null,
        string? Phone = null, string? Country = null, string? CountryCode = null,
        string? City = null, string? Postcode = null, string? TaxId = null,
        string? CustomFieldsJson = null);

    /// <summary>Map an enriched Backstage attendee to a sync row (Master Class eligibility
    /// detected from the ticket name via the shared <see cref="MasterClassTicketPolicy"/>).</summary>
    public static TicketRow FromBackstage(CommunityHub.Core.Integrations.BackstageAttendee a)
    {
        var isTwoDay = MasterClassTicketPolicy.IncludesMasterClass(a.TicketClassName);
        var status = !a.Attending ? TicketStatus.None
            : isTwoDay ? TicketStatus.TwoDay : TicketStatus.Other;
        return new TicketRow(a.TicketId, a.FirstName, a.LastName, a.Email, status, a.TicketClassName,
            a.OrderId, a.CompanyName, a.JobTitle, a.Phone, a.Country, a.CountryCode, a.City, a.Postcode,
            a.TaxId, a.CustomFieldsJson);
    }

    private static void Apply(Attendee a, TicketRow t)
    {
        a.FirstName = t.FirstName; a.LastName = t.LastName;
        a.FullName = $"{t.FirstName} {t.LastName}".Trim();
        a.TicketStatus = t.Status; a.TicketClassName = t.TicketClassName;
        a.OrderId = t.OrderId; a.CompanyName = t.CompanyName; a.JobTitle = t.JobTitle;
        a.Phone = t.Phone; a.Country = t.Country; a.CountryCode = t.CountryCode;
        a.City = t.City; a.Postcode = t.Postcode; a.TaxId = t.TaxId;
        a.CustomFieldsJson = t.CustomFieldsJson;
    }

    /// <summary>A ticket reassigned to a new person who inherited a Master Class — email them.</summary>
    public sealed record Reassignment(int AttendeeId, string NewEmail, string NewName, string? InheritedMcTitle);

    public sealed record SyncResult(
        int Created, int Updated, int Reassigned, int Cancelled,
        IReadOnlyList<Reassignment> Reassignments,
        IReadOnlyList<MasterClassSignupService.PromotionResult> FreedPromotions);

    public async Task<SyncResult> SyncAsync(
        int eventId, IReadOnlyList<TicketRow> tickets, CancellationToken ct = default)
    {
        var existing = await _db.Attendees
            .Where(a => a.EventId == eventId && a.BackstageTicketId != null)
            .ToDictionaryAsync(a => a.BackstageTicketId!, a => a, ct);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        int created = 0, updated = 0, reassigned = 0, cancelled = 0;
        var reassignments = new List<Reassignment>();
        var freed = new List<MasterClassSignupService.PromotionResult>();
        var now = DateTimeOffset.UtcNow;

        foreach (var t in tickets)
        {
            if (string.IsNullOrWhiteSpace(t.TicketId)) continue;     // can't key without an id
            seen.Add(t.TicketId);
            var email = (t.Email ?? "").Trim().ToLowerInvariant();

            if (existing.TryGetValue(t.TicketId, out var a))
            {
                var isReassign = !string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase)
                                 && !string.IsNullOrWhiteSpace(email);
                Apply(a, t);
                a.LastSyncedAt = now;
                if (isReassign)
                {
                    a.Email = email;
                    reassigned++;
                    // The inherited Master Class is KEPT (never cancelled on reassign) —
                    // the MasterClassSignup stays linked to this same attendee row, so it
                    // transfers to the new holder automatically.
                    var mcTitle = await _db.MasterClassSignups.AsNoTracking()
                        .Where(s => s.EventId == eventId && s.AttendeeId == a.Id
                                    && s.Status == MasterClassSignupStatus.Confirmed)
                        .Select(s => s.Session.Title).FirstOrDefaultAsync(ct);
                    // If they inherited an MC, don't re-prompt selection (the
                    // reassignment-VALIDATION email covers them); only re-open the
                    // selection invite when there's nothing inherited to validate.
                    a.MasterClassInviteSentAt = string.IsNullOrEmpty(mcTitle) ? null : now;
                    reassignments.Add(new Reassignment(a.Id, email, $"{t.FirstName} {t.LastName}".Trim(), mcTitle));
                }
                else updated++;
            }
            else
            {
                var na = new Attendee
                {
                    EventId = eventId, BackstageTicketId = t.TicketId, Email = email,
                    CreatedAt = now, LastSyncedAt = now,
                };
                Apply(na, t);
                _db.Attendees.Add(na);
                created++;
            }
        }
        await _db.SaveChangesAsync(ct);

        // CANCELLED: a ticket we held is no longer in the pull → release its MC seats.
        foreach (var (ticketId, a) in existing)
        {
            if (seen.Contains(ticketId)) continue;
            var sessionIds = await _db.MasterClassSignups.AsNoTracking()
                .Where(s => s.EventId == eventId && s.AttendeeId == a.Id)
                .Select(s => s.SessionId).ToListAsync(ct);
            foreach (var sid in sessionIds)
            {
                var promo = await _mc.RemoveAsync(eventId, a.Id, sid, ct);
                if (promo is not null) freed.Add(promo);
            }
            a.TicketStatus = TicketStatus.None;   // ticket gone (audit row kept)
            a.LastSyncedAt = now;
            cancelled++;
        }
        if (cancelled > 0) await _db.SaveChangesAsync(ct);

        return new SyncResult(created, updated, reassigned, cancelled, reassignments, freed);
    }
}
