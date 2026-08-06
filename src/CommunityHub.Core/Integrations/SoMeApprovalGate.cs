using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// §850 — MAY THIS POST BE APPROVED YET? One answer, in one place.
///
/// <para>Operator 2026-08-05: <i>"if the sponsor has not delivered the social media text (specific
/// field in ceh per sponsor), then it is not eligible yet, so it cannot be approved (blocker). it is
/// ok, that it is planned, but it can newer be approved"</i>.</para>
///
/// <para>🔑 <b>The gate is on APPROVAL, not on planning</b>, and that distinction is the requirement:
/// the post is still planned, so the campaign looks complete and the slot is reserved — but the
/// sponsor's missing deliverable can never reach LinkedIn.</para>
///
/// <para>🔒 <b>In the service, never only in a page.</b> A post can be approved from the queue, from
/// the post editor, and from anything added later; a check living in one of them would be enforced in
/// one of them. Same reasoning as §842.5's contractual guard.</para>
///
/// <para>⚠️ <b>The dispatcher must consult this too.</b> Approval is a MOMENT; eligibility is a
/// STATE. A post approved while the text existed, and then the text cleared, must not publish —
/// what matters is the state at send time.</para>
/// </summary>
public sealed class SoMeApprovalGate
{
    private readonly CommunityHubDbContext _db;

    public SoMeApprovalGate(CommunityHubDbContext db) => _db = db;

