using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.DocLibrary;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// §769 / work-order §6.1 — THE DOCUMENT LIBRARY PATHS PAGE.
/// </summary>
/// <remarks>
/// <para>Work order: <i>"the highest-value item here: it is the tool that would have prevented this
/// entire situation, and it makes everything after it verifiable."</i> Every folder the product reads
/// or writes, what it resolves to, whether it really exists, who changed it and when.</para>
///
/// <para>🔒 <b>Why "Test path" is the point of the page.</b> A wrong document-library path does not
/// throw: the store returns an empty listing and every caller reads it as "nothing to do". §767's
/// sweep ran four production cycles reporting healthy zeros against a folder full of photos. This is
/// the one screen that goes and looks, and that can tell EMPTY from MISSING.</para>
///
/// <para>🔒 <b>The root is deliberately read-only</b> (§769.1 D2). It is the PROD/DEV boundary: point
/// production's root at the DEV tree and a sweep overwrites live artwork. Changing it takes a
/// deliberate act in Azure, not a typo in a browser.</para>
/// </remarks>
[Authorize]
public class DocLibraryPathsModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly IDocLibraryPathResolver _paths;
    private readonly DocLibraryOverrideStore _store;
    private readonly DocLibraryPathTester _tester;
    private readonly IOptionsMonitor<DocLibraryOptions> _options;
    private readonly CommunityHubDbContext _db;

    public DocLibraryPathsModel(
        ICurrentParticipantAccessor participant,
        IDocLibraryPathResolver paths,
        DocLibraryOverrideStore store,
        DocLibraryPathTester tester,
        IOptionsMonitor<DocLibraryOptions> options,
        CommunityHubDbContext db)
    {
        _participant = participant;
        _paths = paths;
        _store = store;
        _tester = tester;
        _options = options;
        _db = db;
    }

    public bool AccessDenied { get; private set; }

    /// <summary>Every registered key, resolved, in registry order.</summary>
    public IReadOnlyList<DocLibraryResolvedPath> Rows { get; private set; } = [];

    /// <summary>The configured root — shown, never editable here.</summary>
    public string Root => _options.CurrentValue.RootFolderPath;

    public string SiteUrl => _options.CurrentValue.SiteUrl;
    public string DriveName => _options.CurrentValue.DriveName;
    public bool LibraryConfigured => _paths.IsConfigured;

    /// <summary>Startup-check problems, shown where the operator can act on them.</summary>
    public IReadOnlyList<string> Problems { get; private set; } = [];

    /// <summary>Saved file-name settings (work-order §6.1: the template background + wordmark).</summary>
    public IReadOnlyList<DocLibraryFileNameRow> FileNames { get; private set; } = [];

    /// <summary>Change history, newest first.</summary>
    public IReadOnlyList<DocLibrarySettingChange> History { get; private set; } = [];

    /// <summary>The edition's short name (work-order §6.1) — read-only context.</summary>
    public string? EventShortName { get; private set; }

    /// <summary>The result of the last single-row test, if one was run.</summary>
    public DocLibraryPathTestResult? SingleTest { get; private set; }

    /// <summary>The "Test all" dashboard, when it has been run.</summary>
    public IReadOnlyList<DocLibraryPathTestResult>? AllTests { get; private set; }

    public string? ErrorMessage { get; private set; }
    public string? SavedMessage { get; private set; }

    /// <summary>The key whose editor should be highlighted after a post.</summary>
    public string? FocusKey { get; private set; }

    [BindProperty] public string? Key { get; set; }
    [BindProperty] public string? Value { get; set; }
    [BindProperty] public DocLibrarySettingKind Kind { get; set; }

    /// <summary>One editable static file name.</summary>
    /// <param name="Key">The STORAGE key (e.g. <c>TemplateWordmark</c>) — never shown to the operator.</param>
    /// <param name="Label">§783.11 — the operator-facing name (e.g. "Logo").</param>
    /// <param name="ShippedDefault">The name used when nothing is set, or null when the hub guesses.</param>
    public sealed record DocLibraryFileNameRow(
        string Key, string Label, string? Value, bool IsOverridden, string Purpose,
        string? ShippedDefault);

    /// <summary>
    /// Is this key one the product uses TODAY? A Planned or Reserved folder legitimately may not
    /// exist yet, so counting it as a failure in the dashboard would teach the operator to ignore
    /// red — which is the one thing a health check must never do.
    /// </summary>
    public bool IsInUse(string key) =>
        DocLibraryPaths.Find(key)?.Status == DocLibraryPathStatus.Active;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!Gate()) return GateResult();
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct)
    {
        if (!Gate()) return GateResult();

        var key = (Key ?? string.Empty).Trim();
        FocusKey = key;

        if (Kind == DocLibrarySettingKind.Path && DocLibraryPaths.Find(key) is null)
        {
            ErrorMessage = $"'{key}' is not a registered path.";
            await LoadAsync(ct);
            return Page();
        }

        var error = Kind == DocLibrarySettingKind.FileName
            ? DocLibraryValueValidator.ValidateFileName(Value)
            : DocLibraryValueValidator.ValidatePath(Value);

        if (error is not null)
        {
            ErrorMessage = error;
            await LoadAsync(ct);
            return Page();
        }

        var changed = await _store.SetAsync(Kind, key, Value, _participant.Current?.Email, ct);
        SavedMessage = changed
            ? $"{key} saved. It is in effect for this site now, and for the background jobs within "
              + $"{(int)DocLibraryOverrideCache.Ttl.TotalSeconds} seconds. Use Test to confirm the "
              + "folder really exists."
            : $"{key} was already set to that value — nothing changed.";

        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostRestoreAsync(CancellationToken ct)
    {
        if (!Gate()) return GateResult();

        var key = (Key ?? string.Empty).Trim();
        FocusKey = key;

        // A blank value IS "restore the default" in the store — the row is deleted rather than
        // written with the default's text, so a later change to the registry still reaches here.
        var changed = await _store.SetAsync(Kind, key, null, _participant.Current?.Email, ct);
        SavedMessage = changed
            ? $"{key} is back on its built-in default."
            : $"{key} was already on its built-in default.";

        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostTestAsync(CancellationToken ct)
    {
        if (!Gate()) return GateResult();

        var key = (Key ?? string.Empty).Trim();
        FocusKey = key;
        SingleTest = await _tester.TestAsync(key, ct);

        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostTestAllAsync(CancellationToken ct)
    {
        if (!Gate()) return GateResult();

        AllTests = await _tester.TestAllAsync(ct);
        await LoadAsync(ct);
        return Page();
    }

    private bool Gate()
    {
        // Each handler starts from a clean slate: a message left over from an earlier attempt is a
        // page that says "not allowed" about something that just succeeded.
        ErrorMessage = null;
        SavedMessage = null;

        var me = _participant.Current;
        if (me is null) return false;
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return false; }
        return true;
    }

    private IActionResult GateResult() =>
        AccessDenied ? Page() : RedirectToPage("/Login");

    private async Task LoadAsync(CancellationToken ct)
    {
        Rows = _paths.All();
        Problems = _paths.Validate();
        History = await _store.HistoryAsync(50, ct);

        var overrides = await _store.AllAsync(ct);
        var savedNames = overrides
            .Where(o => o.Kind == DocLibrarySettingKind.FileName)
            .ToDictionary(o => o.Key, o => o.Value, StringComparer.OrdinalIgnoreCase);

        // The static file names the work order names explicitly. They are listed here rather than
        // discovered, because a file name only matters where the code asks for one by key.
        var names = new List<DocLibraryFileNameRow>();
        foreach (var (key, label, purpose) in DocLibraryFileNames.Editable)
        {
            _paths.TryResolveFileName(key, out var effective);
            names.Add(new DocLibraryFileNameRow(
                key,
                label,
                string.IsNullOrWhiteSpace(effective) ? null : effective,
                savedNames.ContainsKey(key),
                purpose,
                DocLibraryFileNames.DefaultFor(key)));
        }
        FileNames = names;

        EventShortName = await _db.Events
            .Where(e => e.IsActive)
            .Select(e => e.Code)
            .FirstOrDefaultAsync(ct);
    }
}
