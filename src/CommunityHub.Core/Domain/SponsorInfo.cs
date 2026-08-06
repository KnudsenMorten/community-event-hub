using CommunityHub.Core.Integrations;
using CommunityHub.Core.Settings;

namespace CommunityHub.Core.Domain;

/// <summary>
/// The sponsorship PACKAGE a company holds, ordered cheapest → richest.
/// Distinct from <see cref="Integrations.BoothTier"/> (the physical booth-wall
/// spec): the package is the commercial level that decides whether the company
/// gets a booth at all. Silver is digital-only (no booth); Gold and above are
/// exhibitor packages that include a booth.
/// </summary>
public enum SponsorPackage
{
    /// <summary>Digital / no booth.</summary>
    Silver = 0,

    /// <summary>Booth / exhibitor.</summary>
    Gold = 1,

    /// <summary>Booth / exhibitor.</summary>
    Diamond = 2,

    /// <summary>Booth / exhibitor.</summary>
    Platinum = 3,
}

/// <summary>
/// A sponsor company's LIFECYCLE status (REQUIREMENTS §253, G8b). Before this the
/// sponsor lifecycle had no exit path at all — a company that withdrew kept its
/// public logo, group party HeadCount and contact logins forever. Withdrawn
/// companies are excluded from the public sponsors page + organizer sponsor
/// counts, and the withdrawal action cascades a deactivation over every contact
/// (via <see cref="Organizer.ParticipantDeactivationService"/>). Zoho/ERP records
/// are never touched (§56 — no deletes).
/// </summary>
public enum SponsorStatus
{
    Active = 0,
    Withdrawn = 1,
}

