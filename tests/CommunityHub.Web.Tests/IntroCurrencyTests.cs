using System.Text.RegularExpressions;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §955 — THE CEH INTRODUCTION MUST NOT SILENTLY FALL BEHIND <c>FEATURES.md</c>.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-07: <i>"each feature must be detailed including those that was build during
/// the period since last update, verify against features.md"</i>.</para>
///
/// <para>🔴 <b>It had drifted by EIGHT chapters before anyone noticed</b>, and the only reason anyone
/// did was that somebody sat down and compared the two files by hand. That is the failure this class
/// exists to end: <i>"is the intro current?"</i> was a judgement, made from memory, at the end of a
/// busy week — which is precisely when it does not get made. §955 asked for it to become a test, the
/// same move <see cref="TestsDocCurrencyTests"/> made for TESTS.md.</para>
///
/// <para>🔑 <b>How it works, and why it is a MAP rather than a keyword search.</b> The obvious
/// implementation — grep the intro for words from each chapter title — is what produced the original
/// hand audit, and it is unreliable in both directions: an OR-matched keyword like "editor" hits
/// unrelated prose and reports a missing chapter as present, while a chapter genuinely covered in the
/// reader's language ("your test accounts stop ordering lunch" → "the lunch order") uses none of the
/// title's words. So coverage is DECLARED, once, per chapter. Adding a chapter to FEATURES.md fails
/// this test until somebody states what happened to it.</para>
///
/// <para>🔒 <b>"Not intro-relevant" is a legitimate answer, but it must be a STATED one.</b> The intro
/// is a customer-facing narrative, not a mirror of the changelog — a chapter can reasonably be a
/// refinement of something already described. <see cref="NotInIntro"/> records those WITH A REASON, so
/// the decision is visible and revisitable instead of being indistinguishable from an oversight.</para>
/// </remarks>
public sealed class IntroCurrencyTests
{
    private const string IntroPath = "config/content/eldk27/ceh-introduction.md";

