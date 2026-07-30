using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CommunityHub.Core.Integrations.Graphics;

/// <summary>
/// REQUIREMENTS §664 — the ledger occasion key for the "your promo graphics are ready" mail.
///
/// <para><b>What went wrong.</b> The key was <c>graphics-ready:{participantId}</c>: no graphic, no
/// session, no date. It therefore meant "this speaker has been told about graphics" FOREVER. The
/// three triggers (organizer release click, quarter-hourly SharePoint sync, daily safety sweep)
/// collapsing to one mail is correct and deliberate — but so did a genuinely NEW graphic released
/// weeks later, which is not.</para>
///
/// <para><b>The rule now:</b> the key carries a hash of the speaker's RELEASED graphic ids. Same set
/// ⇒ same key ⇒ still exactly one mail no matter how many triggers fire. A new graphic ⇒ a different
/// set ⇒ a new occasion ⇒ one more mail. Removing a graphic also changes the set; that is acceptable
/// (a re-release is worth an announcement) and cannot loop, because the ledger row for that key
/// persists.</para>
/// </summary>
public static class GraphicsReadyOccasion
{
    /// <summary>The reminder type, unchanged — it is what the mail is filed under.</summary>
    public const string ReminderType = "speaker-graphics-ready";

    /// <summary>The pre-§664 key shape, still read when backfilling. Never written any more.</summary>
    public static string LegacyKeyFor(int participantId) => $"graphics-ready:{participantId}";

    /// <summary>
    /// The occasion key for a speaker and the exact set of graphics they can currently see.
    ///
    /// <para>Ids are DE-DUPLICATED and SORTED before hashing, so the key depends only on which
    /// graphics are released — not on the order a query happened to return them in. Without that,
    /// the same set could hash two ways and the mail would send twice.</para>
    /// </summary>
    public static string KeyFor(int participantId, IEnumerable<int> releasedGraphicIds)
    {
        var ids = releasedGraphicIds.Distinct().OrderBy(i => i).ToList();

        // An empty set is given its own stable marker rather than a hash of "". It should not occur
        // (the caller only builds keys for speakers who HAVE released graphics), but during the
        // §664 backfill it is the normal answer for "what had they been told about?" when their
        // graphics were all released after the notification.
        if (ids.Count == 0) return $"graphics-ready:{participantId}:none";

        var joined = string.Join(",", ids.Select(i => i.ToString(CultureInfo.InvariantCulture)));
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(joined));

        // 8 hex chars is ample: the value only has to distinguish one speaker's successive graphic
        // sets from each other, and a collision would merely skip one announcement.
        var hash = Convert.ToHexString(digest, 0, 4).ToLowerInvariant();
        return $"graphics-ready:{participantId}:{hash}";
    }
}
