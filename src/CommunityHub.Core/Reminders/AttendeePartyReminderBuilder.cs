using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// §177 + §206/§207/§208 — the party-RSVP reminder cadence for CREW <b>and</b> ATTENDEES.
/// Anyone with an OPEN party sign-up task (any role) is reminded on a repeating
/// <b>every-2-weeks</b> cadence anchored on <b>their welcome</b> (the party task's
/// creation). §232 (operator 2026-07-07): the FIRST reminder fires no earlier than 2 weeks
/// AFTER the welcome — the welcome itself is the day-0 nudge. The cadence stops the moment
/// they RSVP (the party-form task flips to Done, or a PartyRsvp row appears).
///
/// <para>Operator decision 2026-06-30: ONE consistent cadence for ALL roles — the old
/// "nothing before 2026-12-01, every 7 days" gate is removed. The cadence is anchored
/// per-participant on their party task's <see cref="ParticipantTask.CreatedAt"/> (≈ when they
/// were welcomed/provisioned), so a person onboarded later starts their own 2-week clock.</para>
///
/// <para>Stateless + self-healing like the rest of the reminder engine: the OccasionKey
/// embeds the 14-day WINDOW index since that anchor, so the <see cref="ReminderEngine"/> ledger
/// sends exactly one reminder per window even if a daily run is missed. No new column — the
/// cadence is derived from the clock + the per-task anchor. Reuses the shared
/// <c>task-deadline-reminder</c> template, so the recipient gets the §190 magic-link button +
/// §191 readable button + the [ELDK27] subject postfix from the send chokepoint.</para>
/// </summary>
public sealed class AttendeePartyReminderBuilder
{
    /// <summary>
    /// The BODY still renders the shared task template — the wording is identical.
    /// </summary>
    private const string TemplateName = "task-deadline-reminder";

    /// <summary>
    /// 🔒 §707.11 — the MAIL IDENTITY, which is NOT the template it renders. This chaser was split out
    /// of <c>task-deadline-reminder</c> so it can carry its own ring and its own cadence; sharing one
    /// key meant the speaker's due-date reminder and this fortnightly attendee chase could never be
    /// controlled apart.
    /// </summary>
    public const string MailKeyName = "attendee-party-reminder";

    /// <summary>The reminder TYPE, i.e. the ledger key this mail's history is stored under.</summary>
    public const string ReminderTypeName = "attendee-party";

    /// <summary>Shipped default days between repeats — every 2 weeks (operator 2026-06-30).</summary>
    public const int IntervalDays = 14;

    private readonly CommunityHubDbContext _db;
    private readonly EmailTemplateProvider _templates;
    private readonly TimeProvider _clock;
    private readonly EmailReminderCadenceService? _cadence;

