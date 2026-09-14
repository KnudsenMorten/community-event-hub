using ClosedXML.Excel;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations.DocLibrary;

/// <summary>
/// §6.4 / §3.5 — the LUNCH files, read from the sign-up tables CEH already holds.
/// </summary>
/// <remarks>
/// <para>🔒 <b>Operator 2026-08-02: <i>"data is in the table for the different sign up. so
/// calculation service must not add another logic … fx party sign up, lunch, appreciation dinner …
/// we have data already in ceh"</i>.</b> He is right, and the first version of this file proved why
/// the instruction was needed: it DERIVED who eats lunch from roles and ticket classes. CEH already
/// answers that question directly — <see cref="LunchSignup"/> has a flag per day, and people filled
/// it in. A derived head-count competes with the sign-up instead of reporting it.</para>
///
/// <para><b>What the tables say, and this producer only reports:</b></para>
/// <list type="bullet">
/// <item><see cref="LunchSignup"/> — one row per participant with a flag for the early setup day,
///   the setup day and the pre-day. Its own summary states the audience: <i>"Collected for all roles
///   EXCEPT Attendees and Sponsors — they have their own catering arrangement."</i></item>
/// <item><see cref="SponsorInfo.BoothCheckInMemberCount"/> — §298, verbatim: <i>"how many booth
///   members will check in on the pre-day. Feeds the organizer pre-day LUNCH headcount (each
///   checked-in booth member eats the pre-day lunch)."</i> That is the sponsors' lunch answer, and
///   it is a COUNT of booth members, not a person per row.</item>
/// <item>🔴 <b>§1086 — <see cref="Attendee.TicketStatus"/>.</b> Operator 2026-08-14: <i>"logistics
///   lunch preday must contain any attendees that booked a 2-day ticket. i think it is missing in
///   the calculation and excel export"</i>. It was. The <see cref="LunchSignup"/> summary quoted
///   above says the sign-up is collected for every role EXCEPT attendees and sponsors — so for those
///   two there is no row to report, and this producer reported nothing for them. Sponsors had their
///   answer (the booth count); <b>attendees had none at all</b>, and the pre-day is the day the
///   Master Class fills the room.
///   <para>⚠️ This is not the derived logic the 2026-08-02 instruction removed. The data IS already
///   in CEH — the TICKET, synced from the stable Backstage <c>ticket_class_id</c>. A 2-day ticket is
///   a sign-up made in the webshop instead of the hub. The rule now lives once in
///   <see cref="LogisticsAudience.PreDayAttendees"/>, shared with <c>/Organizer/Lunch</c>, which has
///   counted them since §326bv — the file was simply never brought along.</para></item>
/// </list>
///
/// <para>⚠️ <b>BREAKFAST is not built and cannot be, yet.</b> §3.5 names two breakfast files, and
/// CEH has <b>no breakfast sign-up at all</b> — no table, no field, nothing to report. Producing
/// them would mean inventing exactly the logic this class was rewritten to remove. It needs either a
/// sign-up (like lunch has) or an explicit rule from him. Recorded, not guessed (§770.7).</para>
/// </remarks>
public sealed class LunchLogisticsProducer
{
    private readonly CommunityHubDbContext _db;

    public LunchLogisticsProducer(CommunityHubDbContext db) => _db = db;

    /// <summary>
    /// One named person on a lunch sheet.
    /// </summary>
    /// <remarks>
    /// 🔒 <b>§1086b — NO DIET ON A LUNCH SHEET.</b> Operator 2026-08-14: <i>"we do not support diets
    /// in the onboarding regarding lunch. we handle this back2back in the ordering process so we have
    /// different options. we must not include this in the dialog in ceh except for the appreciation
    /// dinner, where we already have this as a required field."</i>
    /// <para>⚠️ The column that used to be here read <c>DietarySurface.SpeakerCatering</c> — a
    /// surface <b>nothing in CEH has ever written</b> (only <c>DietarySurface.Dinner</c> is captured,
    /// on the Dinner form). So it published an always-empty "Diet / allergies" column to the venue,
    /// which reads as "nobody here has a dietary need" rather than as "we do not collect this" — the
    /// worse of the two, because a caterer can act on it.</para>
    /// </remarks>
    private sealed record Cover(int Id, string Name, string Email, string Role, string Notes);

