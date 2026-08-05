using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations.Sessions;

/// <summary>
/// The §57 STAGE 2 (CehToZoho) SESSION PUSH engine. For each CEH session it CREATES the
/// session in Zoho Backstage when it has no <see cref="Session.BackstageSessionId"/> (then
/// stores the returned id), or UPDATES the existing Backstage session when it already has
/// one — keeping the two 1:1 by the stored id (idempotent). It NEVER deletes anything.
///
/// <b>§57 DIRECTION GATE.</b> This engine is only active when the edition's session sync
/// direction is stage 2 (<see cref="SessionSyncDirection.CehToZoho"/>). At the default
/// stage 1 (Sessionize→CEH) and at stage 3 (Zoho→CEH, the §38e read engine) it is INERT —
/// it pushes nothing. The two directions are mutually exclusive by design: the push (§57
/// stage 2) and the change-detection read (§38e / stage 3) never run for the same edition
/// at the same time.
///
/// <b>What is pushed.</b> Title (required), abstract→description, start_time, duration
/// (derived from end−start, falling back to the <see cref="Session.Length"/> bucket),
/// and the 1-based agenda day (derived from the session's start date relative to the
/// edition's first agenda day). A CREATE is the §299 COMPLETE session (stage-2 go-live,
/// operator 2026-07-23): the resolved TRACK is REQUIRED (created under the target name
/// when missing; a blank track or a failed track create fails that session with NO
/// session POST), the VENUE rides along (hall created on demand for registry-known rooms
/// with a capacity; expo/unknown rooms send no venue, warn-only) and the linked speakers'
/// distinct e-mails are attached. Service sessions (breaks/lunch) are skipped and §299 4.5
/// TEST sessions (<see cref="Session.UsedForTesting"/>) are NEVER pushed.
/// </summary>
public sealed class SessionBackstagePushService
{
    private readonly CommunityHubDbContext _db;
    private readonly ZohoClient _zoho;
    private readonly ZohoOptions _zohoOptions;

    // Overridable token source (default = the real ZohoClient refresh). Tests inject a
    // canned token so the gate + push-decision logic is exercised without a token refresh.
    private readonly Func<CancellationToken, Task<string?>>? _tokenOverride;

    // §59: when an ALREADY-LINKED session would be UPDATED, ENQUEUE a CehToZoho Update delta
    // for operator approval instead of pushing inline (NEW sessions still create directly).
    // LAZY (Func) so DI builds the push service WITHOUT eagerly constructing the queue — the
    // queue itself depends on this push service for apply-on-approve, and a lazy factory breaks
    // that otherwise-circular graph. Null ⇒ no queue wired (legacy inline-update behaviour).
    private readonly Func<SyncDeltaQueueService>? _queueFactory;

    // §299.6/b5: WARN-ONLY room-name validation against the config registry — a
    // session pushed with a room name not in sessionRooms gets a warning LOG (the
    // push NEVER blocks; names must be byte-identical across systems, so the warn
    // surfaces exactly the drift). Both optional so legacy constructions/tests
    // stay valid; a null/empty registry keeps the check quiet.
    private readonly Microsoft.Extensions.Logging.ILogger<SessionBackstagePushService>? _logger;
    private readonly Config.RoomRegistryService? _rooms;

    // RULE (operator 2026-07-23): every CEH-made Zoho write must notify info@expertslive.dk
    // (the operator must publish/delete manually in Backstage). Optional so tests/legacy
    // constructions keep compiling; null ⇒ no notification.
    private readonly Email.ZohoChangeNotifier? _zohoChanges;

    // INCIDENT FIX (operator 2026-07-24): attaching a speaker E-MAIL to a session create
    // makes Zoho AUTO-CREATE the speaker record AND SEND THAT PERSON AN INVITATION MAIL —
    // 19 real speakers were invited prematurely because the attach list ignored the ring
    // scope. Session creates may therefore only attach speakers passing the SAME gate as
    // the speaker push engine (approved + inside the backstage-speaker-sync ring), resolved
    // via this gate. FAIL-CLOSED: with no gate wired, NO speaker e-mails are attached.
    private readonly Settings.FeatureGateService? _gate;

    public SessionBackstagePushService(
        CommunityHubDbContext db, ZohoClient zoho, ZohoOptions zohoOptions,
        Func<CancellationToken, Task<string?>>? tokenOverride = null,
        Func<SyncDeltaQueueService>? queueFactory = null,
        Microsoft.Extensions.Logging.ILogger<SessionBackstagePushService>? logger = null,
        Config.RoomRegistryService? rooms = null,
        Email.ZohoChangeNotifier? zohoChanges = null,
        Settings.FeatureGateService? gate = null)
    {
        _db = db; _zoho = zoho; _zohoOptions = zohoOptions;
        _tokenOverride = tokenOverride; _queueFactory = queueFactory;
        _logger = logger; _rooms = rooms;
        _zohoChanges = zohoChanges;
        _gate = gate;
    }

    /// <summary>
    /// The participant ids whose e-mails a session create may attach: an ORGANIZER-APPROVED
    /// speaker — <c>Category</c> set (§299 6.1) + <c>IsActive</c> + lifecycle Active.
    /// <para>
    /// §569 (operator 2026-07-28): the RING filter is GONE — *"once the speaker category is set +
    /// they are active, they must sync to zoho"*. The <c>backstage-speaker-sync</c> KILL SWITCH is
    /// still honoured (that is a stop-everything control, not a rollout gate).
    /// </para>
    /// </summary>
    private async Task<HashSet<int>> AttachableSpeakerIdsAsync(int eventId, CancellationToken ct)
    {
        // 🔒 §569 — THE RING FILTER IS REMOVED, AND THE NO-GATE CASE NO LONGER FAILS CLOSED.
        //
        // This method previously returned "approved AND ring-eligible" and fell back to an EMPTY
        // set whenever no gate was wired. That made it the leading suspect for the live blocker
        // (§567.A): speakers sit at Ring 3 while backstage-speaker-sync was released to Ring 2, so
        // the attach set was empty on every pass — silently, with no failure anywhere.
        //
        // ⚠️ §326bx, THE INCIDENT THIS MUST STILL RESPECT: attaching a speaker's E-MAIL to a session
        // create makes Zoho AUTO-CREATE the speaker AND SEND THEM AN INVITATION — 19 real speakers
        // were invited prematurely on 2026-07-24, which is why the ring filter was added. His rule
        // deliberately replaces "ring-eligible" with "categorized + active", so SETTING A CATEGORY
        // ON AN ACTIVE SPEAKER NOW MEANS "INVITE THIS PERSON". That is his stated intent — but it
        // MUST be said on the Pending-speakers screen, or the consequence is a surprise (§569 to-do 4).
        if (_gate is not null
            && !await _gate.IsFeatureEnabledAsync("backstage-speaker-sync", eventId, ct))
        {
            // The kill switch — "stop everything now" — is the ONE thing that still empties this set.
            return new HashSet<int>();
        }

        var candidates = await _db.SpeakerProfiles.AsNoTracking()
            .Where(sp => sp.EventId == eventId && sp.Category != null)
            .Join(_db.Participants, sp => sp.ParticipantId, p => p.Id,
                (sp, p) => new { p.Id, p.IsActive, p.LifecycleState })
            .Where(p => p.IsActive && p.LifecycleState == ParticipantLifecycleState.Active)
            .ToListAsync(ct);
        return candidates.Select(p => p.Id).ToHashSet();
    }

