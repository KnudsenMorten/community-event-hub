using ClosedXML.Excel;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations.DocLibrary;

/// <summary>
/// §6.4 / §3.5 — the Bella Center FOOD files: the appreciation dinner and the party.
/// </summary>
/// <remarks>
/// <para>§3.5, verbatim: <i>"<b>Appreciation Dinner</b> counts accompanying guests and lists
/// speakers with allergies by name, with their comments. <b>Party</b> counts from sign-ups."</i></para>
///
/// <para>🔒 <b>Named people, not just numbers, and that is the requirement.</b> A caterer needs the
/// head-count to cook; the event needs the NAMES to seat people and to walk a plate to the person
/// who cannot eat what everyone else is having. An allergy total with no name is a number nobody can
/// act on at the table.</para>
///
/// <para>⚠️ <b>Breakfast and lunch are NOT here yet</b> — §3.5 names those four files but not who is
/// counted in them, and a wrong head-count to a venue is money. That question is open (§770.6);
/// building it on a guess is the one thing that would make these files worse than no files.</para>
/// </remarks>
public sealed class FoodLogisticsProducer
{
    private readonly CommunityHubDbContext _db;

    public FoodLogisticsProducer(CommunityHubDbContext db) => _db = db;

    /// <summary>The appreciation-dinner and party files.</summary>
    public async Task<IReadOnlyList<GeneratedFile>> BuildAllAsync(
        int eventId, string eventShortName, CancellationToken ct = default)
    {
        return
        [
            await BuildDinnerAsync(eventId, eventShortName, ct),
            await BuildPartyAsync(eventId, eventShortName, ct),
        ];
    }

    // ===================================================================
    //  APPRECIATION DINNER
    // ===================================================================

    private async Task<GeneratedFile> BuildDinnerAsync(
        int eventId, string eventShortName, CancellationToken ct)
    {
        var countable = LogisticsAudience.Countable(_db.Participants, eventId);

        var rows = await _db.DinnerSignups
            .Where(d => d.EventId == eventId && d.Attending)
            .Join(countable, d => d.ParticipantId, p => p.Id, (d, p) => new
            {
                p.Id, p.FullName, p.Email, p.Role,
                d.PlusOne, d.PlusOneCount, d.DietaryPreference, d.AllergyNotes,
            })
            .ToListAsync(ct);

        // The structured dietary capture for THIS occasion (§21 [H]) — the free-text AllergyNotes on
        // the signup and the structured row are different surfaces and both matter.
        var dietary = await _db.DietaryRequirements
            .Where(x => x.EventId == eventId && x.Surface == DietarySurface.Dinner)
            .ToListAsync(ct);
        var dietaryByPid = dietary.ToDictionary(x => x.ParticipantId);

        var people = rows
            .OrderBy(r => r.FullName, StringComparer.Ordinal)
            .ThenBy(r => r.Id)
            .Select(r =>
            {
                dietaryByPid.TryGetValue(r.Id, out var diet);
                // §3.5: accompanying guests are COUNTED. PlusOneCount is the number; PlusOne alone
                // (a legacy tick with no number) means exactly one.
                var guests = r.PlusOneCount > 0 ? r.PlusOneCount : (r.PlusOne ? 1 : 0);
                return new
                {
                    r.Id, r.FullName, r.Email, r.Role,
                    Guests = guests,
                    Diet = Describe(diet, r.DietaryPreference),
                    Notes = Combine(diet?.OtherAllergens, r.AllergyNotes),
                };
            })
            .ToList();

        using var wb = new XLWorkbook();

        // Sheet 1 — the table plan: every attending person, their guests, and what they cannot eat.
        var ws = wb.Worksheets.Add("Dinner attendees");
        Header(ws, "Name", "Email", "Role", "Guests", "Total seats", "Diet / allergies", "Comments");
        var row = 2;
        foreach (var p in people)
        {
            ws.Cell(row, 1).Value = p.FullName;
            ws.Cell(row, 2).Value = p.Email;
            ws.Cell(row, 3).Value = p.Role.ToString();
            ws.Cell(row, 4).Value = p.Guests;
            ws.Cell(row, 5).Value = 1 + p.Guests;      // the person plus their guests
            ws.Cell(row, 6).Value = p.Diet;
            ws.Cell(row, 7).Value = p.Notes;
            row++;
        }
        Total(ws, people.Count, row, 1, 5);
        Finish(ws);

        // Sheet 2 — ONLY the people with something to accommodate. The kitchen works from this one,
        // and it must not be a filter somebody has to remember to apply.
        var allergy = wb.Worksheets.Add("Allergies by name");
        Header(allergy, "Name", "Role", "Diet / allergies", "Comments");
        var arow = 2;
        foreach (var p in people.Where(x => x.Diet.Length > 0 || x.Notes.Length > 0))
        {
            allergy.Cell(arow, 1).Value = p.FullName;
            allergy.Cell(arow, 2).Value = p.Role.ToString();
            allergy.Cell(arow, 3).Value = p.Diet;
            allergy.Cell(arow, 4).Value = p.Notes;
            arow++;
        }
        Finish(allergy);

        var key = GeneratedFile.KeyOf(people.Select(p =>
            $"{p.Id}|{p.FullName}|{p.Email}|{p.Role}|{p.Guests}|{p.Diet}|{p.Notes}"));

        // ⚠️ SEATS, not people. The venue lays a place per seat, and somebody bringing a guest needs
        // two — the same under-count the sheet's own total row already guards against. The headline
        // has to agree with the sheet, or the organizer has two numbers and no way to pick.
        return new GeneratedFile(
            LogisticsFileNames.AppreciationDinner(eventShortName),
            Save(wb), GeneratedFile.XlsxContentType, key,
            LogisticsHeadline.Of(people.Sum(p => 1 + p.Guests), "seat", "seats"));
    }

