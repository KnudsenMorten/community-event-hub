using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// §207 — the Master Class selection reminder cadence for 2-day attendees. A 2-day attendee
/// with an OPEN <c>masterclass-form:</c> task (they have not yet confirmed a Master Class) is
/// reminded every 2 weeks, starting 2 weeks AFTER their welcome (the task's creation — §232:
/// the welcome is the day-0 nudge, so the first reminder waits a full window),
/// stopping the moment they select one (the task flips to Done, or a CONFIRMED
/// <see cref="MasterClassSignup"/> appears). Mirrors <see cref="AttendeePartyReminderBuilder"/>
/// exactly: stateless, self-healing (one reminder per 14-day window via the OccasionKey), and
/// reusing the shared <c>task-deadline-reminder</c> template so the §190 magic-link button +
/// §191 readable button + [ELDK27] subject postfix come from the send chokepoint.
/// </summary>
public sealed class AttendeeMasterClassReminderBuilder
{
    /// <summary>The BODY still renders the shared task template — the wording is identical.</summary>
    private const string TemplateName = "task-deadline-reminder";

    /// <summary>
    /// 🔒 §707.11 — the MAIL IDENTITY, split out of <c>task-deadline-reminder</c> so this chaser can
    /// carry its own ring and its own cadence (operator 2026-07-30: *"split them, i agree"*).
    /// </summary>
    public const string MailKeyName = "attendee-masterclass-reminder";

    /// <summary>The reminder TYPE — the ledger key this mail's history is stored under.</summary>
    public const string ReminderTypeName = "attendee-masterclass";

    /// <summary>Shipped default days between repeats — every 2 weeks (operator 2026-06-30).</summary>
    public const int IntervalDays = 14;

    private readonly CommunityHubDbContext _db;
    private readonly EmailTemplateProvider _templates;
    private readonly TimeProvider _clock;
    private readonly EmailReminderCadenceService? _cadence;

    public AttendeeMasterClassReminderBuilder(
        CommunityHubDbContext db, EmailTemplateProvider templates, TimeProvider clock,
        // §707.11 — optional so existing constructions keep compiling; null ⇒ the shipped default.
        EmailReminderCadenceService? cadence = null)
    {
        _db = db;
        _templates = templates;
        _clock = clock;
        _cadence = cadence;
    }

    public async Task<IReadOnlyList<ReminderMessage>> BuildDueAsync(
        int eventId, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);

        var ev = await _db.Events
            .Where(e => e.Id == eventId)
            .Select(e => new { e.CommunityName, e.DisplayName })
            .FirstOrDefaultAsync(ct);
        if (ev is null) return System.Array.Empty<ReminderMessage>();

        // §707.11 — the operator's cadence for THIS mail, and every recipient's last send, both read
        // ONCE per run rather than per candidate.
        var intervalDays = _cadence is null
            ? IntervalDays
            // §881 — an attendee-only mail, so the recipient role is not in question; passed
            // explicitly because the signature now carries it ahead of the token.
            : await _cadence.GetIntervalDaysAsync(eventId, MailKeyName, ParticipantRole.Attendee, ct);
        IReadOnlyDictionary<string, DateOnly> lastSentByOccasion = await EmailReminderCadenceService.LastSentByOccasionAsync(_db, eventId, ReminderTypeName, ct);

