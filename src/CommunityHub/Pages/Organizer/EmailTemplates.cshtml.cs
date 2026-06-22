using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// The organizer EMAIL-TEMPLATE editor (REQUIREMENTS §25h): verify + modify every email
/// template's text per edition, preview it, reset to the shipped default, and dial its
/// release ring — all in-portal, no deploy. The shipped on-disk template is the default;
/// a saved edit is stored as an <see cref="EmailTemplateOverride"/> and WINS at send +
/// preview time. Organizer-only (role-gated view; <see cref="OrganizerAuth.IsRealOrganizer"/>
/// for writes). The first line of the text is the <c>Subject:</c>; the rest is the body
/// (same shape as the on-disk files); tokens are <c>{{name}}</c>.
/// </summary>
[Authorize]
public class EmailTemplatesModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly EmailTemplateProvider _templates;
    private readonly EmailTemplateOverrideStore _store;
    private readonly FeatureSettingsService _features;
    private readonly ILogger<EmailTemplatesModel> _log;

    public EmailTemplatesModel(
        ICurrentParticipantAccessor participant,
        EmailTemplateProvider templates,
        EmailTemplateOverrideStore store,
        FeatureSettingsService features,
        ILogger<EmailTemplatesModel> log)
    {
        _participant = participant;
        _templates = templates;
        _store = store;
        _features = features;
        _log = log;
    }

    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }
    public bool IsError { get; private set; }

    public sealed record Row(string Key, string Title, string FeatureKey, Ring Ring, bool Overridden);
    public IReadOnlyList<Row> Templates { get; private set; } = Array.Empty<Row>();

    // The currently-edited template (when ?key=… is selected).
    [BindProperty(SupportsGet = true)] public string? Key { get; set; }
    [BindProperty] public string? EditText { get; set; }
    public bool Editing => !string.IsNullOrWhiteSpace(Key);
    public bool SelectedOverridden { get; private set; }
    public string SelectedTitle { get; private set; } = string.Empty;
    public string SelectedFeatureKey { get; private set; } = "outbound-email";
    public Ring SelectedRing { get; private set; } = Rings.Default;
    public string? UpdatedInfo { get; private set; }

    public string? PreviewSubject { get; private set; }
    public string? PreviewHtml { get; private set; }
    public string? PreviewError { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer || me.IsActingAs) { AccessDenied = true; return Page(); }

        await LoadListAsync(me.EventId, ct);
        if (Editing)
        {
            await LoadEditorAsync(me.EventId, Key!, loadTextFromStore: true, ct);
            BuildPreview(EditText);
        }
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }
        if (string.IsNullOrWhiteSpace(Key)) { return RedirectToPage(); }

        try
        {
            if (string.IsNullOrWhiteSpace(EditText))
                throw new InvalidOperationException("The template text cannot be empty.");
            if (!EditText.TrimStart().StartsWith("Subject:", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The first line must be a \"Subject: …\" line.");

            await _store.UpsertAsync(me.EventId, Key!, EditText, me.Email, ct);
            Message = "Template saved — it is now live for this edition (sends + preview).";
        }
        catch (Exception ex)
        {
            IsError = true; Message = ex.Message;
            _log.LogWarning(ex, "EmailTemplates save failed for {Key}", Key);
        }

        await LoadListAsync(me.EventId, ct);
        await LoadEditorAsync(me.EventId, Key!, loadTextFromStore: false, ct);  // keep the user's text
        BuildPreview(EditText);
        return Page();
    }

    public async Task<IActionResult> OnPostPreviewAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer || me.IsActingAs) { AccessDenied = true; return Page(); }

        await LoadListAsync(me.EventId, ct);
        await LoadEditorAsync(me.EventId, Key ?? string.Empty, loadTextFromStore: false, ct);
        BuildPreview(EditText);   // preview the UNSAVED text
        return Page();
    }

    public async Task<IActionResult> OnPostResetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }
        if (!string.IsNullOrWhiteSpace(Key))
        {
            await _store.DeleteAsync(me.EventId, Key!, ct);
            Message = "Reset to the shipped default.";
        }
        return RedirectToPage(new { Key });
    }

    public async Task<IActionResult> OnPostRingAsync(string ring, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }
        if (!string.IsNullOrWhiteSpace(Key) && Enum.TryParse<Ring>(ring, out var r))
        {
            var featureKey = EmailTemplateCatalog.FeatureKeyFor(Key!);
            await _features.SetReleasedRingAsync(me.EventId, featureKey, r, me.Email, ct);
            Message = $"Ring set to {Rings.Label(r)} for this template.";
        }
        return RedirectToPage(new { Key });
    }

    private async Task LoadListAsync(int eventId, CancellationToken ct)
    {
        var overrides = (await _store.GetAllAsync(eventId, ct)).Select(o => o.TemplateKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rings = (await _features.GetAllAsync(eventId, ct))
            .ToDictionary(f => f.Key, f => f.ReleasedToRing, StringComparer.OrdinalIgnoreCase);

        Templates = _templates.ListTemplateKeys().Select(k =>
        {
            var fk = EmailTemplateCatalog.FeatureKeyFor(k);
            var ring = rings.TryGetValue(fk, out var rr) ? rr
                : FeatureCatalog.DefaultReleasedToRing(fk);
            return new Row(k, EmailTemplateCatalog.TitleFor(k), fk, ring, overrides.Contains(k));
        }).ToList();
    }

    private async Task LoadEditorAsync(int eventId, string key, bool loadTextFromStore, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        SelectedTitle = EmailTemplateCatalog.TitleFor(key);
        SelectedFeatureKey = EmailTemplateCatalog.FeatureKeyFor(key);

        var row = await _store.GetAsync(eventId, key, ct);
        SelectedOverridden = row is not null && !string.IsNullOrWhiteSpace(row.OverrideText);
        if (row is not null)
            UpdatedInfo = $"Overridden — last saved {row.UpdatedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC"
                + (string.IsNullOrWhiteSpace(row.UpdatedByEmail) ? "" : $" by {row.UpdatedByEmail}");

        var ringMatch = (await _features.GetAllAsync(eventId, ct))
            .FirstOrDefault(f => string.Equals(f.Key, SelectedFeatureKey, StringComparison.OrdinalIgnoreCase));
        SelectedRing = ringMatch?.ReleasedToRing ?? FeatureCatalog.DefaultReleasedToRing(SelectedFeatureKey);

        if (loadTextFromStore)
        {
            // Effective text = override if saved, else the shipped default.
            EditText = SelectedOverridden ? row!.OverrideText : SafeDefaultText(key);
        }
    }

    private string SafeDefaultText(string key)
    {
        try { return _templates.GetDefaultText(key); }
        catch (Exception ex) { _log.LogWarning(ex, "default text load failed for {Key}", key); return ""; }
    }

    private void BuildPreview(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            var rendered = _templates.RenderText(text, BuildSampleTokens());
            PreviewSubject = rendered.Subject;
            PreviewHtml = rendered.HtmlBody;
        }
        catch (Exception ex)
        {
            PreviewError = ex.Message;
        }
    }

    // Representative sample tokens so the preview renders realistically. Raw-HTML tokens
    // (…Html/…Block/bodyContent) carry sample markup; everything else is a plain value.
    private Dictionary<string, string> BuildSampleTokens()
    {
        var t = _templates.NewTokenSet();
        void S(string k, string v) { t[k] = v; }
        S("firstName", "Alex"); S("fullName", "Alex Sample");
        S("communityName", "Experts Live"); S("eventDisplayName", "Experts Live Denmark 2027");
        S("eventCode", "ELDK27"); S("roleName", "Speaker");
        S("roleGuidance", "Here's what's relevant for your role at the event.");
        S("roleLine", "Thanks for being part of the event.");
        S("hubUrl", "https://eldk27.eventhub.expertslive.dk");
        S("loginUrl", "https://eldk27.eventhub.expertslive.dk/Login");
        S("taskTitle", "Upload your presentation"); S("dueText", "due Fri 6 Feb");
        S("deadline", "6 Feb 2027"); S("stepLabel", "Hotel booking");
        S("formName", "Hotel"); S("formDeadline", "1 Feb 2027");
        S("masterClassList", "Securing Entra ID");
        S("messageHtml", "<p>This is a sample broadcast message body.</p>");
        S("bodyContent", "<p>Sample body content.</p>");
        S("leadListHtml", "<tr><td>Sample Lead</td><td>lead@example.com</td></tr>");
        return t;
    }
}