    /// <summary>
    /// Chapters deliberately NOT given their own passage in the intro, each with the reason.
    /// ⚠️ Adding a line here is a decision, not a way to make the build green — if the chapter is
    /// something a customer would want to read about, write the passage instead.
    /// </summary>
    private static readonly Dictionary<int, string> NotInIntro = new()
    {
        [14] = "Bilingual UI — the intro itself is published in English only, so a passage about the "
             + "language switcher would be describing something the reader cannot see on the page.",
        [85] = "Session-description drift to the public event site — a refinement of the change "
             + "reporting the intro already describes (the hub tells you what to fix in Backstage), "
             + "not a new capability a reader would recognise as separate. It changes WHICH "
             + "differences are reported, which is an accuracy improvement rather than something "
             + "the customer chooses or operates.",
        [86] = "Prepaid coupon invoicing — an ORGANIZER-only finance workflow on "
             + "/Organizer/CouponInvoicing (raise the draft, print the notes, chase the promo code). "
             + "The intro is written for speakers/sponsors/volunteers/attendees, none of whom ever "
             + "see this page or the invoice it raises.",
        [87] = "Extending a prepaid block — the same ORGANIZER-only coupon page as §86, and a "
             + "refinement of it rather than a separate capability. No participant role sees it.",
        [88] = "Speaker photos rendering correctly — a defect fix, not a capability. The intro "
             + "already describes the speaker profile and the public lineup; a passage saying the "
             + "pictures now load would be describing the absence of a bug.",
        [89] = "The topbar ticket countdown — site chrome the reader sees on every page of the hub "
             + "itself, so the intro describing it would be narrating the page it is printed on.",
        [90] = "Session-SPEAKER drift to the public event site — the §85 decision, for the sibling "
             + "field. It is an ORGANIZER-only ops mail (info@) about what to correct in Backstage; "
             + "no speaker, sponsor, volunteer or attendee ever sees it, and for them the outcome is "
             + "simply that the public agenda is right, which the intro already promises.",
        [91] = "\"Common for All Tracks\" — an ORGANIZER tick box on /Organizer/Sessions plus the "
             + "drift check behind it. A reader of the intro sees only the RESULT (the plenary "
             + "sessions are not filed under one track on the agenda), never the control.",
        [92] = "Session type reaching Backstage as Keynote — the same §85/§90 class: it changes "
             + "which differences the organizer is told about and what the create sends. The "
             + "attendee-visible outcome is an agenda that says Keynote, which needs no passage.",
        [95] = "The organizer sessions page — editing, speaker linking, the calculated end time and "
             + "the test-session filter. Entirely an ORGANIZER admin surface; a speaker sees the "
             + "RESULT (their session details are right) on their own page, which the intro covers.",
        [96] = "Webshop coupon discounts printed on the ERP invoice — a finance document for a "
             + "SPONSOR's accounts payable, not a hub page. No intro reader ever sees it.",
        [97] = "Ticket-class names instead of ids — a correctness fix across organizer pages, "
             + "invoices and ops mail. For an intro reader nothing changes: they never saw an id.",
        [98] = "The hub owning the schedule, with no switch to reverse it — an integration "
             + "ownership rule between the hub, the event platform and the call-for-speakers tool. "
             + "The reader's outcome is simply that the public agenda is correct.",
        [94] = "The 'notify requester' button — an ORGANIZER control on /Organizer/CouponInvoicing. "
             + "The mail it sends goes to a PARTNER's billing contact, who is not a hub participant "
             + "and never reads the intro; for everyone the intro IS written for, this changes "
             + "nothing they can see or operate.",
        [100] = "WHEN a rule may auto-approve a template-built post (inside the lead window, never "
              + "past-dated). Entirely ORGANIZER machinery on the campaign side: no speaker, "
              + "sponsor, volunteer or attendee can see or operate it. What an intro reader sees is "
              + "the STATE on their own announcement — planned until the organizers have accepted "
              + "and approved it — and §99's row already promises exactly that.",
        [93] = "Prepaid coupon invoicing corrections — the §86/§87 decision again. This is the "
             + "ORGANIZER-only /Organizer/CouponInvoicing page and the accounting draft behind it; "
             + "no speaker, sponsor, volunteer or attendee sees the page, the invoice number or the "
             + "unit-price field.",
        [106] = "Ticket types written as \"1-day\"/\"2-day\" instead of the system's own words — a "
              + "WORDING fix on ORGANIZER surfaces (the attendee grid, its export, the printable "
              + "on-site list). No attendee, speaker or sponsor ever saw the old wording, and for "
              + "them nothing changes.",
        [107] = "Volume-package QUALIFICATION — the /Organizer/VolumePackage page: which companies "
              + "have reached ten attendees, and where the count came from. Entirely organizer "
              + "machinery today; nothing reaches a company, so an intro reader has nothing to see "
              + "or operate. ◻ REVISIT AT STAGE 3: the wizard, the group photo and the token page "
              + "ARE company-facing, and that is the passage worth writing — one passage covering "
              + "the benefit as a company experiences it, rather than three describing its plumbing.",
        [108] = "Volume-package APPROVAL REQUESTS — the mail asking the ORGANIZER mailbox who at a "
              + "qualifying company we should deal with, plus the approve/overrule controls on the "
              + "same organizer page. It is deliberately the one mail that goes to us and not to "
              + "them: nobody at the company is contacted, so there is nothing for an intro reader "
              + "to recognise. ◻ Same revisit as §107.",
        [112] = "Group-photo planning, publishing and the event partner's Excel + calendar. Pure "
              + "ORGANIZER and event-partner logistics: the intro's reader is a speaker, sponsor, "
              + "volunteer or attendee, and none of them see the planner, the slot list or the "
              + "partner's running order. What a company sees is its own time, which §110's page "
              + "already carries. ◻ Same revisit as §107/§108/§110/§111.",
        [111] = "The volume-package group photo and its weekly reminder. Organizer scheduling plus "
              + "one company coordinator's own page; the intro's reader is a speaker, sponsor, "
              + "volunteer or attendee, and an attendee is deliberately never written to about the "
              + "photo at all. ◻ Same revisit as §107/§108/§110.",
        [110] = "The volume-package company wizard. ⚠️ This one IS company-facing — but its audience "
              + "is a purchaser or a marketing coordinator at a customer, reached by a private link, "
              + "not a hub participant reading the introduction. The intro's reader is a speaker, "
              + "sponsor, volunteer or attendee; for them the volume package is something their "
              + "employer arranges. ◻ Revisit if the intro ever grows a buyer-facing section — that "
              + "is where §107, §108 and this belong TOGETHER, as one story about the benefit.",
        [109] = "The media crew's picture and video libraries. A CREW WORKING SURFACE for the press / "
              + "photo / video team and organizers — no speaker, sponsor, volunteer or attendee can "
              + "see or operate it. What an intro reader eventually meets is the PHOTOGRAPHS, on the "
              + "event site and in the announcements, which is a different subject from where the "
              + "crew keeps them.",
        [113] = "The Get Started reminder now naming what the team has ALREADY done, and who did it. "
              + "⚠️ This IS sponsor-facing, and it is a real improvement — but it describes the "
              + "wording of a REMINDER EMAIL, not a capability anyone operates. The intro already "
              + "promises that sponsor onboarding is shared across a company's event coordinators; "
              + "that promise is what a reader recognises, and this makes the mail finally say it. "
              + "◻ Revisit if the intro grows a passage about what the hub writes to you.",
        [114] = "Company details requiring the description, short description and social-media text. "
              + "A COMPLETION RULE behind a step the intro already describes — the reader meets it as "
              + "'the step asks for these fields', which is what any onboarding step does. Writing a "
              + "passage would mean narrating a validation rule rather than a capability. ⚠️ The "
              + "consequence a customer WOULD recognise (their sponsor announcement post cannot be "
              + "published without the text) belongs with the SoMe story, not here.",
        [115] = "The website address becoming read-only with a hand-off to the webshop. A "
              + "field-ownership correction: the value is unchanged and still shown, it is simply "
              + "edited where it is actually mastered. The intro does not enumerate which of a "
              + "sponsor's fields are hub-owned versus webshop-owned, and starting here — with one "
              + "field, in the middle of onboarding — would raise a question it does not answer.",
        [116] = "Sponsor reminders reaching the whole coordinator team. ⚠️ Sponsor-facing and a real "
              + "correction — but it describes WHO RECEIVES an email, which is exactly what a reader "
              + "cannot observe: from any one coordinator's seat the visible behaviour is 'I get my "
              + "reminders', before and after. The intro already promises that sponsor onboarding is "
              + "shared across a company's event coordinators; this makes that promise true in the "
              + "mail path rather than adding something to describe. ◻ Revisit together with §113 if "
              + "the intro ever grows a passage about what the hub writes to you and who else sees it.",
        [117] = "The all-roles participant-status board (/Organizer/ParticipantStatus). An ORGANIZER "
              + "chase list — the §95/§107 class: no speaker, sponsor, volunteer or attendee can see "
              + "or open it. ⚠️ And what it shows them IS already theirs to read: every figure on it "
              + "is the person's own Get Started progress, which they see on their own page and which "
              + "the intro already describes. A passage here would tell a reader that somebody else "
              + "can also see the percentage they are looking at — true, and not a capability they "
              + "gain. What changes for them is that a coordinator is now chased for the right thing, "
              + "which is an outcome rather than a page.",
        [118] = "Catering head-counts computed by one engine, and attendee totals that exclude "
              + "cancelled tickets. ORGANIZER machinery end to end: the lunch tiles, the headcount "
              + "page and the spreadsheet that goes to the VENUE. ⚠️ It matters enormously — it is "
              + "whether there is lunch for everyone — but an intro reader neither operates it nor "
              + "sees it: what they experience is that food was ordered for the right number of "
              + "people, which is the event working rather than a feature they use.",
        [119] = "The main-day lunch spreadsheet and the removal of dietary columns from the lunch "
              + "files. A VENUE-facing report plus a statement about where diets are handled "
              + "(the ordering process, not the hub). ⚠️ The one participant-visible half — the "
              + "Appreciation Dinner asking for dietary requirements — is not new and the intro "
              + "already describes the dinner sign-up; what changed is a default nobody should "
              + "notice, because it is what an untouched form should always have stored.",
        [120] = "Coupon invoicing moving onto the organizer MENU. The §86/§87 page, and the same "
              + "answer: an ORGANIZER-only finance workflow no speaker, sponsor, volunteer or "
              + "attendee ever opens. ⚠️ And this is a NAVIGATION change to it — a passage would be "
              + "telling a reader where a menu entry sits on a page they cannot reach.",

        // §121–§128 — the coupon/customer-billing story, catalogued 2026-08-21. ⚠️ These are NOT the
        // §86/§87 "organizer-only" excuse: half of them ARE seen by somebody outside the organizer
        // team. But that somebody is a PURCHASER — the person at a customer who bought a block of
        // tickets and reads a link about their own usage — not a hub participant reading the
        // introduction, who is a speaker, sponsor, volunteer or attendee. It is the §110 distinction,
        // and it now covers a whole chapter range rather than one page.
        // ◻ REVISIT TOGETHER IF THE INTRO EVER GROWS A BUYER-FACING SECTION: §107, §108, §110 and
        // §121–§128 belong there as ONE story about buying tickets for your people, not as nine.
        [121] = "The agreed price and the invoiced share on a coupon. ORGANIZER finance "
              + "configuration — two fields on the coupon page that decide what a customer's invoice "
              + "says. Nobody outside the organizer team sets them, and what a purchaser meets is a "
              + "correct invoice, which is the event working rather than a feature they use.",
        [122] = "The customer's own usage page and its export. ⚠️ Genuinely customer-facing — but "
              + "for a PURCHASER holding a private link, not for a hub participant. ◻ See the "
              + "buyer-facing-section note above.",
        [123] = "Self-service 'Increase max'. Same audience as §122 — the purchaser who bought the "
              + "block, reached by their own link. ◻ Same revisit.",
        [124] = "The near-the-agreed-number alert. It goes to the ORGANIZER mailbox: it is the hub "
              + "saying 'you cannot stop a claim, and this customer is about to pass what they "
              + "agreed'. An intro reader is neither the sender nor the recipient.",
        [125] = "The claim invitation going out automatically once the code is live. It describes "
              + "WHEN an email is sent and to whom — which from the recipient's seat is simply 'I "
              + "was told I could start claiming'. The ORDERING is the substance, and ordering is "
              + "not something a reader observes. ◻ Revisit with §113/§116 if the intro grows a "
              + "passage about what the hub writes to you.",
        [126] = "The two per-coupon contact ticks. ORGANIZER control over who gets written to — and "
              + "its whole purpose is that a quiet customer stays quiet, i.e. its success case is "
              + "the ABSENCE of mail. There is nothing for a reader to recognise.",
        [127] = "The wording, spacing and font of the coupon mails. ⚠️ The house-font and "
              + "button-width half touches EVERY mail the platform sends, including a speaker's — "
              + "but it is a rendering fix, and the intro already describes the things those mails "
              + "are about. A passage saying the emails are now legible would be describing the "
              + "absence of a defect (the §88 decision).",
        [128] = "The organizer mailbox being CC'd on customer coupon mails. It describes WHO ELSE "
              + "receives an email — the §116 decision exactly: from the recipient's seat nothing "
              + "changes. ◻ Same revisit as §116.",
        [129] = "The guest-category note on the pending-speaker page and its approval mail. ⚠️ It is "
              + "ABOUT speakers, which is why it is worth stating why it is not FOR them: the page "
              + "and the mail are the ORGANIZER's, and the rule is guidance for whoever approves. "
              + "What a speaker experiences is that their hotel and travel are covered as agreed — "
              + "an outcome the intro already promises, not a screen they operate.",

        // §131–§140 — the defect fixes catalogued 2026-08-21 at the operator's request ("add the
        // remaining fixes to features too"). ⚠️ They are in FEATURES.md because he wants the record
        // complete, and they are excused here for the reason the §88 entry already gives: a fix
        // describes the ABSENCE of a defect. The intro promises the working behaviour, and promised
        // it before the fix — so a passage would either repeat the promise or advertise that it had
        // not been true.
        [130] = "Sponsor social links reaching the public exhibitor page. ⚠️ Sponsor-facing and a "
              + "real gain — but the defect was in the PUBLISHING PLATFORM, not the hub, and the "
              + "hub's side never changed. The intro already says a company's details flow to the "
              + "public listing; this makes that true for two more fields. ◻ Revisit only if the "
              + "intro ever enumerates WHICH fields publish, which it deliberately does not.",
        [131] = "A deliberate skip counted as a failure. ORGANIZER telemetry — a job summary, a "
              + "button caption and a log level. No participant sees any of the three surfaces.",
        [132] = "The prepaid billing badge wording. An ORGANIZER finance page (§86/§87 again), and a "
              + "two-word chip on it.",
        [133] = "The two-form coupon card. Same organizer finance page; a LAYOUT correction to it.",
        [134] = "Wording and form fixes on the coupon page. Same page. ⚠️ The 'partner' → 'customer' "
              + "half also touches the customer MAILS — but that is a word choice in a mail sent to "
              + "a purchaser, not to an intro reader (see the §121–§128 note).",
        [135] = "Names instead of machine ids on invoices and customer mails. The audience is the "
              + "organizer reading an invoice and the purchaser receiving one. ⚠️ And it is a fix: "
              + "the intro never promised an id, so there is nothing here to newly promise.",
        [136] = "The claim invitation's web address. A defect in a mail sent to a purchaser, and the "
              + "correct behaviour was always the promise. ◻ Same revisit as §121–§128.",
        [137] = "Withdrawing a company that has left the sponsor customer group. ⚠️ Its EFFECT is "
              + "public — the company leaves the sponsors page — but the audience of the capability "
              + "is the organizer, and what an intro reader sees is a sponsors page that is correct, "
              + "which the intro already promises. A passage would describe our bookkeeping.",
        [138] = "A network fault reported as itself instead of as a customer's order. Entirely "
              + "internal: the mail it corrects goes to the organizer mailbox.",
        [139] = "The ERP reconcile actually performing the deactivation its mail claimed. ORGANIZER "
              + "machinery and an ops mail. ⚠️ What a participant would have experienced is an "
              + "account that should have been closed staying open — the absence of a defect again, "
              + "not a capability.",
        [140] = "The reconcile mail repeating only when its list changes. It describes the CADENCE "
              + "of a mail to the organizer mailbox — the §116/§128 decision, and this one is not "
              + "even sent to a customer.",

        // §1121/§1122/§1123/§1124 — the whole 141–145 run is INTERNAL OPS MAIL: who on the organizing
        // team receives a to-do, and what that to-do says. The intro is written for speakers,
        // sponsors, volunteers and attendees, and NONE of these five mails is ever sent to one of
        // them. A passage about them would describe a mailbox the reader has no access to.
        [141] = "Speaker/session to-dos reaching the right organizers. It is the RECIPIENT LIST of "
              + "internal ops mail — no speaker, sponsor, volunteer or attendee ever receives one of "
              + "these, so there is nothing here a reader of the intro could recognise or act on.",
        [142] = "The social-media approval notice showing the post. The notice goes to the organizer "
              + "mailbox so an organizer can overrule a publish date. The customer only ever sees "
              + "the published POST, which the intro already covers.",
        [143] = "A 'do this by hand' mail naming the right system. A copy fix inside an internal ops "
              + "mail about the event platform's own interfaces — the intro deliberately does not "
              + "describe which back-office system CEH writes to, or how.",
        [144] = "One audience for all seven speaker/session to-dos. A consistency change to internal "
              + "mail ROUTING; the customer-visible behaviour is identical either way.",
        [145] = "A speaker alert that was never arriving now arrives. The alert goes to the organizer "
              + "mailbox, not to the speaker — the speaker's own experience is unchanged, so the "
              + "intro has nothing new to tell them.",

        // §1125 — ⚠️ This one IS sponsor-visible, unlike 141–145, so the reason is different in kind
        // and worth stating plainly rather than reusing the "internal ops mail" line.
        [146] = "A website corrected in the webshop reaching the hub. It is a DEFECT FIX that "
              + "restores behaviour the product already promised — the company-details page has "
              + "always said the address comes from the webshop and syncs back automatically. The "
              + "intro describes the webshop as the source of sponsor company data already; a "
              + "passage saying 'and this time it really does update' would document the bug, not "
              + "the capability. ⚠️ If the intro is ever rewritten to walk through sponsor company "
              + "details field by field, the webshop-owned website belongs in that passage.",

        [147] = "LinkedIn and X/Twitter following the webshop. Same reasoning as §146, of which this "
              + "is the widening: WHICH of a sponsor's three URLs the webshop owns is a data-"
              + "ownership detail, and the intro already presents the webshop as where sponsor "
              + "company data is maintained. ⚠️ Same caveat — a field-by-field sponsor walkthrough "
              + "should name all three as webshop-owned.",

        [148] = "The reminder when a sponsor's webshop links are missing. A CHASER — it exists to "
              + "get a blank field filled, and nobody chooses or operates it. The intro sells what "
              + "the platform does for a sponsor; 'we will email you if you left something out' is "
              + "housekeeping around the webshop-owned fields §146/§147 already cover.",

        [149] = "The contact no longer reported as hand-work when the event platform already has "
              + "it. A defect fix in an INTERNAL organizer report — no sponsor, speaker, volunteer "
              + "or attendee ever sees that mail, and the sponsor's own experience is unchanged "
              + "either way.",

        [150] = "The organizer filter rows lining up. Cosmetic alignment on two ORGANIZER-only admin "
              + "grids (/Organizer/Participants, /Organizer/Speakers). No speaker, sponsor, "
              + "volunteer or attendee can reach those pages, so there is nothing here for a reader "
              + "of the intro to recognise.",

        [151] = "Readable text and a tidier toolbar on the organizer lists. Same reasoning as §150 — "
              + "presentation of ORGANIZER-only admin grids, unreachable by any other role.",
        [152] = "The participants export as a button. An ORGANIZER-only control on an organizer-only "
              + "page; the intro does not describe the admin toolbar at all.",

        [153] = "Speaker/volunteer photos also saved under the person's name. A FILING convention "
              + "inside the organizers' own SharePoint folder — speakers and volunteers never see "
              + "it, their upload and their published picture are unchanged, and the intro does not "
              + "describe where files are stored.",

        [154] = "Volunteer availability showing each day's start time. ⚠️ This one IS volunteer-"
              + "facing, so the reason is different in kind: the CAPABILITY (state how much you can "
              + "help per day) is unchanged and already described — what changed is the per-edition "
              + "HOURS, which are config that moves every year. Pinning 06:40 into the intro would "
              + "make it wrong for ELDK28 while looking authoritative.",

        [155] = "The hotel form accepting every real arrival night. Same reasoning as §154: the "
              + "CAPABILITY (book your hotel through the hub) is unchanged and already described — "
              + "what changed is the per-edition bookable RANGE, which is config that moves every "
              + "year. A fixed '5–12 February' in the intro would be wrong for the next edition.",

        [156] = "Audit times in Copenhagen time. An ORGANIZER-only page (/Organizer/Audit) that no "
              + "speaker, sponsor, volunteer or attendee can reach, and a display preference on it "
              + "at that — the intro does not describe the admin tooling.",

        [157] = "Audit exports including Copenhagen time. The export of an ORGANIZER-only page; a "
              + "column layout in a troubleshooting file that no customer ever receives.",

        [158] = "Clearer main-day option for volunteers attending the conference. ⚠️ Volunteer-"
              + "facing, so the reason is the §154 one: the CAPABILITY (say how much you can help "
              + "each day) is unchanged and already described — this is the WORDING of one choice, "
              + "and the 17:00 in it is per-edition timing that moves every year.",

        [159] = "Company billing details following the finance system. An ORGANIZER/back-office data-"
              + "ownership rule between the webshop and the ERP — no speaker, sponsor, volunteer or "
              + "attendee sees either system, and the intro deliberately does not describe which "
              + "back-office system owns which field.",
        [160] = "VAT zone changes reaching the webshop. The §159 decision for the sibling field — "
              + "the same ERP/webshop ownership rule, and a defect fix within it rather than a new "
              + "capability. What a reader would see is an invoice that is correct, which is not "
              + "something the intro promises to explain the mechanics of.",
        [180] = "Availability grouped by where each volunteer stands. Layout and subtotals on "
              + "the ORGANIZER-only availability grid (§167/§170). A volunteer never sees the grid, "
              + "and their own answers are unchanged — only how organizers read them.",
        [178] = "Fewer add-by-hand e-mails. An ORGANIZER ops mail about an integration, and "
              + "about which failures are worth a human. No participant receives it or is affected "
              + "by it.",
        [179] = "Sponsor cards on the sponsor hub too. Navigation: the same organizer-only pages "
              + "reachable from a second hub. Nothing a participant sees.",
        [208] = "Track artwork retiring with its track. Campaign-engine housekeeping behind the "
              + "graphics run. A speaker sees their own graphic either way; which track bundle is in "
              + "circulation is not something they see or act on.",
        [209] = "Being told when hand-made artwork ages. An ORGANIZER-only report on the graphics "
              + "run, about files only organizers replace.",
        [210] = "Published LinkedIn posts named in the audit log. An ORGANIZER-only record in an "
              + "organizer-only page; the posts themselves go out exactly as before.",
        [211] = "Telemetry card headings corrected. A label defect fix on an existing page the intro "
              + "already describes; no new capability.",
        [212] = "One-speaker override for a master class announcement. An ORGANIZER-only exception on "
              + "an existing approval check; what goes out is the same kind of post as before.",
        [213] = "Automatic public-name fixes listed as receipts in the reconcile mail. A wording fix "
              + "in an ORGANIZER-only ops mail; the public-name behaviour itself is unchanged.",
        [214] = "No repeated Website pushes when the event platform refuses a read. A defect fix in a "
              + "background sync; nothing a sponsor or attendee sees changes.",
        [215] = "Deleted event-platform sponsor entries cleaned up after repeated proof. A self-heal "
              + "in a background sync; what a sponsor appears under is unchanged.",
        [216] = "Get Started reminder reaches automatically welcomed sponsors. A defect fix to an "
              + "existing reminder the intro already describes; the reminder itself is unchanged.",
        [217] = "Company names on the sponsor welcome overview. An ORGANIZER-only display fix on an "
              + "admin page; nothing a sponsor sees changes.",
        [218] = "Session announcements tag the speakers. A wording fix to existing social-media posts "
              + "the intro already describes; no new capability.",
        [219] = "No hand-entry mail for platform fields the hub writes. A defect fix in an ORGANIZER-only "
              + "ops mail; nothing a sponsor sees changes.",
        [220] = "Shared-mailbox sponsor contacts reach the webshop. A defect fix in a background sync; "
              + "no new capability.",
        [221] = "The public edition builds and its setup guide matches what it ships. A fix to the "
              + "open-source template's build and getting-started documentation for communities running "
              + "their own copy; nothing an organizer, speaker, sponsor or attendee of this event sees changes.",
        [222] = "A fresh install comes with default content. Neutral default texts shipped only in the "
              + "open-source template for other communities; this event's own content and hub are unchanged.",
        [223] = "Installing your own copy step by step. Install scripts and docs for the open-source "
              + "template only; nothing in this event's hub changes.",
        [206] = "Email export on the participant status board. An ORGANIZER-only convenience over "
              + "addresses the organizer already holds; nothing a participant sees, sets or receives "
              + "differently.",
        [207] = "Filters that mean what they say, and test users hidden. A defect fix plus a filter "
              + "on an ORGANIZER-only board. Test accounts are an internal concept a customer-facing "
              + "narrative should not introduce at all.",
        [205] = "The sponsor Get-started chase list. An ORGANIZER-only view over data the sponsor "
              + "already sees on their own Get started page, which the intro describes. Nothing new "
              + "for a sponsor to do, see or operate.",
        [202] = "A dated round being planned. A defect fix inside the campaign engine, where two "
              + "pages each held a round count and the stale one won. ORGANIZER-only scheduling; a "
              + "speaker or sponsor sees the dates of their own posts either way.",
        [203] = "The post list naming 2a/2b/2c. A column label on the ORGANIZER-only post queue, "
              + "matching the names the settings page already used.",
        [204] = "The hidden test-data flag on a session. An ORGANIZER-only correction: a legacy flag "
              + "that excluded a session from the campaign with no way to see or clear it. Nothing a "
              + "participant sets, sees, or is affected by beyond the post appearing as it should.",
        [201] = "\"Not ready\" naming what is missing. A label on the ORGANIZER-only queue and post "
              + "calendar. The person who owes the missing logo or text is told on their own page "
              + "already (that IS described in the intro); this is the organizer's side of the same "
              + "fact.",
        [197] = "Event posts keeping their identity. A defect fix inside the campaign engine — the "
              + "planner was rebuilding the organizer's own dated posts every ten minutes and "
              + "re-issuing their ids. Nothing a participant chooses or operates, and the posts "
              + "themselves went out unchanged either way.",
        [198] = "One announcement per subject per round. A correctness rule inside the ORGANIZER-only "
              + "post planner. A speaker or sponsor would only ever have seen the effect as a second "
              + "identical post, which is the thing being prevented rather than a capability.",
        [199] = "What the held-back announcements are waiting for. A diagnostic list on the "
              + "ORGANIZER-only SoMe settings page. The underlying requirement — a sponsor owes their "
              + "logo and social text — is already described where the sponsor is asked for it.",
        [200] = "Held-back announcements staying in the plan and moving forward. Campaign-engine "
              + "behaviour behind the organizer's calendar, in the same class as §165 and §191. A "
              + "speaker or sponsor sees the date of their own post on their own page either way; "
              + "which pass re-dated it is not visible to them.",
        [196] = "Posting capacity. An ORGANIZER planning view over the campaign calendar. A "
              + "speaker or sponsor sees their own posts and their dates either way; whether the "
              + "organizers have room for everything is not something they see or act on.",
        [195] = "Announcement rules per category. ORGANIZER-only scheduling controls and the "
              + "engine behind them. A speaker or sponsor sees the dates of their own posts on their "
              + "own page either way; how many rounds a category has and when each opens is not "
              + "something they set or see.",
        [194] = "Master classes and other sessions starting on their own dates. An ORGANIZER "
              + "scheduling field. A speaker sees the date of their own session post on their own "
              + "page either way; when the category opens is not something they set.",
        [193] = "Every round of every post type having its own start date. ORGANIZER-only "
              + "scheduling fields and the round counts behind them. A speaker or sponsor sees the "
              + "dates of their own posts on their own page either way; how many rounds there are "
              + "and when each opens is not something they set or see.",
        [189] = "Announcement dates arranged by post type. Layout and labelling of an ORGANIZER-only "
              + "settings page, plus two values moving from code into that page. A participant never "
              + "sees the page and the campaign's output is unchanged by where the fields sit.",
        [190] = "Sponsor tier posts as two named rounds. An ORGANIZER scheduling control. A sponsor "
              + "sees their tier post and its date on their own page either way; when the round runs "
              + "is not something they set.",
        [191] = "Changing a date moving posts that already exist. Campaign-engine behaviour behind "
              + "the ORGANIZER-only settings above. For a sponsor or speaker the outcome is simply "
              + "that the date shown on their page is the one that happens, which the intro already "
              + "promises.",
        [192] = "Seeing what else is on a day before rescheduling. An aid inside the ORGANIZER-only "
              + "post editor. No participant role can reach the editor.",
        [186] = "No posts over Christmas and New Year. A scheduling rule inside the campaign engine. "
              + "A reader sees only that nothing arrived over the holidays, which is an absence and "
              + "needs no passage; the organizer sets nothing and operates nothing.",
        [187] = "A start date for sponsor announcements. An ORGANIZER setting on the SoMe settings "
              + "page, alongside the two announcement dates already there. A sponsor sees their own "
              + "posts and their dates on their own page either way; when the campaign chooses to "
              + "begin is not something they set or see.",
        [188] = "The post editor surviving a duplicated post. A server-error fix on an "
              + "ORGANIZER-only page. No participant role can reach the post editor, and describing "
              + "the absence of a crash is not a capability.",
        [182] = "Sponsor promotion graphics reaching the sponsor's post. A defect fix inside the "
              + "campaign machinery — a release gate that nothing could pass. The intro already "
              + "promises the sponsor a promotion graphic and a preview of the post it goes in; a "
              + "passage saying the two are now connected would be describing the absence of a bug.",
        [183] = "One answer to whether a post has a picture. The same class as §182: three views "
              + "agreeing about the artwork they already claimed to show. For the sponsor or speaker "
              + "reading their own page the promise is unchanged — the preview is what publishes — so "
              + "there is nothing new for them to learn or operate.",
        [184] = "Test and excluded sessions out of the campaign and its graphics. Two ORGANIZER tick "
              + "boxes on /Organizer/Sessions and the exclusion rule behind them. A participant "
              + "never sees the controls, and the outcome for them is that nothing about a rehearsal "
              + "session is announced — an absence, which no passage can usefully describe.",
        [185] = "Excluded subjects removed from the post planner. The enforcement half of §184, on "
              + "the ORGANIZER-only post queue. It changes what an organizer finds in their own "
              + "planner; no speaker, sponsor, volunteer or attendee has a view of it.",
        [181] = "The same event-platform update announced once. The §178 decision for a second ops "
              + "mail — how often an ORGANIZER integration message repeats itself, and when a write "
              + "that is not landing is worth a human. No participant receives it, and the sponsor "
              + "data it concerns reaches the public site either way.",
        [177] = "Company names staying correct on the public event site. A back-office "
              + "integration rule between the webshop, the hub and the event platform. What a "
              + "reader sees is a sponsor listed under the right name, which the intro already "
              + "promises; which system owns that name is not something they operate.",
        [176] = "Web addresses without https://. Input handling on the volunteer sign-up plus "
              + "how an organizer-only queue renders a link. Nothing a participant chooses or "
              + "operates — it just accepts what they were always going to type.",
        [175] = "Being featured needs a photo and a LinkedIn link. The §173 rule extended to the "
              + "second field the same consent names. Validation text on the public sign-up, read "
              + "in context at the moment of choosing — not a capability the intro describes.",
        [173] = "A photo required when asking to be featured. ⚠️ VOLUNTEER-facing, on the public "
              + "sign-up form — but it is validation text read in context at the moment of "
              + "choosing, not a capability. The intro describes what volunteering involves, not "
              + "which fields the form insists on.",
        [174] = "LinkedIn profiles on the pre-selection queue. An ORGANIZER convenience on an "
              + "organizer-only page; the applicant already knows they gave us the link.",
        [172] = "Free-ticket attendees showing a country. The attendee DASHBOARD, which is an "
              + "organizer/sponsor analytics surface. An attendee never sees the country breakdown, "
              + "and nothing about their own record changed — only how a blank is rendered in an "
              + "aggregate chart.",
        [170] = "A page for volunteer availability. The §167 grid, moved to its own ORGANIZER "
              + "page instead of sitting under the hub links. A relocation of an organizer-only "
              + "surface; nothing a participant can reach changed.",
        [171] = "Deactivate says what it does, and asks you to type it. An ORGANIZER safety "
              + "control on the pre-selection queue. The behaviour it guards is unchanged and "
              + "already invisible to participants — a hidden sign-up simply never hears from us.",
        [167] = "Volunteer availability at a glance. An ORGANIZER planning surface on the "
              + "pre-selection queue and the Volunteers hub. A volunteer answers the availability "
              + "question on the sign-up form; how organizers then READ those answers is not "
              + "something any participant role sees or operates.",
        [168] = "More of the sign-up visible where you choose people. The same ORGANIZER queue as "
              + "§167 — contact details, consent and the ring picker. Every value shown was "
              + "submitted by the volunteer on a form they already know about.",
        [169] = "Undo a pre-selection without losing the sign-up. An ORGANIZER correction on a "
              + "state no participant can see: Preselected grants nothing and sends nothing, so "
              + "for the volunteer there is literally nothing to observe.",
        [163] = "Sponsor artwork staying in step with the files. ORGANIZER/back-office integrity "
              + "between the shared library and the hub's own record. A sponsor sees only that their "
              + "announcement goes out; there is no control and no visible behaviour to describe.",
        [164] = "Clearer sponsor logo guidance. ⚠️ Sponsor-facing, and deliberately NOT in the "
              + "intro: it is instruction text ON the upload control itself, read at the moment of "
              + "choosing a file. An intro passage would describe a sentence the reader will see in "
              + "context anyway, and could drift from it.",
        [165] = "Slipped announcements re-scheduled. ORGANIZER campaign machinery on the SoMe "
              + "planner. What a reader sees is an announcement arriving; the slot arithmetic "
              + "behind it is not something any participant role operates.",
        [166] = "Volunteer photos filed under both name and id. A FILING convention inside the "
              + "organizers' own library — the §153 decision for the back-fill of it. The volunteer "
              + "uploads one picture either way and sees no difference.",
        [162] = "A steadier connection to the event platform. Infrastructure: how the hub shares "
              + "its authorisation for the event platform between background workers. No participant "
              + "role observes it at all — the visible outcome is that syncs the intro already "
              + "promises simply keep working.",
        [161] = "Fewer false alarms from the signage sync. The notice in question goes to the "
              + "ORGANIZER mailbox, never to a participant, and the screens themselves behaved "
              + "correctly throughout — they hold the last good agenda. For every reader of the "
              + "intro the observable behaviour is unchanged, so there is nothing to describe.",
    };

