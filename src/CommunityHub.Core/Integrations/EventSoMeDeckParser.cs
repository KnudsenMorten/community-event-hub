using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CommunityHub.Core.Integrations;

/// <summary>One dated run of a post, as the deck states it.</summary>
public sealed record EventSoMeDeckOccurrence(
    DateOnly Date,
    string GraphicFileName,
    string? SourcePhotoFileName);

/// <summary>One post parsed out of the deck.</summary>
public sealed record EventSoMeDeckPost(
    string Slug,
    string Title,
    string Body,
    IReadOnlyList<EventSoMeDeckOccurrence> Occurrences);

/// <summary>
/// The outcome of parsing a deck. <see cref="Problems"/> is never thrown away — a deck that parses
/// 26 of 27 posts must SAY so, because a silent partial import is indistinguishable from a good one.
/// </summary>
public sealed record EventSoMeDeckParseResult(
    IReadOnlyList<EventSoMeDeckPost> Posts,
    IReadOnlyList<string> Problems);

/// <summary>
/// §828 — READS THE OPERATOR'S EVENT-POST DECK (<c>ELDK27-LinkedIn-posts.md</c>).
///
/// <para>🔒 <b>THE FORMAT IS READ, NEVER INFERRED.</b> §828.6: <i>"slug is in markdown and pairing is
/// also in file"</i> — so this parser follows the file's own structure and derives nothing. There is
/// no slugify-the-title and no <c>&lt;slug&gt;.png</c> convention, which is what keeps §767 (a guessed
/// filename convention that matched NOTHING for four production runs) off this path entirely.</para>
///
/// <para>The shape it reads, per post:</para>
/// <code>
/// ## Post 19 — The Danish IT Class Re-union
/// `title:` The Danish IT Class Re-union
/// `slug:` eldk27-networking
/// `dates:`
///
///   - 2026-10-29 · `eldk27_20261029_eldk27-networking_v1_card.png` (It-peers_networking_lounge.JPG)
///   - 2026-12-10 · `eldk27_20261210_eldk27-networking_v2_card.png` (eldk27-networking.JPG)
///
/// `graphics:`
/// - **Style:** framed card          ← production notes for HIM, not post content
/// - **Photos used (1):** …
///
/// &lt;the post body&gt;              ← first non-blank, non-bullet line after `graphics:`
/// ---                              ← ends the post
/// </code>
///
/// <para><b>⚠️ THE SUMMARY TABLE AT THE TOP OF THE FILE IS IGNORED ON PURPOSE.</b> The deck opens
/// with a 46-row calendar table, and <b>two of its rows name a graphic that does not exist</b>
/// (§828.7 — measured against the library). The per-post <c>dates:</c> blocks are 46/46 correct.
/// Parsing the table would have failed on exactly two posts and looked healthy on the other 44.</para>
///
/// <para>Pure and side-effect free: it takes text and returns records. Fetching the file and writing
/// rows belong to the importer, so the format can be unit-tested without SharePoint or a database.</para>
/// </summary>
public static class EventSoMeDeckParser
{
    /// <summary>`title:` / `slug:` — a backticked key, then its value to end of line.</summary>
    private static readonly Regex TitleLine = new(@"^`title:`\s*(?<v>.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex SlugLine = new(@"^`slug:`\s*(?<v>.+?)\s*$", RegexOptions.Compiled);

    /// <summary>
    /// A run: <c>- 2026-10-29 · `file.png` (Photo.JPG)</c>. The separator is U+00B7 MIDDLE DOT.
    ///
    /// <para>🔑 The trailing field is captured RAW and interpreted afterwards, rather than matched as
    /// a tidy <c>\((?&lt;photo&gt;[^)]*)\)</c>. A strict parenthesis pair looked right and passed a
    /// hand-written fixture, but the real deck writes the text-only runs as
    /// <c>((background only))</c> — DOUBLE parentheses — and the strict form rejected the whole line.
    /// That silently cost <b>five of the deck's 46 runs</b> (41 imported, no error), and it was only
    /// visible by running the parser over the actual file. Keep this tolerant.</para>
    /// </summary>
    private static readonly Regex DateLine = new(
        @"^\s*-\s*(?<date>\d{4}-\d{2}-\d{2})\s*·\s*`(?<graphic>[^`]+)`\s*(?<photo>.*)$",
        RegexOptions.Compiled);

    private const string PostHeadingPrefix = "## Post ";
    private const string DatesKey = "`dates:`";
    private const string GraphicsKey = "`graphics:`";

    /// <summary>
    /// Parses <paramref name="markdown"/> into posts. Never throws on malformed content: a post that
    /// cannot be understood is reported in <see cref="EventSoMeDeckParseResult.Problems"/> and skipped,
    /// so one bad block cannot cost him the other 26.
    /// </summary>
    public static EventSoMeDeckParseResult Parse(string? markdown)
    {
        var posts = new List<EventSoMeDeckPost>();
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(markdown))
        {
            problems.Add("The deck is empty — nothing to import.");
            return new EventSoMeDeckParseResult(posts, problems);
        }

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        // Post blocks start at a "## Post " heading. Everything before the first one is the summary
        // table + preamble, and is deliberately not read (see the class remarks).
        var starts = new List<int>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith(PostHeadingPrefix, StringComparison.Ordinal))
            {
                starts.Add(i);
            }
        }

        if (starts.Count == 0)
        {
            problems.Add(
                $"No '{PostHeadingPrefix.Trim()}' headings found — the deck is not in the expected "
                + "format, so nothing was imported.");
            return new EventSoMeDeckParseResult(posts, problems);
        }

        var seenSlugs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var b = 0; b < starts.Count; b++)
        {
            var from = starts[b];
            var to = b + 1 < starts.Count ? starts[b + 1] : lines.Length;
            var heading = lines[from].Trim();

            var parsed = ParseBlock(lines, from, to, heading, problems);
            if (parsed is null)
            {
                continue;
            }

            // 🔒 The slug is the import key, so a deck that states one twice is ambiguous BEFORE it
            // reaches the database. Reported and skipped rather than letting the later block win by
            // accident — "which post is this slug?" must never depend on file order.
            if (seenSlugs.TryGetValue(parsed.Slug, out var firstAt))
            {
                problems.Add(
                    $"'{heading}': slug '{parsed.Slug}' is already used by the post at line {firstAt + 1}. "
                    + "The slug is the import key and must be unique — this post was skipped.");
                continue;
            }

            seenSlugs[parsed.Slug] = from;
            posts.Add(parsed);
        }

        return new EventSoMeDeckParseResult(posts, problems);
    }

    private static EventSoMeDeckPost? ParseBlock(
        string[] lines, int from, int to, string heading, List<string> problems)
    {
        string? title = null;
        string? slug = null;
        var occurrences = new List<EventSoMeDeckOccurrence>();

        var i = from + 1;

        // --- header: title + slug, then the dates block, up to `graphics:` --------------------
        var sawDates = false;
        var sawGraphics = false;

        for (; i < to; i++)
        {
            var line = lines[i];

            if (line.StartsWith(GraphicsKey, StringComparison.Ordinal))
            {
                sawGraphics = true;
                i++;
                break;
            }

            if (line.StartsWith(DatesKey, StringComparison.Ordinal))
            {
                sawDates = true;
                continue;
            }

            var t = TitleLine.Match(line);
            if (t.Success)
            {
                title = t.Groups["v"].Value.Trim();
                continue;
            }

            var s = SlugLine.Match(line);
            if (s.Success)
            {
                slug = s.Groups["v"].Value.Trim();
                continue;
            }

            // Only inside the dates block: the `graphics:` block below carries lines that also hold a
            // middle dot and a backticked .png (the photo→graphic notes), and reading those as runs
            // would double every occurrence.
            if (!sawDates)
            {
                continue;
            }

            var d = DateLine.Match(line);
            if (!d.Success)
            {
                continue;
            }

            if (!DateOnly.TryParseExact(
                    d.Groups["date"].Value, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                problems.Add($"'{heading}': could not read the date '{d.Groups["date"].Value}' — run skipped.");
                continue;
            }

            var photo = NormalizePhoto(d.Groups["photo"].Value);

            occurrences.Add(new EventSoMeDeckOccurrence(date, d.Groups["graphic"].Value.Trim(), photo));
        }

        if (string.IsNullOrWhiteSpace(slug))
        {
            problems.Add($"'{heading}': no `slug:` line — the import key is missing, so the post was skipped.");
            return null;
        }

        if (!sawGraphics)
        {
            problems.Add($"'{heading}': no `graphics:` line, so the body could not be located — post skipped.");
            return null;
        }

        if (occurrences.Count == 0)
        {
            problems.Add($"'{heading}' (slug '{slug}'): no dated runs in its `dates:` block.");
        }

        // --- body: first non-blank, non-bullet line after `graphics:`, up to the `---` ---------
        var body = ReadBody(lines, i, to);

        if (string.IsNullOrWhiteSpace(body))
        {
            problems.Add($"'{heading}' (slug '{slug}'): the post body is empty — post skipped.");
            return null;
        }

        return new EventSoMeDeckPost(
            slug,
            string.IsNullOrWhiteSpace(title) ? slug : title!,
            body,
            occurrences);
    }

    /// <summary>
    /// The source photograph named after the graphic, or null when the run names none.
    ///
    /// <para>Handles the deck's three real spellings: <c>(Photo.JPG)</c>, <c>((background only))</c>
    /// and nothing at all. "Background only" is PROSE, not a file — storing it would put a
    /// non-existent file name in a provenance column, so anything without a dotted extension is
    /// treated as no photo.</para>
    /// </summary>
    private static string? NormalizePhoto(string raw)
    {
        var v = raw.Trim().Trim('(', ')').Trim();

        // A file name, not a sentence: "background only" has no extension, "ELDK_lamp.JPG" does.
        return v.Length > 0 && v.Contains('.') && !v.Contains(' ') ? v : null;
    }

    /// <summary>
    /// The post text. The <c>graphics:</c> block is a bullet list of production notes (style, photos
    /// used, photo brief) — skipped, because it is instruction to himself and not part of the post.
    /// The body is what follows it, and ends at the <c>---</c> separator, which is what stops the last
    /// post from swallowing the appendix at the end of the deck.
    /// </summary>
    private static string ReadBody(string[] lines, int start, int to)
    {
        var i = start;

        // Skip the graphics bullet list (and the blank lines inside it). Verified against the real
        // deck: every one of the 27 bodies begins at the first non-blank, non-bullet line here.
        for (; i < to; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (trimmed.Length == 0)
            {
                continue;
            }

            // The separator ENDS the post. It also begins with '-', so without this the skip loop
            // would step over it and hunt for a body that is not there.
            if (trimmed == "---")
            {
                break;
            }

            if (line.TrimStart().StartsWith('-'))
            {
                continue;
            }

            break;
        }

        var sb = new StringBuilder();
        for (; i < to; i++)
        {
            if (lines[i].Trim() == "---")
            {
                break;
            }

            sb.AppendLine(lines[i]);
        }

        return sb.ToString().Trim();
    }
}
