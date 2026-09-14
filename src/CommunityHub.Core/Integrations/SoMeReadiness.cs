using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// 🔴 §1206 — IS THIS POST READY, AND IF NOT, IN A FEW WORDS, WHY? One answer, shared by every
/// screen that shows the plan.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-12: <i>"does the 19 exist in the planner, that was my questions, if no,
/// then add then, but show then as not ready, like some graphics missing"</i>.</para>
///
/// <para>✅ <b>They do exist.</b> A held-back post is a planned post: it has a date, it sits in the
/// queue, it holds a seat in the capacity report, and §1205 keeps its date honest. Readiness gates
/// APPROVAL, never PLACEMENT — the planner has never refused to plan a subject for being unready.</para>
///
/// <para>🔴 <b>What was missing is the label.</b> The reason existed in exactly one place per
/// audience and nowhere he works: <see cref="SoMeApprovalGate"/> composes a sentence naming what is
/// owed and who owes it, a sponsor's own page renders it as <i>"⏳ Waiting on: …"</i> (§1027), and
/// §1204 grouped it on the settings page. But the QUEUE showed only a bare "no graphic" chip — one
/// blocker out of a dozen — and the CALENDAR said "held — not approved", which states that a human
/// has not clicked and not that the post COULD not be approved. Two different situations wearing the
/// same badge is the §335 trap: a post nobody has got to and a post nobody CAN approve look
/// identical.</para>
///
/// <para>🔑 <b>One class, because four screens asking the same question four ways is the §1178
/// failure</b> — that morning cost a day to four divergent definitions of "excluded".
/// <c>[[ceh-count-the-shared-things]]</c></para>
///
/// <para>⚠️ <b>Only HELD posts are asked about.</b> The gate is a query per post, so the set is
/// narrowed to the posts the answer can change anything for: not approved and not published. An
/// approved post's state already says it is ready; a published one is history. That is the same
/// condition <c>SoMeAnnouncementQuery.ProjectWithBlockersAsync</c> uses, and on this edition it is a
/// few dozen rows rather than the whole campaign.</para>
/// </remarks>
public sealed class SoMeReadiness
{
    private readonly CommunityHubDbContext _db;

    public SoMeReadiness(CommunityHubDbContext db) => _db = db;

    /// <summary>
    /// The held posts that are NOT ready, as post id → the gate's full sentence.
    /// </summary>
    /// <remarks>
    /// 🔒 A post that is ready is simply absent from the dictionary — so "ready" and "not asked" are
    /// the same lookup, and a caller cannot accidentally render a blank reason as a blocker.
    /// </remarks>
    public async Task<IReadOnlyDictionary<int, string>> HeldReasonsAsync(
        IReadOnlyCollection<SoMePost> posts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(posts);

        var gate = new SoMeApprovalGate(_db);
        var reasons = new Dictionary<int, string>();

        foreach (var post in posts)
        {
            if (!IsHeld(post)) continue;

            if (await gate.BlockedReasonAsync(post, ct) is { Length: > 0 } why)
            {
                reasons[post.Id] = why;
            }
        }

        return reasons;
    }

    /// <summary>Held = the approval is still open. Not approved, not published, not withdrawn.</summary>
    public static bool IsHeld(SoMePost post)
    {
        ArgumentNullException.ThrowIfNull(post);
        return !post.IsDeleted
               && !post.IsActive
               && post.Status != SoMePostStatus.Published
               && post.PublishedAtUtc is null;
    }

    /// <summary>
    /// §1204 — the ACTION clause of a blocker: the part that says what has to happen.
    /// </summary>
    /// <remarks>
    /// <para>🔑 The gate's sentences carry a tail of reassurance (<i>"…and becomes approvable as
    /// soon as that is filled in"</i>) which is right on one post and noise repeated twenty times.
    /// Cut at the first <c>", so "</c> or sentence end.</para>
    ///
    /// <para>⚠️ It keeps the SUBJECT — <i>"Surveil has not delivered their social-media text"</i> —
    /// because that is who he has to chase. Grouping is what collapses the repetition, not
    /// truncation; §854's rule is name it, do not count it.</para>
    /// </remarks>
    public static string Short(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return string.Empty;

        var cut = reason.IndexOf(", so ", StringComparison.OrdinalIgnoreCase);
        if (cut < 0) cut = reason.IndexOf(". ", StringComparison.Ordinal);

        var text = (cut > 0 ? reason[..cut] : reason).Trim();
        return text.Length > 160 ? text[..160].TrimEnd() + "…" : text;
    }

    /// <summary>
    /// A chip-sized label — a handful of words for a table cell, in HIS vocabulary.
    /// </summary>
    /// <remarks>
    /// <para>🔑 <b>He named the shape himself:</b> <i>"show then as not ready, like some graphics
    /// missing"</i>. A chip has room for the CATEGORY of what is missing; the full sentence stays on
    /// the row's tooltip and on the settings page, so nothing is lost by shortening.</para>
    ///
    /// <para>⚠️ Keyword-matched against the gate's own wording, and it falls back to
    /// <c>"not ready"</c> rather than guessing. A wrong label is worse than a generic one: it would
    /// send him chasing the wrong person.</para>
    /// </remarks>
    public static string Chip(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return "not ready";

        var r = reason.ToLowerInvariant();

        // Order matters: a sponsor post blocked on the LOGO also mentions the graphic it feeds.
        if (r.Contains("logo")) return "logo missing";
        if (r.Contains("graphic")) return "graphic missing";
        if (r.Contains("social-media text") || r.Contains("social text")
            || r.Contains("socialmediaintro")) return "sponsor text missing";
        if (r.Contains("description") || r.Contains("abstract")) return "description missing";
        if (r.Contains("linkedin")) return "LinkedIn URL missing";
        if (r.Contains("speaker")) return "speaker missing";
        if (r.Contains("exclude")) return "excluded";

        return "not ready";
    }
}
