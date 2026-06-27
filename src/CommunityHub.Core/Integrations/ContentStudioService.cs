using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// Event facts the content templates fold into copy (REQUIREMENTS §31). Bound from
/// the <c>"ContentStudio"</c> section; defaults carry the current edition's known
/// facts so the studio is useful out of the box. Env-var form uses <c>__</c>
/// (e.g. <c>ContentStudio__TicketsUrl</c>).
/// </summary>
public sealed class ContentStudioOptions
{
    public const string SectionName = "ContentStudio";

    public string EventName { get; set; } = "Experts Live Denmark 2027";
    public string DatesText { get; set; } = "9–10 June 2027";
    public string? VenueText { get; set; } = "Bella Center, Copenhagen";
    public string? TicketsUrl { get; set; } = "https://expertslive.dk/tickets/";
    public string? AgendaUrl { get; set; } = "https://expertslive.dk/agenda/";

    public ContentContext ToContext() =>
        new(EventName, DatesText, VenueText, TicketsUrl, AgendaUrl);
}

/// <summary>
/// Orchestrates §31 content generation: gathers the data (aggregate attendee
/// telemetry for ticket-sales; the edition's master classes + speakers for the
/// master-class post), renders it with the pure <see cref="ContentTemplateEngine"/>,
/// and — gated by the <c>content-studio</c> feature — creates a <b>WordPress
/// draft</b> the operator validates before publishing. Preview is side-effect-free;
/// only draft creation is gated + writes out. LinkedIn output is the short text,
/// held for validation (nothing auto-posts in v1).
/// </summary>
public sealed class ContentStudioService
{
    public const string FeatureKey = "content-studio";

    private readonly CommunityHubDbContext _db;
    private readonly AttendeeTelemetryService _telemetry;
    private readonly ContentTemplateEngine _engine;
    private readonly IWordPressPublisher _wordpress;
    private readonly ContentStudioOptions _options;
    private readonly FeatureGateService _gate;
    private readonly ILogger<ContentStudioService> _log;

    public ContentStudioService(
        CommunityHubDbContext db,
        AttendeeTelemetryService telemetry,
        ContentTemplateEngine engine,
        IWordPressPublisher wordpress,
        ContentStudioOptions options,
        FeatureGateService gate,
        ILogger<ContentStudioService> log)
    {
        _db = db;
        _telemetry = telemetry;
        _engine = engine;
        _wordpress = wordpress;
        _options = options;
        _gate = gate;
        _log = log;
    }

    /// <summary>True when the WordPress connector is wired (drives the "create draft" button).</summary>
    public bool WordPressReady => _wordpress.CanWrite;

    /// <summary>Render the content for preview. Side-effect-free; null when the data isn't available.</summary>
    public async Task<GeneratedContent?> PreviewAsync(
        int eventId, ContentKind kind, CancellationToken ct = default)
    {
        var ctx = _options.ToContext();
        switch (kind)
        {
            case ContentKind.TicketSales:
            {
                var t = await _telemetry.GetAsync("all", ct: ct);
                return t is null ? null : _engine.BuildTicketSales(t, ctx);
            }
            case ContentKind.MasterClasses:
            {
                var items = await LoadMasterClassesAsync(eventId, ct);
                return items.Count == 0 ? null : _engine.BuildMasterClasses(items, ctx);
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// Create a WordPress DRAFT for the rendered content. Gated by <c>content-studio</c>
    /// (disabled ⇒ honest no-op) and self-gated by the connector (unconfigured ⇒
    /// no-op). Never publishes.
    /// </summary>
    public async Task<WordPressPublishResult> CreateWordPressDraftAsync(
        int eventId, ContentKind kind, CancellationToken ct = default)
    {
        if (!await _gate.IsFeatureEnabledAsync(FeatureKey, eventId, ct))
            return new WordPressPublishResult(false, null, null,
                "Content Studio is turned off for this event. Enable it in Settings first.");

        if (!_wordpress.CanWrite)
            return new WordPressPublishResult(false, null, null,
                "WordPress connector is not configured — no draft created.");

        var content = await PreviewAsync(eventId, kind, ct);
        if (content is null)
            return new WordPressPublishResult(false, null, null,
                "No data available to generate this post yet.");

        var result = await _wordpress.CreateDraftAsync(
            new WordPressDraft(content.Title, content.BodyHtml), ct);
        _log.LogInformation("ContentStudio: WordPress draft for {Kind} (event {EventId}) — {Msg}",
            kind, eventId, result.Message);
        return result;
    }

    private async Task<List<MasterClassContentItem>> LoadMasterClassesAsync(
        int eventId, CancellationToken ct)
    {
        var sessions = await _db.Sessions
            .Where(s => s.EventId == eventId && s.Type == SessionType.MasterClass && !s.IsServiceSession)
            .Select(s => new
            {
                s.Title,
                s.Abstract,
                s.Track,
                s.PublicSlug,
                Speakers = s.SessionSpeakers
                    .Select(ss => new { ss.Participant.FullName, ss.ParticipantId })
                    .ToList(),
            })
            .OrderBy(s => s.Title)
            .ToListAsync(ct);

        // Resolve taglines from speaker profiles (company isn't on the profile).
        var profiles = await _db.SpeakerProfiles
            .Where(p => p.EventId == eventId)
            .Select(p => new { p.ParticipantId, p.Tagline })
            .ToDictionaryAsync(p => p.ParticipantId, p => p.Tagline, ct);

        return sessions.Select(s => new MasterClassContentItem(
            Title: s.Title,
            Abstract: s.Abstract,
            Track: s.Track,
            Speakers: s.Speakers.Select(sp => new MasterClassSpeaker(
                Name: sp.FullName,
                Tagline: profiles.TryGetValue(sp.ParticipantId, out var tag) ? tag : null,
                Company: null)).ToList(),
            PublicUrl: BuildMasterClassUrl(s.PublicSlug))).ToList();
    }

    private string? BuildMasterClassUrl(string? slug) =>
        string.IsNullOrWhiteSpace(slug) ? null : $"{_options.AgendaUrl?.TrimEnd('/')}";
}