        // 🔒 §733.1 — RETIRED, for the same reason as the party chaser. Operator 2026-07-31:
        // *"get started wizard gets remindes every 14 days. each of the entries dont have due
        // dates. tasks lives outside of this with due dates"*, and earlier *"if yes, treat them
        // similar"*.
        //
        // Master Class SELECTION is a Get-Started step for a 2-day attendee — it is step 1 of
        // AttendeeWizardService. So this per-row chase and `getstarted-digest` were chasing the SAME
        // wizard, both every 14 days, and only the digest links to the step the attendee can see.
        //
        // 🔑 Coverage is not lost: the digest runs every 14 days and stops the moment the wizard is
        // 100% complete, and §732 made the optional steps count as complete so nobody is chased for
        // something they cannot finish. `pending-master-class-selection` (AttendeeBackstageSyncJob)
        // also still exists for the ticket-holder who has not chosen.
        //
        // Early return rather than deletion — see AttendeePartyReminderBuilder for the same shape.
        return System.Array.Empty<ReminderMessage>();

#pragma warning disable CS0162 // Unreachable — deliberately preserved; see §733.1 above.
        var key = AttendeeMasterClassTaskSeeder.MasterClassTaskKey + ":";
        var tasks = await _db.Tasks
            .Where(t => t.EventId == eventId
                        && t.State != TaskState.Done
                        && t.AssignedParticipantId != null
                        && t.SourceKey != null
                        && t.SourceKey.StartsWith(key))
            .Select(t => new { t.Id, t.CreatedAt, Participant = t.AssignedParticipant! })
            .ToListAsync(ct);
        if (tasks.Count == 0) return System.Array.Empty<ReminderMessage>();

        // Belt-and-braces stop signal: anyone already holding a CONFIRMED Master Class seat
        // (matched by email) is dropped even if their task hasn't been reconciled yet.
        var confirmedEmails = (await _db.MasterClassSignups
                .Where(s => s.EventId == eventId && s.Status == MasterClassSignupStatus.Confirmed)
                .Select(s => s.Attendee.Email)
                .ToListAsync(ct))
            .Select(e => e.Trim().ToLowerInvariant())
            .ToHashSet();

        // §234: a soft-cancelled ATTENDEE must stop receiving the cadence. The mirror keys
        // on the ticket id and one email can legitimately hold BOTH a cancelled and an
        // active ticket — entitlement = ≥1 ACTIVE ticket per email. Only Attendee-role
        // participants map to the mirror; crew roles have no Attendee row and MUST keep
        // their reminders, so the gate below applies to Role == Attendee only.
        // Suppress ONLY when the email HAS mirror rows and NONE are Active — a
        // participant with no mirror row at all (hand-added, tests) keeps the cadence.
        var mirrorRows = (await _db.Attendees
                .Where(a => a.EventId == eventId)
                .Select(a => new { a.Email, a.MirrorState, a.MasterClassInviteSentAt })
                .ToListAsync(ct))
            .Where(a => !string.IsNullOrWhiteSpace(a.Email))
            .GroupBy(a => a.Email.Trim().ToLowerInvariant())
            .ToList();

        var mirror = mirrorRows.ToDictionary(
            g => g.Key, g => g.Any(x => x.MirrorState == MirrorState.Active));

        // §358 (operator 2026-07-26, CRITICAL): "attendees receive 2 emails, both the reminder +
        // welcome email … they should only receive the reminder every 7 days, but never at the
        // initial invite."
        //
        // The §232 rule ("the first reminder waits a full window after the welcome") was anchored on
        // the TASK's CreatedAt — which is NOT when the welcome was sent. For a normal attendee the
        // two nearly coincide, which is why this never showed. But the moment the invite is RE-sent
        // (an organizer §236/§355 reset, a re-provision) the task is OLD while the invite is brand
        // new, so the 14-day gate passed instantly and the chaser went out in the SAME job pass as
        // the welcome — exactly the two mails in the operator's screenshot.
        //
        // The honest anchor is when the invite actually went out; the task date is only a fallback
        // for a participant with no mirror row (hand-added, tests).
        var inviteSentAt = mirrorRows.ToDictionary(
            g => g.Key, g => g.Max(x => x.MasterClassInviteSentAt));

