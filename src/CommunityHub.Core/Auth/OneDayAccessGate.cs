using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Settings;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Auth;

/// <summary>
/// §299 7.1 — hub sign-in gate for 1-DAY ticket holders. The hub is built for 2-day
/// holders (master-class selection, party sign-up); a 1-day holder signing in is
/// BLOCKED with <see cref="BlockedMessage"/> — EXCEPT participants inside the
/// <c>one-day-hub-access</c> feature's released ring (default Ring1), so ring 0/1
/// test users can validate the 1-day experience end to end. Deliberately the
/// STANDARD ring-cap mechanism (<c>participant.Ring &lt;= item.RingCap</c>), not a
/// bespoke check: widening 1-day access later is one ring promotion in Settings.
///
/// Enforced at every sign-in entry point (PIN login, magic link, /go) AND in the
/// cookie-revalidation backstop (<c>OnValidatePrincipal</c>) so an already-issued
/// 365-day cookie cannot keep a blocked 1-day holder signed in — the server-side
/// check is what actually holds; hiding pages is not enough (§299 7.1).
/// </summary>
public sealed class OneDayAccessGate
{
    /// <summary>The EXISTING attendee-1day-access catalog feature (UserImpact, Ring1
    /// default) — the same key the provisioning/lockout sweep
    /// (<c>ReconcileOneDayAccessAsync</c>) rides, so sign-in gating and the batch
    /// lockout are ONE mechanism with one ring knob in Settings.</summary>
    public const string FeatureKey = "attendee-1day-access";

    /// <summary>The exact operator-specified block message (§299 7.1).</summary>
    public const string BlockedMessage =
        "You are a 1-day ticket holder. Event hub is only relevant for 2-day ticket holders";

    private readonly CommunityHubDbContext _db;
    private readonly FeatureGateService _gate;
    private readonly RingResolver _rings;

    public OneDayAccessGate(CommunityHubDbContext db, FeatureGateService gate, RingResolver rings)
    {
        _db = db;
        _gate = gate;
        _rings = rings;
    }

    /// <summary>
    /// §326ap — the SAME verdict resolved from an E-MAIL address, for the FIRST step of
    /// PIN sign-in (operator 2026-07-25: a 1-day holder must be told before they wait for
    /// a PIN). Returns false for an unknown address, so the PIN step's neutral,
    /// non-enumerating answer is unchanged for everyone who is not a mirrored 1-day holder.
    /// </summary>
    public async Task<bool> IsBlockedByEmailAsync(
        int eventId, string? email, CancellationToken ct = default)
    {
        var norm = (email ?? string.Empty).Trim().ToLower();
        if (norm.Length == 0) return false;

        var id = await _db.Participants
            .Where(p => p.EventId == eventId && (p.Email ?? string.Empty).ToLower() == norm)
            .Select(p => (int?)p.Id)
            .FirstOrDefaultAsync(ct);

        return id is not null && await IsBlockedAsync(id.Value, ct);
    }

    /// <summary>
    /// True when this participant must be REFUSED hub access: an Attendee whose
    /// mirrored active ticket(s) are all non-2-day, outside the feature's released
    /// ring. Non-attendees, attendees holding any active 2-day ticket, and attendee
    /// participants with NO mirrored ticket at all (seeded/test rows — the ticket
    /// truth lives in the Zoho mirror) are never blocked here.
    /// </summary>
    public async Task<bool> IsBlockedAsync(int participantId, CancellationToken ct = default)
    {
        var p = await _db.Participants
            .Where(x => x.Id == participantId)
            .Select(x => new { x.Id, x.EventId, x.Email, x.Role })
            .FirstOrDefaultAsync(ct);
        if (p is null || p.Role != ParticipantRole.Attendee) return false;

        var email = (p.Email ?? string.Empty).Trim().ToLower();
        if (email.Length == 0) return false;

        // Ticket truth from the Zoho mirror. One email can legitimately hold several
        // rows (cancelled + active, §234) — only ACTIVE rows decide.
        var tickets = await _db.Attendees
            .Where(a => a.EventId == p.EventId
                        && a.MirrorState == MirrorState.Active
                        && a.Email.ToLower() == email)
            .Select(a => a.TicketStatus)
            .ToListAsync(ct);
        if (tickets.Count == 0) return false;                    // no mirrored ticket — not gated
        if (tickets.Contains(TicketStatus.TwoDay)) return false; // any active 2-day ticket wins
        if (!tickets.Contains(TicketStatus.Other)) return false; // only Unknown/None rows — fail open

        // A 1-day holder: allowed only inside the released ring (RingCap=1 default).
        return !await _gate.IsFeatureActiveForParticipantAsync(
            FeatureKey, p.EventId, p.Id, _rings, ct);
    }
}
