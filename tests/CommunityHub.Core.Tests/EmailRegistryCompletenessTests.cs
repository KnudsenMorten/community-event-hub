using CommunityHub.Core.Email;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §704.1b — <b>THE RULE, IN HIS WORDS:</b> <i>"basically everything targetting one of the roles
/// with an email has a subject + internal name and must be defined in settings, no exception. they
/// must be grouped"</i> (operator 2026-07-29).
///
/// <para>🔒 He also rejected the taxonomy I had been using — <i>"i dont understand the type
/// (naming)"</i>. There are not "templates" and "reminders" and "builder mails"; there are e-mails.
/// Where a body is composed is an internal detail. So these tests assert over the WHOLE registry
/// uniformly, with no per-kind exemptions, because a per-kind exemption is exactly the thing he
/// said must not exist.</para>
/// </summary>
public sealed class EmailRegistryCompletenessTests
{
    /// <summary>
    /// Every registry entry has an internal name (its key), a human title, and resolves to a
    /// group — the three things a Settings row needs to be operable.
    /// </summary>
    [Fact]
    public void Every_mail_has_an_internal_name_a_title_and_a_group()
    {
        foreach (var key in EmailTemplateCatalog.Map.Keys)
        {
            Assert.False(string.IsNullOrWhiteSpace(key));
            Assert.False(string.IsNullOrWhiteSpace(EmailTemplateCatalog.TitleFor(key)),
                $"'{key}' has no title.");
            // Grouping must not throw and must land somewhere real.
            var audience = EmailTemplateCatalog.AudienceFor(key);
            Assert.False(string.IsNullOrWhiteSpace(EmailTemplateCatalog.AudienceLabel(audience)),
                $"'{key}' groups under an audience with no label.");
        }
    }

    /// <summary>
    /// §563 — every mail says WHO gets it, in words. A row carrying a key and a ring but no
    /// recipient forces the audience to be inferred from the key, which is what made this page
    /// unreadable.
    /// </summary>
    [Fact]
    public void Every_mail_says_who_receives_it()
    {
        var generic = EmailTemplateCatalog.RecipientHint("a-key-that-does-not-exist");

        foreach (var key in EmailTemplateCatalog.Map.Keys)
        {
            var hint = EmailTemplateCatalog.RecipientHint(key);
            Assert.False(string.IsNullOrWhiteSpace(hint), $"'{key}' has no recipient hint.");
            Assert.NotEqual(generic, hint);
        }
    }

    /// <summary>
    /// 🔒 §704.1b — a mail composed IN CODE still has to show a SUBJECT. It has no template file to
    /// read a <c>Subject:</c> line from, so it must declare one in the catalog; otherwise it lists
    /// with a friendly title only, which is the §695.1 ambiguity ("all emails must be with subject +
    /// internal name so i easily can separate them appart").
    /// </summary>
    [Fact]
    public void A_mail_with_no_template_file_declares_its_subject()
    {
        foreach (var kv in EmailTemplateCatalog.InlineSubjects)
        {
            Assert.True(EmailTemplateCatalog.Map.ContainsKey(kv.Key),
                $"'{kv.Key}' declares a subject but is not in the registry, so it has no Settings row.");
            Assert.False(string.IsNullOrWhiteSpace(kv.Value), $"'{kv.Key}' declares a blank subject.");
        }
    }

    /// <summary>
    /// §704.1c — the two organizer mails the 2026-07-29 audit found were reaching a role with no
    /// internal name and no Settings row at all. Pinned so they cannot silently drop out again.
    /// </summary>
    [Theory]
    [InlineData("some-speaker-prealert")]
    [InlineData("some-published")]
    public void The_SoMe_organizer_mails_found_by_the_audit_are_in_the_registry(string key)
    {
        Assert.True(EmailTemplateCatalog.Map.ContainsKey(key));
        Assert.Equal(EmailAudience.Organizer, EmailTemplateCatalog.AudienceFor(key));
        // They are ops alerts to a designated organizer: exempt, and therefore no ring is shown.
        Assert.True(EmailTemplateCatalog.IsRingExempt(key));
        // ...but they still carry a real subject, per the rule.
        Assert.False(string.IsNullOrWhiteSpace(EmailTemplateCatalog.InlineSubjects[key]));
    }

