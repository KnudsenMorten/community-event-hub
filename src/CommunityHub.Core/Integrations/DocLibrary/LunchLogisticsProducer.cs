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

    /// <summary>One person who signed up for a lunch.</summary>
    private sealed record Cover(int Id, string Name, string Email, string Role, string Diet, string Notes);

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
        var (preDay, sponsorBoothMembers) = await PreDayAsync(eventId, ct);

        // The main day has no lunch or breakfast sign-up at all, so its head-count is everybody who
        // is coming — his answer: "main day: everyone".
        var mainDayHeads = await LogisticsAudience
            .Countable(_db.Participants, eventId)
            .CountAsync(ct);

        var preDayHeads = preDay.Count + sponsorBoothMembers;

        return
        [
            // §3.5's "day1-preday" lunch: the pre-day sign-ups plus the sponsors' booth members.
            BuildLunch(
                LogisticsFileNames.Lunch(eventShortName, LogisticsDay.PreDay),
                "Lunch — pre-day", preDay, sponsorBoothMembers),

            BuildBreakfast(
                LogisticsFileNames.Breakfast(eventShortName, LogisticsDay.PreDay),
                "Breakfast — pre-day", preDayHeads,
                "Pre-day lunch sign-ups plus the sponsors' checked-in booth members."),

            BuildBreakfast(
                LogisticsFileNames.Breakfast(eventShortName, LogisticsDay.MainDay),
                "Breakfast — main day", mainDayHeads,
                "Every active participant in the edition — CEH holds no main-day sign-up."),
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

    /// <summary>Everyone who SAID they are eating the pre-day lunch, and the booth-member count.</summary>
    private async Task<(IReadOnlyList<Cover> Covers, int BoothMembers)> PreDayAsync(
        int eventId, CancellationToken ct)
    {
        var countable = LogisticsAudience.Countable(_db.Participants, eventId);

        // The sign-up IS the answer. No role rule, no ticket rule, no inference.
        var rows = await _db.LunchSignups
            .Where(l => l.EventId == eventId && l.LunchPreDay)
            .Join(countable, l => l.ParticipantId, p => p.Id, (l, p) => new
            {
                p.Id, p.FullName, p.Email, p.Role, l.Notes,
            })
            .ToListAsync(ct);

        var dietary = await _db.DietaryRequirements
            .Where(x => x.EventId == eventId && x.Surface == DietarySurface.SpeakerCatering)
            .ToListAsync(ct);
        var dietByPid = dietary.ToDictionary(x => x.ParticipantId);

        var covers = rows
            .Select(r =>
            {
                dietByPid.TryGetValue(r.Id, out var diet);
                return new Cover(
                    r.Id, r.FullName, r.Email, r.Role.ToString(),
                    Describe(diet),
                    Combine(diet?.OtherAllergens, r.Notes));
            })
            .OrderBy(c => c.Role, StringComparer.Ordinal)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ThenBy(c => c.Id)
            .ToList();

        // §298 — the sponsors' pre-day lunch, as a COUNT of booth members. The opt-out slot means
        // they are not there at all, so it must not contribute; a null count means "not stated".
        var boothMembers = await _db.SponsorInfos
            .Where(s => s.EventId == eventId
                        && s.BoothCheckInSlot != null
                        && s.BoothCheckInSlot != BoothCheckInSlots.NotParticipating)
            .SumAsync(s => (int?)s.BoothCheckInMemberCount ?? 0, ct);

        return (covers, boothMembers);
    }

    private static GeneratedFile BuildLunch(
        string fileName, string sheetName, IReadOnlyList<Cover> covers, int boothMembers)
    {
        using var wb = new XLWorkbook();

        var ws = wb.Worksheets.Add(sheetName.Length <= 31 ? sheetName : sheetName[..31]);
        Header(ws, "Name", "Email", "Role", "Diet / allergies", "Notes");
        var row = 2;
        foreach (var c in covers)
        {
            ws.Cell(row, 1).Value = c.Name;
            ws.Cell(row, 2).Value = c.Email;
            ws.Cell(row, 3).Value = c.Role;
            ws.Cell(row, 4).Value = c.Diet;
            ws.Cell(row, 5).Value = c.Notes;
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
        summary.Cell(srow, 1).Value = "Total covers";
        summary.Cell(srow, 2).Value = covers.Count + boothMembers;
        summary.Range(srow, 1, srow, 2).Style.Font.Bold = true;
        srow += 2;

        foreach (var g in covers
                     .Where(c => c.Diet.Length > 0)
                     .SelectMany(c => c.Diet.Split(", ", StringSplitOptions.RemoveEmptyEntries))
                     .GroupBy(x => x, StringComparer.Ordinal)
                     .OrderByDescending(g => g.Count())
                     .ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            summary.Cell(srow, 1).Value = g.Key;
            summary.Cell(srow, 2).Value = g.Count();
            srow++;
        }
        Finish(summary);

        var key = GeneratedFile.KeyOf(
            covers.Select(c => $"{c.Id}|{c.Name}|{c.Email}|{c.Role}|{c.Diet}|{c.Notes}")
                .Append($"booth:{boothMembers}"));

        // Named sign-ups PLUS booth members — the venue's own "Total covers" row. Reporting only the
        // named ones would under-cater the pre-day by exactly the booth staff.
        return new GeneratedFile(fileName, Save(wb), GeneratedFile.XlsxContentType, key,
            LogisticsHeadline.Of(covers.Count + boothMembers, "cover", "covers"));
    }

    private static string Describe(DietaryRequirement? diet)
    {
        if (diet is null) return string.Empty;
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(diet.DietChoice) && diet.DietChoice != "None")
            parts.Add(diet.DietChoice!);
        parts.AddRange(diet.Allergens().Where(a => a.IsSet).Select(a => a.Token));
        return string.Join(", ", parts);
    }

    private static string Combine(string? a, string? b) =>
        string.Join(" · ", new[] { a, b }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase));

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
