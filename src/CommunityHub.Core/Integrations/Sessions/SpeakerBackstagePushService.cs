using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations.Sessions;

/// <summary>
/// The §58 STAGE 2 (CehToZoho) SPEAKER PUSH engine. For each CEH speaker it CREATES the
/// speaker in Zoho Backstage when it has no <see cref="SpeakerProfile.BackstageSpeakerId"/>
/// (then stores the returned id). The Backstage v3 speakers API is CREATE-ONLY (no update
/// endpoint, verified 2026-06-25), so an already-linked speaker is left ALONE (no
/// duplicate, no delete) — manual Backstage edits handle in-place changes (see
/// <see cref="SpeakerBioBackstageSyncService"/> for the alert-on-existing path). This keeps
/// the link 1:1 by the stored id (idempotent) and NEVER deletes.
///
/// <b>§58 DIRECTION GATE.</b> Active only when the edition's SPEAKER sync direction is
/// stage 2 (<see cref="SessionSyncDirection.CehToZoho"/>, on
/// <see cref="SessionSourceSetting.SpeakerSyncDirection"/> — SEPARATE from the session
/// direction). At the default stage 1 and at stage 3 it is INERT.
///
/// <b>Publish gate.</b> <c>featured</c> tracks <see cref="SpeakerProfile.SelectedForPublish"/>
/// so an unselected speaker is created non-featured (never highlighted/published).
///
/// <b>Approval + ring gates (stage-2 go-live, operator 2026-07-23).</b> Only an APPROVED
/// speaker is pushed: the participant must be <see cref="Participant.IsActive"/>, fully
/// activated (<see cref="ParticipantLifecycleState.Active"/>) AND categorized
/// (<see cref="SpeakerProfile.Category"/> non-null — an uncategorized speaker is excluded
/// from everything, §299 6.1). On top, when a <see cref="Settings.FeatureGateService"/> is
/// wired, the §26c <c>backstage-speaker-sync</c> feature gates per speaker: kill switch off
/// ⇒ EVERY speaker is held; otherwise a speaker whose <see cref="Participant.Ring"/> is
/// above the feature's released ring (catalog default Ring1) is held until promoted. Held
/// speakers count as Skipped with a reason. A null gate (tests/legacy) changes nothing.
/// </summary>
public sealed class SpeakerBackstagePushService
{
    /// <summary>The §26c feature key whose released ring scopes the speaker push.</summary>
    private const string RingFeatureKey = "backstage-speaker-sync";

    private readonly CommunityHubDbContext _db;
    private readonly ZohoClient _zoho;
    private readonly ZohoOptions _zohoOptions;

    private readonly Func<CancellationToken, Task<string?>>? _tokenOverride;

    // §59: when an ALREADY-LINKED speaker would be UPDATED, ENQUEUE a CehToZoho Update delta
    // for operator approval instead of acting inline (NEW speakers still create directly). LAZY
    // (Func) so DI builds this push service WITHOUT eagerly constructing the queue (the queue
    // depends back on this service for apply-on-approve). Null ⇒ no queue wired.
    private readonly Func<SyncDeltaQueueService>? _queueFactory;

    // RULE (operator 2026-07-23): every CEH-made Zoho write must notify info@expertslive.dk
    // (the operator must publish/delete manually in Backstage). Optional so tests/legacy
    // constructions keep compiling; null ⇒ no notification.
    private readonly Email.ZohoChangeNotifier? _zohoChanges;

    // §26c ring gate (stage-2 go-live): optional so tests/legacy constructions keep
    // compiling; null ⇒ no ring gating (behaviour unchanged).
    private readonly Settings.FeatureGateService? _gate;