    /// <summary>
    /// Chapters below this are GRANDFATHERED — the test enforces coverage from here on.
    /// </summary>
    /// <remarks>
    /// <para>🔑 <b>Why a baseline rather than mapping all 82 retroactively.</b> The intro was
    /// restructured and re-audited against the catalogue at §953/§954, and §955 then MEASURED where it
    /// had fallen behind: §74 onward. Hand-mapping the earlier 73 chapters to passages written in a
    /// customer's language — where "your test accounts stop ordering lunch" legitimately reads as "the
    /// lunch order" — is exactly the memory-and-judgement exercise §955 asked to be rid of, and it
    /// would produce a map nobody could trust.</para>
    ///
    /// <para>🔒 <b>The purpose is to stop the NEXT drift, not to relitigate the last one.</b> Eight
    /// chapters accumulated because nothing failed when one was forgotten; from here, one does.</para>
    ///
    /// <para>⚠️ Do NOT raise this number to make a failure go away — that silently re-opens the gap.
    /// Lowering it, chapter by chapter, as older passages get marked, is the improvement path.</para>
    /// </remarks>
    private const int FirstEnforcedChapter = 74;

    [PrivateContentFact] // the public default intro is a snapshot; this pins the upstream intro to FEATURES.md
    public void Every_FEATURES_chapter_is_either_in_the_intro_or_explicitly_excused()
    {
        var repoRoot = FindRepoRoot();
        var features = File.ReadAllText(Path.Combine(repoRoot, "docs", "FEATURES.md"));

        // The doc's own convention throughout: "## 82. Add somebody the sync has not heard of yet".
        var chapters = Regex.Matches(features, @"(?m)^##\s+(\d+)\.\s+(.+?)\s*(?:\*\(|$)")
            .Select(m => (Number: int.Parse(m.Groups[1].Value), Title: m.Groups[2].Value.Trim()))
            .DistinctBy(c => c.Number)
            .OrderBy(c => c.Number)
            .ToList();

        // The regex itself must not silently stop matching — a doc reformat that broke it would
        // otherwise turn this whole test into a no-op that still reports green.
        Assert.True(chapters.Count > 50,
            $"only found {chapters.Count} chapters in FEATURES.md — the heading pattern probably changed.");

        var introFile = Path.Combine(repoRoot, IntroPath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(introFile), $"intro not found at {IntroPath}");
        var intro = File.ReadAllText(introFile);

        // Coverage is DECLARED per chapter (see the class remarks on why this is not a keyword
        // search). The marker lives in an HTML COMMENT so it is invisible to the reader — the intro
        // is customer-facing, and a bookkeeping token rendered into the prose would be a worse
        // problem than the one this solves.
        var covered = Regex.Matches(intro, @"covers:\s*([0-9]+(?:\s*,\s*[0-9]+)*)")
            .SelectMany(m => m.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries))
            .Select(int.Parse)
            .ToHashSet();

