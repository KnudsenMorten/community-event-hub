using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// §917 — THE CURRENT GRAPHIC FOR A SUBJECT, resolved LATE.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-06: <i>"we agree that the picture + sect with mention of speakers etc are
/// captured/build before the post happens, so it takes the current list of speaker - and not stamp
/// them at planning time. so i dont have to worry about wrong speaker assigned"</i>.</para>
///
/// <para>🔴 <b>He was right about the text and wrong about the picture — because §915 made him
/// wrong.</b> §901 already resolves the WORDS at publish, so a speaker added later appears. But
/// §915 copied the graphic's FILE NAME onto the post at plan time, which is stamping, and for a
/// SESSION the name is not stable: one speaker renders <c>session-12.png</c>, two render
/// <c>session-12.gif</c>. A second speaker joining therefore left the post pointing at a file that
/// no longer represented the session — §767 Round 8's warning, walked into
/// (<i>"a queued SoMe post must point at the asset ROW and not a copied path"</i>).</para>
///
/// <para>🔒 So the stored <see cref="SoMePost.ImageRef"/> is a DEFAULT, not the answer. Publishing
/// asks this class for the subject's graphic as it stands NOW. The two properties he asked for both
/// hold: he never picks a picture, and the picture can never be stale.</para>
///
/// <para>⚠️ Types 1–4 only. Type 5 names its own file in the deck (§828.7) and an ad-hoc post has no
/// subject at all — for those the stored ref IS the answer, and overriding it would discard a choice
/// somebody made deliberately.</para>
/// </remarks>
public sealed class SoMeSubjectGraphic
{
    private readonly CommunityHubDbContext _db;

    public SoMeSubjectGraphic(CommunityHubDbContext db) => _db = db;

    /// <summary>True when this post's picture belongs to its SUBJECT rather than to the post.</summary>
    public static bool IsSubjectOwned(SoMePost post) =>
        post.TemplateKind is SoMeTemplateKind.SpeakerTracks
            or SoMeTemplateKind.Session
            or SoMeTemplateKind.SponsorCategory
            or SoMeTemplateKind.Sponsor;

    /// <summary>
    /// The file name of the subject's CURRENT graphic, or null when it has none (or the post owns
    /// its own picture).
    /// </summary>
    public async Task<string?> CurrentFileNameAsync(SoMePost post, CancellationToken ct = default)
    {
        if (!IsSubjectOwned(post) || string.IsNullOrWhiteSpace(post.SubjectKey)) return null;

        var map = await FileNamesAsync(post.EventId, ct);
        return Lookup(map, post.SubjectKey);
    }

