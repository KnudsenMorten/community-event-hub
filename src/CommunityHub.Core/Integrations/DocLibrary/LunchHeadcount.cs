using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations.DocLibrary;

/// <summary>
/// §1086 — the lunch head-count, broken into the parts it is made of.
/// </summary>
/// <remarks>
/// <para>Every component is exposed, not just the totals, because the numbers are used to ORDER
/// FOOD: an organizer who cannot see where a head came from cannot check it, and §326bv already
/// made that point once (*"i need to trust this, so i order correct amount"* — the auto-counted crew
/// were in the tile and not in the table, so the number could not be reconciled).</para>
/// </remarks>
/// <param name="PreDayCrewAutoCounted">
/// Always-on-site crew (Organizer / Media / Event Partner). They are never SHOWN a pre-day
/// checkbox (<see cref="LunchAudience.PreDayAutoCountedRole"/>), so their <c>LunchPreDay = false</c>
/// is an unanswered default and never a "no".
/// </param>
/// <param name="PreDayDeclared">
/// People who ticked the pre-day box and are not already auto-counted — speakers and volunteers.
/// ⚠️ SPONSORS are excluded here on purpose: their pre-day heads arrive as
/// <paramref name="PreDaySponsorBoothMembers"/>, a company-level count, so counting a sponsor's own
/// tick as well would bill the venue twice for the same person.
/// </param>
/// <param name="PreDaySponsorBoothMembers">
/// §298 — each checked-in exhibitor's declared booth-member count. A COUNT of a team, not a row per
/// person; the opt-out slot contributes nothing.
/// </param>
/// <param name="PreDayTwoDayAttendees">
/// §1086 — the Master Class audience. The pre-day IS the Master Class day and only a 2-day ticket
/// admits you to it.
/// </param>
/// <param name="MainDayCrew">
/// §326h — nobody registers for main-day lunch (*"it is ordered for everyone"*), so presence is the
/// count: every countable participant — 🔴 §1258: EXCEPT those already represented by a live
/// ticket, who are counted on the attendee side.
/// </param>
/// <param name="MainDayAttendees">Every attendee still holding a ticket, any class.</param>
/// <remarks>
/// 🔴 <b>§1258 — the two sides are disjoint because they are MADE disjoint, not because they
/// happen to be.</b> This note used to say crew do not hold tickets (operator 2026-08-14: <i>"yes
/// crew do not have tickets, so no dedup needed"</i>) and therefore the two sides could simply be
/// added. That was true when written and it is STILL true of crew — not one speaker, sponsor or
/// organizer holds a ticket — but the ticket launch began provisioning a PARTICIPANT LOGIN for
/// every 2-day buyer, so the participant table stopped being crew-only. Measured in PROD
/// 2026-09-20: 137 of the 260 "crew" rows were attendee logins that each also held a live ticket,
/// so the main-day figure read 423 where the truth was 286 — ~137 lunches over-ordered.
/// ⇒ <see cref="LogisticsAudience.CountableCrew"/> now EXCLUDES anyone whose e-mail holds a live
/// ticket, so the disjointness is enforced rather than assumed. The old note's fear — that a dedup
/// would subtract a real head — is answered by matching on the TICKET (an actual second count),
/// never on a name or a guess.
/// </remarks>
public sealed record LunchHeadcount(
    int EarlySetupDay,
    int SetupDay,
    int PreDayCrewAutoCounted,
    int PreDayDeclared,
    int PreDaySponsorBoothMembers,
    int PreDayTwoDayAttendees,
    int MainDayCrew,
    int MainDayAttendees,
    int SignupResponses)
{
    /// <summary>Participant-level pre-day heads — the people this file can NAME.</summary>
    public int PreDayNamed => PreDayCrewAutoCounted + PreDayDeclared;

    /// <summary>The number the pre-day lunch is ordered against.</summary>
    public int PreDay => PreDayNamed + PreDaySponsorBoothMembers + PreDayTwoDayAttendees;

    /// <summary>The number the main-day lunch is ordered against.</summary>
    public int MainDay => MainDayCrew + MainDayAttendees;
}

/// <summary>
/// §1086 — <b>THE ONE LUNCH CALCULATION ENGINE.</b>
/// </summary>
/// <remarks>
/// <para>🔴 <b>Why it exists.</b> Operator 2026-08-14: <i>"logistics lunch preday must contain any
/// attendees that booked a 2-day ticket. i think it is missing in the calculation and excel
/// export"</i>, then, on finding the dashboard: <i>"lunch count here must use same calculation
/// engine"</i>. He was reading THREE different answers to one question:</para>
/// <list type="table">
///   <item><term><c>/Organizer/Lunch</c></term><description>crew auto-count ∪ declared + booth
///     members + 2-day attendees — the complete rule</description></item>
///   <item><term><c>/Organizer/Dashboard</c></term><description>raw <c>LunchSignups</c> ticks: no
///     crew, no booth, no attendees, and it did not even exclude TEST users (§946)</description></item>
///   <item><term><see cref="LunchLogisticsProducer"/> (the Excel the venue gets)</term>
///     <description>sign-ups only: no crew auto-count, no attendees, and a sponsor who ticked the
///     box was counted TWICE — once named, once inside their booth-member count</description></item>
/// </list>
///
/// <para>🔑 <b>Each surface was individually defensible and collectively wrong.</b> The catering
/// order is one number; three ways of reaching it is three chances to under-order. This is the §366 /
/// §1081 shape — one question must have one implementation — and the answer is the same as it was
/// for the wizard progress in §1085: put the rule where every reader can reach it, then make the
/// readers ask.</para>
///
/// <para>⚠️ <b>It reads, it never writes.</b> Nothing here decides anything a person told us: the
/// sign-up table, the booth check-in and the ticket class are all existing answers (the operator's
/// 2026-08-02 rule: <i>"data is in the table for the different sign up … calculation service must
/// not add another logic"</i>). The only rule this class adds is which of those answers apply to
/// which day, and that rule was already written down in three places.</para>
/// </remarks>
public sealed class LunchHeadcountService
{
    private readonly CommunityHubDbContext _db;