    /// <summary>
    /// §770.8 — the share of a head-count that actually eats the venue breakfast.
    /// </summary>
    /// <remarks>
    /// 🔒 <b>Operator 2026-08-02: <i>"come up with breakfast calculation formula. use 75% of people
    /// as … eat breakfast"</i></b>, and the reason, from him a moment later: <i>"some [sleep] at
    /// hotels"</i> — a hotel guest eats breakfast at their hotel, not at the venue. There is no
    /// breakfast sign-up in CEH, so this is a factor rather than a count, and it is a SETTING so he
    /// can move it after the first morning without a deploy.
    ///
    /// <para>⚠️ It is an estimate and the file says so on its face. CEH does hold
    /// <c>HotelBooking</c> rows, so a later version could subtract known hotel guests instead of
    /// estimating — noted in REQUIREMENTS rather than done unasked.</para>
    /// </remarks>
    public const double BreakfastFactor = 0.75;

    /// <summary>The lunch and breakfast files §3.5 asks for.</summary>
    public async Task<IReadOnlyList<GeneratedFile>> BuildAllAsync(
        int eventId, string eventShortName, CancellationToken ct = default)
    {
        // 🔴 §1086 — THE NUMBERS COME FROM THE ONE ENGINE (operator 2026-08-14: *"i need to trust
        // numbers and have logic consistent across"*). This file, /Organizer/Lunch and the dashboard
        // tile each used to reach the head-count their own way and reached three different answers.
        // What stays here is turning those numbers into a WORKBOOK.
        var counts = await new LunchHeadcountService(_db).ComputeAsync(eventId, ct);
        var preDay = await PreDayCoversAsync(eventId, ct);

        // ⚠️ The named list and the engine must agree by CONSTRUCTION, not by coincidence: both ask
        // LunchHeadcountService which participants are pre-day heads.
        var preDayHeads = counts.PreDay;
        var mainDayHeads = counts.MainDay;

        return
        [
            // §3.5's "day1-preday" lunch: the pre-day heads (crew + those who declared), the
            // sponsors' booth members, and (§1086) the 2-day ticket holders in the Master Class.
            BuildLunch(
                LogisticsFileNames.Lunch(eventShortName, LogisticsDay.PreDay),
                "Lunch — pre-day", preDay,
                counts.PreDaySponsorBoothMembers, counts.PreDayTwoDayAttendees),

            // §1086b — the MAIN-DAY lunch, named crew + the attendee count. The file name has been
            // supported since §3.5; nothing produced it until now, so the event's biggest catering
            // order existed only on screen.
            BuildMainDayLunch(
                LogisticsFileNames.Lunch(eventShortName, LogisticsDay.MainDay),
                "Lunch — main day", await MainDayCoversAsync(eventId, ct), counts.MainDayAttendees),

            BuildBreakfast(
                LogisticsFileNames.Breakfast(eventShortName, LogisticsDay.PreDay),
                "Breakfast — pre-day", preDayHeads,
                "Pre-day lunch sign-ups, the sponsors' checked-in booth members, and the attendees "
                + "holding a 2-day (Master Class) ticket."),

            BuildBreakfast(
                LogisticsFileNames.Breakfast(eventShortName, LogisticsDay.MainDay),
                "Breakfast — main day", mainDayHeads,
                "Every active participant in the edition plus every attendee holding a ticket — "
                + "CEH holds no main-day sign-up."),
        ];
    }

