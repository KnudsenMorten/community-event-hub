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

    // §551 — the same two stages, plus "did anyone ever CHOOSE this?" and the audited history.
    // The page must never again present an unset stage as a decision.
    public SyncStageState SessionStage { get; private set; } = new(SessionSyncDirection.SessionizeToCeh, false);
    public SyncStageState SpeakerStage { get; private set; } = new(SessionSyncDirection.SessionizeToCeh, false);

    /// <summary>§551 — the edition this page is reading, shown alongside a NOT-CONFIGURED warning.
    /// The leading hypothesis for the silent revert is a settings row that belongs to a DIFFERENT
    /// edition, so the edition id is the first thing worth seeing.</summary>
    public int EventId { get; private set; }

    /// <summary>
    /// One selectable sync stage. <paramref name="Implemented"/> means "there is an engine
    /// behind this" and is what gates activation; <paramref name="Blocker"/> is the separate,
    /// honest answer to "will it actually do anything today?" — a stage can be fully built and
    /// still no-op because an upstream API scope has not been granted. §326bm went wrong twice
    /// by collapsing those two questions into one boolean.
    /// </summary>
    public sealed record DirectionOption(
        SessionSyncDirection Direction, int Stage, string Label, string Note, bool Implemented,
        string? Blocker = null);

    public IReadOnlyList<DirectionOption> DirectionOptions { get; } = new[]
    {
        new DirectionOption(SessionSyncDirection.SessionizeToCeh, 1, "Sessionize → CEH",
            "Import speakers and sessions from Sessionize into CEH. This is the current default and the only "
            + "stage that runs today.", Implemented: true),
        // §326bm CORRECTION: stage 2 IS implemented for sessions — SessionBackstagePushService
        // (create-if-unlinked / update-if-linked, idempotent on the stored Backstage id) is
        // real, gated on this very setting, and driven by SessionBackstagePushJob. The old
        // "not yet implemented" note was stale and made a working stage look inert.
        new DirectionOption(SessionSyncDirection.CehToZoho, 2, "CEH → Zoho Backstage",
            "Push sessions from CEH up to Zoho Backstage — creates a session that is not linked yet, "
            + "updates one that is, never deletes. Approved CEH→Zoho changes in the Sync Approval Queue "
            + "are applied by this push.", Implemented: true),
        new DirectionOption(SessionSyncDirection.ZohoToCeh, 3, "Zoho Backstage → CEH",
            "Pull the finalized Zoho Backstage agenda (session time/location) back into CEH and alert affected "
            + "speakers (the §38e change-detection engine). Inactive until this stage is selected.", Implemented: true),
    };

    // §58: the SPEAKER stages mirror the session ones — and, since §333, all three are built.
    public IReadOnlyList<DirectionOption> SpeakerDirectionOptions { get; } = new[]
    {
        new DirectionOption(SessionSyncDirection.SessionizeToCeh, 1, "Sessionize → CEH",
            "Import speakers from Sessionize into CEH. This is the current default and the only "
            + "stage that runs today.", Implemented: true),
        // §326bm CORRECTION: the SPEAKER stage-2 push is implemented too —
        // SpeakerBackstagePushService creates a speaker in Backstage when they are not linked
        // yet and updates a linked one; it is what applies an approved CEH→Zoho speaker change
        // from the Sync Approval Queue. Ring-scoped and off by default (see Settings).
        new DirectionOption(SessionSyncDirection.CehToZoho, 2, "CEH → Zoho Backstage",
            "Push speakers from CEH up to Zoho Backstage — creates a speaker who is not linked yet, "
            + "updates one who is. The Backstage API is create-only for speakers, so an existing "
            + "speaker is never duplicated.", Implemented: true),
        // §333 — the THIRD stale "not implemented" flag on this page (§326bm found two). The
        // engine is real and complete: SpeakerChangeDetectionService diffs each LINKED speaker's
        // name/tagline/bio/country/social against the CEH snapshot and ENQUEUES a Pending delta
        // for approval (first-populate seeds silently, never auto-applies, never deletes),
        // driven hourly at :50 by SpeakerChangeDetectionJob, and SyncDeltaQueueService has the
        // arm that writes an approved upstream value back into CEH. What is missing is not code
        // but an upstream SCOPE — hence Blocker rather than Implemented: false.
        new DirectionOption(SessionSyncDirection.ZohoToCeh, 3, "Zoho Backstage → CEH",
            "Pull Zoho Backstage speaker changes back into CEH and raise them for approval — the hourly "
            + "change-detection engine diffs each linked speaker's name, tagline, bio, country and social "
            + "links against CEH and queues anything that differs. It never auto-applies and never deletes; "
            + "you approve or reject each change in the Sync Approval Queue.",
            Implemented: true,
            Blocker: "Built, but it will find nothing until Zoho Backstage grants the speaker READ scope "
                     + "(ZohoBackstage.speaker.READ) and speaker reads are switched on. Until then the job "
                     + "runs, logs that the source is unavailable, and changes nothing."),
    };

    /// <summary>§326bm — is this stage backed by real behaviour? Only an implemented
    /// stage may be activated; an unknown stage is treated as NOT implemented.</summary>
    private static bool IsImplemented(
        IReadOnlyList<DirectionOption> options, SessionSyncDirection direction) =>
        options.FirstOrDefault(o => o.Direction == direction)?.Implemented ?? false;

    /// <summary>§326bm — the stage that is stored AND real. When the stored stage is an
    /// unimplemented one (an earlier build allowed that), the page reports the stage that
    /// actually runs rather than repeating the stored fiction.</summary>
    public SessionSyncDirection EffectiveDirection =>
        IsImplemented(DirectionOptions, ActiveDirection)
            ? ActiveDirection : SessionSyncDirection.SessionizeToCeh;

    public SessionSyncDirection EffectiveSpeakerDirection =>
        IsImplemented(SpeakerDirectionOptions, ActiveSpeakerDirection)
            ? ActiveSpeakerDirection : SessionSyncDirection.SessionizeToCeh;

    /// <summary>True when a stage was stored that cannot actually run — the page shows a
    /// warning so the operator knows the stored value is being ignored.</summary>
    public bool DirectionIsStale => !IsImplemented(DirectionOptions, ActiveDirection);
    public bool SpeakerDirectionIsStale => !IsImplemented(SpeakerDirectionOptions, ActiveSpeakerDirection);

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
        EventId = me.EventId;
        ActiveKey = await _settings.GetActiveKeyAsync(me.EventId, ct);

        // §551 — read the STATE (stage + was-it-configured + last audited change), not the bare
        // stage. The bare read collapses "nobody ever set this" into stage 1, which is precisely
        // how a push that had switched itself off kept looking like a deliberate choice.
        SessionStage = await _settings.GetSyncDirectionStateAsync(me.EventId, ct);
        SpeakerStage = await _settings.GetSpeakerSyncDirectionStateAsync(me.EventId, ct);
        ActiveDirection = SessionStage.Effective;
        ActiveSpeakerDirection = SpeakerStage.Effective;

        BuildOptions();
        return Page();
    }

    public async Task<IActionResult> OnPostSetDirectionAsync(int stage, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        if (!Enum.IsDefined(typeof(SessionSyncDirection), stage))
            return RedirectToPage(new { msg = "Unknown sync direction." });

        var direction = (SessionSyncDirection)stage;

        // §326bm (operator 2026-07-25: "this seems wrong") — an UNIMPLEMENTED stage may no
        // longer be activated. The page used to let one be selected and then rendered it as
        // "Active" *and* "Not implemented yet" at the same time, which reads as "the system
        // is pushing to Backstage" when no push exists. Refuse, and say what still runs.
        if (!IsImplemented(DirectionOptions, direction))
        {
            return RedirectToPage(new { msg =
                $"Stage {stage} is not implemented yet, so it cannot be made the active stage — "
                + "nothing was changed. Sessionize → CEH (stage 1) remains what actually runs." });
        }

        await _settings.SetSyncDirectionAsync(me.EventId, direction, me.Email, ct);
        return RedirectToPage(new { msg = $"Sync direction set to stage {stage} ({direction})." });
    }

    public async Task<IActionResult> OnPostSetSpeakerDirectionAsync(int stage, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        if (!Enum.IsDefined(typeof(SessionSyncDirection), stage))
            return RedirectToPage(new { msg = "Unknown speaker sync direction." });

        var direction = (SessionSyncDirection)stage;

        // §326bm — same guard as the session stages. For SPEAKERS only stage 1 exists today
        // (stages 2 and 3 have no engine at all), so this keeps the page honest: speakers are
        // imported from Sessionize, full stop, until an engine is actually built.
        if (!IsImplemented(SpeakerDirectionOptions, direction))
        {
            return RedirectToPage(new { msg =
                $"Speaker stage {stage} is not implemented yet, so it cannot be made the active "
                + "stage — nothing was changed. Sessionize → CEH (stage 1) remains what actually runs." });
        }

        await _settings.SetSpeakerSyncDirectionAsync(me.EventId, direction, me.Email, ct);
        return RedirectToPage(new { msg = $"Speaker sync direction set to stage {stage} ({direction})." });
    }

    public async Task<IActionResult> OnPostSetAsync(string source, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        if (!SessionSourceKinds.IsKnown(source))
            return RedirectToPage(new { msg = "Unknown source." });

        // Guard: don't let an organizer switch to a source that can't pull yet.
        if (!Available(source))
            return RedirectToPage(new { msg = $"{source} isn't available yet, so the source was not changed." });

        await _settings.SetAsync(me.EventId, source, me.Email, ct);
        return RedirectToPage(new { msg = $"Session source set to {source}." });
    }
}
