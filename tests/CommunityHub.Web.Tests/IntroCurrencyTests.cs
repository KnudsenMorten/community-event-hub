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
        [120] = "Coupon invoicing moving onto the organizer MENU. A NAVIGATION change: the page itself "
              + "did not move, and the intro describes what the ticket-agreement work does (the "
              + "\"Ticket agreements with customers\" row), not where its menu entry sits.",

        // 2026-09-15 — the 106+ range was re-audited when the intro was brought current. The intro
        // now has an ORGANIZER section, a media/partner section and a buyer-facing passage ("If your
        // company buys tickets for its people"), so "only organizers / only a purchaser see it" is no
        // longer a reason on its own, and most of what used to be excused here now has a passage.
        // What remains is excused for one of four honest reasons: a DEFECT FIX (the §88 decision —
        // a passage would describe the absence of a bug), COSMETIC presentation of a page whose
        // capability the intro already describes, NAVIGATION, or per-edition CONFIG that would make
        // the intro wrong next year.
        [132] = "The prepaid billing badge wording. A two-word chip on the coupon page; the intro "
              + "describes what ticket agreements do, not the labels on the page.",
        [133] = "The two-form coupon card. A LAYOUT correction to the coupon page — the capability "
              + "(buying tickets into a pool, which raises an invoice) is unchanged and described.",
        [134] = "Wording and form fixes on the coupon page and its mails ('customer' not 'partner', "
              + "the send button saying that it sends). Presentation of a capability the intro "
              + "already describes in the buyer-facing passage and the organizer row.",
        [135] = "Names instead of machine ids on invoices and customer mails. A defect fix: the intro "
              + "never promised an id, so there is nothing here to newly promise.",
        [136] = "The claim invitation's web address. A defect in one mail; a working link was always "
              + "the promise, and the buyer-facing passage already describes the invitation.",
        [138] = "A network fault reported as itself instead of as a customer's order. A defect fix in "
              + "an organizer ops mail — the absence of a misleading error, not a capability.",
        [139] = "The ERP reconcile actually performing the deactivation its mail claimed. A defect "
              + "fix: the intro describes the withdrawal (\"Sponsor records kept honest\"), and a "
              + "passage saying it now really happens would document the bug.",
        [145] = "A speaker alert that was never arriving now arrives. A defect fix — the recipient-"
              + "safety check was discarding one organizer alert. The intro already promises that "
              + "speaker to-dos reach the organizers; saying this one now does would describe the "
              + "absence of a defect (the §88 decision).",

        [150] = "The organizer filter rows lining up. Cosmetic alignment on two organizer grids; the "
              + "intro describes what those lists are for, not how their controls line up.",
        [151] = "Readable text and a tidier toolbar on the organizer lists. Same reasoning as §150 — "
              + "presentation, not capability.",
        [152] = "The participants export as a button instead of a link. The export already existed "
              + "and the intro names exports; this is how the control looks.",

        [153] = "Speaker/volunteer photos also saved under the person's name. A FILING convention "
              + "inside the organizers' own SharePoint folder — speakers and volunteers never see "
              + "it, their upload and their published picture are unchanged, and the intro does not "
              + "describe where files are stored.",

        [155] = "The hotel form accepting every real arrival night. The CAPABILITY (book your hotel "
              + "through the hub) is unchanged and already described — what changed is the "
              + "per-edition bookable RANGE, which is config that moves every year. A fixed "
              + "'5–12 February' in the intro would be wrong for the next edition.",

        [179] = "Sponsor contacts and company defaults linked from the sponsor hub too. Navigation: "
              + "two existing organizer pages gained a second entry point; the pages are unchanged.",
        [211] = "Telemetry card headings corrected. A label defect fix on an existing page the intro "
              + "already describes; no new capability.",
        [217] = "Company names on the sponsor welcome overview. A display fix on an organizer page; "
              + "no new capability.",
        [220] = "Shared-mailbox sponsor contacts reach the webshop. A defect fix in a background sync; "
              + "no new capability.",
        [224] = "The Get Started reminder speaks to the person it is sent to. A wording fix to an "
              + "existing reminder; no new capability.",
        [225] = "Sponsor upload alerts name the sponsor. A wording fix in an ORGANIZER-only alert mail; "
              + "no new capability.",
        [226] = "No hand-entry line for booth brochures. A defect fix in an ORGANIZER-only ops mail; "
              + "nothing a sponsor sees changes.",
        [227] = "Editing a session keeps its type. A defect fix in the ORGANIZER session grid; "
              + "no new capability.",
        [228] = "The disappeared-from-Sessionize alert stops once acted on. A defect fix in an "
              + "ORGANIZER-only ops mail; no new capability.",
        [229] = "One recipient list for the speaker/session to-do mails. ORGANIZER mail routing "
              + "(§1124/§1233); nothing a speaker, sponsor or attendee sees.",
        [230] = "Tagged people publish as real mentions. A defect fix in social publishing "
              + "(§1234); no new capability.",
        [231] = "One person, several sponsor companies. SPONSOR-ADMIN plumbing (§1235) for group "
              + "sponsors with divisions; the intro does not describe contact-role mechanics.",
        [232] = "The sponsor portal company switcher (§1236). A control inside the sponsor portal "
              + "for the few contacts serving several companies; not intro material.",
        [233] = "Sponsors are named, not numbered (§1237). A defect fix in ORGANIZER pages, ops "
              + "mails and draft post text; no new capability.",
        [234] = "ERP→webshop company creation (§1238). Back-office plumbing between the finance "
              + "system and the webshop; nothing a sponsor or attendee sees.",
        [235] = "One person reaching both of their hubs (§1255). From the person's side there is "
              + "nothing to learn or do: they sign in as before and the speaker area is simply "
              + "there. The change is a GATE moving from a role field to a capability, which is "
              + "invisible when it works. ⚠️ Honest note: the two-hats SITUATION may deserve an "
              + "intro passage one day (people do ask 'I organise and I speak — which login?'), "
              + "but that is a passage about the event, not about this fix.",
        [238] = "Session feedback on ordinary tablets (§1259). The intro describes the HUB a "
              + "speaker, sponsor, volunteer or attendee signs into; this is a stand in a corridor "
              + "with four faces, which nobody logs into and nobody needs told about — you walk "
              + "past and tap. The organizer-side setup (enrol, approve, link to a room) is an "
              + "operations task. ⚠️ If the intro ever gains a 'what you'll see at the venue' "
              + "section, this belongs in it.",
        [236] = "Catering counted per human (§1257/§1258). A COUNTING correction in organizer "
              + "pages and the venue spreadsheets — the numbers behind the order, not something "
              + "a speaker or attendee ever operates. The de-duplication link is an organizer "
              + "decision on the people list; an attendee's experience is identical either way.",
        [237] = "Fewer, better organizer emails (§1256). Four ops-alert defects: an alert that "
              + "ignored an approval, a throttle that reset on restart, stray markup in a "
              + "copy-paste block, and a change report for an invisible difference. Every one of "
              + "them lands in the ORGANIZER inbox; describing the absence of a bad email is not "
              + "a capability a reader would recognise.",
        [239] = "Sponsor announcements tag the company first (§1300). A refinement of the social "
              + "tagging the intro already describes for the sponsor's own people (#76): the company "
              + "is now looked up and tagged too, and a bare 'Tag:' line no longer publishes. The "
              + "sponsor does nothing new — their post simply tags them.",
        [240] = "Prepaid ticket invoices name the ticket type (§1301). A wording fix on the "
              + "partner's e-conomic invoice line; the intro does not describe invoice layout, and "
              + "a partner does nothing new.",
        [241] = "Booth tasks give the right furniture coupon code (§1302). A wrong code in "
              + "task text corrected; no new capability for the sponsor.",
        [249] = "Lower monitoring cost: quiet logging by default with an on-demand detail switch (§1317). An operations "
            + "change with no visible behaviour for organizers, sponsors or speakers.",
        [248] ="Public Sponsors page shows the real names and logos (§1315). A display fix on an existing public page; "
              + "nothing new for any role to learn.",
        [247] ="Long Zoho bios stored whole; a job's own failure no longer reported as a database outage (§1314). An "
              + "internal robustness fix; nothing new for any role to learn.",
        [246] ="SoMe organizer mails show the post as it will publish, picture attached (§1313). Internal organizer "
              + "notices made complete; nothing new for a speaker, sponsor or attendee to learn.",
        [245] = "Missing mandatory steps and overdue tasks marked red for every role (§1312). A small visibility cue on "
              + "existing Get Started and task pages, not a new capability the intro needs to explain.",
        // 2026-10-01 intro refresh (operator: "update the ceh intro"): §244, the sponsor shop, got its passage
        // ("Buying, reserving and signing") and left this list. §242/§243 stay excused on their own reasons.
        [243] = "SoMe posts never go out with a missing speaker photo (§1304). A publishing safeguard "
              + "for the company-page posts the intro already covers; nobody does anything new.",
        [242] = "Evaluation tablets managed by Intune (§1303). Organiser-side device setup for the "
              + "session-feedback kiosk; attendees tap the same faces, and the intro describes the hub people "
              + "sign into, not the corridor stand (the §238 decision).",
        [166] = "Volunteer photos filed under both name and id. A FILING convention inside the "
              + "organizers' own library — the §153 decision for the back-fill of it. The volunteer "
              + "uploads one picture either way and sees no difference.",
        [188] = "The post editor surviving a duplicated post. A server-error fix; describing the "
              + "absence of a crash is not a capability. Duplicating a post is already described.",
        [197] = "Event posts keeping their identity. A defect fix inside the campaign engine — the "
              + "planner was rebuilding the organizer's own dated posts every ten minutes and "
              + "re-issuing their ids. The posts themselves went out unchanged either way.",
        [203] = "The post list naming 2a/2b/2c. A column label on the post queue, matching the names "
              + "the settings page already used; the categories themselves are described.",
        [204] = "A clear button for a legacy session test flag that nothing sets any more. A data "
              + "clean-up control; the intro describes the two controls that DO keep a session out "
              + "of the campaign (test session, exclude from social media).",
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
