using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Config;

/// <summary>One speaker deadline from speaker-deadlines.&lt;edition&gt;.json.</summary>
public sealed class SpeakerDeadlineDefinition
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>Days before the event start date this is due.</summary>
    [JsonPropertyName("daysBeforeEvent")]
    public int DaysBeforeEvent { get; set; }

    /// <summary>If true, also applies to Master Class speakers' pre-day.</summary>
    [JsonPropertyName("includesMasterclass")]
    public bool IncludesMasterclass { get; set; } = true;
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
/// configured deadline, dated from the event start date. Idempotent: each task
/// has a SourceKey "speakerdl:{participantId}:{slug}", so re-running never
/// duplicates. New speakers (e.g. a later Sessionize import) get their
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

        var ev = await _db.Events
            .Where(e => e.Id == eventId)
            .Select(e => new { e.StartDate })
            .FirstOrDefaultAsync(ct);
        if (ev is null)
        {
            return 0;
        }

        var speakers = await _db.Participants
            .Where(p => p.EventId == eventId
                        && p.IsActive
                        && (p.Role == ParticipantRole.Speaker
                            || p.Role == ParticipantRole.MasterclassSpeaker))
            .Select(p => new { p.Id, p.Role })
            .ToListAsync(ct);

        var now = _clock.GetUtcNow();
        var created = 0;

        foreach (var speaker in speakers)
        {
            foreach (var dl in config.Deadlines)
            {
                // A Master-Class-only deadline is skipped for plain speakers.
                if (!dl.IncludesMasterclass
                    && speaker.Role == ParticipantRole.MasterclassSpeaker)
                {
                    // (deadline applies to everyone unless flagged otherwise)
                }

                var sourceKey = $"speakerdl:{speaker.Id}:{Slug(dl.Title)}";
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
                    DueDate = ev.StartDate.AddDays(-dl.DaysBeforeEvent),
                    State = TaskState.Open,
                    SourceKey = sourceKey,
                    CreatedAt = now,
                });
                created++;
            }
        }

        if (created > 0)
        {
            await _db.SaveChangesAsync(ct);
        }
        return created;
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
