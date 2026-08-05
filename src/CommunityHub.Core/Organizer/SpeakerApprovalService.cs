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

    /// <summary>§877 — the outcome of a one-click bulk approval, for the confirmation line.</summary>
    public sealed record BulkApproveResult(int Count, IReadOnlyList<string> Names);

    /// <summary>
    /// §877 — approve EVERY currently-pending speaker in one action: same category, Ring 3.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-05: *"sets all pending speaker to community and ring 3. it will be
    /// the case for most"*, plus the same for Guest and Sponsor — *"default for pending sponsor
    /// should be ring 3. only difference is their speakercategory"*. So the ring is NOT a parameter
    /// of his request; it is always <see cref="Rings.Default"/> (Ring 3), which is also what a new
    /// speaker already arrives as.</para>
    ///
    /// <para>🔒 Operates on <see cref="PendingAsync"/>'s list, never on "all speakers": a speaker
    /// who is already approved has a category an organizer CHOSE, and a bulk button must not
    /// silently re-categorize them. It is therefore safe to click twice — the second click finds an
    /// empty queue and reports 0.</para>
    ///
    /// <para>⚠️ One SaveChanges for the whole batch, so a mid-batch failure approves nobody rather
    /// than half the queue.</para>
    /// </remarks>
    public async Task<BulkApproveResult> ApproveAllPendingAsync(
        int eventId, SpeakerCategory category, CancellationToken ct = default)
    {
        var pending = await PendingAsync(eventId, ct);
        if (pending.Speakers.Count == 0) return new BulkApproveResult(0, Array.Empty<string>());

        var ids = pending.Speakers.Select(s => s.ParticipantId).ToList();

        var profiles = await _db.SpeakerProfiles
            .Where(sp => sp.EventId == eventId && ids.Contains(sp.ParticipantId)).ToListAsync(ct);
        var participants = await _db.Participants
            .Where(p => p.EventId == eventId && ids.Contains(p.Id)).ToListAsync(ct);

        var now = _clock.GetUtcNow();
        foreach (var profile in profiles)
        {
            profile.Category = category;
            profile.UpdatedAt = now;
        }
        foreach (var participant in participants)
        {
            participant.Ring = Rings.Default;          // Ring 3 — his stated default for all three
            participant.IsActive = true;
            participant.LifecycleState = ParticipantLifecycleState.Active;
        }

        await _db.SaveChangesAsync(ct);

        var names = pending.Speakers
            .Select(s => string.IsNullOrWhiteSpace(s.FullName) ? s.Email : s.FullName)
            .ToList();
        return new BulkApproveResult(names.Count, names);
    }

    /// <summary>
    /// §877 — the query value naming a bulk-approve category (<c>?approveAll=community</c>).
    /// Returns false for anything unrecognised, so a stray value never approves anybody.
    /// </summary>
    public static bool TryParseApproveAll(string? value, out SpeakerCategory category)
    {
        category = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        switch (value.Trim().ToLowerInvariant())
        {
            case "community": category = SpeakerCategory.Community; return true;
            case "guest":     category = SpeakerCategory.Guest;     return true;
            case "sponsor":   category = SpeakerCategory.Sponsor;   return true;
            default: return false;
        }
    }

    /// <summary>
    /// The immediate ops mail (§304) the import job sends when NEW speakers arrived and
    /// pending work exists. Lists every pending speaker with their blockers + the admin
    /// link. Returns null when nothing is pending (no mail).
    ///
    /// <para>§877 — plus three ONE-CLICK buttons that approve the whole queue at Ring 3,
    /// differing only in category.</para>
    /// </summary>
    public static string? BuildPendingMailHtml(PendingResult pending, string baseUrl)
    {
        if (pending.Speakers.Count == 0) return null;
        string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);
        var root = Enc(baseUrl.TrimEnd('/'));
        var rows = string.Join("", pending.Speakers.Select(s =>
            $"<li><strong>{Enc(s.FullName)}</strong> ({Enc(s.Email)}) — ring {(int)s.Ring}, "
            + $"category {(s.Category?.ToString() ?? "NONE")}: {Enc(string.Join("; ", s.Blockers))}</li>"));

        var n = pending.Speakers.Count;
        return
            $"<p><strong>{n} speaker(s)</strong> need approval before they can "
            + "flow to Zoho Backstage — set the speaker category, place the ring and activate:</p>"
            + $"<ul>{rows}</ul>"
            // §877 — the common case is one click. Community first: "it will be the case for most".
            + $"<p style=\"margin:18px 0 6px;font-weight:bold;\">Approve all {n} at ring 3:</p>"
            + MailButton($"{root}{AdminPath}?approveAll=community", $"Approve all {n} as Community", primary: true)
            + MailButton($"{root}{AdminPath}?approveAll=guest", $"Approve all {n} as Guest", primary: false)
            + MailButton($"{root}{AdminPath}?approveAll=sponsor", $"Approve all {n} as Sponsor", primary: false)
            + "<p style=\"font-size:13px;color:#4b5563;margin:4px 0 14px;\">Each button sets the "
            + "category on every speaker above, places them at ring 3 and activates them. You will "
            + "see exactly who was approved, and can still change any single one afterwards.</p>"
            + $"<p><a href=\"{root}{AdminPath}\">Open Pending speakers →</a> "
            + $"(released Zoho ring today: {(int)pending.ReleasedRing})</p>";
    }

    /// <summary>
    /// §877 — a mail button in the operator-verified house style (§865/§684.10).
    /// </summary>
    /// <remarks>
    /// 🔒 <b>Desktop Outlook renders with the WORD engine</b>, which ignores <c>border-radius</c>
    /// and will paint dark text on a dark fill. So a button emits a <b>VML roundrect</b> inside the
    /// <c>mso</c> conditional and an ordinary anchor for every other client. This is the standard
    /// already used by the task and welcome mails — do not "simplify" it to a styled anchor.
    /// VML needs an explicit pixel width; the WORD engine will not size a roundrect to its content.
    /// </remarks>
    private static string MailButton(string href, string label, bool primary)
    {
        const string font = "Aptos,'Segoe UI',Arial,sans-serif";
        var fill = primary ? "#1565c0" : "#4b5563";
        var width = Math.Clamp(label.Length * 9 + 48, 200, 440);
        return
            "<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" "
            + "style=\"margin:6px 0;\"><tr><td align=\"left\">"
            + "<!--[if mso]>"
            + "<v:roundrect xmlns:v=\"urn:schemas-microsoft-com:vml\" "
            + "xmlns:w=\"urn:schemas-microsoft-com:office:word\" href=\"" + href
            + "\" style=\"height:44px;v-text-anchor:middle;width:" + width
            + "px;\" arcsize=\"50%\" stroke=\"f\" fillcolor=\"" + fill + "\">"
            + "<w:anchorlock/>"
            + "<center style=\"color:#ffffff;font-family:" + font
            + ";font-size:15px;font-weight:bold;\">" + label + "</center>"
            + "</v:roundrect>"
            + "<![endif]-->"
            + "<!--[if !mso]><!-- -->"
            + "<a href=\"" + href + "\" style=\"background-color:" + fill
            + ";border-radius:999px;color:#ffffff;display:inline-block;font-family:" + font
            + ";font-size:15px;font-weight:700;line-height:44px;text-align:center;"
            + "text-decoration:none;width:" + width
            + "px;-webkit-text-size-adjust:none;\">" + label + "</a>"
            + "<!--<![endif]-->"
            + "</td></tr></table>";
    }
}
