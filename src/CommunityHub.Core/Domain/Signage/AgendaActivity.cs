namespace CommunityHub.Core.Domain.Signage;

/// <summary>
/// §754 — ONE activity on the event floor, mirrored from the Zoho Backstage agenda for the signage
/// screens. Presentations, master classes, breaks, registration, lunch and the party all live here
/// side by side.
/// </summary>
/// <remarks>
/// <para>🔒 <b>This is a CACHE, not a second place to author an agenda.</b> Zoho Backstage is the
/// source of truth (§754 §4) — it holds every activity on the floor, including the breaks and meals
/// CEH's own <c>Sessions</c> table does not model at all. Nothing writes here except
/// <c>SignageAgendaSyncService</c>, and every column is overwritten on the next sync. Editing a row
/// by hand is pointless: the 5-minute poll reverts it.</para>
///
/// <para>🔑 <b>Why not reuse <c>Session</c>?</b> Because the screens must show the 10:00 coffee break
/// and the 18:00 party, and a CEH <c>Session</c> is a talk with a speaker, a track and a Sessionize
/// origin. Modelling a break as a session would have leaked into the public agenda, the speaker
/// pages, the evaluation attribution and the session-change alerts — every consumer of that table
/// would need a new "…but not this kind" filter.</para>
///
/// <para>🔒 <b><see cref="BackstageSessionId"/> is the natural key</b> (unique per edition). The sync
/// upserts on it, so a title or room change in Zoho updates the row in place and the screen keeps its
/// sort position instead of a card jumping pages. It is the same stable id the §301b self-heal and
/// §302 change detection key on.</para>
///
/// <para>⚠️ <b>An empty pull must never empty this table.</b> A failed read and a genuinely-cleared
/// agenda look identical from here, and the consequence of getting it wrong is 15 blank screens
/// mid-event. The sync keeps the last-good rows on any failure — see the fail-safe contract on
/// <c>SignageAgendaSyncService</c>.</para>
/// </remarks>
public class AgendaActivity
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>
    /// The Zoho Backstage session id. Opaque, stable, and the natural key this row upserts on.
    /// </summary>
    public string BackstageSessionId { get; set; } = string.Empty;

    /// <summary>The card's headline. Always present — Backstage requires a title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>UTC. When the activity starts; drives the §5 hour-slot placement.</summary>
    public DateTimeOffset StartsAt { get; set; }

    /// <summary>
    /// UTC. DERIVED as <c>StartsAt + duration</c> — Backstage returns a <c>duration</c> in minutes
    /// and no end time at all.
    /// </summary>
    /// <remarks>
    /// 🔑 It is stored rather than derived at render time because §5's rule — a multi-hour activity
    /// appears in EVERY hour slot it overlaps — is a query over the end time, and 15 screens polling
    /// every few seconds should not each recompute it. An activity that arrives with no usable
    /// duration is SKIPPED by the sync with a count, never given a guessed end: a wrong end time
    /// silently drops a session out of a slot, which looks exactly like a cancellation.
    /// </remarks>
    public DateTimeOffset EndsAt { get; set; }

    /// <summary>The venue room (Backstage "hall"), resolved from the session's <c>venue</c> id.</summary>
    public string? Room { get; set; }

    /// <summary>The track name, resolved from the session's <c>track</c> id. First sort key (§6).</summary>
    public string? Track { get; set; }

    /// <summary>
    /// Zoho's <c>session_type</c> — how "Break", "Registration", "Lunch" and "Party" reach the
    /// screens at all.
    /// </summary>
    /// <remarks>
    /// 🔒 <b>A STRING, deliberately not an enum.</b> Session types are event-specific configuration
    /// in Backstage: the operator can add one in the UI at any time. An enum would turn that into a
    /// CEH deploy, and an unrecognised value would become an exception on a public screen during an
    /// event. Signage only ever displays it, so an unknown type costs nothing.
    /// </remarks>
    public string? ActivityType { get; set; }

    /// <summary>
    /// The speaker names as the card prints them: joined with ", " in display order, or null for an
    /// activity with no speakers (a break, registration, lunch).
    /// </summary>
    /// <remarks>
    /// 🔑 Stored PRE-JOINED because the signage card renders exactly one speaker line and nothing
    /// else reads this table. A join table would create a second speaker identity inside CEH — one
    /// that no participant record backs and that the §58 speaker change detection would then have to
    /// know about.
    /// </remarks>
    public string? Speakers { get; set; }

    /// <summary>
    /// The 1-based Backstage agenda day this activity was pulled from (the <c>?day=</c> index).
    /// </summary>
    /// <remarks>
    /// Kept as Zoho reports it rather than derived from <see cref="StartsAt"/>, so the holding
    /// screen's "Day 2 starts 09:00" (§10) and the agenda-day grouping agree with what the operator
    /// sees in Backstage. A day boundary computed from a UTC timestamp would disagree with Zoho for
    /// anything scheduled late in the evening.
    /// </remarks>
    public int DayIndex { get; set; }

    /// <summary>UTC. When this row was last confirmed against Zoho — §4 sync health.</summary>
    public DateTimeOffset LastSyncedAt { get; set; }
}