    public SpeakerBackstagePushService(
        CommunityHubDbContext db, ZohoClient zoho, ZohoOptions zohoOptions,
        Func<CancellationToken, Task<string?>>? tokenOverride = null,
        Func<SyncDeltaQueueService>? queueFactory = null,
        Email.ZohoChangeNotifier? zohoChanges = null,
        Settings.FeatureGateService? gate = null)
    {
        _db = db; _zoho = zoho; _zohoOptions = zohoOptions;
        _tokenOverride = tokenOverride; _queueFactory = queueFactory;
        _zohoChanges = zohoChanges;
        _gate = gate;
    }

    public enum PushAction { Skipped = 0, Created = 1, AlreadyLinked = 2, Failed = 3, Enqueued = 4 }

    public sealed record SpeakerPushResult(
        int ParticipantId, string Email, PushAction Action, string? BackstageId = null, string? Error = null);

    public sealed record Result(
        bool DirectionActive,
        string? InactiveReason,
        bool SourceAvailable,
        string? UnavailableReason,
        int Created,
        int AlreadyLinked,
        int Failed,
        int Skipped,
        IReadOnlyList<SpeakerPushResult> Items,
        int Enqueued = 0)
    {
        public static Result Inactive(string reason) =>
            new(false, reason, false, null, 0, 0, 0, 0, Array.Empty<SpeakerPushResult>());

        public static Result Unavailable(string reason) =>
            new(true, null, false, reason, 0, 0, 0, 0, Array.Empty<SpeakerPushResult>());
    }

    /// <summary>
    /// Run one push pass for an edition. Gated on §58 stage 2 (SpeakerSyncDirection ==
    /// CehToZoho). Creates each not-yet-linked APPROVED, ring-released speaker in Zoho and
    /// stores the id (held speakers count as Skipped with a reason — see the class doc);
    /// leaves already-linked speakers untouched. Never deletes.
    /// </summary>
    public async Task<Result> RunAsync(int eventId, CancellationToken ct = default)
    {
        // 🔒 §569 — THE §58 SPEAKER DIRECTION GATE IS GONE. DO NOT REINTRODUCE IT.
        // Same removal, same reasoning as the session direction gate (see
        // SessionBackstagePushService.RunAsync): stage 2 is the permanent mode, so this switch had
        // one legal value and existed only to be left off by accident — which is what happened.
        var token = await GetTokenAsync(ct);
        if (token is null)
            return Result.Unavailable("No Zoho access token (token refresh failed).");

        // Each speaker profile + its participant identity (email/name) and the approval/
        // ring fields the stage-2 gates read (active, activated, ring).
        var speakers = await _db.SpeakerProfiles
            .Where(p => p.EventId == eventId)
            .Join(_db.Participants, p => p.ParticipantId, pa => pa.Id,
                (p, pa) => new { Profile = p, pa.Email, pa.IsActive, pa.LifecycleState, pa.Ring })
            .ToListAsync(ct);

        // §301b SELF-HEAL + §304 ADOPT-BY-EMAIL (operator 2026-07-24): fetch the LIVE
        // speaker index once per pass — LAZILY, when there is a linked profile to verify
        // OR an unlinked one to match. Uses: (a) a linked profile whose stored id is gone
        // was deleted in the Backstage UI — re-link by e-mail when the record exists under
        // another id, else NULL + re-create below; (b) §304: an UNLINKED approved speaker
        // whose e-mail ALREADY has a Backstage record (e.g. re-imported from Sessionize
        // after a CEH delete) ADOPTS that record instead of failing on the duplicate-
        // e-mail create — the approval flow then reaches Zoho with no manual repair.
        // ⚠ FAIL-SAFE: an EMPTY index is indistinguishable from a failed read — skip
        // healing/adopting entirely then (never mass-NULL on an outage). A false NULL
        // from a PARTIAL index is self-correcting: Zoho refuses the duplicate-e-mail
        // create, and the next good pass re-links.
        HashSet<string>? liveSpeakerIds = null;
        IReadOnlyDictionary<string, string>? liveSpeakersByEmail = null;
        var liveIndexFetched = false;
        async Task FetchLiveIndexOnceAsync()
        {
            if (liveIndexFetched) return;
            liveIndexFetched = true;
            try
            {
                var idx = await _zoho.GetSpeakerIdsByEmailAsync(token, ct);
                if (idx.Count > 0)
                {
                    liveSpeakersByEmail = idx;
                    liveSpeakerIds = idx.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
                }
            }
            catch { /* fail-safe: no healing/adopting this pass */ }
        }

        // 🔒 §569 — KILL SWITCH ONLY. THE RELEASED-RING LOOKUP IS GONE.
        // Operator 2026-07-28: "yes, approved speaker syncs regardless of ring - remove those
        // gates. we dont need more gates here. lets simplify. once the speaker category is set +
        // they are active, they must sync to zoho." The on/off switch stays (a valid
        // stop-everything control); the RING behind it is retired.
        var ringFeatureEnabled = _gate is null
            || await _gate.IsFeatureEnabledAsync(RingFeatureKey, eventId, ct);

        int created = 0, alreadyLinked = 0, failed = 0, skipped = 0, enqueued = 0, healed = 0;
        var items = new List<SpeakerPushResult>(speakers.Count);
        // Operator 2026-07-23: collect every SUCCESSFUL Zoho write for the batched ops mail.
        var zohoWrites = new List<string>();

        // Operator 2026-07-24: a session already in Zoho can NEVER gain a speaker via the
        // API (create-only) — and delete+recreate would lose attendees' session favorites.
        // So when a speaker is created here AFTER their session(s) were pushed, the ops mail
        // must tell the operator to link the speaker to those sessions in the Backstage UI.
        var sessionsInZohoBySpeaker = (await _db.SessionSpeakers.AsNoTracking()
                .Join(_db.Sessions, ss => ss.SessionId, s => s.Id,
                    (ss, s) => new { ss.ParticipantId, s.EventId, s.Title, s.BackstageSessionId })
                .Where(x => x.EventId == eventId && x.BackstageSessionId != null)
                .ToListAsync(ct))
            .GroupBy(x => x.ParticipantId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Title).ToList());
        // §302: the queue factory is no longer consulted here — the CehToZoho
        // update-delta enqueue is retired (one-way decision, 2026-07-24).