    /// <summary>
    /// §574 — everything about a JUST-CREATED session that is still NOT right in Zoho, rendered
    /// onto the same mail entry as the create itself.
    /// <para>
    /// Operator 2026-07-28: *"i missed in the email information about the missing tags for sessions
    /// like session language, session level + tags. it should have been in the initial email about
    /// the creation, instead of coming later"*.
    /// </para>
    /// </summary>
    /// <remarks>
    /// ❗ WHY THIS BELONGS ON THE CREATE LINE AND NOWHERE ELSE: the Zoho sessions API is
    /// CREATE-ONLY. A field not set at create can NEVER be pushed afterwards — it is a manual
    /// Backstage edit forever. So the moment of the create is the cheapest moment to fix it, and
    /// telling him in a later mail is telling him after that moment has passed.
    ///
    /// Two different kinds of gap are reported, and the wording keeps them apart on purpose:
    ///   • "the API cannot send it" — language / level / tags / description. CEH may well HAVE the
    ///     value; the create payload has no field for it (see ZohoClient.BuildSessionPayload,
    ///     where description is explicitly excluded on create). Nothing to fix in CEH.
    ///   • "CEH has no value" — room, speakers. Fixable in CEH, but only BEFORE the create, which
    ///     is why silence here was expensive: §571.3 found all 9 sessions pushed with no venue and
    ///     "Master Class: AI low code" with no speaker at all, and he was told none of it.
    /// </remarks>
    public static string DescribeCreateGaps(
        Session s, string? venueId, IReadOnlyList<string>? speakerEmails)
    {
        var manual = new List<string>();
        var missing = new List<string>();

        // --- Carried by CEH, but the create payload has no field for it -------------------
        // §760 — value printed BARE: this is a value he has to paste into Backstage by hand, and
        // quotes get caught by a drag-selection. The label already delimits it.
        manual.Add(Has(s.Level) ? $"level {s.Level!.Trim()}" : "level (none in CEH)");
        manual.Add("language");
        if (Has(s.Abstract)) manual.Add("description/abstract");

        // --- Genuinely absent, and only fixable BEFORE the create -------------------------
        if (!Has(s.Room)) missing.Add("no room set in CEH");
        else if (venueId is null) missing.Add($"room \"{s.Room!.Trim()}\" did not resolve to a Backstage hall");
        if (speakerEmails is not { Count: > 0 })
            missing.Add("NO speaker attached (Zoho will show \"speaker to be announced\")");

        var sb = new System.Text.StringBuilder();
        sb.Append("\nSet manually in Backstage — the API cannot send these on create: ")
          .Append(string.Join(", ", manual)).Append('.');

        // §594 — TAGS GET THEIR OWN PASTE-READY LINE, HERE AND NOWHERE ELSE.
        //
        // Zoho neither accepts tags on create nor returns them on read, so this create-time entry
        // is the ONLY moment the information is both complete and actionable. The line is plain
        // comma-separated because Zoho's Tags box takes exactly that, so it pastes in one go —
        // the useful half of the old (unsatisfiable) "Tags missing" mail, kept.
        var tags = ZohoFieldMap.SessionTags(s);
        if (tags.Count > 0)
            sb.Append("\nTags — paste this line into the Tags box: ").Append(string.Join(", ", tags));

        if (missing.Count > 0)
            sb.Append("\nGaps in CEH at create time: ").Append(string.Join("; ", missing)).Append('.');
        return sb.ToString();

        static bool Has(string? v) => !string.IsNullOrWhiteSpace(v);
    }

    /// <summary>§299.6/b5 — log-warn (never block) when a pushed session's room name is
    /// non-blank and not in the configured registry. Quiet without a registry/logger.</summary>
    private void WarnOnUnknownRoom(Session s)
    {
        if (_logger is null || _rooms is not { HasEntries: true }) return;
        if (string.IsNullOrWhiteSpace(s.Room) || _rooms.IsKnown(s.Room)) return;
        Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(
            _logger,
            "Session {SessionId} '{Title}' has room '{Room}' which is NOT in the configured "
            + "room registry (sessionRooms). Room names must be byte-identical across systems "
            + "— check for a typo/rename. The push was not blocked.",
            s.Id, s.Title, s.Room);
    }

    /// <summary>
    /// The TARGET Backstage track name for a CEH (= Sessionize) track label: the
    /// <see cref="ZohoOptions.TrackNameMap"/> entry when one exists (operator 2026-07-23 —
    /// the two AI tracks carry shorter names in Zoho), else the Sessionize label itself.
    /// </summary>
    internal string TargetTrackName(string cehTrack)
    {
        var name = cehTrack.Trim();
        return _zohoOptions.TrackNameMap is { Count: > 0 } map
               && map.TryGetValue(name, out var mapped)
               && !string.IsNullOrWhiteSpace(mapped)
            ? mapped.Trim()
            : name;
    }

