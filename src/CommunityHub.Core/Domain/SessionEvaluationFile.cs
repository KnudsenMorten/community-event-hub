namespace CommunityHub.Core.Domain;

/// <summary>
/// The KIND of a final per-session evaluation PDF (REQUIREMENTS §192). A session now has
/// up to TWO independent evaluation files: a quantitative <see cref="Score"/> summary and a
/// qualitative <see cref="Open"/> (open-feedback) write-up. Stored as <c>int</c> (see
/// <c>CommunityHubDbContext</c>), so the numeric values below are part of the persisted
/// contract. The kind also names the SharePoint file (<c>session-{id}-score.pdf</c> /
/// <c>session-{id}-feedback.pdf</c>) and the hub proxy route segment (<c>score</c>/<c>feedback</c>).
/// </summary>
public enum EvaluationPdfKind
{
    /// <summary>The quantitative SCORE evaluation PDF (e.g. the HappyOrNot rating summary).</summary>
    Score = 0,

    /// <summary>The qualitative OPEN-feedback PDF (free-text comments). Not every session has one.</summary>
    Open = 1,
}

/// <summary>
/// PROVENANCE for one uploaded final per-session evaluation PDF (REQUIREMENTS §192c). The
/// bytes live on SharePoint under a deterministic name (handled by
/// <see cref="CommunityHub.Core.Integrations.Graphics.SessionEvalPdfService"/>); THIS row
/// records WHO uploaded the file and WHEN, so the organizer page can show "uploaded by
/// {name} on {date time}" next to each file, and the speaker page can tell which kinds
/// exist for a session without listing the store.
///
/// One row per (session, kind): an organizer REPLACE updates the same row in place (the
/// upsert key is <c>(SessionId, Kind)</c>). Edition-scoped via <see cref="EventId"/>.
/// </summary>
public class SessionEvaluationFile
{
    public int Id { get; set; }

    /// <summary>The edition this file belongs to. Every query is scoped by this.</summary>
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>The session the evaluation PDF is for.</summary>
    public int SessionId { get; set; }
    public Session Session { get; set; } = null!;

    /// <summary>Which evaluation file this is (Score vs Open-feedback).</summary>
    public EvaluationPdfKind Kind { get; set; }

    /// <summary>
    /// The participant id of the organizer who uploaded (or last replaced) the file —
    /// audit only. Nullable so a future system/automated upload can leave it unset.
    /// </summary>
    public int? UploadedByParticipantId { get; set; }

    /// <summary>The display name of who uploaded the file, captured at upload time (§192c).</summary>
    public string UploadedByName { get; set; } = string.Empty;

    /// <summary>When the file was uploaded / last replaced (§192c).</summary>
    public DateTimeOffset UploadedAt { get; set; }

    /// <summary>The deterministic SharePoint file name written for this (session, kind).</summary>
    public string FileName { get; set; } = string.Empty;
}