        foreach (var row in speakers)
        {
            var p = row.Profile;
            var email = row.Email;

            if (string.IsNullOrWhiteSpace(email))
            {
                skipped++;
                items.Add(new SpeakerPushResult(p.ParticipantId, email ?? string.Empty, PushAction.Skipped,
                    p.BackstageSpeakerId, "speaker has no email — not pushed"));
                continue;
            }

            // APPROVAL GATE (stage-2 go-live, operator 2026-07-23): only an APPROVED
            // speaker — active (not withdrawn), fully ACTIVATED, categorized (§299 6.1:
            // an uncategorized speaker is excluded from everything) — is pushed.
            if (!row.IsActive
                || row.LifecycleState != ParticipantLifecycleState.Active
                || p.Category is null)
            {
                skipped++;
                items.Add(new SpeakerPushResult(p.ParticipantId, email, PushAction.Skipped,
                    p.BackstageSpeakerId,
                    "not approved (inactive / not activated / uncategorized) — held"));
                continue;
            }

            // §415 GET-STARTED GATE (operator 2026-07-27: "dont push to zoho before the get started
            // have completed - otherwise it will fail").
            //
            // A speaker who has not finished onboarding has a half-filled profile, and Zoho
            // VALIDATES on create: his run failed 4 of 4 with HTTP 400 "The country code must be in
            // ISO Alpha-2 format". Pushing an incomplete profile cannot succeed — it just burns an
            // API call and puts an alarming failure line in the ops mail every pass.
            //
            // Gated on the SPEAKER-DETAILS step specifically, not the whole wizard. That step is
            // where every field Zoho needs is collected (country, accreditation, bio, links), and it
            // is marked done by `BioLastEditedBySpeakerAt` — the "the speaker actually edited this"
            // marker (P13), which the Sessionize import never sets. Requiring the FULL wizard would
            // hold a complete, Zoho-ready profile hostage to an unrelated step like Swag or Party,
            // which is not what the failure was about.
            if (p.BioLastEditedBySpeakerAt is null)
            {
                skipped++;
                items.Add(new SpeakerPushResult(p.ParticipantId, email, PushAction.Skipped,
                    p.BackstageSpeakerId,
                    "Get Started not completed (speaker details not filled in yet) — held"));
                continue;
            }

            // 🔒 §569 — KILL SWITCH ONLY. The per-speaker RING test is REMOVED.
            // It is what made every speaker read "outside the backstage-speaker-sync released ring
            // — held" on the Pending-speakers page (§550): speakers are Ring 3, the sync ring was
            // Ring 2. Under his rule the condition is category + active, tested above — nothing here.
            if (!ringFeatureEnabled)
            {
                skipped++;
                items.Add(new SpeakerPushResult(p.ParticipantId, email, PushAction.Skipped,
                    p.BackstageSpeakerId,
                    "the backstage-speaker-sync feature is disabled — held"));
                continue;
            }

            // §301b SELF-HEAL: the stored id no longer exists in the live index — the record
            // was deleted in the Backstage UI. Re-link by e-mail when a record for this
            // e-mail exists under ANOTHER id (deleted + manually re-created in the UI);
            // otherwise NULL the link so the create path below re-creates it this pass.
            string? healedNote = null;
            if (!string.IsNullOrWhiteSpace(p.BackstageSpeakerId)) await FetchLiveIndexOnceAsync();
            if (!string.IsNullOrWhiteSpace(p.BackstageSpeakerId)
                && liveSpeakerIds is not null
                && !liveSpeakerIds.Contains(p.BackstageSpeakerId))
            {
                var stale = p.BackstageSpeakerId;
                if (liveSpeakersByEmail!.TryGetValue(email, out var otherId))
                {
                    p.BackstageSpeakerId = otherId;
                    p.UpdatedAt = DateTimeOffset.UtcNow;
                    healed++;
                    alreadyLinked++;
                    items.Add(new SpeakerPushResult(p.ParticipantId, email, PushAction.AlreadyLinked, otherId,
                        $"stored Backstage id {stale} was deleted — re-linked to the live record {otherId} by e-mail"));
                    continue;
                }
                p.BackstageSpeakerId = null;
                healed++;
                healedNote = $" (previous Backstage record {stale} was deleted in the UI — re-created)";
            }

            // Already linked ⇒ leave it alone (create-only API — no duplicate, no delete).
            // §302 ONE-WAY DECISION (operator 2026-07-24): the §59 CehToZoho update-delta
            // ENQUEUE is retired — a linked speaker's profile edit mails info@ the
            // field-level changes at SAVE time (SpeakerDetailsFormService), so the hourly
            // pass enqueues nothing and generates no approval noise.
            if (!string.IsNullOrWhiteSpace(p.BackstageSpeakerId))
            {
                alreadyLinked++;
                items.Add(new SpeakerPushResult(p.ParticipantId, email, PushAction.AlreadyLinked, p.BackstageSpeakerId));
                continue;
            }

            // §304 ADOPT-BY-EMAIL (LINK-ONLY — operator 2026-07-24: "technically it isn't
            // supported"): unlinked, but a Backstage record for this e-mail already exists
            // ⇒ adopt its ID instead of a duplicate create Zoho would refuse. This WRITES
            // NO DATA (the speakers API is create-only) — it only restores the link, so a
            // duplicate-create failure can't loop and no re-invitation fires. Because the
            // adopted record may carry OUTDATED content that the API can never overwrite,
            // the ops mail says so explicitly: refreshing = delete the record in the
            // Backstage UI and CEH re-creates it fresh (CEH is the master).
            await FetchLiveIndexOnceAsync();
            if (liveSpeakersByEmail is not null
                && liveSpeakersByEmail.TryGetValue(email.Trim(), out var existingId)
                && !string.IsNullOrWhiteSpace(existingId))
            {
                p.BackstageSpeakerId = existingId;
                p.UpdatedAt = DateTimeOffset.UtcNow;
                healed++;
                alreadyLinked++;
                items.Add(new SpeakerPushResult(p.ParticipantId, email, PushAction.AlreadyLinked, existingId,
                    "adopted the existing Backstage record for this e-mail (link only — no data written)"));
                zohoWrites.Add(
                    $"NOTE: linked speaker '{email}' to the EXISTING Backstage record {existingId} — "
                    + "the API cannot update its content; if it is outdated, DELETE the record in the "
                    + "Backstage UI and the next pass re-creates it fresh from CEH (CEH is the master).");
                continue;
            }

            // §302b: skills DERIVE via the one ZohoFieldMap (accreditation first, MVP
            // categories complement, full labels) — the old "MvpCategories ?? Accreditation"
            // masked the accreditation whenever MVP categories were set (Skills landed
            // empty/partial in Zoho); company rides when CEH captured it.
            var create = await _zoho.CreateSpeakerAsync(
                token, email, p.FirstName, p.LastName, p.Country,
                p.Tagline, p.Biography, p.LinkedIn, p.Twitter,
                skills: ZohoFieldMap.SpeakerSkills(p),
                featured: p.SelectedForPublish, ct,
                company: p.CompanyName);

            // 🔒 §609 — BLOCKED BY THE §340-H GUARD IS **SKIPPED**, NOT FAILED. Same rule as the
            // session push: on DEV every write is refused by design, and alerting on a permanent,
            // correct condition only teaches the operator to ignore alerts.
            if (string.Equals(create.Error, ZohoClient.ExternalWritesDisabledError, StringComparison.Ordinal))
            {
                skipped++;
                items.Add(new SpeakerPushResult(p.ParticipantId, email, PushAction.Skipped, null,
                    "external writes are disabled for this host — not pushed"));
                continue;
            }

            if (create.Error is not null)
            {
                failed++;
                items.Add(new SpeakerPushResult(p.ParticipantId, email, PushAction.Failed, null,
                    $"Zoho speaker create failed: {create.Error}"));
                continue;
            }
            var id = create.Id ?? string.Empty;

            // id == "" means created but id not parsed — record the create without storing a link.
            if (id.Length > 0)
            {
                p.BackstageSpeakerId = id;
                p.UpdatedAt = DateTimeOffset.UtcNow;
            }
            created++;
            items.Add(new SpeakerPushResult(p.ParticipantId, email, PushAction.Created,
                id.Length > 0 ? id : null));
            var who = $"{p.FirstName} {p.LastName}".Trim();
            var display = who.Length > 0 ? who : email;
            zohoWrites.Add((id.Length > 0
                ? $"Created speaker '{display}' ({email}) (Backstage id {id})"
                : $"Created speaker '{display}' ({email})") + healedNote);

            // Operator 2026-07-24: the speaker's session(s) already exist in Zoho — the API
            // cannot attach a speaker to an existing session, and delete+recreate loses
            // attendees' favorites. Tell the operator to link manually in the Backstage UI.
            if (sessionsInZohoBySpeaker.TryGetValue(p.ParticipantId, out var titles))
            {
                foreach (var title in titles)
                {
                    zohoWrites.Add(
                        $"ACTION NEEDED: link the new speaker '{display}' ({email}) to the existing "
                        + $"session '{title}' in the Backstage UI — the API cannot attach speakers to "
                        + "an existing session, and deleting/recreating the session would lose favorites.");
                }
            }

            // §363 (operator 2026-07-26: "i acknowledge the photo limitation … i guess we need to do
            // this manually as well and upload picture as well from sharepoint - put that into the
            // mail an organizer must do when speakers are synced").
            //
            // §356 established that the Zoho speaker API carries NO photo field, so CEH can never
            // push one — the speaker would simply appear photo-less on the public programme with
            // nothing anywhere to say why. Same precedent as §322l, where session descriptions are
            // unsettable via the API and the ACTION mail therefore carries the paste-ready text:
            // when the API cannot do it, the mail hands the operator everything needed to do it by
            // hand. Here that means the LINK TO THE PHOTO, so it is not a hunt through SharePoint.
            var photo = !string.IsNullOrWhiteSpace(p.PhotoSharePointPath)
                ? p.PhotoSharePointPath
                : p.PhotoUrl;
            zohoWrites.Add(string.IsNullOrWhiteSpace(photo)
                ? $"ACTION NEEDED: upload a photo for the new speaker '{display}' ({email}) in "
                  + "Backstage — the Zoho speaker API has no photo field, so CEH can never push it. "
                  + "NO photo is on file in the hub yet either, so ask the speaker for one first."
                : $"ACTION NEEDED: upload the photo for the new speaker '{display}' ({email}) in "
                  + "Backstage — the Zoho speaker API has no photo field, so CEH can never push it. "
                  + $"The photo is here: {photo}");
        }