    /// <summary>
    /// 🔒 §704.1c / §326bx — a ring-exempt mail must state WHY no ring applies. This is the defect
    /// that cost the operator confidence in this page: a control that appears to govern an audience
    /// while governing nothing. The honest alternative is words, not a disabled dropdown.
    /// </summary>
    [Fact]
    public void Every_ring_exempt_mail_explains_itself()
    {
        var generic = EmailTemplateCatalog.RingExemptReason("not-a-real-key");

        foreach (var key in EmailTemplateCatalog.RingExemptTemplates)
        {
            Assert.True(EmailTemplateCatalog.Map.ContainsKey(key),
                $"'{key}' is marked ring-exempt but is not in the registry.");

            var reason = EmailTemplateCatalog.RingExemptReason(key);
            Assert.False(string.IsNullOrWhiteSpace(reason));
            Assert.NotEqual(generic, reason);
        }
    }

    /// <summary>
    /// 🔒 §705.14 — THE MAIL KEYS A SEND SITE PASSES MUST EXIST IN THE REGISTRY.
    /// </summary>
    /// <remarks>
    /// Since §705.2 a registered mail with no ring FAILS CLOSED, and a send site passing a key that is
    /// NOT in the registry falls through to its feature ring instead — so a typo in either direction is
    /// silent: the mail either reaches nobody or quietly keeps using a feature ring the model has
    /// removed. Neither shows up as an error anywhere.
    ///
    /// <para>These three compose their bodies in code, so there is no template FILE whose absence would
    /// give the mistake away — which is exactly why they need pinning here.</para>
    /// </remarks>
    [Theory]
    [InlineData("masterclass-question-posted")]
    [InlineData("masterclass-instructions-updated")]
    [InlineData("task-allocation-committed")]
    public void Mail_keys_passed_by_code_composed_send_sites_are_registered(string key)
    {
        Assert.True(EmailTemplateCatalog.Map.ContainsKey(key),
            $"'{key}' is passed as EmailContext.TemplateName by a send site but is not in the registry. "
            + "It would have no Settings row and no ring of its own.");

        // Each must also carry a subject, since none has a template file to read one from.
        Assert.True(EmailTemplateCatalog.InlineSubjects.ContainsKey(key),
            $"'{key}' composes its body in code, so it must declare a subject in InlineSubjects.");
    }

    /// <summary>
    /// The constants the send sites actually use — asserted against the strings above so a rename on
    /// either side cannot drift apart silently.
    /// </summary>
    [Fact]
    public void The_send_site_constants_match_the_registered_keys()
    {
        Assert.Equal("masterclass-question-posted",
            CommunityHub.Core.Reminders.MasterClassNotificationService.QandAMailKey);
        Assert.Equal("masterclass-instructions-updated",
            CommunityHub.Core.Reminders.MasterClassNotificationService.InstructionsMailKey);
        Assert.Equal("task-allocation-committed", CommitNotificationService.MailKey);
    }

    /// <summary>
    /// The counterpart guard: a mail that is NOT exempt must not carry an exemption reason, so the
    /// two states can never both be true for one row.
    /// </summary>
    [Fact]
    public void A_ring_gated_mail_is_not_also_marked_exempt() =>
        Assert.DoesNotContain(
            EmailTemplateCatalog.Map.Keys.Where(k => !EmailTemplateCatalog.IsRingExempt(k)),
            EmailTemplateCatalog.RingExemptTemplates.Contains);

