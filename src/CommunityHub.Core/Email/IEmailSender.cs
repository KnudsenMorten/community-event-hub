namespace CommunityHub.Core.Email;

/// <summary>
/// Sends transactional email. One seam used by both the PIN login (Stage 3)
/// and the reminder jobs (Stage 6+). The implementation is Brevo SMTP
/// (CONTEXT.md section 11) - host smtp-relay.brevo.com:587, STARTTLS,
/// credentials from Key Vault.
/// </summary>
public interface IEmailSender
{
    /// <summary>
    /// Send a single HTML email. Throws on hard failure so the caller (e.g.
    /// the PIN flow) can surface a retry; the reminder job catches and logs.
    /// </summary>
    Task SendAsync(
        string toEmail,
        string subject,
        string htmlBody,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Send a single HTML email with optional CC recipients (e.g. a participant's
    /// secondary email). Each CC is subject to the same ring gate + redirect as the
    /// primary recipient. The 4-arg <see cref="SendAsync(string,string,string,CancellationToken)"/>
    /// overload is just this with no CC.
    /// </summary>
    Task SendAsync(
        string toEmail,
        string subject,
        string htmlBody,
        IReadOnlyCollection<string>? cc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Send a single HTML email with an optional <b>Reply-To</b> address (name +
    /// email) so that an organizer hitting "Reply" replies to <paramref name="replyTo"/>
    /// (e.g. the person who actually asked) rather than the configured From mailbox.
    /// Used by the AiHelper intake (REQUIREMENTS §137), where the To is the dev /
    /// organizer mailbox but the conversation should continue with the asker.
    ///
    /// Default interface implementation no-ops the reply-to (it just delegates to the
    /// 3-arg overload) so existing senders and test doubles need no change; the
    /// production <see cref="BrevoEmailSender"/> overrides it to set
    /// <c>MailMessage.ReplyToList</c>. The same ring gate + redirect + kill-switch
    /// as the other overloads applies.
    /// </summary>
    Task SendAsync(
        string toEmail,
        string subject,
        string htmlBody,
        EmailReplyTo? replyTo,
        CancellationToken cancellationToken = default)
        => SendAsync(toEmail, subject, htmlBody, cancellationToken);

    /// <summary>
    /// Send an email carrying BOTH an HTML body and a plain-text alternative
    /// (a <c>multipart/alternative</c>): clients that prefer plain text (or
    /// strip HTML) render <paramref name="textBody"/>, the rest render
    /// <paramref name="htmlBody"/>. Used by the welcome-with-login email, whose
    /// requirement is HTML + plain-text. The same ring gate + redirect as
    /// <see cref="SendAsync(string,string,string,CancellationToken)"/> applies.
    /// </summary>
    Task SendAsync(
        string toEmail,
        string subject,
        string htmlBody,
        string textBody,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Send an HTML email with one inline iCalendar (.ics) attachment.
    /// Used for RSVP confirmations that add the event to the participant's calendar.
    /// </summary>
    Task SendWithIcsAsync(
        string toEmail,
        string subject,
        string htmlBody,
        string icsContent,
        string icsFileName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Send an HTML email with one or more arbitrary file attachments
    /// (REQUIREMENTS §48 — travel-reimbursement receipts/invoice to the ERP inbox).
    /// Each attachment is added with its own content type; the same
    /// ring gate + redirect + kill-switch as the other overloads applies,
    /// so attaching files never bypasses gating — set an <c>EmailContext</c> with
    /// <c>RingExempt = true</c> when the recipient is a non-participant (e.g. the ERP
    /// mailbox) that must not be ring-dropped.
    /// </summary>
    Task SendWithAttachmentsAsync(
        string toEmail,
        string subject,
        string htmlBody,
        IReadOnlyCollection<EmailAttachment> attachments,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One in-memory file attachment for <see cref="IEmailSender.SendWithAttachmentsAsync"/>.
/// </summary>
public sealed record EmailAttachment(
    string FileName,
    byte[] Content,
    string ContentType);

/// <summary>
/// An optional Reply-To address for a send (<see cref="IEmailSender.SendAsync(string,string,string,EmailReplyTo?,CancellationToken)"/>).
/// <paramref name="Name"/> is the optional display name; <paramref name="Email"/> is required.
/// </summary>
public sealed record EmailReplyTo(string Email, string? Name = null);

/// <summary>
/// Brevo SMTP settings, bound from configuration. The username and key come
/// from Key Vault (secret names brevo-smtp-username / brevo-smtp-key); the
/// rest are non-secret and may sit in appsettings.
/// </summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>
    /// GLOBAL OUTBOUND-EMAIL KILL SWITCH (REQUIREMENTS §23). When true, NO mail
    /// leaves the hub by ANY path — web, jobs, digests — regardless of rings or
    /// the redirect. The send is short-circuited before SMTP and logged
    /// as a kill-switch drop. This is the process-wide hard stop set in config
    /// (app setting <c>Email__KillSwitch</c>); the per-edition organizer switch
    /// (the <c>outbound-email</c> feature gate) sits on top for edition-scoped
    /// control. Default false (mail flows, subject to the ring gate).
    /// </summary>
    public bool KillSwitch { get; set; }

    public string SmtpHost { get; set; } = "smtp-relay.brevo.com";
    public int SmtpPort { get; set; } = 587;

    /// <summary>Brevo-issued SMTP username (NOT the account login email).</summary>
    public string SmtpUsername { get; set; } = string.Empty;

    /// <summary>Brevo SMTP key (the SMTP password).</summary>
    public string SmtpKey { get; set; } = string.Empty;

    public string FromAddress { get; set; } = "info@expertslive.dk";
    public string FromDisplayName { get; set; } = "Experts Live Denmark";

    /// <summary>
    /// §707.27 C1 — the edition's ORGANIZER INBOX: where an internal ops alert lands when no more
    /// specific recipient is configured (today, the SoMe speaker pre-alert).
    /// </summary>
    /// <remarks>
    /// 🔒 <b>Defaulted IN CODE, not via an app setting</b>, for the §462 reason: app settings swap
    /// with the deployment slot, so an ops alert whose only recipient lived in a slot setting could
    /// lose its address on a swap and fail silently. Config may still override it per edition.
    ///
    /// <para>A SHARED inbox, deliberately — the operator's instruction named the address, not a
    /// person (*"some-speaker-prealert MUST GO TO info@expertslive.dk"*). One designated organizer
    /// on holiday is how a 5-minute ops window gets missed.</para>
    /// </remarks>
    public string OrganizerInbox { get; set; } = "info@expertslive.dk";

    /// <summary>
    /// TEST MODE redirect. When non-empty, every outbound mail's To: is replaced
    /// with this address regardless of the original recipient; the original is
    /// preserved in the subject as "[TEST -> original@example.com]". Set ONLY
    /// in dev (via app setting Email__RedirectAllTo). Leave empty in prod.
    /// </summary>
    public string RedirectAllTo { get; set; } = string.Empty;

    /// <summary>
    /// RING CEILING (REQUIREMENTS §23) — caps the outbound-email audience at a ring,
    /// regardless of a feature's released ring. Primary use is the DEV env: set
    /// <c>Email__MaxReleaseRing=Ring1</c> so DEV onboarding / reminders / schedules
    /// reach only ring0/ring1 participants and NEVER ring2+ (real sponsors, speakers,
    /// volunteers) even if a feature is released to ring2/ring3. The effective email
    /// release ring becomes <c>min(featureReleasedRing, MaxReleaseRing)</c> (lower =
    /// more restrictive). Leave EMPTY in PROD so the feature's released ring rules.
    /// Accepts a <see cref="Settings.Ring"/> name ("Ring0".."Ring3"/"Broad") or its
    /// number ("0".."3"); unparseable / empty = no ceiling.
    /// </summary>
    public string MaxReleaseRing { get; set; } = string.Empty;

    /// <summary>
    /// §217 — ATTENDEE-WELCOME-SPECIFIC release ceiling (Risk-3 / F3). The attendee
    /// welcome emails (the 1-day <c>AttendeeOneDayWelcomeEmailService</c> + the 2-day
    /// attendee provisioning welcome) are sent to up to ~1500 inboxes, so they carry
    /// their OWN release cap ON TOP of the general <see cref="MaxReleaseRing"/> /
    /// feature gate: an attendee welcome is only delivered when the recipient's ring is
    /// at/below this ring; above it the send is SUPPRESSED (logged, not sent). The cap
    /// only ever TIGHTENS the effective release ring (it is min'd with the general gate),
    /// never loosens it, and applies ONLY to sends tagged
    /// <see cref="EmailContext.AttendeeWelcome"/> — every other email (speaker/sponsor
    /// welcomes, reminders, ops mail) is unaffected.
    ///
    /// <para>Default <b>Ring1</b> (test/internal only) so a deploy NEVER blasts the full
    /// ~1500 attendee list: the operator deliberately steps it Ring1 → Ring2 → Broad to
    /// release the welcome in phases. Accepts a <see cref="Settings.Ring"/> name
    /// ("Ring0".."Ring2"/"Broad") or its number ("0".."3"). FAIL-SAFE: an empty /
    /// unparseable value is treated as Ring1 (never wider), so misconfiguration can only
    /// ever be MORE restrictive, not less. (Attendee PARTICIPANTS stay at their normal
    /// Broad ring for sign-in — this cap governs only the welcome EMAIL release.)</para>
    /// </summary>
    public string AttendeeWelcomeMaxReleaseRing { get; set; } = "Ring1";

    /// <summary>
    /// §516 — the release cap for WELCOME emails of every persona (speaker, sponsor, volunteer,
    /// media, event partner), applied to any send tagged <see cref="EmailContext.Welcome"/> ON TOP
    /// of the general gate and the welcome-email feature ring.
    ///
    /// <para><b>Why this exists.</b> §217 gave the ATTENDEE welcome its own cap; every other
    /// persona had only the <c>welcome-email</c> feature ring — an ordinary picker on the Settings
    /// page — holding it back. One mis-click there widened the speaker/sponsor welcome with nothing
    /// underneath. The operator asked for this cap explicitly before going live with SPEAKERS:
    /// "you should set the ring cap to ring 1 as a start for welcome mails".</para>
    ///
    /// <para>Default <b>Ring1</b> (test/internal only), and deliberately defaulted IN CODE rather
    /// than relying on an app setting: app settings swap with the deployment slot (§462), so a cap
    /// configured on only one slot silently disappears on a swap. Accepts a
    /// <see cref="Settings.Ring"/> name ("Ring0".."Ring2"/"Broad") or its number ("0".."3").
    /// FAIL-SAFE: an empty / unparseable value is treated as Ring1 (never wider). Only ever
    /// TIGHTENS — it is min'd with the general gate and can never loosen it.</para>
    /// </summary>
    public string WelcomeMaxReleaseRing { get; set; } = "Ring1";

    /// <summary>
    /// §219 (Risk-4) — PACING delay, in milliseconds, inserted BETWEEN consecutive
    /// sends in a BULK loop (the reminder engine batch + the attendee welcome
    /// provisioning loops) so a ~1500-recipient run does not trip Brevo's per-second
    /// rate limit. Applied ONLY in bulk loops via <see cref="IBulkSendPacer"/>; a single
    /// interactive send (PIN sign-in, one re-send) never paces, so it stays fast.
    /// Default 150 ms (≈ a handful of mails/second, comfortably under Brevo's cap).
    /// Set to 0 to disable pacing. Tunable via config without a code change.
    /// </summary>
    public int BulkSendDelayMs { get; set; } = 150;

    /// <summary>
    /// §326av — HARD CEILING on how many e-mails one edition may send per rolling hour.
    /// The 2026-07-25 audit found the codebase had NO recipient cap of any kind: every blast
    /// was bounded only by the ring gate, and one un-confirmed "promote the Email group to
    /// Broad" click in Settings removes that bound everywhere at once. With ~1500 attendees
    /// that is an unrecallable mistake waiting to happen.
    ///
    /// <para>Counted from <c>EmailLog</c> — durable (survives a restart) and shared by web
    /// AND jobs, so it is a real ceiling rather than a per-process guess. Over the ceiling a
    /// send is DECLINED, which for the reminder engine means DEFERRED, not lost: the engine
    /// deliberately does not stamp the sent-ledger for a declined delivery, so the next run
    /// retries. An operator broadcast above the ceiling IS cut short — that is the intent,
    /// and it is logged as an error.</para>
    ///
    /// <para><see cref="EmailContext.RingExempt"/> sends (PIN sign-in, engine alerts,
    /// operator test sends) BYPASS the ceiling: they are user-initiated one-offs that can
    /// never be a blast, and nobody should be locked out of the hub because a welcome run is
    /// in progress. 0 disables the ceiling.</para>
    ///
    /// <para><b>Operator decision 2026-07-27 — DISABLED (600 → 0).</b> Verbatim: <i>"there can not
    /// be any email limit (remove)"</i>. The ceiling was sized for a much smaller audience and had
    /// become wrong at this one's: it is GLOBAL PER EDITION, so a single welcome wave or broadcast
    /// to 1,500–2,000 people stopped at 600 and the remaining ~1,400 got nothing that hour. A limit
    /// that silently cuts the event's own announcements in half is not a safety net.</para>
    ///
    /// <para><b>What is given up:</b> the in-app backstop against a runaway or mis-triggered mass
    /// send — nothing now caps total outbound volume. What REMAINS if a blast ever starts:
    /// <c>Email:KillSwitch</c> (stops everything), the ring gate (who is eligible at all), the §219
    /// pacer holding sends ~150 ms apart, and Brevo's own account limits.</para>
    /// </summary>
    public int MaxSendsPerHour { get; set; } = 0;

    /// <summary>
    /// §219 (Risk-4) — total SMTP send attempts per message at the transport
    /// (<see cref="BrevoEmailSender"/>): the first try plus retries on TRANSIENT
    /// failures (Brevo throttling / rate-limit 4xx, 5xx, socket/IO/timeout) with a
    /// backoff between attempts. A permanent rejection (bad address) is NOT retried.
    /// Default 3 (initial + 2 retries). Minimum 1 (no retry). Tunable via config.
    /// </summary>
    public int SendMaxAttempts { get; set; } = 3;

    /// <summary>
    /// §219 (Risk-4) — base backoff in milliseconds between SMTP retry attempts; the
    /// delay grows per attempt (attempt × base ⇒ 1×, 2×, …) so a throttled message is
    /// re-spaced rather than hammered. Default 1000 ms (1s, 2s — matching the prior
    /// hard-coded behaviour). Tunable via config.
    /// </summary>
    public int SendRetryBaseDelayMs { get; set; } = 1000;

    /// <summary>
    /// PROD OPERATOR BCC. When non-empty, this address is added as a BCC on every
    /// mail that ACTUALLY SENDS — i.e. after the ring gate AND the
    /// redirect/kill-switch decision have all passed for the primary
    /// recipient. It is NOT added to mail that is ring-dropped,
    /// redirected away, or kill-switched, so the bcc faithfully reflects what truly
    /// went out (a silent archive of real outbound). The bcc itself is not subject
    /// to ring filtering — it is the operator (organizer, ring0)
    /// and must always receive a copy of any sent mail — but the rules
    /// for the PRIMARY recipient are never bypassed. Set in PROD via app setting
    /// <c>Email__BccAllTo</c> (e.g. the operator's mailbox); leave empty in DEV /
    /// tests (default) so behaviour is unchanged there.
    /// </summary>
    public string BccAllTo { get; set; } = string.Empty;

    /// <summary>
    /// The active edition's short CODE, appended exactly ONCE as a POSTFIX to the END
    /// of EVERY outbound subject at the single send chokepoint (REQUIREMENTS §180):
    /// e.g. "Your session evaluation is ready" → "Your session evaluation is ready
    /// [ELDK27]". Normalisation strips any existing "[CODE]" token first (whether the
    /// caller wrote it as a prefix or inline) so it is never doubled, preserves a
    /// leading "[EXT]" the mail gateway may prepend, and is idempotent. Applying it at
    /// the transport (<see cref="BrevoEmailSender"/>) means INLINE-built emails get the
    /// postfix too, not just template-rendered subjects (the §103 renderer). Default
    /// "ELDK27"; set per edition via app setting <c>Email__EventCode</c>.
    /// </summary>
    public string EventCode { get; set; } = "ELDK27";
}
