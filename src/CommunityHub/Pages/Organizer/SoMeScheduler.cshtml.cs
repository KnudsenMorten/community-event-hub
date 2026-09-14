using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>One post type's line on the planner page.</summary>
public sealed record SoMePlanRow(
    string Name, int Planned, int Approved, int Published, DateTimeOffset? NextDue);

/// <summary>
/// §833 — THE POST PLANNER, given a face.
///
/// <para>§824.20/§824.21 built the scheduler as a JOB and nothing else, so the operator went looking
/// for it on the Marketing / SoMe hub and found nothing: <i>"where are a features like scheduler,
/// post editor etc"</i>.</para>
///
/// <para>🔑 <b>The important half is not the button, it is the REASON.</b> §824.23 makes the planner
/// refuse to compose while the edition's post footer is empty and "log why" — to a log. That refusal
/// is currently the single most important fact about the SoMe engine (§829.1 lists the empty footer
/// as the one thing blocking it), and it was visible only in App Insights. This page states it on
/// screen, in his words, with the way to fix it one click away.</para>
///
/// <para>Running the planner from here is safe by construction: every post it creates is
/// <c>IsActive = false</c> (§824.21a), so it fills the queue for approval and can publish nothing.</para>
/// </summary>
[Authorize]
public class SoMeSchedulerModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly CommunityHubDbContext _db;
    private readonly SoMeScheduleService _scheduler;

    public SoMeSchedulerModel(
        ICurrentParticipantAccessor participant,
        CommunityHubDbContext db,
        SoMeScheduleService scheduler)
    {
        _participant = participant;
        _db = db;
        _scheduler = scheduler;
    }

    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }
    public bool MessageIsError { get; private set; }

    /// <summary>The §824.23 footer parts this edition is still missing, in his words. Empty = ready.</summary>
    public IReadOnlyList<string> MissingFooterParts { get; private set; } = Array.Empty<string>();

    /// <summary>True when the edition has already started — the planner declines then too.</summary>
    public bool EditionAlreadyStarted { get; private set; }

    public bool CanCompose => MissingFooterParts.Count == 0 && !EditionAlreadyStarted;

    public IReadOnlyList<SoMePlanRow> Rows { get; private set; } = Array.Empty<SoMePlanRow>();

    public int TotalPlanned => Rows.Sum(r => r.Planned);
    public int TotalApproved => Rows.Sum(r => r.Approved);
    public int TotalPublished => Rows.Sum(r => r.Published);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostRunAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        // The service returns its own refusal reason (§824.23) — surfaced verbatim rather than
        // reworded, so what he reads here is exactly what the job would have logged.
        var result = await _scheduler.RunAsync(me.EventId, ct);

        Message = result.Created > 0
            ? $"{result.Created} post(s) planned and held for approval. {result.Message}"
            : result.Message;
        // §1178 — a run that planned nothing but REMOVED an excluded post did work, and did it
        // correctly. Painting that red reads as a failure and sends him looking for one.
        MessageIsError = result.Created == 0 && result.AlreadyPlanned == 0 && result.GuardRemoved == 0;

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    private async Task LoadAsync(int eventId, CancellationToken ct)
    {
        // Why the planner would decline, computed the same way the service decides (§824.23).
        var footer = await _db.SoMeSettings
            .Where(s => s.EventId == eventId)
            .Select(s => new { s.EventSystemUrl, s.EventTags, s.OrganizerCredits })
            .FirstOrDefaultAsync(ct);

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(footer?.EventSystemUrl)) missing.Add("event link");
        if (string.IsNullOrWhiteSpace(footer?.EventTags)) missing.Add("hashtags");
        if (string.IsNullOrWhiteSpace(footer?.OrganizerCredits)) missing.Add("organizer credit");
        MissingFooterParts = missing;

        var start = await _db.Events
            .Where(e => e.Id == eventId)
            .Select(e => (DateOnly?)e.StartDate)
            .FirstOrDefaultAsync(ct);
        EditionAlreadyStarted = start is not null && start.Value <= DateOnly.FromDateTime(DateTime.UtcNow);

        var posts = await _db.SoMePosts
            .Where(p => p.EventId == eventId)
            .Select(p => new { p.TemplateKind, p.Status, p.IsActive, p.ScheduledAtUtc })
            .AsNoTracking()
            .ToListAsync(ct);

        var kinds = new (SoMeTemplateKind? Kind, string Name)[]
        {
            (SoMeTemplateKind.SpeakerTracks,   "Type 1 — speaker tracks"),
            (SoMeTemplateKind.Session,         "Type 2 — sessions"),
            (SoMeTemplateKind.SponsorCategory, "Type 3 — sponsor tiers"),
            (SoMeTemplateKind.Sponsor,         "Type 4 — sponsors"),
            (SoMeTemplateKind.EventPost,       "Type 5 — event posts"),
            (null,                             "Ad-hoc / written by hand"),
        };

        Rows = kinds.Select(k =>
        {
            var mine = posts.Where(p => p.TemplateKind == k.Kind).ToList();
            return new SoMePlanRow(
                k.Name,
                // "Planned" is the §824.21a hold: queued but NOT yet approved. That distinction is
                // the whole safety model, so the page counts it as its own column.
                Planned: mine.Count(p => p.Status == SoMePostStatus.Queued && !p.IsActive),
                Approved: mine.Count(p => p.Status == SoMePostStatus.Queued && p.IsActive),
                Published: mine.Count(p => p.Status == SoMePostStatus.Published),
                NextDue: mine
                    .Where(p => p.Status == SoMePostStatus.Queued && p.IsActive)
                    .Select(p => (DateTimeOffset?)p.ScheduledAtUtc)
                    .DefaultIfEmpty(null)
                    .Min());
        }).ToList();
    }
}
