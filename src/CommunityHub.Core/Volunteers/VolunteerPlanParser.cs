using System.Text;
using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Volunteers;

/// <summary>
/// One parsed task row from the volunteer plan CSV, plus the Bucket it belongs to.
/// Pure data — no EF, no DB. <see cref="VolunteerPlanImportService"/> turns these
/// into <see cref="VolunteerCategory"/> (Bucket) + <see cref="VolunteerTask"/> rows.
/// </summary>
public sealed record ParsedPlanTask(
    string BucketName,
    string Title,
    /// <summary>§327k — the calendar date the work happens (CSV "Date", dd-MM-yyyy). Was
    /// DROPPED by the parser: a run plan whose tasks have no date cannot be worked from.</summary>
    DateOnly? Date,
    /// <summary>§327k — the start time (CSV "Time Start"), stored as VolunteerTask.Shift.
    /// Also dropped previously. Together with <see cref="Date"/> this is what makes two rows
    /// of the same task DIFFERENT shifts rather than a duplicate.</summary>
    string? Shift,
    string? TimeEnd,
    VolunteerTaskStatus Status,
    VolunteerTaskCriticality Criticality,
    string? ResponsibleTeam,
    string? EldkLeadName,
    int ResourcesNeeded,
    IReadOnlyList<string> ResourceNames,
    string? Prerequisites,
    string? Expectations);

/// <summary>The result of parsing a plan CSV: the tasks plus the distinct bucket
/// names (in first-seen order) so the importer can create buckets up front.</summary>
public sealed record ParsedPlan(
    IReadOnlyList<ParsedPlanTask> Tasks,
    IReadOnlyList<string> Buckets);

/// <summary>
/// Parses the real ELDK volunteer plan CSV into <see cref="ParsedPlanTask"/> rows.
///
/// The file is semicolon-delimited with the header:
///   T-day;Date;Time Start;Time End;Task Name;Status;Criticality;
///   Responsible Team;ELDK Lead Task;Resources Needed;Resource Names;Pre-req;Expectations
///
/// The spreadsheet groups tasks into colored section bands which the CSV loses, so
/// the <b>Bucket</b> is derived from the <b>Responsible Team</b> column — the
/// strongest grouping signal that survives the export (e.g. "BeFree", "Photo",
/// "BC-F&amp;B"). Rows with a blank team fall into an "Unassigned" bucket.
///
/// Resource Names is a quoted, newline-separated list of people; the parser keeps
/// each name as a list entry (the importer matches them to participants by name).
/// This parser is RFC-4180-ish: it handles quoted fields containing the delimiter
/// and embedded newlines. It is pure (no I/O) so tests run it on a FAKE-name
/// fixture, never the real file.
/// </summary>
public sealed class VolunteerPlanParser
{
    private const char Delimiter = ';';
    public const string UnassignedBucket = "Unassigned";

    // Column indices in the known header layout.
    private const int ColDate = 1;
    private const int ColTimeStart = 2;
    private const int ColTimeEnd = 3;
    private const int ColTaskName = 4;
    private const int ColStatus = 5;
    private const int ColCriticality = 6;
    private const int ColTeam = 7;
    private const int ColEldkLead = 8;
    private const int ColResourcesNeeded = 9;
    private const int ColResourceNames = 10;
    private const int ColPrereq = 11;
    private const int ColExpectations = 12;

    /// <summary>
    /// §327k — resolves each column by its HEADER NAME, falling back to the fixed layout above
    /// when that name is absent.
    ///
    /// <para><b>Why this exists.</b> The parser was purely positional, written for the ELDK26
    /// export which carried a <c>Status</c> column at index 5. The ELDK27 export has no such
    /// column, so every field from index 5 on shifted by one and the parser read the
    /// <i>ELDK Lead</i> column as the Responsible Team. It did not fail — it "succeeded",
    /// reporting buckets named after PEOPLE ("Kent Agerlund", "Martin Byskov"), and would have
    /// imported 127 tasks into them. A positional parser cannot tell a shifted file from a
    /// valid one; a header-driven one can.</para>
    /// </summary>
    private sealed class ColumnMap
    {
        private readonly Dictionary<string, int> _byName = new(StringComparer.OrdinalIgnoreCase);

        public ColumnMap(IReadOnlyList<string>? header)
        {
            if (header is null) return;
            for (var i = 0; i < header.Count; i++)
            {
                var name = header[i].Trim();
                if (name.Length > 0 && !_byName.ContainsKey(name)) _byName[name] = i;
            }
        }

        /// <summary>Index of <paramref name="name"/>, or <paramref name="fallback"/> when the
        /// file has no such header (a legacy headerless export).</summary>
        public int Of(string name, int fallback) =>
            _byName.TryGetValue(name, out var i) ? i : fallback;

        public bool Has(string name) => _byName.ContainsKey(name);
    }

