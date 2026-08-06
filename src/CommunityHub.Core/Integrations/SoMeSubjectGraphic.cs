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
            .Where(g => g.EventId == eventId && g.FileName != null && g.FileName != "")
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
