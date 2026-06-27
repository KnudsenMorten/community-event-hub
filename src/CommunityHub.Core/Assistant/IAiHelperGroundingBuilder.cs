using CommunityHub.Core.Content;
using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Assistant;

/// <summary>
/// Supplies the RAW markdown for a registered content-hub page (the prose under
/// <c>config/content/&lt;edition&gt;/{slug}.md</c>). The web layer implements this over
/// the on-disk file; tests fake it. The builder only ever ASKS for slugs a role may
/// see, so this provider is never the authorization boundary — it just reads text.
/// </summary>
public interface IAiHelperContentProvider
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
public interface IAiHelperOwnDataProvider
{
    Task<IReadOnlyList<AiHelperGroundingSection>> GetOwnDataAsync(
        int eventId, int participantId, ParticipantRole role, CancellationToken ct = default);
}

/// <summary>
/// Assembles the per-request <see cref="AiHelperContext"/> from the signed-in
/// participant's role + id. This is the authorization-at-retrieval seam.
/// </summary>
public interface IAiHelperGroundingBuilder
{
    Task<AiHelperContext> BuildAsync(
        int eventId, int participantId, ParticipantRole role, CancellationToken ct = default);
}

/// <summary>
/// Builds the assistant's grounding with AUTHORIZATION AT RETRIEVAL (REQUIREMENTS §129,
/// security-critical). Two enforced rules:
///
/// 1. <b>Role-scoped content.</b> Only the pages
///    <see cref="ContentPageRegistry.ForRole"/> returns for THIS role are ever
///    requested from <see cref="IAiHelperContentProvider"/>. A volunteer's ForRole set
///    excludes every speaker/organizer-only page, so those pages are never even
///    read — a volunteer literally cannot retrieve speaker/organizer content.
///
/// 2. <b>Own rows only.</b> User data comes from <see cref="IAiHelperOwnDataProvider"/>
///    keyed by the server-resolved <c>participantId</c>; no client-supplied id is
///    accepted anywhere in the chain.
///
/// 3. <b>Organizer-only ops aggregates (REQUIREMENTS §133).</b> When an optional
///    <see cref="IAiHelperOrganizerOpsProvider"/> is wired, its curated, read-only
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
public sealed class AiHelperGroundingBuilder : IAiHelperGroundingBuilder
{
    private readonly IAiHelperContentProvider _content;
    private readonly IAiHelperOwnDataProvider _ownData;
    private readonly IAiHelperOrganizerOpsProvider? _organizerOps;
    private readonly IAiHelperPublicInfoProvider? _publicInfo;
    private readonly IAiHelperSharePointGroundingProvider? _sharePointGrounding;

    public AiHelperGroundingBuilder(
        IAiHelperContentProvider content,
        IAiHelperOwnDataProvider ownData,
        IAiHelperOrganizerOpsProvider? organizerOps = null,
        IAiHelperPublicInfoProvider? publicInfo = null,
        IAiHelperSharePointGroundingProvider? sharePointGrounding = null)
    {
        _content = content;
        _ownData = ownData;
        _organizerOps = organizerOps;
        _publicInfo = publicInfo;
        _sharePointGrounding = sharePointGrounding;
    }

    /// <summary>
    /// Public content pages that are NOT in <see cref="ContentPageRegistry"/> but every role
    /// may ask about — grounded for ALL roles (operator 2026-06-27, §152). The organizer/
    /// contact roster lives in <c>organizers.md</c> (rendered on <c>/Contact</c> as an
    /// un-registered page), so it was never grounded and the helper could not answer
    /// "who are the organizers?". It is public contact info (names/roles/emails), no
    /// sensitive data.
    /// </summary>
    private static readonly (string Slug, string Heading)[] AlwaysPublicContent =
    {
        ("organizers", "Contact the organizers"),
    };

    public async Task<AiHelperContext> BuildAsync(
        int eventId, int participantId, ParticipantRole role, CancellationToken ct = default)
    {
        var sections = new List<AiHelperGroundingSection>();

        // (1) Role-scoped content: ONLY pages this role may view (ForRole is the gate).
        foreach (var page in ContentPageRegistry.ForRole(role))
        {
            var md = _content.GetContentMarkdown(page.Slug);
            if (!string.IsNullOrWhiteSpace(md))
            {
                sections.Add(new AiHelperGroundingSection($"Help page — {page.Title}", md.Trim()));
            }
        }

        // (1b) ALWAYS-PUBLIC unregistered content, grounded for EVERY role (§152) — e.g. the
        // organizer/contact roster (organizers.md). Not in ContentPageRegistry, so the
        // role-scoped loop above never picks it up.
        foreach (var (slug, heading) in AlwaysPublicContent)
        {
            var md = _content.GetContentMarkdown(slug);
            if (!string.IsNullOrWhiteSpace(md))
            {
                sections.Add(new AiHelperGroundingSection(heading, md.Trim()));
            }
        }

        // (2) The participant's OWN data (own rows only; provider filters by id).
        var own = await _ownData.GetOwnDataAsync(eventId, participantId, role, ct);
        sections.AddRange(own.Where(s => !string.IsNullOrWhiteSpace(s.Body)));

        // (2b) PUBLIC info for EVERY role (REQUIREMENTS §149): published speakers + their
        // skills + sessions, the session programme, and the event schedule / key times
        // (doors open, lunch, party, dinner). Added for ALL roles — UNLIKE the organizer
        // ops below, there is no role gate, because this is public, non-personal info the
        // operator wants anyone to be able to ask about. The provider itself enforces the
        // speaker publish HARD GATE, so no unselected speaker can leak.
        if (_publicInfo is not null)
        {
            var pub = await _publicInfo.GetPublicInfoAsync(eventId, ct);
            sections.AddRange(pub.Where(s => !string.IsNullOrWhiteSpace(s.Body)));
        }

        // (2c) SHAREPOINT grounding docs for EVERY role (REQUIREMENTS §152): the operator-
        // dropped md/txt/docx/pdf/xlsx files in the configured SharePoint grounding folder,
        // extracted to text. Added for ALL roles with NO role gate (mirrors the §149 public-
        // info block above) — it is curated PUBLIC reference material the operator wants
        // anyone to be able to ask about. INERT until configured: the provider returns empty
        // when the SharePoint read seam is not wired or the folder path is blank.
        if (_sharePointGrounding is not null)
        {
            var sp = await _sharePointGrounding.GetGroundingAsync(ct);
            sections.AddRange(sp.Where(s => !string.IsNullOrWhiteSpace(s.Body)));
        }

        // (3) ORGANIZER-ONLY ops aggregates (REQUIREMENTS §133). THE GATE: keyed on the
        // SERVER-resolved role, in this retrieval layer — never the prompt. A non-organizer
        // never even calls the ops provider, so ops/aggregate grounding cannot leak to them.
        if (role == ParticipantRole.Organizer && _organizerOps is not null)
        {
            var ops = await _organizerOps.GetOpsAggregatesAsync(eventId, ct);
            sections.AddRange(ops.Where(s => !string.IsNullOrWhiteSpace(s.Body)));
        }

        return new AiHelperContext(role, participantId, sections);
    }
}
