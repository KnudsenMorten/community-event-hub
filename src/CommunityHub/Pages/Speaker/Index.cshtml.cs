using CommunityHub.Auth;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Speaker;

/// <summary>
/// The Speaker Hub -- a single, self-service milestone tracker for the
/// signed-in speaker / master-class speaker. It consolidates the speaker's
/// deadline milestones (one card each, with a live countdown and a
/// mark-done / reopen action) and the speaker-form completeness check into a
/// cohesive journey view, so a speaker sees exactly where they are in the
/// path from "accepted" to "on-stage" without hunting through the generic
/// task list. Mobile-first (works at ~360px).
///
/// Only Speaker / MasterclassSpeaker reach the content; any other role gets a
/// friendly "not a speaker" message instead of a 403 so the nav stays simple.
/// </summary>
[Authorize]
public class IndexModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SpeakerMilestoneService _milestones;
    private readonly SpeakerDeadlineSeeder _speakerDeadlines;
    private readonly MasterClassLogisticsService _logistics;
    private readonly SpeakerSessionsService _sessions;
    private readonly PublicSessionsService _publicSessions;
    private readonly CalendarFeedTokenService _calendarTokens;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(
        CommunityHubDbContext db,
        ICurrentParticipantAccessor participant,
        SpeakerMilestoneService milestones,
        SpeakerDeadlineSeeder speakerDeadlines,
        MasterClassLogisticsService logistics,
        SpeakerSessionsService sessions,
        PublicSessionsService publicSessions,
        CalendarFeedTokenService calendarTokens,
        ILogger<IndexModel> logger)
    {
        _db = db;
        _participant = participant;
        _milestones = milestones;
        _speakerDeadlines = speakerDeadlines;
        _logistics = logistics;
        _sessions = sessions;
        _publicSessions = publicSessions;
        _calendarTokens = calendarTokens;
        _logger = logger;
    }

    /// <summary>One master class this speaker is linked to, with its public logistics link.</summary>
    /// <param name="SessionId">The master-class session id.</param>
    /// <param name="Title">The master-class title.</param>
    /// <param name="PublicLink">The public logistics page URL (minted on view).</param>
    public sealed record MyMasterClass(int SessionId, string Title, string Slug, string PublicLink);

    /// <summary>The signed-in speaker's master classes (with public logistics link).</summary>
    public List<MyMasterClass> MasterClasses { get; private set; } = new();

    public static readonly ParticipantRole[] EligibleRoles =
    {
        ParticipantRole.Speaker,
        ParticipantRole.MasterclassSpeaker,
    };

    public bool AccessDenied { get; private set; }
    public ParticipantRole Role { get; private set; }
    public string FirstName { get; private set; } = "there";
    public SpeakerMilestoneProgress Progress { get; private set; } =
        new(Array.Empty<SpeakerMilestone>());

    /// <summary>True once the speaker has saved their speaker-form details.</summary>
    public bool SpeakerDetailsSubmitted { get; private set; }

    /// <summary>The signed-in speaker's own sessions (room/time + question links).</summary>
    public IReadOnlyList<MySpeakerSession> MySessions { get; private set; } =
        Array.Empty<MySpeakerSession>();

    /// <summary>
    /// Ids of the speaker's own sessions whose PUBLIC page (<c>/Sessions/{id}</c>)
    /// would actually resolve — the same gate <see cref="PublicSessionsService.GetByIdAsync"/>
    /// applies (in the active edition, not a service session), NOT the speaker's own
    /// profile-publish state. The view shows the "view public session page" link iff
    /// the session id is in this set.
    /// </summary>
    public IReadOnlySet<int> PubliclyViewableSessionIds { get; private set; } =
        new HashSet<int>();

    /// <summary>
    /// The speaker's PUBLIC preview URL (<c>/Speakers/{id}</c>) when their profile
    /// is selected-for-publish (the hard gate). Empty while unselected — the page
    /// then explains the preview unlocks once the lineup is announced.
    /// </summary>
    public string PublicPreviewUrl { get; private set; } = string.Empty;

    /// <summary>True once the speaker is SelectedForPublish (their public page is live).</summary>
    public bool PublicProfileLive { get; private set; }

    /// <summary>webcal:// subscribe URL for the speaker's personal deadline feed (empty if sync off).</summary>
    public string CalendarWebcalUrl { get; private set; } = string.Empty;

    /// <summary>True when the active edition has calendar sync enabled.</summary>
    public bool CalendarSyncEnabled { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");

        Role = me.Role;
        FirstName = me.FirstName;
        if (!EligibleRoles.Contains(me.Role))
        {
            AccessDenied = true;
            return Page();
        }

        // Make sure the speaker's milestone tasks exist before we read them --
        // a speaker imported after the last seeding run otherwise sees an empty
        // tracker on their first visit. Idempotent on SourceKey; never fails the
        // page (matches /Index's behaviour).
        try
        {
            await _speakerDeadlines.SeedAsync(me.EventId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Speaker hub: deadline seeding failed for event {EventId}", me.EventId);
        }

        await LoadAsync(me, ct);
        return Page();
    }

    /// <summary>Mark one of the speaker's own milestones done / not done.</summary>
    public async Task<IActionResult> OnPostToggleAsync(int taskId, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!EligibleRoles.Contains(me.Role)) return RedirectToPage("/Index");

        await _milestones.ToggleAsync(me.EventId, me.ParticipantId, taskId, ct);
        return RedirectToPage();
    }

    private async Task LoadAsync(CurrentParticipant me, CancellationToken ct)
    {
        Progress = await _milestones.GetProgressAsync(me.EventId, me.ParticipantId, ct);

        var profile = await _db.SpeakerProfiles.FirstOrDefaultAsync(
            sp => sp.EventId == me.EventId && sp.ParticipantId == me.ParticipantId, ct);

        SpeakerDetailsSubmitted = profile is not null
            && (profile.Accreditation != null || profile.Country != null
                || profile.SpeakingPreDay || profile.SpeakingMainDay);

        // My sessions (own-row scoped server-side) — room/time + question links.
        MySessions = await _sessions.GetMySessionsAsync(
            me.EventId, me.ParticipantId, me.Role, ct);

        // Resolve, per session, whether its PUBLIC /Sessions/{id} page would actually
        // resolve — the same gate the public page uses (active edition + not a service
        // session), independent of the speaker's own profile-publish state. The view
        // only links rows that are genuinely publicly viewable.
        PubliclyViewableSessionIds = await _publicSessions.GetPubliclyViewableSessionIdsAsync(
            MySessions.Select(s => s.SessionId), ct);

        // Public preview: only resolvable once the organizer has selected this
        // speaker for publish (the §6 hard gate — an unselected /Speakers/{id}
        // 404s, so we only surface the link when it actually resolves).
        PublicProfileLive = profile?.SelectedForPublish == true;
        if (PublicProfileLive)
        {
            PublicPreviewUrl =
                Url.Page("/Speakers/Detail", new { id = me.ParticipantId }) ?? string.Empty;
        }

        // Calendar reminders: surface the speaker's personal deadline feed
        // (the same per-user token feed the volunteer My-schedule uses), so a
        // speaker can subscribe once and keep their milestone deadlines in sync.
        CalendarSyncEnabled = await _db.Events
            .Where(e => e.Id == me.EventId)
            .Select(e => e.CalendarSyncEnabled)
            .FirstOrDefaultAsync(ct);
        if (CalendarSyncEnabled)
        {
            try
            {
                var token = await _calendarTokens.EnsureTokenAsync(me.ParticipantId, ct);
                var host = Request.Host.Value ?? string.Empty;
                CalendarWebcalUrl = $"webcal://{host}/cal/{token}.ics";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Speaker hub: failed to ensure calendar feed token for participant {Pid}",
                    me.ParticipantId);
            }
        }

        // Master classes this speaker is linked to — surface the "show public
        // link" affordance (REQUIREMENTS § 6c). The slug is minted on first view.
        var myMasterClasses = await _db.SessionSpeakers
            .Where(ss => ss.ParticipantId == me.ParticipantId
                         && ss.Session.EventId == me.EventId
                         && ss.Session.Type == ParticipantMasterClassType)
            .Select(ss => new { ss.Session.Id, ss.Session.Title })
            .ToListAsync(ct);

        MasterClasses = new List<MyMasterClass>();
        foreach (var mc in myMasterClasses)
        {
            var slug = await _logistics.EnsureSlugAsync(me.EventId, mc.Id, ct);
            var link = Url.PageLink(pageName: "/MasterClass/Index", values: new { slug }) ?? string.Empty;
            MasterClasses.Add(new MyMasterClass(mc.Id, mc.Title, slug, link));
        }
    }

    private const SessionType ParticipantMasterClassType = SessionType.CommunityMasterClass;
}
