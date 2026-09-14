using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations;

/// <summary>What a publish did.</summary>
public sealed record GroupPhotoPublishResult(int Published, int AlreadyPublished, int Skipped)
{
    public override string ToString() =>
        $"{Published} published, {AlreadyPublished} unchanged, {Skipped} skipped";
}

/// <summary>
/// §1077 stage 5 — the SCHEDULE around the planner: the slots an organizer keeps, the proposal, and
/// the publish that finally tells a company when to turn up.
/// </summary>
/// <remarks>
/// <para>🔴 <b>PROPOSE and PUBLISH are two separate acts, and that is the whole design.</b> A time
/// on a company's page is forwarded to eleven colleagues the moment they read it, so a proposal must
/// be arguable, re-runnable and disposable without anybody being told anything. Publishing is the
/// deliberate moment the plan becomes a promise.</para>
///
/// <para>🔒 Published slots are PINNED on every later run — see <see cref="GroupPhotoPlanner"/>.</para>
/// </remarks>
public sealed class GroupPhotoScheduleService
{
    private readonly CommunityHubDbContext _db;
    private readonly VolumePackageGroupPhotoService _photos;
    private readonly TimeProvider _clock;

    public GroupPhotoScheduleService(
        CommunityHubDbContext db, VolumePackageGroupPhotoService photos, TimeProvider? clock = null)
    {
        _db = db;
        _photos = photos;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Danish wall clock — the same convention the hotel and photo invites already use.</summary>
    public static TimeSpan LocalOffset(DateTime localDate) =>
        TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time").GetUtcOffset(localDate);

    // ---------------------------------------------------------------- slots

    public async Task<IReadOnlyList<GroupPhotoSlot>> SlotsAsync(
        int eventId, CancellationToken ct = default) =>
        await _db.GroupPhotoSlots
            .Where(s => s.EventId == eventId)
            .OrderBy(s => s.StartUtc)
            .ToListAsync(ct);

    /// <summary>
    /// Add one slot. Returns false when that exact moment already has one — the unique index is the
    /// real guard, and this turns it into an answer rather than an exception.
    /// </summary>
    public async Task<bool> AddSlotAsync(
        int eventId, DateTimeOffset startUtc, bool isPreDay, int durationMinutes = 10,
        string? label = null, string? location = null, CancellationToken ct = default)
    {
        var exists = await _db.GroupPhotoSlots
            .AnyAsync(s => s.EventId == eventId && s.StartUtc == startUtc, ct);
        if (exists) return false;

        _db.GroupPhotoSlots.Add(new GroupPhotoSlot
        {
            EventId = eventId,
            StartUtc = startUtc,
            IsPreDay = isPreDay,
            DurationMinutes = durationMinutes,
            Label = label,
            Location = location,
            CreatedAt = _clock.GetUtcNow(),
        });
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// 🔴 Deleting a slot a company has already been TOLD about is refused. Their calendar entry
    /// would point at a time that no longer exists, and nobody would know until the day. Move them
    /// first, or block the slot instead.
    /// </summary>
    public async Task<string?> DeleteSlotAsync(int slotId, CancellationToken ct = default)
    {
        var slot = await _db.GroupPhotoSlots.FirstOrDefaultAsync(s => s.Id == slotId, ct);
        if (slot is null) return "That slot no longer exists.";

        var published = await _db.GroupPhotoRegistrations
            .Where(r => r.EventId == slot.EventId && r.SlotPublishedAt != null
                        && r.ScheduledAtUtc == slot.StartUtc)
            .Select(r => r.CompanyName)
            .FirstOrDefaultAsync(ct);

        if (published is not null)
            return $"“{published}” has already been told about this slot. Give them another time "
                 + "first, or block the slot instead of deleting it.";

        _db.GroupPhotoSlots.Remove(slot);
        await _db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>
    /// Parse pasted lines of <c>yyyy-MM-dd HH:mm</c> (local) into slots — the fast way to enter the
    /// operator's twenty-five. Returns how many were added and every line that could not be read.
    /// </summary>
    /// <remarks>
    /// ⚠️ A line that cannot be parsed is REPORTED, never skipped quietly: a mistyped slot that
    /// silently vanishes is a company with no photo, discovered on the day.
    /// </remarks>
    /// <param name="mainDay">
    /// 🔴 The LAST day of the edition — the "main day" in the operator's words. Every slot before it
    /// is a pre-day slot.
    /// </param>
    /// <remarks>
    /// ⚠️ <b>Deliberately NOT <c>Event.PreDayDate</c>.</b> For ELDK27 that is 8 February, the
    /// master-class / setup day; the operator's photo days are <b>9 February (pre-day)</b> and
    /// <b>10 February (main day)</b> — <i>"it is 2 days - preday 9 feb 2027 … and main day 10 feb
    /// 2027"</i>. Deriving the flag from the LAST day gets both right, keeps working if a slot is
    /// ever put on the 8th, and needs no new configuration to disagree with.
    /// </remarks>
    public async Task<(int Added, IReadOnlyList<string> Rejected)> AddSlotsFromTextAsync(
        int eventId, string? text, DateOnly? mainDay, int durationMinutes = 10,
        CancellationToken ct = default)
    {
        var rejected = new List<string>();
        var added = 0;
        if (string.IsNullOrWhiteSpace(text)) return (0, rejected);

        foreach (var raw in text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (!TryParseSlotLine(line, out var local, out var minutes))
            {
                rejected.Add($"{line} — could not read a date and time "
                           + "(use 2027-02-09 09:30, or 09-02-2027 09:30-09:35)");
                continue;
            }

            var startUtc = new DateTimeOffset(local, LocalOffset(local)).ToUniversalTime();
            // Pre-day = anything BEFORE the last day. See the remarks: the operator's two photo days
            // are 9 Feb (pre-day) and 10 Feb (main day), which is not what Event.PreDayDate holds.
            var isPreDay = mainDay is { } last && DateOnly.FromDateTime(local) < last;

            if (await AddSlotAsync(eventId, startUtc, isPreDay, minutes ?? durationMinutes, ct: ct))
                added++;
            else
                rejected.Add($"{line} — there is already a slot at that time");
        }

        return (added, rejected);
    }

    /// <summary>
    /// Read one pasted line into a local start time and, where the line gives a RANGE, the slot
    /// length it implies.
    /// </summary>
    /// <remarks>
    /// <para>🔑 <b>It accepts what the operator's list actually looks like</b> — his 2026 plan was
    /// <c>25-02-2026&#9;14:35-14:40</c>: day-first dates, a tab, and a time RANGE. A parser that only
    /// took <c>yyyy-MM-dd HH:mm</c> would have rejected every line of the real data and reported it
    /// as twenty-three mistakes.</para>
    ///
    /// <para>🔴 <b>Day-first is tried BEFORE month-first, and that is deliberate.</b>
    /// <c>09-02-2027</c> is 9 February to him and 2 September to an American parser — both parse, so
    /// "whichever works" would silently schedule photos seven months late. Danish input, Danish
    /// reading; an unambiguous ISO date still parses either way.</para>
    ///
    /// <para>⚠️ The RANGE sets the duration (14:35-14:40 ⇒ 5 minutes) instead of the caller's
    /// default, because the length is something he has already decided and typed.</para>
    /// </remarks>
    public static bool TryParseSlotLine(string line, out DateTime localStart, out int? minutes)
    {
        localStart = default;
        minutes = null;

        // Split on the FIRST run of whitespace: "<date><tab or spaces><time or range>".
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;

        var datePart = parts[0];
        var timePart = string.Concat(parts.Skip(1)).Replace(" ", string.Empty);

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var styles = System.Globalization.DateTimeStyles.None;

        // Day-first first (see the remarks), then ISO / anything else invariant understands.
        var dayFirstFormats = new[] { "dd-MM-yyyy", "d-M-yyyy", "dd/MM/yyyy", "d/M/yyyy", "dd.MM.yyyy" };
        if (!DateTime.TryParseExact(datePart, dayFirstFormats, inv, styles, out var date)
            && !DateTime.TryParse(datePart, inv, styles, out date))
            return false;

        // A range ("14:35-14:40") or a single time ("14:35").
        var times = timePart.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (times.Length == 0) return false;
        if (!TimeOnly.TryParse(times[0], inv, styles, out var start)) return false;

        if (times.Length > 1 && TimeOnly.TryParse(times[1], inv, styles, out var end))
        {
            // 🔴 Compare the TIMES, not their difference. `TimeOnly - TimeOnly` WRAPS around
            // midnight: 14:40-14:35 does not come back as -5 minutes, it comes back as 23h55m —
            // so a "span > 0" guard reads a typo as a 1,435-minute photo slot and stores it.
            // ⚠️ A photo slot never crosses midnight, so end-after-start is the whole rule.
            if (end > start) minutes = (int)(end - start).TotalMinutes;
        }

        localStart = date.Date.Add(start.ToTimeSpan());
        return true;
    }

    /// <summary>
    /// §1077 stage 5 — a STARTER SET for testing: twenty-five slots across the two days, which an
    /// organizer then edits or replaces with the real ones.
    /// </summary>
    /// <remarks>
    /// 🔑 Built as a button rather than rows I insert by hand, so it is repeatable, visible and
    /// reversible — and so the operator's real twenty-five can be pasted over it later.
    /// ⚠️ It adds NOTHING when slots already exist: a starter set that silently doubled a real
    /// schedule would be worse than no button at all.
    /// </remarks>
    public async Task<int> AddStarterSetAsync(int eventId, CancellationToken ct = default)
    {
        if (await _db.GroupPhotoSlots.AnyAsync(s => s.EventId == eventId, ct)) return 0;

        var ev = await _db.Events.FirstOrDefaultAsync(e => e.Id == eventId, ct);
        if (ev is null) return 0;

        var mainDay = ev.StartDate;
        var preDay = ev.PreDayDate;

        var added = 0;
        // Pre-day: 10 slots either side of lunch, when only the 2-day people are in the building.
        if (preDay is { } pre)
        {
            added += await SeedDayAsync(eventId, pre, isPreDay: true,
                new[] { "11:30", "11:45", "12:00", "12:15", "12:30", "15:00", "15:15", "15:30", "15:45", "16:00" },
                ct);
        }
        // Main day: 15 slots, clustered on the breaks when everybody is out of the rooms.
        added += await SeedDayAsync(eventId, mainDay, isPreDay: false,
            new[]
            {
                "09:45", "10:00", "10:15", "10:30", "10:45",
                "12:00", "12:15", "12:30", "12:45", "13:00",
                "14:30", "14:45", "15:00", "15:15", "15:30",
            },
            ct);

        return added;
    }

    private async Task<int> SeedDayAsync(
        int eventId, DateOnly day, bool isPreDay, string[] times, CancellationToken ct)
    {
        var added = 0;
        foreach (var t in times)
        {
            var local = day.ToDateTime(TimeOnly.Parse(t, System.Globalization.CultureInfo.InvariantCulture));
            var startUtc = new DateTimeOffset(local, LocalOffset(local)).ToUniversalTime();
            if (await AddSlotAsync(eventId, startUtc, isPreDay, 10,
                    label: isPreDay ? "Pre-day (starter set)" : "Main day (starter set)", ct: ct))
                added++;
        }
        return added;
    }

    // ------------------------------------------------------------- the plan

    /// <summary>
    /// Propose a plan. Reads nothing but stored slots and the companies that asked for a photo, so
    /// the answer can be recomputed at any time and compared with what is published.
    /// </summary>
    public async Task<GroupPhotoPlan> ProposeAsync(int eventId, CancellationToken ct = default)
    {
        var slots = (await SlotsAsync(eventId, ct))
            .Where(s => s.IsAvailable)
            .Select(s => new GroupPhotoSlotOption(s.Id, s.StartUtc, s.IsPreDay))
            .ToList();

        var registrations = await _db.GroupPhotoRegistrations
            .Where(r => r.EventId == eventId)
            .ToListAsync(ct);

        var byStart = slots.ToDictionary(s => s.StartUtc, s => s.SlotId);

        var candidates = new List<GroupPhotoCandidate>();
        foreach (var r in registrations)
        {
            // ⚠️ The day constraint comes from the company's OWN attendees, counted the same way the
            // qualification counts them — so the plan cannot disagree with the number on the page.
            var canDoPreDay = true;
            if (r.VolumePackageCompanyId is { } companyId)
            {
                var hint = await _photos.DayHintAsync(companyId, ct);
                canDoPreDay = hint.PreDayPossible;
            }

            int? pinned = null;
            if (r.SlotIsPinned && r.ScheduledAtUtc is { } when && byStart.TryGetValue(when, out var id))
                pinned = id;
            else if (r.SlotIsPinned)
                pinned = -1;   // published against a slot that no longer exists — the planner says so

            candidates.Add(new GroupPhotoCandidate(r.Id, r.CompanyName, canDoPreDay, pinned));
        }

        return GroupPhotoPlanner.Plan(candidates, slots);
    }

    /// <summary>
    /// Write the proposal onto the registrations as PLANNED times. Still tells nobody.
    /// </summary>
    public async Task<int> SaveProposalAsync(
        int eventId, GroupPhotoPlan plan, CancellationToken ct = default)
    {
        var registrations = await _db.GroupPhotoRegistrations
            .Where(r => r.EventId == eventId)
            .ToDictionaryAsync(r => r.Id, ct);

        var saved = 0;
        foreach (var a in plan.Assignments.Where(a => !a.WasAlreadyPublished))
        {
            if (!registrations.TryGetValue(a.RegistrationId, out var r)) continue;
            r.PlannedAtUtc = a.StartUtc;
            saved++;
        }
        await _db.SaveChangesAsync(ct);
        return saved;
    }

    /// <summary>
    /// 🔴 <b>PUBLISH — the moment a proposal becomes a promise.</b> Copies the planned time onto the
    /// scheduled one, stamps it, and from then on the company's own page shows it and the planner
    /// treats it as pinned.
    /// </summary>
    /// <remarks>
    /// 🔒 It never re-publishes a company whose published time is unchanged, so pressing it twice
    /// does not restamp twenty-five rows — and the "notify" step downstream can therefore trust
    /// <see cref="GroupPhotoRegistration.SlotPublishedAt"/> to mean "this is news".
    /// </remarks>
    public async Task<GroupPhotoPublishResult> PublishAsync(
        int eventId, CancellationToken ct = default)
    {
        var registrations = await _db.GroupPhotoRegistrations
            .Where(r => r.EventId == eventId && r.PlannedAtUtc != null)
            .ToListAsync(ct);

        var now = _clock.GetUtcNow();
        int published = 0, unchanged = 0, skipped = 0;

        foreach (var r in registrations)
        {
            if (r.ScheduledAtUtc == r.PlannedAtUtc && r.SlotPublishedAt is not null)
            {
                unchanged++;
                continue;
            }

            // ⚠️ Moving a company that has ALREADY been told is a real act with a real consequence
            // (their colleagues hold the old time). It is allowed — plans change — but it is stamped
            // as a fresh publication so the notify step tells them it has moved.
            r.ScheduledAtUtc = r.PlannedAtUtc;
            r.SlotPublishedAt = now;
            published++;
        }

        await _db.SaveChangesAsync(ct);
        return new GroupPhotoPublishResult(published, unchanged, skipped);
    }

    // ------------------------------------------- what the event partner needs

    /// <summary>One line of the running order, as the coordinating partner reads it.</summary>
    public sealed record ScheduleRow(
        DateTimeOffset? StartUtc, string Day, string CompanyName, int People,
        string ContactName, string ContactEmail, string ContactMobile,
        string Location, string State);

    /// <summary>
    /// The running order for the person coordinating the photos from our side.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-11: <i>"i must be able to create excel list to my event partner who
    /// handles coordination from our side. incl invite in calender to them."</i></para>
    ///
    /// <para>🔑 <b>It includes the not-yet-scheduled and the not-yet-published rows, marked as
    /// such.</b> A list containing only the settled companies looks complete and is not — the
    /// partner would arrive on the day without the two that were still being argued about.</para>
    /// </remarks>
    public async Task<IReadOnlyList<ScheduleRow>> ScheduleAsync(
        int eventId, CancellationToken ct = default)
    {
        var registrations = await _db.GroupPhotoRegistrations
            .Where(r => r.EventId == eventId)
            .ToListAsync(ct);

        var preDay = await _db.Events.Where(e => e.Id == eventId)
            .Select(e => e.PreDayDate).FirstOrDefaultAsync(ct);

        // The coordinator's MOBILE lives on the volume-package row (the company typed it in their
        // own wizard). On the day, a phone number is the difference between "they are late" and
        // "we skip them", so it is worth the join.
        var mobiles = await MobilesAsync(eventId, ct);

        return registrations
            .Select(r =>
            {
                var when = r.ScheduledAtUtc ?? r.PlannedAtUtc;
                var day = when is null ? "—"
                    : preDay is { } p && DateOnly.FromDateTime(when.Value.UtcDateTime) == p
                        ? "Pre-day" : "Main day";

                var state =
                    r.ScheduledAtUtc is not null && r.SlotPublishedAt is not null ? "Published"
                    : r.PlannedAtUtc is not null ? "Planned — not yet published"
                    : "No slot yet";

                return new ScheduleRow(
                    when, day, r.CompanyName, r.TicketCount,
                    r.ContactName ?? string.Empty, r.ContactEmail ?? string.Empty,
                    Mobile(mobiles, r), r.Location ?? string.Empty, state);
            })
            // Unscheduled last: the partner reads this as a running order, and a blank time at the
            // top would break the one thing the list is for.
            .OrderBy(r => r.StartUtc ?? DateTimeOffset.MaxValue)
            .ThenBy(r => r.CompanyName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// ONE calendar file holding EVERY scheduled photo, for the coordinating partner.
    /// </summary>
    /// <remarks>
    /// 🔑 One file with many events, not twenty-five files: the partner imports once and their day
    /// is laid out. ⚠️ Each event keeps the SAME stable UID as the company's own invite
    /// (<c>group-photo-{eventId}-{registrationId}</c>), so when a slot moves and the file is
    /// re-imported, the entry UPDATES instead of leaving the old time behind next to the new one.
    /// 🔒 <c>METHOD:PUBLISH</c> and no ATTENDEE lines — this is a schedule to read, not an invitation
    /// that asks twenty-five companies to accept.
    /// </remarks>
    public async Task<string> ScheduleIcsAsync(int eventId, CancellationToken ct = default)
    {
        var registrations = await _db.GroupPhotoRegistrations
            .Where(r => r.EventId == eventId && r.ScheduledAtUtc != null)
            .OrderBy(r => r.ScheduledAtUtc)
            .ToListAsync(ct);

        var ev = await _db.Events.Where(e => e.Id == eventId)
            .Select(e => new { e.DisplayName, e.VenueName })
            .FirstOrDefaultAsync(ct);

        var mobiles = await MobilesAsync(eventId, ct);

        var sb = new System.Text.StringBuilder();
        sb.Append("BEGIN:VCALENDAR\r\nVERSION:2.0\r\n");
        sb.Append("PRODID:-//CommunityHub//GroupPhotos//EN\r\nMETHOD:PUBLISH\r\n");

        foreach (var r in registrations)
        {
            var start = r.ScheduledAtUtc!.Value;
            var end = start.AddMinutes(r.DurationMinutes);
            var where = string.IsNullOrWhiteSpace(r.Location) ? ev?.VenueName ?? string.Empty : r.Location!;

            sb.Append("BEGIN:VEVENT\r\n");
            sb.Append($"UID:group-photo-{eventId}-{r.Id}@communityhub\r\n");
            sb.Append($"DTSTAMP:{_clock.GetUtcNow().UtcDateTime:yyyyMMdd'T'HHmmss'Z'}\r\n");
            sb.Append($"DTSTART:{start.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}\r\n");
            sb.Append($"DTEND:{end.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}\r\n");
            sb.Append($"SUMMARY:{Escape($"Group photo - {r.CompanyName}")}\r\n");
            // The contact is IN the entry: on the day, the partner needs to reach the coordinator
            // from their phone, not from a spreadsheet on a laptop in another room.
            sb.Append($"DESCRIPTION:{Escape(
                $"{r.TicketCount} attendees. Contact: {r.ContactName} {r.ContactEmail} {Mobile(mobiles, r)}".Trim())}\r\n");
            if (where.Length > 0) sb.Append($"LOCATION:{Escape(where)}\r\n");
            sb.Append("END:VEVENT\r\n");
        }

        sb.Append("END:VCALENDAR\r\n");
        return sb.ToString();
    }

    /// <summary>Coordinator mobiles by volume-package company id (they typed it in their wizard).</summary>
    private async Task<Dictionary<int, string>> MobilesAsync(int eventId, CancellationToken ct) =>
        await _db.VolumePackageCompanies
            .Where(c => c.EventId == eventId && c.GroupPhotoContactMobile != null)
            .ToDictionaryAsync(c => c.Id, c => c.GroupPhotoContactMobile!, ct);

    private static string Mobile(Dictionary<int, string> mobiles, GroupPhotoRegistration r) =>
        r.VolumePackageCompanyId is { } id && mobiles.TryGetValue(id, out var m) ? m : string.Empty;

    /// <summary>RFC 5545 text escaping — a comma in a company name must not split a field.</summary>
    private static string Escape(string raw) => raw
        .Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,")
        .Replace("\r\n", "\\n").Replace("\n", "\\n");
}
