using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Sessions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// Organizer Settings switch for the edition's SESSION source (Sessionize vs Zoho
/// Backstage) — REQUIREMENTS §6. Sessionize is the default + active source today;
/// Zoho Backstage becomes selectable once its agenda scope is granted. Speakers
/// are always sourced from Sessionize; this governs only sessions. Organizer-gated.
/// </summary>
[Authorize]
public class SessionSourceModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly SessionSourceSettingsService _settings;
    private readonly IReadOnlyList<ISessionSource> _sources;

    public SessionSourceModel(
        ICurrentParticipantAccessor participant,
        SessionSourceSettingsService settings,
        IEnumerable<ISessionSource> sources)
    {
        _participant = participant;
        _settings = settings;
        _sources = sources.ToList();
    }

    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }
    public string ActiveKey { get; private set; } = SessionSourceKinds.Default;

    public sealed record SourceOption(string Key, string Label, bool Available, string Note);
    public IReadOnlyList<SourceOption> Options { get; private set; } = Array.Empty<SourceOption>();

    private bool Available(string key) =>
        _sources.FirstOrDefault(s => s.Key == key)?.IsAvailable ?? false;

    private void BuildOptions() => Options = new[]
    {
        new SourceOption(SessionSourceKinds.Sessionize, "Sessionize",
            Available(SessionSourceKinds.Sessionize),
            "Sessions from the Sessionize v2 view API (current default)."),
        new SourceOption(SessionSourceKinds.ZohoBackstage, "Zoho Backstage",
            Available(SessionSourceKinds.ZohoBackstage),
            "The finalized agenda from Zoho Backstage. Not yet enabled — needs the "
            + "ZohoBackstage.agenda.READ scope on the refresh token."),
    };

    public async Task<IActionResult> OnGetAsync(string? msg, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        Message = msg;
        ActiveKey = await _settings.GetActiveKeyAsync(me.EventId, ct);
        BuildOptions();
        return Page();
    }

    public async Task<IActionResult> OnPostSetAsync(string source, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        if (!SessionSourceKinds.IsKnown(source))
            return RedirectToPage(new { msg = "Unknown source." });

        // Guard: don't let an organizer switch to a source that can't pull yet.
        if (!Available(source))
            return RedirectToPage(new { msg = $"{source} isn't available yet, so the source was not changed." });

        await _settings.SetAsync(me.EventId, source, me.Email, ct);
        return RedirectToPage(new { msg = $"Session source set to {source}." });
    }
}