    /// <summary>
    /// 🔒 §707.3 FINDING 1 — THE EXEMPT LIST MUST MATCH THE SEND SITES, EXACTLY.
    /// </summary>
    /// <remarks>
    /// The 2026-07-30 re-verification found <c>masterclass-cancelled</c> sending with
    /// <c>RingExempt: true</c> since §346 while being ABSENT from the exempt list. PROD therefore
    /// carried a Ring 2 row for it and the Settings page offered a ring that could never apply —
    /// the §326bx defect the exempt list itself exists to prevent, hiding in that list's blind spot.
    ///
    /// <para>🔑 <b>Neither of the existing guards could catch it.</b> They check the list against the
    /// registry (both directions), and this key was consistent with the registry — it was the SEND
    /// SITE that disagreed, and no test looked there. So this pins the whole set as a literal: adding
    /// or removing an exemption now forces a deliberate edit here, which is the prompt to go and check
    /// the corresponding <c>EmailContext(..., RingExempt: ...)</c> at the send.</para>
    ///
    /// <para>⚠️ When this test fails, do NOT just update the expected set — first read the send site
    /// and decide which of the two is wrong. A mail listed here shows "always sent" on the Settings
    /// page; a mail missing from here is offered a ring control instead.</para>
    /// </remarks>
    [Fact]
    public void The_exempt_list_matches_the_send_sites_that_set_RingExempt()
    {
        var expected = new[]
        {
            "pin-signin",              // PinLoginService
            "calendar-invite",         // CalendarInviteEmailService
            "calendar-dinner",         // CalendarInviteEmailService (dinner mailKey)
            "hotel-calendar-selfsend", // CalendarInviteEmailService (hotel self-send mailKey)
            // §779 — the send site was READ before this line was added, per the ⚠ above:
            // SignalFormService.SendLinksEmailAsync passes `RingExempt: true`, so the registry moved
            // to match the code and not the other way round. Same class as the two calendar
            // self-sends: the participant pressed the button and is waiting for the mail — on a
            // phone they are about to pick up, which is the whole reason the mail exists.
            "signal-join-links",       // SignalFormService ("email me the join links")
            "some-speaker-prealert",   // SoMeDispatchService
            "some-published",          // SoMeDispatchService
            // §707.27 F — registered read-only for visibility on 2026-07-30. Each send site was READ
            // before this line was added (the ⚠ above): all three already passed `RingExempt: true`,
            // so the registry moved to match the code, never the other way round. None reaches a
            // participant — the recipient is a mailbox, so there is no audience for a ring to narrow.
            "travel-reimbursement-erp", // TravelFormService (ERP inbox)
            "feedback-intake",          // FeedbackIntakeService (organizer ops inbox)
            "engine-alert",             // EngineAlertSender (operator-confirmed: no ring)
            // 🔒 §707.20 — `masterclass-cancelled` is deliberately NOT here any more. §346 made it
            // exempt under the §326by participant-clicked rule; the operator overruled that on
            // 2026-07-30 (*"it should also be ring-gated … it is a similar mail as any other mail"*),
            // so the send site dropped `RingExempt` and took a normal (mail × role) ring.
        };

        Assert.Equal(
            expected.OrderBy(k => k, StringComparer.Ordinal),
            EmailTemplateCatalog.RingExemptTemplates.OrderBy(k => k, StringComparer.Ordinal));
    }

    /// <summary>
    /// §707.27 F — the last three code-sent mails, registered read-only for visibility (the §704.1c
    /// precedent). Pinned so they cannot silently drop out of the registry again.
    /// </summary>
    [Theory]
    [InlineData("travel-reimbursement-erp")]
    [InlineData("feedback-intake")]
    [InlineData("engine-alert")]
    public void The_internal_ops_mails_are_registered_read_only(string key)
    {
        Assert.True(EmailTemplateCatalog.Map.ContainsKey(key));
        Assert.Equal(EmailAudience.Organizer, EmailTemplateCatalog.AudienceFor(key));
        // Exempt at the send site ⇒ no ring control, and a stated reason instead.
        Assert.True(EmailTemplateCatalog.IsRingExempt(key));
        Assert.False(string.IsNullOrWhiteSpace(EmailTemplateCatalog.InlineSubjects[key]));
        // 🔒 Ring-exempt ⇒ NO recipient roles, so the page offers no per-role ring either. A mailbox
        // is not a role, and a control over a mailbox would govern nothing (§326bx).
        Assert.Empty(EmailTemplateCatalog.RecipientRolesFor(key));
    }

