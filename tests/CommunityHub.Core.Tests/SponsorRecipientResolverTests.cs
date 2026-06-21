using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations.Erp;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Tests for the universal sponsor-email audience rule (REQUIREMENTS §7c): every
/// sponsor email goes to ALL of a company's EVENT-COORDINATOR contacts;
/// signer-only contacts never receive it; a both-roles contact STILL receives it;
/// several coordinators are all returned. Covers the pure
/// <see cref="SponsorRecipientResolver.Select"/> selection, the DB-scoped
/// <see cref="SponsorRecipientResolver.ResolveAsync"/>, and the welcome
/// reset-flag path that re-enables a resend.
/// </summary>
public class SponsorRecipientResolverTests
{
    private static Participant Sponsor(
        string email, bool coordinator, bool signer,
        bool active = true, string company = "test-2linkit", string name = "Sample Person") =>
        new()
        {
            Email = email,
            FullName = name,
            Role = ParticipantRole.Sponsor,
            SponsorCompanyId = company,
            IsEventCoordinator = coordinator,
            IsSigner = signer,
            IsActive = active,
        };

    // ----------------------------------------------------------------------
    // Pure selection (Select)
    // ----------------------------------------------------------------------

    [Fact]
    public void Coordinator_only_is_included()
    {
        var picked = SponsorRecipientResolver.Select(new[]
        {
            Sponsor("coordinator@2linkit.net", coordinator: true, signer: false),
        });

        var r = Assert.Single(picked);
        Assert.Equal("coordinator@2linkit.net", r.Email);
    }

    [Fact]
    public void Signer_only_is_excluded()
    {
        var picked = SponsorRecipientResolver.Select(new[]
        {
            Sponsor("signer@2linkit.net", coordinator: false, signer: true),
        });

        Assert.Empty(picked);
    }

    [Fact]
    public void Both_roles_contact_is_included_because_they_are_a_coordinator()
    {
        var picked = SponsorRecipientResolver.Select(new[]
        {
            Sponsor("both@2linkit.net", coordinator: true, signer: true),
        });

        var r = Assert.Single(picked);
        Assert.Equal("both@2linkit.net", r.Email);
    }

    [Fact]
    public void Multiple_coordinators_are_all_returned_signers_dropped()
    {
        var picked = SponsorRecipientResolver.Select(new[]
        {
            Sponsor("coord1@2linkit.net", coordinator: true,  signer: false),
            Sponsor("coord2@2linkit.net", coordinator: true,  signer: true),  // both -> kept
            Sponsor("signer@2linkit.net", coordinator: false, signer: true),  // signer-only -> dropped
            Sponsor("nobody@2linkit.net", coordinator: false, signer: false), // neither -> dropped
        });

        Assert.Equal(
            new[] { "coord1@2linkit.net", "coord2@2linkit.net" },
            picked.Select(p => p.Email).ToArray());
    }

    [Fact]
    public void Inactive_coordinator_is_excluded()
    {
        var picked = SponsorRecipientResolver.Select(new[]
        {
            Sponsor("inactive@2linkit.net", coordinator: true, signer: false, active: false),
        });

        Assert.Empty(picked);
    }

    [Fact]
    public void Duplicate_emails_are_deduplicated_case_insensitively()
    {
        var picked = SponsorRecipientResolver.Select(new[]
        {
            Sponsor("Coord@2linkit.net", coordinator: true, signer: false),
            Sponsor("coord@2linkit.net", coordinator: true, signer: false),
        });

        Assert.Single(picked);
    }

    // ----------------------------------------------------------------------
    // DB-scoped resolve
    // ----------------------------------------------------------------------

    [Fact]
    public async Task ResolveAsync_scopes_to_company_and_event_and_returns_coordinators_only()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await SeedEventAsync(db);

        var rows = new[]
        {
            Sponsor("coord@2linkit.net",  coordinator: true,  signer: false, company: "test-2linkit"),
            Sponsor("both@2linkit.net",   coordinator: true,  signer: true,  company: "test-2linkit"),
            Sponsor("signer@2linkit.net", coordinator: false, signer: true,  company: "test-2linkit"),
            // Different company -- must NOT appear.
            Sponsor("other@acme.test",    coordinator: true,  signer: false, company: "company-99"),
        };
        foreach (var p in rows) p.EventId = eventId;
        db.Participants.AddRange(rows);
        await db.SaveChangesAsync();

