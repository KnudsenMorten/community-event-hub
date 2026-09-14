using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Erp;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Sponsors;
using CommunityHub.Core.Tests.Scenario;
using CommunityHub.Forms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1081 — THE SPONSOR COMPANY STEP IS AN <b>AND</b>, THE DENOMINATOR ONLY COUNTS WHAT WE CAN
/// EVALUATE, AND THE DIGEST SAYS WHAT IS ALREADY DONE.
/// </summary>
/// <remarks>
/// <para>🔴 <b>The incident.</b> A sponsor's event coordinator was mailed *"a few Get Started steps
/// are still waiting for you"* while a colleague had already uploaded both logos. Both parties were
/// right: steps HAD been completed, and others genuinely were still open. The mail simply never said
/// which was which — so it read as the hub not knowing what the team had done.</para>
///
/// <para>🔑 <b>Operator 2026-08-13:</b> <i>"we need all fields like company description, short
/// description, some branding description to be filled out … so the OR here is wrong"</i> and
/// <i>"website url comes from webshop … that one is authoritative for that field"</i>.</para>
///
/// <para>FAKE names, companies and addresses only.</para>
/// </remarks>
public sealed class SponsorCompanyContentAndDigestTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LongAgo = Now.AddDays(-60);
    private const string CompanyId = "4242";

    private static EmailTemplateProvider Templates() =>
        new(Options.Create(new EmailTemplateOptions { TemplateDirectory = RepoPaths.EmailTemplates() }));

    /// <summary>e-conomic unreachable — which is exactly the state that makes the contacts step
    /// undeterminable, so these fixtures exercise the null-step case for free.</summary>
    private sealed class NoEconomicClient : IEconomicContactAdminClient
    {
        public bool CanWrite => false;
        public Task<IReadOnlyList<EconomicCustomerRow>> ListCustomersAsync(
            string? search, int? customerGroup = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EconomicCustomerRow>>(Array.Empty<EconomicCustomerRow>());
        public Task<IReadOnlyList<EconomicContactRow>> ListContactsAsync(
            int customerNumber, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EconomicContactRow>>(Array.Empty<EconomicContactRow>());
        public Task<int> CreateContactAsync(
            int customerNumber, EconomicContactInput input, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task UpdateContactAsync(
            int customerNumber, int contactNumber, EconomicContactInput input, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task DeleteContactAsync(
            int customerNumber, int contactNumber, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static SponsorWizardService NewWizard(CommunityHubDbContext db)
    {
        var cmOptions = new CompanyManagerOptions();
        return new SponsorWizardService(
            db, new CompanyManagerClient(new HttpClient(), cmOptions), cmOptions,
            new EconomicContactAdminService(new NoEconomicClient()),
            NullLogger<SponsorWizardService>.Instance);
    }

    private static GetStartedDigestBuilder NewDigest(CommunityHubDbContext db) =>
        new(db, Templates(), new FixedClock(Now),
            new SpeakerWizardService(db), new RoleWizardService(db),
            new AttendeeWizardService(db), NewWizard(db));

    private static async Task<(int EventId, int ParticipantId)> SeedAsync(
        CommunityHubDbContext db, SponsorInfo info)
    {
        var ev = new Event
        {
            CommunityName = "C", DisplayName = "T", Code = "T27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();

        var p = new Participant
        {
            EventId = ev.Id, FullName = "Robin Solberg", Email = "robin@example.test",
            Role = ParticipantRole.Sponsor, IsActive = true, IsEventCoordinator = true,
            LifecycleState = ParticipantLifecycleState.Active,
            SponsorCompanyId = CompanyId,
            CreatedAt = LongAgo, WelcomeWithLoginSentAt = LongAgo,
        };
        db.Participants.Add(p);

        info.EventId = ev.Id;
        info.SponsorCompanyId = CompanyId;
        db.SponsorInfos.Add(info);
        await db.SaveChangesAsync();
        return (ev.Id, p.Id);
    }

    private static async Task<bool?> CompanyStepAsync(CommunityHubDbContext db, SponsorInfo info)
    {
        var (evId, pid) = await SeedAsync(db, info);
        var v = await NewWizard(db).BuildAsync(evId, pid);
        return v!.Steps.Single(s => s.Key == "company").Done;
    }

    // ---------------------------------------------------------------- the AND rule

    /// <summary>
    /// 🔴 THE REGRESSION HE REPORTED, PINNED. The old rule was
    /// <c>WebsiteUrl OR CompanyDescription</c>, and the website is the field almost every sponsor
    /// has — so it MASKED the other three and the step read done while the content was missing.
    /// </summary>
    [Fact]
    public async Task A_website_alone_does_NOT_complete_the_company_step()
    {
        using var db = ScenarioFixture.NewDb();
        Assert.False(await CompanyStepAsync(db, new SponsorInfo
        {
            SponsorPackage = SponsorPackage.Silver,
            WebsiteUrl = "https://example.test",
        }));
    }

    /// <summary>
    /// 🔒 The mirror of the rule above: the website is NOT required, because the WEBSHOP owns it and
    /// the §41b reconcile fills it in. Chasing a sponsor here for it would send them to a form that
    /// cannot fix it.
    /// </summary>
    [Fact]
    public async Task The_website_is_not_required_for_completion()
    {
        using var db = ScenarioFixture.NewDb();
        Assert.True(await CompanyStepAsync(db, new SponsorInfo
        {
            SponsorPackage = SponsorPackage.Silver,      // no booth
            CompanyDescription = "We do cloud things.",
            SocialMediaIntro = "Come and say hello.",
            // WebsiteUrl deliberately absent
        }));
    }

    /// <summary>Each required field, withheld one at a time, keeps the step open.</summary>
    [Theory]
    [InlineData(null, "some social text", "the company description is missing")]
    [InlineData("a description", null, "the SoMe branding text is missing")]
    public async Task Every_required_field_is_individually_load_bearing(
        string? description, string? social, string because)
    {
        using var db = ScenarioFixture.NewDb();
        Assert.False(
            await CompanyStepAsync(db, new SponsorInfo
            {
                SponsorPackage = SponsorPackage.Silver,
                CompanyDescription = description,
                SocialMediaIntro = social,
            }),
            because);
    }

    /// <summary>
    /// ⚠️ THE §732 TRAP, AVOIDED. <c>SponsorCompanyFormService</c> writes the short description only
    /// inside <c>if (info.HasBooth)</c> — so demanding it from a NON-exhibitor would park them below
    /// 100% for ever on a field their own form refuses to save.
    /// </summary>
    [Fact]
    public async Task A_non_exhibitor_does_NOT_need_the_short_description()
    {
        using var db = ScenarioFixture.NewDb();
        Assert.True(await CompanyStepAsync(db, new SponsorInfo
        {
            SponsorPackage = SponsorPackage.Silver,      // no booth
            CompanyDescription = "We do cloud things.",
            SocialMediaIntro = "Come and say hello.",
            // CompanyDescriptionShort absent — and correctly not demanded
        }));
    }

    /// <summary>…and the other half of the same rule: an EXHIBITOR does need it.</summary>
    [Fact]
    public async Task An_exhibitor_DOES_need_the_short_description()
    {
        using var db = ScenarioFixture.NewDb();
        Assert.False(await CompanyStepAsync(db, new SponsorInfo
        {
            SponsorPackage = SponsorPackage.Gold,        // has booth — HasBooth is DERIVED from this
            CompanyDescription = "We do cloud things.",
            SocialMediaIntro = "Come and say hello.",
            // CompanyDescriptionShort missing → still open
        }));
    }

    /// <summary>
    /// <i>"fields must contain text at minimum"</i> — whitespace is not text, and the form stores
    /// blank input as null anyway.
    /// </summary>
    [Fact]
    public async Task Whitespace_is_not_text()
    {
        using var db = ScenarioFixture.NewDb();
        Assert.False(await CompanyStepAsync(db, new SponsorInfo
        {
            SponsorPackage = SponsorPackage.Silver,
            CompanyDescription = "   ",
            SocialMediaIntro = "\t\n ",
        }));
    }

    // ------------------------------------------------- the two forms of the rule agree

    /// <summary>
    /// 🔒 §867.1's LESSON, AS A TEST. That section existed because two halves of one gate asked
    /// different questions — the list filter and the reason string — so a post was hidden whose
    /// reason said it was fine. <see cref="SponsorCompanyContent"/> carries the same rule twice (an
    /// in-memory predicate and an EF expression), so this pins them together: the expression must
    /// select exactly the companies the predicate calls incomplete, EVALUATED IN THE DATABASE.
    /// </summary>
    [Fact]
    public async Task The_EF_expression_and_the_in_memory_predicate_agree()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = new Event
        {
            CommunityName = "C", DisplayName = "T", Code = "T27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();

        db.SponsorInfos.AddRange(
            new SponsorInfo   // complete, no booth
            {
                EventId = ev.Id, SponsorCompanyId = "1",
                CompanyDescription = "d", SocialMediaIntro = "s",
            },
            new SponsorInfo   // exhibitor missing the short description
            {
                EventId = ev.Id, SponsorCompanyId = "2", SponsorPackage = SponsorPackage.Gold,
                CompanyDescription = "d", SocialMediaIntro = "s",
            },
            new SponsorInfo   // exhibitor, complete
            {
                EventId = ev.Id, SponsorCompanyId = "3", SponsorPackage = SponsorPackage.Gold,
                CompanyDescription = "d", SocialMediaIntro = "s", CompanyDescriptionShort = "short",
            },
            new SponsorInfo   // blank-but-present text
            {
                EventId = ev.Id, SponsorCompanyId = "4",
                CompanyDescription = "  ", SocialMediaIntro = "s",
            });
        await db.SaveChangesAsync();

        var fromDatabase = (await db.SponsorInfos
                .Where(SponsorCompanyContent.IsMissingContent)
                .Select(s => s.SponsorCompanyId!)
                .ToListAsync())
            .OrderBy(x => x).ToArray();

        var fromPredicate = (await db.SponsorInfos.ToListAsync())
            .Where(s => !SponsorCompanyContent.StatusOf(s).AllDelivered)
            .Select(s => s.SponsorCompanyId!)
            .OrderBy(x => x).ToArray();

        Assert.Equal(new[] { "2", "4" }, fromDatabase);
        Assert.Equal(fromDatabase, fromPredicate);
    }

    // ------------------------------------------------- the denominator

    /// <summary>
    /// 🔴 DEFECT 2. The contacts step is undeterminable here (e-conomic unreachable), and it used to
    /// sit in the DENOMINATOR while never counting toward the numerator — so this company could
    /// never reach 100%, the progress bar stuck below it for ever, and the completion notice never
    /// fired. It is not only a transient outage: a company with no ERP customer number is in this
    /// state permanently.
    /// </summary>
    [Fact]
    public async Task An_undeterminable_step_no_longer_makes_100_percent_unreachable()
    {
        using var db = ScenarioFixture.NewDb();
        var (evId, pid) = await SeedAsync(db, new SponsorInfo
        {
            SponsorPackage = SponsorPackage.Silver,      // no booth
            CompanyDescription = "We do cloud things.",
            SocialMediaIntro = "Come and say hello.",
        });

        foreach (var kind in new[] { "some", "print" })
            db.SponsorUploadAudits.Add(new SponsorUploadAudit
            {
                EventId = evId, SponsorCompanyId = CompanyId, Kind = kind,
                FileName = $"{kind}.png", Version = 1, WebUrl = "/x",
                UploadedByEmail = "alex@example.test", UploadedAt = Now.AddDays(-2),
            });
        db.PartyRsvps.Add(new PartyRsvp
        {
            EventId = evId, ParticipantId = pid, Name = "Robin Solberg",
            Email = "robin@example.test", Attending = true, HeadCount = 3,
        });
        await db.SaveChangesAsync();

        var v = (await NewWizard(db).BuildAsync(evId, pid))!;

        Assert.Null(v.Steps.Single(s => s.Key == "contacts").Done);   // still undeterminable
        Assert.Equal(4, v.TotalSteps);        // it is still LISTED — the sponsor may need that link
        Assert.Equal(3, v.EvaluableSteps);    // …but it is no longer in the denominator
        Assert.Equal(100, v.Percent);
        Assert.True(v.AllDone);
    }

    // ------------------------------------------------- stage A: the digest tells the truth

    /// <summary>
    /// 🔴 STAGE A — THE ACTUAL FIX FOR THE REPORTED SYMPTOM. The mail must name the FIELDS that are
    /// missing (§854: what somebody has to act on must be named), and must say what the team has
    /// ALREADY done, with who did it — which is the half that would have prevented the report even
    /// if nothing else changed.
    /// </summary>
    [Fact]
    public async Task The_digest_names_the_missing_fields_and_credits_what_is_already_done()
    {
        using var db = ScenarioFixture.NewDb();
        var (evId, pid) = await SeedAsync(db, new SponsorInfo
        {
            SponsorPackage = SponsorPackage.Gold,   // exhibitor ⇒ short description required too
            // The reported company's real shape: logos delivered, company content NOT.
            CompanyDescription = null,
            CompanyDescriptionShort = null,
            SocialMediaIntro = null,
            WebsiteUrl = "https://example.test",   // present, and correctly not enough
        });

        foreach (var kind in new[] { "some", "print" })
            db.SponsorUploadAudits.Add(new SponsorUploadAudit
            {
                EventId = evId, SponsorCompanyId = CompanyId, Kind = kind,
                FileName = $"{kind}.png", Version = 1, WebUrl = "/x",
                UploadedByEmail = "alex@example.test",
                UploadedAt = new DateTimeOffset(2026, 8, 11, 9, 30, 0, TimeSpan.Zero),
            });
        await db.SaveChangesAsync();

        var messages = await NewDigest(db).BuildDueAsync(evId);
        var mail = Assert.Single(messages);

        // The three missing fields are NAMED, not summarised as "company details".
        Assert.Contains("company description", mail.HtmlBody);
        Assert.Contains("short description", mail.HtmlBody);
        Assert.Contains("social-media branding text", mail.HtmlBody);

        // 🔑 …and the colleague's completed work is shown, with attribution and a date. This is the
        // sentence whose absence produced the complaint.
        Assert.Contains("Already done", mail.HtmlBody);
        Assert.Contains("alex@example.test", mail.HtmlBody);
        Assert.Contains("11 Aug 2026", mail.HtmlBody);
    }

    /// <summary>
    /// 🔒 A brand-new sponsor has finished nothing, and a heading reading *"Already done (0):"* over
    /// an empty box would be worse than silence. The whole block is one token so it can vanish.
    /// </summary>
    [Fact]
    public async Task The_done_block_is_absent_when_nothing_is_done_yet()
    {
        using var db = ScenarioFixture.NewDb();
        var (evId, _) = await SeedAsync(db, new SponsorInfo { SponsorPackage = SponsorPackage.Silver });

        var mail = Assert.Single(await NewDigest(db).BuildDueAsync(evId));

        Assert.DoesNotContain("Already done", mail.HtmlBody);
        Assert.DoesNotContain("{{doneBlockHtml}}", mail.HtmlBody);   // token always substituted
    }

    // ------------------------------------------------- stage 3: one audience rule

    /// <summary>An ERP that names its Role-2 coordinators — the resolver's PRIMARY source.</summary>
    private sealed class ErpCoordinators(params string[] emails) : ISponsorErpCoordinatorSource
    {
        public bool IsEnabled => true;
        public Task<IReadOnlyCollection<string>?> GetCoordinatorEmailsAsync(
            string sponsorCompanyId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyCollection<string>?>(emails);
    }

    /// <summary>
    /// 🔴 §1081 STAGE 3 — THE DRIFT, PINNED. The digest used to filter on
    /// <c>p.IsEventCoordinator</c> inline, which is only the FALLBACK half of the rule:
    /// <see cref="SponsorRecipientResolver"/> treats the e-conomic Role-2 set as PRIMARY and the hub
    /// flag as an additive override.
    /// <para>⇒ A coordinator holding Role 2 in ERP whose hub flag was never set received sponsor
    /// TASK reminders (which route through the resolver) and silently did NOT receive the Get
    /// Started digest. One question, two answers — the §366 shape. The digest now asks the
    /// resolver.</para>
    /// </summary>
    [Fact]
    public async Task An_ERP_coordinator_without_the_hub_flag_now_receives_the_digest()
    {
        using var db = ScenarioFixture.NewDb();
        var (evId, pid) = await SeedAsync(db, new SponsorInfo { SponsorPackage = SponsorPackage.Silver });

        // The seeded contact is a coordinator in ERP only — the hub flag is explicitly false.
        var p = await db.Participants.FirstAsync(x => x.Id == pid);
        p.IsEventCoordinator = false;
        await db.SaveChangesAsync();

        var resolver = new SponsorRecipientResolver(db, new ErpCoordinators("robin@example.test"));
        var digest = new GetStartedDigestBuilder(
            db, Templates(), new FixedClock(Now),
            new SpeakerWizardService(db), new RoleWizardService(db),
            new AttendeeWizardService(db), NewWizard(db),
            cadence: null, sponsorRecipients: resolver);

        var mail = Assert.Single(await digest.BuildDueAsync(evId));
        Assert.Equal("robin@example.test", mail.RecipientEmail);
    }

    /// <summary>
    /// 🔒 The other half of the same rule: a contact who is a coordinator in NEITHER place is still
    /// not chased. Stage 3 widened WHERE the answer comes from, not who qualifies.
    /// </summary>
    [Fact]
    public async Task A_contact_who_is_no_ones_coordinator_is_still_not_chased()
    {
        using var db = ScenarioFixture.NewDb();
        var (evId, pid) = await SeedAsync(db, new SponsorInfo { SponsorPackage = SponsorPackage.Silver });
        var p = await db.Participants.FirstAsync(x => x.Id == pid);
        p.IsEventCoordinator = false;
        await db.SaveChangesAsync();

        var resolver = new SponsorRecipientResolver(db, new ErpCoordinators("someone.else@example.test"));
        var digest = new GetStartedDigestBuilder(
            db, Templates(), new FixedClock(Now),
            new SpeakerWizardService(db), new RoleWizardService(db),
            new AttendeeWizardService(db), NewWizard(db),
            cadence: null, sponsorRecipients: resolver);

        Assert.Empty(await digest.BuildDueAsync(evId));
    }

    // ------------------------------------------------- the retired legacy task

    /// <summary>
    /// 🔒 §1081 — "sponsor.initial-onboarding" IS RETIRED AND MUST NOT COME BACK.
    /// </summary>
    /// <remarks>
    /// Operator 2026-08-13: <i>"i think that initial onboarding is legacy before we had get started
    /// wizard … it is being replaced by get started"</i>. It was raised for every sponsor, due from
    /// config, and auto-closed the moment a <c>CompanyDescription</c> was saved — i.e. the Get
    /// Started "company" step, written before the wizard existed. Keeping both meant ONE missing
    /// description produced TWO chases, in different words, on different cadences.
    /// ⚠️ A definition is easy to re-add by copy-paste from a sibling; this is the guard.
    /// </remarks>
    [Fact]
    public void The_legacy_initial_onboarding_task_is_no_longer_defined()
    {
        Assert.DoesNotContain(
            Core.Tasks.Definitions.SponsorTaskDefinitions.All,
            d => d.Key.Contains("initial-onboarding", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 🔑 §1081 — THE ORGANIZER PAGE AND THE SPONSOR'S WIZARD MUST AGREE. The deliverables
    /// "Contract & onboarding" stage used to test <c>CompanyDescription</c> alone, so an organizer
    /// saw ✓ for a sponsor who was still missing the SoMe branding text — while
    /// <c>SoMeApprovalGate</c> was blocking that company's posts for exactly that field. One
    /// question, one answer, on every surface.
    /// </summary>
    [Fact]
    public async Task The_deliverables_onboarding_stage_follows_the_same_content_rule()
    {
        using var db = ScenarioFixture.NewDb();
        var ev = new Event
        {
            CommunityName = "C", DisplayName = "T", Code = "T27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = ev.Id, SponsorCompanyId = CompanyId,
            SponsorPackage = SponsorPackage.Silver,
            CompanyDescription = "We do cloud things.",
            SocialMediaIntro = null,          // 🔴 the field the old rule ignored
        });
        await db.SaveChangesAsync();

        var d = await new Sponsors.SponsorDeliverablesService(db)
            .BuildForCompanyAsync(ev.Id, CompanyId, DateOnly.FromDateTime(Now.UtcDateTime));

        Assert.Contains(d.MissingStages, s => s.Key == "onboarding");
    }

    /// <summary>
    /// 🔒 The digest must still STOP once the company is finished — stage A changes what the mail
    /// says, never who receives one. A completed sponsor gets nothing.
    /// </summary>
    [Fact]
    public async Task A_completed_company_is_still_not_chased()
    {
        using var db = ScenarioFixture.NewDb();
        var (evId, pid) = await SeedAsync(db, new SponsorInfo
        {
            SponsorPackage = SponsorPackage.Silver,
            CompanyDescription = "We do cloud things.",
            SocialMediaIntro = "Come and say hello.",
        });

        foreach (var kind in new[] { "some", "print" })
            db.SponsorUploadAudits.Add(new SponsorUploadAudit
            {
                EventId = evId, SponsorCompanyId = CompanyId, Kind = kind,
                FileName = $"{kind}.png", Version = 1, WebUrl = "/x",
                UploadedByEmail = "alex@example.test", UploadedAt = Now.AddDays(-2),
            });
        db.PartyRsvps.Add(new PartyRsvp
        {
            EventId = evId, ParticipantId = pid, Name = "Robin Solberg",
            Email = "robin@example.test", Attending = true, HeadCount = 3,
        });
        await db.SaveChangesAsync();

        Assert.Empty(await NewDigest(db).BuildDueAsync(evId));
    }
}
