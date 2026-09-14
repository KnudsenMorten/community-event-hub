using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>
/// Sends the welcome email to a participant (CONTEXT.md - email matrix). The
/// email is role-aware: one template (welcome.html), with a {{roleGuidance}}
/// token whose text depends on the participant's role, so a speaker, a
/// volunteer and a sponsor each get guidance relevant to them.
///
/// Sent on participant creation / import. Routed through SentReminder (via the
/// caller, or directly here) so a re-import does not re-welcome someone.
/// </summary>
public sealed class WelcomeEmailService
{
    private const string TemplateName = "welcome";
    private const string ReminderType = "welcome";

    private readonly CommunityHubDbContext _db;
    private readonly EmailTemplateProvider _templates;
    private readonly IEmailSender _emailSender;
    private readonly TimeProvider _clock;
    private readonly IEmailContextAccessor? _context;
    private readonly CommunityHub.Core.Settings.FeatureGateService? _gate;
    private readonly CommunityHub.Core.Settings.RingResolver? _rings;
    // §234: delivered-vs-dropped seam — the SentReminder "welcome" ledger row is
    // written only when the transport actually delivered, so a ring-dropped welcome
    // (e.g. via a speaker override address) is retried once rings widen. Null
    // (legacy/test wiring) ⇒ old always-record behaviour.
    private readonly IEmailDeliveryOutcome? _outcome;

    public WelcomeEmailService(
        CommunityHubDbContext db,
        EmailTemplateProvider templates,
        IEmailSender emailSender,
        TimeProvider clock,
        IEmailContextAccessor? context = null,
        CommunityHub.Core.Settings.FeatureGateService? gate = null,
        CommunityHub.Core.Settings.RingResolver? rings = null,
        IEmailDeliveryOutcome? outcome = null)
    {
        _db = db;
        _templates = templates;
        _emailSender = emailSender;
        _clock = clock;
        _context = context;
        _gate = gate;
        _rings = rings;
        _outcome = outcome;
    }