    public LunchHeadcountService(CommunityHubDbContext db) => _db = db;

    public async Task<LunchHeadcount> ComputeAsync(int eventId, CancellationToken ct = default)
    {
        // 🔒 ONE audience rule for the participant side (§946 + §253 G4): active, real people.
        // The dashboard used to filter on IsActive alone, so every seeded test-*@ account was a
        // meal in the order.
        var countable = LogisticsAudience.Countable(_db.Participants, eventId);

        var signups = await _db.LunchSignups
            .Where(l => l.EventId == eventId)
            .Join(countable, l => l.ParticipantId, p => p.Id, (l, p) => new
            {
                p.Id, p.Role,
                l.LunchEarlySetupDay, l.LunchSetupDay, l.LunchPreDay,
            })
            .ToListAsync(ct);

        // The always-on-site crew: no checkbox exists for them, so presence is the answer.
        var autoCountedIds = await countable
            .Where(p => p.Role == ParticipantRole.Organizer
                        || p.Role == ParticipantRole.Media
                        || p.Role == ParticipantRole.EventPartner)
            .Select(p => p.Id)
            .ToListAsync(ct);
        var autoCounted = autoCountedIds.ToHashSet();

        // Declared, minus the two sets that must not be double-counted: the auto-counted crew (who
        // are already in) and the sponsors (whose heads arrive as a company booth count).
        var declared = signups
            .Where(s => s.LunchPreDay
                        && s.Role != ParticipantRole.Sponsor
                        && !autoCounted.Contains(s.Id))
            .Select(s => s.Id)
            .Distinct()
            .Count();

        var boothMembers = await _db.SponsorInfos
            .Where(s => s.EventId == eventId
                        && s.BoothCheckInSlot != null
                        && s.BoothCheckInSlot != BoothCheckInSlots.NotParticipating)
            .SumAsync(s => (int?)s.BoothCheckInMemberCount ?? 0, ct);

        return new LunchHeadcount(
            EarlySetupDay: signups.Count(s => s.LunchEarlySetupDay),
            SetupDay: signups.Count(s => s.LunchSetupDay),
            PreDayCrewAutoCounted: autoCounted.Count,
            PreDayDeclared: declared,
            PreDaySponsorBoothMembers: boothMembers,
            PreDayTwoDayAttendees: await LogisticsAudience
                .PreDayAttendees(_db.Attendees, eventId).CountAsync(ct),
            // §1258 — the crew side EXCLUDING anyone a live ticket already counts (see the class note).
            MainDayCrew: await LogisticsAudience
                .CountableCrew(_db.Participants, _db.Attendees, eventId).CountAsync(ct),
            MainDayAttendees: await LogisticsAudience
                .CountableAttendees(_db.Attendees, eventId).CountAsync(ct),
            SignupResponses: signups.Count);
    }

    /// <summary>
    /// The participant ids whose pre-day head this engine counted, so a caller can LIST the same
    /// people it totalled. 🔑 A number an organizer cannot reconcile against a list is a number they
    /// will not trust (§326bv).
    /// </summary>
    public async Task<IReadOnlyCollection<int>> PreDayNamedIdsAsync(
        int eventId, CancellationToken ct = default)
    {
        var countable = LogisticsAudience.Countable(_db.Participants, eventId);

        var ids = await countable
            .Where(p => p.Role == ParticipantRole.Organizer
                        || p.Role == ParticipantRole.Media
                        || p.Role == ParticipantRole.EventPartner)
            .Select(p => p.Id)
            .ToListAsync(ct);
        var set = ids.ToHashSet();

        var declared = await _db.LunchSignups
            .Where(l => l.EventId == eventId && l.LunchPreDay)
            .Join(countable.Where(p => p.Role != ParticipantRole.Sponsor),
                l => l.ParticipantId, p => p.Id, (l, p) => p.Id)
            .ToListAsync(ct);
        set.UnionWith(declared);

        return set;
    }
}

/// <summary>
/// §1086 — WHICH ROLES have no pre-day lunch choice, in <b>Core</b> so every reader can ask.
/// </summary>
/// <remarks>
/// 🔴 The rule already existed as <c>LunchFormService.PreDayAutoCountedRole</c> — in the WEB
/// project, which the Core logistics producers cannot reference. That is the concrete reason the
/// Excel file could not share it and grew its own answer instead. A rule the other half of the
/// system physically cannot reach is a rule that will be re-implemented.
/// </remarks>
public static class LunchAudience
{
    /// <summary>
    /// True for the always-on-site crew: they are never shown a pre-day checkbox, so their stored
    /// <c>false</c> means "never asked", not "not eating".
    /// </summary>
    public static bool PreDayAutoCountedRole(ParticipantRole role) =>
        role is ParticipantRole.Organizer or ParticipantRole.Media or ParticipantRole.EventPartner;
}