        var messages = new List<ReminderMessage>();
        foreach (var t in tasks)
        {
            // §253 G9: a DEACTIVATED login is never nagged — the same gate the party
            // sibling has (AttendeePartyReminderBuilder). The §234 mirror gate below
            // only covers ticket-cancelled attendees; an organizer-deactivated one
            // (ticket still Active) needs this participant-level gate.
            //
            // §499 — through the SHARED predicate now. `IsActive` ALONE also let through someone
            // still awaiting onboarding (IsActive true but LifecycleState not Active): a person who
            // cannot even sign in, so "go and choose a Master Class" was unactionable advice.
            // Remindable requires BOTH, exactly as PinIdentityProvider does for login.
            if (!t.Participant.IsRemindable()) continue;
            if (confirmedEmails.Contains(t.Participant.Email.Trim().ToLowerInvariant())) continue;
            // §234: cancelled attendee (mirror rows exist but no ACTIVE ticket) ⇒ no nag.
            if (t.Participant.Role == ParticipantRole.Attendee
                && mirror.TryGetValue(
                    (t.Participant.Email ?? string.Empty).Trim().ToLowerInvariant(),
                    out var hasActive)
                && !hasActive)
                continue;

            // §232 (operator 2026-07-07): the FIRST reminder fires no earlier than a full window
            // after the welcome — the welcome itself is the day-0 nudge.
            // §358: anchor on when the INVITE WAS SENT, not the task's creation. See the comment
            // where inviteSentAt is built: the old task-date anchor let the chaser fire in the same
            // pass as a re-sent welcome, so the attendee got both mails at once.
            var emailKey = (t.Participant.Email ?? string.Empty).Trim().ToLowerInvariant();
            var sentAt = inviteSentAt.TryGetValue(emailKey, out var s) ? s : null;

            // Anchor on the LATER of the two known signals. The invite stamp is the honest anchor
            // (§358), but it can be NULL right after an organizer §355 reset — and then the task
            // date must still be fresh, which is why the reset service re-stamps CreatedAt. Taking
            // the later of the two means neither a stale task nor a cleared stamp can let the
            // chaser slip out alongside the welcome.
            var anchorSource = sentAt is { } sent && sent > t.CreatedAt ? sent : t.CreatedAt;
            var anchor = DateOnly.FromDateTime(anchorSource.UtcDateTime);
            if (today < anchor) continue;

            // 🔒 §707.11 — DUE = lastSent + interval (§707.10), not a calendar window. The first send
            // still waits a full interval from the anchor above.
            var occasionRoot = $"masterclass-attendee:{t.Id}";
            var lastSent = lastSentByOccasion.TryGetValue(occasionRoot, out var ls)
                ? ls : (DateOnly?)null;
            if (!EmailReminderCadenceService.IsDue(today, anchor, lastSent, intervalDays)) continue;

            var firstName = string.IsNullOrWhiteSpace(t.Participant.FullName)
                ? "there"
                : t.Participant.FullName.Split(' ')[0];

            var tokens = _templates.NewTokenSet(t.Participant.Id);
            tokens["firstName"] = firstName;
            tokens["communityName"] = ev.CommunityName ?? string.Empty;
            tokens["eventDisplayName"] = ev.DisplayName ?? string.Empty;
            tokens["taskTitle"] = "Select your Master Class";
            tokens["dueDate"] = string.Empty;
            tokens["state"] = "still open";
            tokens["taskLink"] = "Open the hub to choose your Master Class.";
            RoleContact.AddTo(tokens, t.Participant.Role, EmptyPlaceholders, DefaultSupportEmail);
            var rendered = _templates.Render(TemplateName, tokens);

            messages.Add(new ReminderMessage(
                RecipientEmail: t.Participant.Email,
                ReminderType: ReminderTypeName,
                // §707.11 — the occasion is the DAY now; "due" is decided above by lastSent+interval.
                OccasionKey: $"{occasionRoot}:{today:yyyyMMdd}",
                Subject: rendered.Subject,
                HtmlBody: rendered.HtmlBody,
                Persona: Email.OnboardingEmailSets.PersonaFor(t.Participant.Role).ToString(),
                ParticipantId: t.Participant.Id,
                RecipientName: t.Participant.FullName, MailKey: MailKeyName));
        }

        return messages;
#pragma warning restore CS0162
    }

    private const string DefaultSupportEmail = "info@expertslive.dk";
    private static readonly IReadOnlyDictionary<string, string> EmptyPlaceholders =
        new Dictionary<string, string>();
}