    /// <summary>
    /// §770.8 — breakfast is an ESTIMATE, and the file is written so nobody can mistake it for a
    /// sign-up list.
    /// </summary>
    /// <remarks>
    /// 🔒 There are no names on this sheet, deliberately. A per-person list would look like a
    /// register of who is eating, and CEH does not know that — nobody was asked. Publishing invented
    /// names to a venue is worse than publishing an honest number.
    /// </remarks>
    private static GeneratedFile BuildBreakfast(
        string fileName, string sheetName, int headCount, string baseExplanation)
    {
        var covers = (int)Math.Ceiling(headCount * BreakfastFactor);

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(sheetName.Length <= 31 ? sheetName : sheetName[..31]);

        Header(ws, "Figure", "Value");
        ws.Cell(2, 1).Value = "People expected on this day";
        ws.Cell(2, 2).Value = headCount;
        ws.Cell(3, 1).Value = "Share expected to eat breakfast at the venue";
        ws.Cell(3, 2).Value = $"{BreakfastFactor:P0}";
        ws.Cell(4, 1).Value = "Breakfast covers";
        ws.Cell(4, 2).Value = covers;
        ws.Range(4, 1, 4, 2).Style.Font.Bold = true;

        ws.Cell(6, 1).Value = "How this is worked out";
        ws.Cell(6, 1).Style.Font.Bold = true;
        ws.Cell(7, 1).Value = baseExplanation;
        ws.Cell(8, 1).Value =
            $"There is no breakfast sign-up, so this is an ESTIMATE: {BreakfastFactor:P0} of the "
            + "people expected. The rest are assumed to have breakfast at their hotel.";
        Finish(ws);

        // 🔑 The key is the FIGURES, so the file is "changed" when the head-count moves and not when
        // a workbook is rebuilt.
        var key = GeneratedFile.KeyOf([$"{headCount}", $"{BreakfastFactor}", $"{covers}"]);

        // 🔒 Labelled an ESTIMATE on the page as well as inside the sheet. There IS no breakfast
        // sign-up (§770.8) — this is a stated formula, and the venue bills against it. An organizer
        // who reads it as a register of people will not chase the sign-up that does not exist.
        return new GeneratedFile(fileName, Save(wb), GeneratedFile.XlsxContentType, key,
            LogisticsHeadline.Estimate(covers, "cover", "covers"));
    }

    /// <summary>
    /// The pre-day heads this file can NAME — with their diet, which is the only part of a name the
    /// caterer actually uses.
    /// </summary>
    /// <remarks>
    /// 🔴 §1086 — <b>the people are chosen by <see cref="LunchHeadcountService"/>, not by this
    /// query.</b> Two bugs lived in the version that picked them here:
    /// <list type="bullet">
    ///   <item>The always-on-site CREW were left out. They are never shown a pre-day checkbox
    ///     (<see cref="LunchAudience.PreDayAutoCountedRole"/>), so their stored <c>false</c> was
    ///     read as "not eating" — the venue was short every organizer, media and partner.</item>
    ///   <item>A SPONSOR who ticked the box was counted TWICE: once as a named cover here, and
    ///     again inside their company's booth-member count.</item>
    /// </list>
    /// </remarks>
    private async Task<IReadOnlyList<Cover>> PreDayCoversAsync(int eventId, CancellationToken ct)
    {
        var headIds = await new LunchHeadcountService(_db).PreDayNamedIdsAsync(eventId, ct);

        var rows = await LogisticsAudience.Countable(_db.Participants, eventId)
            .Where(p => headIds.Contains(p.Id))
            .Select(p => new { p.Id, p.FullName, p.Email, p.Role })
            .ToListAsync(ct);

        // The sign-up's free-text note, where there is one — the crew who were auto-counted never
        // filled a form, so most have none.
        var notesByPid = await _db.LunchSignups
            .Where(l => l.EventId == eventId && l.Notes != null)
            .Select(l => new { l.ParticipantId, l.Notes })
            .ToDictionaryAsync(x => x.ParticipantId, x => x.Notes, ct);

        return rows
            .Select(r =>
            {
                notesByPid.TryGetValue(r.Id, out var notes);
                return new Cover(r.Id, r.FullName, r.Email, r.Role.ToString(), notes ?? string.Empty);
            })
            .OrderBy(c => c.Role, StringComparer.Ordinal)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ThenBy(c => c.Id)
            .ToList();
    }

    /// <summary>
    /// §1086b — the MAIN-DAY named crew. Operator 2026-08-14: <i>"make the main day lunch excel with
    /// named crew"</i>.
    /// </summary>
    /// <remarks>
    /// 🔑 <b>There is no sign-up to read, and that is the point.</b> Main-day lunch is ordered for
    /// everyone on site (§326h — <i>"it is ordered for everyone"</i>), so the roll IS the list: every
    /// countable participant, no checkbox. Attendees are the other half of the number and stay a
    /// COUNT — he asked for named CREW.
    /// </remarks>
    private async Task<IReadOnlyList<Cover>> MainDayCoversAsync(int eventId, CancellationToken ct)
    {
        var rows = await LogisticsAudience.Countable(_db.Participants, eventId)
            .Select(p => new { p.Id, p.FullName, p.Email, p.Role })
            .ToListAsync(ct);

        return rows
            .Select(r => new Cover(r.Id, r.FullName, r.Email, r.Role.ToString(), string.Empty))
            .OrderBy(c => c.Role, StringComparer.Ordinal)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ThenBy(c => c.Id)
            .ToList();
    }

