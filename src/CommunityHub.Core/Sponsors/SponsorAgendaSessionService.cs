using System.Text.RegularExpressions;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Reminders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Sponsors;

/// <summary>
/// §1352 — a bought sponsor session becomes an agenda session, in its slot, before the sponsor has said
/// anything about it.
/// </summary>
/// <remarks>
/// <para>Operator 2026-10-19: *"i pefer to have their sesssions released in the agenda … it can be fx.
/// sponsorname: session tbd as initial name in the agenda + room. then we can update it once the sponsor
/// updates the title in the hub + assign speaker"* · *"the initial is only if they have not already
/// submitted"* · *"a, we need the attendees to see it so they can plan their agenda"*.</para>
///
/// <para>🔒 <b>Only real purchases.</b> The caller passes the orders the sponsor-order pull itself trusts —
/// COMPLETED webshop orders and SIGNED CEH shop orders. A cancelled order is never in that list, so a
/// withdrawn test purchase (*"2linkit bought this as a test, but it is withdrawn"*) makes no session.
/// <c>SponsorInfo.HasSponsorSession</c> is deliberately NOT the source: it is raise-only (§299 b9) and
/// never clears after a cancellation — the same flaw §1348 fixed for booth tiers.</para>
///
/// <para>🔑 The slot is in the product name (<c>"Sponsor Long 60 min Session - ELDK27 - timeslot: 3 (est.
/// time: 13:30-14:30)"</c>), the room per product kind in <c>sponsor.&lt;edition&gt;.json → agendaSessions</c>.
/// One session per bought slot, keyed by <see cref="Session.SponsorSlotKey"/>; every run rewrites title,
/// abstract, track, speakers, room and time from that truth, so an organizer edit to these fields is
/// replaced (change the sponsor's details or the order instead).</para>
///
/// <para>Zoho: the session push creates the session in Backstage as it stands (create-only). When the
/// sponsor's details later change it, the push's §1002 difference mail tells info@ what to change by hand,
/// every hour until Backstage matches — that is the "inform" half of the operator's ask.</para>
///
/// <para>SoMe: <see cref="Session.ExcludeFromSoMeAnnouncements"/> is set, because a sponsor session is
/// already announced as <c>sponsorsession:{id}</c> once its speaker is known, and "Session TBD" must never
/// be posted.</para>
/// </remarks>
public sealed partial class SponsorAgendaSessionService
{
    private readonly CommunityHubDbContext _db;
    private readonly ILogger? _log;

    public SponsorAgendaSessionService(CommunityHubDbContext db, ILogger? log = null)
    {
        _db = db;
        _log = log;
    }

    /// <summary>The title a session carries until its sponsor has submitted details.</summary>
    public static string PlaceholderTitle(string sponsorName) => $"{sponsorName}: Session TBD";

    /// <summary>One bought slot, read from a product name.</summary>
    public sealed record Slot(string Kind, int Number, TimeOnly Start, TimeOnly End);