    /// <summary>
    /// Resolve a CEH (= Sessionize) track NAME to the Backstage track ID: EXACT
    /// case-insensitive match on the <see cref="TargetTrackName"/> (mapped, else the
    /// Sessionize label) — deliberately NO fuzzy substring matching (a substring rule would
    /// mis-file e.g. "Data Compliance &amp; Security" under "SECURITY"). Null when no exact
    /// match (a blank CEH track resolves to nothing); <see cref="CreateOneAsync"/> then
    /// CREATES the missing track under the target name, while the lenient RunAsync/update
    /// paths simply omit the track.
    /// </summary>
    internal string? ResolveTrackId(string? cehTrack, IReadOnlyList<ZohoClient.BackstageTrack> tracks)
    {
        if (string.IsNullOrWhiteSpace(cehTrack) || tracks.Count == 0) return null;
        var name = TargetTrackName(cehTrack);
        return tracks.FirstOrDefault(t =>
            string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    /// <summary>What happened to one session in a push pass (for the job log + tests).</summary>
    public enum PushAction { Skipped = 0, Created = 1, Updated = 2, Failed = 3, Enqueued = 4 }

    /// <summary>Per-session outcome.</summary>
    public sealed record SessionPushResult(
        int SessionId, string Title, PushAction Action, string? BackstageId = null, string? Error = null);

    /// <summary>The outcome of one push pass.</summary>
    public sealed record Result(
        bool DirectionActive,
        string? InactiveReason,
        bool SourceAvailable,
        string? UnavailableReason,
        int Created,
        int Updated,
        int Failed,
        int Skipped,
        IReadOnlyList<SessionPushResult> Items,
        int Enqueued = 0)
    {
        public static Result Inactive(string reason) =>
            new(false, reason, false, null, 0, 0, 0, 0, Array.Empty<SessionPushResult>());

        public static Result Unavailable(string reason) =>
            new(true, null, false, reason, 0, 0, 0, 0, Array.Empty<SessionPushResult>());
    }

    /// <summary>
    /// Run one push pass for an edition. Gated on §57 stage 2 (CehToZoho). Pushes every
    /// non-service, non-test CEH session: create-if-unlinked / update-if-linked, idempotent
    /// by the stored Backstage id. Never deletes.
    /// </summary>
    public async Task<Result> RunAsync(int eventId, CancellationToken ct = default)
    {
        // 🔒 §569 — THE §57 DIRECTION GATE IS GONE. DO NOT REINTRODUCE IT.
        //
        // Operator 2026-07-28: "sessions should also NOT have gates, once they sync with sessionize
        // they must flow to zoho … as we are in stage 2 mode". Stage 3 was deleted long ago, so
        // stage 2 (CEH→Zoho) is the permanent and ONLY legal mode for this edition.
        //
        // A selector with one legal value is not a choice, it is a trap — and it was already sprung:
        // this gate silently blocked the push for WEEKS while the Jobs page showed a healthy green
        // run (§544/§551). Deleting it removes an entire class of INVISIBLE failure rather than
        // adding another alert for it.
        //
        // What still holds the line (safety, not gates — §569 keeps these explicitly):
        //   • UsedForTesting / IsServiceSession — a TEST session must never reach the public agenda
        //     (§299 4.5/b8); he has test sessions in CEH right now.
        //   • the TRACK requirement — Zoho's own constraint ("you can NOT create a session without a
        //     track"); it fails that one session, not the run.
        //   • Zoho:Enabled + the external-writes guard — the "stop everything now" kill switches.
        var token = await GetTokenAsync(ct);
        if (token is null)
            return Result.Unavailable("No Zoho access token (token refresh failed).");

        // The first agenda day anchor: the edition's pre-day (master classes) when set,
        // else its start date. day index is 1-based (agenda day 0 is empty).
        var ev = await _db.Events.AsNoTracking()
            .Where(e => e.Id == eventId)
            .Select(e => new { e.StartDate, e.PreDayDate })
            .FirstOrDefaultAsync(ct);
        var firstDay = ev?.PreDayDate ?? ev?.StartDate;

        // §299 4.5/b8: a TEST session must NEVER reach the public agenda — the bulk pass
        // carries the same UsedForTesting guard as CreateOneAsync/UpdateLinkedSessionAsync
        // (stage-2 go-live, operator 2026-07-23).
        var sessions = await _db.Sessions
            .Where(s => s.EventId == eventId && !s.IsServiceSession && !s.UsedForTesting)
            .ToListAsync(ct);

        // §301b SELF-HEAL (operator 2026-07-24: "if id doesn't exist … null the record and
        // create/sync records again"): a linked session whose stored Backstage id is gone
        // was deleted in the Backstage UI — NULL the link so the create path below re-creates
        // it this same pass (complete: track/hall/speakers). ⚠ FAIL-SAFE: an EMPTY live set
        // is indistinguishable from a failed read — skip healing entirely then (never
        // mass-NULL on an outage).
        var healed = 0;
        var healedSessionNotes = new Dictionary<int, string>();
        // §555 — sessions whose stored id a COMPLETE read confirms is gone. Reported for operator
        // approval, never unlinked automatically (an unlink is a re-create, and a wrong one
        // duplicates the agenda irreversibly).
        List<Session> staleLinks = new();
        // §302: the same strict live read also feeds the linked-session CHANGE DETECTION
        // below (one-way sync — a CEH change on an already-pushed session can only be
        // applied manually; the engine mails info@ once per distinct diff).
        IReadOnlyDictionary<string, ZohoClient.LiveSession>? liveSessions = null;
        if (sessions.Any(s => !string.IsNullOrWhiteSpace(s.BackstageSessionId)))
        {
            // §553/§554 — CONFIRM BEFORE PRUNING. Operator 2026-07-28: "it is important that you by
            // mistake dont auto-create sessions if fx api is causing issues inside zoho and you
            // cannot retrieve id … so it must maybe make a rety and try 3 times before pruning".
            //
            // Unlinking is what makes the create path fire, so a WRONG unlink duplicates the live
            // agenda. The read is therefore attempted up to 3 times, and a link is pruned ONLY when
            // an attempt SUCCEEDED and was COMPLETE (the read is strict: a partial page throws) and
            // the id was genuinely absent. Every other outcome — throw, empty set, auth failure —
            // is UNKNOWN: nothing is unlinked, nothing is created, and it is reported LOUDLY.
            //
            // The bare `catch { }` this replaces is why a 400 on every single call for months read
            // exactly like "nothing to heal".
            const int attempts = 3;
            string? readFailure = null;
            IReadOnlySet<string>? liveIds = null;

            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                try
                {
                    var map = await _zoho.GetLiveSessionMapAsync(token, ct);
                    // An EMPTY map cannot be told apart from a failed read (ZohoClient's own
                    // fail-safe contract), so it is never grounds to unlink.
                    if (map.Count == 0)
                    {
                        readFailure = "the live agenda read returned NOTHING, which cannot be "
                            + "distinguished from a failed read";
                        break;
                    }

                    liveSessions = map;
                    liveIds = map.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    readFailure = null;
                    break;
                }
                catch (Exception ex)
                {
                    readFailure = $"attempt {attempt}/{attempts}: {ex.Message}";
                    if (attempt < attempts)
                        await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
                }
            }

            if (liveIds is null)
            {
                // UNKNOWN — say so. This is the state that used to be silent.
                if (_logger is not null)
                {
                    Microsoft.Extensions.Logging.LoggerExtensions.LogError(
                        _logger,
                        "Session self-heal SKIPPED — could not read the live Backstage agenda after "
                        + "{Attempts} attempts ({Failure}). No link was pruned and no session was "
                        + "created. Stale links stay stale until this read works.",
                        attempts, readFailure ?? "unknown");
                }
            }
            else
            {
                // §555 — DETECT, THEN ASK. Operator 2026-07-28: "i dont want to end up have double
                // of sessions + double speakers + double sponsors … alternative let me as organizer
                // approve it". Unlinking is what makes the create path fire, so an unlink IS a
                // create — and a wrong one duplicates the live agenda, which cannot be undone
                // through this API (it never deletes). A confirmed-absent id is therefore
                // REPORTED, not acted on: the operator approves the re-create.
                var stale = new List<Session>();
                foreach (var s in sessions.Where(x => !string.IsNullOrWhiteSpace(x.BackstageSessionId)))
                {
                    var probe = ExternalLinkProbe.Probe(liveIds, s.BackstageSessionId);
                    if (probe.IsGone) stale.Add(s);   // Exists or Unknown ⇒ never touch the link
                }

                if (stale.Count > 0)
                {
                    if (_logger is not null)
                    {
                        Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(
                            _logger,
                            "{Count} session(s) point at Backstage records that a COMPLETE live read "
                            + "confirms are gone: {Ids}. NOT unlinked automatically — re-creating "
                            + "them is a write that cannot be undone, so it waits for approval.",
                            stale.Count,
                            string.Join(", ", stale.Select(x => $"{x.Title} [{x.BackstageSessionId}]")));
                    }

                    // §559 — RAISE EACH ONE FOR APPROVAL. Operator: "you can use this queue for
                    // this purpose", after "no clue where to find the place to approve". Enqueued
                    // BEFORE the mail so the mail can honestly point at a button that exists.
                    // Idempotent: EnqueueAsync dedupes on (event, type, id, kind), so a stale link
                    // detected every 5 minutes updates one row instead of stacking hundreds.
                    if (_queueFactory is not null)
                    {
                        var queue = _queueFactory();
                        foreach (var s in stale)
                        {
                            await queue.EnqueueStaleLinkAsync(
                                eventId, SyncDeltaEntityType.Session, s.Id.ToString(), s.Title,
                                s.BackstageSessionId!, SessionSyncDirection.CehToZoho, ct);
                        }
                    }

                    // §555 — TELL HIM, with the fix. Operator 2026-07-28: "i need mails and what i
                    // can do to fix ths for the future. deliver this now so i can tr to fix and get
                    // the sessions in". The whole failure was silent for months; the mail is the
                    // part that makes it not silent.
                    if (_zohoChanges is not null)
                    {
                        var lines = stale
                            .Select(x =>
                                $"<b>{System.Net.WebUtility.HtmlEncode(x.Title)}</b> — stored Backstage id "
                                + $"<code>{System.Net.WebUtility.HtmlEncode(x.BackstageSessionId)}</code> "
                                + "no longer exists in the live agenda.")
                            .ToList();
                        lines.Add(
                            "<br><b>What this means:</b> the hub still believes these sessions are in "
                            + "Backstage, so it neither updates nor re-creates them — which is why "
                            + "they are missing from your agenda.");
                        lines.Add(
                            "<b>How to fix it:</b> each of these is now waiting for you in the "
                            + "<b>Sync approval queue</b>. <b>Approve</b> clears the dead link, and "
                            + "the next push creates the session in Backstage again. <b>Reject</b> "
                            + "leaves it exactly as it is.");
                        lines.Add(
                            "<i>Why it asks first:</i> clearing the link IS the re-create, and this "
                            + "API cannot delete — so a re-create that turns out to be wrong would "
                            + "duplicate your agenda permanently.");
                        // §558/§559 — ACTIONABLE, and it now names a button that EXISTS. The first
                        // version of this mail pointed at the Zoho admin; the second admitted no
                        // approve action was built. Naming a button that is not there is what made
                        // it misleading, so this link changed in the same commit that built it.
                        await _zohoChanges.NotifyAsync(
                            "Sessions — stale Backstage links", lines, ct,
                            actionable: true,
                            actionUrl: "https://eldk27.eventhub.expertslive.dk/Organizer/SyncQueue",
                            actionText: "Open the sync approval queue");
                    }
                }

                staleLinks = stale;
            }
        }

        // Resolve track NAME → Backstage track ID once per pass (live-verified: the create
        // endpoint requires the track id, not the name), kept as a MUTABLE name→id cache so
        // a track CREATED for one session (create-if-missing, shared with CreateOneAsync)
        // is reused by the next — never two creates for the same target name in one pass.
        var trackCache = ToNameIdCache(
            (await _zoho.GetTracksAsync(token, ct)).Select(t => (t.Id, t.Name)));

        // VENUE cache — the Backstage halls are fetched ONCE per pass, lazily on the first
        // create that carries a room; a hall created during the pass joins the cache so two
        // sessions sharing a new room create it once.
        Dictionary<string, string>? hallCache = null;

        // SPEAKERS — every linked speaker e-mail for the edition in ONE query, grouped per
        // session — FILTERED to the attachable (approved + ring-eligible) speakers only
        // (INCIDENT FIX 2026-07-24: an attached e-mail makes Zoho create + INVITE the
        // speaker, so the ring scope must hold here too).
        var attachable = await AttachableSpeakerIdsAsync(eventId, ct);
        var speakerEmailsBySession = (await _db.SessionSpeakers.AsNoTracking()
                .Where(ss => ss.Session.EventId == eventId)
                .Select(ss => new { ss.SessionId, ss.ParticipantId, ss.Participant.Email })
                .ToListAsync(ct))
            .Where(x => attachable.Contains(x.ParticipantId))
            .GroupBy(x => x.SessionId)
            .ToDictionary(g => g.Key, g => NormalizeSpeakerEmails(g.Select(x => x.Email)));

        // The required, event-specific session type (operator config; no enumerable list).
        var sessionType = _zohoOptions.PushSessionType;

        int created = 0, updated = 0, failed = 0, skipped = 0, enqueued = 0;
        var items = new List<SessionPushResult>(sessions.Count);
        // Operator 2026-07-23: collect every SUCCESSFUL Zoho write for the batched ops mail.
        // §302: the CehToZoho update-delta ENQUEUE is retired (one-way decision) — linked
        // sessions get the hash-deduped change mail below instead of a queue approval.
        var zohoWrites = new List<string>();

        foreach (var s in sessions)
        {
            if (string.IsNullOrWhiteSpace(s.Title))
            {
                skipped++;
                items.Add(new SessionPushResult(s.Id, s.Title, PushAction.Skipped, s.BackstageSessionId,
                    "session has no title — not pushed"));
                continue;
            }

            // §299.6/b5: warn-only unknown-room check (never blocks the push).
            WarnOnUnknownRoom(s);

            var duration = DurationMinutes(s);
            var day = DayIndex(firstDay, s.StartsAt);

            if (string.IsNullOrWhiteSpace(s.BackstageSessionId))
            {
                // CREATE — not yet in Zoho. §299 stage-2 COMPLETE create (operator
                // 2026-07-23, the same contract as the live-verified CreateOneAsync pilot):
                // the TRACK is REQUIRED — a blank CEH track or a failed track create fails
                // THIS session with NO session POST; venue + speakers ride along.
                if (string.IsNullOrWhiteSpace(s.Track))
                {
                    failed++;
                    items.Add(new SessionPushResult(s.Id, s.Title, PushAction.Failed, null,
                        "The session has no track — the Zoho create requires one."));
                    continue;
                }
                var (trackId, trackError) =
                    await ResolveOrCreateTrackAsync(token, s.Track, trackCache, zohoWrites, ct);
                if (trackId is null)
                {
                    failed++;
                    items.Add(new SessionPushResult(s.Id, s.Title, PushAction.Failed, null, trackError));
                    continue;
                }

                string? venueId = null;
                if (!string.IsNullOrWhiteSpace(s.Room))
                {
                    hallCache ??= ToNameIdCache(await _zoho.GetHallsAsync(token, ct));
                    venueId = await ResolveOrCreateVenueAsync(token, s, hallCache, zohoWrites, ct);
                }

                var speakerEmails = speakerEmailsBySession.GetValueOrDefault(s.Id);
                var res = await _zoho.CreateSessionAsync(
                    token, day, s.Title, s.Abstract, s.StartsAt, duration, trackId, sessionType,
                    venueId, speakerEmails is { Count: > 0 } ? speakerEmails : null, ct);
                if (res.Ok)
                {
                    s.BackstageSessionId = res.Id;
                    s.UpdatedAt = DateTimeOffset.UtcNow;
                    created++;
                    items.Add(new SessionPushResult(s.Id, s.Title, PushAction.Created, res.Id));
                    zohoWrites.Add($"Created session '{s.Title}' (Backstage id {res.Id})"
                        + healedSessionNotes.GetValueOrDefault(s.Id)
                        + DescribeCreateGaps(s, venueId, speakerEmails));
                }
                else if (string.Equals(res.Error, ZohoClient.ExternalWritesDisabledError, StringComparison.Ordinal))
                {
                    // 🔒 §609 — BLOCKED BY THE §340-H GUARD IS **SKIPPED**, NOT FAILED.
                    //
                    // On DEV every write is refused BY DESIGN (*"we cannot have external writes from
                    // DEV !!!!"*). Counting that as a failure made the job mail a red "session push:
                    // failures" listing all 11 sessions after every DEV deploy — an alert for a
                    // permanent, correct condition, which is the fastest way to teach him to ignore
                    // alerts entirely. It is reported once per run, below, at Information.
                    skipped++;
                    items.Add(new SessionPushResult(s.Id, s.Title, PushAction.Skipped, null,
                        "external writes are disabled for this host — not pushed"));
                }
                else
                {
                    failed++;
                    items.Add(new SessionPushResult(s.Id, s.Title, PushAction.Failed, null, res.Error));
                }
            }
            else
            {
                // §302 ONE-WAY DECISION (operator 2026-07-24): two-way sync is dropped; the
                // Zoho sessions API is CREATE-ONLY, so a CEH change on an already-pushed
                // session (room, time, title …) can only be applied manually in the
                // Backstage UI. DIFF CEH against the LIVE Zoho session and mail info@ the
                // Zoho GUI field changes — ONCE per distinct diff (hash-deduped across the
                // hourly passes: "I don't want an email at every sync"). Supersedes both
                // the §59 CehToZoho update-delta enqueue and the honest-failure inline PUT.
                var live = liveSessions?.GetValueOrDefault(s.BackstageSessionId!);
                if (live is null)
                {
                    skipped++;
                    items.Add(new SessionPushResult(s.Id, s.Title, PushAction.Skipped, s.BackstageSessionId,
                        "already in Zoho (live state unreadable this pass — no change check)"));
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(s.Room))
                    hallCache ??= ToNameIdCache(await _zoho.GetHallsAsync(token, ct));
                var diffs = BuildLinkedSessionDiffs(s, live, duration, trackCache, hallCache);
                if (diffs.Count == 0)
                {
                    if (s.ZohoChangeNotifiedHash is not null) { s.ZohoChangeNotifiedHash = null; healed++; }
                    skipped++;
                    items.Add(new SessionPushResult(s.Id, s.Title, PushAction.Skipped, s.BackstageSessionId,
                        "already in Zoho — matches CEH (no change)"));
                    continue;
                }

                var hash = DiffHash(diffs);
                if (!string.Equals(hash, s.ZohoChangeNotifiedHash, StringComparison.Ordinal))
                {
                    s.ZohoChangeNotifiedHash = hash;
                    updated++;   // "updated" now counts change-NOTIFIED sessions (no API write exists)
                    // §322m: MULTI-LINE mail entry — one line per field, paste blocks on
                    // their own lines (ZohoChangeNotifier renders \n as <br/>).
                    zohoWrites.Add(
                        $"ACTION NEEDED: session '{s.Title}' (Backstage id {s.BackstageSessionId}) changed in CEH — "
                        + "apply manually in the Backstage UI:\n"
                        + string.Join("\n", diffs.Select(d => "• " + d)));
                    // The GUI result row stays compact: labels only, no paste blocks.
                    items.Add(new SessionPushResult(s.Id, s.Title, PushAction.Updated, s.BackstageSessionId,
                        "change mail sent: " + string.Join("; ", diffs.Select(d => d.Split('\n')[0]))));
                }
                else
                {
                    skipped++;
                    items.Add(new SessionPushResult(s.Id, s.Title, PushAction.Skipped, s.BackstageSessionId,
                        "pending manual Backstage update (operator already mailed)"));
                }
            }
        }

        if (created > 0 || updated > 0 || healed > 0) await _db.SaveChangesAsync(ct);

        // Operator 2026-07-23: ONE batched ops mail per pass listing every successful Zoho
        // write (the operator must publish/delete manually in Backstage). Never throws.
        if (_zohoChanges is not null)
            await _zohoChanges.NotifyAsync("Agenda / sessions", zohoWrites, ct);

        return new Result(true, null, true, null, created, updated, failed, skipped, items, enqueued);
    }

    /// <summary>
    /// Build the {Field, Old, New} diff list for a CehToZoho session UPDATE delta. CEH is the
    /// source of truth here, so NewValue carries the current CEH value the operator's approve
    /// will push; OldValue is left null (the queue UI shows it as the value being pushed). Only
    /// non-blank fields are included so a blank CEH value never appears as a "change".
    /// </summary>
    private static IReadOnlyList<SyncFieldChange> BuildPushChanges(Session s)
    {
        var list = new List<SyncFieldChange>();
        if (!string.IsNullOrWhiteSpace(s.Title))
            list.Add(new SyncFieldChange(SyncDeltaQueueService.FieldTitle, null, s.Title));
        if (s.StartsAt is { } st)
            list.Add(new SyncFieldChange(SyncDeltaQueueService.FieldStartsAt, null,
                st.ToString("o", System.Globalization.CultureInfo.InvariantCulture)));
        if (s.EndsAt is { } en)
            list.Add(new SyncFieldChange(SyncDeltaQueueService.FieldEndsAt, null,
                en.ToString("o", System.Globalization.CultureInfo.InvariantCulture)));
        if (!string.IsNullOrWhiteSpace(s.Track))
            list.Add(new SyncFieldChange(SyncDeltaQueueService.FieldTrack, null, s.Track));
        if (!string.IsNullOrWhiteSpace(s.Abstract))
            list.Add(new SyncFieldChange(SyncDeltaQueueService.FieldAbstract, null, s.Abstract));
        return list;
    }

    /// <summary>
    /// Push the CURRENT CEH values of ONE already-linked session to Zoho (§57 stage-2 UPDATE
    /// on approve, REQUIREMENTS §59). Used by the delta-approval queue: a stage-2 update of a
    /// linked record is ENQUEUED (not pushed inline) and only pushed here when the operator
    /// approves it. Resolves the track name → Backstage track id exactly like
    /// <see cref="RunAsync"/>. Returns (ok, message). Does NOT re-check the direction gate —
    /// the enqueue side already did — and NEVER creates (a session with no Backstage id is a
    /// caller error here, reported as a failure).
    /// </summary>
    public async Task<(bool Ok, string Message)> UpdateLinkedSessionAsync(
        int eventId, int sessionId, CancellationToken ct = default)
    {
        var session = await _db.Sessions
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.EventId == eventId, ct);
        if (session is null)
            return (false, "The session no longer exists in this edition.");
        // §299 4.5/b8: a TEST session must NEVER reach the public agenda, any environment.
        if (session.UsedForTesting)
            return (false, "Test session (UsedForTesting) — never pushed to the public agenda.");
        if (string.IsNullOrWhiteSpace(session.BackstageSessionId))
            return (false, "The session is not linked to a Zoho session (nothing to update).");
        if (string.IsNullOrWhiteSpace(session.Title))
            return (false, "The session has no title — not pushed.");

        var token = await GetTokenAsync(ct);
        if (token is null) return (false, "No Zoho access token (token refresh failed).");

        var ev = await _db.Events.AsNoTracking()
            .Where(e => e.Id == eventId)
            .Select(e => new { e.StartDate, e.PreDayDate })
            .FirstOrDefaultAsync(ct);
        var firstDay = ev?.PreDayDate ?? ev?.StartDate;

        string? trackId = null;
        if (!string.IsNullOrWhiteSpace(session.Track))
            trackId = ResolveTrackId(session.Track, await _zoho.GetTracksAsync(token, ct));

        var ok = await _zoho.UpdateSessionAsync(
            token, session.BackstageSessionId!, session.Title, session.Abstract,
            session.StartsAt, DurationMinutes(session), trackId, _zohoOptions.PushSessionType, ct);
        if (!ok) return (false, "Zoho session update failed (see logs).");

        session.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        // Operator 2026-07-23: a successful Zoho write must notify the ops mailbox
        // (publish/delete is manual in Backstage). This apply-on-approve push is its
        // own one-item "pass".
        if (_zohoChanges is not null)
            await _zohoChanges.NotifyAsync("Agenda / sessions",
                new[] { $"Updated session '{session.Title}' (Backstage id {session.BackstageSessionId})" }, ct);

        return (true, "Pushed the session's current values to Zoho.");
    }