    /// <param name="twoDayAttendees">
    /// §1086 — the Master Class audience, as a COUNT. 🔑 <b>Deliberately not one named row each</b>,
    /// for the same reason the booth members are not: the venue needs covers, and adding hundreds of
    /// attendee identities to a spreadsheet that leaves the organisation buys nothing a caterer can
    /// act on. The CREW are named because the organizer works from that list on the day.
    /// </param>
    private static GeneratedFile BuildLunch(
        string fileName, string sheetName, IReadOnlyList<Cover> covers, int boothMembers,
        int twoDayAttendees)
    {
        using var wb = new XLWorkbook();

        var ws = wb.Worksheets.Add(sheetName.Length <= 31 ? sheetName : sheetName[..31]);
        // §1086b — NO DIET COLUMN: lunch options are agreed with the venue in the ordering process,
        // not collected in CEH (see the Cover record).
        Header(ws, "Name", "Email", "Role", "Notes");
        var row = 2;
        foreach (var c in covers)
        {
            ws.Cell(row, 1).Value = c.Name;
            ws.Cell(row, 2).Value = c.Email;
            ws.Cell(row, 3).Value = c.Role;
            ws.Cell(row, 4).Value = c.Notes;
            row++;
        }
        Finish(ws);

        // The venue's number. Booth members are a separate line because they are a COUNT from the
        // sponsor check-in, not named people — showing them merged would imply a name list we do
        // not have, and showing them not at all would under-cater the pre-day by that many.
        var summary = wb.Worksheets.Add("Catering summary");
        Header(summary, "Requirement", "Covers");
        var srow = 2;
        summary.Cell(srow, 1).Value = "Signed-up covers (named)";
        summary.Cell(srow, 2).Value = covers.Count;
        srow++;
        summary.Cell(srow, 1).Value = "Sponsor booth members (from booth check-in)";
        summary.Cell(srow, 2).Value = boothMembers;
        srow++;
        // §1086 — the Master Class audience. Its own line, like the booth members: it is a count
        // from the ticket, not a list of people who answered a sign-up, and merging it into the
        // named total would misrepresent where the number came from.
        summary.Cell(srow, 1).Value = "Attendees with a 2-day ticket (Master Class)";
        summary.Cell(srow, 2).Value = twoDayAttendees;
        srow++;
        summary.Cell(srow, 1).Value = "Total covers";
        summary.Cell(srow, 2).Value = covers.Count + boothMembers + twoDayAttendees;
        summary.Range(srow, 1, srow, 2).Style.Font.Bold = true;
        srow++;
        summary.Cell(srow, 1).Value =
            "The pre-day is the Master Class day: every 2-day ticket holder is on site and eats. "
            + "Attendees are counted from their ticket — they are never asked to sign up for lunch.";
        srow++;
        // 🔒 §1086b — SAY THAT DIETS ARE NOT HERE. The sheet used to carry a diet column fed by a
        // surface nothing writes, so it published an empty column that reads as "no dietary needs".
        // Naming the real process is the honest version: lunch options are agreed with the venue.
        summary.Cell(srow, 1).Value =
            "Dietary options for lunch are agreed with the venue in the ordering process and are "
            + "not collected in the hub. (The Appreciation Dinner does capture them, on its own form.)";
        Finish(summary);

        // 🔑 The attendee count is part of the CHANGE KEY: a ticket sold on Tuesday must make this
        // file "changed" on Tuesday, or the venue keeps the old order.
        var key = GeneratedFile.KeyOf(
            covers.Select(c => $"{c.Id}|{c.Name}|{c.Email}|{c.Role}|{c.Notes}")
                .Append($"booth:{boothMembers}")
                .Append($"attendees2d:{twoDayAttendees}"));

        // Named sign-ups PLUS booth members PLUS the Master Class audience — the venue's own "Total
        // covers" row. Reporting only the named ones under-catered the pre-day by the booth staff
        // and (§1086) by every paying attendee in the room.
        return new GeneratedFile(fileName, Save(wb), GeneratedFile.XlsxContentType, key,
            LogisticsHeadline.Of(covers.Count + boothMembers + twoDayAttendees, "cover", "covers"));
    }