    /// <summary>
    /// Send the welcome email to one participant if it has not been sent
    /// before (idempotent via the SentReminder ledger). Returns true if an
    /// email was actually sent. Pass <paramref name="force"/> = true for an
    /// organizer-initiated RESEND: the once-ever idempotency check is bypassed
    /// (the ring gate + redirect/kill-switch still apply), and no duplicate ledger
    /// row is written.
    /// </summary>
    public async Task<bool> SendWelcomeAsync(
        int participantId, CancellationToken ct = default, bool force = false)
    {
        var participant = await _db.Participants
            .Include(p => p.Event)
            .FirstOrDefaultAsync(p => p.Id == participantId, ct);
        if (participant is null || !participant.IsActive)
        {
            return false;
        }

        // Attendees get the per-role variant welcome (welcome-attendee) via the
        // WelcomeWithLoginEmailService provisioning path, NOT this legacy once-ever
        // welcome — so skip them here to avoid a double welcome (operator 2026-06-22).
        if (participant.Role == ParticipantRole.Attendee)
        {
            return false;
        }

        // Route to the speaker's effective address (override ?? Sessionize).
        // Non-speakers / no override resolve to the participant's own address.
        var profile = await _db.SpeakerProfiles
            .Where(sp => sp.ParticipantId == participant.Id)
            .Select(sp => new { sp.ContactEmailOverride, sp.Category })
            .FirstOrDefaultAsync(ct);
        var toEmail = Domain.SpeakerProfile.EffectiveEmailFor(
            participant.Email, profile?.ContactEmailOverride);

        // 🔒 §880.6 — A SPEAKER WITH NO CATEGORY IS NOT WELCOMED. This is the second half of §880
        // and the half that makes it the option he chose.
        //
        // §880 lets a Sessionize speaker arrive ACTIVE, which is what lets them sign in. Active is
        // also what this service and WelcomeReconcileJob use to decide who gets welcomed — and
        // welcome-email is released to Ring 3 in PROD while imported speakers arrive Ring 3, so
        // WITHOUT this gate an unreviewed import would be mailed within ~10 minutes of landing.
        // Offered the choice with those corrected facts he chose "Active, but welcome gated on
        // category": the mail follows the ORGANIZER'S decision, not the import.
        //
        // 🔑 The predicate is deliberately the SAME as the Zoho/Backstage push gate
        // (SpeakerBackstagePushService: `p.Category is null`). One decision point — setting the
        // category — releases both, so an organizer never has to learn two rules.
        //
        // 🔒 UNRECORDED SKIP, like the ring skip below: nothing is written to SentReminders, so the
        // moment a category is set the next WelcomeReconcileJob pass welcomes them. That is what
        // makes this a HOLD rather than a silent loss.
        //
        // ⚠️ Applies to Speakers only (no other role has a category), and it applies to a FORCED
        // resend too: "send this speaker their welcome" before anyone has decided what kind of
        // speaker they are is the exact mail he did not want. Set the category — one click on the
        // pending-speakers page — and it sends.
        //
        // 🔒 A SPEAKER WITH NO PROFILE ROW AT ALL IS NOT HELD, and that is not an oversight — it is
        // what keeps this gate the SAME predicate as the push gate rather than a stricter one.
        // `SpeakerBackstagePushService` iterates SpeakerProfiles, so a profile-less speaker is
        // invisible to it too; and `SpeakerApprovalService.PendingAsync` joins the same table, so
        // such a person would be held by a mail gate while appearing on NO queue that explains why.
        // Every §880 speaker has a profile (the importer writes one in the same pass, before the
        // welcome loop), so the population this gate exists for is fully covered.
        if (participant.Role == ParticipantRole.Speaker && profile is not null && profile.Category is null)
        {
            return false;
        }

        // Idempotency: one welcome per participant, ever.
        //
        // §326bf: this used to AND on `RecipientEmail == participant.Email` as well as the
        // occasion key. `welcome:{participantId}` is ALREADY unique per person, so the
        // address added nothing to the identity — but it could take the match AWAY:
        // correcting a typo'd address, a re-import that changes the case, or a Sessionize
        // update made the stored ledger row unmatchable, and the 10-minute
        // WelcomeReconcileJob then welcomed that person all over again. The occasion key IS
        // the identity; the address is merely where the mail happened to go that day.
        var occasionKey = $"welcome:{participant.Id}";
        var already = await _db.SentReminders.AnyAsync(
            s => s.EventId == participant.EventId
                 && s.ReminderType == ReminderType
                 && s.OccasionKey == occasionKey,
            ct);
        if (already && !force)
        {
            return false;
        }

        // 🔒 §724 — THE FEATURE RING NO LONGER GATES THIS SEND. Operator 2026-07-31:
        // *"but we killed the welcome-email gate !!!"* … *"i consider that as an old feature gate
        // that we should have removed, as we now control all on the actual email templates"*.
        //
        // He is right, and this was a MISS in §707.6. That change deleted the feature-ring clamp
        // from the transport (BrevoEmailSender: "the transport no longer asks a FEATURE for a ring
        // under any circumstance") — but this second, earlier enforcement point survived, so the
        // welcome alone still resolved its audience as MIN(feature ring, mail ring).
        //
        // §721 is what it cost: `welcome-speaker` and `welcome-sponsor` were set to Ring 3 and the
        // page said Ring 3, while `welcome-email` sat at Ring 2 — so every Ring-3 speaker and
        // sponsor was skipped HERE, silently, before their mail's own ring was ever read. 13
        // speakers and 19 sponsors had a task reminder chasing them without ever having been
        // welcomed.
        //
        // 🔑 ENABLED is still honoured — only the RING is gone. That is exactly what §707.6 did:
        // "The feature keeps its ON/OFF — that still stops whole jobs — it simply no longer
        // contributes a RING." So switching welcome-email OFF still stops every welcome; the
        // AUDIENCE is now the mail's own (mail × role) ring alone, decided in BrevoEmailSender.
        //
        // The skip stays UNRECORDED either way, which is the property §326 wanted: a recipient who
        // is dropped is not written to SentReminders, so widening the mail's ring re-sends to them
        // on the next pass instead of marking them welcomed-but-never-mailed.
        if (_gate is not null
            && !await _gate.IsFeatureEnabledAsync("welcome-email", participant.EventId, ct))
        {
            return false;
        }

        var firstName = string.IsNullOrWhiteSpace(participant.FullName)
            ? "there"
            : participant.FullName.Split(' ')[0];

        // §169: the welcome CTA becomes the recipient's personal auto-login link.
        var tokens = _templates.NewTokenSet(participant.Id);
        tokens["firstName"] = firstName;
        tokens["communityName"] = participant.Event.CommunityName;
        tokens["eventDisplayName"] = participant.Event.DisplayName;
        tokens["eventCode"] = participant.Event.Code;
        tokens["roleName"] = FriendlyRoleName(participant.Role);
        tokens["roleGuidance"] = RoleGuidance(participant.Role);
        tokens["roleLine"] = WelcomeWithLoginEmailService.RoleLine(participant.Role);
        // Sponsor variant: dynamic "you are receiving this in your role as …" line.
        tokens["sponsorRole"] = CommunityHub.Core.Email.WelcomeVariants.SponsorRoleLabel(
            participant.IsEventCoordinator, participant.IsSigner, participant.IsBoothMember);

        // Render the per-role VARIANT (welcome-sponsor / welcome-speaker / …) when
        // one exists for the role, so the polished role-specific copy is what goes
        // out; fall back to the generic welcome for roles without a variant.
        //
        // §726 — for a SPEAKER the key is further split by SpeakerCategory, so each category
        // carries its own ring. The category lives on SpeakerProfile, not on Participant.
        //
        // §880.6 — read from the profile already loaded above rather than querying a second time.
        // For a Speaker it is now guaranteed non-null: the category gate returned earlier otherwise.
        var speakerCategory = participant.Role == ParticipantRole.Speaker ? profile?.Category : null;

        // 🔑 TWO keys, deliberately. `mailKey` is the mail's IDENTITY — it decides the ring and the
        // Settings row. `fileKey` is the body to render: the three category variants have no file
        // of their own, so one body serves all three and cannot drift (§660/§719).
        var mailKey =
            CommunityHub.Core.Email.WelcomeVariants.TemplateKeyFor(participant.Role, speakerCategory)
            ?? TemplateName;
        var fileKey = CommunityHub.Core.Email.WelcomeVariants.TemplateFileKeyFor(mailKey);

        // §726 — the ONLY part of the speaker welcome that differs by category. Named …Html so the
        // renderer inserts it verbatim (it carries <strong>) instead of encoding the markup.
        tokens["speakerIntroHtml"] = CommunityHub.Core.Email.WelcomeVariants.SpeakerIntroHtml(
            speakerCategory, participant.Event.DisplayName);

        var rendered = _templates.Render(fileKey, tokens);
        // Ring-governed by the welcome-email feature (operator 2026-06-22).
        // §516: Welcome:true adds the Email:WelcomeMaxReleaseRing cap (default Ring1) BENEATH the
        // feature ring, so widening the Settings picker alone cannot release a persona welcome.
        // TemplateName carries the per-role variant so the transport can see WHICH welcome this is.
        using (_context?.Set(new EmailContext(
            ReminderType, participant.EventId, participant.Id, participant.FullName,
            // §726 — the MAIL key, not the file key: the transport picks the ring from
            // TemplateName, which is exactly what lets one body carry three identities.
            TemplateName: mailKey,
            FeatureKey: "welcome-email", Welcome: true)))
        {
            await _emailSender.SendAsync(
                toEmail, rendered.Subject, rendered.HtmlBody, ct);
        }

        // §234: a gated (ring-dropped / kill-switched) send is NOT a welcome — do
        // not write the once-ever ledger row, so a later reconcile re-sends
        // automatically once rings widen. (Catches what the desired-state pre-gate
        // above cannot: e.g. a speaker override address dropped at the transport.)
        if (_outcome is not null && !_outcome.LastSendDelivered)
        {
            return false;
        }

        // 🔴 §1222 — STAMP THE WELCOME, like WelcomeWithLoginEmailService does. This path only wrote
        // the ledger row, and the Get Started digest anchors on WelcomeWithLoginSentAt (§738: never
        // welcomed ⇒ never chased). Every sponsor/speaker welcomed by the reconcile jobs was
        // therefore never chased — operator 2026-09-14: "sponsors are reporting that they dont get
        // any weekly reminders when get started is not completed".
        // 🔒 Only when empty: a forced RESEND must not push an existing cadence anchor forward.
        participant.WelcomeWithLoginSentAt ??= _clock.GetUtcNow();

        // Record it (first time) so a re-import does not re-send. A forced resend
        // re-sends an already-welcomed person without adding a duplicate ledger row.
        if (!already)
        {
            _db.SentReminders.Add(new SentReminder
            {
                EventId = participant.EventId,
                RecipientEmail = participant.Email,
                ReminderType = ReminderType,
                OccasionKey = occasionKey,
                SentAt = _clock.GetUtcNow(),
            });
        }
        // §1222 — saved on a forced resend too, which can now carry a stamp the first send never set.
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private static string FriendlyRoleName(ParticipantRole role) => role switch
    {
        ParticipantRole.Organizer => "organizer",
        ParticipantRole.Speaker => "speaker",
        ParticipantRole.Volunteer => "volunteer",
        ParticipantRole.Sponsor => "sponsor contact",
        ParticipantRole.Attendee => "attendee",
        _ => "participant",
    };

    /// <summary>Role-specific guidance paragraph for the welcome email.</summary>
    private static string RoleGuidance(ParticipantRole role) => role switch
    {
        ParticipantRole.Organizer =>
            "You have full access: participant management, sponsor orders, and "
            + "attendee reconciliation.",
        ParticipantRole.Speaker =>
            "Start with the \"Get Started\" flow in the hub — it walks you "
            + "through everything you need to set up. Afterwards you can change "
            + "any of your preferences from the hub. You can access the hub using https://hub.expertslive.dk - save the link to your favourites.",
        ParticipantRole.Volunteer =>
            "Start with the \"Get Started\" flow in the hub — it walks you "
            + "through everything you need to set up. Afterwards you can change "
            + "any of your preferences from the hub. You can access the hub using https://hub.expertslive.dk - save the link to your favourites.",
        ParticipantRole.Sponsor =>
            "Your sponsor onboarding tasks and deadlines are in the hub. New "
            + "tasks appear as your order is processed.",
        ParticipantRole.Attendee =>
            "You can check your Master Class booking status in the hub.",
        _ => "Sign in to the hub to see what is relevant to you.",
    };
}
