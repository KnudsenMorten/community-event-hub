using ClosedXML.Excel;

namespace CommunityHub.Core.Integrations;

/// <summary>A speaker row read from a Sessionize Excel export or v2 view API.</summary>
public sealed record SessionizeSpeaker(
    string Email,
    string FirstName,
    string LastName,
    string? TagLine,
    string? Biography = null,
    string? Blog = null,
    string? LinkedIn = null,
    string? Twitter = null,
    string? ProfilePictureUrl = null,
    // The Sessionize speaker id (GUID) from the v2 view API. Used to link
    // sessions to their speaker(s) by id (the Sessionize session carries a
    // speakers id array). Empty for the Excel path, which has no speaker id.
    string SessionizeId = "");

/// <summary>The outcome of parsing a Sessionize Excel file.</summary>
public sealed record SessionizeParseResult(
    IReadOnlyList<SessionizeSpeaker> Speakers,
    IReadOnlyList<string> Warnings,
    string? Error);

/// <summary>
/// A session read from the Sessionize v2 view API (the <c>All</c>/<c>Sessions</c>
/// view, alongside speakers). <see cref="SpeakerIds"/> holds the Sessionize speaker
/// ids the session is delivered by; the session importer resolves each to the
/// participant the speaker import created.
/// </summary>
public sealed record SessionizeSession(
    string SessionizeId,
    string Title,
    string? Abstract,
    string? Room,
    string? Track,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    bool IsServiceSession,
    IReadOnlyList<string> SpeakerIds);

/// <summary>The outcome of parsing the Sessionize sessions view.</summary>
public sealed record SessionizeSessionsParseResult(
    IReadOnlyList<SessionizeSession> Sessions,
    IReadOnlyList<string> Warnings,
    string? Error);

/// <summary>
/// Parses a Sessionize speaker export (.xlsx) uploaded by an organizer
/// (CONTEXT.md / DESIGN_NOTES - Sessionize import is file-based, not an API).
///
/// Columns are located by HEADER NAME on the first row, case-insensitively,
/// so the export's column order does not matter and extra columns are ignored.
/// Recognised headers (any one of the listed aliases):
///   email      : "Email", "E-mail", "Email Address"
///   first name : "First Name", "FirstName", "Firstname"
///   last name  : "Last Name", "LastName", "Lastname", "Surname"
///   tag line   : "Tag Line", "TagLine", "Tagline", "Headline"  (optional)
///
/// A row with no email is skipped (a participant needs an email to log in)
/// and reported as a warning. The parser never throws for bad data - problems
/// come back in the result.
/// </summary>
public sealed class SessionizeExcelParser
{
    private static readonly string[] EmailHeaders =
        { "email", "e-mail", "email address" };
    private static readonly string[] FirstNameHeaders =
        { "first name", "firstname" };
    private static readonly string[] LastNameHeaders =
        { "last name", "lastname", "surname" };
    private static readonly string[] TagLineHeaders =
        { "tag line", "tagline", "headline",
          "speaker 1 tagline", "speaker 1 tag line" };
    private static readonly string[] BiographyHeaders =
        { "biography", "bio",
          "speaker 1 biography" };
    private static readonly string[] BlogHeaders =
        { "blog", "website",
          "speaker 1 blog" };
    private static readonly string[] LinkedInHeaders =
        { "linkedin",
          "speaker 1 linkedin" };
    private static readonly string[] TwitterHeaders =
        { "twitter", "twitter/x", "x", "x (twitter)",
          "speaker 1 twitter", "speaker 1 twitter/x", "speaker 1 x" };
    private static readonly string[] ProfilePictureHeaders =
        { "profile picture", "speaker 1 profile picture" };

    // Columns that the Sessionize export ships but the Hub does NOT consume,
    // either because the Hub form is the authoritative source (Country,
    // Gender, polo, first-time-speaker), because they belong to a different
    // form (Hotel, Lunch), or because they describe the OTHER speaker on a
    // joint session (every "Speaker 2 *" column). Listed here only as
    // documentation -- the parser ignores any unrecognised column by default,
    // so this array is not actively used at runtime.
    public static readonly string[] IgnoredColumns =
    {
        "speaker 1 country", "speaker 1 gender", "speaker 1 category",
        "speaker 1 is this your first time as speaker?",
        "speaker 1 polo size",
        "speaker 1 hotel requirement feb 24-25 (1 night)?",
        "speaker 1 hotel check-in date", "speaker 1 hotel check-out date",
        "speaker 1 hotel extra guest room",
        "speaker 1 participate in ask the expert (60 min)",
        "co-speaker will be joining session",
        "speaker 2 first name", "speaker 2 last name", "speaker 2 email",
        "speaker 2 blog", "speaker 2 linkedin", "speaker 2 twitter/x",
        "speaker 2 tagline", "speaker 2 biography",
        "speaker 2 country", "speaker 2 gender", "speaker 2 category",
        "speaker 2 polo size",
        "speaker 2 hotel requirement feb 24-25 (1 night)?",
        "speaker 2 hotel check-in date", "speaker 2 hotel check-out date",
        "speaker 2 hotel extra guest room",
        "speaker 2 participate in ask the expert (60 min)",
        "speaker 2 is this your first time as speaker?",
        "comments to eldk organizers",
    };