/// <summary>
/// One sponsor company's self-service info: logos + descriptive text.
/// Scoped to (EventId, SponsorCompanyId) so all contacts of a company edit
/// the same row -- first one to save sets values; subsequent contacts edit
/// the same row. Replaces the email-based "send us your description" flow:
/// the data lives here, the hub auto-marks the matching ParticipantTask
/// rows Done when this is saved.
/// </summary>
public class SponsorInfo
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>WooCommerce / Company Manager company id.</summary>
    public string SponsorCompanyId { get; set; } = string.Empty;

    /// <summary>
    /// §593 — the company's DISPLAY NAME, synced from Company Manager's <b>Public Company Name</b>
    /// (falling back to Legal Company Name, then the webshop billing company). This is the single
    /// per-company home for the name in CEH.
    /// </summary>
    /// <remarks>
    /// 🔒 WHY THIS COLUMN EXISTS. Operator 2026-07-28: *"you need to use a field inside CM which is
    /// this field and it must be carried over to CEH as the company name"* — after
    /// `/Organizer/Participants` rendered **"(name not synced — CM id 30)"** for a company whose CM
    /// record clearly reads "System Center Dudes".
    ///
    /// <para>The name WAS being resolved correctly during the order pull — and then thrown away.
    /// Its only persistent home was <c>SponsorUploadLocation.CompanyName</c>, which is written
    /// INSIDE the SharePoint folder-provisioning loop. If SharePoint did not run (unconfigured, a
    /// throw, or no upload-folder task definitions), no row was written and the resolved name was
    /// discarded — while the order still created its tasks, so nothing looked broken. A company's
    /// identity was, in effect, a by-product of folder provisioning.</para>
    ///
    /// <para>Persisted here at the MOMENT OF RESOLUTION instead, so it cannot depend on an
    /// unrelated subsystem. The SharePoint folder name is derived from the SAME resolved value, so
    /// the folder and the display name can never disagree (his instruction: *"that is also the name
    /// that the sharepoint integration must create"*).</para>
    ///
    /// <para>⚠️ §443 still holds: this is SYNCED. Never fetch a company name from Company Manager
    /// while rendering a page — that pattern made five organizer pages take 6–8 s warm.</para>
    /// </remarks>
    public string? CompanyName { get; set; }

    /// <summary>
    /// Zoho Backstage SPONSOR id for this company (every paying company is a Zoho
    /// sponsor). Persisted so the Zoho sync targets this company by id instead of
    /// matching on company name (which can change). Null until mapped. See the
    /// one-time ID-fetch that matches by company name across CEH / Zoho / webshop.
    /// </summary>
    public string? ZohoSponsorId { get; set; }

    /// <summary>
    /// Zoho Backstage EXHIBITOR id for this company — present only when the company
    /// bought booth products (so it appears as an exhibitor as well as a sponsor).
    /// Null for sponsor-only companies. A company can therefore carry TWO Zoho ids.
    /// </summary>
    public string? ZohoExhibitorId { get; set; }

    /// <summary>
    /// The sponsorship tier this company holds (the booth tier — Gold / Diamond /
    /// Platinum / Feature, or <see cref="BoothTier.None"/> when unknown). Drives the
    /// grouping on the PUBLIC sponsors page (<c>/Sponsors</c>), where sponsors are
    /// listed by tier. Sponsors are public, so there is no publish gate — a company
    /// is shown once it has a tier (or as an "other supporters" group when
    /// <see cref="BoothTier.None"/>). Set from the product classification when a
    /// sponsor's booth order is processed; an organizer may correct it.
    /// </summary>
    public BoothTier Tier { get; set; } = BoothTier.None;

    /// <summary>
    /// The physical booth slot label (e.g. "E-26"), parsed from the booth product name
    /// during the order pull. Sent to Zoho as <c>booth_label</c> on exhibitor create so
    /// the booth is actually assigned (else Zoho shows "No booth selected"). Null when the
    /// order carries no booth slot. REQUIREMENTS §41a.
    /// </summary>
    public string? BoothLabel { get; set; }

    /// <summary>
    /// The commercial sponsorship package this company bought
    /// (Silver/Gold/Diamond/Platinum). Defaults to
    /// <see cref="SponsorPackage.Silver"/> (digital, no booth). Set from the
    /// purchased product name at sync time (see
    /// <see cref="Integrations.SponsorPackageMapper"/>); an organizer may correct
    /// it. Drives <see cref="HasBooth"/> and the sponsor-hat order entitlements.
    /// </summary>
    public SponsorPackage SponsorPackage { get; set; } = SponsorPackage.Silver;

    /// <summary>
    /// True when this company's package includes a booth/exhibitor presence
    /// (Gold and above). Silver is digital-only. Computed from
    /// <see cref="SponsorPackage"/>; not persisted.
    /// </summary>
    public bool HasBooth => SponsorPackage >= SponsorPackage.Gold;

    /// <summary>
    /// Optional public website URL shown as the sponsor's link on the public
    /// sponsors page. Hub-collected (a sponsor contact / organizer can set it);
    /// null/blank renders no link. Format is validated in the editing UI; the
    /// public page only ever renders an absolute http(s) URL.
    /// </summary>
    public string? WebsiteUrl { get; set; }

    /// <summary>
    /// Company LinkedIn page URL (full https URL, e.g.
    /// https://www.linkedin.com/company/2linkit). Hub-collected on the Company
    /// Details page; synced to Zoho Backstage exhibitor <c>company_social_pages</c>.
    /// </summary>
    public string? LinkedInUrl { get; set; }

    /// <summary>
    /// §824.15 — the sponsor's NUMERIC LinkedIn organization id, e.g. <c>98360537</c>. This is what a
    /// real company mention needs (<c>urn:li:organization:{id}</c>); a profile URL is not enough.
    /// </summary>
    /// <remarks>
    /// <para>🔴 <b>Held here because LinkedIn will not tell us.</b> Measured 2026-08-04 with a live org
    /// token (§824.14c): <c>organizations?q=vanityName</c> returns <c>403 ACCESS_DENIED</c>, and so
    /// does fetching the organization we administer BY ID. So the id cannot be derived from
    /// <see cref="LinkedInUrl"/> through the API with the app's current product access.</para>
    ///
    /// <para>Nor can it be parsed out of the URL in practice: all 14 sponsor URLs on record are vanity
    /// slugs (<c>/company/glueckkanja</c>), not numeric (§824.12a). <c>LinkedInUrlParser</c> still
    /// reads an id when one IS present, because a URL copied from a company's admin view carries the
    /// number — that path just cannot be relied on.</para>
    ///
    /// <para>🔒 <b>Null is a first-class state, not a gap to paper over.</b> A post for a sponsor with
    /// no id mentions them as PLAIN TEXT — exactly how the ELDK26 posts read — rather than emitting a
    /// broken mention or refusing to publish. Tagging is an enhancement on top of the announcement,
    /// never a precondition for it.</para>
    /// </remarks>
    public string? LinkedInOrganizationId { get; set; }

    /// <summary>
    /// Company Twitter/X page URL (full https URL). Hub-collected on the Company
    /// Details page; synced to Zoho Backstage exhibitor <c>company_social_pages</c>.
    /// </summary>
    public string? TwitterUrl { get; set; }

    // --- Event Coordinator (the sponsor's primary contact) -------------------
    // Synced to the Zoho sponsor/exhibitor `contact` object (first/last/email +
    // phone on the exhibitor's mobile_no). Seeded by a one-time migration from the
    // webshop default event coordinator; thereafter CEH owns it (editable on
    // Company Details).
    public string? EventCoordinatorFirstName { get; set; }
    public string? EventCoordinatorLastName { get; set; }
    public string? EventCoordinatorCompanyName { get; set; }
    public string? EventCoordinatorEmail { get; set; }
    public string? EventCoordinatorPhone { get; set; }

    /// <summary>
    /// §597.4 — the Company Manager user id that was the company's <b>Default Event Coordinator</b>
    /// the last time CEH read it. Null = never read.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-07-28: *"maybe add the ability to select DEFAULT event coordinator here"* →
    /// *"then you know which will be synced to zoho"* → *"default comes from CM (default event
    /// coordinator)"*. So the POINTER is CM's to own, even though the VALUES above are CEH's once a
    /// human edits them on Company Details.</para>
    ///
    /// <para><b>This column is what makes both halves true at once.</b> Without it the fill was
    /// blank-only — CEH took the coordinator once and could never notice him changing it in CM
    /// afterwards. With it, a CHANGED pointer is unambiguous evidence of a deliberate decision in CM
    /// and the coordinator is re-read; an UNCHANGED pointer leaves whatever the hub holds alone, so
    /// a hub edit is never silently reverted by the next sync.</para>
    ///
    /// <para>🔒 <b>Why the pointer and not the e-mail.</b> Zoho hard-caps contact-e-mail updates at
    /// 3 attempts and a burnt cap means an exhibitor's LEADS ARE LOST (§596.1) — his words: *"which
    /// is a disaster as leads will be lost"*. Deriving the contact from "first coordinator in the
    /// list" would churn the e-mail every time a booth member was added or removed and spend the cap
    /// on nothing. A single pointer changes only when he changes it. The
    /// <see cref="ZohoContactEmail"/> guard still stands in front of every send regardless.</para>
    /// </remarks>
    public int? CmDefaultCoordinatorUserId { get; set; }

    /// <summary>
    /// The contact email LAST pushed to Zoho Backstage for this company (the
    /// sponsor/exhibitor record's contact email). Zoho hard-caps email updates at
    /// 3 attempts — even a no-op resend burns one — so the sync sends the contact
    /// email on UPDATE ONLY when the desired email differs from this stored value,
    /// then stamps the new value here on a successful email-changing update. Set on
    /// create (the email is sent once at create) and never re-sent on a no-op sync.
    /// Null until the company is created in / first synced to Zoho. REQUIREMENTS §41a.
    /// </summary>
    public string? ZohoContactEmail { get; set; }

    /// <summary>
    /// §302d (operator 2026-07-24, the perpetual "Social Pages" mails): the hash of the
    /// social/overview values LAST PUSHED to the Zoho exhibitor. LIVE FACT: the
    /// exhibitor PUT ACCEPTS company_social_pages / company_overview but the GET NEVER
    /// echoes them back, so a blank-in-Zoho check could never see them and the engine
    /// re-pushed + re-mailed every pass. CEH therefore remembers what it sent: re-push
    /// (and mail) ONLY when the CEH values differ from this stamp. Null = never pushed.
    /// </summary>
    public string? ZohoSocialPushedHash { get; set; }

    /// <summary>
    /// §596 — the hash of the SPONSOR-record profile values last pushed to Zoho (description +
    /// website). The sponsor twin of <see cref="ZohoSocialPushedHash"/>. Null = never pushed.
    /// </summary>
    /// <remarks>
    /// 🔒 WHY THIS EXISTS. Operator 2026-07-28: *"the sponsor for an exhibitor in zoho is not
    /// updated, when i change something in ceh … i have changed the company description but the
    /// description in the sponsor area doesn't reflect that"* — while the EXHIBITOR record showed
    /// his edit correctly.
    ///
    /// <para>Cause: the sponsor push was FILL-BLANK ONLY —
    /// <c>BlankInZoho(z?.Description) &amp;&amp; …</c>. Once Zoho held any description, that
    /// condition was false forever, so a CHANGED CEH description could never reach the sponsor
    /// record. The exhibitor side was already CHANGE-driven (via the social hash), which is exactly
    /// why one updated and the other did not.</para>
    ///
    /// <para>A hash stamp is used rather than a live diff even though the sponsor GET *does* echo
    /// the description: Zoho reformats rich text, so a char-compare would differ on every pass and
    /// re-push + re-mail forever — the §302 "70-mail night". Stamping what we sent means at most
    /// ONE push per real CEH change.</para>
    ///
    /// <para>⚠️ This push carries NO contact block. Zoho hard-caps sponsor e-mail updates at 3 and
    /// even a no-op resend burns one; exceeding it renders the sponsor AND exhibitor objects
    /// unupdatable, recoverable only by deletion — which loses leads (§596.1). The contact e-mail
    /// is still sent ONLY when it genuinely changed, on its own existing condition.</para>
    /// </remarks>
    public string? ZohoSponsorProfilePushedHash { get; set; }

    // --- Logos -------------------------------------------------------------
    // §468: these hold the SHAREPOINT webUrl returned by the upload (SponsorLogosFormService /
    // CompanyDetails), NOT a local path. The previous comment claimed "relative paths under
    // wwwroot, e.g. uploads/sponsors/<co>/logo.eps" — a leftover convention: nothing has written
    // to wwwroot at runtime for a long time, and since §462b (WEBSITE_RUN_FROM_PACKAGE=1) wwwroot
    // is READ-ONLY, so anything that tried would now fail outright. Corrected because that stale
    // comment is precisely what made a local-disk logo bug look plausible when it was not one.
    public string? LogoVectorPath { get; set; }
    public string? LogoVectorFileName { get; set; }
    public string? LogoRasterPath { get; set; }
    public string? LogoRasterFileName { get; set; }

    // --- Descriptions (char limits enforced in the form + service) -----------
    /// <summary>Up to 1000 chars. Used for company profile page and Zoho.</summary>
    public string? CompanyDescription { get; set; }

    /// <summary>Up to 80 chars. One-liner shown on listings.</summary>
    public string? CompanyDescriptionShort { get; set; }

    /// <summary>Up to 600 chars. Social-media announcement intro (bullets fine).</summary>
    public string? SocialMediaIntro { get; set; }

    // --- Release ring (company default for its contacts, REQUIREMENTS §23) ----
    /// <summary>
    /// This sponsor company's DEFAULT release ring — the fallback access level for
    /// every contact of this company that has no contact-level ring of its own.
    /// A contact's own <see cref="Participant.Ring"/> SUPERSEDES this; the
    /// effective ring of a sponsor contact is
    /// <c>contact.Ring ?? company.Ring ?? Broad</c> (link via
    /// <see cref="Participant.SponsorCompanyId"/> == <see cref="SponsorCompanyId"/>).
    ///
    /// Defaults to <see cref="Ring.Broad"/> (general availability) so an
    /// unassigned company behaves exactly as today (its contacts see only
    /// fully-released features unless given an earlier ring).
    /// </summary>
    public Ring Ring { get; set; } = Rings.Default;

    /// <summary>
    /// §905 — this company is TEST DATA: never announced, never rendered into a graphic, never
    /// named in a sponsor-tier post.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-06: <i>"we need to have that, so we fx can control test sponsors"</i>.
    /// The sibling of <see cref="Participant.IsTestUser"/>, which had no company-level counterpart —
    /// so "is this sponsor a fixture?" could only be INFERRED from its contacts.</para>
    ///
    /// <para>🔑 <b>Explicit beats derived, and the reason is a real company.</b> The derived rule is
    /// "every contact is a test user", and it is still applied as a fallback
    /// (<see cref="Integrations.TestDataScope"/>) so existing fixtures keep working with no data
    /// entry. But it cannot express a MIXED company: measured on PROD, the operator's own firm is a
    /// paying Gold sponsor carrying six test contacts beside six real ones. Only a column he sets
    /// himself can say which of those a company is.</para>
    ///
    /// <para>🔒 Defaults to <c>false</c>, so every existing sponsor is real until he says otherwise —
    /// the safe direction: a fixture that slips through is visible in the queue he approves, while a
    /// real sponsor wrongly marked test would silently vanish from the campaign (§842.5 makes that a
    /// contract breach).</para>
    /// </remarks>
    public bool IsTestData { get; set; }

    // --- Booth check-in (pre-day expected arrival, REQUIREMENTS §229) ---------
    /// <summary>
    /// §229: when the sponsor expects to arrive at their booth on the pre-day
    /// (9 Feb 2027). One of <see cref="BoothCheckInSlots.All"/> (canonical slot keys,
    /// incl. the "we don't expect to participate on pre-day" opt-out); null = not
    /// answered yet (the sponsor Get-Started step stays open).
    /// </summary>
    public string? BoothCheckInSlot { get; set; }
    public DateTimeOffset? BoothCheckInSetAt { get; set; }
    public string? BoothCheckInSetByEmail { get; set; }

    /// <summary>§298: how many booth members will check in on the pre-day. Feeds the organizer
    /// pre-day LUNCH headcount (each checked-in booth member eats the pre-day lunch). Null / 0 =
    /// not stated; ignored when the slot is the not-participating opt-out.</summary>
    public int? BoothCheckInMemberCount { get; set; }

    /// <summary>§292 — true when this sponsor bought a "Sponsor Sessions" speaking slot (webshop
    /// product category). Gates the extra Get-Started step where they register the session
    /// title/abstract + speakers. Set from the order pull (or manually) — see SponsorSession.</summary>
    public bool HasSponsorSession { get; set; }

    // --- Lifecycle (REQUIREMENTS §253, G8b) ----------------------------------
    /// <summary>
    /// Whether the company is still sponsoring. <see cref="SponsorStatus.Withdrawn"/>
    /// hides the company from the public sponsors page + the organizer sponsor
    /// counts; set via the organizer "withdraw company" action, whose cascade also
    /// deactivates the company's contacts and cancels its group party reservation.
    /// </summary>
    public SponsorStatus Status { get; set; } = SponsorStatus.Active;

    /// <summary>When the company was withdrawn (null while active).</summary>
    public DateTimeOffset? WithdrawnAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? LastUpdatedByEmail { get; set; }
}

/// <summary>
/// §229 — the canonical booth check-in slot keys + display labels for the pre-day
/// (9 Feb 2027) expected-arrival question in the sponsor Get-Started wizard.
/// </summary>
public static class BoothCheckInSlots
{
    public const string S0730 = "0730-0900";
    public const string S0900 = "0900-1030";
    public const string S1030 = "1030-1200";
    public const string S1200 = "1200-1500";
    /// <summary>"We don't expect to participate on pre-day."</summary>
    public const string NotParticipating = "not-participating";

    public static readonly IReadOnlyList<string> All =
        new[] { S0730, S0900, S1030, S1200, NotParticipating };

    public static bool IsValid(string? slot) => slot is not null && All.Contains(slot);

    public static string Label(string? slot) => slot switch
    {
        S0730 => "7:30–9:00",
        S0900 => "9:00–10:30",
        S1030 => "10:30–12:00",
        S1200 => "12:00–15:00",
        NotParticipating => "We don't expect to participate on pre-day",
        _ => "Not answered yet",
    };
}
