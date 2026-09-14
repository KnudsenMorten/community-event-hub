using System;
using System.Collections.Generic;

namespace CommunityHub.Core.Domain;

/// <summary>
/// §1077 — ONE COMPANY ENTITY for the volume-package benefits (≥10 attendees ⇒ keynote mention,
/// social-media announcement, group photo).
/// </summary>
/// <remarks>
/// <para>🔑 <b>ONE ROW IS ONE ENTITY, and its identity is its E-MAIL DOMAINS.</b> Operator
/// 2026-08-11 asked for two ways to link companies, and both reduce to this single shape:</para>
/// <list type="bullet">
/// <item><b>(a) several company NAMES on one shared domain</b> — ARROW FI / DK / NO all on
/// <c>@arrow.com</c>. The domain already groups them; <see cref="CustomName"/> replaces the three
/// varying names that arrive on the orders.</item>
/// <item><b>(b) several DOMAINS under one mother</b> — <c>globeteam.dk</c> + <c>globeteam.no</c>.
/// Both domains go on one row.</item>
/// </list>
///
/// <para>🔴 <b>Why the domain is the identity and the name is only a label.</b> The company name on an
/// order is FREE TEXT typed by a buyer. §1045 is the standing warning — <i>"2linkIT" vs "2linkIT ApS"
/// vs "2LINKIT"</i> — and matching on it here would decide who appears in a KEYNOTE. Domains are
/// exact, and case (a) above is precisely the case a name-based identity would get wrong.</para>
///
/// <para>🔒 <b>There is no parent/child linking and no "which name wins" rule</b>, because neither is
/// needed once the row can hold several domains. That is a simplification the operator approved, not
/// a reduction of what he asked for.</para>
///
/// <para>⚠️ <b>Qualification is NOT stored here as truth.</b> <see cref="LastQualifiedCount"/> and
/// <see cref="QualifiedNow"/> are the last computed answer, refreshed daily and kept only so a page
/// can render without recomputing. The authority is the computation over live attendee data — see
/// <c>VolumePackageQualificationService</c>.</para>
/// </remarks>
public class VolumePackageCompany
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>
    /// The name shown everywhere — keynote slide, announcement, organizer page. Operator: a
    /// <i>"custom name field"</i> for both linking cases, e.g. "ARROW" for three ARROW companies.
    /// </summary>
    public string CustomName { get; set; } = string.Empty;

    /// <summary>
    /// The identity: one or more e-mail domains, lower-cased, no <c>@</c> (e.g. <c>arrow.com</c>).
    /// Stored newline-separated; use <see cref="DomainList"/>.
    /// </summary>
    public string Domains { get; set; } = string.Empty;

    /// <summary>
    /// §1077 — coupon codes belonging to this entity, as an OVERRIDE for a coupon that has no ERP
    /// mapping. Normally check 2 resolves through <c>CouponInvoicingSetting.ErpCustomerNumber</c>,
    /// which is already curated for invoicing — deriving beats re-typing.
    /// </summary>
    public string CouponCodes { get; set; } = string.Empty;

    /// <summary>
    /// Manually linked attendee e-mails: the freelance consultant who registers with a private
    /// address but belongs to this company. Counted exactly like a domain match.
    /// </summary>
    public string LinkedEmails { get; set; } = string.Empty;

    /// <summary>§1077 — the ERP customer numbers whose coupons belong to this entity.</summary>
    public string ErpCustomerNumbers { get; set; } = string.Empty;

    // ---- benefits the company has approved (step 2 fills these) ----------------------------

    public bool ApprovedKeynoteMention { get; set; }
    public bool ApprovedSocialMediaAnnouncement { get; set; }
    public bool ApprovedGroupPhoto { get; set; }

    /// <summary>Set when the company actively declines — it stops the wizard AND its reminders.</summary>
    public DateTimeOffset? DeclinedAt { get; set; }

    // ---- people --------------------------------------------------------------------------

    /// <summary>
    /// 🔑 The approver IS whoever this address names — the two "flags" the operator asked for are
    /// DERIVED from this and never stored on the Zoho mirror, which the sync rewrites every 10
    /// minutes (his words: <i>"then we dont have to extend schema but separate it"</i>).
    /// </summary>
    public string? ApproverEmail { get; set; }
    public string? ApproverName { get; set; }
    public string? ApproverMobile { get; set; }

    /// <summary>Step 2 — the company's own group-photo coordinator.</summary>
    public string? GroupPhotoContactEmail { get; set; }
    public string? GroupPhotoContactName { get; set; }
    public string? GroupPhotoContactMobile { get; set; }

    // ---- assets (step 2) ------------------------------------------------------------------

    public string? LinkedInUrl { get; set; }
    public string? LogoWebPath { get; set; }
    public string? LogoPrintPath { get; set; }

    // ---- status ---------------------------------------------------------------------------

    /// <summary>The last computed answer. See the class remarks: a cache, not the authority.</summary>
    public bool QualifiedNow { get; set; }

    /// <summary>Distinct attendees counted at the last run.</summary>
    public int LastQualifiedCount { get; set; }

    /// <summary>When it FIRST qualified — kept even if it later drops below the threshold.</summary>
    public DateTimeOffset? FirstQualifiedAt { get; set; }

    public DateTimeOffset? LastCheckedAt { get; set; }

    /// <summary>
    /// 🔴 STICKY APPROVAL (operator agreed). Once an organizer approves the benefits, dropping below
    /// ten sets <see cref="QualifiedNow"/> false and RAISES A FLAG — it never clears this.
    /// ⚠️ Otherwise one cancellation silently removes a company from a keynote slide that is already
    /// being designed.
    /// </summary>
    public DateTimeOffset? BenefitsApprovedAt { get; set; }
    public string? BenefitsApprovedByEmail { get; set; }

    // ---- stage 3: the token that opens the wizard -------------------------------------------

    /// <summary>
    /// §1077 stage 3 — the secret in the URL that opens this company's benefits wizard.
    /// </summary>
    /// <remarks>
    /// <para>🔑 <b>Token, not a login</b> (operator: <i>"same as what you made yesterday so i click
    /// the link and get access"</i>). It also has to work for a person who is <b>not an attendee at
    /// all</b> — a marketing coordinator the company nominates — which no sign-in could serve, and
    /// it avoids opening <c>OneDayAccessGate</c> for two roles (four holes in a deliberate gate).</para>
    ///
    /// <para>🔴 <b>This token WRITES, and the §1040 monitor token did not.</b> That page rested on
    /// four legs — unguessable, scoped, revocable/expiring, and <b>read-only</b>. The fourth is gone
    /// here, so the other three carry more weight and one more is added: <b>every write is confined
    /// to this row</b> — the company's own benefit choices, its own coordinator, its own logo. A
    /// forwarded link can embarrass this company; it can never reach another one's data, and it can
    /// never read a person's e-mail address it did not already know.</para>
    /// </remarks>
    public string? WizardToken { get; set; }

    public DateTimeOffset? WizardTokenIssuedAt { get; set; }

    /// <summary>Set when an organizer withdraws the link; the wizard then answers 404.</summary>
    public DateTimeOffset? WizardTokenRevokedAt { get; set; }

    /// <summary>
    /// When the link stops working by itself. 🔑 The half of the safety that depends on nobody
    /// remembering — see <see cref="AttendeeMonitor.ExpiresAt"/>, and the same date.
    /// </summary>
    public DateTimeOffset? WizardTokenExpiresAt { get; set; }

    /// <summary>When the company finished the wizard (or declined — see <see cref="DeclinedAt"/>).</summary>
    public DateTimeOffset? WizardCompletedAt { get; set; }

    /// <summary>When the invitation carrying the link was last sent to the approver.</summary>
    public DateTimeOffset? WizardInvitedAt { get; set; }

    public DateTimeOffset? WizardLastOpenedAt { get; set; }
    public int WizardOpenCount { get; set; }

    /// <summary>
    /// §1077 stage 4 — when the weekly reminder last went out, and how many have been sent.
    /// </summary>
    /// <remarks>
    /// 🔑 The COUNT is kept as well as the date because they answer different questions. The date
    /// decides whether one is due; the count is what tells an organizer that a company has been
    /// asked five times and it is now a conversation to have by phone, not a sixth e-mail.
    /// </remarks>
    public DateTimeOffset? WizardRemindedAt { get; set; }
    public int WizardReminderCount { get; set; }

    /// <summary>
    /// §1077.9 — when the post-event thank-you (pictures + the LinkedIn tagging ask) was sent.
    /// </summary>
    /// <remarks>
    /// 🔒 It is what stops a second one. A thank-you that arrives twice stops reading as a
    /// thank-you and starts reading as a mailing list.
    /// </remarks>
    public DateTimeOffset? PostEventMailSentAt { get; set; }

    /// <summary>True while the link still opens: issued, not revoked, not past its date.</summary>
    public bool WizardTokenIsActive(DateTimeOffset now) =>
        !string.IsNullOrWhiteSpace(WizardToken)
        && WizardTokenRevokedAt is null
        && !(WizardTokenExpiresAt is { } e && now >= e);

    /// <summary>
    /// §1077 stage 2 — when the "please approve these benefits" mail last went to the organizer
    /// mailbox. 🔒 <b>This stamp IS the dedupe.</b> The daily sweep asks for approval once per
    /// company and then leaves it alone; without a durable mark, a job that runs every day would ask
    /// again every day for a company nobody has got round to approving — the fastest way to teach a
    /// shared inbox to ignore us. An organizer can deliberately re-send from the page, which moves
    /// the stamp forward.
    /// </summary>
    public DateTimeOffset? ApprovalRequestedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? LastUpdatedByEmail { get; set; }

    // ---- list helpers ---------------------------------------------------------------------

    public IReadOnlyList<string> DomainList => Split(Domains);
    public IReadOnlyList<string> CouponCodeList => Split(CouponCodes);
    public IReadOnlyList<string> LinkedEmailList => Split(LinkedEmails);
    public IReadOnlyList<int> ErpCustomerNumberList
    {
        get
        {
            var list = new List<int>();
            foreach (var s in Split(ErpCustomerNumbers))
                if (int.TryParse(s, out var n)) list.Add(n);
            return list;
        }
    }

    /// <summary>
    /// Split a stored multi-value field. ⚠️ Newline AND comma AND semicolon are all accepted, because
    /// an organizer pasting a list will use whichever their source used, and a value silently dropped
    /// here is a company that quietly stops qualifying.
    /// </summary>
    public static IReadOnlyList<string> Split(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? Array.Empty<string>()
            : raw.Split(['\n', '\r', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Normalise a domain for comparison: lower-case, no leading @, no surrounding spaces.</summary>
    public static string NormaliseDomain(string? raw) =>
        (raw ?? string.Empty).Trim().TrimStart('@').ToLowerInvariant();
}

/// <summary>
/// §1077 — one day's answer for one entity. Appended, never overwritten, so "they qualified in
/// December and dropped in January" is answerable.
/// </summary>
public class VolumePackageQualificationSnapshot
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public int VolumePackageCompanyId { get; set; }
    public VolumePackageCompany Company { get; set; } = null!;

    public DateOnly OnDate { get; set; }

    /// <summary>Distinct attendees across all three checks — the union, never a sum.</summary>
    public int AttendeeCount { get; set; }
    public bool Qualified { get; set; }

    /// <summary>Per-check contribution, for the page's "where did this come from" column.</summary>
    public int FromOrders { get; set; }
    public int FromCoupons { get; set; }
    public int FromAttendeeDomains { get; set; }

    public DateTimeOffset ComputedAt { get; set; } = DateTimeOffset.UtcNow;
}