    /// <summary>
    /// §1086b — the MAIN-DAY lunch file: <b>named crew</b> plus the attendee count.
    /// </summary>
    /// <remarks>
    /// <para>🔴 <b>Why it did not exist.</b> <see cref="LogisticsFileNames.Lunch"/> has always
    /// supported <c>day2-mainday</c> and nothing ever produced it, so the biggest catering order of
    /// the event lived only on an organizer screen. Operator 2026-08-14: <i>"make the main day lunch
    /// excel with named crew"</i>.</para>
    ///
    /// <para>🔑 <b>There is no sign-up sheet here and there should not be</b> (§326h — main-day lunch
    /// <i>"is ordered for everyone"</i>). So the named list is the roll of people on site, and the
    /// sheet says so rather than implying everybody answered something.</para>
    /// </remarks>
    private static GeneratedFile BuildMainDayLunch(
        string fileName, string sheetName, IReadOnlyList<Cover> crew, int attendees)
    {
        using var wb = new XLWorkbook();

        var ws = wb.Worksheets.Add(sheetName.Length <= 31 ? sheetName : sheetName[..31]);
        Header(ws, "Name", "Email", "Role");
        var row = 2;
        foreach (var c in crew)
        {
            ws.Cell(row, 1).Value = c.Name;
            ws.Cell(row, 2).Value = c.Email;
            ws.Cell(row, 3).Value = c.Role;
            row++;
        }
        Finish(ws);

        var summary = wb.Worksheets.Add("Catering summary");
        Header(summary, "Requirement", "Covers");
        var srow = 2;
        summary.Cell(srow, 1).Value = "Crew, speakers, volunteers and sponsors (named)";
        summary.Cell(srow, 2).Value = crew.Count;
        srow++;
        summary.Cell(srow, 1).Value = "Attendees (every live ticket, 1-day and 2-day)";
        summary.Cell(srow, 2).Value = attendees;
        srow++;
        summary.Cell(srow, 1).Value = "Total covers";
        summary.Cell(srow, 2).Value = crew.Count + attendees;
        summary.Range(srow, 1, srow, 2).Style.Font.Bold = true;
        srow += 2;
        summary.Cell(srow, 1).Value =
            "Main-day lunch is ordered for everyone on site, so there is no sign-up: the list is "
            + "every active participant, and the attendee line is every ticket still held.";
        srow++;
        summary.Cell(srow, 1).Value =
            "Dietary options for lunch are agreed with the venue in the ordering process and are "
            + "not collected in the hub. (The Appreciation Dinner does capture them, on its own form.)";
        Finish(summary);

        var key = GeneratedFile.KeyOf(
            crew.Select(c => $"{c.Id}|{c.Name}|{c.Email}|{c.Role}")
                .Append($"attendees:{attendees}"));

        return new GeneratedFile(fileName, Save(wb), GeneratedFile.XlsxContentType, key,
            LogisticsHeadline.Of(crew.Count + attendees, "cover", "covers"));
    }

    // 🗑 §1086b — the diet formatter and the diet/notes combiner went with the column they fed.
    // Lunch options are agreed with the venue in the ordering process; the only place CEH captures
    // dietary requirements is the Appreciation Dinner, and FoodLogisticsProducer serves that file
    // from DietarySurface.Dinner. Leaving the helpers here would be plumbing waiting to be re-used
    // by a future lunch sheet that must not have it.

    private static void Header(IXLWorksheet ws, params string[] headers)
    {
        for (var i = 0; i < headers.Length; i++) ws.Cell(1, i + 1).Value = headers[i];
        ws.Range(1, 1, 1, headers.Length).Style.Font.Bold = true;
    }

    private static void Finish(IXLWorksheet ws)
    {
        ws.Columns().AdjustToContents();
        ws.SheetView.FreezeRows(1);
    }

    private static byte[] Save(XLWorkbook wb)
    {
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
