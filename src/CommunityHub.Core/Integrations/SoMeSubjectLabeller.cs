using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// §889 — WHAT A POST IS ABOUT, IN WORDS: a track name, a session title, a company, his own headline.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-06: <i>"i need to see everything in list format first and then have edit
/// option - i cannot go through 72 not approved every time to find number 33"</i>.</para>
///
/// <para>🔑 <b>A list of posts is only navigable if each row says what it is about.</b> A
/// <c>SubjectKey</c> like <c>session:50</c> or <c>sponsor:12</c> is a join key, not something anyone
/// recognises. And a body snippet does not rescue it for types 1–4: those bodies are the shared
/// template, so all 16 track posts read identically. <b>The subject is the only thing that separates
/// them.</b></para>
///
/// <para>🔒 <b>Batched — two queries for a whole page, never one per row.</b> The queue is 80+ posts
/// today and grows all year; a per-row lookup is the shape that turns a list into a page-load
/// problem right when the list finally becomes useful.</para>
///
/// <para>⚠️ Falls back to the raw key rather than to an empty cell. A subject CEH cannot resolve —
/// a session since deleted, a sponsor row that moved — must still be identifiable, and a blank
/// column would read as "this post is about nothing".</para>
/// </remarks>
public sealed class SoMeSubjectLabeller
{
    private readonly CommunityHubDbContext _db;

    public SoMeSubjectLabeller(CommunityHubDbContext db) => _db = db;

    /// <summary>Post id → the human subject for that post.</summary>
    public async Task<IReadOnlyDictionary<int, string>> LabelsForAsync(
        int eventId, IReadOnlyCollection<SoMePost> posts, CancellationToken ct = default)
    {
        var result = new Dictionary<int, string>();
        if (posts.Count == 0) return result;

        // The ids this page actually needs, so the two lookups stay proportional to the page.
        var sessionIds = new HashSet<int>();
        var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in posts)
        {
            var id = IdPart(p.SubjectKey);
            if (id.Length == 0) continue;
            if (p.TemplateKind == SoMeTemplateKind.Session && int.TryParse(id, out var sid)) sessionIds.Add(sid);
            else if (p.TemplateKind == SoMeTemplateKind.EventPost) slugs.Add(id);
        }

        var sessionTitles = sessionIds.Count == 0
            ? new Dictionary<int, string>()
            : await _db.Sessions
                .Where(s => s.EventId == eventId && sessionIds.Contains(s.Id))
                .Select(s => new { s.Id, s.Title })
                .ToDictionaryAsync(x => x.Id, x => x.Title, ct);

        var deckTitles = slugs.Count == 0
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : (await _db.EventSoMePosts
                    .Where(e => e.EventId == eventId && slugs.Contains(e.Slug))
                    .Select(e => new { e.Slug, e.Title })
                    .ToListAsync(ct))
                .ToDictionary(x => x.Slug, x => x.Title, StringComparer.OrdinalIgnoreCase);

        // Sponsor names go through the same fallback chain as everywhere else (DESIGN §6), so the
        // list cannot show a company a different name from the post it is announcing.
        var sponsorNames = await _db.SponsorInfos
            .Where(s => s.EventId == eventId && s.SponsorCompanyId != null)
            .Select(s => new { s.SponsorCompanyId, s.CompanyName })
            .ToListAsync(ct);
        var sponsorByCompany = sponsorNames
            .GroupBy(s => s.SponsorCompanyId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => SponsorCompanyName.Resolve(g.First().CompanyName, null, null, g.Key),
                StringComparer.OrdinalIgnoreCase);

        foreach (var p in posts)
        {
            var id = IdPart(p.SubjectKey);
            var label = p.TemplateKind switch
            {
                // The track name IS the id — no lookup needed, and it already reads as words.
                SoMeTemplateKind.SpeakerTracks => id,
                SoMeTemplateKind.Session when int.TryParse(id, out var sid) =>
                    sessionTitles.GetValueOrDefault(sid),
                SoMeTemplateKind.SponsorCategory => $"{id} sponsors",
                SoMeTemplateKind.Sponsor => sponsorByCompany.GetValueOrDefault(id),
                SoMeTemplateKind.EventPost => deckTitles.GetValueOrDefault(id),
                _ => null,
            };

            result[p.Id] = string.IsNullOrWhiteSpace(label) ? (p.SubjectKey ?? "—") : label!;
        }

        return result;
    }

    /// <summary>The part after the <c>kind:</c> prefix — the whole key when there is no prefix.</summary>
    private static string IdPart(string? subjectKey)
    {
        if (string.IsNullOrWhiteSpace(subjectKey)) return string.Empty;
        var i = subjectKey.IndexOf(':');
        return i < 0 ? subjectKey : subjectKey[(i + 1)..];
    }
}