    public ParsedPlan Parse(string csv)
    {
        var records = SplitRecords(csv ?? string.Empty);
        var tasks = new List<ParsedPlanTask>();
        var buckets = new List<string>();

        // A header is present when ANY cell of the first record is the task-name header —
        // not just the cell at the ELDK26 position, which is the assumption that broke.
        IReadOnlyList<string>? header = null;
        var first = records.FirstOrDefault();
        if (first is not null
            && first.Any(f => f.Trim().Equals("Task Name", StringComparison.OrdinalIgnoreCase)))
        {
            header = first;
        }

        var map = new ColumnMap(header);
        int cDate = map.Of("Date", ColDate);
        int cTimeStart = map.Of("Time Start", ColTimeStart);
        int cTimeEnd = map.Of("Time End", ColTimeEnd);
        int cTaskName = map.Of("Task Name", ColTaskName);
        int cStatus = map.Of("Status", ColStatus);
        int cCriticality = map.Of("Criticality", ColCriticality);
        int cTeam = map.Of("Responsible Team", ColTeam);
        int cLead = map.Of("ELDK Lead Task", ColEldkLead);
        int cResources = map.Of("Resources Needed", ColResourcesNeeded);
        int cNames = map.Of("Resource Names", ColResourceNames);
        int cPrereq = map.Of("Pre-req", ColPrereq);
        int cExpect = map.Of("Expectations", ColExpectations);
        // No Status column ⇒ nothing has been done yet, so every task is Open.
        bool hasStatus = header is null || map.Has("Status");

        bool skippedHeader = false;
        foreach (var fields in records)
        {
            if (header is not null && !skippedHeader)
            {
                skippedHeader = true;
                continue;   // the header row itself
            }

            var title = Get(fields, cTaskName).Trim();
            if (title.Length == 0) continue; // blank separator rows

            var team = NullIfBlank(Get(fields, cTeam));
            var bucket = team ?? UnassignedBucket;
            if (!buckets.Contains(bucket, StringComparer.OrdinalIgnoreCase))
                buckets.Add(bucket);

            tasks.Add(new ParsedPlanTask(
                BucketName: bucket,
                Title: title,
                Date: ParseDate(Get(fields, cDate)),
                Shift: NullIfBlank(Get(fields, cTimeStart)),
                TimeEnd: NullIfBlank(Get(fields, cTimeEnd)),
                Status: hasStatus ? ParseStatus(Get(fields, cStatus)) : VolunteerTaskStatus.Open,
                Criticality: ParseCriticality(Get(fields, cCriticality)),
                ResponsibleTeam: team,
                EldkLeadName: NullIfBlank(Get(fields, cLead)),
                ResourcesNeeded: ParseInt(Get(fields, cResources)),
                ResourceNames: SplitNames(Get(fields, cNames)),
                Prerequisites: NullIfBlank(Get(fields, cPrereq)),
                Expectations: NullIfBlank(Get(fields, cExpect))));
        }

        return new ParsedPlan(tasks, buckets);
    }

    // --- Field accessors / value parsing ------------------------------------

    private static string Get(IReadOnlyList<string> fields, int i)
        => i < fields.Count ? fields[i] : string.Empty;

    private static string? NullIfBlank(string s)
        => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>
    /// §327k — parse the plan's date cell. The export is Danish <c>dd-MM-yyyy</c>; ISO is
    /// accepted too so a re-saved file still imports. Unparseable ⇒ null rather than a guess:
    /// a wrong date on a run-plan task is worse than a missing one, because nobody checks it.
    /// </summary>
    internal static DateOnly? ParseDate(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var v = s.Trim();
        string[] formats = { "dd-MM-yyyy", "d-M-yyyy", "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy" };
        return DateOnly.TryParseExact(
                   v, formats,
                   System.Globalization.CultureInfo.InvariantCulture,
                   System.Globalization.DateTimeStyles.None, out var d)
            ? d
            : null;
    }

    internal static int ParseInt(string s)
        => int.TryParse((s ?? string.Empty).Trim(), out var n) && n > 0 ? n : 0;

    internal static VolunteerTaskStatus ParseStatus(string s)
        => (s ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "completed" or "complete" or "done" => VolunteerTaskStatus.Done,
            "in progress" or "in-progress" or "ongoing" => VolunteerTaskStatus.InProgress,
            "cancelled" or "canceled" => VolunteerTaskStatus.Cancelled,
            _ => VolunteerTaskStatus.Open,
        };

    internal static VolunteerTaskCriticality ParseCriticality(string s)
        => (s ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "need-to-have" or "need to have" or "must" or "must-have" => VolunteerTaskCriticality.NeedToHave,
            "nice-to-have" or "nice to have" or "optional" => VolunteerTaskCriticality.NiceToHave,
            _ => VolunteerTaskCriticality.Unspecified,
        };

    /// <summary>Split a Resource Names cell (newline- and/or comma-separated) into
    /// trimmed, de-duplicated person names.</summary>
    internal static IReadOnlyList<string> SplitNames(string cell)
    {
        if (string.IsNullOrWhiteSpace(cell)) return Array.Empty<string>();
        return cell
            .Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(n => n.Trim())
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // --- CSV tokenizer (handles quotes + embedded delimiters/newlines) ------

    /// <summary>Split the whole file into records, each a list of fields. A record
    /// boundary is an unquoted newline; the delimiter is ';'. Doubled quotes ("")
    /// inside a quoted field are a literal quote.</summary>
    internal static List<List<string>> SplitRecords(string text)
    {
        // Strip a UTF-8 BOM if present.
        if (text.Length > 0 && text[0] == '﻿') text = text.Substring(1);

        var records = new List<List<string>>();
        var current = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false;
        bool sawAny = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    sawAny = true;
                    break;
                case Delimiter:
                    current.Add(field.ToString());
                    field.Clear();
                    sawAny = true;
                    break;
                case '\r':
                    break; // handled with \n
                case '\n':
                    current.Add(field.ToString());
                    field.Clear();
                    records.Add(current);
                    current = new List<string>();
                    sawAny = false;
                    break;
                default:
                    field.Append(c);
                    sawAny = true;
                    break;
            }
        }

        // Flush the final record (file may not end with a newline).
        if (sawAny || field.Length > 0 || current.Count > 0)
        {
            current.Add(field.ToString());
            records.Add(current);
        }

        return records;
    }
}
