using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// §1125 — the fields the WEBSHOP (Company Manager) OWNS, and what "owns" actually means when the
/// value changes.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-25: <i>"a sponsor surveil has fixed the url in the webshop from
/// http://surveil.co to https://surveil.co but it is not syncing into ceh"</i> ·
/// <i>"it must always pull from sponsor webshop"</i>.</para>
///
/// <para>🔴 <b>THE BUG WAS A CONTRADICTION BETWEEN TWO CORRECT DECISIONS.</b> §41b built the
/// webshop↔CEH reconcile as <b>FILL-BLANK</b> — <i>"NEVER overwrites a non-blank value"</i> — which
/// is the right rule for a field both sides may edit. §1081 then made the website <b>authoritative
/// on the webshop side</b> (operator 2026-08-13: <i>"website url comes from webshop … that one is
/// authoritative for that field"</i>), rendered the CEH input read-only, and dropped the CEH write —
/// but left the SYNC on §41b's fill-blank rule.</para>
///
/// <para>⇒ CEH could take the website <b>exactly once</b>, when its own field happened to be blank,
/// and then ignored every later correction for ever. The page meanwhile told the sponsor, in so many
/// words, <i>"Change it there and it syncs back here automatically."</i> <b>The UI promised
/// authority the engine did not implement</b>, so a sponsor fixing their own URL saw nothing happen
/// and had no way to fix it from either side — the CEH field was read-only by then.</para>
///
/// <para>🔑 <b>The lesson worth keeping:</b> "X is authoritative" is a statement about what happens
/// on <b>CHANGE</b>, not about who supplies the first value. A fill-blank rule expresses "whoever
/// gets there first wins", which is the opposite of authority — and the two are indistinguishable
/// until somebody edits the value a second time. That is why this went unnoticed: it worked
/// perfectly for every sponsor who never corrected their URL.</para>
///
/// <para>✅ <b>§1126 — ALL THREE, not just the website.</b> Operator 2026-08-25, immediately after
/// §1125 shipped: <i>"linkedin + twitter is also coming from webshop"</i> · <i>"it must also
/// overwrite as webshop is authoritative"</i>. This SUPERSEDES §1081's note that the other two kept
/// fill-blank <i>"deliberately, not by oversight"</i> — that recorded the limit of what he had been
/// asked about at the time, and he has now answered the wider question.</para>
///
/// <para>🔴 <b>The overwrite forced a UI change, and shipping one without the other would have been
/// worse than the bug.</b> LinkedIn and Twitter were EDITABLE in CEH. An authoritative webshop plus
/// an editable CEH field means a sponsor types a LinkedIn URL, saves it, is told it saved — and the
/// next sync silently reverts it. That is a trap, not a limitation: the person cannot see it happen
/// and has no reason to look. ⇒ Both inputs are now read-only with the same "Change on the webshop"
/// hand-off the website has, AND the CEH writes are dropped — §1081's own reasoning, quoted: a
/// <i>"readonly input still POSTs its value"</i>, so removing the write is what makes read-only real
/// rather than cosmetic.</para>
///
/// <para>🔒 <b>Defined ONCE because the block is duplicated.</b> The identical fill-blank code lives
/// in <c>SponsorZohoSyncService.ReconcileWithWebshopAsync</c> AND
/// <c>SponsorZohoProvisionService</c>. Fixing one and not the other would have left the bug alive on
/// the provisioning path — the §1124 lesson, one release later.</para>
/// </remarks>
public static class WebshopOwnedFields
{
    /// <summary>
    /// Apply the webshop's website to <paramref name="info"/>. Returns true when CEH changed.
    /// </summary>
    /// <remarks>
    /// <para>⚠️ <b>A BLANK webshop value does NOT blank CEH.</b> "Always pull from the webshop"
    /// governs what the webshop SAYS; it is not an instruction to erase a sponsor's website because
    /// the webshop field happens to be empty. Blanking would remove a live link from the public
    /// event site as a side effect of a sync, which is not a change anybody asked for and is not
    /// visible to the person it affects. The callers already handle the empty case the other way
    /// round — they PUSH CEH's value into a blank webshop field — so after one pass the webshop is
    /// no longer blank and the authority rule takes over naturally.</para>
    ///
    /// <para>Compared ORDINALLY, so <c>http://</c> → <c>https://</c> registers as the change it is.
    /// A case-insensitive compare would have masked scheme- and host-case corrections, which are
    /// exactly the kind being made here.</para>
    /// </remarks>
    public static bool ApplyWebsite(SponsorInfo info, string? webshopWebsite)
    {
        if (info is null) return false;
        if (!Changed(info.WebsiteUrl, webshopWebsite, out var value)) return false;

        info.WebsiteUrl = value;
        return true;
    }

    /// <summary>
    /// §1126 — apply the webshop's LinkedIn URL. Same authority rule as
    /// <see cref="ApplyWebsite"/>, including the blank-does-not-erase guarantee.
    /// </summary>
    public static bool ApplyLinkedIn(SponsorInfo info, string? webshopLinkedIn)
    {
        if (info is null) return false;
        if (!Changed(info.LinkedInUrl, webshopLinkedIn, out var value)) return false;
        info.LinkedInUrl = value;
        return true;
    }

    /// <summary>
    /// §1126 — apply the webshop's Twitter/X URL. Same authority rule as
    /// <see cref="ApplyWebsite"/>, including the blank-does-not-erase guarantee.
    /// </summary>
    public static bool ApplyTwitter(SponsorInfo info, string? webshopTwitter)
    {
        if (info is null) return false;
        if (!Changed(info.TwitterUrl, webshopTwitter, out var value)) return false;
        info.TwitterUrl = value;
        return true;
    }

    /// <summary>
    /// §1126 — apply ALL THREE webshop-owned URLs at once. Returns true when anything changed.
    /// </summary>
    /// <remarks>
    /// 🔒 The callers duplicate this block, so they call THIS rather than the three individually —
    /// a caller that remembers two of the three is precisely the §1125 failure repeating.
    /// </remarks>
    public static bool ApplyAll(
        SponsorInfo info, string? website, string? linkedIn, string? twitter)
    {
        var changed = ApplyWebsite(info, website);
        if (ApplyLinkedIn(info, linkedIn)) changed = true;
        if (ApplyTwitter(info, twitter)) changed = true;
        return changed;
    }

    /// <summary>
    /// The shared authority test: a non-blank incoming value that differs ordinally from what CEH
    /// holds. Blank incoming ⇒ no change (never an erase).
    /// </summary>
    private static bool Changed(string? current, string? incoming, out string value)
    {
        value = string.Empty;
        var trimmed = incoming?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return false;
        if (string.Equals(current?.Trim(), trimmed, StringComparison.Ordinal)) return false;

        value = trimmed;
        return true;
    }
}
