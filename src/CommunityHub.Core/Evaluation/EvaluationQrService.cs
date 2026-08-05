using System.Security.Cryptography;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain.Evaluation;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Evaluation;

/// <summary>
/// §748 C5/C6 — the QR channel: resolve a printed token to a session, and record the attendee's
/// rating as an ordinary <see cref="EvaluationResponse"/>.
/// </summary>
/// <remarks>
/// <para>🔒 <b>The QR and the device are ONE instrument.</b> A scan writes the same table, the same
/// four ratings and the same weights as a button press; <see cref="EvaluationResponse.Source"/>
/// records which path it came by, <b>for diagnostics only</b>. There is deliberately no separate QR
/// table and no separate QR score — the moment those exist, someone reports them separately and the
/// event has two different satisfaction figures.</para>
///
/// <para>🔑 <b>One token per SESSION</b> (operator: *"1 per session"*), generated once and permanent,
/// so a session that moves room or time keeps its already-printed code. It reuses the existing
/// <see cref="Domain.Session.PublicToken"/> — the same token the ask page uses — rather than minting
/// a second one, because two unguessable strings per session is two things to print, mix up and
/// revoke.</para>
/// </remarks>
public sealed class EvaluationQrService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public EvaluationQrService(CommunityHubDbContext db, TimeProvider? clock = null)
    {
        _db = db;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Why a scan cannot be accepted right now. Each maps to a message the attendee sees.</summary>
    public enum FeedbackState
    {
        /// <summary>The window is open — show the buttons.</summary>
        Open,

        /// <summary>
        /// No session for this token. A printed code whose session was DELETED lands here, which the
        /// brief requires: it must resolve to an honest "session not found" rather than silently
        /// accepting orphan feedback.
        /// </summary>
        NotFound,

        /// <summary>
        /// The session exists in CEH but has not been mirrored into the evaluation model yet, so it
        /// has no collection window and nothing to attribute against.
        /// 🔑 This is an ORGANISER problem (the C3 sync has not run), not an attendee one — the page
        /// says feedback is not open yet rather than blaming the person holding the phone.
        /// </summary>
        NotSynced,

        /// <summary>Scanned before the session started.</summary>
        NotOpenYet,

        /// <summary>
        /// The collection window has closed. 🔒 The brief is explicit: refuse and SAY SO —
        /// *"A form that appears to work but discards the result is worse than an honest refusal."*
        /// </summary>
        Closed,
    }

    /// <summary>What a scan resolves to. <see cref="EvaluationSessionId"/> is null unless Open.</summary>
    public sealed record Resolution(
        FeedbackState State,
        int? CehSessionId,
        int? EvaluationSessionId,
        int? EventId,
        string? Title,
        string? Room,
        IReadOnlyList<string> Speakers,
        DateTimeOffset? WindowOpensAt,
        DateTimeOffset? WindowClosesAt);

    private static readonly IReadOnlyList<string> NoSpeakers = Array.Empty<string>();

    /// <summary>
    /// Resolve a printed token. 🔑 The title and speakers come back even when the window is shut, so
    /// the page can still name the session — the brief wants an attendee who scanned the WRONG code
    /// to notice before submitting, and that is just as true for a refusal.
    /// </summary>
    public async Task<Resolution> ResolveAsync(string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return new Resolution(FeedbackState.NotFound, null, null, null, null, null, NoSpeakers, null, null);

        var session = await _db.Sessions
            .Include(s => s.SessionSpeakers).ThenInclude(ss => ss.Participant)
            .FirstOrDefaultAsync(s => s.PublicToken == token, ct);

        if (session is null)
            return new Resolution(FeedbackState.NotFound, null, null, null, null, null, NoSpeakers, null, null);

        var speakers = session.SessionSpeakers
            .Select(ss => ss.Participant?.FullName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.CurrentCulture)
            .ToList();

        var evalSession = await _db.EvaluationSessions
            .Include(s => s.Room)
            .FirstOrDefaultAsync(s => s.CehSessionId == session.Id && s.EventId == session.EventId, ct);

        if (evalSession is null)
        {
            return new Resolution(
                FeedbackState.NotSynced, session.Id, null, session.EventId,
                session.Title, null, speakers, null, null);
        }

        var now = _clock.GetUtcNow();
        var state =
            now < evalSession.CollectionWindowOpensAt ? FeedbackState.NotOpenYet :
            now > evalSession.CollectionWindowClosesAt ? FeedbackState.Closed :
            FeedbackState.Open;

        return new Resolution(
            state, session.Id, evalSession.Id, session.EventId,
            session.Title, evalSession.Room?.Name, speakers,
            evalSession.CollectionWindowOpensAt, evalSession.CollectionWindowClosesAt);
    }

    /// <summary>Valid ratings. The four-point forced choice — no neutral midpoint, by design.</summary>
    public const int MinRating = 1;
    public const int MaxRating = 4;

    /// <summary>Matches the column bound in <c>CommunityHubDbContext</c>.</summary>
    public const int MaxFreeTextLength = 4000;

    /// <summary>The outcome of a submission. Anything but <see cref="FeedbackState.Open"/> wrote nothing.</summary>
    public sealed record SubmitResult(FeedbackState State, bool Saved);

    /// <summary>
    /// Record one attendee rating from a scan.
    /// </summary>
    /// <remarks>
    /// <para>🔒 <b>The window is re-checked HERE, not trusted from the page.</b> A form rendered
    /// while the window was open can be posted after it closed — someone with the page still on
    /// screen — and the brief refuses that submission rather than counting it.</para>
    ///
    /// <para>🔒 <b>Append-only, like every other response</b> (§8.1): a second submission is a new
    /// row, never an edit of the first. That is what keeps recomputation after late data safe to run
    /// repeatedly, and it matches the device — where nothing stops the same person pressing twice
    /// either.</para>
    ///
    /// <para>🔒 <b>Both timestamps are "now" here, and that is correct.</b> Unlike a device flushing
    /// a cache, a scan is submitted at the moment it is collected — there is no offline buffer. They
    /// are still written separately rather than shared, because they answer different questions and
    /// the day one of them stops being "now" this code should not have to be found first.</para>
    /// </remarks>
    public async Task<SubmitResult> SubmitAsync(
        string? token, int rating, string? freeText, CancellationToken ct = default)
    {
        var resolved = await ResolveAsync(token, ct);
        if (resolved.State != FeedbackState.Open) return new SubmitResult(resolved.State, false);

        if (rating < MinRating || rating > MaxRating)
            throw new ArgumentOutOfRangeException(nameof(rating), rating, "Rating must be 1–4.");

        var text = string.IsNullOrWhiteSpace(freeText) ? null : freeText.Trim();
        if (text is { Length: > MaxFreeTextLength }) text = text[..MaxFreeTextLength];

        var now = _clock.GetUtcNow();

        _db.EvaluationResponses.Add(new EvaluationResponse
        {
            EventId = resolved.EventId!.Value,
            SessionId = resolved.EvaluationSessionId,
            SerialNumber = null,            // a scan has no device
            DeviceRecordId = null,      // and no device-generated id; the unique index is filtered
            Rating = rating,
            CollectionTimestamp = now,
            ReceivedTimestamp = now,
            Source = EvaluationResponseSources.Qr,
            FreeText = text,
        });

        await _db.SaveChangesAsync(ct);
        return new SubmitResult(FeedbackState.Open, true);
    }

    /// <summary>
    /// The session's permanent QR token, minted on first use. 🔒 An existing token is NEVER replaced
    /// — printed material depends on it, and this is the same token the ask page addresses.
    /// </summary>
    public async Task<string?> EnsureTokenAsync(int sessionId, CancellationToken ct = default)
    {
        var session = await _db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null) return null;

        if (!string.IsNullOrWhiteSpace(session.PublicToken)) return session.PublicToken;

        session.PublicToken = NewToken();
        await _db.SaveChangesAsync(ct);
        return session.PublicToken;
    }

    /// <summary>
    /// 24 bytes of CSPRNG output, base64url. 🔒 Unguessable is the whole security model of an
    /// anonymous page — and URL-safe because this string is typed into a QR encoder and, when a code
    /// will not scan, read aloud off a sign.
    /// </summary>
    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
}

