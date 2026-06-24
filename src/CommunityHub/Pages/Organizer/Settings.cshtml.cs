using System.Reflection;
using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// The organizer Feature settings page (REQUIREMENTS §23) — the controlled-rollout
/// surface. It renders the feature catalog grouped into chapters; each advanced
/// feature gets an enable/disable toggle, and a disabled feature is shown DIMMED
/// with a small "Disabled" label (never hidden, for discoverability). It also
/// surfaces the first-class email controls: the global outbound-email kill switch,
/// plus the (read-only, infra-managed) allowlist + redirect for transparency.
///
/// Organizer-only (server-enforced), mobile-first (~360px), a11y, English.
/// State persists to the per-edition <see cref="FeatureSetting"/> store via
/// <see cref="FeatureSettingsService"/>; the same store the web + jobs gate on.
/// </summary>
[Authorize]
public class SettingsModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly FeatureSettingsService _settings;
    private readonly FeatureGateService _gate;
    private readonly EmailOptions _email;
    private readonly IWebHostEnvironment _env;

    public SettingsModel(
        ICurrentParticipantAccessor participant,
        FeatureSettingsService settings,
        FeatureGateService gate,
        IOptions<EmailOptions> email,
        IWebHostEnvironment env)
    {
        _participant = participant;
        _settings = settings;
        _gate = gate;
        _email = email.Value;
        _env = env;
    }

    public bool AccessDenied { get; private set; }
    public bool Saved { get; private set; }

    /// <summary>
    /// The deployed environment name (Development / Production). With the build
    /// stamp this answers "which build + env am I looking at" so dev-vs-prod is
    /// visible at a glance — rings tell you HOW FAR a feature is rolled out per
    /// env, the build tells you whether its CODE is here (REQUIREMENTS §23a: NO
    /// per-feature version — build version per env + ring state).
    /// </summary>
    public string EnvName => _env.EnvironmentName;

    /// <summary>
    /// The deployed build, as <c>v&lt;version&gt; (&lt;sha7&gt;)</c>, read from the
    /// entry assembly's <see cref="AssemblyInformationalVersionAttribute"/>
    /// (<c>1.0.0+&lt;gitsha&gt;</c>). One number per deployment — NOT per feature.
    /// </summary>
    public string BuildVersion
    {
        get
        {
            var info = Assembly.GetEntryAssembly()
                ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(info)) return "unknown";
            var plus = info.IndexOf('+');
            if (plus < 0) return $"v{info}";
            var ver = info[..plus];
            var sha = info[(plus + 1)..];
            var sha7 = sha.Length > 7 ? sha[..7] : sha;
            return $"v{ver} ({sha7})";
        }
    }

    /// <summary>The catalog states grouped by chapter, in display order.</summary>
    public IReadOnlyList<IGrouping<FeatureGroup, FeatureState>> Groups { get; private set; }
        = Array.Empty<IGrouping<FeatureGroup, FeatureState>>();

    /// <summary>Enabled-but-prerequisite-off warnings (feature, missing dependency).</summary>
    public IReadOnlyList<(FeatureDescriptor Feature, FeatureDescriptor Missing)> UnmetDependencies
    { get; private set; } = Array.Empty<(FeatureDescriptor, FeatureDescriptor)>();

    /// <summary>The effective lifecycle ring of every feature group (§23a) — the per-group control state.</summary>
    public IReadOnlyList<GroupRingState> GroupRings { get; private set; }
        = Array.Empty<GroupRingState>();

    /// <summary>True when all background jobs are currently PAUSED for this edition (master switch).</summary>
    public bool JobsPaused { get; private set; }

    /// <summary>The effective group ring for one group (for the GUI group header).</summary>
    public GroupRingState GroupRingFor(FeatureGroup g) =>
        GroupRings.FirstOrDefault(x => x.Group == g) ?? new GroupRingState(g, Ring.Broad, false);

    // --- Email controls (read-only context for the settings surface) ---------
    /// <summary>The process-wide config kill switch (forces email OFF regardless of the per-edition switch).</summary>
    public bool EmailConfigKillSwitch => _email.KillSwitch;
    /// <summary>The optional ring CEILING (DEV caps outbound email at e.g. Ring1). Empty = no ceiling (PROD).</summary>
    public string MaxReleaseRingDisplay => string.IsNullOrWhiteSpace(_email.MaxReleaseRing)
        ? string.Empty : _email.MaxReleaseRing;
    public string RedirectDisplay => string.IsNullOrWhiteSpace(_email.RedirectAllTo)
        ? string.Empty : _email.RedirectAllTo;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>
    /// Toggle one feature's kill switch. Posts the feature key + the new state;
    /// the service ignores core/unknown keys. After saving the page re-renders the
    /// full surface so the toggle, the dimmed state and any dependency warning
    /// reflect the new reality immediately.
    /// </summary>
    public async Task<IActionResult> OnPostToggleAsync(
        string key, bool enable, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        if (!string.IsNullOrWhiteSpace(key))
        {
            await _settings.SetEnabledAsync(me.EventId, key, enable, me.Email, ct);
            Saved = true;
        }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>
    /// Set one feature's RELEASED-TO ring (0–3) for this edition — the
    /// progressive-rollout control (REQUIREMENTS §23). A feature is active for a
    /// resource only when its effective ring ≤ this released ring.
    /// </summary>
    public async Task<IActionResult> OnPostReleaseRingAsync(
        string key, Ring ring, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        if (!string.IsNullOrWhiteSpace(key))
        {
            await _settings.SetReleasedRingAsync(me.EventId, key, ring, me.Email, ct);
            Saved = true;
        }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>
    /// Set a whole feature GROUP's lifecycle ring (§23a) — the primary rollout
    /// control; every feature in the group without its own override inherits it.
    /// </summary>
    public async Task<IActionResult> OnPostGroupRingAsync(
        FeatureGroup group, Ring ring, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        await _settings.SetGroupRingAsync(me.EventId, group, ring, me.Email, ct);
        Saved = true;
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>
    /// Clear a feature's ring override so it adopts its (effective) group's ring
    /// again (§23a "adopt the group's lifecycle").
    /// </summary>
    public async Task<IActionResult> OnPostInheritGroupRingAsync(string key, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        if (!string.IsNullOrWhiteSpace(key))
        {
            await _settings.ClearReleasedRingOverrideAsync(me.EventId, key, me.Email, ct);
            Saved = true;
        }
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>
    /// RE-HOME / graduate a feature into a different group (§23a) — it then adopts
    /// the destination group's lifecycle ring.
    /// </summary>
    public async Task<IActionResult> OnPostFeatureGroupAsync(
        string key, FeatureGroup group, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        if (!string.IsNullOrWhiteSpace(key))
        {
            await _settings.SetFeatureGroupAsync(me.EventId, key, group, me.Email, ct);
            Saved = true;
        }
        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>
    /// PAUSE or RESUME all background jobs for this edition — the master switch.
    /// Every timer job consults this (via JobsPauseMiddleware) and no-ops while
    /// paused; resume takes effect on each job's next tick.
    /// </summary>
    public async Task<IActionResult> OnPostSetJobsPausedAsync(bool paused, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        await _settings.SetJobsPausedAsync(me.EventId, paused, me.Email, ct);
        Saved = true;

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    private async Task LoadAsync(int eventId, CancellationToken ct)
    {
        Groups = await _settings.GetByGroupAsync(eventId, ct);
        UnmetDependencies = await _settings.GetUnmetDependenciesAsync(eventId, ct);
        GroupRings = await _settings.GetGroupRingsAsync(eventId, ct);
        JobsPaused = await _gate.AreJobsPausedAsync(eventId, ct);
    }
}