    /// <summary>
    /// §299 stage-2 PILOT / BULK-CREATE path (operator 2026-07-23): CREATE exactly ONE
    /// not-yet-linked CEH session in the Zoho Backstage agenda — an explicit,
    /// operator-authorized push, deliberately WITHOUT the §57 direction gate (which would
    /// push the whole edition). Refuses test sessions (§299 4.5), service sessions, untitled
    /// and ALREADY-LINKED sessions (never double-creates). Stores the returned Backstage id
    /// on success so the session is linked 1:1 exactly as a full stage-2 pass would leave it.
    ///
    /// <para>COMPLETE create (operator flagged the incomplete first pilot): the create carries
    /// the resolved TRACK (REQUIRED by the live API — exact Sessionize-name match, else the
    /// track is CREATED with the Sessionize name per the operator's 2026-07-23
    /// create-if-missing rule; only a BLANK CEH track fails), the VENUE (the session's room
    /// resolved against GET /halls
    /// by exact case-insensitive name; a hall missing in Backstage but known to the room
    /// registry WITH a capacity is created on demand via POST /halls {name, capacity} —
    /// expo/unknown rooms send no venue, warn-only) and the SPEAKERS (the linked
    /// participants' distinct non-blank e-mails).</para>
    ///
    /// <para>Returns (Ok, Message, Changes) where Changes are the human-readable Zoho-write
    /// lines (created hall and/or session). When <paramref name="suppressNotify"/> is false
    /// (default) the per-call ops change mail is sent as before; a BULK caller passes true
    /// and sends ONE batched <see cref="Email.ZohoChangeNotifier"/> mail itself from the
    /// returned lines. A hall created before a FAILED session create is still a real Zoho
    /// write — it is returned in Changes (and notified when not suppressed).</para>
    /// </summary>
    public async Task<(bool Ok, string Message, IReadOnlyList<string> Changes)> CreateOneAsync(
        int eventId, int sessionId, bool suppressNotify = false, CancellationToken ct = default)
    {
        var changes = new List<string>();
        var session = await _db.Sessions
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.EventId == eventId, ct);
        if (session is null)
            return (false, "The session does not exist in this edition.", changes);
        if (session.UsedForTesting)
            return (false, "Test session (UsedForTesting) — never pushed to the public agenda.", changes);
        if (session.IsServiceSession)
            return (false, "Service session (break/lunch) — not pushed.", changes);
        if (!string.IsNullOrWhiteSpace(session.BackstageSessionId))
            return (false, $"Already linked to Zoho session {session.BackstageSessionId} — nothing created.", changes);
        if (string.IsNullOrWhiteSpace(session.Title))
            return (false, "The session has no title — not pushed.", changes);