        if (created > 0 || healed > 0) await _db.SaveChangesAsync(ct);

        // Operator 2026-07-23: ONE batched ops mail per pass listing every successful Zoho
        // write (the operator must publish/delete manually in Backstage). Never throws.
        if (_zohoChanges is not null)
            await _zohoChanges.NotifyAsync("Speakers", zohoWrites, ct);

        return new Result(true, null, true, null, created, alreadyLinked, failed, skipped, items, enqueued);
    }

    /// <summary>
    /// Build the {Field, Old, New} diff list for a CehToZoho speaker UPDATE delta. CEH is the
    /// source of truth, so NewValue carries the current CEH value an approve would push;
    /// OldValue is null. Only non-blank fields are included.
    /// </summary>
    private static IReadOnlyList<SyncFieldChange> BuildSpeakerPushChanges(SpeakerProfile p, string email)
    {
        var list = new List<SyncFieldChange>();
        var name = $"{p.FirstName} {p.LastName}".Trim();
        if (!string.IsNullOrWhiteSpace(name))
            list.Add(new SyncFieldChange(SyncDeltaQueueService.FieldName, null, name));
        if (!string.IsNullOrWhiteSpace(p.Tagline))
            list.Add(new SyncFieldChange("Tagline", null, p.Tagline));
        if (!string.IsNullOrWhiteSpace(p.Biography))
            list.Add(new SyncFieldChange("Biography", null, p.Biography));
        return list;
    }

    /// <summary>
    /// Apply an approved CehToZoho speaker UPDATE on approve (REQUIREMENTS §59). The Backstage
    /// v3 speakers API is CREATE-ONLY (no update endpoint, verified 2026-06-25), so there is no
    /// in-place push: this acknowledges the approval and reports that the Backstage edit must be
    /// made manually. It NEVER creates a duplicate and NEVER deletes. Returns (ok, message).
    /// </summary>
    public async Task<(bool Ok, string Message)> UpdateLinkedSpeakerAsync(
        int eventId, int participantId, CancellationToken ct = default)
    {
        var profile = await _db.SpeakerProfiles
            .FirstOrDefaultAsync(p => p.ParticipantId == participantId && p.EventId == eventId, ct);
        if (profile is null)
            return (false, "The speaker no longer exists in this edition.");
        if (string.IsNullOrWhiteSpace(profile.BackstageSpeakerId))
            return (false, "The speaker is not linked to a Zoho speaker (nothing to update).");

        // Create-only API: record the acknowledgement; the Backstage speaker edit is manual.
        return (true,
            "Acknowledged — the Backstage speaker API is create-only; apply the bio/profile "
            + "change manually in Backstage. CEH never creates a duplicate or deletes.");
    }

    private async Task<string?> GetTokenAsync(CancellationToken ct) =>
        _tokenOverride is not null ? await _tokenOverride(ct) : await _zoho.GetAccessTokenAsync(ct);
}