    /// <summary>
    /// Every subject's graphic file name for one edition, keyed by the post's own
    /// <see cref="SoMePost.SubjectKey"/>.
    /// </summary>
    /// <remarks>
    /// 🔑 The join is asymmetric, and this is the single place that reconciles it:
    /// <list type="bullet">
    /// <item>Track — asset <c>track:{SLUG}</c> vs post <c>track:{Display Name}</c></item>
    /// <item>Session — <c>session:{id}</c> both sides</item>
    /// <item>Sponsor — <c>sponsor:{companyId}</c> both sides</item>
    /// <item>Tier — asset <c>sponsor-tier:{tier}</c> vs post <c>tier:{tier}</c></item>
    /// </list>
    /// ⚠️ <b>Newest wins</b> where a subject has several assets: a rebuilt graphic is the current
    /// artwork, and an unpublished post should carry it.
    /// </remarks>
    public async Task<Dictionary<string, string>> FileNamesAsync(
        int eventId, CancellationToken ct = default)
    {
        var rows = await _db.GraphicAssets
            .AsNoTracking()
            // 🔒 §1060(g) — RELEASED ONLY. Every other consumer of GraphicAssets already filters this
            // (BrandingGraphicsProvider and SpeakerGraphicVisibility both label it "THE GATE";
            // SpeakerGraphicsReadyNotifier and Organizer/Sessions do the same) — and this class, the
            // one on the path that publishes artwork TO THE PUBLIC, did not.
            //
            // ⚠️ The claim that used to stand here — *"since §784.12(a) graphics are BORN Released,
            // this filter removes nothing"* — was FALSE FOR SPONSORS, and §1178 is what it cost.
            //
            // 🔴 §1178 — A SPONSOR ROW IS BORN `Generated`, ON PURPOSE, AND FOR A DIFFERENT GATE.
            // `GraphicsService.InitialStatusFor` returns `Generated` for `GraphicAssetType.Sponsor`
            // alone, with its own reasoning: sponsor artwork is internal-only and it feeds
            // `BrandingGraphicsProvider`, "auto-releasing them would change which artwork the branding
            // surfaces pick up, which is a different decision nobody has made". That reasoning is
            // sound and is NOT reversed here.
            //
            // 🔑 But it means NO Type 4 sponsor post has been able to carry its graphic since §1060(g)
            // added this filter. The two halves disagreed in the worst possible way:
            //   • the planner's own plannability gate asks only `FileName != null` ⇒ the sponsor IS
            //     announceable, and a post gets planned;
            //   • this map (Released only) ⇒ no graphic ⇒ `ImageRef` stamped NULL at plan time, the
            //     queue badge reads "no graphic", the approval gate blocks on "waiting for the
            //     graphic", and the dispatcher publishes TEXT-ONLY.
            // Operator 2026-09-12: *"linkedin planner is wrong or the linkedin post calender when it
            // comes to some graphics readiness. take surveil as example"* — Surveil (company 36)
            // uploaded its logo 2026-08-25 13:50 and `sponsor-36.png` was rendered at 13:55. The file
            // has existed for a fortnight; the campaign could not see it. §1143 predicted exactly this
            // ("the likely explanation if Surveil's row turns out to exist") and left it open.
            //
            // 🔒 ⇒ RELEASE IS THE BRANDING GATE, NOT THE ANNOUNCEMENT GATE. Track, session and tier
            // artwork is speaker-facing and keeps the §1060(g) gate exactly as it was. A sponsor row
            // is admitted on `FileName` alone, because for sponsors "released" was never a state
            // anything could reach and a gate nothing can pass is not a control — it is an outage.
            .Where(g => g.EventId == eventId
                        && (g.Status == GraphicAssetStatus.Released
                            || g.Type == GraphicAssetType.Sponsor)
                        && g.FileName != null && g.FileName != "")
            .OrderBy(g => g.CreatedAt)
            .Select(g => new { g.StableKey, g.FileName, g.Type, g.SessionId, g.SponsorCompanyId })
            .ToListAsync(ct);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var g in rows)
        {
            // Ordered oldest-first above, so a later row overwriting an earlier one IS "newest wins".
            switch (g.Type)
            {
                case GraphicAssetType.TrackBundle when g.StableKey is { Length: > 0 }:
                    map[g.StableKey] = g.FileName!;   // stored slugged; Lookup slugs to meet it
                    break;
                case GraphicAssetType.Session when g.SessionId is { } sid:
                    map[$"session:{sid}"] = g.FileName!;
                    break;
                case GraphicAssetType.Sponsor when g.SponsorCompanyId is { Length: > 0 }:
                    map[$"sponsor:{g.SponsorCompanyId}"] = g.FileName!;
                    break;
                case GraphicAssetType.SponsorCategory when g.StableKey is { Length: > 0 }
                        && g.StableKey.StartsWith("sponsor-tier:", StringComparison.OrdinalIgnoreCase):
                    map[$"tier:{g.StableKey["sponsor-tier:".Length..]}"] = g.FileName!;
                    break;
            }
        }

        return map;
    }

    /// <summary>
    /// §1178 — THE PICTURE THIS POST WILL ACTUALLY PUBLISH WITH. The dispatcher's rule, as a function.
    /// </summary>
    /// <remarks>
    /// <para>🔴 <b>Four surfaces answered this question and only two of them asked the dispatcher.</b>
    /// Operator 2026-09-12: <i>"linkedin planner is wrong or the linkedin post calender when it comes
    /// to some graphics readiness"</i>. §1168 fixed the queue BADGE by resolving late; the Post
    /// calendar, the sponsor/speaker announcement preview and <c>/some-post-media</c> all went on
    /// printing the ref stamped at plan time. So one page said "no graphic" while another showed one,
    /// about the same post — and neither was necessarily what would publish.</para>
    ///
    /// <para>🔑 The rule is exactly <c>SoMeDispatchService.ResolveImageAsync</c>'s: for a
    /// subject-owned post (types 1–4) the subject's CURRENT graphic wins; the stamp is only the
    /// fallback. For a Type 5 or an ad-hoc post the stamp IS the answer, because somebody chose it.
    /// One function, so a preview and a publish cannot disagree — the §932 lesson.</para>
    /// </remarks>
    public static string? Effective(IReadOnlyDictionary<string, string> map, SoMePost post) =>
        IsSubjectOwned(post)
            ? Lookup(map, post.SubjectKey) ?? NullIfBlank(post.ImageRef)
            : NullIfBlank(post.ImageRef);

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>The graphic for one subject key, handling the track name→slug asymmetry.</summary>
    /// <remarks>
    /// ⚠️ Slugged at LOOKUP, never reversed: "AI for Makers (Copilot &amp; Agents)" slugs to
    /// "ai-for-makers-copilot-agents", and no rule turns that back into the original punctuation.
    /// </remarks>
    public static string? Lookup(IReadOnlyDictionary<string, string> map, string? subjectKey)
    {
        if (string.IsNullOrWhiteSpace(subjectKey)) return null;
        if (map.TryGetValue(subjectKey, out var direct)) return direct;

        const string trackPrefix = "track:";
        if (subjectKey.StartsWith(trackPrefix, StringComparison.OrdinalIgnoreCase)
            && map.TryGetValue($"{trackPrefix}{Slug(subjectKey[trackPrefix.Length..])}", out var bySlug))
        {
            return bySlug;
        }

        return null;
    }

    /// <summary>The same slug rule the graphics sweep writes with — kept identical on purpose.</summary>
    public static string Slug(string value)
    {
        var chars = value.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        return string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }
}
