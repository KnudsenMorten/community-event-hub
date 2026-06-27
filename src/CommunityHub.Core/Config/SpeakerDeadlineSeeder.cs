using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Entitlements;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Config;

/// <summary>One speaker deadline from speaker-deadlines.&lt;edition&gt;.json.</summary>
public sealed class SpeakerDeadlineDefinition
{
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
}

/// <summary>The speaker-deadlines config file.</summary>
public sealed class SpeakerDeadlineConfig
{
    [JsonPropertyName("deadlines")]
    public List<SpeakerDeadlineDefinition> Deadlines { get; set; } = new();
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

    public SpeakerDeadlineSeeder(
        CommunityHubDbContext db,
        SpeakerDeadlineOptions options,
        TimeProvider clock)
    {
        _db = db;
        _options = options;
        _clock = clock;
    }

    /// <summary>Create any missing speaker-deadline tasks. Returns the count created.</summary>
    public async Task<int> SeedAsync(int eventId, CancellationToken ct = default)
    {
        if (!File.Exists(_options.ConfigPath))
        {
            return 0; // no config => nothing to seed
        }

        var config = JsonSerializer.Deserialize<SpeakerDeadlineConfig>(
            File.ReadAllText(_options.ConfigPath), JsonOptions);
        if (config is null || config.Deadlines.Count == 0)
        {
            return 0;
        }

        var eventExists = await _db.Events.AnyAsync(e => e.Id == eventId, ct);
        if (!eventExists)
        {
            return 0;
        }

        // The pre-day ("Master Class") nuance now lives on the speaker PROFILE
        // (SpeakingPreDay), not on a separate role — so a masterclass-only
        // deadline is gated on the profile's SpeakingPreDay flag below.
        var speakers = await _db.Participants
            .Where(p => p.EventId == eventId
                        && p.IsActive
                        && p.Role == ParticipantRole.Speaker)
            .Select(p => new
            {
                p.Id,
                SpeakingPreDay = _db.SpeakerProfiles
                    .Where(s => s.EventId == eventId && s.ParticipantId == p.Id)
                    .Select(s => (bool?)s.SpeakingPreDay)
                    .FirstOrDefault() ?? false,
            })
            .ToListAsync(ct);

        var now = _clock.GetUtcNow();
        var created = 0;
        var removed = 0;

        foreach (var speaker in speakers)
        {
            // P12: this speaker's EFFECTIVE entitlement set, computed once, so the
            // per-deadline logistics gate (below) can drop tasks they can't act on.
            var entitled = await EffectiveItemsAsync(eventId, speaker.Id, ct);

            // The speakerdl SourceKeys that SHOULD exist for this speaker after this
            // run — every deadline that still applies once the masterclass + P12
            // entitlement gates are honoured. Drives both the add (missing keys) and
            // the orphan-prune (no-longer-desired keys) below.
            var desiredKeys = new HashSet<string>(StringComparer.Ordinal);

            foreach (var dl in config.Deadlines)
            {
                // A masterclass-only deadline is skipped unless this speaker is
                // delivering on the pre-day (SpeakingPreDay).
                if (dl.MasterclassOnly && !speaker.SpeakingPreDay)
                {
                    continue;
                }

                var slug = Slug(dl.Title);

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
            // (P12). GUARD: only prune when this run produced a non-empty desired
            // set, so a transient empty/missing config can never wipe a speaker's
            // tasks. Mirrors the orphan-prune precedent in SponsorOrderPullService.
            if (desiredKeys.Count > 0)
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

        if (created > 0 || removed > 0)
        {
            await _db.SaveChangesAsync(ct);
        }
        return created;
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
        return true; // non-logistics deadline (e.g. presentation upload) — no gate
    }

    /// <summary>
    /// Compute a participant's EFFECTIVE <see cref="OrderItem"/> entitlement set —
    /// the role + speaker hats with their per-person overrides applied. This mirrors
    /// the Web-layer <c>FormEntitlementGate.EffectiveItemsAsync</c>; the logic is
    /// replicated here (rather than referenced) because that helper lives in the
    /// CommunityHub web project, which this Core seeder cannot reference. Both
    /// delegate to the shared <see cref="OrderEntitlements.Effective"/> rules.
    /// </summary>
    private async Task<IReadOnlySet<OrderItem>> EffectiveItemsAsync(
        int eventId, int participantId, CancellationToken ct)
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

        return OrderEntitlements.Effective(participant, speaker, overrides);
    }

    private static string Slug(string title)
    {
        var chars = title.ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c == ' ')
            .ToArray();
        var slug = new string(chars).Replace(' ', '-');
        return slug.Length > 60 ? slug[..60] : slug;
    }
}