        // §299.6/b5: warn-only unknown-room check (never blocks the create).
        WarnOnUnknownRoom(session);

        var token = await GetTokenAsync(ct);
        if (token is null) return (false, "No Zoho access token (token refresh failed).", changes);

        var ev = await _db.Events.AsNoTracking()
            .Where(e => e.Id == eventId)
            .Select(e => new { e.StartDate, e.PreDayDate })
            .FirstOrDefaultAsync(ct);
        var firstDay = ev?.PreDayDate ?? ev?.StartDate;

        // TRACK — the live create REQUIRES one. Operator 2026-07-23: reuse an exact
        // (case-insensitive) Backstage match on the TARGET name — the Sessionize label, or
        // its TrackNameMap entry (the two AI tracks carry shorter names in Zoho) — else
        // CREATE the track under the target name (the operator prunes unwanted tracks in
        // the UI; every create is reported via the change mail). A BLANK track still fails.
        if (string.IsNullOrWhiteSpace(session.Track))
            return (false, "The session has no track — the Zoho create requires one.", changes);
        var trackCache = ToNameIdCache(
            (await _zoho.GetTracksAsync(token, ct)).Select(t => (t.Id, t.Name)));
        var (trackId, trackError) =
            await ResolveOrCreateTrackAsync(token, session.Track, trackCache, changes, ct);
        if (trackId is null)
            return (false, trackError!, changes);