    public AttendeePartyReminderBuilder(
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
            // §881 — the recipient role is passed explicitly now that a cadence can be per-role.
            : await _cadence.GetIntervalDaysAsync(eventId, MailKeyName, ParticipantRole.Attendee, ct);
        IReadOnlyDictionary<string, DateOnly> lastSentByOccasion = await EmailReminderCadenceService.LastSentByOccasionAsync(_db, eventId, ReminderTypeName, ct);

        // 🔒 §733.1 — THIS CADENCE IS RETIRED. Operator 2026-07-31: *"go with (a). get started
        // wizard gets remindes every 14 days. each of the entries dont have due dates. tasks lives
        // outside of this with due dates"*.
        //
        // The party sign-up IS a Get-Started step for every role that has it, so under his model it
        // is chased ONCE by `getstarted-digest` — a 14-day cadence that already stops the moment the
        // wizard reaches 100% — and not a second time per row by this builder.
        //
        // §717 is what the second chase cost: speakers and sponsors received *"task still open: Sign
        // up for the Party"* about something they experience as a wizard step, and replied asking
        // what it was. The digest links to the step; this mail pointed at a task row.
        //
        // 🔑 Left as an EARLY RETURN rather than deleting the builder: it stays registered and
        // documented, so re-enabling is one line if he ever wants a dedicated party chase back —
        // and the reasoning above travels with it. Its tests now assert the silence.
        return System.Array.Empty<ReminderMessage>();

#pragma warning disable CS0162 // Unreachable — deliberately preserved; see §733.1 above.
        // Open party tasks for CREW and ATTENDEES (any role) — not yet RSVP'd ⇒ still Open.
        // The party-form task is flipped to Done by FormTaskReconciler the moment an RSVP is
        // saved, so State is the stop signal. We belt-and-braces also drop anyone who already
        // has a PartyRsvp row. The per-task CreatedAt anchors each person's 2-week cadence.
        var key = PartyTaskSeeder.PartyTaskKey + ":";
        var tasks = await _db.Tasks
            .Where(t => t.EventId == eventId
                        && t.State != TaskState.Done
                        && t.AssignedParticipantId != null
                        && t.SourceKey != null
                        && t.SourceKey.StartsWith(key))
            .Select(t => new { t.Id, t.CreatedAt, Participant = t.AssignedParticipant! , WelcomeSentAt = t.AssignedParticipant!.WelcomeWithLoginSentAt })
            .ToListAsync(ct);
        if (tasks.Count == 0) return System.Array.Empty<ReminderMessage>();

        var answered = (await _db.PartyRsvps
                .Where(r => r.EventId == eventId && r.ParticipantId != null)
                .Select(r => r.ParticipantId!.Value)
                .ToListAsync(ct))
            .ToHashSet();

        // §234: a soft-cancelled ATTENDEE must stop receiving the cadence. The mirror keys
        // on the ticket id and one email can legitimately hold BOTH a cancelled and an
        // active ticket — entitlement = ≥1 ACTIVE ticket per email. Only Attendee-role
        // participants map to the mirror; crew roles have no Attendee row and MUST keep
        // their reminders, so the gate below applies to Role == Attendee only.
        // Load per-email mirror knowledge in one query: an email is SUPPRESSED only when
        // it HAS mirror rows and NONE are Active. An Attendee participant with no mirror
        // row at all (hand-added, tests) is NOT suppressed — absence of the mirror is not
        // evidence of cancellation.
        var mirror = (await _db.Attendees
                .Where(a => a.EventId == eventId)
                .Select(a => new { a.Email, a.MirrorState })
                .ToListAsync(ct))
            .Where(a => !string.IsNullOrWhiteSpace(a.Email))
            .GroupBy(a => a.Email.Trim().ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.Any(x => x.MirrorState == MirrorState.Active));

        // §228: a sponsor company's single GROUP reservation covers EVERY linked contact —
        // once anyone on the team has answered for the group, stop nagging all of them.
        var answeredCompanies = (await _db.PartyRsvps
                .Where(r => r.EventId == eventId && r.ParticipantId != null)
                .Join(_db.Participants, r => r.ParticipantId, p => p.Id,
                    (r, p) => p.SponsorCompanyId)
                .Where(c => c != null && c != "")
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet();

        var messages = new List<ReminderMessage>();
        foreach (var t in tasks)
        {
            // §242: a DEACTIVATED login is never nagged — this is how a suspended 1-day
            // attendee's PRE-EXISTING party task goes quiet while attendee-1day-access is
            // OFF (the sync locks their login out; their task stays but reminders stop).
            // Also the sane rule generally: no reminders to anyone who cannot sign in.
            //
            // §499 — that stated rule is now ENFORCED by the shared predicate rather than
            // approximated by one flag: "cannot sign in" is IsActive AND LifecycleState == Active
            // (what PinIdentityProvider requires), so someone still awaiting onboarding is no
            // longer nagged either.
            if (!t.Participant.IsRemindable()) continue;
            if (answered.Contains(t.Participant.Id)) continue; // already RSVP'd — stop nagging
            // §234: cancelled attendee (mirror rows exist but no ACTIVE ticket) ⇒ no nag.
            if (t.Participant.Role == ParticipantRole.Attendee
                && mirror.TryGetValue(
                    (t.Participant.Email ?? string.Empty).Trim().ToLowerInvariant(),
                    out var hasActive)
                && !hasActive)
                continue;
            if (t.Participant.Role == ParticipantRole.Sponsor
                && !string.IsNullOrWhiteSpace(t.Participant.SponsorCompanyId)
                && answeredCompanies.Contains(t.Participant.SponsorCompanyId))
                continue; // §228: the company group reservation covers this contact

            // The 2-week cadence is anchored on the welcome (the task's creation), and the
            // FIRST reminder fires no earlier than 2 weeks AFTER it (§232, operator
            // 2026-07-07) — the welcome itself is the day-0 nudge, so window 0 sends
            // nothing. A task created in the future (clock skew) is simply not due yet.
            // §358: anchor on when the WELCOME was actually sent, not the task date. The rule is
            // "quiet for a full window after the welcome"; a RE-sent welcome (organizer §236/§355
            // reset, a re-provision) leaves an OLD task beside a NEW welcome, and the task-date
            // anchor then let the chaser fire in the same job pass as the welcome.
            //
            // 🔒 §738 — and the case §358 MISSED: never welcomed at all. Falling back to the task
            // date made a missing stamp read as "welcomed when the task was created" — months ago —
            // so the person was maximally overdue and fired the moment their ring opened. That is
            // how "task still open: Sign up for the Party" reached sponsors at 08:00 on 2026-07-31
            // whose welcome did not go out until 08:45. Same rule as the digest: a null date must
            // never read as "long ago". Never welcomed ⇒ never chased.
            if (t.WelcomeSentAt is null) continue;

            var anchor = DateOnly.FromDateTime(t.WelcomeSentAt.Value.UtcDateTime);
            if (today < anchor) continue;

            // 🔒 §707.11 — DUE = lastSent + interval, not a calendar window. §707.10 explains why the
            // old `daysSince / IntervalDays` was a different rule: a send delayed by a ring drop
            // delivered late in its window while the next window opened on schedule, so one person
            // could get two mails a day apart. The FIRST send still waits a full interval from the
            // welcome (§232). The interval is the operator's, per mail, defaulting to the shipped 14.
            var occasionRoot = $"party-attendee:{t.Id}";
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
            tokens["taskTitle"] = "Sign up for the Party";
            tokens["dueDate"] = string.Empty;
            tokens["state"] = "still open";
            tokens["taskLink"] = "Open the hub to RSVP for the party.";
            RoleContact.AddTo(tokens, t.Participant.Role, EmptyPlaceholders, DefaultSupportEmail);
            var rendered = _templates.Render(TemplateName, tokens);

            messages.Add(new ReminderMessage(
                RecipientEmail: t.Participant.Email,
                ReminderType: ReminderTypeName,
                // §707.11 — the occasion is now the DAY, not a window index: "due" is decided above
                // by lastSent + interval, and this key's only remaining job is to stop the SAME run
                // (or a same-day re-run) sending twice.
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
