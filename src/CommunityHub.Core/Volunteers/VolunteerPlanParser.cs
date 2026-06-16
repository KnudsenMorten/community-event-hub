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

    public ParsedPlan Parse(string csv)
    {
        var records = SplitRecords(csv ?? string.Empty);
        var tasks = new List<ParsedPlanTask>();
        var buckets = new List<string>();

        bool headerSeen = false;
        foreach (var fields in records)
        {
            // Skip the header row (first record whose 5th column is the task-name header).
            if (!headerSeen)
            {
                headerSeen = true;
                if (Get(fields, ColTaskName).Equals("Task Name", StringComparison.OrdinalIgnoreCase))
                    continue;
                // No header present — fall through and treat this record as data.
            }

            var title = Get(fields, ColTaskName).Trim();
            if (title.Length == 0) continue; // blank separator rows

            var team = NullIfBlank(Get(fields, ColTeam));
            var bucket = team ?? UnassignedBucket;
            if (!buckets.Contains(bucket, StringComparer.OrdinalIgnoreCase))
                buckets.Add(bucket);

            tasks.Add(new ParsedPlanTask(
                BucketName: bucket,
                Title: title,
                TimeEnd: NullIfBlank(Get(fields, ColTimeEnd)),
                Status: ParseStatus(Get(fields, ColStatus)),
                Criticality: ParseCriticality(Get(fields, ColCriticality)),
                ResponsibleTeam: team,
                EldkLeadName: NullIfBlank(Get(fields, ColEldkLead)),
                ResourcesNeeded: ParseInt(Get(fields, ColResourcesNeeded)),
                ResourceNames: SplitNames(Get(fields, ColResourceNames)),
                Prerequisites: NullIfBlank(Get(fields, ColPrereq)),
                Expectations: NullIfBlank(Get(fields, ColExpectations))));
        }

        return new ParsedPlan(tasks, buckets);
    }

    // --- Field accessors / value parsing ------------------------------------

    private static string Get(IReadOnlyList<string> fields, int i)
        => i < fields.Count ? fields[i] : string.Empty;

    private static string? NullIfBlank(string s)
        => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

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
