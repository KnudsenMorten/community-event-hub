using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Settings;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Organizer;

/// <summary>
/// §304 (operator 2026-07-24): "i need an organizer admin interface + mail immediately
/// when there are pending tasks for us to complete — linking the SpeakerCategory +
/// validate ring for any pending speaker and include activating it so it flows to zoho
/// fast." A speaker imported from Sessionize arrives Ring 3 / inactive / uncategorized
/// (the correct fail-closed default) and is therefore HELD by the stage-2 Zoho gates;
/// this service is the single source for (a) WHO is pending and WHY, (b) the one-click
/// APPROVE that clears every blocker at once, and (c) the immediate ops-mail body the
/// import job sends when new pending speakers appear (/Organizer/PendingSpeakers).
/// </summary>
public sealed class SpeakerApprovalService
{
    /// <summary>The ring feature whose released ring scopes the Zoho speaker flow.</summary>
    public const string RingFeatureKey = "backstage-speaker-sync";

    /// <summary>The admin page the mail links to.</summary>
    public const string AdminPath = "/Organizer/PendingSpeakers";

    private readonly CommunityHubDbContext _db;
    private readonly FeatureGateService _gate;
    private readonly TimeProvider _clock;

    public SpeakerApprovalService(CommunityHubDbContext db, FeatureGateService gate, TimeProvider clock)
    {
        _db = db;
        _gate = gate;
        _clock = clock;
    }

    /// <summary>One pending speaker + exactly which §299 gates hold them back.</summary>
    public sealed record PendingSpeaker(
        int ParticipantId, string Email, string FullName,
        Ring Ring, SpeakerCategory? Category, bool IsActive, ParticipantLifecycleState Lifecycle,
        IReadOnlyList<string> Blockers);

    public sealed record PendingResult(Ring ReleasedRing, bool FeatureEnabled, IReadOnlyList<PendingSpeaker> Speakers);

    /// <summary>
    /// Every speaker of the edition currently HELD from the Zoho flow, with the exact
    /// blockers: uncategorized (§299 6.1), not activated / inactive, or ring above the
    /// released <see cref="RingFeatureKey"/> ring. An approved+in-ring speaker never
    /// appears here.
    /// </summary>
    public async Task<PendingResult> PendingAsync(int eventId, CancellationToken ct = default)
    {
        var enabled = await _gate.IsFeatureEnabledAsync(RingFeatureKey, eventId, ct);
        var released = enabled ? await _gate.GetReleasedRingAsync(RingFeatureKey, eventId, ct) : Ring.Ring0;

        var rows = await _db.SpeakerProfiles.AsNoTracking()
            .Where(sp => sp.EventId == eventId)
            .Join(_db.Participants.AsNoTracking(), sp => sp.ParticipantId, p => p.Id,
                (sp, p) => new
                {
                    sp.Category, p.Id, p.Email, p.FullName, p.Ring, p.IsActive, p.LifecycleState,
                    p.DeactivatedByOrganizerAt,
                })
            .OrderBy(x => x.Email)
            .ToListAsync(ct);

        var pending = new List<PendingSpeaker>();
        foreach (var r in rows)
        {
            // §498/§499 — a speaker who WAS in and has since left (withdrew, or an organizer
            // deactivated them) is not pending ANYTHING: there is no decision left to take, so
            // listing them as awaiting approval invents work that does not exist.
            //
            // ❗ The opposite case is deliberately KEPT: someone imported from Sessionize and not
            // yet activated is exactly what this queue is FOR. Filtering on "inactive" alone would
            // have emptied the queue of its entire purpose — which is why §499 separates the two
            // meanings of inactive rather than treating every inactive row alike.
            var droppedOut = !r.IsActive
                             && (r.LifecycleState == ParticipantLifecycleState.Active
                                 || r.DeactivatedByOrganizerAt != null);
            if (droppedOut) continue;

            var blockers = new List<string>();
            if (r.Category is null) blockers.Add("no speaker category (§299 6.1 — excluded from everything)");
            if (!r.IsActive) blockers.Add("participant inactive");
            if (r.LifecycleState != ParticipantLifecycleState.Active) blockers.Add($"not activated (lifecycle {r.LifecycleState})");
            // 🔒 §569 — THE RING IS NO LONGER A BLOCKER. DO NOT RE-ADD IT.
            // This line is what made EVERY speaker appear on this page as held: speakers are Ring 3
            // while backstage-speaker-sync was released to Ring 2 (§550). The operator's point was
            // that the page should list only speakers genuinely lacking a DECISION — category or
            // activation — and his rule is now explicit: "once the speaker category is set + they
            // are active, they must sync to zoho". A ring answers "which PARTICIPANTS get this
            // yet?"; pushing an organizer-approved speaker to Zoho is not that question.
            if (blockers.Count > 0)
                pending.Add(new PendingSpeaker(r.Id, r.Email, r.FullName, r.Ring, r.Category,
                    r.IsActive, r.LifecycleState, blockers));
        }

        return new PendingResult(released, enabled, pending);
    }

    /// <summary>
    /// The one-click APPROVE: set the category, place the ring, activate — everything
    /// the Zoho flow gates on, in one write. Returns false when the speaker is gone.
    /// </summary>
    public async Task<bool> ApproveAsync(
        int eventId, int participantId, SpeakerCategory category, Ring ring, CancellationToken ct = default)
    {
        var profile = await _db.SpeakerProfiles.FirstOrDefaultAsync(
            sp => sp.EventId == eventId && sp.ParticipantId == participantId, ct);
        var participant = await _db.Participants.FirstOrDefaultAsync(
            p => p.EventId == eventId && p.Id == participantId, ct);
        if (profile is null || participant is null) return false;

        profile.Category = category;
        profile.UpdatedAt = _clock.GetUtcNow();
        participant.Ring = ring;
        participant.IsActive = true;
        participant.LifecycleState = ParticipantLifecycleState.Active;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// The immediate ops mail (§304) the import job sends when NEW speakers arrived and
    /// pending work exists. Lists every pending speaker with their blockers + the admin
    /// link. Returns null when nothing is pending (no mail).
    /// </summary>
    public static string? BuildPendingMailHtml(PendingResult pending, string baseUrl)
    {
        if (pending.Speakers.Count == 0) return null;
        string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);
        var rows = string.Join("", pending.Speakers.Select(s =>
            $"<li><strong>{Enc(s.FullName)}</strong> ({Enc(s.Email)}) — ring {(int)s.Ring}, "
            + $"category {(s.Category?.ToString() ?? "NONE")}: {Enc(string.Join("; ", s.Blockers))}</li>"));
        return
            $"<p><strong>{pending.Speakers.Count} speaker(s)</strong> need approval before they can "
            + "flow to Zoho Backstage — set the speaker category, place the ring and activate:</p>"
            + $"<ul>{rows}</ul>"
            + $"<p><a href=\"{Enc(baseUrl.TrimEnd('/'))}{AdminPath}\">Open Pending speakers →</a> "
            + $"(released Zoho ring today: {(int)pending.ReleasedRing})</p>";
    }
}