    // ---------- §707.27 B — a shared mail is findable under EVERY role it reaches ----------

    /// <summary>
    /// §707.27 B (operator 2026-07-30: *"why does the reminders mails for sponsors not show under
    /// sponsors"*) — a shared mail lists under every role in <c>RecipientRolesFor</c>, not only under
    /// its one filing home.
    /// </summary>
    [Fact]
    public void A_shared_mail_lists_under_every_role_it_reaches()
    {
        var listed = EmailTemplateCatalog.ListingAudiencesFor("getstarted-digest");

        Assert.Contains(EmailAudience.Speaker, listed);
        Assert.Contains(EmailAudience.Sponsor, listed);
        Assert.Contains(EmailAudience.Attendee, listed);
    }

    /// <summary>
    /// The all-roles mail the complaint started from: filed under "Any role", it reaches all seven —
    /// so it must be findable under each of them.
    /// </summary>
    [Fact]
    public void The_all_roles_task_reminder_lists_under_every_role()
    {
        var listed = EmailTemplateCatalog.ListingAudiencesFor("task-deadline-reminder");

        foreach (var role in EmailTemplateCatalog.RecipientRolesFor("task-deadline-reminder"))
        {
            Assert.Contains(EmailTemplateCatalog.AudienceForRole(role), listed);
        }
    }

    /// <summary>
    /// 🔒 THE FILING HOME IS FIRST, AND ALWAYS PRESENT. The Settings page renders the all-roles ring
    /// control on the FIRST (primary) appearance only; every later one carries just that role's ring.
    /// If the home could be absent or in second place, either two sections would offer the same
    /// all-roles control — two dropdowns over one stored value, the §707.27 B trap — or the control
    /// would vanish from the page entirely.
    /// </summary>
    [Theory]
    [InlineData("getstarted-digest")]
    [InlineData("task-deadline-reminder")]
    [InlineData("task-allocation-committed")]
    [InlineData("welcome-speaker")]
    public void The_filing_home_is_always_the_first_listing(string key)
    {
        var listed = EmailTemplateCatalog.ListingAudiencesFor(key);

        Assert.NotEmpty(listed);
        Assert.Equal(EmailTemplateCatalog.AudienceFor(key), listed[0]);
    }

    /// <summary>A mail that reaches ONE role appears exactly once — cross-listing adds no noise.</summary>
    [Fact]
    public void A_single_role_mail_is_listed_exactly_once()
    {
        Assert.Single(EmailTemplateCatalog.ListingAudiencesFor("welcome-sponsor"));
        Assert.Single(EmailTemplateCatalog.ListingAudiencesFor("sponsor-leads-digest"));
    }

    /// <summary>
    /// No mail is listed twice in the SAME section — a duplicate row would mean two controls over one
    /// value, which is the defect this feature must not introduce while fixing findability.
    /// </summary>
    [Fact]
    public void No_mail_is_listed_twice_in_the_same_section()
    {
        foreach (var key in EmailTemplateCatalog.Map.Keys)
        {
            var listed = EmailTemplateCatalog.ListingAudiencesFor(key);
            Assert.Equal(listed.Count, listed.Distinct().Count());
        }
    }

    /// <summary>
    /// A RING-EXEMPT mail is never cross-listed: it reaches no role, so there is no role section it
    /// belongs in and no per-role ring to show there.
    /// </summary>
    [Fact]
    public void A_ring_exempt_mail_stays_in_one_place()
    {
        foreach (var key in EmailTemplateCatalog.RingExemptTemplates)
        {
            Assert.Single(EmailTemplateCatalog.ListingAudiencesFor(key));
        }
    }
}
