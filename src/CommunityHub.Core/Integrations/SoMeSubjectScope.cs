using CommunityHub.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// §1178 — WHAT MAY NEVER BE ANNOUNCED, asked in ONE place.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-12: <i>"the master class with the 4 organizers should be marked as test and
/// the graphics for security track is wrong as no test session speakers must be included in test in
/// some post"</i> · <i>"guard needed. no test sessions or test sponsor or excluded can exist in some
/// planner"</i>.</para>
///
/// <para>🔴 <b>THE EXCLUSIONS WERE SPREAD OVER FOUR SERVICES AND NO TWO AGREED.</b> Measured against
/// the code, not guessed:</para>
/// <list type="table">
/// <item><term>planner (<see cref="SoMeScheduleService"/>)</term><description>service · IsTestData ·
///   all-speakers-are-test · title patterns — but NOT <c>ExcludeFromSoMeAnnouncements</c> and NOT
///   <c>UsedForTesting</c></description></item>
/// <item><term>graphics sweep (<c>SoMeBundleBuildService</c>)</term><description>service · IsTestData ·
///   test SPEAKERS — and nothing else at all</description></item>
/// <item><term>approval gate</term><description><c>ExcludeFromSoMeAnnouncements</c> only</description></item>
/// <item><term>auto-approve</term><description><c>ExcludeFromSoMeAnnouncements</c> only, and it merely
///   un-approves</description></item>
/// </list>
///
/// <para>🔑 <b>So "excluded" meant four different things, and the graphics sweep had the narrowest
/// definition of the four</b> — which is exactly why the Security track GIF could contain speakers
/// from a session nothing else in the campaign would touch. A graphic is not a lesser surface: it is
/// the picture that goes on the company page.</para>
///
/// <para>🔴 <b>AND THE ONE FLAG EVERY SoMe READER KEYED ON WAS NEVER WRITTEN BY ANYTHING.</b>
/// <see cref="Domain.Session.IsTestData"/> shipped with §909 on 2026-08-06 — column, default
/// <c>false</c>, four readers — and no UI, no service, no seed and no migration has ever set it. Its
/// own doc comment names the two rows it was built for ("Test Master Class" and "Test Session", four
/// REAL speakers each), and neither was ever flagged, because there was no way to flag them. Meanwhile
/// the Sessions page has a button reading <b>"Mark as TEST session"</b> that writes
/// <see cref="Domain.Session.UsedForTesting"/> — a different column, which the SoMe engine did not
/// read. ⇒ He pressed the button that says what he wanted, and the campaign never heard about it.
/// <c>[[ceh-count-the-shared-things]]</c></para>
///
/// <para>🔒 <b>So <see cref="Domain.Session.UsedForTesting"/> now excludes from SoMe too.</b> §299
/// 4.5/b8 promises a flagged session <i>"never appears on any PUBLIC page"</i>, and a LinkedIn
/// company-page post is the most public surface CEH has — announcing one was always a contradiction
/// of the flag's own contract. <see cref="Domain.Session.IsTestData"/> is KEPT, not replaced: it stays
/// the campaign-only flag for a session that is real everywhere else, which is the distinction
/// <c>MasterClassSignupService</c> §972 deliberately drew and this does not undo.</para>
///
/// <para>⚠️ <b>Widening an exclusion silently drops content, so every rule here is one he has already
/// set by hand.</b> Nothing is inferred from a title, a date or a name — the derived all-test-speakers
/// rule is the single exception and it predates this class (§905), kept so fixtures seeded before the
/// flags existed still behave.</para>
/// </remarks>
public sealed class SoMeSubjectScope
{
    private readonly CommunityHubDbContext _db;

    public SoMeSubjectScope(CommunityHubDbContext db) => _db = db;

    /// <summary>Why one session is out of the campaign, in words fit for a log line.</summary>
    public sealed record Excluded(int SessionId, string Title, string Reason);