    /// <summary>
    /// Why this post may not be approved yet, or null when it may.
    /// </summary>
    /// <remarks>
    /// Returns the REASON rather than a bool so every caller shows the same sentence — and so he is
    /// told what to fix rather than that something is merely "not allowed".
    /// </remarks>
    public async Task<string?> BlockedReasonAsync(SoMePost post, CancellationToken ct = default)
    {
        // 🔴 §922 — THE GENERAL RULE, ASKED FIRST. Operator 2026-08-06: *"a post can NOT go out if a
        // variable is empty in the post. that is the blocker."*
        //
        // 🔑 It runs BEFORE the per-type checks below because it subsumes most of them and is
        // derived from the post's OWN body: a sponsor owing their social text shows up here as
        // {SponsorSocialMediaCompanyDescription} being empty, without this class needing to know
        // what a sponsor is. The type-specific checks stay for what a body cannot express — a
        // missing LOGO is not a variable in the text, and §842.5 makes it contractual.
        //
        // ⚠️ Empty ≠ unknown: a token nothing can resolve is a template bug and still publishes
        // verbatim (§864.3), because withholding a post for ever and telling nobody is worse.
        var values = await new SoMePostComposer(_db, new SoMeVariableResolver(_db))
            .ValuesForAsync(post, ct);
        if (SoMeEmptyVariableGate.ReasonFor(post.EffectiveText, values) is { Length: > 0 } emptyVariable)
        {
            return emptyVariable;
        }

        // 🔴 §926 — A SESSION POST NEEDS THE SESSION'S DESCRIPTION. Operator 2026-08-06:
        // *"master class announcement and sessions has dependency to description. if empty it is
        // not ready"*.
        //
        // ⚠️ §922's general rule CANNOT catch this, and that is why the check is here. The rule
        // inspects variables IN the body — and his 38 session wordings do not print the abstract,
        // they print {SessionTeaserTextAI}. The abstract is the source the teaser is WRITTEN FROM,
        // one level behind the text.
        //
        // 🔴 An empty abstract therefore does not produce a shorter post, it produces an INVENTED
        // one. Measured 2026-08-06: "ELDK27 Welcome" has no abstract, and the assistant had already
        // written *"where the energy is high and the community comes alive"* from the title alone —
        // and §911 stores a teaser permanently, so that invention would have been reused for ever.
        if (post.TemplateKind == SoMeTemplateKind.Session
            && !SoMeSponsorSessionKey.TryParse(post.SubjectKey, out _)
            && SessionIdOf(post) is { } sessionId)
        {
            var hasAbstract = await _db.Sessions
                .Where(s => s.Id == sessionId && s.EventId == post.EventId)
                .Select(s => s.Abstract != null && s.Abstract.Trim() != "")
                .FirstOrDefaultAsync(ct);

            if (!hasAbstract)
            {
                return "waiting for the session description — the announcement is written from it, "
                     + "so without one there is nothing to describe. Add the abstract and this "
                     + "becomes approvable.";
            }
        }

        // 🔴 §865.4 — A TIER POST IS A SPONSOR POST TOO, AND THE GATE NEVER RAN ON IT.
        // Operator 2026-08-05: "guards are not working - it still shows posts which are NOT ready",
        // on post #540 (Silver) — whose THREE sponsors have all failed to deliver their text.
        // The gate keyed on a single company id, which a Type 3 tier post does not have, so
        // SponsorCompanyIdOf returned null and the post passed every check. Not a weak rule — a
        // rule that never ran on this type. ⚠️ Same shape as §824.12b/§856.2: a check that looked
        // complete because it was never exercised against the case that breaks it.
        if (post.TemplateKind == SoMeTemplateKind.SponsorCategory)
        {
            var tier = TierOf(post);
            if (tier is null) return null;

            // 🔒 §867.1 — the SAME two dependencies as a single-sponsor post: text AND logo.
            // The reason and the list filter must ask the identical question, or the walk hides a
            // post whose reason says it is fine (or the reverse) — a disagreement he would have to
            // debug from the outside.
            var waiting = await _db.SponsorInfos
                .Where(s => s.EventId == post.EventId
                            && s.SponsorPackage == tier
                            && ((s.SocialMediaIntro == null || s.SocialMediaIntro.Trim() == "")
                                || ((s.LogoRasterFileName == null || s.LogoRasterFileName == "")
                                    && (s.LogoVectorFileName == null || s.LogoVectorFileName == ""))))
                .Select(s => s.CompanyName)
                .ToListAsync(ct);

            if (waiting.Count == 0) return null;

            // Name them: he has to chase these people, and "some sponsors" is not chaseable (§854).
            return $"{string.Join(", ", waiting)} — {waiting.Count} sponsor(s) in this tier have not "
                 + "delivered their social-media text and logo yet, so this post cannot be approved. "
                 + "It stays planned, and becomes approvable as soon as those are filled in.";
        }

        // Only SPONSOR posts carry this prerequisite. A track, session or event post has no sponsor
        // deliverable to wait for, and gating them would stall the campaign for no reason.
        var companyId = SponsorCompanyIdOf(post);
        if (companyId is null) return null;

        var sponsor = await _db.SponsorInfos
            .Where(s => s.EventId == post.EventId && s.SponsorCompanyId == companyId)
            .Select(s => new
            {
                s.CompanyName, s.SocialMediaIntro, s.LogoRasterFileName, s.LogoVectorFileName,
            })
            .FirstOrDefaultAsync(ct);

        // No sponsor row at all ⇒ nothing to wait for. The post is odd, but blocking approval on a
        // missing row would be a puzzle rather than an instruction.
        if (sponsor is null) return null;

        // 🔒 §867.1 — TWO DELIVERABLES, NOT ONE. Operator 2026-08-05: "there are dependencies so if
        // they havent upload logo + delivered some text, they are not ready for some posting".
        // The gate previously checked only the text, so a sponsor with copy but no logo read as
        // ready — and §854 had already established that no logo means no graphic.
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(sponsor.SocialMediaIntro)) missing.Add("their social-media text");
        if (string.IsNullOrWhiteSpace(sponsor.LogoRasterFileName)
            && string.IsNullOrWhiteSpace(sponsor.LogoVectorFileName)) missing.Add("their logo");

        if (missing.Count == 0) return null;

