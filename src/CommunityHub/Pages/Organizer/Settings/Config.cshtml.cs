using CommunityHub.Auth;
using CommunityHub.Core.Config;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer.Settings;

/// <summary>
/// The organizer "Configuration" editor (admin-editable config, Phase 2): a
/// super-admin GUI to edit the SCALAR config values of the active edition —
/// grouped by the three editable sections (Event / Sponsor / Integrations). For
/// each scalar leaf it shows the label, the current EFFECTIVE value (shipped
/// default deep-merged with any per-edition override), whether it is currently
/// OVERRIDDEN vs the shipped default, an input to change it, and a per-field
/// "Reset to default".
///
/// <para>Saving deep-merges the changed key into the existing
/// <see cref="ConfigOverride"/> fragment for (active edition, section) via
/// <see cref="ConfigOverrideStore"/> (sibling keys survive); reset removes that
/// one key (and deletes the row when nothing is left). The store invalidates its
/// cache on upsert/delete so the new effective config is live WITHOUT a
/// redeploy.</para>
///
/// <para>FAIL-SAFE: input is validated per field (URLs must be valid http(s) or
/// blank; numbers must parse) and errors show inline — a bad save never reaches
/// the store and never 500s the site. SECRETS are never editable here (see
/// <see cref="ConfigScalarEditor.IsExcludedKey"/>); they stay in Key Vault.</para>
///
/// Organizer-only (server-enforced via <see cref="OrganizerAuth.IsRealOrganizer"/>
/// for writes, role-gated for the view), mobile-first (~360px), English. DEFERRED:
/// lists/arrays (Phase 3); ring-gating the editor + clone-edition + GUI email
/// templates (Phase 4).
/// </summary>
[Authorize]
public class ConfigModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly ConfigOverrideStore _store;
    private readonly EventConfigOptions _eventOptions;
    private readonly SponsorConfigOptions _sponsorOptions;
    private readonly IntegrationsConfigOptions _integrationsOptions;
    private readonly ILogger<ConfigModel> _logger;

    public ConfigModel(
        ICurrentParticipantAccessor participant,
        ConfigOverrideStore store,
        EventConfigOptions eventOptions,
        SponsorConfigOptions sponsorOptions,
        IntegrationsConfigOptions integrationsOptions,
        ILogger<ConfigModel> logger)
    {
        _participant = participant;
        _store = store;
        _eventOptions = eventOptions;
        _sponsorOptions = sponsorOptions;
        _integrationsOptions = integrationsOptions;
        _logger = logger;
    }

    public bool AccessDenied { get; private set; }
    public bool Saved { get; private set; }
    public bool Reset { get; private set; }

    /// <summary>An inline, per-field validation error to surface (path + message).</summary>
    public string? ErrorPath { get; private set; }
    public string? ErrorMessage { get; private set; }

    /// <summary>The editable scalar fields per section, in section display order.</summary>
    public IReadOnlyList<SectionFields> Sections { get; private set; } =
        Array.Empty<SectionFields>();

    /// <summary>One section's resolved scalar fields for the view.</summary>
    public sealed record SectionFields(
        ConfigSection Section, string TitleKey, IReadOnlyList<ScalarField> Fields);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>
    /// Save one changed scalar leaf: validate, deep-merge it into the existing
    /// override fragment (sibling keys preserved), and persist via the store
    /// (which invalidates the cache). On a validation failure nothing is written
    /// and the error is shown inline against the field.
    /// </summary>
    public async Task<IActionResult> OnPostSaveAsync(
        string section, string path, string? value, int kind, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        if (!TryResolve(section, path, kind, out var configSection, out var scalarKind))
        {
            await LoadAsync(me.EventId, ct);
            return Page();
        }

        var posted = value ?? string.Empty;

        // FAIL-SAFE: validate first; a bad value never reaches the store.
        var error = ConfigScalarEditor.Validate(posted, scalarKind);
        if (error is not null)
        {
            ErrorPath = path;
            ErrorMessage = error;
            await LoadAsync(me.EventId, ct);
            return Page();
        }

        try
        {
            var existing = await _store.GetOverrideJsonAsync(me.EventId, configSection, ct);
            var merged = ConfigScalarEditor.ApplyChange(existing, path, posted, scalarKind);
            await _store.UpsertAsync(me.EventId, configSection, merged, me.Email, ct);
            Saved = true;
        }
        catch (Exception ex)
        {
            // Belt-and-braces: never 500 the site on a save problem.
            _logger.LogWarning(ex,
                "Config editor: save failed for {Section}.{Path}", section, path);
            ErrorPath = path;
            ErrorMessage = "Could not save that value. Please check it and try again.";
        }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>
    /// Reset one scalar leaf to its shipped default by removing that key from the
    /// override fragment. When the fragment becomes empty the whole row is deleted
    /// (effective config returns byte-for-byte to the shipped default).
    /// </summary>
    public async Task<IActionResult> OnPostResetAsync(
        string section, string path, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        if (!TryResolve(section, path, kind: 0, out var configSection, out _))
        {
            await LoadAsync(me.EventId, ct);
            return Page();
        }

        try
        {
            var existing = await _store.GetOverrideJsonAsync(me.EventId, configSection, ct);
            var remaining = ConfigScalarEditor.RemovePath(existing, path);
            if (string.IsNullOrWhiteSpace(remaining))
            {
                await _store.DeleteAsync(me.EventId, configSection, ct);
            }
            else
            {
                await _store.UpsertAsync(me.EventId, configSection, remaining, me.Email, ct);
            }
            Reset = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Config editor: reset failed for {Section}.{Path}", section, path);
            ErrorPath = path;
            ErrorMessage = "Could not reset that value. Please try again.";
        }

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    private static bool TryResolve(
        string section, string path, int kind,
        out ConfigSection configSection, out ScalarKind scalarKind)
    {
        scalarKind = Enum.IsDefined(typeof(ScalarKind), kind)
            ? (ScalarKind)kind : ScalarKind.String;
        configSection = default;

        if (!Enum.TryParse(section, ignoreCase: true, out configSection))
        {
            return false;
        }
        // Guard: never accept a posted path whose last key is excluded (secret /
        // doc) — defence in depth even though the editor never renders one.
        if (string.IsNullOrWhiteSpace(path)) return false;
        var lastKey = path.Split('.')[^1];
        return !ConfigScalarEditor.IsExcludedKey(lastKey);
    }

    private async Task LoadAsync(int eventId, CancellationToken ct)
    {
        var sections = new List<SectionFields>(3);
        sections.Add(await BuildSectionAsync(
            eventId, ConfigSection.Event, _eventOptions.EventConfigPath,
            "ConfigEditor.Section.Event", ct));
        sections.Add(await BuildSectionAsync(
            eventId, ConfigSection.Sponsor, _sponsorOptions.SponsorConfigPath,
            "ConfigEditor.Section.Sponsor", ct));
        sections.Add(await BuildSectionAsync(
            eventId, ConfigSection.Integrations, _integrationsOptions.IntegrationsConfigPath,
            "ConfigEditor.Section.Integrations", ct));
        Sections = sections;
    }

    private async Task<SectionFields> BuildSectionAsync(
        int eventId, ConfigSection section, string path, string titleKey,
        CancellationToken ct)
    {
        IReadOnlyList<ScalarField> fields;
        try
        {
            var defaultJson = System.IO.File.Exists(path)
                ? await System.IO.File.ReadAllTextAsync(path, ct)
                : "{}";
            var overrideJson = await _store.GetOverrideJsonAsync(eventId, section, ct);
            fields = ConfigScalarEditor.Enumerate(defaultJson, overrideJson);
        }
        catch (Exception ex)
        {
            // A broken config file must not break the editor — show no fields.
            _logger.LogWarning(ex,
                "Config editor: failed to read section {Section} from {Path}",
                section, path);
            fields = Array.Empty<ScalarField>();
        }
        return new SectionFields(section, titleKey, fields);
    }
}