    // ===================================================================
    //  PARTY
    // ===================================================================

    /// <remarks>
    /// ⚠️ <b>The party is the one count where "inactive is not counted" cannot be applied whole.</b>
    /// A party RSVP may be ANONYMOUS — a public sign-up with a name and an email and no participant
    /// row at all (§209). Those people are coming and must be fed; there is no active flag to test.
    /// So: a LINKED rsvp counts only when its participant is countable, and an UNLINKED one counts
    /// on its own. The two are also reported separately, because "who are these 40 people?" is a
    /// question somebody will ask.
    /// </remarks>
    private async Task<GeneratedFile> BuildPartyAsync(
        int eventId, string eventShortName, CancellationToken ct)
    {
        var countableIds = await LogisticsAudience
            .Countable(_db.Participants, eventId)
            .Select(p => p.Id)
            .ToListAsync(ct);
        var countable = countableIds.ToHashSet();

        var rsvps = await _db.PartyRsvps
            .Where(r => r.EventId == eventId && r.Attending)
            .Select(r => new { r.Id, r.Name, r.Email, r.HeadCount, r.ParticipantId })
            .ToListAsync(ct);

        var rows = rsvps
            .Where(r => r.ParticipantId is null || countable.Contains(r.ParticipantId.Value))
            .OrderBy(r => r.Name, StringComparer.Ordinal)
            .ThenBy(r => r.Id)
            .Select(r => new
            {
                r.Name,
                r.Email,
                // A blank head-count means the person themselves — an RSVP that says "yes" is at
                // least one seat. Treating null as 0 would under-cater by exactly the people who
                // did not fill in a number.
                Seats = r.HeadCount is > 0 ? r.HeadCount.Value : 1,
                Kind = r.ParticipantId is null ? "Public sign-up" : "Hub participant",
            })
            .ToList();

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Party sign-ups");
        Header(ws, "Name", "Email", "Seats", "Source");
        var row = 2;
        foreach (var r in rows)
        {
            ws.Cell(row, 1).Value = r.Name;
            ws.Cell(row, 2).Value = r.Email;
            ws.Cell(row, 3).Value = r.Seats;
            ws.Cell(row, 4).Value = r.Kind;
            row++;
        }
        Total(ws, rows.Count, row, 1, 3);
        Finish(ws);

        var key = GeneratedFile.KeyOf(rows.Select(r => $"{r.Name}|{r.Email}|{r.Seats}|{r.Kind}"));

        return new GeneratedFile(
            LogisticsFileNames.Party(eventShortName), Save(wb), GeneratedFile.XlsxContentType, key,
            LogisticsHeadline.Of(rows.Sum(r => r.Seats), "seat", "seats"));
    }

    // ===================================================================

    /// <summary>The structured diet + allergens, else whatever free text the signup carried.</summary>
    private static string Describe(DietaryRequirement? diet, string? signupPreference)
    {
        if (diet is null)
            return (signupPreference ?? string.Empty).Trim();

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(diet.DietChoice) && diet.DietChoice != "None")
            parts.Add(diet.DietChoice!);

        parts.AddRange(diet.Allergens().Where(a => a.IsSet).Select(a => a.Token));

        if (parts.Count == 0 && !string.IsNullOrWhiteSpace(signupPreference))
            parts.Add(signupPreference.Trim());

        return string.Join(", ", parts);
    }

    private static string Combine(string? a, string? b)
    {
        var parts = new[] { a, b }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return string.Join(" · ", parts);
    }

    private static void Header(IXLWorksheet ws, params string[] headers)
    {
        for (var i = 0; i < headers.Length; i++) ws.Cell(1, i + 1).Value = headers[i];
        ws.Range(1, 1, 1, headers.Length).Style.Font.Bold = true;
    }

    private static void Total(IXLWorksheet ws, int dataCount, int row, int labelCol, int sumCol)
    {
        if (dataCount == 0) return;
        ws.Cell(row, labelCol).Value = "Grand Total";
        ws.Cell(row, sumCol).FormulaA1 =
            $"SUM({ws.Cell(2, sumCol).Address.ColumnLetter}2:{ws.Cell(row - 1, sumCol).Address.ColumnLetter}{row - 1})";
        ws.Range(row, labelCol, row, sumCol).Style.Font.Bold = true;
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
