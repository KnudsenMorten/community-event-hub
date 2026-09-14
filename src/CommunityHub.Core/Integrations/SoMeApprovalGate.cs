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
            var session = await _db.Sessions
                .Where(s => s.Id == sessionId && s.EventId == post.EventId)
                .Select(s => new
                {
                    s.Abstract, s.Title, s.ExcludeFromSoMeAnnouncements, s.Type,
                    s.SoMeTextEligible, s.SoMeTextEligibleReason, s.SoMeSingleSpeakerConfirmed,
                    LinkedSpeakers = s.SessionSpeakers.Count,
                })
                .FirstOrDefaultAsync(ct);

            // 🛑 §1060(m) — ASK-THE-EXPERTS IS NEVER ANNOUNCED. Operator 2026-08-11: *"sessions of
            // type asktheexperts are not some announced"*.
            //
            // 🔑 Worded as a PERMANENT state, not a to-do. Every other sentence in this class says
            // "…and this becomes approvable", because every other blocker is something somebody can
            // clear. This one cannot be, and a to-do-shaped sentence would send someone hunting for
            // a fix that does not exist (§1042: the walk that read as "gone" because it was a
            // footnote). ⚠️ The durable form of his §927 title filter `ask the experts*` — a filter
            // on the TITLE stops working the moment a session is renamed.
            if (session?.Type == SessionType.AskTheExperts)
            {
                return "Ask the Experts sessions are never announced on social media — they are a "
                     + "format rather than a talk, and are covered by a post the organizers write "
                     + "themselves. Nothing to fix here.";
            }

            // 🔴 §1060(h) — THE EXCLUDE FLAG IS A BLOCKER, NOT ONLY A PLANNER FILTER.
            // Operator 2026-08-11: *"that flag ... must cover to exclude session from being planned
            // and approved in some"*. The planner governs only what is CREATED; a post that already
            // existed when the flag was set would otherwise be picked up by the now-windowless
            // auto-approver (§1060) and published by §889.1 — the flag would have looked applied
            // while the session went out anyway.
            if (session?.ExcludeFromSoMeAnnouncements == true)
            {
                return "this session is marked \"exclude from social-media announcements\", so it is "
                     + "never announced. Clear that flag on the session if it should be.";
            }

            // 🔴 §1060(a) — "NOT EMPTY" WAS NEVER THE QUESTION; "IS IT A DESCRIPTION" IS.
            // Operator 2026-08-11: *"ai must not approve a speaker session if text is blank or
            // something tbd or we are working on it and will release session description soon"*.
            // ⚠️ A placeholder is §926's defect wearing a disguise: TBD is not a description, but it
            // is not empty either, so it passed the only check there was — and with §1060's lead-time
            // window dropped this gate is the last thing before LinkedIn.
            if (SoMePlaceholderText.IsMissingOrPlaceholder(session?.Abstract))
            {
                return "waiting for the session description — the announcement is written from it, "
                     + "so without one there is nothing to describe. A placeholder (\"TBD\", "
                     + "\"coming soon\", \"we're working on it\") counts as missing. Add the real "
                     + "abstract and this becomes approvable.";
            }

            // 🔴 §1060(l) — THE AI'S STORED VERDICT. Operator 2026-08-11: *"save it as an record on
            // the session row. each time they will be 0 until the text is eligible."*
            //
            // 🔒 READ, never asked live — see Session.SoMeTextEligible for why. The daily sweep forms
            // the verdict; this only consults it, so the gate stays deterministic between sweeps.
            //
            // ⚠️ `null` (never judged) does NOT block. That is the deliberate fail-direction: on an
            // environment with no model configured — DEV, or PROD before the first sweep — a
            // blocking null would silently stop the entire campaign, and the rules above have
            // already refused everything that is certainly a placeholder. "Could not ask" must never
            // read as "the answer is no" (§858.16h).
            if (session?.SoMeTextEligible == false)
            {
                var why = string.IsNullOrWhiteSpace(session.SoMeTextEligibleReason)
                    ? "it does not yet describe the talk"
                    : session.SoMeTextEligibleReason!.Trim();

                return $"the session description was reviewed and is not ready to announce — {why}. "
                     + "Update the abstract; it is re-checked daily and this becomes approvable once "
                     + "it describes the session.";
            }

            // 🔴 §1060(m) — A CO-TAUGHT FORMAT WITH ONE SPEAKER IS NOT SET UP YET.
            // Operator 2026-08-11: *"a master class has +1 speakers and 1 means that session owner is
            // missing to link the speakers"*, and *"gates for panel discussion must be implemented if
            // only 1 speaker is linked, same as master class"*.
            //
            // 🔑 A DIFFERENT KIND OF GATE from every other rule here. The others ask "is this content
            // present?"; this asks "is this record PLAUSIBLE?" Nothing is empty — the session is
            // fully filled in and simply wrong, which is why §922's empty-variable rule can never
            // catch it.
            // ⚠️ ONE is the dangerous count, not zero. Zero fails loudly elsewhere; exactly one looks
            // complete on every screen and would announce a co-taught session as a solo talk —
            // publicly, naming the wrong people. A wrong announcement is worse than a missing one.
            //
            // 🔓 §1218 — unless an organizer has CONFIRMED one speaker is right (the post editor's
            // override button). ⚠️ One, never zero: a confirmed session with nobody linked still has
            // nobody to announce.
            if (session is not null
                && IsCoPresented(session.Type)
                && session.LinkedSpeakers < 2
                && !(session.SoMeSingleSpeakerConfirmed && session.LinkedSpeakers == 1))
            {
                var format = session.Type == SessionType.MasterClass ? "master class" : "panel discussion";
                return $"a {format} is co-presented, but only {session.LinkedSpeakers} speaker(s) "
                     + "are linked to this session — so the announcement would name the wrong people. "
                     + "Link the other speaker(s) on the session and this becomes approvable"
                     + (session.LinkedSpeakers == 1
                         ? ", or confirm that one speaker is correct."
                         : ".");
            }
        }

        // 🔴 §1060(g) — SPONSOR SESSIONS HAD NO GATES AT ALL. This is the type with the FEWEST
        // checks, not the most: the branch above deliberately excludes them
        // (`!SoMeSponsorSessionKey.TryParse`) because their description lives on a different table,
        // and nothing was ever pointed at that table. Same shape as §865.4 — not a weak rule, a rule
        // that never ran on this type.
        if (SoMeSponsorSessionKey.TryParse(post.SubjectKey, out var sponsorSessionId))
        {
            var ss = await _db.SponsorSessions
                .Where(s => s.Id == sponsorSessionId && s.EventId == post.EventId)
                .Select(s => new
                {
                    s.Title, s.Abstract, s.ExcludeFromSoMeAnnouncements,
                    // 🔑 A speaker "linked" means the real Participant exists — the sponsor-typed
                    // Name/Email on the row are what they TYPED, not proof of a hub speaker.
                    Speakers = s.Speakers
                        .Where(sp => sp.ParticipantId != null)
                        .Select(sp => new { sp.Name, sp.ParticipantId })
                        .ToList(),
                })
                .FirstOrDefaultAsync(ct);

            if (ss is null) return await GraphicBlockerAsync(post, ct);   // no row ⇒ a puzzle, not an instruction (§854)

            if (ss.ExcludeFromSoMeAnnouncements)
            {
                return "this sponsor session is marked \"exclude from social-media announcements\", "
                     + "so it is never announced. Clear that flag on the session if it should be.";
            }

            if (SoMePlaceholderText.IsMissingOrPlaceholder(ss.Abstract))
            {
                return "waiting for the sponsor session's description — the announcement is written "
                     + "from it. A placeholder (\"TBD\", \"coming soon\") counts as missing. Add the "
                     + "real abstract on the sponsor's session and this becomes approvable.";
            }

            // 🔒 Operator 2026-08-11: *"sponsor sessions must also link speaker with required
            // linkedin url and speaker photo"* — and *"block announcement only"*, so the sponsor's
            // own session form still SAVES without a speaker. The rule governs announcing, not
            // editing: refusing a save would break a flow sponsors are already using in order to
            // enforce a rule about a later act.
            if (ss.Speakers.Count == 0)
            {
                return "no speaker is linked to this sponsor session yet, so there is nobody to "
                     + "announce. Add the speaker on the sponsor's session and this becomes "
                     + "approvable.";
            }

            var participantIds = ss.Speakers.Select(sp => sp.ParticipantId!.Value).ToList();
            var missingLinkedIn = await _db.SpeakerProfiles
                .Where(p => participantIds.Contains(p.ParticipantId)
                            && (p.LinkedIn == null || p.LinkedIn.Trim() == ""))
                .Select(p => p.Participant!.FullName)
                .ToListAsync(ct);

            // ⚠️ A speaker with NO SpeakerProfile row at all is missing LinkedIn just as surely as
            // one with a blank field — count them, or a speaker who never opened a form reads as
            // complete.
            var withProfile = await _db.SpeakerProfiles
                .Where(p => participantIds.Contains(p.ParticipantId))
                .Select(p => p.ParticipantId)
                .ToListAsync(ct);
            var noProfile = ss.Speakers
                .Where(sp => !withProfile.Contains(sp.ParticipantId!.Value))
                .Select(sp => sp.Name)
                .ToList();

            var needLinkedIn = missingLinkedIn.Concat(noProfile).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            if (needLinkedIn.Count > 0)
            {
                // Name them — he has to chase these people, and "a speaker" is not chaseable (§854).
                return $"{string.Join(", ", needLinkedIn)} — no LinkedIn URL on their speaker "
                     + "profile yet, and a sponsor session is not announced without one. They add it "
                     + "on their Speaker details form, and this becomes approvable.";
            }
        }

        // (The graphic check is LAST — see GraphicBlockerAsync. Every "no content blocker" exit
        //  below routes through it rather than returning null directly.)

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
            if (tier is null) return await GraphicBlockerAsync(post, ct);

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

            if (waiting.Count == 0) return await GraphicBlockerAsync(post, ct);

            // Name them: he has to chase these people, and "some sponsors" is not chaseable (§854).
            return $"{string.Join(", ", waiting)} — {waiting.Count} sponsor(s) in this tier have not "
                 + "delivered their social-media text and logo yet, so this post cannot be approved. "
                 + "It stays planned, and becomes approvable as soon as those are filled in.";
        }

        // Only SPONSOR posts carry this prerequisite. A track, session or event post has no sponsor
        // deliverable to wait for, and gating them would stall the campaign for no reason.
        var companyId = SponsorCompanyIdOf(post);
        if (companyId is null) return await GraphicBlockerAsync(post, ct);

        var sponsor = await _db.SponsorInfos
            .Where(s => s.EventId == post.EventId && s.SponsorCompanyId == companyId)
            .Select(s => new
            {
                s.CompanyName, s.SocialMediaIntro, s.LogoRasterFileName, s.LogoVectorFileName,
            })
            .FirstOrDefaultAsync(ct);

        // No sponsor row at all ⇒ nothing to wait for. The post is odd, but blocking approval on a
        // missing row would be a puzzle rather than an instruction.
        if (sponsor is null) return await GraphicBlockerAsync(post, ct);

        // 🔒 §867.1 — TWO DELIVERABLES, NOT ONE. Operator 2026-08-05: "there are dependencies so if
        // they havent upload logo + delivered some text, they are not ready for some posting".
        // The gate previously checked only the text, so a sponsor with copy but no logo read as
        // ready — and §854 had already established that no logo means no graphic.
        // §1081 — "does this field carry text?" is now answered in ONE place
        // (SponsorCompanyContent.HasText), shared with the sponsor Get Started "company" step. Until
        // then the wizard did not ask for SocialMediaIntro at all, so a post could sit blocked here on
        // a field nothing was chasing. The wizard now requires it; this is the same question, asked
        // the same way. ⚠️ The list-filter queries below deliberately keep their inline SQL shape —
        // see the note there.
        var missing = new List<string>();
        if (!Core.Sponsors.SponsorCompanyContent.HasText(sponsor.SocialMediaIntro)) missing.Add("their social-media text");
        if (string.IsNullOrWhiteSpace(sponsor.LogoRasterFileName)
            && string.IsNullOrWhiteSpace(sponsor.LogoVectorFileName)) missing.Add("their logo");

        if (missing.Count == 0) return await GraphicBlockerAsync(post, ct);

        // Name what is missing, not merely that something is: he has to chase it (§854).
        return $"{sponsor.CompanyName ?? companyId} has not delivered {string.Join(" or ", missing)} "
             + "yet, so this post cannot be approved. It stays planned, and becomes approvable as "
             + "soon as that is filled in on their sponsor record.";
    }

    /// <summary>§1060(m) — the formats that are normally co-presented and so need 2+ linked speakers.</summary>
    public static bool IsCoPresented(SessionType type) =>
        type is SessionType.MasterClass or SessionType.PanelDiscussion;

    /// <summary>
    /// §1218 — the session behind this post when the single-speaker override APPLIES to it: a
    /// co-presented format with exactly one linked speaker. Null otherwise.
    /// </summary>
    /// <remarks>
    /// Lives here, beside the rule it lifts, so the button is offered on exactly the posts the rule
    /// blocks — a page computing its own version would drift from the gate.
    /// </remarks>
    public async Task<SingleSpeakerOverride?> SingleSpeakerOverrideAsync(
        SoMePost post, CancellationToken ct = default)
    {
        if (post.TemplateKind != SoMeTemplateKind.Session
            || SoMeSponsorSessionKey.TryParse(post.SubjectKey, out _)
            || SessionIdOf(post) is not { } sessionId)
        {
            return null;
        }

        var s = await _db.Sessions
            .Where(x => x.Id == sessionId && x.EventId == post.EventId)
            .Select(x => new
            {
                x.Id, x.Title, x.Type, x.SoMeSingleSpeakerConfirmed,
                LinkedSpeakers = x.SessionSpeakers.Count,
            })
            .FirstOrDefaultAsync(ct);

        return s is not null && IsCoPresented(s.Type) && s.LinkedSpeakers == 1
            ? new SingleSpeakerOverride(s.Id, s.Title, s.Type, s.SoMeSingleSpeakerConfirmed)
            : null;
    }

    /// <summary>§1218 — see <see cref="SingleSpeakerOverrideAsync"/>.</summary>
    public sealed record SingleSpeakerOverride(int SessionId, string Title, SessionType Type, bool Confirmed);

    /// <summary>
    /// §1060(g) — NO GRAPHIC, NO ANNOUNCEMENT. Operator 2026-08-11: <i>"hard block"</i>, and
    /// <i>"some graphics must be released as gate first to plan a post. important."</i>
    /// </summary>
    /// <remarks>
    /// <para>🔑 Since §784.12(a) graphics are BORN <c>Released</c>, "released" and "exists" are the
    /// same question — <see cref="SoMeSubjectGraphic"/> now filters on Released, so asking it for a
    /// file name asks both at once.</para>
    ///
    /// <para>🔴 <b>Called LAST, from every content-clean exit rather than as a block of its own.</b>
    /// That is not a style choice — it is message PRIORITY, and the tests caught me getting it wrong.
    /// Placed before the sponsor checks, a sponsor who owes their social text was told <i>"waiting for
    /// the graphic"</i>: the one blocker they cannot clear, hiding the one they can. Content problems
    /// belong to a human and are chaseable (§854); the graphic is produced FOR them and usually
    /// clears by itself. ⇒ Every "no content blocker" path routes here, so this is the last word and
    /// never the first.</para>
    /// </remarks>
    private async Task<string?> GraphicBlockerAsync(SoMePost post, CancellationToken ct)
    {
        if (!SoMeSubjectGraphic.IsSubjectOwned(post)) return null;

        var graphic = await new SoMeSubjectGraphic(_db).CurrentFileNameAsync(post, ct);
        if (!string.IsNullOrWhiteSpace(graphic)) return null;

        // 🔴 §1060(k) — NAME THE DEPENDENCY, NOT THE SYMPTOM. Operator 2026-08-11: *"either they wait
        // for logo (which is a dependency for sponsor some graphics). then another dependency for the
        // some text. wording must reflect what we are waiting for"*.
        //
        // 🔑 A sponsor graphic is RENDERED FROM THE LOGO — `GenerateSponsorSingleAsync(…, byte[] logo,
        // …)`. So for a sponsor with no logo, "waiting for the graphic" is true and useless: it names
        // an artefact that CANNOT exist until they act, and hides the one thing they can do. §854 —
        // he has to chase these people, and a message that names no action is not chaseable.
        //
        // ⚠️ The §867.1 checks above already say "their logo" / "their social-media text" when a
        // SPONSOR post is missing one, and they run FIRST. So reaching here with a sponsor subject
        // means the deliverables ARE in and the render simply has not happened — a queue, not a
        // person. The two messages must therefore say different things, or the earlier one is wasted.
        if (SponsorCompanyIdOf(post) is { } waitingCompanyId)
        {
            var logoMissing = await _db.SponsorInfos
                .Where(s => s.EventId == post.EventId && s.SponsorCompanyId == waitingCompanyId)
                .Select(s => (s.LogoRasterFileName == null || s.LogoRasterFileName == "")
                             && (s.LogoVectorFileName == null || s.LogoVectorFileName == ""))
                .FirstOrDefaultAsync(ct);

            if (logoMissing)
            {
                return "waiting for the sponsor's LOGO — their social-media graphic is generated from "
                     + "it, so no logo means no artwork and no announcement. Upload the logo on their "
                     + "sponsor record and the graphic follows automatically.";
            }

            return "the social-media graphic for this sponsor has not been generated yet. Everything "
                 + "needed is delivered — this clears by itself on the next graphics run, and needs "
                 + "no action from anyone.";
        }

        // A session or track graphic is not built from a sponsor logo, so it must not borrow that
        // sentence (§767: never describe a convention you have not checked).
        return "waiting for the social-media graphic — this subject has no released artwork yet, and "
             + "an announcement is never sent without a picture. Graphics are generated and released "
             + "automatically, so this usually clears by itself.";
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
