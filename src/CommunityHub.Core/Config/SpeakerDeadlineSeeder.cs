using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Entitlements;
using CommunityHub.Core.Tasks.Definitions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CommunityHub.Core.Config;

/// <summary>One speaker deadline from speaker-deadlines.&lt;edition&gt;.json.</summary>
public sealed class SpeakerDeadlineDefinition
{
    /// <summary>
    /// §708 — the STABLE key a migrated <c>SpeakerTaskDefinition</c> names via
    /// <see cref="Tasks.Definitions.TaskDue.FromSpeakerConfig"/> to read this entry's date.
    /// </summary>
    /// <remarks>
    /// 🔒 The DATE stays in config (per edition, evergreen) while the definition, audience and body
    /// move into code — so a migrated entry keeps its row here purely as this file's date table.
    /// Null/blank on an UNMIGRATED entry, which still seeds through the JSON path below.
    /// </remarks>
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>Absolute date (yyyy-MM-dd) this deadline is due. Not event-relative.</summary>
    [JsonPropertyName("dueDate")]
    public DateOnly DueDate { get; set; }

    /// <summary>
    /// If true, this deadline applies ONLY to Master Class speakers
    /// (plain session speakers do not get it). Default false = all speakers.
    /// </summary>
    [JsonPropertyName("masterclassOnly")]
    public bool MasterclassOnly { get; set; }

    /// <summary>
    /// If true, this deadline applies ONLY to speakers whose Speaker Details
    /// <see cref="Domain.SpeakerProfile.Country"/> is NOT Denmark (operator
    /// 2026-06-27, §143 — the travel-reimbursement task). Danish speakers don't
    /// travel, so they never see it. A speaker with no country set yet is treated
    /// as non-Denmark (they get the task). Default false = all speakers.
    /// </summary>
    [JsonPropertyName("nonDenmarkOnly")]
    public bool NonDenmarkOnly { get; set; }
}

/// <summary>
/// §326b (operator 2026-07-25): the Get-Started completion deadline for speakers.
/// On <see cref="ReminderDate"/> the daily reminder run sends ONE reminder (once
/// ever per speaker) to every active speaker whose Get-Started wizard is not yet
/// 100% complete; the send window runs through <see cref="Deadline"/> so a
/// ring-dropped or missed-run send self-heals before the deadline passes. Absent
/// block = the reminder is inert. Consumed by
/// <c>CommunityHub.Core.Reminders.GetStartedDeadlineReminderBuilder</c>.
/// </summary>
public sealed class GetStartedDeadlineDefinition
{
    /// <summary>The day the one-shot reminder fires (yyyy-MM-dd, event-local date).</summary>
    [JsonPropertyName("reminderDate")]
    public DateOnly ReminderDate { get; set; }

    /// <summary>The Get-Started completion deadline the mail names (yyyy-MM-dd).</summary>
    [JsonPropertyName("deadline")]
    public DateOnly Deadline { get; set; }
}

/// <summary>The speaker-deadlines config file.</summary>
public sealed class SpeakerDeadlineConfig
{
    [JsonPropertyName("deadlines")]
    public List<SpeakerDeadlineDefinition> Deadlines { get; set; } = new();

    /// <summary>§326b: optional Get-Started completion deadline block (null = inert).</summary>
    [JsonPropertyName("getStartedDeadline")]
    public GetStartedDeadlineDefinition? GetStartedDeadline { get; set; }

    /// <summary>
    /// §708 step 1 — this file as a DATE TABLE: every keyed entry's absolute due date, by key.
    /// </summary>
    /// <remarks>
    /// 🔒 <b>A duplicate key keeps the FIRST entry rather than throwing.</b> This runs on speaker
    /// page load and inside the Functions host (§326bb), so a config typo must degrade to one wrong
    /// date, never to an exception on every speaker's task page — the §682 rule. The duplicate is a
    /// build failure in <c>SpeakerTaskDefinitionTests</c>, which is where it is cheap to see.
    /// </remarks>
    public IReadOnlyDictionary<string, DateOnly> DueDatesByKey()
    {
        var map = new Dictionary<string, DateOnly>(StringComparer.Ordinal);
        foreach (var dl in Deadlines)
        {
            if (string.IsNullOrWhiteSpace(dl.Key)) continue;
            map.TryAdd(dl.Key.Trim(), dl.DueDate);
        }
        return map;
    }
}