/// <summary>
/// §748 C6 — the button labels. 🔒 <b>Presentation only, never persisted</b> (brief §2.2): the stored
/// value is the number 1–4, so relabelling or translating can never invalidate history.
/// </summary>
public static class EvaluationRatingLabels
{
    /// <summary>
    /// 🔑 Rating 2's label carries a MILDLY NEGATIVE connotation on purpose. The brief is explicit:
    /// there is no neutral midpoint, so 2 counts as negative in every figure — and if the button
    /// reads as neutral ("OK"), respondents' mental model stops matching how their answer
    /// aggregates, which quietly biases the result.
    /// </summary>
    /// <remarks>
    /// 🔒 §750.8 — these are the BRIEF's own words for the four buttons (dark green "Very happy",
    /// light green "Happy", yellow "Somewhat unhappy", red "Unhappy"), restoring them over an
    /// earlier Excellent/Good/Could-be-better/Poor set. Operator 2026-08-01: <i>"use the wordings
    /// like very happy"</i>.
    /// 🔑 ONE vocabulary everywhere: the report names each button exactly as the attendee saw it
    /// when they pressed it. Two wordings for one button is how a speaker ends up arguing with
    /// their own results. The rule above still holds — rating 2 stays negative.
    /// </remarks>
    public static string Label(int rating) => rating switch
    {
        4 => "Very happy",
        3 => "Happy",
        2 => "Somewhat unhappy",
        1 => "Unhappy",
        _ => string.Empty,
    };

    /// <summary>The four-point colour scale, dark green → red. Presentation, like the label.</summary>
    public static string Colour(int rating) => rating switch
    {
        4 => "#15803d",   // dark green
        3 => "#84cc16",   // light green
        2 => "#eab308",   // yellow
        1 => "#dc2626",   // red
        _ => "#6b7280",
    };

    /// <summary>Highest first — the order the buttons appear, matching the device's layout.</summary>
    public static readonly int[] Order = { 4, 3, 2, 1 };
}
