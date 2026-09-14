using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// §994 — gives every ORGANIZER a Get-Started cadence anchor, without sending them a welcome mail.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-09: *"organizers should not get welcome mail but seed the field, so he is
/// chased if not filled out"*.</para>
///
/// <para>🔴 <b>The dead end this closes.</b> <c>WelcomeVariants.TemplateKeyFor</c> returns
/// <b>null for Organizer</b> — organizers are deliberately never welcomed (operator decision
/// 2026-06-22). But §738's gate in <see cref="GetStartedDigestBuilder"/> is
/// <c>if (p.WelcomeWithLoginSentAt is null) continue</c> — *never welcomed ⇒ never chased*. The two
/// rules combined meant **an organizer could never be reminded about an unfinished wizard**, which
/// is exactly how the operator found it: he is an organizer, he had filled in nothing, and nothing
/// ever chased him.</para>
///
/// <para>🔑 <b>Why a SWEEP and not a hook at participant creation.</b> This is the
/// <see cref="GetStartedCompletionSweep"/> argument, and it has already been paid for once: §720
/// hooked completion into the wizard page and real people finished elsewhere, so no mail went out.
/// Organizers are created from the organizer page, from imports and from seeding — a hook on one
/// path works today and breaks silently the next time a path is added. Asking *"which organizer has
/// no anchor?"* catches every path, including ones that do not exist yet.</para>
///
/// <para>🔒 <b>The anchor is <c>CreatedAt</c>, and nothing else was available.</b> §985a established
/// this by hand for the four existing organizers: there is no welcome mail, so there is no welcome
/// date to match, and CreatedAt is the only honest value. ⚠️ It is deliberately NOT "now" — that
/// would restart the 7-day cadence on every new organizer and delay the first chase by a week for
/// somebody who has been in the system for months.</para>
///
/// <para>🔒 <b>Granting an anchor is not the same as sending a mail.</b> The digest still skips a
/// 100 %-complete wizard (<c>openKeys.Count == 0</c>), so this grants *eligibility* to be chased;
/// the person's own progress remains the filter. An organizer who has finished everything gets
/// nothing.</para>
///
/// <para>⚠️ <b>Only ever fills a NULL.</b> An existing anchor is never overwritten — doing so would
/// reset somebody's cadence on every run and re-chase people who were just chased.</para>
/// </remarks>
public sealed class OrganizerWelcomeAnchorSeeder
{
    private readonly CommunityHubDbContext _db;
    private readonly ILogger<OrganizerWelcomeAnchorSeeder>? _log;

    public OrganizerWelcomeAnchorSeeder(
        CommunityHubDbContext db, ILogger<OrganizerWelcomeAnchorSeeder>? log = null)
    {
        _db = db;
        _log = log;
    }

    /// <summary>
    /// Seeds any unanchored active organizer, and (§1222) anyone welcomed without a stamp.
    /// Returns how many were seeded.
    /// </summary>
    public async Task<int> RunAsync(int eventId, CancellationToken ct = default) =>
        await SeedOrganizersAsync(eventId, ct) + await SeedFromWelcomeLedgerAsync(eventId, ct);

    /// <summary>
    /// 🔴 §1222 — anyone who WAS welcomed (a <c>welcome:{id}</c> ledger row exists) but has no
    /// <see cref="Participant.WelcomeWithLoginSentAt"/> gets the ledger's send date as their anchor.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-09-14: <i>"sponsors are reporting that they dont get any weekly reminders
    /// when get started is not completed"</i>. <c>WelcomeEmailService</c> — the path the sponsor and
    /// speaker reconcile jobs use — wrote the ledger row and never the stamp, so §738's "never
    /// welcomed ⇒ never chased" skipped people who HAD been welcomed. Found on PROD: 9 sponsor
    /// coordinators plus speakers, volunteers, media and event partners.</para>
    ///
    /// <para>🔒 The anchor is the date the welcome was actually SENT, never "now" — same reasoning as
    /// the organizer seed above: "now" would delay the first chase for people already waiting weeks.
    /// Only fills a NULL. A sweep rather than a one-off backfill, so any other path that forgets the
    /// stamp is healed the same way.</para>
    /// </remarks>
    private async Task<int> SeedFromWelcomeLedgerAsync(int eventId, CancellationToken ct)
    {
        var welcomedAt = await _db.SentReminders.AsNoTracking()
            .Where(s => s.EventId == eventId && s.ReminderType == "welcome" && s.OccasionKey.StartsWith("welcome:"))
            .Select(s => new { s.OccasionKey, s.SentAt })
            .ToListAsync(ct);
        if (welcomedAt.Count == 0) return 0;

        var byParticipant = new Dictionary<int, DateTimeOffset>();
        foreach (var w in welcomedAt)
        {
            if (!int.TryParse(w.OccasionKey["welcome:".Length..], out var pid)) continue;
            if (!byParticipant.TryGetValue(pid, out var first) || w.SentAt < first) byParticipant[pid] = w.SentAt;
        }

        var ids = byParticipant.Keys.ToList();
        var unanchored = await _db.Participants
            .Where(p => p.EventId == eventId && p.IsActive
                        && p.WelcomeWithLoginSentAt == null && ids.Contains(p.Id))
            .ToListAsync(ct);
        if (unanchored.Count == 0) return 0;

        foreach (var p in unanchored) p.WelcomeWithLoginSentAt = byParticipant[p.Id];
        await _db.SaveChangesAsync(ct);

        _log?.LogInformation(
            "§1222: seeded the Get-Started anchor for {Count} participant(s) from their welcome ledger "
            + "row — they were welcomed but never stamped, so the digest had skipped them.", unanchored.Count);
        return unanchored.Count;
    }

    private async Task<int> SeedOrganizersAsync(int eventId, CancellationToken ct)
    {
        var unanchored = await _db.Participants
            .Where(p => p.EventId == eventId
                        && p.IsActive
                        && p.Role == ParticipantRole.Organizer
                        && p.WelcomeWithLoginSentAt == null)
            .ToListAsync(ct);

        if (unanchored.Count == 0) return 0;

        foreach (var p in unanchored)
        {
            p.WelcomeWithLoginSentAt = p.CreatedAt;
        }

        await _db.SaveChangesAsync(ct);

        _log?.LogInformation(
            "§994: seeded the Get-Started anchor for {Count} organizer(s) from their CreatedAt — "
            + "they get no welcome mail (by design) but are now chaseable.", unanchored.Count);

        return unanchored.Count;
    }
}
