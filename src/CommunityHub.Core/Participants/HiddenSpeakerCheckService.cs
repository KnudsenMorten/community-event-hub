using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Participants;

/// <summary>
/// §1385 — "No hidden speakers": the check CEH runs itself so a speaker nobody can see on the
/// agenda, or a session nobody is speaking at, is never forgotten.
/// </summary>
/// <remarks>
/// <para>Operator 2026-11-07: <i>"we cannot have hidden speakers we forgot"</i>. Found that day on
/// PROD: 9 active, real Speaker-role people on NO session (counted as speakers, invisible on the
/// agenda and every per-session view), 2 of them with no speaker profile, and 8 real sessions with
/// no speakers. The Sessionize import that removed CEH-made links is fixed; this is what makes the
/// next gap visible instead of waiting for someone to count.</para>
///
/// <para>🔑 <b>"Real session" is the narrow, structural rule</b>: same edition, not a TEST session
/// (<see cref="Session.UsedForTesting"/>), not legacy test data (<see cref="Session.IsTestData"/>),
/// not a service slot (break, lunch). <b>It deliberately ignores
/// <see cref="Session.ExcludeFromSoMeAnnouncements"/></b> and the SoMe title patterns — those decide
/// whether a session is ANNOUNCED, not whether it exists. A sponsor session kept off LinkedIn is
/// still on the agenda, and a speaker on it is not hidden.</para>
///
/// <para>🔑 A speaker linked ONLY to a test session counts as "on no session": a test session never
/// appears on any public page, so the speaker is exactly as hidden as one with no link at all.</para>
///
/// <para>Pure read — never writes, never sends. The page and the weekly organizer notice
/// (<see cref="Reminders.HiddenSpeakersReminderBuilder"/>) both read this one answer.</para>
/// </remarks>
public sealed class HiddenSpeakerCheckService
{
    private readonly CommunityHubDbContext _db;

    public HiddenSpeakerCheckService(CommunityHubDbContext db) => _db = db;

    /// <summary>A speaker the check found, with when the record was created.</summary>
    public sealed record SpeakerItem(int ParticipantId, string Name, DateTimeOffset CreatedAt);

    /// <summary>A real session with no speaker linked.</summary>
    /// <param name="ExpectedEmpty">
    /// True for a known placeholder or sponsor slot (title carries <c>[Placeholder]</c> or
    /// <c>TBD</c>) — listed so it is not forgotten, but not counted as a problem.
    /// </param>
    public sealed record SessionItem(int SessionId, string Title, bool ExpectedEmpty);

    /// <summary>What the check found for one edition.</summary>
    public sealed record Result(
        IReadOnlyList<SpeakerItem> SpeakersWithoutSession,
        IReadOnlyList<SpeakerItem> SpeakersWithoutProfile,
        IReadOnlyList<SessionItem> SessionsWithoutSpeakers)
    {
        /// <summary>Real sessions with no speaker that are NOT a known placeholder — the problems.</summary>
        public IReadOnlyList<SessionItem> SessionsNeedingSpeakers =>
            SessionsWithoutSpeakers.Where(s => !s.ExpectedEmpty).ToList();

        /// <summary>Known placeholders / sponsor slots with no speaker yet — shown, not a problem.</summary>
        public IReadOnlyList<SessionItem> ExpectedEmptySessions =>
            SessionsWithoutSpeakers.Where(s => s.ExpectedEmpty).ToList();

        /// <summary>True when something needs an organizer's attention (placeholders alone do not).</summary>
        public bool AnyProblem =>
            SpeakersWithoutSession.Count > 0
            || SpeakersWithoutProfile.Count > 0
            || SessionsWithoutSpeakers.Any(s => !s.ExpectedEmpty);

        /// <summary>"9 speakers on no session · 2 without a speaker profile · 2 sessions with no speakers (+ 6 expected empty)".</summary>
        public string Summary
        {
            get
            {
                var expected = SessionsWithoutSpeakers.Count(s => s.ExpectedEmpty);
                var needing = SessionsWithoutSpeakers.Count - expected;
                return $"{SpeakersWithoutSession.Count} {Plural(SpeakersWithoutSession.Count, "speaker", "speakers")} on no session"
                    + $" · {SpeakersWithoutProfile.Count} without a speaker profile"
                    + $" · {needing} {Plural(needing, "session", "sessions")} with no speakers"
                    + (expected > 0 ? $" (+ {expected} expected empty)" : string.Empty);
            }
        }

        private static string Plural(int n, string one, string many) => n == 1 ? one : many;
    }