    /// <summary>
    /// Every session in this edition that must never be announced and never rendered into a graphic,
    /// with the reason.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Service sessions are NOT here.</b> A break or a lunch is excluded by every caller
    /// already, as part of "what counts as a session at all" rather than as a campaign decision —
    /// folding it in would make this set mean two things.
    /// </remarks>
    public async Task<IReadOnlyList<Excluded>> ExcludedSessionsAsync(
        int eventId, CancellationToken ct = default)
    {
        var rows = await _db.Sessions
            .AsNoTracking()
            .Where(s => s.EventId == eventId && !s.IsServiceSession)
            .Select(s => new
            {
                s.Id,
                s.Title,
                s.IsTestData,
                s.UsedForTesting,
                s.ExcludeFromSoMeAnnouncements,
                HasSpeakers = s.SessionSpeakers.Any(),
                // ⚠️ An unresolvable participant is NOT test, so a session holding one is never
                // classified as a fixture and dropped from the campaign (§905's null-tolerance).
                AllSpeakersTest = s.SessionSpeakers.All(
                    ss => ss.Participant != null && ss.Participant.IsTestUser),
            })
            .ToListAsync(ct);

        // His own title filter (§927) — parsed once, matched in memory because the wildcard is his
        // syntax and not SQL's.
        var patterns = SoMeTitleExclusions.Parse(await _db.SoMeSettings
            .AsNoTracking()
            .Where(s => s.EventId == eventId)
            .Select(s => s.ExcludedSessionTitlePatterns)
            .FirstOrDefaultAsync(ct));

        var excluded = new List<Excluded>();

        foreach (var s in rows)
        {
            // 🔑 The FLAGS first and the inference last, so the reason names the thing he can see and
            // change. A session that is both flagged and all-test reads as flagged, which is true and
            // actionable; "every speaker is a test user" would send him looking at participants.
            var reason =
                s.IsTestData ? "marked as test data (IsTestData)"
                : s.UsedForTesting ? "marked as a TEST session (UsedForTesting)"
                : s.ExcludeFromSoMeAnnouncements ? "excluded from social-media announcements"
                : SoMeTitleExclusions.IsExcluded(s.Title, patterns) ? "matches an excluded title pattern"
                : s.HasSpeakers && s.AllSpeakersTest ? "every speaker on it is a test user"
                : null;

            if (reason is not null) excluded.Add(new Excluded(s.Id, s.Title ?? $"#{s.Id}", reason));
        }

        return excluded;
    }

    /// <summary>The same set as ids, for a query filter.</summary>
    public async Task<IReadOnlySet<int>> ExcludedSessionIdsAsync(
        int eventId, CancellationToken ct = default) =>
        (await ExcludedSessionsAsync(eventId, ct)).Select(x => x.SessionId).ToHashSet();

    /// <summary>
    /// §1178 — every <see cref="Domain.SoMePost.SubjectKey"/> that must not be in the planner:
    /// excluded sessions, excluded sponsor speaker sessions, and test sponsor companies.
    /// </summary>
    /// <remarks>
    /// <para>🔑 <b>Tier keys are deliberately absent.</b> A tier is a real tier even when one test
    /// company sits in it — the tier post's own content already excludes test companies
    /// (<see cref="TestDataScope"/>), and dropping the whole tier because of a fixture would delete a
    /// real announcement about real sponsors.</para>
    /// <para>⚠️ Track keys are absent for the same reason and a stronger one: a track is DERIVED from
    /// its sessions, so a track that exists only because of an excluded session has no sessions left
    /// to announce and the planner never proposes it. Listing tracks here would need a name→slug
    /// round-trip that <see cref="SoMeSubjectGraphic"/> already documents as one-way.</para>
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, string>> ExcludedSubjectKeysAsync(
        int eventId, CancellationToken ct = default)
    {
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var s in await ExcludedSessionsAsync(eventId, ct))
        {
            keys[$"session:{s.SessionId}"] = $"session \"{s.Title}\" — {s.Reason}";
        }

        // §1060(h)'s sibling flag on a SPONSOR speaker session, which the auto-approve sweep already
        // withdraws for. Same decision, same treatment.
        var sponsorSessions = await _db.SponsorSessions
            .AsNoTracking()
            .Where(s => s.EventId == eventId && s.ExcludeFromSoMeAnnouncements)
            .Select(s => new { s.Id, s.Title })
            .ToListAsync(ct);

        foreach (var s in sponsorSessions)
        {
            keys[SoMeSponsorSessionKey.For(s.Id)] =
                $"sponsor session \"{s.Title ?? $"#{s.Id}"}\" — excluded from social-media announcements";
        }

        // 🔒 The SAME sponsor rule the planner and the tier content already use — the flag he sets,
        // or a company whose every contact is a test user (§905).
        foreach (var companyId in await TestDataScope.TestSponsorCompanyIdsAsync(_db, eventId, ct))
        {
            keys[$"sponsor:{companyId}"] = $"sponsor company {companyId} — test data";
        }

        return keys;
    }
}
