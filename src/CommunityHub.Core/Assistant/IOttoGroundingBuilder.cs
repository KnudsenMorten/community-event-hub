using CommunityHub.Core.Content;
using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Assistant;

/// <summary>
/// Supplies the RAW markdown for a registered content-hub page (the prose under
/// <c>config/content/&lt;edition&gt;/{slug}.md</c>). The web layer implements this over
/// the on-disk file; tests fake it. The builder only ever ASKS for slugs a role may
/// see, so this provider is never the authorization boundary — it just reads text.
/// </summary>
public interface IOttoContentProvider
{
    /// <summary>The markdown body for a content slug, or null when the file is absent.</summary>
    string? GetContentMarkdown(string slug);
}

/// <summary>
/// Supplies the signed-in participant's OWN data (their tasks, their sessions, their
/// form/readiness state) as already-scoped grounding sections. The implementation
/// MUST filter strictly by <paramref name="participantId"/> (own rows only) — a
/// volunteer must never receive another person's rows. Implemented in the web layer
/// over EF Core; faked in tests.
/// </summary>
public interface IOttoOwnDataProvider
{
    Task<IReadOnlyList<OttoGroundingSection>> GetOwnDataAsync(
        int eventId, int participantId, ParticipantRole role, CancellationToken ct = default);
}

/// <summary>
/// Assembles the per-request <see cref="OttoContext"/> from the signed-in
/// participant's role + id. This is the authorization-at-retrieval seam.
/// </summary>
public interface IOttoGroundingBuilder
{
    Task<OttoContext> BuildAsync(
        int eventId, int participantId, ParticipantRole role, CancellationToken ct = default);
}

/// <summary>
/// Builds Otto's grounding with AUTHORIZATION AT RETRIEVAL (REQUIREMENTS §129,
/// security-critical). Two enforced rules:
///
/// 1. <b>Role-scoped content.</b> Only the pages
///    <see cref="ContentPageRegistry.ForRole"/> returns for THIS role are ever
///    requested from <see cref="IOttoContentProvider"/>. A volunteer's ForRole set
///    excludes every speaker/organizer-only page, so those pages are never even
///    read — a volunteer literally cannot retrieve speaker/organizer content.
///
/// 2. <b>Own rows only.</b> User data comes from <see cref="IOttoOwnDataProvider"/>
///    keyed by the server-resolved <c>participantId</c>; no client-supplied id is
///    accepted anywhere in the chain.
///
/// 3. <b>Organizer-only ops aggregates (REQUIREMENTS §133).</b> When an optional
///    <see cref="IOttoOrganizerOpsProvider"/> is wired, its curated, read-only
///    operational aggregates (speaker readiness / missing slides, sponsor missing
///    deliverables, master-class non-selections, participation / task / attendee
///    counts) are added to the grounding <b>only</b> when the SERVER-resolved
///    <c>role</c> is <see cref="ParticipantRole.Organizer"/>. The gate is HERE, in the
///    retrieval seam, keyed on the server role — never in the model prompt (mirrors the
///    §69 organizer-only rule). A non-organizer never even calls the ops provider, so
///    ops/aggregate grounding is unreachable for non-organizers by construction.
///
/// The result holds only already-authorized text; the model gets no raw DB access.
/// </summary>
public sealed class OttoGroundingBuilder : IOttoGroundingBuilder
{
    private readonly IOttoContentProvider _content;
    private readonly IOttoOwnDataProvider _ownData;
    private readonly IOttoOrganizerOpsProvider? _organizerOps;

    public OttoGroundingBuilder(
        IOttoContentProvider content,
        IOttoOwnDataProvider ownData,
        IOttoOrganizerOpsProvider? organizerOps = null)
    {
        _content = content;
        _ownData = ownData;
        _organizerOps = organizerOps;
    }

    public async Task<OttoContext> BuildAsync(
        int eventId, int participantId, ParticipantRole role, CancellationToken ct = default)
    {
        var sections = new List<OttoGroundingSection>();

        // (1) Role-scoped content: ONLY pages this role may view (ForRole is the gate).
        foreach (var page in ContentPageRegistry.ForRole(role))
        {
            var md = _content.GetContentMarkdown(page.Slug);
            if (!string.IsNullOrWhiteSpace(md))
            {
                sections.Add(new OttoGroundingSection($"Help page — {page.Title}", md.Trim()));
            }
        }

        // (2) The participant's OWN data (own rows only; provider filters by id).
        var own = await _ownData.GetOwnDataAsync(eventId, participantId, role, ct);
        sections.AddRange(own.Where(s => !string.IsNullOrWhiteSpace(s.Body)));

        // (3) ORGANIZER-ONLY ops aggregates (REQUIREMENTS §133). THE GATE: keyed on the
        // SERVER-resolved role, in this retrieval layer — never the prompt. A non-organizer
        // never even calls the ops provider, so ops/aggregate grounding cannot leak to them.
        if (role == ParticipantRole.Organizer && _organizerOps is not null)
        {
            var ops = await _organizerOps.GetOpsAggregatesAsync(eventId, ct);
            sections.AddRange(ops.Where(s => !string.IsNullOrWhiteSpace(s.Body)));
        }

        return new OttoContext(role, participantId, sections);
    }
}