    /// <summary>
    /// True when a session title marks a known placeholder or sponsor slot: it contains
    /// <c>[Placeholder]</c> or <c>TBD</c>, case-insensitive.
    /// </summary>
    public static bool IsExpectedEmptyTitle(string? title) =>
        !string.IsNullOrEmpty(title)
        && (title.Contains("[Placeholder]", StringComparison.OrdinalIgnoreCase)
            || title.Contains("TBD", StringComparison.OrdinalIgnoreCase));

    /// <summary>Run the check for one edition.</summary>
    public async Task<Result> CheckAsync(int eventId, CancellationToken ct = default)
    {
        // Real sessions: same edition, not TEST, not legacy test data, not a service slot.
        // ⚠️ ExcludeFromSoMeAnnouncements is NOT a filter here (see the class remarks).
        var realSessions = await _db.Sessions
            .AsNoTracking()
            .Where(s => s.EventId == eventId
                        && !s.UsedForTesting && !s.IsTestData && !s.IsServiceSession)
            .Select(s => new { s.Id, s.Title })
            .ToListAsync(ct);
        var realSessionIds = realSessions.Select(s => s.Id).ToHashSet();

        var links = await _db.SessionSpeakers
            .AsNoTracking()
            .Where(ss => ss.Session.EventId == eventId)
            .Select(ss => new { ss.SessionId, ss.ParticipantId })
            .ToListAsync(ct);

        var onRealSession = links
            .Where(l => realSessionIds.Contains(l.SessionId))
            .Select(l => l.ParticipantId)
            .ToHashSet();
        var sessionsWithAnyLink = links.Select(l => l.SessionId).ToHashSet();

        // Active, non-test Speaker-role participants of this edition.
        var speakers = await _db.Participants
            .AsNoTracking()
            .Where(p => p.EventId == eventId
                        && p.Role == ParticipantRole.Speaker
                        && p.IsActive && !p.IsTestUser)
            .Select(p => new { p.Id, p.FullName, p.CreatedAt })
            .ToListAsync(ct);

        var withProfile = (await _db.SpeakerProfiles
                .AsNoTracking()
                .Where(sp => sp.EventId == eventId)
                .Select(sp => sp.ParticipantId)
                .ToListAsync(ct))
            .ToHashSet();

        static SpeakerItem ToItem(int id, string name, DateTimeOffset created) =>
            new(id, string.IsNullOrWhiteSpace(name) ? $"Participant {id}" : name, created);

        var withoutSession = speakers
            .Where(p => !onRealSession.Contains(p.Id))
            .OrderBy(p => p.FullName, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id)
            .Select(p => ToItem(p.Id, p.FullName, p.CreatedAt))
            .ToList();

        var withoutProfile = speakers
            .Where(p => !withProfile.Contains(p.Id))
            .OrderBy(p => p.FullName, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id)
            .Select(p => ToItem(p.Id, p.FullName, p.CreatedAt))
            .ToList();

        var emptySessions = realSessions
            .Where(s => !sessionsWithAnyLink.Contains(s.Id))
            .Select(s => new SessionItem(s.Id, s.Title ?? string.Empty, IsExpectedEmptyTitle(s.Title)))
            .OrderBy(s => s.ExpectedEmpty)
            .ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.SessionId)
            .ToList();

        return new Result(withoutSession, withoutProfile, emptySessions);
    }
}