    /// <summary>
    /// Parse the speaker list from an uploaded Excel stream. Reads the first
    /// worksheet.
    /// </summary>
    public SessionizeParseResult Parse(Stream excelStream)
    {
        var speakers = new List<SessionizeSpeaker>();
        var warnings = new List<string>();

        XLWorkbook workbook;
        try
        {
            workbook = new XLWorkbook(excelStream);
        }
        catch (Exception ex)
        {
            return new SessionizeParseResult(
                speakers, warnings,
                $"Could not open the file as an Excel workbook: {ex.Message}");
        }

        using (workbook)
        {
            var sheet = workbook.Worksheets.FirstOrDefault();
            if (sheet is null)
            {
                return new SessionizeParseResult(
                    speakers, warnings, "The workbook has no worksheets.");
            }

            var headerRow = sheet.FirstRowUsed();
            if (headerRow is null)
            {
                return new SessionizeParseResult(
                    speakers, warnings, "The worksheet is empty.");
            }

            // Map header text -> column number.
            var columns = new Dictionary<string, int>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var cell in headerRow.CellsUsed())
            {
                var text = cell.GetString().Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    columns[text] = cell.Address.ColumnNumber;
                }
            }

            int? emailCol = FindColumn(columns, EmailHeaders);
            int? firstCol = FindColumn(columns, FirstNameHeaders);
            int? lastCol = FindColumn(columns, LastNameHeaders);
            int? tagCol = FindColumn(columns, TagLineHeaders);
            int? bioCol = FindColumn(columns, BiographyHeaders);
            int? blogCol = FindColumn(columns, BlogHeaders);
            int? linkedInCol = FindColumn(columns, LinkedInHeaders);
            int? twitterCol = FindColumn(columns, TwitterHeaders);
            int? picCol = FindColumn(columns, ProfilePictureHeaders);

            if (emailCol is null)
            {
                return new SessionizeParseResult(
                    speakers, warnings,
                    "No 'Email' column was found in the first row. Expected a "
                    + "header named Email, E-mail, or Email Address.");
            }

            // Data rows start after the header row.
            // Sessionize "flattened accepted sessions" export emits one row
            // per (session, speaker) pair, so the same speaker recurs across
            // their N sessions. Dedupe on email + keep the first non-empty
            // value for each field; subsequent rows for the same speaker only
            // back-fill blanks.
            var seen = new Dictionary<string, SessionizeSpeaker>(StringComparer.OrdinalIgnoreCase);
            var firstDataRow = headerRow.RowNumber() + 1;
            var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 0;

            for (var rowNum = firstDataRow; rowNum <= lastRow; rowNum++)
            {
                var row = sheet.Row(rowNum);

                var email = CellText(row, emailCol.Value)
                    .Trim().ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(email))
                {
                    var hasOtherData =
                        (firstCol is not null
                         && !string.IsNullOrWhiteSpace(CellText(row, firstCol.Value)))
                        || (lastCol is not null
                            && !string.IsNullOrWhiteSpace(CellText(row, lastCol.Value)));
                    if (hasOtherData)
                    {
                        warnings.Add($"Row {rowNum}: skipped - no email address.");
                    }
                    continue;
                }

                var current = new SessionizeSpeaker(
                    Email:             email,
                    FirstName:         Read(row, firstCol),
                    LastName:          Read(row, lastCol),
                    TagLine:           ReadOrNull(row, tagCol),
                    Biography:         ReadOrNull(row, bioCol),
                    Blog:              ReadOrNull(row, blogCol),
                    LinkedIn:          ReadOrNull(row, linkedInCol),
                    Twitter:           ReadOrNull(row, twitterCol),
                    ProfilePictureUrl: ReadOrNull(row, picCol));

                if (seen.TryGetValue(email, out var existing))
                {
                    seen[email] = Merge(existing, current);
                }
                else
                {
                    seen[email] = current;
                }
            }
            speakers.AddRange(seen.Values);
        }

        return new SessionizeParseResult(speakers, warnings, null);
    }

    private static int? FindColumn(
        IReadOnlyDictionary<string, int> columns, string[] aliases)
    {
        foreach (var alias in aliases)
        {
            if (columns.TryGetValue(alias, out var col))
            {
                return col;
            }
        }
        return null;
    }

    private static string CellText(IXLRow row, int columnNumber) =>
        row.Cell(columnNumber).GetString();

    private static string Read(IXLRow row, int? col) =>
        col is null ? string.Empty : CellText(row, col.Value).Trim();

    private static string? ReadOrNull(IXLRow row, int? col)
    {
        if (col is null) return null;
        var s = CellText(row, col.Value).Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    private static SessionizeSpeaker Merge(SessionizeSpeaker a, SessionizeSpeaker b) =>
        a with
        {
            FirstName         = NonEmpty(a.FirstName, b.FirstName),
            LastName          = NonEmpty(a.LastName, b.LastName),
            TagLine           = a.TagLine           ?? b.TagLine,
            Biography         = a.Biography         ?? b.Biography,
            Blog              = a.Blog              ?? b.Blog,
            LinkedIn          = a.LinkedIn          ?? b.LinkedIn,
            Twitter           = a.Twitter           ?? b.Twitter,
            ProfilePictureUrl = a.ProfilePictureUrl ?? b.ProfilePictureUrl,
        };

    private static string NonEmpty(string a, string b) =>
        string.IsNullOrWhiteSpace(a) ? b : a;
}