        var resolver = new SponsorRecipientResolver(db);
        var recipients = await resolver.ResolveAsync(eventId, "test-2linkit");

        Assert.Equal(
            new[] { "both@2linkit.net", "coord@2linkit.net" },
            recipients.Select(r => r.Email).OrderBy(e => e).ToArray());
    }

    [Fact]
    public async Task ResolveAsync_returns_empty_when_company_has_no_coordinators()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await SeedEventAsync(db);

        var signer = Sponsor("signer@2linkit.net", coordinator: false, signer: true);
        signer.EventId = eventId;
        db.Participants.Add(signer);
        await db.SaveChangesAsync();

        var resolver = new SponsorRecipientResolver(db);
        var recipients = await resolver.ResolveAsync(eventId, "test-2linkit");

        Assert.Empty(recipients);
    }

    // ----------------------------------------------------------------------
    // e-conomic ERP Role-2 (event coordinator) resolution + fail-soft fallback
    // ----------------------------------------------------------------------

    /// <summary>
    /// A fake read-only ERP coordinator source: hands back a fixed Role-2 email
    /// set (or null = "unavailable, fall back"), exactly like the real
    /// e-conomic-backed <see cref="SponsorErpCoordinatorSource"/>.
    /// </summary>
    private sealed class FakeErpCoordinatorSource : ISponsorErpCoordinatorSource
    {
        private readonly IReadOnlyCollection<string>? _emails;
        public FakeErpCoordinatorSource(IReadOnlyCollection<string>? emails, bool enabled = true)
        {
            _emails = emails;
            IsEnabled = enabled;
        }
        public bool IsEnabled { get; }
        public Task<IReadOnlyCollection<string>?> GetCoordinatorEmailsAsync(
            string sponsorCompanyId, CancellationToken ct = default) =>
            Task.FromResult(_emails);
    }

    [Theory]
    [InlineData("Type:1;Role:1,2", new[] { 1, 2 })]
    [InlineData("Type:2;Role:2", new[] { 2 })]
    [InlineData("Type:1;Role:1", new[] { 1 })]
    [InlineData("Type:1, Role: 1, 2 ", new[] { 1, 2 })]
    [InlineData("role:2", new[] { 2 })]            // case-insensitive
    [InlineData("just a normal note", new int[0])] // no Role: segment
    [InlineData("", new int[0])]
    public void ParseRoles_extracts_role_ids_from_contact_notes(string notes, int[] expected)
    {
        var roles = EconomicRoleClient.ParseRoles(notes);
        Assert.Equal(expected.OrderBy(x => x), roles.OrderBy(x => x));
    }

    [Fact]
    public void ParseRoles_handles_null_notes()
    {
        Assert.Empty(EconomicRoleClient.ParseRoles(null));
    }

    [Fact]
    public async Task ResolveAsync_uses_erp_role2_set_returning_multiple_coordinators()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await SeedEventAsync(db);

        // Hub flags are all FALSE here -- the audience must come from the ERP
        // Role-2 set, proving ERP is the primary source. Two Role-2 contacts.
        var rows = new[]
        {
            Sponsor("coord1@2linkit.net", coordinator: false, signer: false),
            Sponsor("coord2@2linkit.net", coordinator: false, signer: false),
            Sponsor("signer@2linkit.net", coordinator: false, signer: false),
        };
        foreach (var p in rows) p.EventId = eventId;
        db.Participants.AddRange(rows);
        await db.SaveChangesAsync();

        // e-conomic says coord1 + coord2 are Role 2; signer is not in the set.
        var erp = new FakeErpCoordinatorSource(new[] { "coord1@2linkit.net", "coord2@2linkit.net" });
        var resolver = new SponsorRecipientResolver(db, erp);

        var recipients = await resolver.ResolveAsync(eventId, "test-2linkit");

        Assert.Equal(
            new[] { "coord1@2linkit.net", "coord2@2linkit.net" },
            recipients.Select(r => r.Email).OrderBy(e => e).ToArray());
    }

    [Fact]
    public async Task ResolveAsync_erp_signer_only_excluded_both_roles_included()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await SeedEventAsync(db);

        var rows = new[]
        {
            Sponsor("coord@2linkit.net",  coordinator: false, signer: false),
            Sponsor("both@2linkit.net",   coordinator: false, signer: false),
            Sponsor("signer@2linkit.net", coordinator: false, signer: false),
        };
        foreach (var p in rows) p.EventId = eventId;
        db.Participants.AddRange(rows);
        await db.SaveChangesAsync();

        // ERP Role-2 set carries the coordinator + the both-roles contact, but
        // NOT the signer-only contact (Role 1 only is filtered out upstream).
        var erp = new FakeErpCoordinatorSource(new[] { "coord@2linkit.net", "both@2linkit.net" });
        var resolver = new SponsorRecipientResolver(db, erp);

        var recipients = await resolver.ResolveAsync(eventId, "test-2linkit");

        Assert.Equal(
            new[] { "both@2linkit.net", "coord@2linkit.net" },
            recipients.Select(r => r.Email).OrderBy(e => e).ToArray());
        Assert.DoesNotContain("signer@2linkit.net", recipients.Select(r => r.Email));
    }

    [Fact]
    public async Task ResolveAsync_honors_manual_flag_as_override_on_top_of_erp()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await SeedEventAsync(db);

        // ERP knows coord1; an organizer also manually flagged coord2 in the hub.
        // Both must be returned (manual flag is an additive override).
        var rows = new[]
        {
            Sponsor("coord1@2linkit.net", coordinator: false, signer: false),
            Sponsor("coord2@2linkit.net", coordinator: true,  signer: false), // manual override
        };
        foreach (var p in rows) p.EventId = eventId;
        db.Participants.AddRange(rows);
        await db.SaveChangesAsync();

        var erp = new FakeErpCoordinatorSource(new[] { "coord1@2linkit.net" });
        var resolver = new SponsorRecipientResolver(db, erp);

        var recipients = await resolver.ResolveAsync(eventId, "test-2linkit");

        Assert.Equal(
            new[] { "coord1@2linkit.net", "coord2@2linkit.net" },
            recipients.Select(r => r.Email).OrderBy(e => e).ToArray());
    }

    [Fact]
    public async Task ResolveAsync_falls_back_to_cm_default_flags_when_erp_returns_null()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await SeedEventAsync(db);

        // Manual flag (seeded from the CM single default coordinator) is the only
        // signal. ERP is unavailable (returns null) -> fall back to the flag.
        var rows = new[]
        {
            Sponsor("cmdefault@2linkit.net", coordinator: true,  signer: false),
            Sponsor("signer@2linkit.net",    coordinator: false, signer: true),
        };
        foreach (var p in rows) p.EventId = eventId;
        db.Participants.AddRange(rows);
        await db.SaveChangesAsync();

        // Enabled source that returns null = "unavailable / no ERP data".
        var erp = new FakeErpCoordinatorSource(emails: null, enabled: true);
        var resolver = new SponsorRecipientResolver(db, erp);

        var recipients = await resolver.ResolveAsync(eventId, "test-2linkit");

        var r = Assert.Single(recipients);
        Assert.Equal("cmdefault@2linkit.net", r.Email);
    }

    [Fact]
    public async Task ResolveAsync_falls_back_to_flags_when_erp_source_disabled()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await SeedEventAsync(db);

        var coord = Sponsor("cmdefault@2linkit.net", coordinator: true, signer: false);
        coord.EventId = eventId;
        db.Participants.Add(coord);
        await db.SaveChangesAsync();

        // Disabled source must never be consulted; flag-only fallback applies.
        var erp = new FakeErpCoordinatorSource(new[] { "should-not-be-used@x.test" }, enabled: false);
        var resolver = new SponsorRecipientResolver(db, erp);

        var recipients = await resolver.ResolveAsync(eventId, "test-2linkit");

        var r = Assert.Single(recipients);
        Assert.Equal("cmdefault@2linkit.net", r.Email);
    }

    // ----------------------------------------------------------------------
    // Welcome reset-flag path (re-enables a resend)
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Reset_clears_welcome_ledger_for_coordinators_so_resend_fires()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await SeedEventAsync(db);

        var coord = Sponsor("coord@2linkit.net", coordinator: true, signer: false);
        coord.EventId = eventId;
        var signer = Sponsor("signer@2linkit.net", coordinator: false, signer: true);
        signer.EventId = eventId;
        db.Participants.AddRange(coord, signer);

        // Welcome already recorded for BOTH (the coordinator and a signer).
        db.SentReminders.AddRange(
            new SentReminder { EventId = eventId, RecipientEmail = "coord@2linkit.net", ReminderType = "welcome", OccasionKey = "welcome:1" },
            new SentReminder { EventId = eventId, RecipientEmail = "signer@2linkit.net", ReminderType = "welcome", OccasionKey = "welcome:2" },
            // An unrelated reminder type must NOT be touched.
            new SentReminder { EventId = eventId, RecipientEmail = "coord@2linkit.net", ReminderType = "task-deadline", OccasionKey = "task:5:m3" });
        await db.SaveChangesAsync();

        var svc = NewWelcomeService(db, out var sender);

        // Before reset: the coordinator's welcome is idempotently suppressed.
        var before = await svc.SendForCompanyAsync(eventId, "test-2linkit");
        Assert.Equal(1, before.CoordinatorsResolved);
        Assert.Equal(0, before.Sent);          // already welcomed -> skipped
        Assert.Equal(1, before.Skipped);
        Assert.Empty(sender.Sent);

        // Reset clears ONLY the coordinator's welcome row (signer's welcome +
        // the task-deadline row are left alone).
        var deleted = await svc.ResetForCompanyAsync(eventId, "test-2linkit");
        Assert.Equal(1, deleted);
        Assert.True(await db.SentReminders.AnyAsync(s => s.RecipientEmail == "signer@2linkit.net" && s.ReminderType == "welcome"));
        Assert.True(await db.SentReminders.AnyAsync(s => s.ReminderType == "task-deadline"));
        Assert.False(await db.SentReminders.AnyAsync(s => s.RecipientEmail == "coord@2linkit.net" && s.ReminderType == "welcome"));

        // After reset: the resend actually fires to the coordinator only.
        var after = await svc.SendForCompanyAsync(eventId, "test-2linkit");
        Assert.Equal(1, after.Sent);
        var sent = Assert.Single(sender.Sent);
        Assert.Equal("coord@2linkit.net", sent.To);
    }

    // ----------------------------------------------------------------------
    // Sponsor task-deadline reminder fans out to coordinators (not the assignee)
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Sponsor_task_reminder_goes_to_coordinators_not_a_signer_only_assignee()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await SeedEventAsync(db);

        // The task is assigned to a SIGNER-ONLY contact, but the company also has
        // two coordinators. The reminder must go to the coordinators, never the
        // signer-only assignee.
        var signerAssignee = Sponsor("signer@2linkit.net", coordinator: false, signer: true);
        signerAssignee.EventId = eventId;
        var coord1 = Sponsor("coord1@2linkit.net", coordinator: true, signer: false);
        coord1.EventId = eventId;
        var coord2 = Sponsor("coord2@2linkit.net", coordinator: true, signer: true); // both -> kept
        coord2.EventId = eventId;
        db.Participants.AddRange(signerAssignee, coord1, coord2);
        await db.SaveChangesAsync();

        // A sponsor task due within the reminder window, assigned to the signer.
        var due = DateOnly.FromDateTime(ScenarioFixture.Clock.GetUtcNow().UtcDateTime).AddDays(2);
        db.Tasks.Add(new ParticipantTask
        {
            EventId = eventId,
            AssignedParticipantId = signerAssignee.Id,
            SponsorCompanyId = "test-2linkit",
            Title = "Upload your company logo",
            DueDate = due,
            State = TaskState.Open,
        });
        await db.SaveChangesAsync();

        var templates = new EmailTemplateProvider(
            Options.Create(new EmailTemplateOptions { TemplateDirectory = RepoPaths.EmailTemplates() }));
        var builder = new TaskReminderBuilder(
            db, templates, ScenarioFixture.Clock, new SponsorRecipientResolver(db));

        var messages = await builder.BuildDueAsync(eventId);

        var recipients = messages.Select(m => m.EffectiveRecipient).OrderBy(e => e).ToArray();
        Assert.Equal(new[] { "coord1@2linkit.net", "coord2@2linkit.net" }, recipients);
        Assert.DoesNotContain("signer@2linkit.net", recipients);
        // Each coordinator deduped independently (occasion key carries the email).
        Assert.Equal(2, messages.Select(m => m.OccasionKey).Distinct().Count());
    }

    // ----------------------------------------------------------------------
    // Unique-identifier contact link — the resolver carries CmUserId (the id),
    // and correlation keys on id/email, never on name (REQUIREMENTS §7c).
    // ----------------------------------------------------------------------

    [Fact]
    public void Select_carries_CmUserId_id_link_on_each_recipient()
    {
        var coord = Sponsor("coord@2linkit.net", coordinator: true, signer: false);
        coord.CmUserId = 68;

        var r = Assert.Single(SponsorRecipipientOrSelect(coord));
        Assert.Equal(68, r.CmUserId);
        Assert.Equal("coord@2linkit.net", r.Email);
    }

    private static IReadOnlyList<SponsorRecipient> SponsorRecipipientOrSelect(Participant p) =>
        SponsorRecipientResolver.Select(new[] { p });

    [Fact]
    public async Task ResolveAsync_recipients_carry_the_CmUserId_from_the_participant_row()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await SeedEventAsync(db);

        var coord = Sponsor("coord@2linkit.net", coordinator: true, signer: false);
        coord.EventId = eventId;
        coord.CmUserId = 68;
        db.Participants.Add(coord);
        await db.SaveChangesAsync();

        var resolver = new SponsorRecipientResolver(db);
        var recipients = await resolver.ResolveAsync(eventId, "test-2linkit");

        var r = Assert.Single(recipients);
        Assert.Equal(68, r.CmUserId);
    }

    [Fact]
    public async Task ResolveAsync_correlates_by_id_email_not_by_name()
    {
        using var db = ScenarioFixture.NewDb();
        var eventId = await SeedEventAsync(db);

        // Two contacts share the SAME full name; only one is a coordinator. The
        // resolver must pick by the per-contact flag (id-scoped row), never collapse
        // or match the pair by their identical name.
        var coord = Sponsor("coord@2linkit.net", coordinator: true, signer: false, name: "Same Name");
        coord.EventId = eventId;
        coord.CmUserId = 68;
        var signerTwin = Sponsor("signer@2linkit.net", coordinator: false, signer: true, name: "Same Name");
        signerTwin.EventId = eventId;
        signerTwin.CmUserId = 77;
        db.Participants.AddRange(coord, signerTwin);
        await db.SaveChangesAsync();

        var resolver = new SponsorRecipientResolver(db);
        var recipients = await resolver.ResolveAsync(eventId, "test-2linkit");

        // Only the coordinator row (by its own flag), identified by its CmUserId.
        var r = Assert.Single(recipients);
        Assert.Equal("coord@2linkit.net", r.Email);
        Assert.Equal(68, r.CmUserId);
    }

    // ----------------------------------------------------------------------

    private static SponsorWelcomeEmailService NewWelcomeService(
        CommunityHubDbContext db, out CapturingEmailSender sender)
    {
        sender = new CapturingEmailSender();
        var templates = new EmailTemplateProvider(
            Options.Create(new EmailTemplateOptions
            {
                TemplateDirectory = RepoPaths.EmailTemplates(),
            }));
        var welcome = new WelcomeEmailService(db, templates, sender, ScenarioFixture.Clock);
        return new SponsorWelcomeEmailService(db, new SponsorRecipientResolver(db), welcome);
    }

    private static async Task<int> SeedEventAsync(CommunityHubDbContext db)
    {
        var ev = new Event
        {
            CommunityName = "Experts Live Denmark",
            DisplayName = "Experts Live Denmark 2027",
            Code = "ELDK27",
            IsActive = true,
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        return ev.Id;
    }
}
