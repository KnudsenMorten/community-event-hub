using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Email;

/// <summary>
/// Static metadata for the shipped email templates (REQUIREMENTS §25h): a friendly title
/// and the FeatureCatalog key whose released ring governs each template. The editor uses
/// this to show, per template, a human name + the per-template ring (and to let the
/// organizer dial that ring). Templates with no specific feature map to the
/// <c>outbound-email</c> transport (kill-switch only). Keys are the on-disk file names
/// without ".html".
/// </summary>
public static class EmailTemplateCatalog
{
    /// <summary>templateKey → (Title, FeatureKey). Many templates share one feature.</summary>
    public static readonly IReadOnlyDictionary<string, (string Title, string FeatureKey)> Map =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["welcome"]                    = ("Welcome", "welcome-email"),
            ["welcome-speaker"]            = ("Welcome: speaker", "welcome-email"),
            // 🔒 §726 — SPLIT BY SpeakerCategory (operator 2026-07-31). All three RENDER
            // welcome-speaker.html and differ only in the opening clause (WelcomeVariants
            // .SpeakerIntroHtml): a community speaker was SELECTED and is congratulated, a
            // sponsor-brought or hired guest speaker was not. Three KEYS rather than three files,
            // because the ring is per (mail × role) — so this is what gives each category its own
            // ring, exactly as §707.11 did for the task chasers. `welcome-speaker` stays for a
            // speaker whose category is not set yet.
            ["welcome-speaker-community"]  = ("Welcome: speaker (community)", "welcome-email"),
            ["welcome-speaker-guest"]      = ("Welcome: speaker (guest)", "welcome-email"),
            ["welcome-speaker-sponsor"]    = ("Welcome: speaker (sponsor)", "welcome-email"),
            ["welcome-volunteer"]          = ("Welcome: volunteer", "welcome-email"),
            ["welcome-sponsor"]            = ("Welcome: sponsor", "welcome-email"),
            ["welcome-media"]              = ("Welcome: media", "welcome-email"),
            ["welcome-eventpartner"]       = ("Welcome: event partner", "welcome-email"),
            // §241: the selection invite IS the 2-day attendee welcome (§215) — its send
            // path tags FeatureKey welcome-email + AttendeeWelcome, so the editor must
            // show the ring that actually governs it.
            ["masterclass-selection-invite"] = ("Master Class selection invite (2-day welcome)", "welcome-email"),
            // §252 F5: the WHOLE Master Class funnel rides the welcome-email ring —
            // the masterclass-invites key was removed so raising ONE ring at go-live
            // can never split the funnel (invited but never confirmed, or vice versa).
            ["masterclass-confirmed"]      = ("Master Class confirmed seat", "welcome-email"),
            ["masterclass-waitlisted"]     = ("Master Class waitlisted", "welcome-email"),
            ["masterclass-cancelled"]      = ("Master Class cancelled", "welcome-email"),
            // §243: TICKET cancelled (2-day holder soft-cancelled in the mirror) — a
            // different event from the SEAT cancellation above. Rides the welcome-email
            // ring (the same gate as the §241 selection-invite welcome), no §217 cap.
            ["masterclass-cancelled-ticket"] = ("Ticket cancelled (2-day holder)", "welcome-email"),
            ["masterclass-reassignment"]   = ("Master Class reassignment validation", "welcome-email"),
            ["masterclass-offer"]          = ("Master Class seat offer", "welcome-email"),
            ["masterclass-promoted"]       = ("Master Class waitlist promotion", "welcome-email"),
            // masterclass-month-reminder REMOVED from the catalog (§244, operator
            // 2026-07-07: "drop this reminder 1 month before, it's too confusing") —
            // MasterClassMonthReminderJob is retired; the template file stays on disk
            // only so historic sends can still be re-rendered from the email log.
            ["pin-signin"]                 = ("Sign-in code (PIN)", "outbound-email"),
            // §779 — the Signal join links, mailed because the participant pressed "send them to
            // me". Get Started is filled in on a desktop, and a signal.group link only does anything
            // on a device with Signal installed, so this mail is the bridge to their phone.
            // 🔒 RING-EXEMPT (see RingExemptTemplates): user-initiated, and a ring drop would leave
            // somebody watching an inbox for a mail a rollout ring silently ate.
            ["signal-join-links"]          = ("Signal join links (sent on request)", "outbound-email"),
            ["calendar-invite"]            = ("Activation calendar invite", "outbound-email"),
            ["session-evaluation-results"] = ("Session evaluation results", "session-eval-email"),
            // §750 C7/C0 — the analysis engine's "report ready" notification. Its OWN mail identity,
            // so it resolves its own (mail × role) ring, on the SAME feature switch as the results
            // mail above. ⚠️ A NEW identity needs its ring ROW in the target environment: that row is
            // DATA, it does NOT travel with a deploy, and a missing one means this mail reaches
            // NOBODY — silently. Check /Organizer/Settings after deploying.
            ["session-evaluation-report-ready"] = ("Session evaluation report ready", "session-eval-email"),
            // 🗑 §705.12 — "invitation" and "broadcast" DELETED 2026-07-29 (verified unused: zero
            // PROD SentReminders rows for either). Invitation was an early access mail superseded by
            // the welcome mails, which carry a magic link; broadcast was free-form mass mail whose
            // subject and audience were chosen at send time, so it could never carry a fixed name or
            // a per-role ring — the one mail that broke the §705 model.
            ["task-deadline-reminder"]     = ("Task deadline reminder", "reminder-jobs"),
            // 🔒 §707.11 — SPLIT OUT OF `task-deadline-reminder` (operator 2026-07-30: *"split them,
            // i agree"*). All three RENDER the same `task-deadline-reminder.html` body, but they are
            // three different mails: one fires on a task's due date to ANY role, the other two chase
            // ATTENDEES every N days about the party and about picking a Master Class. One key meant
            // one ring and one cadence for all three, so "control it per mail" was impossible — the
            // §705.15 naming defect ("it is too generic in the naming") in another place.
            ["attendee-party-reminder"]        = ("Attendee: party sign-up chaser", "reminder-jobs"),
            ["attendee-masterclass-reminder"]  = ("Attendee: Master Class task chaser", "reminder-jobs"),
            ["task-manual-reminder"]       = ("Manual task reminder", "reminder-jobs"),
            // 🔒 §879.2 — "digest" is banned vocabulary (operator: *"hate that word digest, dont use
            // it and dont understand it"*). The KEY is a live DB row and NEVER moves (§595/§642: the
            // ring row and the operator's switch are keyed on it); only the DISPLAY changes.
            ["speaker-question-digest"]    = ("Speaker Q&A round-up", "digest-emails"),
            ["onboarding-getting-started"] = ("Onboarding: getting started", "welcome-email"),
            // §250: the biweekly Get-Started-incomplete digest (wizard steps ONLY) —
            // built by GetStartedDigestBuilder, sent by ReminderJob, ring-gated at the
            // transport under the welcome-email feature (same key it is filed under here).
            ["getstarted-digest"]          = ("Get Started still unfinished", "welcome-email"),   // §879.2 — display only; key unchanged
            // §326b: the ONE-SHOT speaker "complete Get Started before the deadline"
            // reminder (GetStartedDeadlineReminderBuilder; dates in the speaker-deadlines
            // config) — same welcome-email transport ring as the digest.
            ["getstarted-deadline-reminder"] = ("Get Started deadline reminder", "welcome-email"),
            // §326bs: the 3-days-ahead warning to ORGANIZERS before a hotel release
            // deadline, carrying that hotel's live over/under per night
            // (HotelCutoffReminderBuilder). Internal ops mail — organizers only.
            ["hotel-cutoff-reminder"]      = ("Hotel release-deadline reminder", "reminder-jobs"),
            ["onboarding-step-reset"]      = ("Onboarding step reset", "onboarding-step-reset"),
            ["travel-reimbursement-paid"]  = ("Travel reimbursement paid", "travel-reimbursement-email"),
            ["group-photo-invite"]         = ("Group-photo invite", "group-photo-invites"),
            ["app-game-gift-reminder"]     = ("App-game gift reminder", "sponsor-reminders"),
            // §1127: chases a sponsor whose WEBSHOP website or LinkedIn is blank. Since §1125/§1126
            // the webshop OWNS those fields and the CEH inputs are read-only, so a blank can only be
            // fixed at the source — which is why this mail exists rather than a CEH task.
            // ⚠️ X/Twitter is deliberately NOT chased (operator: "twitter is optional").
            ["sponsor-webshop-links-missing"] = ("Sponsor webshop links missing", "sponsor-reminders"),
            ["volunteer-help-raised"]      = ("Volunteer help raised", "outbound-email"),
            ["sponsor-leads-digest"]       = ("Sponsor leads round-up", "sponsor-leads"),          // §879.2 — display only; key unchanged
            // §26c (2026-06-24): the only attendee chaser now — a 2-day-ticket holder
            // who hasn't selected a master class IN-HUB yet. (attendee-missing-booking,
            // attendee-missing-ticket and attendee-duplicate-booking were removed:
            // master classes are in-hub one-seat, and the pull is filtered to 2-day buyers.)
            ["pending-master-class-selection"] = ("Attendee: pending master class selection", "attendee-reconcile"),
            // §26c "Help Promote": notify a speaker when their promo graphics are released.
            ["speaker-graphics-ready"]     = ("Speaker: promo graphics ready", "speaker-graphics-promote"),
            // §1060(b) — ONE template for BOTH audiences and BOTH sends (scheduled · day before).
            // 🛑 Deliberately separate from `speaker-graphics-ready` above, though they can land in
            // the same minute: that one says "your artwork is ready, go promote it", this one says
            // "ELDK is announcing you, here is when". Operator 2026-08-11: *"that template is
            // different and separate"*.
            ["some-announcement"]          = ("Speaker/sponsor: your announcement is scheduled", "some-scheduling"),
            // §705.13 (operator 2026-07-29: "session-change-alert is just an email" … "if you have
            // not created it as an email, then you need to change that"). Both templates SHIP and
            // SEND today but had NO registry entry, so neither appeared on the Settings page — a
            // direct breach of "everything targetting one of the roles … must be defined in settings,
            // no exception".
            //
            // 🔑 Registering the first one also FIXES A MIS-CLASSIFICATION by itself:
            // `FeatureCatalog.EmailFeatureKeys` derives "is this an email key?" from the templates
            // that declare it, so `session-change-alerts` was filed as a FEATURE (ring decides who
            // SEES something) purely because its template was missing here. It is a mail to speakers.
            ["session-time-location-changed"] = ("Session time/location changed", "session-change-alerts"),
            // Retired in code by §299 OPEN-26 (operator 2026-07-23: 1-day attendees get no welcome).
            // Registered anyway so it is VISIBLE with its ring rather than invisible — and so the ring
            // is already correct on the day the code path returns. 🔒 Re-enabling it is a DEPLOY, not
            // a toggle: AttendeeOneDayWelcomeEmailService.SendForProvisioningAsync returns false.
            ["welcome-attendee-1day"]      = ("Welcome: 1-day attendee (retired in code)", "welcome-email"),
            // §704.1c — ORGANIZER ops mail from SoMeDispatchService. Added by the 2026-07-29 audit:
            // both reach a ROLE (organizers) but had no internal name and no Settings row, so they
            // were invisible on the page. Operator's rule: "everything targetting one of the roles
            // with an email has a subject + internal name and must be defined in settings, no
            // exception". They are RingExempt at the send site (time-critical ops alerts to a
            // designated organizer, never a participant), so they list without a ring.
            ["some-speaker-prealert"]      = ("SoMe: speaker post publishes in ~5 min", "some-scheduling"),
            ["some-published"]             = ("SoMe: company-page post published", "some-scheduling"),
            // §705.14 — the three mails the audit found reaching a ROLE with no identity at all: they
            // compose their bodies in code, so they had no template key and therefore no Settings row and
            // no ring of their own. Their send sites now pass these keys as EmailContext.TemplateName.
            //
            // 🔑 Each is ONE mail serving SEVERAL roles, which is exactly what §705.3b's per-role ring is
            // for: masterclass-* reach speakers AND attendees of that class; task-allocation-committed
            // reaches volunteers on a volunteer commit and organizers on an organizer commit. One key,
            // independently controllable audiences.
            ["masterclass-question-posted"]      = ("Master Class: new Q&A question", "masterclass-notifications"),
            ["masterclass-instructions-updated"] = ("Master Class: instructions updated", "masterclass-notifications"),
            ["task-allocation-committed"]        = ("Task allocation committed", "volunteer-allocation"),
            // §705.15 — `hotel-invite` was ONE generic name over three different behaviours, which is why
            // it read as "the hotel mail" to everyone including its author. Split by what each actually
            // does. (The retired submit-time auto-invite, §512, is deliberately NOT re-created.)
            //
            // 🔒 NO TEMPLATE FILES, and that is the right call: HotelEmailContentBuilder composes the body
            // with THREE intro variants plus conditional address / room-type / confirmation blocks, while
            // the renderer only does flat {{token}} substitution. Extraction would either explode into a
            // combinatorial set of files or leave the wording in code as token values — a template holding
            // almost nothing. The mail IDENTITY does not need a file (see the three mails above), so the
            // naming and the ring are delivered without touching guest-facing wording.
            ["hotel-confirmation-guest"]  = ("Hotel confirmation to guests", "hotel-invite"),
            ["hotel-calendar-selfsend"]   = ("Hotel: guest sent themselves the calendar entry", "hotel-invite"),
            // §705.15a — `calendar-invite` was generic across several unrelated mails. Named the two whose
            // send sites are verified; all are user-initiated so all stay ring-EXEMPT, and the split
            // exists purely so the page says WHICH mail a row is.
            //
            // ◻ The remaining calendar callers (Master Class entry, the wizard paths) still use the
            // generic `calendar-invite`. Deliberately NOT named yet: inventing a key that no send site
            // passes would create a registry row governing nothing — the §326bx/§699 defect this whole
            // section exists to remove. Name them when their send sites are confirmed.
            ["calendar-dinner"]           = ("Appreciation-dinner calendar invite", "outbound-email"),
            // §707.27 F — the last three code-sent mails that had no registry row at all. Registered
            // READ-ONLY for VISIBILITY, exactly as §704.1c did for the two SoMe ops mails: all three
            // are RingExempt at the send site, so they list with their subject and a stated reason
            // instead of a ring control. The operator confirmed `engine-alert` needs no ring.
            //
            // 🔒 None of them reaches a PARTICIPANT — they go to an ERP mailbox, the organizer ops
            // inbox and the engine alert address. That is WHY they are exempt, and it is also why
            // registering them costs nothing: there is no audience a ring could narrow.
            ["travel-reimbursement-erp"]  = ("Travel reimbursement claim to ERP", "travel-reimbursement-email"),
            ["feedback-intake"]           = ("AiHelper: bug / feature / question intake", "outbound-email"),
            ["engine-alert"]              = ("Engine alert (internal failure notice)", "outbound-email"),
        };

    /// <summary>The feature key governing a template's ring (outbound-email transport when unmapped).</summary>
    public static string FeatureKeyFor(string templateKey) =>
        Map.TryGetValue(templateKey, out var v) ? v.FeatureKey : "outbound-email";

    /// <summary>A friendly title for a template (the key itself when unmapped).</summary>
    public static string TitleFor(string templateKey) =>
        Map.TryGetValue(templateKey, out var v) ? v.Title : templateKey;

    /// <summary>
    /// §707.11 — the RECURRING mails: those that chase a GOAL until the goal is complete, and so
    /// carry an operator-settable repeat interval beside their ring.
    /// </summary>
    /// <remarks>
    /// <para>🔒 <b>Only these get a cadence control.</b> A confirmation, a receipt or a welcome fires
    /// once because the thing it reports already happened — offering it a "repeat every N days" box
    /// would be a control that governs nothing, which is the §326bx defect this codebase keeps
    /// removing. The value is the SHIPPED DEFAULT; an operator override lives in
    /// <c>EmailReminderCadence</c>.</para>
    ///
    /// <para>The string is the COMPLETION CONDITION, shown on the Settings page so the row says what
    /// makes the chasing stop — otherwise "repeat every 14 days" reads like an unbounded nag.</para>
    ///
    /// <para>⚠️ <b>`task-deadline-reminder` and `pending-master-class-selection` were ONCE-EVER until
    /// 2026-07-30</b>; the operator's instruction was *"the 2 must follow same pattern"*, so they now
    /// repeat like the rest. **This is the one behaviour change in the §707.11 batch that moves real
    /// mail** — everything else defaults to what it already did.</para>
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, (int DefaultIntervalDays, string StopsWhen)>
        RecurringMails = new Dictionary<string, (int, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["getstarted-digest"] =
                (14, "Stops as soon as that person's Get Started wizard is 100% complete."),
            ["task-deadline-reminder"] =
                (14, "Stops when the task is marked done. Starts on the task's due date."),
            ["attendee-party-reminder"] =
                (14, "Stops when the attendee answers the party question."),
            ["attendee-masterclass-reminder"] =
                (14, "Stops when the attendee's Master Class task is done."),
            ["pending-master-class-selection"] =
                (14, "Stops as soon as the attendee selects a Master Class."),
        };

    /// <summary>True when this mail chases a goal and therefore has a repeat interval.</summary>
    public static bool IsRecurring(string templateKey) => RecurringMails.ContainsKey(templateKey);

    /// <summary>
    /// §707.25 — mails that are RETIRED IN CODE: registered and visible, but nothing sends them
    /// today and re-enabling one is a DEPLOY, not a switch.
    /// </summary>
    /// <remarks>
    /// Operator 2026-07-30: *"as this is retired in code, it should not be shown here — maybe add so
    /// i can tick on to show retired features in code"*. They stay REGISTERED on purpose (so the ring
    /// is already right on the day the code path returns, and so they are not invisible), but they are
    /// hidden from the default view because a row nobody can act on is noise on a page whose whole
    /// job is to be trustworthy. The Settings page states the hidden COUNT, so this never becomes a
    /// lie by omission.
    /// </remarks>
    public static readonly IReadOnlySet<string> RetiredInCodeTemplates =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // §299 OPEN-26 — AttendeeOneDayWelcomeEmailService.SendForProvisioningAsync returns false.
            "welcome-attendee-1day",
        };

    /// <summary>True when nothing in the shipped code sends this mail today.</summary>
    public static bool IsRetiredInCode(string templateKey) =>
        RetiredInCodeTemplates.Contains(templateKey);

    /// <summary>The shipped default repeat interval in days, or null when the mail is not recurring.</summary>
    public static int? DefaultIntervalDaysFor(string templateKey) =>
        RecurringMails.TryGetValue(templateKey, out var v) ? v.DefaultIntervalDays : null;

    /// <summary>What makes this mail stop chasing (empty when it is not recurring).</summary>
    public static string StopsWhenFor(string templateKey) =>
        RecurringMails.TryGetValue(templateKey, out var v) ? v.StopsWhen : string.Empty;

    /// <summary>
    /// §704.1c — mails whose SEND SITE is <c>RingExempt</c>, so no ring can ever apply to them.
    /// </summary>
    /// <remarks>
    /// 🔒 <b>These must NOT be offered a ring control on the Settings page.</b> Showing one is the
    /// §326bx defect verbatim — a ring badge that governs nothing — which is what cost the operator
    /// his confidence in this page. The audit that found them (2026-07-29) also found that
    /// <c>calendar-invite</c> was the single catalog template with no explicit PROD ring row, and
    /// this is why: a ring could never have applied to it in the first place.
    ///
    /// <para>The exemptions themselves are CORRECT and stay. Both are <b>participant-CLICKED</b>
    /// mail — someone asked for it and is waiting for it — and the standing rule is that such mail
    /// is never ring-gated, because hearing nothing back due to a rollout ring is undiagnosable by
    /// the person affected (§326av, and §589 for the sign-in code).</para>
    ///
    /// <para>The page must therefore say <i>"always sent — not ring-gated, because you asked for
    /// it"</i>, which is honest, rather than showing a ring that does nothing.</para>
    /// </remarks>
    public static readonly IReadOnlySet<string> RingExemptTemplates =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "pin-signin",      // PinLoginService — the sign-in code must reach any ring.
            "calendar-invite", // CalendarInviteEmailService — the person pressed "add to calendar".
            // §705.15a — the three mails `calendar-invite` used to stand for. All USER-INITIATED
            // (operator 2026-07-29: *"any user-initiated mail are not ring-gated"*), so all exempt.
            // 🔒 `hotel-confirmation-guest` is deliberately ABSENT: that one is ORGANIZER-triggered —
            // he enters a confirmation number and every guest in the hotel is mailed. The guest did
            // not ask for it, so it keeps a ring (§705.14c).
            "calendar-dinner",
            "hotel-calendar-selfsend",
            // §779 — SignalFormService: they pressed "send me the join links" and are about to walk
            // to another device to use them. Same rule as the two above (user-initiated mail is never
            // ring-gated), and the failure it prevents is specific: a ring drop leaves somebody
            // refreshing a phone inbox for a mail that was never sent, with nothing to diagnose.
            "signal-join-links",
            // §326ca — organizer ops alerts. Time-critical (~5 minutes to paste a speaker handle
            // before a post goes live) and sent to ONE designated organizer, never a participant.
            // An ops alert must not be silenceable by a participant rollout ring.
            "some-speaker-prealert",
            "some-published",
            // §707.27 F — the three internal mails registered read-only for visibility. Each send
            // site already passes `RingExempt: true`; this list is what stops the page from offering
            // a ring control that would govern nothing (§326bx). None reaches a participant.
            "travel-reimbursement-erp",
            "feedback-intake",
            "engine-alert",
            // 🔒 §707.20 — `masterclass-cancelled` was HERE and is now RING-GATED again, by operator
            // decision 2026-07-30: *"this is wrong, as it should also be ring-gated. set it for ring
            // 2 … it is a similar mail as any other mail"*.
            //
            // History, so this is not flip-flopped again by someone reading only half of it: §346
            // made the send `RingExempt` under the §326by rule that participant-CLICKED mail is never
            // ring-gated (they pressed "give up my seat" and are waiting for the receipt). §707.3
            // then found the send site exempt while this list was not, and closed the gap by adding
            // it here. He has now overruled the CLASSIFICATION itself: it is an ordinary attendee
            // mail and takes an ordinary ring.
            //
            // ⚠️ The consequence he accepted: an attendee OUTSIDE the released ring who cancels a
            // seat now gets no confirmation back.
        };

    /// <summary>True when no ring can apply to this mail (see <see cref="RingExemptTemplates"/>).</summary>
    public static bool IsRingExempt(string templateKey) => RingExemptTemplates.Contains(templateKey);

    /// <summary>
    /// §818 — the mails that go to a MAILBOX, never to a person in a role.
    /// </summary>
    /// <remarks>
    /// <para>Deliberately NOT the same set as <see cref="EmailAudience.Organizer"/>, which mixes
    /// these with real organizer PARTICIPANT mail such as <c>hotel-cutoff-reminder</c>. That
    /// distinction is exactly what the organizer Comms cockpit has to answer, so it is recorded here
    /// beside the registry rather than re-derived by whoever is reading a log.</para>
    ///
    /// <para>🔒 <b>Do not use the RECIPIENT ADDRESS to answer this.</b> That was tried, and the DEV
    /// render disproved it: engine alerts go to <c>mok@</c>, which is ALSO the organizer's own
    /// participant address, so an address test files 252 alerts as his personal mail — the precise
    /// burial §815.1 forbids. What a mail IS does not depend on who happens to read it.</para>
    /// </remarks>
    public static readonly IReadOnlySet<string> InternalMailboxTemplates =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "engine-alert",
            "feedback-intake",
            "travel-reimbursement-erp",
            "evaluation-consolidated",
        };

    /// <summary>
    /// §818 — true when a logged mail is OPS mail rather than a participant's.
    /// </summary>
    /// <remarks>
    /// A REGISTERED <paramref name="templateKey"/> that is not an internal-mailbox mail identifies a
    /// participant's mail. Everything else — no identity at all, an unregistered one, or an internal
    /// one — is ops.
    ///
    /// <para>🔑 <b>A blank template counts as ops on purpose.</b> Ops senders carry no template
    /// identity, measured against PROD on 2026-08-04: all 364 <c>engine-alert</c> / <c>other</c> /
    /// <c>feedback-intake</c> rows have none, while every <c>session-eval</c> row has one. And of the
    /// two ways to be wrong, showing a participant's mail inside a clearly labelled ops section is
    /// far milder than hiding a failure alert inside one person's row.</para>
    /// </remarks>
    public static bool IsInternalMailboxMail(string? templateKey) =>
        string.IsNullOrWhiteSpace(templateKey)
        || InternalMailboxTemplates.Contains(templateKey)
        || !Map.ContainsKey(templateKey);

    /// <summary>
    /// §704.1b — the real subject of a mail whose body is composed IN CODE rather than from a
    /// template file, so every registry row can show a subject.
    /// </summary>
    /// <remarks>
    /// 🔒 The operator's rule is <i>"everything targetting one of the roles with an email has a
    /// subject + internal name … no exception"</i>. A mail with no template FILE has no
    /// <c>Subject:</c> line to read, so without this it would list with a friendly title only — the
    /// exact ambiguity §695.1 exists to remove. Where the body is composed is an internal detail and
    /// must not change what he sees.
    ///
    /// <para>⚠️ Keep these in step with the send site. They are duplicated strings, which is why the
    /// set is deliberately tiny: prefer a real template file for anything new.</para>
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, string> InlineSubjects =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // §707.27 C3 — the EVENT NAME replaced the "[SoMe]" tag (operator 2026-07-30: *"subject
            // for these 2 should shown ELDK27 (eventname) instead of [SOME]"*). The token is shown
            // UNRESOLVED per §695.1, and these MUST be changed in the same commit as
            // `SoMeDispatchService`'s send sites — the page reads this map, so a lone edit here (or
            // there) makes the row lie about the mail that actually goes out.
            ["some-speaker-prealert"] = "{event}: Speaker post publishes in ~5 min — insert the LinkedIn handle",
            ["some-published"]        = "{event}: A LinkedIn company-page post was published",
            // §705.14 — subjects are built per send from the class title / the person's assignments, so
            // the token is shown UNRESOLVED exactly as §695.1 requires ("a subject with tokens should be
            // shown with the tokens visible, not resolved — that IS how it is identified").
            // §707.11 — the two chasers split out of task-deadline-reminder RENDER that template's
            // body, so they have no file of their own to read a Subject: line from. Kept identical to
            // `task-deadline-reminder.html`'s subject, because that IS the subject that goes out.
            // §726 — the three speaker-category welcomes have no file of their own (they render
            // welcome-speaker.html), so they cannot read a Subject: line. Kept identical to that
            // template's subject, because that IS the subject that goes out.
            ["welcome-speaker-community"]        = "Welcome to {event}",
            ["welcome-speaker-guest"]            = "Welcome to {event}",
            ["welcome-speaker-sponsor"]          = "Welcome to {event}",
            ["attendee-party-reminder"]          = "{event} task {state}: {taskTitle}",
            ["attendee-masterclass-reminder"]    = "{event} task {state}: {taskTitle}",
            ["masterclass-question-posted"]      = "New question in {Master Class title}",
            ["masterclass-instructions-updated"] = "Updated instructions for {Master Class title}",
            ["task-allocation-committed"]        = "Your tasks for [Event] ({n} assignment(s))",
            // §705.15 — built by HotelEmailContentBuilder; the state tag flips with confirmation status,
            // so both forms are shown rather than picking one and misleading him.
            ["hotel-confirmation-guest"]  = "[CONFIRMED] or [PLACEHOLDER] {event} Hotel - {hotel name}",
            ["hotel-calendar-selfsend"]   = "{hotel name} — your stay ({check-in}–{check-out})",

            ["calendar-dinner"]           = "{event}: appreciation dinner",

            // §707.27 F — the three internal mails. Tokens shown UNRESOLVED per §695.1: that IS how
            // he identifies them in a mailbox.
            ["travel-reimbursement-erp"] = "Travel Rebursement - {speaker name} - {event}",
            ["feedback-intake"]          = "AiHelper {bug|feature|question} from {name} [{event}]",
            // §702 — every engine alert carries the environment tag, and the rest of the subject is
            // written at the call site. Showing the tag is the point: it is what tells him whether a
            // failure is DEV or PROD.
            ["engine-alert"]             = "[DEV] or [PROD] {what failed}",
        };

    /// <summary>
    /// §704.1c — the one-line reason a ring-exempt mail is always sent, shown in place of its ring.
    /// </summary>
    public static string RingExemptReason(string templateKey) => templateKey.ToLowerInvariant() switch
    {
        "pin-signin" =>
            "Always sent — never ring-gated. Someone who asks for a sign-in code and hears nothing "
            + "back cannot work out why, so this must reach every ring.",
        "calendar-invite" =>
            "Always sent — never ring-gated. The person pressed \"add to calendar\" and is waiting "
            + "for it; a rollout ring would silently drop a mail they explicitly asked for.",
        // §779 — the Signal join links.
        "signal-join-links" =>
            "Always sent — never ring-gated. They pressed \"email me the join links\" and are about "
            + "to open that mail on a DIFFERENT device, because a Signal link only works on a phone "
            + "with Signal installed. A ring drop would leave somebody refreshing a phone inbox for "
            + "a mail that was never sent, with nothing to tell them why.",
        "some-speaker-prealert" =>
            "Always sent — never ring-gated. It goes to one designated organizer and is "
            + "time-critical: about 5 minutes to paste in the speaker's handle before the post goes "
            + "live. An ops alert must not be silenceable by a participant rollout ring.",
        "some-published" =>
            "Always sent — never ring-gated. It goes to the organizer notification list to confirm a "
            + "company-page post went live, and reaches no participant.",
        // §707.27 F — the three internal mails. Same reasoning in each case: the recipient is a
        // MAILBOX, not a ring-gated person, so there is no audience for a ring to narrow.
        "travel-reimbursement-erp" =>
            "Always sent — never ring-gated. It carries a speaker's reimbursement claim and receipts "
            + "to the ERP mailbox for payment. A ring would silently withhold a claim someone is "
            + "waiting to be paid on.",
        "feedback-intake" =>
            "Always sent — never ring-gated. It forwards a bug, feature idea or question to the "
            + "organizer ops inbox. The person raising it is already talking to you; dropping their "
            + "message on a rollout ring would lose it with nobody the wiser.",
        "engine-alert" =>
            "Always sent — never ring-gated (operator-confirmed). It reports that something in the "
            + "engine FAILED, tagged [DEV] or [PROD]. An alert you can switch off by rollout ring is "
            + "an alert you cannot trust.",
        "hotel-calendar-selfsend" =>
            "Always sent — never ring-gated. The guest pressed \"Add to calendar\" on their own hotel "
            + "form and is waiting for it.",
        "calendar-dinner" =>
            "Always sent — never ring-gated. The person asked for the dinner entry from their own form.",
        _ => "Always sent — never ring-gated.",
    };

    /// <summary>
    /// §515 — WHO receives this template, so the Settings page can file each mail under its role
    /// instead of dumping every mail into one "E-mails" chapter. Kept as a classifier beside the
    /// map (rather than a third tuple member) so each entry's history and comments stay untouched.
    ///
    /// <para>Audience means the RECIPIENT: the hotel release-deadline warning is *about* speakers
    /// but goes to organizers, so it files under Organizer. An unmapped key falls to
    /// <see cref="EmailAudience.Everyone"/> — the safe default, since it then appears in the
    /// general section rather than hiding under a role it may not belong to.</para>
    /// </summary>
    public static EmailAudience AudienceFor(string templateKey) => templateKey.ToLowerInvariant() switch
    {
        // --- Speakers ---------------------------------------------------------------
        "welcome-speaker"                => EmailAudience.Speaker,
        "welcome-speaker-community"      => EmailAudience.Speaker,   // §726
        "welcome-speaker-guest"          => EmailAudience.Speaker,   // §726
        "welcome-speaker-sponsor"        => EmailAudience.Speaker,   // §726
        "speaker-question-digest"        => EmailAudience.Speaker,
        "speaker-graphics-ready"         => EmailAudience.Speaker,
        // ⚠️ Speaker AND sponsor share this one. Filed under Speaker because that is the larger
        // audience and the enum has no "both" — the per-send recipient list is what actually
        // decides who gets it (SoMeAnnouncementNotifier.AudienceForAsync).
        "some-announcement"              => EmailAudience.Speaker,
        "getstarted-digest"              => EmailAudience.Speaker,
        "getstarted-deadline-reminder"   => EmailAudience.Speaker,
        "onboarding-getting-started"     => EmailAudience.Speaker,
        "onboarding-step-reset"          => EmailAudience.Speaker,
        "session-evaluation-results"     => EmailAudience.Speaker,
        "session-evaluation-report-ready" => EmailAudience.Speaker,   // §750
        "travel-reimbursement-paid"      => EmailAudience.Speaker,
        // §705.13 — a speaker whose session moved room or time. Operator: "session-time-location-
        // changed must go to speakers and be sent in case of ceh changes where a session is moved to
        // new room, then speaker must be notified."
        "session-time-location-changed"  => EmailAudience.Speaker,
        // §561 (operator 2026-07-28: "groups photos is not relevant to speakers (wrong placement)
        // - put under organizer/event section") — it is an ORGANIZER/event logistics mail.
        "group-photo-invite"             => EmailAudience.Organizer,

        // --- Sponsors ---------------------------------------------------------------
        "welcome-sponsor"                => EmailAudience.Sponsor,
        "sponsor-leads-digest"           => EmailAudience.Sponsor,
        "app-game-gift-reminder"         => EmailAudience.Sponsor,
        // §1127 — sponsor-facing: it chases the company's own missing webshop links.
        "sponsor-webshop-links-missing"  => EmailAudience.Sponsor,

        // --- Volunteers -------------------------------------------------------------
        "welcome-volunteer"              => EmailAudience.Volunteer,
        "volunteer-help-raised"          => EmailAudience.Volunteer,

        // --- Attendees (including the whole Master Class funnel) ---------------------
        // §252 F5: the funnel moves together on ONE ring by design — never split it.
        "masterclass-selection-invite"   => EmailAudience.Attendee,
        "masterclass-confirmed"          => EmailAudience.Attendee,
        "masterclass-waitlisted"         => EmailAudience.Attendee,
        "masterclass-cancelled"          => EmailAudience.Attendee,
        "masterclass-cancelled-ticket"   => EmailAudience.Attendee,
        "masterclass-reassignment"       => EmailAudience.Attendee,
        "masterclass-offer"              => EmailAudience.Attendee,
        "masterclass-promoted"           => EmailAudience.Attendee,
        "pending-master-class-selection" => EmailAudience.Attendee,
        // §707.11 — split out of task-deadline-reminder; both chase ATTENDEES only.
        "attendee-party-reminder"        => EmailAudience.Attendee,
        "attendee-masterclass-reminder"  => EmailAudience.Attendee,
        // §705.13 — 1-day ticket holders. Retired in code (§299 OPEN-26) but registered so it is
        // visible with its ring instead of invisible.
        "welcome-attendee-1day"          => EmailAudience.Attendee,
        // §705.14 — these reach the class: SPEAKERS of the session AND signed-up attendees
        // (confirmed + waitlisted). Filed under Attendee because that is the larger audience; the
        // per-ROLE rings (§705.3b) are what actually control each side, not this heading.
        "masterclass-question-posted"      => EmailAudience.Attendee,
        "masterclass-instructions-updated" => EmailAudience.Attendee,

        // --- Media / event partners --------------------------------------------------
        "welcome-media"                  => EmailAudience.Media,
        "welcome-eventpartner"           => EmailAudience.EventPartner,

        // --- Organizers (internal ops mail) -----------------------------------------
        "hotel-cutoff-reminder"          => EmailAudience.Organizer,
        // §704.1c — found by the audit: organizer mail that had no Settings row at all.
        "some-speaker-prealert"          => EmailAudience.Organizer,
        "some-published"                 => EmailAudience.Organizer,
        // §707.27 F — internal ops mail to a MAILBOX (ERP, organizer ops inbox, engine alerts).
        // Filed under Organizer because that is who reads them, not who they are about: the travel
        // claim concerns a speaker but no speaker ever receives it.
        "travel-reimbursement-erp"       => EmailAudience.Organizer,
        "feedback-intake"                => EmailAudience.Organizer,
        "engine-alert"                   => EmailAudience.Organizer,
        // §705.14 — the queue COMMIT notification. Reaches VOLUNTEERS on a volunteer commit and
        // ORGANIZERS on an organizer commit; filed under Organizer, with the per-role rings doing the
        // actual controlling.
        "task-allocation-committed"      => EmailAudience.Organizer,
        // §705.15 — the hotel + calendar mails reach whoever is placed in a hotel / activating /
        // signed up for dinner, which is any role. They stay in the cross-role bucket, and their
        // RecipientHint says who actually gets each one.
        "hotel-confirmation-guest" or "hotel-calendar-selfsend"
            or "calendar-dinner" => EmailAudience.Everyone,

        // --- Everyone ----------------------------------------------------------------
        // Cross-role by nature: the generic welcome (a role with no variant), sign-in codes,
        // invitations, broadcasts, task reminders and the activation calendar invite.
        _                                => EmailAudience.Everyone,
    };

    /// <summary>
    /// §705.9 — the roles a mail ACTUALLY REACHES, so the Settings page can offer a ring per role
    /// (§705.3b) for exactly those roles and no others.
    /// </summary>
    /// <remarks>
    /// 🔑 <b>Read from the SENDERS, not guessed</b> — the 2026-07-29 audit. That mattered: the page had
    /// been filing <c>getstarted-digest</c> under Speakers while <c>GetStartedDigestBuilder</c> branches
    /// on Speaker, Sponsor AND Attendee, so one row named one role and mailed three. Operator: *"1 mail
    /// type to a role = 1 ring gate."*
    ///
    /// <para>Default is the single <see cref="AudienceFor"/> role, which is right for the ~28 mails that
    /// genuinely serve one. Only the multi-role mails are listed, so this stays a short, checkable
    /// exception list rather than a duplicate of the whole registry.</para>
    ///
    /// <para>An empty list means "no specific role" — the mail is controlled by its all-roles ring only
    /// (e.g. the ring-EXEMPT mails, which have no ring at all).</para>
    /// </remarks>
    public static IReadOnlyList<ParticipantRole> RecipientRolesFor(string templateKey)
    {
        if (IsRingExempt(templateKey)) return Array.Empty<ParticipantRole>();

        return templateKey.ToLowerInvariant() switch
        {
            // Whoever owns a task — and task seeders cover every role (SponsorTaskDefinitions,
            // SpeakerDeadlineSeeder, AttendeeMasterClassTaskSeeder, and PartyTaskSeeder which alone
            // spans six roles plus attendees).
            "task-deadline-reminder" => AllRoles,

            // OnboardingEmailSets defines 5 persona sets; attendees get none.
            "onboarding-getting-started" or "onboarding-step-reset" => new[]
            {
                ParticipantRole.Organizer, ParticipantRole.Speaker, ParticipantRole.Volunteer,
                ParticipantRole.Media, ParticipantRole.EventPartner, ParticipantRole.Sponsor,
            },

            // GetStartedDigestBuilder branches on exactly these three.
            "getstarted-digest" => new[]
            {
                ParticipantRole.Speaker, ParticipantRole.Sponsor, ParticipantRole.Attendee,
            },

            // The generic welcome is the fallback for roles with no welcome-{role} variant.
            "welcome" => new[] { ParticipantRole.Organizer, ParticipantRole.Attendee },

            // §726 — each speaker-category welcome reaches SPEAKERS only. Declared explicitly so
            // the Settings page files them under Speaker rather than the generic section.
            "welcome-speaker-community" or "welcome-speaker-guest" or "welcome-speaker-sponsor"
                => new[] { ParticipantRole.Speaker },

            // The class: speakers of the session PLUS signed-up attendees (confirmed + waitlisted).
            "masterclass-question-posted" or "masterclass-instructions-updated" => new[]
            {
                ParticipantRole.Speaker, ParticipantRole.Attendee,
            },

            // Volunteers from the volunteer queue, organizers from the organizer queue.
            "task-allocation-committed" => new[]
            {
                ParticipantRole.Volunteer, ParticipantRole.Organizer,
            },

            _ => SingleRoleFor(templateKey),
        };
    }

    private static readonly ParticipantRole[] AllRoles =
    {
        ParticipantRole.Organizer, ParticipantRole.Speaker, ParticipantRole.Volunteer,
        ParticipantRole.Sponsor, ParticipantRole.Attendee, ParticipantRole.Media,
        ParticipantRole.EventPartner,
    };

    /// <summary>
    /// §707.27 B — the audience section a ROLE files under, so a shared mail can be cross-listed under
    /// every role it reaches.
    /// </summary>
    public static EmailAudience AudienceForRole(ParticipantRole role) => role switch
    {
        ParticipantRole.Speaker      => EmailAudience.Speaker,
        ParticipantRole.Sponsor      => EmailAudience.Sponsor,
        ParticipantRole.Volunteer    => EmailAudience.Volunteer,
        ParticipantRole.Attendee     => EmailAudience.Attendee,
        ParticipantRole.Media        => EmailAudience.Media,
        ParticipantRole.EventPartner => EmailAudience.EventPartner,
        ParticipantRole.Organizer    => EmailAudience.Organizer,
        _                            => EmailAudience.Everyone,
    };

    /// <summary>
    /// §707.27 B — every section a mail must be LISTED under: its one filing home
    /// (<see cref="AudienceFor"/>) plus, for a shared mail, each role in
    /// <see cref="RecipientRolesFor"/>.
    /// </summary>
    /// <remarks>
    /// 🔒 <b>The home is always first and always present</b>, even when it is not one of the roles
    /// reached (<c>task-deadline-reminder</c> files under "Any role" and reaches all seven). That is
    /// what keeps the ALL-ROLES ring control on exactly ONE row: the home row carries it, the
    /// cross-listed rows carry only their own role's ring. Two sections offering the same all-roles
    /// control would fight over one value — the trap named in §707.27 B.
    ///
    /// <para>Operator 2026-07-30: *"why does the reminders mails for sponsors not show under
    /// sponsors"* and *"if this is relevant (sent) to the volunteer, then it should be shown on the
    /// volunteer role"*. The §707.18 *"shared · N roles"* badge EXPLAINED the single filing home; it
    /// did not make the mail findable under the role, which is what he asked for.</para>
    /// </remarks>
    public static IReadOnlyList<EmailAudience> ListingAudiencesFor(string templateKey)
    {
        var home = AudienceFor(templateKey);
        var reach = RecipientRolesFor(templateKey);
        if (reach.Count <= 1) return new[] { home };

        var list = new List<EmailAudience> { home };
        foreach (var a in reach.Select(AudienceForRole).OrderBy(a => (int)a))
        {
            if (!list.Contains(a)) list.Add(a);
        }
        return list;
    }

    /// <summary>The one role an audience maps to, or empty when the audience is not role-specific.</summary>
    private static ParticipantRole[] SingleRoleFor(string templateKey) => AudienceFor(templateKey) switch
    {
        EmailAudience.Speaker => new[] { ParticipantRole.Speaker },
        EmailAudience.Sponsor => new[] { ParticipantRole.Sponsor },
        EmailAudience.Volunteer => new[] { ParticipantRole.Volunteer },
        EmailAudience.Attendee => new[] { ParticipantRole.Attendee },
        EmailAudience.Media => new[] { ParticipantRole.Media },
        EmailAudience.EventPartner => new[] { ParticipantRole.EventPartner },
        EmailAudience.Organizer => new[] { ParticipantRole.Organizer },
        // "Any role — audience depends on the send": no fixed role, so the all-roles ring governs.
        _ => Array.Empty<ParticipantRole>(),
    };

    /// <summary>
    /// §515 — every catalog template grouped by audience, in display order. Drives the per-role
    /// sections on the Settings page.
    /// </summary>
    public static IReadOnlyList<IGrouping<EmailAudience, string>> ByAudience() =>
        Map.Keys
            .GroupBy(AudienceFor)
            .OrderBy(g => (int)g.Key)
            .ToList();

    /// <summary>A human label for an audience section heading.</summary>
    public static string AudienceLabel(EmailAudience a) => a switch
    {
        EmailAudience.Speaker      => "Speakers",
        EmailAudience.Sponsor      => "Sponsors",
        EmailAudience.Volunteer    => "Volunteers",
        EmailAudience.Attendee     => "Attendees",
        EmailAudience.Media        => "Media",
        EmailAudience.EventPartner => "Event partners",
        EmailAudience.Organizer    => "Organizers (internal)",
        // §563 — was "Everyone / cross-role". Operator: *"this is very confusing - who is this"*, and
        // he was right: `_ => Everyone` is the DEFAULT for anything unclassified, so the bucket meant
        // "unassigned" while the heading claimed a deliberate audience. The new name states the one
        // thing that is actually true of every mail in it.
        _                          => "Any role — audience depends on the send",
    };

    /// <summary>
    /// §563 — a one-line "who actually gets this", per template.
    /// </summary>
    /// <remarks>
    /// <para>Operator, pointing at the cross-role section heading: *"this is very confusing - who is
    /// this"*. Renaming the bucket (above) fixes the heading; it does not fix the ROWS, which is
    /// where the question is really asked. A row reading <c>task-deadline-reminder · Ring 2</c> tells
    /// him a ring but never a recipient, so the audience has to be inferred from the key.</para>
    ///
    /// <para>This also answers §561's *"i dont understand why one single task becomes something in
    /// ring"* (he was looking at <c>app-game-gift-reminder</c>). The row looked as weighty as a
    /// persona welcome because nothing said it reaches only the handful of sponsors who ran an app
    /// game. The hint restores that proportion in words rather than by hiding the row.</para>
    ///
    /// <para>🔒 <b>Every template must have one</b> — pinned by test, the same mechanical guarantee
    /// `JobCatalogCompletenessTests` gives the Jobs page, so a new template cannot ship as another
    /// unexplained row.</para>
    /// </remarks>
    public static string RecipientHint(string templateKey) => templateKey.ToLowerInvariant() switch
    {
        // --- genuinely cross-role: the audience is chosen at SEND time -------------------
        "pin-signin"        => "Anyone signing in — the code goes to whoever asked for it, in any role.",
        // §705.12 — "invitation" / "broadcast" hints removed with their catalog entries.
        "calendar-invite"   => "The person activating their account, in any role.",
        // §779 — every role with Signal groups in scope: today speakers, volunteers, event partners
        // and media. Sent ONLY when that person presses the button asking for it.
        "signal-join-links" => "Whoever pressed \"send me the Signal links\" — any role that has Signal groups.",
        "welcome"           => "The fallback welcome — used only for a role that has no welcome of its own.",
        // §561: "whoever OWNS the task" — which is why these are not filed under a single role.
        "task-deadline-reminder" => "Whoever owns the task, in any role — sent as its due date approaches.",
        "task-manual-reminder"   => "Whoever owns the task, in any role — sent when an organizer chases it by hand.",

        // --- speakers --------------------------------------------------------------------
        "welcome-speaker"              => "Every speaker, once, when their account is created — used only until their category is set.",
        "welcome-speaker-community"    => "A COMMUNITY speaker, once — the one that congratulates them on being selected.",
        "welcome-speaker-guest"        => "A GUEST speaker (hired on individual terms), once.",
        "welcome-speaker-sponsor"      => "A SPONSOR-brought speaker, once.",
        "speaker-question-digest"      => "A speaker who has unanswered questions on their session.",
        "speaker-graphics-ready"       => "A speaker whose promo graphics have just been released.",
        "some-announcement"            => "The speaker or sponsor a scheduled social-media post is "
                                        + "about — once when it is scheduled, once the day before.",
        "getstarted-digest"            => "Speakers who have not finished Get Started — biweekly until they do.",
        "getstarted-deadline-reminder" => "Speakers with Get Started still open, once, before the deadline.",
        "onboarding-getting-started"   => "A speaker starting onboarding.",
        "onboarding-step-reset"        => "A speaker whose onboarding step an organizer has reopened.",
        "session-evaluation-results"   => "A speaker whose session has been evaluated.",
        // §750 — distinct from the line above: that one CARRIES the results, this one says a PDF
        // report has been published and links to it. Also re-sent when late data supersedes it.
        "session-evaluation-report-ready" =>
            "A speaker whose session evaluation report has just been published, or republished after "
            + "late feedback changed the figures.",
        "travel-reimbursement-paid"    => "A speaker whose travel reimbursement has just been paid.",
        "session-time-location-changed" => "A speaker whose session has been moved to a new room or time.",

        // --- sponsors --------------------------------------------------------------------
        "welcome-sponsor"        => "Every sponsor contact, once, when their account is created.",
        "sponsor-leads-digest"   => "Sponsor contacts, with the leads scanned at their booth.",
        // §561 — the row he singled out. Say how narrow it is.
        "app-game-gift-reminder" => "Only sponsors running an app game who still owe their gift — a handful of companies, not all sponsors.",
        "sponsor-webshop-links-missing" => "Only sponsor companies whose WEBSHOP website or LinkedIn is blank, and only their event-coordinator contacts. Stops by itself once both are filled. X/Twitter is optional and never chased.",

        // --- volunteers ------------------------------------------------------------------
        "welcome-volunteer"    => "Every volunteer, once, when their account is created.",
        "volunteer-help-raised" => "Volunteers, when a help request is raised during the event.",

        // --- attendees -------------------------------------------------------------------
        // §252 F5: the funnel moves together on ONE ring by design — never split it.
        "masterclass-selection-invite"   => "2-day ticket holders — this IS their welcome, inviting them to pick a Master Class.",
        "masterclass-confirmed"          => "An attendee who has been given a Master Class seat.",
        "masterclass-waitlisted"         => "An attendee placed on a Master Class waitlist.",
        "masterclass-cancelled"          => "An attendee whose Master Class SEAT was cancelled.",
        "masterclass-cancelled-ticket"   => "A 2-day holder whose TICKET was cancelled in the webshop.",
        "masterclass-reassignment"       => "An attendee asked to confirm a moved Master Class seat.",
        "masterclass-offer"              => "A waitlisted attendee being offered a seat that freed up.",
        "masterclass-promoted"           => "A waitlisted attendee who has been promoted to a seat.",
        "pending-master-class-selection" => "2-day ticket holders who have not yet selected a Master Class.",
        // §707.11 — say which of the three task-reminder behaviours each row is, since the single
        // old name could not.
        "attendee-party-reminder" =>
            "Attendees with the party question still unanswered — repeats until they answer. Never a speaker or sponsor task.",
        "attendee-masterclass-reminder" =>
            "Attendees whose Master Class task is still open — repeats until it is done.",
        "welcome-attendee-1day"          => "1-day ticket holders. RETIRED IN CODE — nothing sends this today, and re-enabling it needs a deploy, not a switch.",

        // --- media / event partners ------------------------------------------------------
        "welcome-media"        => "Every media contact, once, when their account is created.",
        "welcome-eventpartner" => "Every event-partner contact, once, when their account is created.",

        // --- organizers (internal ops) ---------------------------------------------------
        // §515: audience is the RECIPIENT. These are ABOUT other roles but go to organizers.
        "hotel-cutoff-reminder" => "Organizers only — 3 days before a hotel release deadline. Not sent to speakers.",
        "group-photo-invite"    => "Organizers only — event logistics, not a speaker mail (§561).",
        "some-speaker-prealert" => "One designated organizer — ~5 minutes before a speaker post goes live, to paste in the LinkedIn handle. Never a speaker.",
        "some-published"        => "The SoMe notification list (organizers) — confirms a company-page post went live.",
        // §707.27 F — say the MAILBOX, since none of these reaches a person in a role.
        "travel-reimbursement-erp" => "The ERP mailbox — a speaker's travel-reimbursement claim and receipts, for payment. The speaker never receives it.",
        "feedback-intake"          => "The organizer ops inbox — a bug, feature idea or question raised through AiHelper, with Reply-To set to the person who asked.",
        "engine-alert"             => "The engine alert address — an internal failure notice, tagged [DEV] or [PROD]. Reaches no participant.",
        // §705.14 — say the real audience, including the part that surprises: waitlisted attendees are
        // included, and whoever triggered it is never mailed.
        "masterclass-question-posted" =>
            "The speakers of that Master Class plus everyone signed up to it — confirmed AND waitlisted. Never the person who posted, and never anyone who opted out on that class's page.",
        "masterclass-instructions-updated" =>
            "The speakers of that Master Class plus everyone signed up to it — confirmed AND waitlisted. Never the speaker who made the edit, and never anyone who opted out.",
        "task-allocation-committed" =>
            "The people an organizer just committed tasks to — VOLUNTEERS from the volunteer queue, ORGANIZERS from the organizer queue. One mail listing all of that person's assignments.",
        // §705.15 — say which of the three hotel mails this is, since the old single name did not.
        "hotel-confirmation-guest" =>
            "EVERY guest placed in a hotel, sent when an organizer enters that hotel's confirmation number. Organizer-triggered, so it IS ring-gated — the guest did not ask for it.",
        "hotel-calendar-selfsend" =>
            "One guest, when they press \"Add to calendar\" on their own hotel form. Never a bulk send.",
        "calendar-dinner" =>
            "One person, when they ask for the appreciation-dinner entry from their own form.",

        _ => "Audience depends on how this mail is sent.",
    };
}
