using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

[Authorize]
public class LunchModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;

    public LunchModel(CommunityHubDbContext db, ICurrentParticipantAccessor participant)
    {
        _db = db;
        _participant = participant;
    }

    public bool AccessDenied { get; private set; }

    public int EarlySetupDayCount { get; private set; }
    public int SetupDayCount { get; private set; }
    public int PreDayCount   { get; private set; }
    /// <summary>Pre-day headcount auto-counted (active crew + MC speakers).</summary>
    public int AutoCountedPreDayCount { get; private set; }
    /// <summary>Pre-day headcount from form declarations (non-auto-counted roles).</summary>
    public int DeclaredPreDayCount { get; private set; }
    /// <summary>§298 — pre-day heads from sponsor booth check-in: the SUM of each checked-in
    /// company's declared booth-member count (company-level, added on top of participant heads).</summary>
    public int BoothCheckInPreDayCount { get; private set; }
    public int TotalResponses { get; private set; }

    // §326bv (operator 2026-07-25: "i need to have a single count for main day also …
    // i need to trust this, so i order correct amount").
    /// <summary>Attendees eating on the PRE-DAY: mirrored, active 2-day ticket holders —
    /// the pre-day IS the Master Class day, and only a 2-day ticket gets in.</summary>
    public int AttendeePreDayCount { get; private set; }
    /// <summary>Crew heads on the MAIN day: every active participant. Nobody registers for
    /// main-day lunch (§326h — "it is ordered for everyone"), so presence is the count.</summary>
    public int CrewMainDayCount { get; private set; }
    /// <summary>Attendees on the MAIN day: every mirrored active attendee, any ticket class.</summary>
    public int AttendeeMainDayCount { get; private set; }
    /// <summary>The single number to order main-day lunch against.</summary>
    public int MainDayCount => CrewMainDayCount + AttendeeMainDayCount;

    public string EarlySetupDayLabel { get; private set; } = "Setup day (Sun)";
    public string SetupDayLabel { get; private set; } = "Setup day (Mon)";
    public string PreDayLabel   { get; private set; } = "Pre-day";
    public string MainDayLabel  { get; private set; } = "Main day";

    public List<Row> Rows { get; private set; } = new();

    /// <summary>
    /// One line on the audit list. <paramref name="AutoCounted"/> marks a head the hub
    /// counted WITHOUT the person declaring anything — always-on-site crew. Those rows used
    /// to be invisible: they were in the tile but not the table, so the number could not be
    /// checked against the list. They are now shown and flagged (§326bv).
    /// </summary>
    public record Row(
        string Name, string Email, string Role,
        bool EarlySetupDay, bool SetupDay, bool PreDay, string? Notes,
        bool AutoCounted = false);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        var evt = await _db.Events
            .Where(e => e.Id == me.EventId)
            .Select(e => new { e.StartDate })
            .FirstOrDefaultAsync(ct);
        if (evt is not null)
        {
            // StartDate IS the pre-day / Master Class; the two days before are setup.
            EarlySetupDayLabel = $"Setup day ({evt.StartDate.AddDays(-2):dddd, MMM d yyyy})";
            SetupDayLabel      = $"Setup day ({evt.StartDate.AddDays(-1):dddd, MMM d yyyy})";
            PreDayLabel        = $"Pre-day / Master Class ({evt.StartDate:dddd, MMM d yyyy})";
            MainDayLabel       = $"Main day ({evt.StartDate.AddDays(1):dddd, MMM d yyyy})";
        }

        // ACTIVE people only (§253 G4): a drop-out's surviving lunch sign-up must
        // not count toward the caterer numbers or list on this page.
        // §946 (operator 2026-08-07): and NOT a test user — "IsTestUsers should not count towards
        // these logistics". Every number on this page becomes a meal the caterer prepares. The
        // predicate is the FLAG, not Ring 1: the seeded test-*@ accounts carry IsTestUser without
        // being Ring 1, so a ring-based filter would have left them all in the order.
        var rows = await _db.LunchSignups
            .Where(l => l.EventId == me.EventId)
            .Join(_db.Participants, l => l.ParticipantId, p => p.Id, (l, p) => new
            {
                p.Id, p.FullName, p.Email, p.Role, p.IsActive, p.IsTestUser,
                l.LunchEarlySetupDay, l.LunchSetupDay, l.LunchPreDay, l.Notes
            })
            .Where(x => x.IsActive && !x.IsTestUser)
            .ToListAsync(ct);

        // (Rows are assembled AFTER the auto-count set is known — see below, §326bv: the
        // auto-counted crew must appear in the list even though they never filled the form.)

        // Pre-day headcount (§294, operator 2026-07-11). Three non-overlapping contributions,
        // deduplicated by participant so nobody is counted twice:
        //   1. AUTO-COUNT — the always-on-site crew (Organizer / Media / Event-partner). They
        //      have no pre-day checkbox. (Speakers are NO LONGER auto-counted: any speaker can
        //      join the pre-day but some arrive after lunch, so they DECLARE via the opt-in
        //      checkbox — master-class speakers just default it to Yes.)
        //   2. DECLARED — anyone who ticked the pre-day lunch checkbox (speakers/sponsors/vols).
        //   3. BOOTH CHECK-IN — §298: each checked-in exhibitor company declares HOW MANY booth
        //      members will check in on the pre-day; that COUNT (a whole team, not 1 CEH participant)
        //      is the company's pre-day lunch heads.
        // §326bv: the auto-counted crew are now loaded WITH their name/email so they can be
        // listed, not just tallied. An organizer has to be able to read the number off the
        // table; a head that appears only in a tile cannot be checked.
        var crew = await _db.Participants
            .Where(ParticipantActivation.IsActiveExpr)
            .Where(p => p.EventId == me.EventId && !p.IsTestUser
                && (p.Role == ParticipantRole.Organizer
                    || p.Role == ParticipantRole.Media
                    || p.Role == ParticipantRole.EventPartner))
            .Select(p => new { p.Id, p.FullName, p.Email, p.Role })
            .ToListAsync(ct);
        var crewIds = crew.Select(c => c.Id).ToHashSet();

        // §298: sum each checked-in company's declared booth-member count (real arrival slot, not
        // the opt-out). This is the sponsor pre-day lunch contribution — company-level, not per CEH
        // participant — so sponsors are EXCLUDED from the per-participant "declared" set below (no
        // double-count).
        var boothMemberCount = (await _db.SponsorInfos
            .Where(s => s.EventId == me.EventId
                && s.BoothCheckInSlot != null
                && s.BoothCheckInSlot != BoothCheckInSlots.NotParticipating
                && s.BoothCheckInMemberCount != null)
            .Select(s => s.BoothCheckInMemberCount!.Value)
            .ToListAsync(ct))
            .Sum();

        var declaredIds = rows.Where(r => r.LunchPreDay && r.Role != ParticipantRole.Sponsor)
            .Select(r => r.Id).ToHashSet();

        // Participant-level heads (crew ∪ declared), deduped; the company-level booth member count
        // is added on top.
        var preDaySet = new HashSet<int>(crewIds);
        preDaySet.UnionWith(declaredIds);

        TotalResponses = rows.Count;
        EarlySetupDayCount = rows.Count(r => r.LunchEarlySetupDay);
        SetupDayCount      = rows.Count(r => r.LunchSetupDay);
        AutoCountedPreDayCount = crewIds.Count;
        DeclaredPreDayCount = declaredIds.Except(crewIds).Count();
        BoothCheckInPreDayCount = boothMemberCount;

        // §326bv — ATTENDEES eat too, and were missing from both pre-day and main day.
        // Pre-day IS the Master Class day, which only a 2-day ticket admits; the main day
        // admits every ticket class. Mirror-active rows only, so a cancellation (§326as)
        // drops out of the catering order on the next sync.
        AttendeePreDayCount = await _db.Attendees.CountAsync(
            a => a.EventId == me.EventId
                 && a.MirrorState == MirrorState.Active
                 && a.TicketStatus == TicketStatus.TwoDay, ct);
        AttendeeMainDayCount = await _db.Attendees.CountAsync(
            a => a.EventId == me.EventId && a.MirrorState == MirrorState.Active, ct);

        // MAIN DAY is not a sign-up: lunch is ordered for everyone on site (§326h), so the
        // crew side is simply every ACTIVE participant — no checkbox, no dedup needed.
        CrewMainDayCount = await _db.Participants
            .Where(ParticipantActivation.IsActiveExpr)
            .CountAsync(p => p.EventId == me.EventId && !p.IsTestUser, ct);

        PreDayCount = preDaySet.Count + boothMemberCount + AttendeePreDayCount;

        // The audit list: everyone who declared a day, PLUS the auto-counted crew that no
        // form ever produced a row for. Crew who DID fill the form keep their declared row
        // (flagged auto as well, since their pre-day head is counted either way).
        var declaredRows = rows
            .Select(r => new Row(r.FullName, r.Email, r.Role.ToString(),
                r.LunchEarlySetupDay, r.LunchSetupDay, r.LunchPreDay, r.Notes,
                AutoCounted: crewIds.Contains(r.Id)))
            .ToList();

        var listedIds = rows.Select(r => r.Id).ToHashSet();
        var autoOnlyRows = crew
            .Where(c => !listedIds.Contains(c.Id))
            .Select(c => new Row(c.FullName, c.Email, c.Role.ToString(),
                EarlySetupDay: false, SetupDay: false, PreDay: true, Notes: null,
                AutoCounted: true));

        Rows = declaredRows.Concat(autoOnlyRows)
            .OrderBy(r => r.Role)
            .ThenBy(r => r.Name)
            .ToList();

        return Page();
    }
}