    [GeneratedRegex(@"Sponsor\s+(?<kind>.+?)\s+Session\b.*?timeslot:\s*(?<n>\d+)\s*\(est\.\s*time:\s*(?<s>\d{1,2}:\d{2})\s*-\s*(?<e>\d{1,2}:\d{2})\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SlotPattern();

    /// <summary>The slot in a sponsor-session product name, or null (not a session product, or no time — e.g. a TEST slot).</summary>
    public static Slot? ParseSlot(string? productName)
    {
        if (string.IsNullOrWhiteSpace(productName)) return null;
        var m = SlotPattern().Match(productName);
        if (!m.Success) return null;
        if (!TimeOnly.TryParse(m.Groups["s"].Value, out var start) || !TimeOnly.TryParse(m.Groups["e"].Value, out var end)) return null;
        if (end <= start) return null;
        return new Slot(m.Groups["kind"].Value.Trim(), int.Parse(m.Groups["n"].Value), start, end);
    }

    public sealed record Result(int Created, int Updated, IReadOnlyList<string> Orphans);

    /// <param name="currentOrders">Completed webshop orders + signed CEH shop orders — never cancelled ones.</param>
    public async Task<Result> SyncAsync(
        int eventId, IReadOnlyList<WooOrder> currentOrders, SponsorAgendaSessionsConfig? config, CancellationToken ct = default)
    {
        if (config is null || config.Rooms.Count == 0) return new Result(0, 0, Array.Empty<string>());

        var ev = await _db.Events.Where(e => e.Id == eventId).Select(e => new { e.StartDate, e.PreDayDate }).FirstOrDefaultAsync(ct);
        if (ev is null) return new Result(0, 0, Array.Empty<string>());
        var mainDay = MainDay(ev.StartDate, ev.PreDayDate);

        // ---- what was bought: one entry per (company, kind, timeslot) -------------------------
        var wanted = new Dictionary<string, (string CompanyId, string BillingCompany, Slot Slot, string Room)>(StringComparer.Ordinal);
        foreach (var order in currentOrders)
        {
            if (string.IsNullOrWhiteSpace(order.CompanyId)) continue;
            foreach (var line in order.LineItems)
            {
                if (ParseSlot(line.ProductName) is not { } slot) continue;
                var room = config.Rooms.FirstOrDefault(r => string.Equals(r.Key, slot.Kind, StringComparison.OrdinalIgnoreCase)).Value;
                if (string.IsNullOrWhiteSpace(room))
                {
                    _log?.LogWarning("§1352: no agenda room configured for sponsor session kind '{Kind}' ({Product}).", slot.Kind, line.ProductName);
                    continue;
                }
                wanted.TryAdd($"{order.CompanyId}|{slot.Kind}|{slot.Number}", (order.CompanyId!, order.BillingCompany, slot, room));
            }
        }

        var companyIds = wanted.Values.Select(w => w.CompanyId).Distinct().ToList();
        var sponsors = await _db.SponsorInfos.AsNoTracking()
            .Where(s => s.EventId == eventId && companyIds.Contains(s.SponsorCompanyId))
            .Select(s => new { s.SponsorCompanyId, s.CompanyName, s.IsTestData })
            .ToListAsync(ct);
        var sponsorById = sponsors.GroupBy(s => s.SponsorCompanyId).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var details = await _db.SponsorSessions.AsNoTracking()
            .Where(s => s.EventId == eventId && companyIds.Contains(s.SponsorCompanyId))
            .Include(s => s.Speakers)
            .ToListAsync(ct);
        var detailsById = details.GroupBy(d => d.SponsorCompanyId).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var existing = await _db.Sessions
            .Where(s => s.EventId == eventId && s.SponsorSlotKey != null)
            .Include(s => s.SessionSpeakers)
            .ToListAsync(ct);
        var existingByKey = existing.ToDictionary(s => s.SponsorSlotKey!, StringComparer.Ordinal);

        var now = DateTimeOffset.UtcNow;
        int created = 0, updated = 0;
        foreach (var (key, w) in wanted)
        {
            var sponsor = sponsorById.GetValueOrDefault(w.CompanyId);
            if (sponsor?.IsTestData == true) continue;   // a test sponsor never reaches the public agenda
            var name = !string.IsNullOrWhiteSpace(sponsor?.CompanyName) ? sponsor!.CompanyName!.Trim()
                     : !string.IsNullOrWhiteSpace(w.BillingCompany) ? w.BillingCompany.Trim()
                     : $"Company {w.CompanyId}";

            // "Submitted" = the sponsor saved the session step, which requires a title and an abstract.
            var d = detailsById.GetValueOrDefault(w.CompanyId);
            var submitted = d is not null && !string.IsNullOrWhiteSpace(d.Title);
            var title = submitted ? d!.Title!.Trim() : PlaceholderTitle(name);
            var @abstract = submitted && !string.IsNullOrWhiteSpace(d!.Abstract) ? d.Abstract!.Trim() : null;
            // Track: the sponsor's own choice wins; otherwise the session KEEPS the track it has — an organizer
            // may have set it on the Sessions page (operator 2026-10-19 set Intune/Azure by hand, and the first
            // version reset them to the fallback every run). The fallback is only for a NEW session, because
            // Zoho refuses one without a track.
            var existingTrack = existingByKey.TryGetValue(key, out var current) && !string.IsNullOrWhiteSpace(current.Track)
                ? current.Track!.Trim() : null;
            var track = submitted && !string.IsNullOrWhiteSpace(d!.Track) ? d.Track!.Trim()
                      : existingTrack
                        ?? (string.IsNullOrWhiteSpace(config.FallbackTrack) ? null : config.FallbackTrack.Trim());
            var speakerIds = submitted
                ? d!.Speakers.Where(sp => sp.ParticipantId != null).Select(sp => sp.ParticipantId!.Value).Distinct().ToHashSet()
                : new HashSet<int>();

            // Danish wall time on the main day → a real instant (the slot text is local time).
            var startsAt = ToUtc(mainDay, w.Slot.Start);
            var endsAt = ToUtc(mainDay, w.Slot.End);
            var minutes = (int)(endsAt - startsAt).TotalMinutes;

            if (!existingByKey.TryGetValue(key, out var session))
            {
                session = new Session
                {
                    EventId = eventId,
                    SessionizeId = SessionManagementService.HubSessionizeIdPrefix + Guid.NewGuid().ToString("N"),
                    IsHubAdded = true,
                    IsServiceSession = false,
                    Type = SessionType.TechnicalSession,
                    SponsorCompanyId = w.CompanyId,
                    SponsorSlotKey = key,
                    CreatedAt = now,
                };
                _db.Sessions.Add(session);
                created++;
            }
            else if (Differs(session, title, @abstract, track, w.Room, startsAt, endsAt, speakerIds))
            {
                updated++;
            }
            else continue;

            session.Title = title;
            session.Abstract = @abstract;
            session.Track = track;
            session.Room = w.Room;
            session.StartsAt = startsAt;
            session.EndsAt = endsAt;
            session.LengthMinutes = minutes;
            session.Length = SessionDefaultsMapper.ToLengthBucket(minutes);
            session.IsDateOverridden = true;
            session.ExcludeFromSoMeAnnouncements = true;
            session.UpdatedAt = now;

            foreach (var link in session.SessionSpeakers.Where(l => !speakerIds.Contains(l.ParticipantId)).ToList())
                session.SessionSpeakers.Remove(link);
            foreach (var pid in speakerIds.Where(pid => session.SessionSpeakers.All(l => l.ParticipantId != pid)))
                session.SessionSpeakers.Add(new SessionSpeaker { Session = session, ParticipantId = pid });

            _log?.LogInformation("§1352: sponsor agenda session '{Title}' ({Key}) {Action}.", title, key,
                session.Id == 0 ? "created" : "updated");
        }

        // A slot that is no longer bought (order cancelled or moved) is NOT deleted here: the session may
        // already be in Zoho and carry attendee data. It is reported so an organizer removes it on purpose.
        var orphans = existing
            .Where(s => !wanted.ContainsKey(s.SponsorSlotKey!))
            .Select(s => $"'{s.Title}' ({s.SponsorSlotKey}) has no current order — remove it from the agenda and Backstage if the slot is gone.")
            .ToList();
        foreach (var o in orphans) _log?.LogWarning("§1352: {Orphan}", o);

        if (created > 0 || updated > 0) await _db.SaveChangesAsync(ct);
        return new Result(created, updated, orphans);
    }

    private static bool Differs(Session s, string title, string? @abstract, string? track, string room,
                                DateTimeOffset startsAt, DateTimeOffset endsAt, HashSet<int> speakerIds) =>
        s.Title != title
        || s.Abstract != @abstract
        || s.Track != track
        || s.Room != room
        || s.StartsAt != startsAt
        || s.EndsAt != endsAt
        || !s.ExcludeFromSoMeAnnouncements
        || !s.SessionSpeakers.Select(l => l.ParticipantId).ToHashSet().SetEquals(speakerIds);

    /// <summary>
    /// The MAIN day: the first day after the pre-day. 🔴 Not <c>StartDate</c> — on ELDK27 the edition starts
    /// WITH the pre-day (StartDate = PreDayDate = 9 Feb, main day 10 Feb). Using StartDate put all seven
    /// sponsor sessions on the pre-day on their first PROD run (2026-10-19).
    /// </summary>
    public static DateOnly MainDay(DateOnly startDate, DateOnly? preDayDate) =>
        preDayDate is { } pre && pre >= startDate ? pre.AddDays(1) : startDate;

    private static DateTimeOffset ToUtc(DateOnly day, TimeOnly time)
    {
        var local = day.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, EventTimezone.Tz), TimeSpan.Zero);
    }
}