        // VENUE — resolve the room name against the Backstage halls; create the hall on
        // demand when the room registry knows it WITH a capacity (capacity is REQUIRED on
        // POST /halls). Expo/unknown rooms send no venue (warn-only, per §299.6/b5).
        string? venueId = null;
        if (!string.IsNullOrWhiteSpace(session.Room))
        {
            var hallCache = ToNameIdCache(await _zoho.GetHallsAsync(token, ct));
            venueId = await ResolveOrCreateVenueAsync(token, session, hallCache, changes, ct);
        }

        // SPEAKERS — the linked participants' distinct, non-blank e-mails, FILTERED to the
        // attachable (approved + ring-eligible) set (INCIDENT FIX 2026-07-24: an attached
        // e-mail makes Zoho create + INVITE the speaker — the ring scope must hold here).
        var attachableOne = await AttachableSpeakerIdsAsync(eventId, ct);
        var speakerEmails = NormalizeSpeakerEmails((await _db.SessionSpeakers.AsNoTracking()
                .Where(ss => ss.SessionId == session.Id)
                .Select(ss => new { ss.ParticipantId, ss.Participant.Email })
                .ToListAsync(ct))
            .Where(x => attachableOne.Contains(x.ParticipantId))
            .Select(x => x.Email));

        var res = await _zoho.CreateSessionAsync(
            token, DayIndex(firstDay, session.StartsAt), session.Title, session.Abstract,
            session.StartsAt, DurationMinutes(session), trackId, _zohoOptions.PushSessionType,
            venueId, speakerEmails.Count > 0 ? speakerEmails : null, ct);
        if (!res.Ok)
        {
            // A hall created above WAS written to Zoho — the notify rule still applies.
            if (!suppressNotify && _zohoChanges is not null && changes.Count > 0)
                await _zohoChanges.NotifyAsync("Agenda / sessions", changes, ct);
            return (false, $"Zoho session create failed: {res.Error}", changes);
        }