        var unaccounted = chapters
            .Where(c => c.Number >= FirstEnforcedChapter)
            .Where(c => !covered.Contains(c.Number) && !NotInIntro.ContainsKey(c.Number))
            .Select(c => $"§{c.Number} — {c.Title}")
            .ToList();

        Assert.True(unaccounted.Count == 0,
            "FEATURES.md chapters with no corresponding passage in the CEH introduction, and no stated "
            + "reason for leaving them out.\n\n"
            + "Write the passage and mark it with [covers:N], or — if it genuinely does not belong in a "
            + "customer-facing narrative — add it to IntroCurrencyTests.NotInIntro WITH the reason.\n\n"
            + string.Join("\n", unaccounted));
    }

    /// <summary>
    /// 🔒 The excuse list must not outlive the chapters it excuses. A stale entry silently re-opens
    /// the gap it was meant to close, because a renumbered chapter would inherit the exemption.
    /// </summary>
    [Fact]
    public void No_excuse_refers_to_a_chapter_that_no_longer_exists()
    {
        var features = File.ReadAllText(Path.Combine(FindRepoRoot(), "docs", "FEATURES.md"));
        var numbers = Regex.Matches(features, @"(?m)^##\s+(\d+)\.")
            .Select(m => int.Parse(m.Groups[1].Value))
            .ToHashSet();

        var dangling = NotInIntro.Keys.Where(n => !numbers.Contains(n)).ToList();
        Assert.True(dangling.Count == 0,
            $"NotInIntro excuses chapters that are not in FEATURES.md: {string.Join(", ", dangling)}");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "docs"))
                && File.Exists(Path.Combine(dir.FullName, "docs", "FEATURES.md")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException($"repo root not found from {AppContext.BaseDirectory}");
    }
}