        // Name what is missing, not merely that something is: he has to chase it (§854).
        return $"{sponsor.CompanyName ?? companyId} has not delivered {string.Join(" or ", missing)} "
             + "yet, so this post cannot be approved. It stays planned, and becomes approvable as "
             + "soon as that is filled in on their sponsor record.";
    }

    /// <summary>
    /// §850.2 — the sponsor companies that are NOT yet approvable, in one query.
    /// </summary>
    /// <remarks>
    /// ⚠️ For filtering a LIST. <see cref="BlockedReasonAsync"/> asks per post and would be one
    /// database round-trip per row — on a 112-post walk that is 112 queries to draw one page.
    /// </remarks>
    public async Task<HashSet<string>> BlockedSponsorCompanyIdsAsync(
        int eventId, CancellationToken ct = default)
    {
        var ids = await _db.SponsorInfos
            .Where(s => s.EventId == eventId
                        && s.SponsorCompanyId != null
                        && ((s.SocialMediaIntro == null || s.SocialMediaIntro.Trim() == "")
                            || ((s.LogoRasterFileName == null || s.LogoRasterFileName == "")
                                && (s.LogoVectorFileName == null || s.LogoVectorFileName == ""))))
            .Select(s => s.SponsorCompanyId!)
            .ToListAsync(ct);

        return ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 🔴 §865.4 — the TIERS that are not yet approvable, because a sponsor in them owes their text.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Needed as a SECOND set because a tier post has no company id.</b> Filtering the walk on
    /// company ids alone silently passed every Type 3 post — the defect he reported on #540.
    /// </remarks>
    public async Task<HashSet<SponsorPackage>> BlockedTiersAsync(
        int eventId, CancellationToken ct = default)
    {
        var tiers = await _db.SponsorInfos
            .Where(s => s.EventId == eventId
                        && ((s.SocialMediaIntro == null || s.SocialMediaIntro.Trim() == "")
                            || ((s.LogoRasterFileName == null || s.LogoRasterFileName == "")
                                && (s.LogoVectorFileName == null || s.LogoVectorFileName == ""))))
            .Select(s => s.SponsorPackage)
            .Distinct()
            .ToListAsync(ct);

        return tiers.ToHashSet();
    }

    /// <summary>
    /// Whether this post is blocked, given the sets from <see cref="BlockedSponsorCompanyIdsAsync"/>
    /// and <see cref="BlockedTiersAsync"/>.
    /// </summary>
    /// <remarks>
    /// 🔒 <b>BOTH sets, always.</b> §865.4: passing only the company set is what let #540 through,
    /// so the tier set is a REQUIRED parameter rather than an optional extra — a caller that forgets
    /// it should not compile ([[ceh-count-the-shared-things]]).
    /// </remarks>
    public static bool IsBlockedBy(
        SoMePost post, HashSet<string> blockedCompanyIds, HashSet<SponsorPackage> blockedTiers)
    {
        if (post.TemplateKind == SoMeTemplateKind.SponsorCategory)
        {
            var tier = TierOf(post);
            return tier is not null && blockedTiers.Contains(tier.Value);
        }

        var companyId = SponsorCompanyIdOf(post);
        return companyId is not null && blockedCompanyIds.Contains(companyId);
    }

    /// <summary>The tier a Type 3 post is about, read from <c>tier:{Package}</c> (§824.21).</summary>
    private static SponsorPackage? TierOf(SoMePost post)
    {
        var key = post.SubjectKey ?? string.Empty;
        var idx = key.IndexOf(':');
        if (idx < 0) return null;

        return Enum.TryParse<SponsorPackage>(key[(idx + 1)..], ignoreCase: true, out var tier)
            ? tier
            : null;
    }

    /// <summary>
    /// The sponsor company a post is about, or null when it is not a sponsor post.
    /// </summary>
    /// <remarks>
    /// ⚠️ Reads the SubjectKey (<c>sponsor:{companyId}</c>, §824.21) as well as the legacy
    /// <see cref="SoMePost.SponsorCompanyId"/> column: planned posts carry the key, and older
    /// hand-made ones carry the column.
    /// </remarks>
    private static string? SponsorCompanyIdOf(SoMePost post)
    {
        if (!string.IsNullOrWhiteSpace(post.SponsorCompanyId)) return post.SponsorCompanyId;

        if (post.TemplateKind == SoMeTemplateKind.Sponsor
            && post.SubjectKey is { } key
            && key.StartsWith(SoMeAnnouncementQuery.SponsorPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return key[SoMeAnnouncementQuery.SponsorPrefix.Length..];
        }

        return null;
    }

    /// <summary>§926 — the Sessions row a Type 2 post announces, or null when it is not one.</summary>
    /// <remarks>
    /// ⚠️ <c>sponsorsession:{id}</c> is a DIFFERENT table (§912) and its ids overlap with
    /// <c>session:{id}</c>, so the caller rules it out first — a bare int parse would read
    /// sponsorsession:1 as Sessions row 1 and gate the wrong talk (the §864.2 trap).
    /// </remarks>
    private static int? SessionIdOf(SoMePost post)
    {
        const string prefix = "session:";

        if (post.SubjectKey is { } key
            && key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(key[prefix.Length..], out var id))
        {
            return id;
        }

        return null;
    }
}
