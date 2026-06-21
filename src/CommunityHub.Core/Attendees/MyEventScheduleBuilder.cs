using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;

namespace CommunityHub.Core.Attendees;

/// <summary>
/// One row of the attendee's personal "My sessions / schedule" view on the
/// My-event page. A flattened, read-only projection of a public
/// <see cref="PublicSessionRow"/> with everything the attendee surface renders:
/// title, type/room/track, schedule, the speaker name(s) joined for display, and
/// the two public deep-links a session may carry — its detail page (always) and,
/// when a public token has been minted, its attendee-question ask page and
/// HappyOrNot evaluate page (<c>/sessions/{token}/ask</c> · <c>…/evaluate</c>).
///
/// <see cref="IsMine"/> marks the session the signed-in attendee is registered for
/// (their reconciled Master Class) so the agenda can highlight it. Pure data so the
/// builder stays unit-testable without a DbContext.
/// </summary>
public sealed record MyEventSessionRow(
    int Id,
    string Title,
    SessionType Type,
    string? Room,
    string? Track,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    string Speakers,
    bool IsMine,
    string DetailUrl,
    string? AskUrl,
    string? EvaluateUrl)
{
    /// <summary>
    /// True once the session's public ask / evaluate token has been minted, i.e.
    /// when <see cref="AskUrl"/> / <see cref="EvaluateUrl"/> are live links. The
    /// ask + evaluate token is minted on demand (typically at the start of the
    /// session), so before then these URLs are null. The attendee surface uses
    /// this to decide whether to render the active ask/evaluate links OR a short
    /// "available during the session" hint — never nothing — so the attendee
    /// understands the action will appear rather than seeing a silent gap.
    /// (Both URLs derive from the same token, so a single flag covers both.)
    /// </summary>
    public bool AskEvaluateAvailable => AskUrl is not null;
}

/// <summary>
/// The attendee's personal agenda view-model: the session(s) they are registered
/// for (their Master Class), the full public agenda for the edition (their session
/// highlighted via <see cref="MyEventSessionRow.IsMine"/>), and convenience counts
/// for the empty states. Built by <see cref="MyEventScheduleBuilder"/> from the
/// already-public session projection — no private data, no schema change.
/// </summary>
public sealed class MyEventSchedule
{
    /// <summary>The session(s) the attendee is registered for (their reconciled Master Class). May be empty.</summary>
    public IReadOnlyList<MyEventSessionRow> MySessions { get; init; } = Array.Empty<MyEventSessionRow>();

    /// <summary>The full public agenda for the edition, the attendee's own session(s) highlighted.</summary>
    public IReadOnlyList<MyEventSessionRow> Agenda { get; init; } = Array.Empty<MyEventSessionRow>();

    /// <summary>True when the edition has no published sessions yet (agenda empty state).</summary>
    public bool AgendaIsEmpty => Agenda.Count == 0;

    /// <summary>True when the attendee is not (yet) registered for any session.</summary>
    public bool HasNoMySessions => MySessions.Count == 0;
}

/// <summary>
/// Builds the attendee's personal schedule for the My-event page from the PUBLIC
/// session projection (<see cref="PublicSessionRow"/>, produced by
/// <see cref="PublicSessionsService"/>) plus the attendee's own reconciled
/// <see cref="Attendee"/> record (may be null). Pure: no DbContext, no clock — every
/// input is explicit so the "which session is mine" matching and the link shaping
/// are unit-testable.
///
/// "Mine" is matched by the attendee's reconciled <see cref="Attendee.MasterClassName"/>
/// (the Master Class they booked in Zoho Bookings) against the public session titles,
/// case-/whitespace-insensitively. <see cref="Attendee.MasterClassName"/> may be a
/// comma-separated list when the attendee is double-booked, so each part is matched.
/// Read-only aggregation only — it never writes and never re-implements booking.
/// </summary>
public static class MyEventScheduleBuilder
{
    public static MyEventSchedule Build(IReadOnlyList<PublicSessionRow> publicSessions, Attendee? record)
    {
        if (publicSessions is null || publicSessions.Count == 0)
        {
            return new MyEventSchedule();
        }

        // The Master Class name(s) the attendee booked, normalised for matching.
        var mine = SplitNames(record?.MasterClassName);

        var agenda = publicSessions
            .Select(s => ToRow(s, IsMine(s.Title, mine)))
            .ToList();

        var mySessions = agenda.Where(r => r.IsMine).ToList();

        return new MyEventSchedule
        {
            Agenda = agenda,
            MySessions = mySessions,
        };
    }

    private static MyEventSessionRow ToRow(PublicSessionRow s, bool isMine)
    {
        var speakers = string.Join(", ", s.Speakers.Select(sp => sp.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n)));

        // The ask / evaluate pages are addressed by the session's unguessable public
        // token (minted on demand). Only offer the link once a token exists.
        string? ask = string.IsNullOrWhiteSpace(s.AskToken) ? null : $"/sessions/{s.AskToken}/ask";
        string? evaluate = string.IsNullOrWhiteSpace(s.AskToken) ? null : $"/sessions/{s.AskToken}/evaluate";

        return new MyEventSessionRow(
            s.Id,
            s.Title,
            s.Type,
            s.Room,
            s.Track,
            s.StartsAt,
            s.EndsAt,
            speakers,
            isMine,
            DetailUrl: $"/Sessions/{s.Id}",
            AskUrl: ask,
            EvaluateUrl: evaluate);
    }

    private static bool IsMine(string title, IReadOnlyCollection<string> mineNormalised)
    {
        if (mineNormalised.Count == 0) return false;
        var t = Normalise(title);
        return t.Length > 0 && mineNormalised.Contains(t);
    }

    /// <summary>
    /// Split a (possibly comma-separated, when double-booked) Master Class name into
    /// the set of normalised names to match. Empty when the attendee has no booking.
    /// </summary>
    private static IReadOnlyCollection<string> SplitNames(string? masterClassName)
    {
        if (string.IsNullOrWhiteSpace(masterClassName)) return Array.Empty<string>();
        return masterClassName
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalise)
            .Where(n => n.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string Normalise(string? s) =>
        string.IsNullOrWhiteSpace(s) ? string.Empty : s.Trim().ToLowerInvariant();
}
