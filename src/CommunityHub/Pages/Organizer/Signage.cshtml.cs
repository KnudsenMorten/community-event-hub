using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Signage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// §754 deliverables 4 + 5 — the venue-screen control panel: the URLs to paste into OptiSigns, the
/// tokens behind them, the on/off switches, the schedule, the tunable layout/timing values, and the
/// agenda sync health.
/// </summary>
/// <remarks>
/// <para>🔒 <b>Organizer-only, and it must never follow the screens out.</b> The signage pages are
/// anonymous by necessity; this is the page that MINTS their credentials. §753's rule applies
/// exactly: the endpoint may be public, the page that issues its keys never is.</para>
///
/// <para>🔑 <b>The URLs are shown assembled and complete</b>, token included, because the operator's
/// job here is to copy one string into an OptiSigns playlist. A page that showed the token separately
/// and asked him to build the URL would be the step where a wall silently fails.</para>
/// </remarks>
[Authorize]
public class SignageModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly TimeProvider _clock;

    public SignageModel(
        CommunityHubDbContext db, ICurrentParticipantAccessor participant, TimeProvider? clock = null)
    {
        _db = db; _participant = participant; _clock = clock ?? TimeProvider.System;
    }

    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }
    public Core.Domain.Signage.SignageSettings Settings { get; private set; } = new();

    /// <summary>§4 sync health: how many activities are cached and when they were last confirmed.</summary>
    public int CachedActivities { get; private set; }
    public DateTimeOffset? LastSyncedAt { get; private set; }

    /// <summary>True when the agenda mirror's feature switch is off — the screens will go stale.</summary>
    public bool SyncSwitchedOff { get; private set; }

    /// <summary>The eight URLs of §3, assembled and ready to paste.</summary>
    public IReadOnlyList<(string Label, string Url)> PlaylistUrls { get; private set; }
        = Array.Empty<(string, string)>();

    public async Task<IActionResult> OnGetAsync(string? msg, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        Message = msg;
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostRotateTokensAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var s = await GetOrCreateAsync(me.EventId, ct);
        s.PortraitToken = SignageAccessService.NewToken();
        s.LandscapeToken = SignageAccessService.NewToken();
        s.TokensRotatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);

        return RedirectToPage(new
        {
            msg = "New tokens generated. ⚠ The OLD URLs stop working immediately — "
                + "paste the new ones into both OptiSigns playlists.",
        });
    }

    public async Task<IActionResult> OnPostSaveAsync(
        Core.Domain.Signage.SignageSettings input, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var s = await GetOrCreateAsync(me.EventId, ct);

        s.PortraitNowEnabled = input.PortraitNowEnabled;
        s.PortraitNextEnabled = input.PortraitNextEnabled;
        s.PortraitFeedbackEnabled = input.PortraitFeedbackEnabled;
        s.LandscapeNowEnabled = input.LandscapeNowEnabled;
        s.LandscapeNextEnabled = input.LandscapeNextEnabled;
        s.LandscapeFeedbackEnabled = input.LandscapeFeedbackEnabled;

        s.DailyFromLocal = input.DailyFromLocal;
        s.DailyToLocal = input.DailyToLocal;
        s.ActiveFromLocal = input.ActiveFromLocal;
        s.ActiveToLocal = input.ActiveToLocal;

        // 🔒 Floors enforced SERVER-SIDE. A browser is not a validator, and a rotation of 0 seconds
        // is not a slow screen — it is a screen that never stops re-rendering, in a public corridor,
        // unattended. The grid floors exist for the same reason: 0 columns renders nothing at all.
        s.RotateSeconds = Math.Clamp(input.RotateSeconds, 3, 600);
        s.PageSeconds = Math.Clamp(input.PageSeconds, 2, 600);
        s.PortraitColumns = Math.Clamp(input.PortraitColumns, 1, 8);
        s.PortraitRows = Math.Clamp(input.PortraitRows, 1, 12);
        s.LandscapeColumns = Math.Clamp(input.LandscapeColumns, 1, 10);
        s.LandscapeRows = Math.Clamp(input.LandscapeRows, 1, 12);
        s.PortraitGap = Math.Clamp(input.PortraitGap, 0, 200);
        s.LandscapeGap = Math.Clamp(input.LandscapeGap, 0, 400);
        s.PortraitRowGap = Math.Clamp(input.PortraitRowGap, 0, 400);
        s.LandscapeRowGap = Math.Clamp(input.LandscapeRowGap, 0, 600);

        await _db.SaveChangesAsync(ct);
        return RedirectToPage(new { msg = "Signage settings saved." });
    }

    private async Task<Core.Domain.Signage.SignageSettings> GetOrCreateAsync(
        int eventId, CancellationToken ct)
    {
        var s = await _db.SignageSettings.FirstOrDefaultAsync(x => x.EventId == eventId, ct);
        if (s is not null) return s;

        // First visit mints the tokens: a settings row with blank tokens would be a page full of
        // URLs that can never work, and the access check treats a blank token as "not configured".
        s = new Core.Domain.Signage.SignageSettings
        {
            EventId = eventId,
            PortraitToken = SignageAccessService.NewToken(),
            LandscapeToken = SignageAccessService.NewToken(),
            TokensRotatedAt = _clock.GetUtcNow(),
        };
        _db.SignageSettings.Add(s);
        await _db.SaveChangesAsync(ct);
        return s;
    }

    private async Task LoadAsync(int eventId, CancellationToken ct)
    {
        Settings = await GetOrCreateAsync(eventId, ct);

        CachedActivities = await _db.AgendaActivities.CountAsync(a => a.EventId == eventId, ct);
        LastSyncedAt = await _db.AgendaActivities
            .Where(a => a.EventId == eventId)
            .OrderByDescending(a => a.LastSyncedAt)
            .Select(a => (DateTimeOffset?)a.LastSyncedAt)
            .FirstOrDefaultAsync(ct);

        // The switch and the cache are separate facts, and the page states both: a full cache with
        // the switch off is the state that looks healthy and is slowly going stale.
        var stored = await _db.FeatureSettings
            .Where(f => f.EventId == eventId && f.FeatureKey == "signage-agenda-sync")
            .Select(f => (bool?)f.Enabled)
            .FirstOrDefaultAsync(ct);
        SyncSwitchedOff = !(stored ?? Core.Settings.FeatureCatalog.DefaultEnabled("signage-agenda-sync"));

        var origin = $"{Request.Scheme}://{Request.Host}";
        var urls = new List<(string, string)>();
        foreach (var (orientation, token) in new[]
                 {
                     ("portrait", Settings.PortraitToken),
                     ("landscape", Settings.LandscapeToken),
                 })
        {
            // The ROTATOR first — it is the one that goes in the playlist (§3); the three pinned
            // views are the exception, for a screen dedicated to one of them.
            foreach (var view in new[] { "rotate", "now", "next", "feedback" })
            {
                urls.Add((
                    $"{orientation} · {view}",
                    $"{origin}/signage/{orientation}/{view}?t={Uri.EscapeDataString(token)}"));
            }
        }
        PlaylistUrls = urls;
    }
}
