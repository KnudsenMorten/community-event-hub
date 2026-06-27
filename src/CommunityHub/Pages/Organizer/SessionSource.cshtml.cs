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

    // §57 session sync DIRECTION / stage (only one active at a time).
    public SessionSyncDirection ActiveDirection { get; private set; } = SessionSyncDirection.SessionizeToCeh;

    // §58 SPEAKER sync DIRECTION / stage (separate from the session one, only one active).
    public SessionSyncDirection ActiveSpeakerDirection { get; private set; } = SessionSyncDirection.SessionizeToCeh;

    public sealed record DirectionOption(
        SessionSyncDirection Direction, int Stage, string Label, string Note, bool Implemented);

    public IReadOnlyList<DirectionOption> DirectionOptions { get; } = new[]
    {
        new DirectionOption(SessionSyncDirection.SessionizeToCeh, 1, "Sessionize → CEH",
            "Import speakers and sessions from Sessionize into CEH. This is the current default and the only "
            + "stage that runs today.", Implemented: true),
        new DirectionOption(SessionSyncDirection.CehToZoho, 2, "CEH → Zoho Backstage",
            "Push sessions from CEH up to Zoho Backstage. Not yet implemented — selecting this records the "
            + "intended stage but performs no push (the operator will build/test that later).", Implemented: false),
        new DirectionOption(SessionSyncDirection.ZohoToCeh, 3, "Zoho Backstage → CEH",
            "Pull the finalized Zoho Backstage agenda (session time/location) back into CEH and alert affected "
            + "speakers (the §38e change-detection engine). Inactive until this stage is selected.", Implemented: true),
    };

    // §58: the SPEAKER stages mirror the session ones. Stage 3 (Zoho→CEH speaker change
    // detection) has NO engine yet — selecting it only records the stage + arms the gate.
    public IReadOnlyList<DirectionOption> SpeakerDirectionOptions { get; } = new[]
    {
        new DirectionOption(SessionSyncDirection.SessionizeToCeh, 1, "Sessionize → CEH",
            "Import speakers from Sessionize into CEH. This is the current default and the only "
            + "stage that runs today.", Implemented: true),
        new DirectionOption(SessionSyncDirection.CehToZoho, 2, "CEH → Zoho Backstage",
            "Push speakers from CEH up to Zoho Backstage. Not yet implemented — selecting this records the "
            + "intended stage but performs no push (the operator will build/test that later).", Implemented: false),
        new DirectionOption(SessionSyncDirection.ZohoToCeh, 3, "Zoho Backstage → CEH",
            "Pull Zoho Backstage speaker changes back into CEH and alert affected speakers (a future "
            + "change-detection engine). Inactive until this stage is selected; no speaker engine runs yet.",
            Implemented: false),
    };

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
        ActiveDirection = await _settings.GetSyncDirectionAsync(me.EventId, ct);
        ActiveSpeakerDirection = await _settings.GetSpeakerSyncDirectionAsync(me.EventId, ct);
        BuildOptions();
        return Page();
    }

    public async Task<IActionResult> OnPostSetDirectionAsync(int stage, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        if (!Enum.IsDefined(typeof(SessionSyncDirection), stage))
            return RedirectToPage(new { msg = "Unknown sync direction." });

        var direction = (SessionSyncDirection)stage;
        await _settings.SetSyncDirectionAsync(me.EventId, direction, me.Email, ct);

        // Stage 2 (CEH→Zoho) push is not yet implemented — record the stage only.
        // TODO §57 stage 2: implement the CEH→Zoho push (operator will build/test it later).
        var note = direction == SessionSyncDirection.CehToZoho
            ? $"Sync direction set to stage {stage} (CEH → Zoho). Note: the CEH→Zoho push is not yet implemented."
            : $"Sync direction set to stage {stage} ({direction}).";
        return RedirectToPage(new { msg = note });
    }

    public async Task<IActionResult> OnPostSetSpeakerDirectionAsync(int stage, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        if (!Enum.IsDefined(typeof(SessionSyncDirection), stage))
            return RedirectToPage(new { msg = "Unknown speaker sync direction." });

        var direction = (SessionSyncDirection)stage;
        await _settings.SetSpeakerSyncDirectionAsync(me.EventId, direction, me.Email, ct);

        // §58: stages 2 (CEH→Zoho push) and 3 (Zoho→CEH speaker change detection) are not yet
        // implemented — record the stage only. Stage 3 only ARMS the gate for the future engine.
        // TODO §58 stage 2/3: implement the CEH→Zoho speaker push + the Zoho→CEH speaker
        // change-detection engine (operator will build/test them later).
        var note = direction == SessionSyncDirection.SessionizeToCeh
            ? $"Speaker sync direction set to stage {stage} ({direction})."
            : $"Speaker sync direction set to stage {stage} ({direction}). Note: this stage is not yet implemented.";
        return RedirectToPage(new { msg = note });
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
