using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Evaluation;
using CommunityHub.Core.Signage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Signage;

/// <summary>
/// §754 — the venue screens. ONE page model serves all eight documented URLs:
/// <c>/signage/{portrait|landscape}/{rotate|now|next|feedback}</c>.
/// </summary>
/// <remarks>
/// <para>🔒 <b>Anonymous BY NECESSITY.</b> An OptiSigns player has no browser session, no keyboard
/// and no way to complete a login. The unguessable <c>?t=</c> token is the whole credential, exactly
/// as it is for <c>/f/{token}</c>.</para>
///
/// <para>🔒 <b>This page NEVER returns an error status.</b> Not 401, not 404, not 500. Every refusal
/// and every fault renders the holding screen at HTTP 200. A player cannot tell a human that an
/// asset failed — it displays whatever came back, so an error page is not a safety net here, it is
/// the failure, rendered two metres tall in a public corridor.</para>
///
/// <para>🔑 <b>Why the data arrives by fetch rather than being rendered server-side.</b> §5 requires
/// the PLAYER's clock to drive the hour rollover, unattended, with no reload for 12+ hours. The page
/// therefore asks for its content repeatedly, passing its OWN clock as <c>at</c>, and the slot is
/// computed by the tested <see cref="SignageSlotBuilder"/> on the server. That keeps the §5–§7 rules
/// in exactly one implementation: a second copy in JavaScript would be the thing that eventually
/// disagrees with the speaker's own schedule.</para>
/// </remarks>
[AllowAnonymous]
public class ScreenModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly EvaluationScoreService _scores;
    private readonly EventEditionConfigLoader _config;
    private readonly EventConfigOptions _configOptions;
    private readonly TimeProvider _clock;
    private readonly ILogger<ScreenModel> _log;

    public ScreenModel(
        CommunityHubDbContext db, EvaluationScoreService scores,
        EventEditionConfigLoader config, EventConfigOptions configOptions,
        ILogger<ScreenModel> log, TimeProvider? clock = null)
    {
        _db = db; _scores = scores; _config = config; _configOptions = configOptions; _log = log;
        _clock = clock ?? TimeProvider.System;
    }

    public SignageOrientation Orientation { get; private set; } = SignageOrientation.Landscape;

    /// <summary>The view this URL pins, or null for the rotator (which cycles all three).</summary>
    public SignageView? PinnedView { get; private set; }

    /// <summary>True when the URL is the rotator (<c>/rotate</c>) rather than a single view.</summary>
    public bool IsRotator => PinnedView is null;

    public bool ShowHolding { get; private set; }
    public string EventDisplayName { get; private set; } = string.Empty;

    /// <summary>The holding screen's one line of information (§10: "Day 2 starts 09:00").</summary>
    public string? HoldingLine { get; private set; }

    public int RotateSeconds { get; private set; } = 10;
    public int PageSeconds { get; private set; } = 5;
    public int Columns { get; private set; } = 5;
    public int Rows { get; private set; } = 3;
    public int Gap { get; private set; } = 35;
    public int RowGap { get; private set; } = 80;

    public string Token { get; private set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(
        string orientation, string view, [FromQuery(Name = "t")] string? t, CancellationToken ct)
    {
        Token = t ?? string.Empty;
        Orientation = ParseOrientation(orientation);
        PinnedView = ParseView(view);

        // An unrecognised view segment is a typo in a playlist, not an attack — hold, don't 404.
        var known = string.Equals(view, "rotate", StringComparison.OrdinalIgnoreCase)
                    || PinnedView is not null;

        var ctx = await LoadContextAsync(ct);
        EventDisplayName = ctx.DisplayName;
        ApplyLayout(ctx.Settings);

        // The rotator is allowed when ANY of its three views is; each view is re-checked when its
        // data is fetched, so a switched-off view is skipped rather than blanking the whole screen.
        var decision = known
            ? CheckAccess(ctx, PinnedView ?? SignageView.Now, t)
            : SignageAccessService.Decision.Holding(SignageAccessService.HoldingReason.NotConfigured);

        if (IsRotator && known && !decision.ShowContent)
        {
            decision = AnyViewAllowed(ctx, t)
                ? SignageAccessService.Decision.Allowed
                : decision;
        }

        ShowHolding = !decision.ShowContent;
        if (ShowHolding)
        {
            HoldingLine = await BuildHoldingLineAsync(ctx, ct);
            // Logged, never shown: "invalid token" on a wall tells a passer-by there is one to guess.
            _log.LogInformation(
                "Signage holding: {Orientation}/{View} — {Reason}", orientation, view, decision.Reason);
        }

        return Page();
    }

    /// <summary>
    /// The content feed. <c>at</c> is the PLAYER's clock (§5) — the schedule check still uses the
    /// server's, because the schedule is an operator control and must not be movable by a screen.
    /// </summary>
    public async Task<IActionResult> OnGetDataAsync(
        string orientation, string view, [FromQuery(Name = "t")] string? t,
        [FromQuery] string? at, [FromQuery] string? only, CancellationToken ct)
    {
        var o = ParseOrientation(orientation);
        var requested = ParseView(only) ?? ParseView(view) ?? SignageView.Now;
        var ctx = await LoadContextAsync(ct);

        var decision = CheckAccess(ctx, requested, t);
        if (!decision.ShowContent)
        {
            // 200 with holding:true — the page decides what to draw. A 403 here would make the
            // player's own error handling the thing on screen.
            return new JsonResult(new { holding = true, line = await BuildHoldingLineAsync(ctx, ct) });
        }

        var playerNow = DateTimeOffset.TryParse(
            at, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : _clock.GetUtcNow();

        if (requested == SignageView.Feedback) return new JsonResult(await BuildFeedbackAsync(ctx, ct));

        return new JsonResult(await BuildAgendaAsync(
            ctx, o, playerNow, hoursAhead: requested == SignageView.Next ? 1 : 0, ct));
    }

    // -------------------------------------------------------------------------

    private sealed record Context(
        int? EventId, string DisplayName, string? TimezoneId,
        Core.Domain.Signage.SignageSettings? Settings);

    private async Task<Context> LoadContextAsync(CancellationToken ct)
    {
        var active = await _db.Events
            .Where(e => e.IsActive)
            .OrderByDescending(e => e.Id)
            .Select(e => new { e.Id, e.DisplayName })
            .FirstOrDefaultAsync(ct);

        if (active is null) return new Context(null, string.Empty, null, null);

        var settings = await _db.SignageSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.EventId == active.Id, ct);

        string? tz = null;
        try { tz = _config.Load(_configOptions.EventConfigPath).Dates?.Timezone; }
        catch (Exception ex)
        {
            // A missing edition config must never take the wall down; UTC is the honest fallback.
            _log.LogWarning(ex, "Signage: edition config unreadable; falling back to UTC.");
        }

        return new Context(active.Id, active.DisplayName, tz, settings);
    }

    private SignageAccessService.Decision CheckAccess(Context ctx, SignageView view, string? token) =>
        SignageAccessService.Check(
            ctx.Settings, Orientation, view, token, _clock.GetUtcNow(), ctx.TimezoneId);

    private bool AnyViewAllowed(Context ctx, string? token) =>
        Enum.GetValues<SignageView>().Any(v => CheckAccess(ctx, v, token).ShowContent);

    private void ApplyLayout(Core.Domain.Signage.SignageSettings? s)
    {
        s ??= new Core.Domain.Signage.SignageSettings();
        RotateSeconds = Math.Max(1, s.RotateSeconds);
        PageSeconds = Math.Max(1, s.PageSeconds);
        if (Orientation == SignageOrientation.Portrait)
        {
            Columns = Math.Max(1, s.PortraitColumns);
            Rows = Math.Max(1, s.PortraitRows);
            Gap = Math.Max(0, s.PortraitGap);
            RowGap = Math.Max(0, s.PortraitRowGap);
        }
        else
        {
            Columns = Math.Max(1, s.LandscapeColumns);
            Rows = Math.Max(1, s.LandscapeRows);
            Gap = Math.Max(0, s.LandscapeGap);
            RowGap = Math.Max(0, s.LandscapeRowGap);
        }
    }

    private async Task<object> BuildAgendaAsync(
        Context ctx, SignageOrientation o, DateTimeOffset at, int hoursAhead, CancellationToken ct)
    {
        if (ctx.EventId is null) return new { holding = false, pages = Array.Empty<object>() };

        var zone = EventLocalTime.Resolve(ctx.TimezoneId);
        var settings = ctx.Settings ?? new Core.Domain.Signage.SignageSettings();

        // Only the two hours either side of the request can possibly be on screen — a whole
        // edition's agenda is pulled into memory otherwise, every few seconds, from 15 screens.
        var (windowStart, _) = SignageSlotBuilder.HourSlot(at, zone);
        var windowEnd = windowStart.AddHours(2);

        var activities = await _db.AgendaActivities
            .AsNoTracking()
            .Where(a => a.EventId == ctx.EventId && a.StartsAt < windowEnd && a.EndsAt > windowStart)
            .ToListAsync(ct);

        var slot = SignageSlotBuilder.Build(
            activities, at, zone, SignageSlotBuilder.PageSize(settings, o), hoursAhead);

        return new
        {
            holding = false,
            slotStart = slot.StartsAt.ToString("HH:mm"),
            slotEnd = slot.EndsAt.ToString("HH:mm"),
            pages = slot.Pages.Select(p => new
            {
                number = p.Number,
                of = p.Of,
                cards = p.Cards.Select(c => new
                {
                    time = $"{TimeZoneInfo.ConvertTime(c.StartsAt, zone):HH\\:mm} - {TimeZoneInfo.ConvertTime(c.EndsAt, zone):HH\\:mm}",
                    track = c.Track,
                    title = c.Title,
                    speakers = c.Speakers,
                    room = c.Room,
                }).ToList(),
            }).ToList(),
        };
    }

    private async Task<object> BuildFeedbackAsync(Context ctx, CancellationToken ct)
    {
        if (ctx.EventId is null) return new { holding = false, responses = 0 };

        // §754.1 — the event-wide pooled figure publishes from the FIRST response; the 10-response
        // floor is a per-session protection and does not apply here.
        var (pooled, _, _) = await _scores.ForEventAsync(ctx.EventId.Value, ct: ct);
        var d = pooled.Distribution;
        var total = Math.Max(1, d.Total);

        return new
        {
            holding = false,
            score = pooled.Score,
            scoreColour = SignagePalette.ScoreHex(pooled.Score),
            // 🔒 Never a bare band and never a % sign (§754.2 / brief §11): the band with its RANGE
            // is what tells a passer-by the scale without teaching them what an index is.
            band = SatisfactionScore.BandFor(pooled.Score),
            bandRange = SatisfactionScore.BandRange(pooled.Score),
            responses = pooled.Responses,
            ratings = new[] { 4, 3, 2, 1 }.Select(r => new
            {
                label = EvaluationRatingLabels.Label(r),
                count = d.CountOf(r),
                // The spec asks for the percentage BESIDE the count: a reader of the PDF can do the
                // arithmetic, a passer-by cannot.
                percent = (int)Math.Round(d.CountOf(r) * 100d / total, MidpointRounding.AwayFromZero),
                colour = SignagePalette.RatingHex(r),
            }).ToList(),
        };
    }

    /// <summary>
    /// §10 — the holding screen's line: the next day of the programme, from the cached agenda.
    /// </summary>
    /// <remarks>
    /// 🔑 Derived from `AgendaActivities` rather than the edition's configured dates, because the
    /// holding screen's whole job is to be right about "when does something happen next" — and the
    /// agenda is the thing that actually knows. It degrades to no line at all rather than to a
    /// guess: a wrong time on a holding screen sends people to a room at the wrong hour.
    /// </remarks>
    private async Task<string?> BuildHoldingLineAsync(Context ctx, CancellationToken ct)
    {
        if (ctx.EventId is null) return null;

        var now = _clock.GetUtcNow();
        var next = await _db.AgendaActivities
            .AsNoTracking()
            .Where(a => a.EventId == ctx.EventId && a.StartsAt > now)
            .OrderBy(a => a.StartsAt)
            .Select(a => (DateTimeOffset?)a.StartsAt)
            .FirstOrDefaultAsync(ct);

        if (next is null) return null;

        var local = EventLocalTime.ToLocal(next.Value, ctx.TimezoneId);
        return $"Next: {local:dddd HH:mm}";
    }

    private static SignageOrientation ParseOrientation(string? value) =>
        string.Equals(value, "portrait", StringComparison.OrdinalIgnoreCase)
            ? SignageOrientation.Portrait
            : SignageOrientation.Landscape;

    private static SignageView? ParseView(string? value) => value?.ToLowerInvariant() switch
    {
        "now" => SignageView.Now,
        "next" => SignageView.Next,
        "feedback" => SignageView.Feedback,
        _ => null,
    };
}