/// <summary>Where the speaker-deadlines config file is.</summary>
public sealed class SpeakerDeadlineOptions
{
    public const string SectionName = "SpeakerDeadlines";

    public string ConfigPath { get; set; } =
        "config/speaker-deadlines.eldk27.json";
}

/// <summary>
/// Seeds speaker-deadline tasks (CONTEXT.md - speaker deadlines). For each
/// active speaker / Master Class speaker, creates one ParticipantTask per
/// configured deadline, dated from the deadline's absolute dueDate. A deadline
/// flagged masterclassOnly is seeded for Master Class speakers only. Idempotent:
/// each task has a SourceKey "speakerdl:{participantId}:{slug}", so re-running
/// never duplicates. New speakers (e.g. a later Sessionize import) get their
/// deadline tasks the next time this runs.
///
/// The reminder job (Stage 6) then picks these dated tasks up automatically -
/// speaker-deadline reminders need no separate path.
/// </summary>
public sealed class SpeakerDeadlineSeeder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly CommunityHubDbContext _db;
    private readonly SpeakerDeadlineOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<SpeakerDeadlineSeeder> _log;

    public SpeakerDeadlineSeeder(
        CommunityHubDbContext db,
        SpeakerDeadlineOptions options,
        TimeProvider clock,
        ILogger<SpeakerDeadlineSeeder>? log = null)
    {
        _db = db;
        _options = options;
        _clock = clock;
        // §682 — optional so the existing test construction sites keep working; DI always
        // supplies a real one. Only used to report a config body that will not fit.
        _log = log ?? NullLogger<SpeakerDeadlineSeeder>.Instance;
    }

    /// <summary>Create any missing speaker-deadline tasks. Returns the count created.</summary>
    public async Task<int> SeedAsync(int eventId, CancellationToken ct = default)
    {
        // §326bb: the same relative-path trap as the §326b reminder — this seeder also runs
        // in the FUNCTIONS host (ReminderJob), whose working directory is not the content
        // root, so the bare relative path missed and SeedAsync was a silent no-op there.
        // Masked in practice only because the WEB app seeds the same tasks on page load.
        var configPath = ConfigPaths.Resolve(_options.ConfigPath);
        if (!File.Exists(configPath))
        {
            return 0; // no config => nothing to seed
        }

        var config = JsonSerializer.Deserialize<SpeakerDeadlineConfig>(
            File.ReadAllText(configPath), JsonOptions);
        if (config is null || config.Deadlines.Count == 0)
        {
            return 0;
        }

        var eventExists = await _db.Events.AnyAsync(e => e.Id == eventId, ct);
        if (!eventExists)
        {
            return 0;
        }

        // ACTIVE speakers only. NOTE (§299 6.1): an UNCATEGORIZED (null-Category)
        // speaker cannot be ACTIVATED any more (the pre-selection queue's hard
        // gate), so the IsActive scope below already excludes new uncategorized
        // speakers from seeding. The one exception is a LEGACY active speaker
        // whose old SpeakerFunding.Organizer value migrated to a null Category
        // (frozen pending ❓OPEN-22): they keep the pre-gate behaviour — no
        // entitlement-gated logistics deadlines (a null category grants nothing),
        // but still the non-gated presentation deadlines, exactly as before.
        var speakers = await _db.Participants
            .Where(p => p.EventId == eventId
                        && p.IsActive
                        && p.Role == ParticipantRole.Speaker)
            .Select(p => new
            {
                p.Id,
                // §299 6.2: Sponsor-category speakers get NO presentation deadlines.
                Category = _db.SpeakerProfiles
                    .Where(s => s.EventId == eventId && s.ParticipantId == p.Id)
                    .Select(s => s.Category)
                    .FirstOrDefault(),
                // Speaker Details country (2-letter code, e.g. "DK"); null when no
                // profile / not yet filled in. Gates the §143 non-Denmark travel task.
                Country = _db.SpeakerProfiles
                    .Where(s => s.EventId == eventId && s.ParticipantId == p.Id)
                    .Select(s => s.Country)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        // §299 C5: whether a speaker presents on the pre-day now DERIVES from
        // their linked sessions (the SpeakingPreDay flag is retired) — drives
        // the masterclassOnly deadline gate below. Loaded once for the edition.
        var daysBySpeaker = await SpeakerDayScope.DaysBySpeakerAsync(_db, eventId, ct);

        var now = _clock.GetUtcNow();
        var created = 0;
        var removed = 0;

        // ── §708 — THE MIGRATED SPEAKER DEFINITIONS ────────────────────────────────────────
        //
        // The dates stay HERE (per edition, evergreen); the definition, audience, completion kind
        // and body live in code. TaskDue.FromSpeakerConfig names the entry's stable `key`.
        var dueDatesByKey = config.DueDatesByKey();

        // Every title the registry now owns. A JSON entry whose title slugs to one of these is NOT
        // seeded by the legacy loop below.
        //
        // 🔒 <b>This must be built from ALL speaker definitions, not from the ones a given speaker's
        // audience matched.</b> Otherwise a speaker EXCLUDED from a definition (§456: an exhibitor
        // speaker has no preview-upload task) would fall through to the JSON entry and be handed the
        // very task the exclusion exists to withhold — silently, and with a reminder behind it.
        // Exclusion has to mean NO task, not "the old task instead".
        var migratedSlugs = TaskDefinitionRegistry.Shipped.All
            .Where(d => d.Audience.Roles.Contains(ParticipantRole.Speaker))
            .Select(d => SponsorTaskKeys.Slug(d.Title))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var speaker in speakers)
        {
            var days = daysBySpeaker.TryGetValue(speaker.Id, out var d) ? d : SpeakerDays.None;

            // P12: this speaker's EFFECTIVE entitlement set, computed once, so the
            // per-deadline logistics gate (below) can drop tasks they can't act on.
            // NOTE: Guest speakers lack TravelReimbursement (§299 6.2), so the travel
            // deadline is excluded for them here automatically.
            var entitled = await EffectiveItemsAsync(eventId, speaker.Id, days, ct);

            // The speakerdl SourceKeys that SHOULD exist for this speaker after this
            // run — every deadline that still applies once the masterclass + P12
            // entitlement gates are honoured. Drives both the add (missing keys) and
            // the orphan-prune (no-longer-desired keys) below.
            var desiredKeys = new HashSet<string>(StringComparer.Ordinal);

            // ── §708 — SEED THE REGISTRY DEFINITIONS FIRST ─────────────────────────────────
            //
            // Same table, same `speakerdl:{id}:{slug}` key shape, same due-date column as the JSON
            // loop below — so every downstream consumer (the deadlines surface, the overdue badges,
            // TaskReminderBuilder, the SentReminders dedup ledger) sees no change at all. That is
            // §684.21's regression gate: this changes where a task's DEFINITION and BODY come from
            // and must change nothing about who gets chased, when, or how.
            //
            // 🔒 Because the titles are carried across VERBATIM (§686.10), the key a registry-seeded
            // row gets is byte-identical to the one the JSON-seeded row already has. The existing
            // row is UPDATED IN PLACE: completion state survives, the reminder ledger still matches,
            // and the orphan prune below sees the key in its desired set and leaves it alone.
            var facts = TaskAudienceFacts.For(
                ParticipantRole.Speaker, SpeakerPredicates(speaker.Category, speaker.Country, days, entitled));

            foreach (var definition in TaskDefinitionRegistry.Shipped.For(facts))
            {
                var defKey = SponsorTaskKeys.ForSpeaker(speaker.Id, definition.Title);
                desiredKeys.Add(defKey);

                DateOnly? defDue = definition.Due switch
                {
                    TaskDue.FromSpeakerConfig fromSpeaker =>
                        dueDatesByKey.TryGetValue(fromSpeaker.DeadlineKey, out var dt) ? dt : null,
                    TaskDue.Fixed fixedDate => fixedDate.Date,
                    TaskDue.EventMinus eventMinus => null, // no speaker definition uses this today
                    _ => null,
                };

                // 🔒 An UNRESOLVED date is loud. A null DueDate drops the task out of the due-day
                // chase entirely and greys out its badge — a silent loss of the deadline, which is
                // the whole reason §708 forbade TaskDue.Fixed and keyed the lookup. The task is
                // still seeded (a dateless task beats no task), but nobody has to guess why.
                if (defDue is null && definition.Due is TaskDue.FromSpeakerConfig missing)
                {
                    _log.LogError(
                        "Speaker definition '{Key}' names deadline key '{DeadlineKey}', which is not "
                        + "in speaker-deadlines config. The task is seeded with NO due date, so it "
                        + "will never be chased. Add the key to the edition's deadlines array.",
                        definition.Key, missing.DeadlineKey);
                }

                var existingDef = await _db.Tasks.FirstOrDefaultAsync(
                    t => t.EventId == eventId && t.SourceKey == defKey, ct);

                if (existingDef is null)
                {
                    _db.Tasks.Add(new ParticipantTask
                    {
                        EventId = eventId,
                        AssignedParticipantId = speaker.Id,
                        Title = definition.Title,
                        // §684.14 — rendered prose lives NOWHERE. Null, not empty: an empty string
                        // would read as "this task has no body" to anything still inspecting the
                        // column, whereas null is unambiguously "ask the registry".
                        Description = null,
                        DueDate = defDue,
                        State = TaskState.Open,
                        SourceKey = defKey,
                        IsMandatory = definition.IsMandatory,
                        FormStepKey = (definition.Completion as TaskCompletion.Form)?.StepKey,
                        CreatedAt = now,
                    });
                    created++;
                }
                else
                {
                    existingDef.DueDate = defDue;
                    existingDef.IsMandatory = definition.IsMandatory;
                    existingDef.FormStepKey = (definition.Completion as TaskCompletion.Form)?.StepKey;
                    // 🔒 CLEAR the stored prose on a row that was previously JSON-seeded. Leaving it
                    // would be the two-sources-of-truth §684.16 forbids: the page would render the
                    // new body while anything still reading the column served the old one.
                    existingDef.Description = null;
                }
            }

            foreach (var dl in config.Deadlines)
            {
                // §708 — a deadline the REGISTRY now owns is not seeded from JSON. Skipped by title
                // slug, which is the same value the SourceKey is derived from, so the two paths can
                // never both claim one key and overwrite each other's row.
                if (migratedSlugs.Contains(Slug(dl.Title)))
                {
                    continue;
                }

                // A masterclass-only deadline is skipped unless this speaker
                // PRESENTS on the pre-day — DERIVED from their linked sessions
                // (§299 C5; the stored SpeakingPreDay flag is retired).
                if (dl.MasterclassOnly && !days.PresentsPreDay)
                {
                    continue;
                }

                // §143 country gate: a non-Denmark-only deadline (the travel
                // reimbursement task) is skipped for Danish speakers. A speaker with
                // no country set yet is treated as non-Denmark, so they get it.
                if (dl.NonDenmarkOnly && IsDenmark(speaker.Country))
                {
                    continue;
                }

                var slug = Slug(dl.Title);

                // §299 6.2: a SPONSOR-category speaker gets NO presentation deadlines by
                // default. Their logistics deadlines are still governed by the P12
                // entitlement gate below (dinner + lunches stay); Community + Guest keep
                // everything.
                //
                // §458 (operator 2026-07-27): the §314 Help-Promote EXEMPTION IS REMOVED —
                // *"sponsor speaker category should not get the task 'Help promote'"*. §314
                // had exempted it on the reasoning that every speaker should promote their
                // session; the operator's decision is that a sponsor speaker's promotion is
                // the sponsor's own business, and their menu entry is hidden too (§457), so
                // leaving the TASK would have meant e-mail reminders for something with no
                // page behind it.
                //
                // §456 (same turn): the ONE deliverable an exhibitor-speaker DOES own is the
                // FINAL presentation — *"only task relevant for a sponsor (exhibitor) speaker
                // is the task for upload final presentation"*. So final is allowed through
                // while preview stays excluded.
                if (speaker.Category == SpeakerCategory.Sponsor
                    && !IsLogisticsSlug(slug)
                    && !IsSponsorSpeakerAllowedSlug(slug))
                {
                    continue;
                }

                // P12 ENTITLEMENT GATE: a logistics deadline (hotel/dinner/swag/lunch)
                // is seeded only when the speaker is entitled to the underlying item —
                // a sponsor-self-funded / organizer-funded speaker should not get a
                // hotel/travel/swag task they cannot act on. Non-logistics deadlines
                // (e.g. presentation uploads) are never entitlement-gated.
                if (!DeadlineAllowedByEntitlement(slug, entitled))
                {
                    continue;
                }

                var sourceKey = $"speakerdl:{speaker.Id}:{slug}";
                desiredKeys.Add(sourceKey);

                var exists = await _db.Tasks.AnyAsync(
                    t => t.EventId == eventId && t.SourceKey == sourceKey, ct);
                if (exists)
                {
                    continue; // idempotent
                }

                // §682 — same guard as SponsorOrderPullService: a config body too long for the
                // column must fail ONLY itself. This seeder runs on page load AND in the
                // Functions host, so an oversized deadline body here would otherwise throw on
                // every speaker's home page as well as inside the reminder job.
                // sourceKey is already in desiredKeys (above), so the orphan prune below will
                // not delete an existing good row just because the new config text is too long.
                if (!TaskFieldGuard.Fits(dl.Title, dl.Description, out var tooLongReason))
                {
                    _log.LogError(
                        "SpeakerDeadlineSeeder: deadline '{Slug}' was SKIPPED for speaker {SpeakerId} — {Reason}. "
                        + "Other deadlines were unaffected. Shorten it in speaker-deadlines.<edition>.json; "
                        + "it is never auto-truncated.",
                        slug, speaker.Id, tooLongReason);
                    continue;
                }

                _db.Tasks.Add(new ParticipantTask
                {
                    EventId = eventId,
                    AssignedParticipantId = speaker.Id,
                    Title = dl.Title,
                    Description = dl.Description,
                    DueDate = dl.DueDate,
                    State = TaskState.Open,
                    SourceKey = sourceKey,
                    CreatedAt = now,
                });
                created++;
            }

            // M1 PRUNE orphans: delete this speaker's speakerdl-managed tasks whose
            // SourceKey the CURRENT config + entitlement set no longer produces —
            // e.g. a deadline renamed (the slug, hence the SourceKey, changes,
            // leaving the old row orphaned) or a speaker who lost an entitlement
            // (P12) / whose category excludes them (§299 6.2 — a Sponsor-category
            // speaker's desired set may be legitimately EMPTY, and their stale
            // tasks must still be pruned). GUARD against a transient empty/missing
            // config wiping tasks = the early "no config / no deadlines" returns
            // above: this loop only runs with a non-empty config in hand. Mirrors
            // the orphan-prune precedent in SponsorOrderPullService.
            {
                var keyPrefix = $"speakerdl:{speaker.Id}:";
                var orphans = await _db.Tasks
                    .Where(t => t.EventId == eventId
                                && t.SourceKey != null
                                && t.SourceKey.StartsWith(keyPrefix)
                                && !desiredKeys.Contains(t.SourceKey))
                    .ToListAsync(ct);
                if (orphans.Count > 0)
                {
                    _db.Tasks.RemoveRange(orphans);
                    removed += orphans.Count;
                }
            }
        }

        // §253 G11: EX-SPEAKER sweep — the per-speaker prune above only reaches
        // participants who are STILL active speakers, so a role change away from
        // Speaker used to orphan the person's dated speakerdl: tasks forever (they
        // kept firing due-day reminders). Delete every speakerdl:-managed task whose
        // assignee is no longer a Speaker. Deactivated speakers are NOT swept — they
        // may be reactivated (reminders are already silenced by the builders'
        // IsActive gate, §253 G9).
        var exSpeakerOrphans = await _db.Tasks
            .Where(t => t.EventId == eventId
                        && t.SourceKey != null
                        && t.SourceKey.StartsWith("speakerdl:")
                        && t.AssignedParticipantId != null
                        && t.AssignedParticipant!.Role != ParticipantRole.Speaker)
            .ToListAsync(ct);
        if (exSpeakerOrphans.Count > 0)
        {
            _db.Tasks.RemoveRange(exSpeakerOrphans);
            removed += exSpeakerOrphans.Count;
        }

        // §264: the Master-Class "submit session title and abstract" deadline was RETIRED
        // (titles/abstracts come from Sessionize, not a speaker task) and removed from config,
        // but stale rows seeded before the removal can survive the per-speaker orphan-prune
        // above (that prune is skipped for a speaker whose current desired set is empty).
        // Sweep them UNCONDITIONALLY here so no speaker keeps seeing the retired task.
        //
        // §340-F — MATCHED BY EXACT KEY, NEVER BY A TITLE SUBSTRING. This used to carry
        // `SourceKey.StartsWith("speakerdl:") && Title.Contains("abstract")`, which was a trap
        // armed and waiting: this seeder runs ON SPEAKER PAGE LOAD (§326a), so the moment anyone
        // added a deadline titled e.g. "Review your session abstract", the per-speaker loop above
        // would CREATE it and this sweep would DELETE it in the same pass — forever, on every page
        // load, with no error anywhere. A task that can never exist and cannot be debugged from its
        // symptom. `Contains` also translates to SQL LIKE, case-insensitive under the default
        // collation, so "Abstract" matched too.
        //
        // Verified on PROD before narrowing (2026-07-27): zero rows match anywhere — no task title
        // contains "abstract", and the distinct `speakerdl:` slugs in use are appreciation-dinner,
        // help-to-promote-your-sessions, hotel, preday-lunch, submit-travel-reimbursement,
        // swag--speaker-gift, upload-final-presentation and upload-preview-presentation. So this
        // narrowing removes a future hazard without leaving a single real retired row behind.
        //
        // What remains matches only things that can no longer be legitimately created: the LEGACY
        // `seed:speaker:abstract` key the retired seeder actually used (found on prod 2026-07-10),
        // the EXACT slug that deadline would have had in `speakerdl:` form, and the EXACT retired
        // title as a last-resort backstop regardless of prefix. All three are equality tests, so a
        // new deadline can share a word with them and still be safe.
        const string retiredAbstractSlug = ":submit-session-title-and-abstract";
        var retiredTitleAbstract = await _db.Tasks
            .Where(t => t.EventId == eventId
                        && t.SourceKey != null
                        && ((t.SourceKey.StartsWith("speakerdl:")
                             && t.SourceKey.EndsWith(retiredAbstractSlug))
                            || t.SourceKey == "seed:speaker:abstract"
                            || t.Title == "Submit session title and abstract"))
            .ToListAsync(ct);
        if (retiredTitleAbstract.Count > 0)
        {
            _db.Tasks.RemoveRange(retiredTitleAbstract);
            removed += retiredTitleAbstract.Count;
        }

        if (created > 0 || removed > 0)
        {
            await _db.SaveChangesAsync(ct);
        }
        return created;
    }

    /// <summary>
    /// §708 — one speaker's audience facts, translated from the gates this seeder already applies.
    /// </summary>
    /// <remarks>
    /// <para>🔒 <b>This is the migration's load-bearing translation, so each line names the rule it
    /// carries across.</b> Every gate below exists in the legacy loop as a slug substring test; the
    /// registry states it once, per definition, where a rename cannot break it. §708.3 recorded that
    /// the §708 plan had counted only two of the three gate families — this is the third.</para>
    ///
    /// <para>⚠️ <b>The OR-pairs are deliberate and must not be tightened.</b>
    /// <c>DeadlineAllowedByEntitlement</c> accepts <c>Swag OR Polo</c> for the swag deadline and
    /// <c>LunchPreDay OR LunchMainDay</c> for lunch. Mapping either pair to a single entitlement
    /// would silently WITHHOLD a task from speakers who hold only the other one.</para>
    /// </remarks>
    private static TaskAudiencePredicate[] SpeakerPredicates(
        SpeakerCategory? category,
        string? country,
        SpeakerDays days,
        IReadOnlySet<OrderItem> entitled)
    {
        var predicates = new List<TaskAudiencePredicate>();

        // P12 entitlement gates — a speaker must not be given a logistics task they cannot act on.
        if (entitled.Contains(OrderItem.Hotel))
            predicates.Add(TaskAudiencePredicate.EntitledToHotel);
        if (entitled.Contains(OrderItem.AppreciationDinner))
            predicates.Add(TaskAudiencePredicate.EntitledToAppreciationDinner);
        if (entitled.Contains(OrderItem.Swag) || entitled.Contains(OrderItem.Polo))
            predicates.Add(TaskAudiencePredicate.EntitledToSwag);
        if (entitled.Contains(OrderItem.LunchPreDay) || entitled.Contains(OrderItem.LunchMainDay))
            predicates.Add(TaskAudiencePredicate.EntitledToLunchPreDay);
        // §253 G10 — travel is a logistics entitlement too: a Guest or sponsor-funded speaker has
        // none, and must never get the claim task or its due-day reminder.
        if (entitled.Contains(OrderItem.TravelReimbursement))
            predicates.Add(TaskAudiencePredicate.EntitledToTravelReimbursement);

        // §143 — outside Denmark. 🔒 A speaker with NO country set yet counts as non-Denmark, i.e.
        // they DO get the travel task. That asymmetry pre-dates this model and is deliberate:
        // withholding a reimbursement task from someone whose country we have simply not asked for
        // would quietly cost them money.
        if (!IsDenmark(country))
            predicates.Add(TaskAudiencePredicate.NonDenmark);

        // §299 C5 — presenting on the pre-day is DERIVED from the linked sessions (the stored
        // SpeakingPreDay flag is retired). No shipped speaker definition requires it yet; it is
        // supplied so the matrix is complete and a masterclass-only task can be added in config-free
        // fashion later.
        if (days.PresentsPreDay)
            predicates.Add(TaskAudiencePredicate.IsMasterClassSpeaker);

        // §299 6.2 / §456 / §458 — an EXHIBITOR's speaker. Stated positively and EXCLUDED at the two
        // definitions it applies to (Help Promote, preview upload); the final upload deliberately
        // does NOT exclude it, which is the single exception he drew.
        if (category == SpeakerCategory.Sponsor)
            predicates.Add(TaskAudiencePredicate.IsSponsorCategorySpeaker);

        return predicates.ToArray();
    }

    /// <summary>
    /// P12 entitlement gate for a deadline, keyed off its slug. A logistics
    /// deadline (hotel / dinner / swag / lunch) is allowed only when the speaker is
    /// entitled to the underlying <see cref="OrderItem"/> — mirroring the per-form
    /// gates (Hotel→Hotel, Dinner→AppreciationDinner, Swag→Swag|Polo,
    /// Lunch→LunchPreDay|LunchMainDay). Any other deadline (e.g. a presentation
    /// upload) is NOT entitlement-gated and is always allowed.
    /// </summary>
    private static bool DeadlineAllowedByEntitlement(string slug, IReadOnlySet<OrderItem> entitled)
    {
        if (slug.Contains("hotel", StringComparison.Ordinal))
            return entitled.Contains(OrderItem.Hotel);
        if (slug.Contains("dinner", StringComparison.Ordinal))
            return entitled.Contains(OrderItem.AppreciationDinner);
        if (slug.Contains("swag", StringComparison.Ordinal))
            return entitled.Contains(OrderItem.Swag) || entitled.Contains(OrderItem.Polo);
        if (slug.Contains("lunch", StringComparison.Ordinal))
            return entitled.Contains(OrderItem.LunchPreDay) || entitled.Contains(OrderItem.LunchMainDay);
        // §253 G10: TRAVEL is a logistics deadline too — a Sponsor-category / Guest
        // speaker (no TravelReimbursement entitlement, §299 6.2) must never get the
        // travel-reimbursement task or its due-day reminder. The nightly orphan
        // prune (desiredKeys, above) removes any already-seeded travel task from a
        // speaker who is not (or no longer) entitled.
        if (slug.Contains("travel", StringComparison.Ordinal))
            return entitled.Contains(OrderItem.TravelReimbursement);
        return true; // non-logistics deadline (e.g. presentation upload) — no gate
    }

    /// <summary>
    /// True when the deadline slug denotes a LOGISTICS deadline (one of the
    /// entitlement-gated families in <see cref="DeadlineAllowedByEntitlement"/>).
    /// Everything else (the preview/final presentation uploads) is a
    /// PRESENTATION-style deadline — the set a Sponsor-category speaker is
    /// excluded from (§299 6.2).
    /// </summary>
    private static bool IsLogisticsSlug(string slug) =>
        slug.Contains("hotel", StringComparison.Ordinal)
        || slug.Contains("dinner", StringComparison.Ordinal)
        || slug.Contains("swag", StringComparison.Ordinal)
        || slug.Contains("lunch", StringComparison.Ordinal)
        || slug.Contains("travel", StringComparison.Ordinal);

    /// <summary>
    /// §456 (operator 2026-07-27) — the NON-logistics deadlines a SPONSOR-category speaker still
    /// owns: *"only task relevant for a sponsor (exhibitor) speaker is the task for upload final
    /// presentation"*.
    ///
    /// <para>Deliberately matched on <c>final</c> AND <c>presentation</c> together rather than on
    /// "final" alone — a future deadline called "final headcount" or similar must not slip into a
    /// sponsor speaker's list because it happens to contain the word. The PREVIEW upload stays
    /// excluded, which is the distinction he drew.</para>
    /// </summary>
    private static bool IsSponsorSpeakerAllowedSlug(string slug) =>
        slug.Contains("final", StringComparison.Ordinal)
        && slug.Contains("presentation", StringComparison.Ordinal);

    /// <summary>
    /// Compute a participant's EFFECTIVE <see cref="OrderItem"/> entitlement set —
    /// the role + speaker hats with their per-person overrides applied. This mirrors
    /// the Web-layer <c>FormEntitlementGate.EffectiveItemsAsync</c>; the logic is
    /// replicated here (rather than referenced) because that helper lives in the
    /// CommunityHub web project, which this Core seeder cannot reference. Both
    /// delegate to the shared <see cref="OrderEntitlements.Effective"/> rules.
    /// </summary>
    private async Task<IReadOnlySet<OrderItem>> EffectiveItemsAsync(
        int eventId, int participantId, SpeakerDays days, CancellationToken ct)
    {
        var participant = await _db.Participants
            .FirstOrDefaultAsync(p => p.Id == participantId && p.EventId == eventId, ct);
        if (participant is null)
        {
            return new HashSet<OrderItem>();
        }

        var speaker = await _db.SpeakerProfiles
            .FirstOrDefaultAsync(sp => sp.EventId == eventId && sp.ParticipantId == participantId, ct);

        var overrides = await _db.ParticipantOrderOverrides
            .Where(o => o.EventId == eventId && o.ParticipantId == participantId)
            .ToListAsync(ct);

        return OrderEntitlements.Effective(participant, speaker, days, overrides);
    }

    /// <summary>
    /// True when the speaker's country denotes Denmark. Speaker Details stores a
    /// 2-letter upper code ("DK"); we also tolerate the full name "Denmark"
    /// case-insensitively. Null/blank = unknown = NOT Denmark (they get the task).
    /// </summary>
    /// <remarks>
    /// §399: delegates to the SHARED <see cref="Entitlements.TravelReimbursementPolicy"/>. This was
    /// once the ONLY implementation of the rule, which is precisely why the nav entry and
    /// <c>/Forms/Travel</c> never applied it — the deadline task was withheld from Danish speakers
    /// while the form happily offered them the claim. One rule, one place, every caller.
    /// </remarks>
    private static bool IsDenmark(string? country) =>
        Entitlements.TravelReimbursementPolicy.IsDenmark(country);

    private static string Slug(string title)
    {
        var chars = title.ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c == ' ')
            .ToArray();
        var slug = new string(chars).Replace(' ', '-');
        return slug.Length > 60 ? slug[..60] : slug;
    }
}
