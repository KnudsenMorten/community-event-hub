namespace CommunityHub.Core.Integrations.Sessions;

/// <summary>
/// §1240 — the two Sessionize CONVENTIONS that decide whether a session reaches CEH at all, and
/// what its track means. Pure string rules, deliberately in ONE place: the importer, the push
/// engine and the tests must agree on them byte for byte, and a convention copied into three files
/// is a convention that drifts.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-20, on the ELDK27 programme going into Sessionize:
/// *"sponsor sessions … must not be imported into ceh, as they will come from ceh already"* ·
/// *"i have created Common in the event track, but dont create that in zoho. these sessions ashould
/// be tagged as common session like welcome keynote and closing keynote"*.</para>
///
/// <para>🔴 <b>Where each rule bites, and why they differ.</b> <see cref="NoImportMarker"/> is
/// checked at the <b>IMPORT</b>: that row is not a session anywhere, so it never enters the hub.
/// <see cref="CommonTrackName"/> is applied at the import but bites at the <b>PUSH</b>, because the
/// Backstage sessions API is CREATE-ONLY — no update, no delete — so anything it writes wrongly is
/// wrong in public until a person fixes it by hand.</para>
/// </remarks>
public static class SessionizeConventions
{
    // 🛑 THERE IS NO `[Placeholder]` RULE. DO NOT ADD ONE BACK.
    //
    // One existed for part of 2026-09-20 — placeholders imported but were held back from Backstage
    // — and the operator REVERSED it the same day: *"i have changed my mind rgr [Placeholder]. i
    // want it to show on zoho, so change logic and remove that gate"*. A `[Placeholder]` session is
    // an ORDINARY session that happens to carry a working title: it imports, it publishes, and the
    // marker travels with the title until he renames it in Sessionize (titles are Sessionize-owned,
    // §999, so the rename arrives on its own).
    //
    // 🔑 Written here rather than nowhere, because the ABSENCE is the surprising part. The live
    // titles still read "… [Placeholder]" — as a POSTFIX since 2026-09-20 ("Identity \"black belt\"
    // session [Placeholder]") — so the next reader will see the word, find only the no-import rule,
    // and reasonably conclude a gate went missing.

    /// <summary>
    /// The title marker that means "this row exists in Sessionize for PLANNING ONLY — it must never
    /// become a CEH session at all".
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-09-20: *"sponsor sessions … must not be imported into ceh, as they will
    /// come from ceh already … it will conflict if they come into ceh as we will then have 2
    /// sessions at the same time and i will never rename those. we had them in sessionize for the
    /// planning purpose"*.</para>
    ///
    /// <para>🔑 <b>The sponsor sessions are owned by CEH.</b> The Sessionize row is a block in the
    /// planner grid holding the slot; the real session is created hub-side by the sponsor. Importing
    /// it would put two sessions in one slot — and unlike a working title, this one is never
    /// renamed, so nothing would ever clear it.</para>
    ///
    /// <para>🔒 Matched ANYWHERE in the title, case-insensitively, never as a prefix: he moved the
    /// markers to the END of the title on 2026-09-20, and a prefix rule would have silently stopped
    /// matching the moment he did.</para>
    /// </remarks>
    public const string NoImportMarker = "[NoImport]";

    /// <summary>
    /// The Sessionize "Suggested Event Track" item that is NOT a track: it marks a session that
    /// belongs to every track (welcome keynote, closing keynote, and anything else common).
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-09-20: *"we already have a common setting on per session level which is
    /// equivalent to track = Common in sessionize"* — i.e. this maps onto the EXISTING
    /// <see cref="Domain.Session.IsCommonForAllTracks"/> tick box (§1011), it is not a new
    /// concept.</para>
    ///
    /// <para>🔴 <b>DO NOT "MAKE THIS CONSISTENT" WITH THE TITLE MARKER.</b> The obvious tidy-up is a
    /// second marker, <c>[Common]</c> in the title. He raised it and ruled it out in the same
    /// breath: *"no then it comes into displayname in zoho and ceh and we want not that"*.</para>
    ///
    /// <para>🔑 <b>And that is the whole rule for choosing between a title marker and a category.</b>
    /// The title IS the public display name. <see cref="NoImportMarker"/> can live there safely
    /// BECAUSE that row never reaches an audience — it never enters CEH at all. A common session is
    /// the opposite: the welcome keynote and the closing are as public as a session gets, so a
    /// marker in its title would be published, reading "[Common] Keynote: TBD" on the live agenda
    /// and in the hub. ⇒ <b>A marker may only go in the title of a session nobody will see.</b>
    /// Everything else belongs in a category — which is what the track is.</para>
    /// </remarks>
    public const string CommonTrackName = "Common";

    /// <summary>True when a title carries the <see cref="NoImportMarker"/> (anywhere, any case).</summary>
    public static bool IsNoImport(string? title) =>
        !string.IsNullOrWhiteSpace(title)
        && title.Contains(NoImportMarker, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when a Sessionize track label is the <see cref="CommonTrackName"/> marker rather than a
    /// real track. Trimmed + case-insensitive, like every other track comparison in the pipeline.
    /// </summary>
    public static bool IsCommonTrack(string? track) =>
        !string.IsNullOrWhiteSpace(track)
        && string.Equals(track.Trim(), CommonTrackName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The error line when somebody has typed the <see cref="CommonTrackName"/> marker into a hub
    /// session's Track field, where it would otherwise be created as a real Backstage track.
    /// </summary>
    /// <remarks>
    /// 🔑 A session that is properly flagged <see cref="Domain.Session.IsCommonForAllTracks"/> never
    /// reaches this: §1240d creates it with NO track, which is the correct Backstage state for it
    /// (live-verified §1011 — the chip is <c>track: null</c>). This line is for the other case: an
    /// unflagged session carrying the literal word as its track, which is a data error worth failing
    /// loudly rather than quietly inventing a track nothing can delete.
    /// </remarks>
    public const string CommonTrackTypedAsTrackError =
        "\"" + CommonTrackName + "\" is not a track — it is the \"Common for all tracks\" marker. "
        + "Tick that box on the session instead; CEH will then create it in Backstage with no track, "
        + "which is how Backstage shows a common session. No \"" + CommonTrackName + "\" track was "
        + "created.";
}
