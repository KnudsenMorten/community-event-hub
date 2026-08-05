namespace CommunityHub.Core.Domain;

/// <summary>
/// §6.4 — what the last logistics run published for one file, and when it was last mailed.
/// </summary>
/// <remarks>
/// <para>🔒 <b>This row is the memory the whole mail schedule runs on.</b> The publisher is
/// deliberately stateless — it compares the content key it is GIVEN — so somebody has to remember
/// the key from last time. That is this table.</para>
///
/// <para>⚠️ <b><see cref="ContentKey"/> is a hash of the DATA, never of the file bytes.</b> An
/// <c>.xlsx</c> is a ZIP whose document properties and entry timestamps differ on every build, so a
/// byte hash would change daily and mail an external venue contact daily (§770.3).</para>
///
/// <para><see cref="LastMailedAt"/> is separate from <see cref="PublishedAt"/> on purpose: a file
/// can be republished without being mailed (the weekly cadence has not come round, or the reports
/// are not approved yet), and a file can be mailed again without changing.</para>
/// </remarks>
public class LogisticsFileState
{
    public int Id { get; set; }

    public int EventId { get; set; }

    /// <summary>The registry key of the folder it was published to (<c>DocLibraryPaths</c>).</summary>
    public string PathKey { get; set; } = string.Empty;

    /// <summary>The file name, e.g. <c>eldk27-lunch-day1-preday.xlsx</c>.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>The content key of the version currently in the library.</summary>
    public string ContentKey { get; set; } = string.Empty;

    public DateTimeOffset PublishedAt { get; set; }

    /// <summary>When this file was last SENT to somebody. Null ⇒ never mailed.</summary>
    public DateTimeOffset? LastMailedAt { get; set; }

    /// <summary>
    /// Who it was last sent to — the RESOLVED address, so the trail shows the review mailbox while
    /// the reports are unapproved rather than the recipient it was meant for.
    /// </summary>
    public string? LastMailedTo { get; set; }

    /// <summary>
    /// §6.3 — the headline figure the producer reported for the version currently published, e.g.
    /// <c>"42 seats"</c>. Null on rows written before this existed.
    /// </summary>
    public string? Headline { get; set; }

    /// <summary>
    /// §6.3 — the document-library link to the published file, so the organizer page can OPEN it
    /// rather than describe where it lives.
    /// </summary>
    /// <remarks>
    /// ⚠️ Nullable, and it stays nullable. A host that cannot write the library never gets one, and
    /// a fabricated link is worse than an absent one: it sends somebody to a 404 and makes them
    /// doubt the file exists at all.
    /// </remarks>
    public string? WebUrl { get; set; }
}

/// <summary>
/// §6.3 — the outcome of the LAST automatic logistics run, so the organizer page can answer "did it
/// run, and did anything go wrong?" without anybody opening a log.
/// </summary>
/// <remarks>
/// <para>🔒 <b>One row per edition, overwritten every run.</b> This is a STATUS, not a history — the
/// question the page asks is "is this healthy right now". The per-file trail already lives in
/// <see cref="LogisticsFileState"/> and the full history is in the logs.</para>
///
/// <para>⚠️ <b>A run that produced nothing still writes this row.</b> "The job has not run since
/// Friday" and "the job ran and had nothing to do" look identical on a page that only records
/// successes, and they need completely different responses from the organizer.</para>
/// </remarks>
public class LogisticsRunSummary
{
    public int Id { get; set; }

    public int EventId { get; set; }

    /// <summary>When the run finished (UTC).</summary>
    public DateTimeOffset RanAt { get; set; }

    public int Published { get; set; }

    public int Unchanged { get; set; }

    public int Mailed { get; set; }

    /// <summary>
    /// The run's problems, one per line — skips, failures, and the people awaiting a hotel.
    /// Empty ⇒ a clean run.
    /// </summary>
    /// <remarks>
    /// Text rather than a child table on purpose: it is displayed verbatim and never queried, and a
    /// table would invite somebody to build reporting on wording that is meant to stay free to change.
    /// </remarks>
    public string? Problems { get; set; }

    /// <summary>True when the run completed with no failures.</summary>
    public bool Ok { get; set; }
}
