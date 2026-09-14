using CommunityHub.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Diagnostics;

/// <summary>
/// §545 CREDENTIAL, the Brevo case — <b>the one integration whose failure cannot be reported by
/// e-mail, because it IS the e-mail.</b>
/// </summary>
/// <remarks>
/// <para><b>Why §631's handler cannot cover this.</b> Brevo is reached over <b>SMTP</b>
/// (<c>smtp-relay.brevo.com:587</c>), not HTTP, so <see cref="CredentialFailureAlertHandler"/> — a
/// <c>DelegatingHandler</c> on <c>HttpClient</c> — never sees it. §545 named Brevo alongside the
/// other six; §631 delivered the six HTTP ones and this closes the seventh by a different route.</para>
///
/// <para>🔒 <b>The alerter cannot alert about itself.</b> If the Brevo credential dies, every send
/// fails — including the "Engine FAILED" mail that would tell him, and including the §631 credential
/// alerts. There is no mail-shaped way out of that, so the ONLY honest channel is IN-APP: this is
/// read by the Organizer Jobs page and shown as a banner.</para>
///
/// <para><b>No new storage.</b> `EmailLogs` already records every send and stamps `Error` on
/// failure (§10a-3), so the evidence exists — what was missing was anybody looking at it. A new
/// table would also have needed a migration and, if keyed like a job, would have been reported as
/// an orphan by §624's watchdog.</para>
/// </remarks>
public sealed class EmailTransportHealth
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public EmailTransportHealth(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db; _clock = clock;
    }

    /// <summary>How far back to look. Long enough to span a quiet period, short enough to be current.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(6);

    /// <summary>
    /// Consecutive failed sends before the transport is called DOWN. More than one, because a single
    /// bad recipient address fails without saying anything about the credential.
    /// </summary>
    public const int ConsecutiveFailuresBeforeDown = 3;

    /// <summary>The verdict, written for the operator rather than for a log.</summary>
    public sealed record Status(bool IsDown, int ConsecutiveFailures, string? LastError, string Detail)
    {
        public static readonly Status Healthy = new(false, 0, null, "E-mail is being delivered normally.");
    }

    /// <summary>
    /// Is outbound e-mail getting through? Judged on the most recent sends only — an old failure
    /// followed by a success is a resolved incident, not a live one.
    /// </summary>
    public async Task<Status> CheckAsync(CancellationToken ct = default)
    {
        var since = _clock.GetUtcNow() - Window;

        // Newest first: the streak we care about is the one at the HEAD of the log.
        var recent = await _db.EmailLogs.AsNoTracking()
            .Where(l => l.SentAt >= since)
            // 🔴 §1061 — A RING-HELD MAIL IS NOT A TRANSPORT SIGNAL. IGNORE IT ENTIRELY.
            //
            // ⚠️ MEASURED IN PROD 2026-08-11: this banner declared **"Outbound e-mail is DOWN — the
            // last 16 sends all failed"** while the relay was perfectly healthy. Its own quoted
            // error said so: *"Ring-dropped (recipient outside the released ring) — not sent."*
            // A ring drop writes an Error string, and every non-empty Error counted toward the
            // failure streak.
            //
            // 🔴 This is the WORST place for that defect. The alarm that exists to tell him mail is
            // broken was firing because mail was working exactly as configured — during a ticket
            // sale, on a page he was watching. An alarm that cries wolf is worse than no alarm,
            // because the next one is the one he ignores.
            //
            // 🔑 EXCLUDED, not counted as a success: nothing was sent, so a hold is evidence of
            // neither health nor breakage. Letting it END a failure streak would be the opposite
            // error — a genuinely dead relay would look recovered the moment one ring drop landed
            // in between.
            .Where(l => !l.Dropped)
            .OrderByDescending(l => l.SentAt)
            .Select(l => new { l.Error })
            .Take(50)
            .ToListAsync(ct);

        return Evaluate(recent.Select(r => r.Error).ToList());
    }

    /// <summary>
    /// The rule alone, over the most-recent-first list of per-send errors (null = delivered). Pure,
    /// so the judgement can be tested without a database or a mail server.
    /// </summary>
    public static Status Evaluate(IReadOnlyList<string?> errorsNewestFirst)
    {
        // 🔒 No sends at all is NOT evidence of a problem. A quiet night looks exactly like a dead
        // relay from here, and claiming "e-mail is down" on silence is the §609 mistake — the
        // banner would be permanently lit on any low-traffic day and he would stop reading it.
        if (errorsNewestFirst.Count == 0) return Status.Healthy;

        var streak = 0;
        string? lastError = null;
        foreach (var error in errorsNewestFirst)
        {
            // A SUCCESS ends the streak: mail is getting through right now, whatever happened before.
            if (string.IsNullOrWhiteSpace(error)) break;
            if (streak == 0) lastError = error;
            streak++;
        }

        if (streak < ConsecutiveFailuresBeforeDown) return Status.Healthy;

        var looksLikeCredential = LooksLikeAuthFailure(lastError);
        return new Status(true, streak, lastError,
            looksLikeCredential
                ? "Outbound e-mail is DOWN and the relay is refusing our credentials. Nothing is "
                  + "being delivered — including the alerts that would normally tell you so. The "
                  + "Brevo SMTP key needs reissuing; this cannot retry its way out."
                : $"Outbound e-mail is DOWN — the last {streak} sends all failed. Nothing is being "
                  + "delivered, including alert mail, so this will not arrive in your inbox.");
    }

    /// <summary>
    /// Does this SMTP error read as a rejected credential rather than a bad recipient or a blip?
    /// </summary>
    public static bool LooksLikeAuthFailure(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return false;

        // 535 is the SMTP code for "authentication credentials invalid"; the rest are the phrasings
        // Brevo and System.Net.Mail actually produce.
        string[] markers =
        {
            "535", "authentication", "auth failed", "not authenticated",
            "credentials", "5.7.8", "5.7.0", "clientnotpermitted",
        };

        return markers.Any(m => error.Contains(m, StringComparison.OrdinalIgnoreCase));
    }
}