        session.BackstageSessionId = res.Id;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        changes.Add($"Created session '{session.Title}' (Backstage id {res.Id})");

        // Operator 2026-07-23: a successful Zoho write must notify the ops mailbox
        // (publish/delete is manual in Backstage) — unless a BULK caller suppressed the
        // per-call mail to send ONE batched mail from the returned change lines.
        if (!suppressNotify && _zohoChanges is not null)
            await _zohoChanges.NotifyAsync("Agenda / sessions", changes, ct);

        return (true, $"Created in the Zoho Backstage agenda (Backstage id {res.Id}).", changes);
    }

    // --- shared §299 stage-2 COMPLETE-create resolution (CreateOneAsync + RunAsync) ------

    /// <summary>A mutable, case-insensitive name→id cache over Backstage rows (tracks/halls).
    /// Mutable so an id created mid-pass joins the cache and is reused, never re-created.</summary>
    private static Dictionary<string, string> ToNameIdCache(IEnumerable<(string Id, string Name)> rows)
    {
        var cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, name) in rows)
        {
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrWhiteSpace(name))
                cache.TryAdd(name.Trim(), id);
        }
        return cache;
    }

    /// <summary>
    /// TRACK resolution for a COMPLETE create: exact case-insensitive match on the TARGET
    /// name (<see cref="TargetTrackName"/> — the Sessionize label, or its TrackNameMap
    /// entry) in the cache, else the track is CREATED under the target name (operator
    /// 2026-07-23 create-if-missing rule; NO fuzzy matching). A created track joins the
    /// cache and the <paramref name="changes"/> ops-mail lines. Returns the track id, or
    /// the error the caller reports when the create failed (that session must then fail
    /// WITHOUT a session POST). Callers reject a BLANK CEH track before calling.
    /// </summary>
    private async Task<(string? TrackId, string? Error)> ResolveOrCreateTrackAsync(
        string token, string cehTrack, Dictionary<string, string> trackIdsByName,
        List<string> changes, CancellationToken ct)
    {
        var targetName = TargetTrackName(cehTrack);
        if (trackIdsByName.TryGetValue(targetName, out var existing)) return (existing, null);

        var newTrack = await _zoho.CreateTrackAsync(token, targetName, ct);
        if (!newTrack.Ok)
            return (null, $"Zoho track create for '{targetName}' failed: {newTrack.Error}");
        trackIdsByName[targetName] = newTrack.Id!;
        changes.Add($"Created track '{targetName}' (Backstage id {newTrack.Id})");
        return (newTrack.Id, null);
    }

    /// <summary>
    /// VENUE resolution for a COMPLETE create: the session's room name against the halls
    /// cache (exact case-insensitive); a hall missing in Backstage but known to the room
    /// registry WITH a capacity is created on demand (capacity is REQUIRED on POST /halls)
    /// and joins the cache + the <paramref name="changes"/> ops-mail lines. Expo/unknown
    /// rooms — and a failed hall create — return null so the session is created WITHOUT a
    /// venue (warn-only, per §299.6/b5; the venue never blocks a push).
    /// </summary>
    private async Task<string?> ResolveOrCreateVenueAsync(
        string token, Session session, Dictionary<string, string> hallIdsByName,
        List<string> changes, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(session.Room)) return null;
        var room = session.Room.Trim();
        if (hallIdsByName.TryGetValue(room, out var existing)) return existing;
        if (_rooms?.Find(room) is not { Capacity: int capacity }) return null;

        var hall = await _zoho.CreateHallAsync(token, room, capacity, ct);
        if (hall.Ok)
        {
            hallIdsByName[room] = hall.Id!;
            // Operator 2026-07-23 notify rule: a created hall is a Zoho write too.
            changes.Add($"Created hall '{room}' (Backstage id {hall.Id})");
            return hall.Id;
        }
        if (_logger is not null)
        {
            Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(
                _logger,
                "Zoho hall create for room '{Room}' failed ({Error}) — session "
                + "'{Title}' is created WITHOUT a venue.",
                room, hall.Error, session.Title);
        }
        return null;
    }

    /// <summary>
    /// §302: the "Zoho GUI field: 'live' → 'CEH'" diff lines for a LINKED session — the
    /// change mail speaks in the field names the operator SEES in the Backstage UI
    /// (Title / Session Time / Duration / Track / Hall), never CEH or API names. Only
    /// fields CEH actually carries are compared; track/hall ids are resolved back to
    /// their display names via the per-pass caches.
    /// </summary>
    private List<string> BuildLinkedSessionDiffs(
        Session s, ZohoClient.LiveSession live, int? duration,
        Dictionary<string, string> trackCache, Dictionary<string, string>? hallCache)
    {
        static string Show(string? v) => string.IsNullOrWhiteSpace(v) ? "(empty)" : v!;
        var diffs = new List<string>();

        if (!string.IsNullOrWhiteSpace(s.Title)
            && !string.Equals(s.Title.Trim(), live.Title?.Trim(), StringComparison.Ordinal))
            diffs.Add($"{ZohoFieldMap.Session.Title.GuiLabel}: '{Show(live.Title)}' → '{s.Title.Trim()}'");

        // §305: compare on the absolute instant; DISPLAY in Danish time (the operator
        // thinks in Europe/Copenhagen, and the Zoho GUI shows the same).
        if (s.StartsAt is { } start && live.StartTime is { } liveStart
            && Math.Abs((start.ToUniversalTime() - liveStart.ToUniversalTime()).TotalMinutes) >= 1)
            diffs.Add($"{ZohoFieldMap.Session.StartTime.GuiLabel}: '{EventTimezone.ToEventLocalString(liveStart)}' → "
                    + $"'{EventTimezone.ToEventLocalString(start)}'");

        if (duration is { } d && live.DurationMinutes is { } ld && d != ld)
            diffs.Add($"{ZohoFieldMap.Session.Duration.GuiLabel}: '{ld}' → '{d}'");

        if (!string.IsNullOrWhiteSpace(s.Track))
        {
            var targetName = TargetTrackName(s.Track);
            if (trackCache.TryGetValue(targetName, out var targetId)
                && !string.Equals(targetId, live.TrackId, StringComparison.Ordinal))
            {
                var liveName = trackCache.FirstOrDefault(
                    kv => string.Equals(kv.Value, live.TrackId, StringComparison.Ordinal)).Key;
                diffs.Add($"{ZohoFieldMap.Session.Track.GuiLabel}: '{Show(liveName ?? live.TrackId)}' → '{targetName}'");
            }
        }

        if (!string.IsNullOrWhiteSpace(s.Room) && hallCache is not null
            && hallCache.TryGetValue(s.Room.Trim(), out var targetHallId)
            && !string.Equals(targetHallId, live.VenueId, StringComparison.Ordinal))
        {
            var liveHall = hallCache.FirstOrDefault(
                kv => string.Equals(kv.Value, live.VenueId, StringComparison.Ordinal)).Key;
            diffs.Add($"{ZohoFieldMap.Session.Hall.GuiLabel}: '{Show(liveHall ?? live.VenueId)}' → '{s.Room.Trim()}'");
        }

        // §302c GuiOnly fields — the create REFUSES them and no update exists, so the
        // ACTION mail is their ONLY channel (operator: "Bug: Mappings missing for
        // Sessions"). Description: CEH abstract vs the live description — the FULL text
        // (§322l, operator "session description is still missing in zoho"): the mail is
        // the operator's copy-PASTE source for the Backstage GUI, so a 200-char clip made
        // it useless for anything longer.
        // Tags: the DERIVED expected set (Session Level value-mapped + the mandatory
        // "Session Language: English" + Sessionize labels) minus what Zoho already has.
        if (!string.IsNullOrWhiteSpace(s.Abstract))
        {
            var liveDesc = StripHtml(live.Description);
            if (string.IsNullOrWhiteSpace(liveDesc))
                // §322m: label line, then the FULL text as its own paste block.
                diffs.Add($"{ZohoFieldMap.Session.Description.GuiLabel} is empty — paste the full text below into the Session Description box:\n{s.Abstract.Trim()}");
            // A NON-blank live description is left alone: Zoho's rich-text editor
            // reformats the text, so a char-compare would flag every session forever.
        }
        // 🔒 §594 — TAGS ARE NOT DIFFED. THE AGENDA API DOES NOT RETURN THEM. DO NOT RE-ADD THIS.
        //
        // This block used to compare the derived expected tag set against `live.Tags` and mail
        // "Tags missing — paste the line below into the Tags box". Operator 2026-07-28 reported
        // receiving it for a session whose tags he had ALREADY entered, and he was right.
        //
        // VERIFIED LIVE against the session in his screenshot (14880000004323055): the
        // GET .../sessions?day=N record has NO `tags` property at all. Its keys are
        //   id, agenda, language, title, track, session_type, duration, start_time, featured,
        //   hidden, venue_to_be_announced, speaker_to_be_announced, venue, description,
        //   created_by, created_time, last_modified_by, last_modified_time, speakers.
        // So `live.Tags` was ALWAYS empty, every expected tag was ALWAYS "missing", and the mail
        // could NEVER be satisfied — pasting the tags in did not and could not clear it.
        //
        // ❗ NOTE HIS HYPOTHESIS WAS NOT THE CAUSE, and the distinction matters: he asked whether
        // tag ORDER broke a string comparison. It did not — the comparison was already
        // order-insensitive (per-item `Contains`, not a joined-string equality). Re-sorting would
        // have changed nothing. The field is simply unreadable.
        //
        // This is precisely the §582 limitation rule he set: `Capability.GuiOnly` fields are
        // refused on create AND not echoed on read, so DIFFING one manufactures a PERMANENT FALSE
        // GAP that mails him forever and destroys trust in the mechanism. Tags are reported ONCE,
        // at CREATE time, by DescribeCreateGaps (§574) — which is the only moment the information
        // is actionable anyway, since the sessions API is create-only.
        //
        // (Secondary, and also real: the expected line renders "Session Level: Expert (400)" while
        // Zoho stores "Session Level Expert (400)" — it drops the colon. So even if tags WERE
        // readable, those two derived tags would mismatch. Recorded in §594; do not "fix" it by
        // loosening the comparison, because there is no comparison left to loosen.)

        return diffs;
    }

    // (§322l: the 200-char Clip() helper is gone — the ACTION mail now carries the FULL
    // description so it can be pasted straight into the Backstage GUI.)

    /// <summary>Zoho's rich-text description arrives as HTML — reduce to text so an
    /// "empty" description ("&lt;p&gt;&lt;/p&gt;") is recognised as blank.</summary>
    private static string? StripHtml(string? html) =>
        string.IsNullOrWhiteSpace(html)
            ? html
            : System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ").Trim();

    /// <summary>§302: the stable dedupe key of one diff set (same diff ⇒ same hash ⇒ no re-mail).</summary>
    private static string DiffHash(IReadOnlyList<string> diffs) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join("\n", diffs))))[..32];

    /// <summary>
    /// The distinct, trimmed, non-blank speaker e-mails a session create attaches —
    /// LOWERCASED (live-verified 2026-07-24): Zoho stores speaker e-mails lowercased and
    /// matches a session's attach list CASE-SENSITIVELY, silently dropping a mixed-case
    /// e-mail ("Hub-Test-…@" was not linked while its lowercase record existed).
    /// </summary>
    private static List<string> NormalizeSpeakerEmails(IEnumerable<string?> emails) =>
        emails.Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e!.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private async Task<string?> GetTokenAsync(CancellationToken ct) =>
        _tokenOverride is not null ? await _tokenOverride(ct) : await _zoho.GetAccessTokenAsync(ct);

    /// <summary>
    /// The session's duration in whole minutes: end−start when both are scheduled, else
    /// the source-of-truth <see cref="Session.LengthMinutes"/> (§299.8/b7), else the
    /// LEGACY <see cref="Session.Length"/> bucket (FullDay → 480 min) for old rows that
    /// never got minutes. Null when nothing is known.
    /// </summary>
    public static int? DurationMinutes(Session s)
    {
        if (s.StartsAt is { } start && s.EndsAt is { } end && end > start)
            return (int)Math.Round((end - start).TotalMinutes);
        if (s.LengthMinutes is int minutes && minutes > 0)
            return minutes;
        return s.Length switch
        {
            SessionLength.FullDay => 480,
            SessionLength.TwentyMin => 20,
            SessionLength.FiftyMin => 50,
            SessionLength.SixtyMin => 60,
            _ => null,
        };
    }

    /// <summary>
    /// The 1-based agenda day index for a session: 1 + (session start date − first agenda
    /// day). Falls back to day 1 when either the anchor or the session start is unknown, or
    /// when the computed offset is negative (a session dated before the anchor).
    /// </summary>
    public static int DayIndex(DateOnly? firstAgendaDay, DateTimeOffset? sessionStart)
    {
        if (firstAgendaDay is not { } anchor || sessionStart is not { } start) return 1;
        var sessionDay = DateOnly.FromDateTime(start.UtcDateTime);
        var offset = sessionDay.DayNumber - anchor.DayNumber;
        return offset >= 0 ? offset + 1 : 1;
    }
}
