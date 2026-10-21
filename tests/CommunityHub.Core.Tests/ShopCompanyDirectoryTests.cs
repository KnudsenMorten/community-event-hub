using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Webshop;
using CommunityHub.Core.Reminders;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1309 — the company directory: find the registrant's company, join without editing it,
/// and the Sponsor / Potential-sponsor status that keeps every automatic mail away from a potential sponsor.
/// </summary>
/// <remarks>
/// Operator 2026-09-27: <i>"we will not onboard all these companies as sponsors and send emails"</i> · <i>"and no emails
/// goes out right !"</i> · <i>"i dont want edit capability in the registration form"</i> · <i>"pull already registrered
/// people for the company as well (dropdown) … so we dont get the same person registrered twice"</i>.
/// </remarks>
public sealed class ShopCompanyDirectoryTests
{
    private sealed record World(CommunityHubDbContext Db, int EventId, ShopCompanyDirectoryService Directory, CapturingEmailSender Mail);

    private static async Task<World> NewAsync()
    {
        var db = ScenarioFixture.NewDb();
        var ev = new Event { CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        var mail = new CapturingEmailSender();
        var path = Path.Combine(Path.GetTempPath(), $"shop-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, SponsorShopOrderTests.MinimalConfig);
        var settings = new ShopSettings(new Core.Config.ShopConfigLoader(), new Core.Config.ShopConfigOptions { ConfigPath = path });
        var templates = new EmailTemplateProvider(Options.Create(new EmailTemplateOptions { TemplateDirectory = RepoPaths.EmailTemplates() }));
        var mailer = new ShopMailer(db, templates, mail, settings);
        var dir = new ShopCompanyDirectoryService(db, new SponsorCompanyMembershipService(db, TimeProvider.System), TimeProvider.System, mailer);
        return new World(db, ev.Id, dir, mail);
    }

    /// <summary>
    /// The directory as the old webshop left it: two potential sponsors and their known people (Red Hat: a default signer and
    /// a coordinator; Lenovo: one signer). A company already present is left as it is.
    /// </summary>
    private static async Task PreloadAsync(World w)
    {
        var now = DateTimeOffset.UtcNow;
        if (!await w.Db.ShopCompanies.AnyAsync(c => c.EventId == w.EventId && c.SponsorCompanyId == "101"))
            w.Db.ShopCompanies.Add(new ShopCompany
            {
                EventId = w.EventId, SponsorCompanyId = "101", Source = ShopCompanySource.CompanyManager, State = ShopCompanyState.Candidate,
                LegalName = "Red Hat Denmark ApS", PublicName = "Red Hat", Website = "https://www.redhat.com",
                LinkedInUrl = "https://linkedin.com/company/redhat", Cvr = "12345678", City = "Copenhagen", CountryIso2 = "DK",
                ErpCustomerNumber = "5001", BillingReference = "PO-77", Notes = "Pays late", CreatedAt = now,
            });
        if (!await w.Db.ShopCompanies.AnyAsync(c => c.EventId == w.EventId && c.SponsorCompanyId == "102"))
            w.Db.ShopCompanies.Add(new ShopCompany
            {
                EventId = w.EventId, SponsorCompanyId = "102", Source = ShopCompanySource.CompanyManager, State = ShopCompanyState.Candidate,
                LegalName = "Lenovo Nordic", PublicName = "Lenovo", Website = "lenovo.com", City = "Aarhus", CountryIso2 = "DK",
                ErpCustomerNumber = "5002", CreatedAt = now,
            });
        w.Db.ShopKnownContacts.AddRange(
            new ShopKnownContact { EventId = w.EventId, SponsorCompanyId = "101", Email = "signe@redhat.com", FullName = "Signe Signer",
                IsSigner = true, Source = "CompanyManager", ImportedAt = now },
            new ShopKnownContact { EventId = w.EventId, SponsorCompanyId = "101", Email = "carl@redhat.com", FullName = "Carl Coordinator",
                IsEventCoordinator = true, Source = "CompanyManager", ImportedAt = now },
            new ShopKnownContact { EventId = w.EventId, SponsorCompanyId = "102", Email = "lena@lenovo.com", FullName = "Lena L",
                IsSigner = true, Source = "CompanyManager", ImportedAt = now });
        await w.Db.SaveChangesAsync();
    }

    /// <summary>
    /// §1312 — operator 2026-09-27, registering test-webshop@&lt;organizers' domain&gt;: <i>"this is wrong as it should not default
    /// to 2linkit. why"</i>. A sponsor's test contacts on the organizers' own domain made that domain point at the sponsor —
    /// and a domain match joins WITHOUT approval. The organizers' domain never identifies a company.
    /// </summary>
    [Fact]
    public async Task The_organizers_own_domain_never_identifies_a_sponsor_company()
    {
        var w = await NewAsync();
        await PreloadAsync(w);
        // A sponsor contact with an address on the organizers' domain (the shop config's organizer / owner / notify addresses).
        w.Db.ShopKnownContacts.Add(new ShopKnownContact { EventId = w.EventId, SponsorCompanyId = "101", Email = "tester@example.test",
            FullName = "Test Booth (tester@example.test)", Source = "CompanyManager", ImportedAt = DateTimeOffset.UtcNow });
        await w.Db.SaveChangesAsync();

        Assert.Empty(await w.Directory.FindByEmailDomainAsync(w.EventId, "test-webshop@example.test"));   // no company offered
        Assert.NotEmpty(await w.Directory.FindByEmailDomainAsync(w.EventId, "new.person@redhat.com"));  // real domains still match

        var people = await w.Directory.PeopleForAsync(w.EventId, "101");
        Assert.DoesNotContain(people, p => p.Name.Contains("tester@example.test"));                       // no address inside a name
        Assert.Contains(people, p => p.Name == "Test Booth (t•••@example.test)");
    }

    /// <summary>
    /// 🔴 §1312 — e-conomic is the master: every field is compared and CEH follows it, a BLANK included (operator 2026-10-01:
    /// "the erp sync must validate ALL fields at every sync … compare the fields and update if different"). SquaredUp kept a
    /// Company Manager placeholder "1000 København" because a blank used to be ignored. Only what CEH cannot hold is skipped:
    /// a blank legal name / currency, a non-zone, an unrecognised country.
    /// </summary>
    [Fact]
    public void Every_economic_field_is_mirrored_a_blank_included()
    {
        var row = new ShopCompany { LegalName = "SquaredUp", PublicName = "SquaredUp", Address = "Pearce Building, Maidenhead SL6 1RL", Postcode = "1000",
            City = "København", VatZone = 1, Currency = "EUR", CountryIso2 = "DK", Cvr = "OLD", InvoiceEmail = "old@example.test" };

        var changed = ShopCompanyService.ApplyErpBilling(row, " ", "GB04561749", "Pearce Building, Maidenhead SL6 1RL", "", null,
            "United Kingdom", "lee@example.test", null, 9);

        Assert.True(changed);
        Assert.Null(row.Postcode);                  // blank in e-conomic ⇒ blank in CEH
        Assert.Null(row.City);
        Assert.Equal("GB", row.CountryIso2);         // e-conomic's country NAME is mapped to the code
        Assert.Equal("04561749", row.Cvr);           // normalised like everywhere else
        Assert.Equal("lee@example.test", row.InvoiceEmail);
        Assert.Equal("SquaredUp", row.LegalName);    // a blank legal name is not a value
        Assert.Equal("EUR", row.Currency);           // nor a blank currency
        Assert.Equal(1, row.VatZone);                // 9 is not a VAT zone

        Assert.False(ShopCompanyService.ApplyErpBilling(row, "SquaredUp", "04561749", "Pearce Building, Maidenhead SL6 1RL", null,
            null, "United Kingdom", "lee@example.test", "EUR", 1));   // nothing different ⇒ nothing changed
    }

    [Theory]
    [InlineData("new.person@redhat.com", "101")]   // the company's website domain
    [InlineData("someone@lenovo.com", "102")]      // website without scheme, and a known person's domain
    [InlineData("me@gmail.com", null)]             // a public address never matches a company
    [InlineData("x@unknown.example", null)]
    public async Task The_registrants_company_is_found_by_their_verified_domain(string email, string? expected)
    {
        var w = await NewAsync();
        await PreloadAsync(w);

        var found = await w.Directory.FindByEmailDomainAsync(w.EventId, email);

        if (expected is null) Assert.Empty(found);
        else Assert.Equal(expected, Assert.Single(found).SponsorCompanyId);
    }

    [Fact]
    public async Task The_dropdown_shows_known_people_with_masked_emails()
    {
        var w = await NewAsync();
        await PreloadAsync(w);

        var people = await w.Directory.PeopleForAsync(w.EventId, "101");

        Assert.Equal(2, people.Count);
        Assert.All(people, p => Assert.DoesNotContain("signe@", p.MaskedEmail));
        Assert.Contains(people, p => p.MaskedEmail == "s•••@redhat.com" && p.IsSigner);
    }

    [Fact]
    public async Task The_first_joiner_picks_signer_and_coordinator_from_known_people_and_nobody_is_registered_twice_or_welcomed()
    {
        var w = await NewAsync();
        await PreloadAsync(w);
        var people = await w.Directory.PeopleForAsync(w.EventId, "101");
        var signe = people.Single(p => p.IsSigner).Key;

        var (r, pid) = await w.Directory.JoinAsync(w.EventId, "101", "new.person@redhat.com", "New Person", null, signe, "me");

        Assert.True(r.Ok, r.Message);
        var hub = await w.Db.SponsorCompanyContacts.Include(l => l.Participant).Where(l => l.SponsorCompanyId == "101").ToListAsync();
        Assert.Equal(2, hub.Count);
        Assert.True(hub.Single(l => l.Participant.Email == "signe@redhat.com").IsSigner);      // reused, not retyped
        Assert.True(hub.Single(l => l.ParticipantId == pid).IsEventCoordinator);
        Assert.Equal(1, await w.Db.Participants.CountAsync(p => p.Email == "signe@redhat.com"));

        // A second join by Signe herself does not create her twice.
        Assert.True((await w.Directory.JoinAsync(w.EventId, "101", "signe@redhat.com", "Signe Signer", null, null, null)).Result.Ok);
        Assert.Equal(1, await w.Db.Participants.CountAsync(p => p.Email == "signe@redhat.com"));

        // 🔒 The company is still a POTENTIAL sponsor, so its coordinator gets no welcome.
        var info = await w.Db.SponsorInfos.IgnoreQueryFilters().SingleAsync(s => s.SponsorCompanyId == "101");
        Assert.False(info.IsSponsor);
        var welcome = new SponsorWelcomeEmailService(w.Db, new SponsorRecipientResolver(w.Db),
            new WelcomeEmailService(w.Db, new EmailTemplateProvider(Options.Create(new EmailTemplateOptions { TemplateDirectory = RepoPaths.EmailTemplates() })), w.Mail, TimeProvider.System));
        var sent = await welcome.SendForCompanyAsync(w.EventId, "101");
        Assert.Equal(0, sent.Sent);
        Assert.Empty(w.Mail.Sent);
    }

    /// <summary>
    /// §1312 — operator 2026-09-27: "i am missing the role here, either signer or event coordinator or both - tick off". A
    /// later joiner ticks their own role(s); the colleague who already holds the role keeps it.
    /// </summary>
    [Fact]
    public async Task A_later_joiner_can_tick_their_own_roles_and_colleagues_keep_theirs()
    {
        var w = await NewAsync();
        await PreloadAsync(w);
        await w.Directory.JoinAsync(w.EventId, "101", "carl@redhat.com", "Carl Coordinator", null, "me", "me");

        var (r, pid) = await w.Directory.JoinAsync(w.EventId, "101", "late@redhat.com", "Late Comer", null, null, null,
            claimSigner: true, claimCoordinator: true);

        Assert.True(r.Ok, r.Message);
        var late = await w.Db.SponsorCompanyContacts.SingleAsync(l => l.ParticipantId == pid);
        Assert.True(late.IsSigner);
        Assert.True(late.IsEventCoordinator);
        var carl = await w.Db.SponsorCompanyContacts.SingleAsync(l => l.Participant.Email == "carl@redhat.com");
        Assert.True(carl.IsSigner);                  // kept
        Assert.True(carl.IsEventCoordinator);        // kept
        Assert.Empty(w.Mail.Sent);                   // a potential sponsor: no mail
    }

    [Fact]
    public async Task A_later_joiner_becomes_a_plain_contact_and_the_companys_roles_and_data_stay_as_they_are()
    {
        var w = await NewAsync();
        await PreloadAsync(w);
        await w.Directory.JoinAsync(w.EventId, "101", "carl@redhat.com", "Carl Coordinator", null, "me", "me");
        var before = await w.Db.ShopCompanies.AsNoTracking().SingleAsync(c => c.SponsorCompanyId == "101");

        var (r, pid) = await w.Directory.JoinAsync(w.EventId, "101", "late@redhat.com", "Late Comer", null,
            (await w.Directory.PeopleForAsync(w.EventId, "101")).First().Key, "me");   // ignored: the company has people already

        Assert.True(r.Ok);
        var late = await w.Db.SponsorCompanyContacts.SingleAsync(l => l.ParticipantId == pid);
        Assert.False(late.IsSigner);
        Assert.False(late.IsEventCoordinator);
        var after = await w.Db.ShopCompanies.AsNoTracking().SingleAsync(c => c.SponsorCompanyId == "101");
        Assert.Equal(before.LegalName, after.LegalName);
        Assert.Equal(before.Website, after.Website);
    }

    [Fact]
    public async Task One_person_can_be_signer_or_coordinator_for_several_companies()
    {
        // Operator 2026-09-27: "arrow have same event coordinator in all 4 countries" · "same with signer, he can be cfo
        // for multiple companies". One participant, one link per company, roles per company.
        var w = await NewAsync();
        await PreloadAsync(w);
        var (a, pid) = await w.Directory.JoinAsync(w.EventId, "101", "cfo@group.example", "Group CFO", null, "me", "me");
        var (b, pid2) = await w.Directory.JoinAsync(w.EventId, "102", "cfo@group.example", "Group CFO", null, "me", "me");

        Assert.True(a.Ok && b.Ok);
        Assert.Equal(pid, pid2);
        Assert.Single(await w.Db.Participants.Where(p => p.Email == "cfo@group.example").ToListAsync());
        var links = await w.Db.SponsorCompanyContacts.Where(l => l.ParticipantId == pid).ToListAsync();
        Assert.Equal(new[] { "101", "102" }, links.Select(l => l.SponsorCompanyId).OrderBy(x => x).ToArray());
        Assert.All(links, l => Assert.True(l.IsSigner && l.IsEventCoordinator));
    }

    [Fact]
    public async Task A_cvr_only_match_waits_for_an_organizer_and_gives_no_access_until_approved()
    {
        var w = await NewAsync();
        await PreloadAsync(w);
        var match = await w.Directory.FindByCvrAsync(w.EventId, "DK12345678");
        Assert.Equal("101", match!.SponsorCompanyId);

        var r = await w.Directory.RequestJoinAsync(w.EventId, "101", "someone@gmail.com", "Some One", null, "cvr");

        Assert.True(r.Ok);
        Assert.Empty(await w.Db.Participants.ToListAsync());                                   // no access yet
        Assert.Single(w.Mail.Sent, m => m.To == "ops@example.test");                           // only the organizers are told
        var req = await w.Db.ShopJoinRequests.SingleAsync();

        Assert.True((await w.Directory.DecideAsync(w.EventId, req.Id, approve: true, "org@example.test", null)).Ok);
        Assert.Single(await w.Db.Participants.Where(p => p.Email == "someone@gmail.com").ToListAsync());
        Assert.Single(w.Mail.Sent);                                                            // approval mails nobody
    }

    [Fact]
    public async Task A_rejected_request_gives_no_access()
    {
        var w = await NewAsync();
        await PreloadAsync(w);
        await w.Directory.RequestJoinAsync(w.EventId, "101", "someone@gmail.com", "Some One", null, "cvr");
        var req = await w.Db.ShopJoinRequests.SingleAsync();

        await w.Directory.DecideAsync(w.EventId, req.Id, approve: false, "org@example.test", "not known");

        Assert.Empty(await w.Db.Participants.ToListAsync());
        Assert.False((await w.Db.ShopJoinRequests.SingleAsync()).Approved);
    }

    [Fact]
    public async Task A_sponsor_company_is_still_welcomed_and_a_company_without_a_sponsor_record_keeps_todays_behaviour()
    {
        var w = await NewAsync();
        var templates = new EmailTemplateProvider(Options.Create(new EmailTemplateOptions { TemplateDirectory = RepoPaths.EmailTemplates() }));
        var welcome = new SponsorWelcomeEmailService(w.Db, new SponsorRecipientResolver(w.Db),
            new WelcomeEmailService(w.Db, templates, w.Mail, TimeProvider.System));
        w.Db.SponsorInfos.Add(new SponsorInfo { EventId = w.EventId, SponsorCompanyId = "s1", CompanyName = "Sponsor", IsSponsor = true });
        w.Db.Participants.AddRange(
            new Participant { EventId = w.EventId, Email = "c@sponsor.test", FullName = "C", Role = ParticipantRole.Sponsor, SponsorCompanyId = "s1", IsEventCoordinator = true, IsActive = true },
            new Participant { EventId = w.EventId, Email = "c@legacy.test", FullName = "L", Role = ParticipantRole.Sponsor, SponsorCompanyId = "legacy", IsEventCoordinator = true, IsActive = true });
        await w.Db.SaveChangesAsync();

        Assert.Equal(1, (await welcome.SendForCompanyAsync(w.EventId, "s1")).Sent);
        Assert.Equal(1, (await welcome.SendForCompanyAsync(w.EventId, "legacy")).Sent);
    }

    // ---- §1311 — the organizer company page and e-conomic roles for potential sponsors -----------------------------

    [Fact]
    public async Task An_organizer_sets_the_default_signer_and_coordinator_of_a_potential_sponsor_exclusively_and_no_mail_goes()
    {
        var w = await NewAsync();
        await PreloadAsync(w);
        var people = await w.Directory.PeopleForAsync(w.EventId, "101");
        var signe = people.Single(p => p.Name == "Signe Signer").Key;
        var carl = people.Single(p => p.Name == "Carl Coordinator").Key;

        Assert.True((await w.Directory.SetDefaultsAsync(w.EventId, "101", signe, carl)).Ok);
        var links = await w.Db.SponsorCompanyContacts.Include(l => l.Participant).Where(l => l.SponsorCompanyId == "101").ToListAsync();
        Assert.True(links.Single(l => l.Participant.Email == "signe@redhat.com").IsSigner);
        Assert.True(links.Single(l => l.Participant.Email == "carl@redhat.com").IsEventCoordinator);

        // Swap: Carl signs too — Signe loses the signer role (one default signer per company).
        var carlNow = (await w.Directory.PeopleForAsync(w.EventId, "101")).Single(p => p.Name == "Carl Coordinator").Key;
        Assert.True((await w.Directory.SetDefaultsAsync(w.EventId, "101", carlNow, carlNow)).Ok);
        links = await w.Db.SponsorCompanyContacts.Include(l => l.Participant).Where(l => l.SponsorCompanyId == "101").ToListAsync();
        Assert.Single(links, l => l.IsSigner);
        Assert.True(links.Single(l => l.IsSigner).Participant.Email == "carl@redhat.com");

        Assert.Empty(w.Mail.Sent);   // a potential sponsor: nothing is mailed
    }

    [Fact]
    public async Task Removing_a_person_keeps_them_in_the_hub_and_they_stop_acting_for_the_company()
    {
        var w = await NewAsync();
        await PreloadAsync(w);
        await w.Directory.LinkPersonAsync(w.EventId, "101", null, "temp@redhat.com", "Temp Person");
        var pid = (await w.Db.Participants.SingleAsync(p => p.Email == "temp@redhat.com")).Id;

        Assert.True((await w.Directory.UnlinkPersonAsync(w.EventId, "101", pid)).Ok);

        var p = await w.Db.Participants.SingleAsync(x => x.Id == pid);                      // never deleted
        Assert.Null(p.SponsorCompanyId);                                                      // no longer acts for it
        Assert.Empty(await w.Db.SponsorCompanyContacts.Where(l => l.ParticipantId == pid).ToListAsync());
    }

    [Fact]
    public async Task The_organizer_edits_only_the_CEH_owned_fields()
    {
        var w = await NewAsync();
        await PreloadAsync(w);

        Assert.False((await w.Directory.UpdateCehDataAsync(w.EventId, "101", "Red Hat", "not a url with spaces", null, null, null, null)).Ok);
        Assert.True((await w.Directory.UpdateCehDataAsync(w.EventId, "101", "Red Hat Nordics", "https://redhat.com/nordics", null, null, "PO-88", "note")).Ok);

        var c = await w.Db.ShopCompanies.SingleAsync(x => x.SponsorCompanyId == "101");
        Assert.Equal("Red Hat Nordics", c.PublicName);
        Assert.Equal("PO-88", c.BillingReference);
        Assert.Equal("Red Hat Denmark ApS", c.LegalName);   // e-conomic-owned: untouched
        Assert.Equal("12345678", c.Cvr);
    }

    /// <summary>
    /// 🔴 §1312 — operator 2026-09-29: a shop-registered company (<c>ceh-…</c>) has no Company Manager record, so its public name
    /// edited here must reach the sponsor record the event site, the uploads and Zoho read. The legal name is untouched.
    /// </summary>
    [Theory]
    [InlineData("ceh-abc", ShopCompanySource.Registered)]
    [InlineData("777", ShopCompanySource.CompanyManager)]   // "we are not pulling orders from CM anymore" — CEH owns it for all
    public async Task A_public_name_edit_reaches_the_sponsor_record(string id, ShopCompanySource source)
    {
        var w = await NewAsync();
        w.Db.ShopCompanies.Add(new ShopCompany
        {
            EventId = w.EventId, SponsorCompanyId = id, Source = source,
            LegalName = "Master Packager SIA", PublicName = "Master Packager SIA",
        });
        w.Db.SponsorInfos.Add(new SponsorInfo { EventId = w.EventId, SponsorCompanyId = id, CompanyName = "Master Packager SIA" });
        await w.Db.SaveChangesAsync();

        Assert.True((await w.Directory.UpdateCehDataAsync(w.EventId, id, "Master Packager", null, null, null, null, null)).Ok);

        Assert.Equal("Master Packager", (await w.Db.SponsorInfos.IgnoreQueryFilters().SingleAsync(i => i.SponsorCompanyId == id)).CompanyName);
        Assert.Equal("Master Packager SIA", (await w.Db.ShopCompanies.SingleAsync(c => c.SponsorCompanyId == id)).LegalName);
    }

    /// <summary>
    /// §1312 — operator 2026-09-29: "remember to add a place in ceh, where i can edit this field". The companies table sets the
    /// public name alone (nothing else on the company), copies it to the sponsor record, and refuses an empty one.
    /// </summary>
    [Fact]
    public async Task The_companies_table_sets_the_public_name_and_it_reaches_the_sponsor_record()
    {
        var w = await NewAsync();
        w.Db.ShopCompanies.Add(new ShopCompany
        {
            EventId = w.EventId, SponsorCompanyId = "43", Source = ShopCompanySource.CompanyManager,
            LegalName = "Admin By Request Nordics ApS", PublicName = "Admin By Request Nordics", Website = "https://adminbyrequest.com",
        });
        w.Db.SponsorInfos.Add(new SponsorInfo { EventId = w.EventId, SponsorCompanyId = "43", CompanyName = "Admin By Request Nordics" });
        await w.Db.SaveChangesAsync();

        Assert.False((await w.Directory.SetPublicNameAsync(w.EventId, "43", "  ")).Ok);
        Assert.True((await w.Directory.SetPublicNameAsync(w.EventId, "43", "Admin By Request")).Ok);

        var c = await w.Db.ShopCompanies.SingleAsync(x => x.SponsorCompanyId == "43");
        Assert.Equal("Admin By Request", c.PublicName);
        Assert.Equal("https://adminbyrequest.com", c.Website);                    // nothing else touched
        Assert.Equal("Admin By Request Nordics ApS", c.LegalName);
        Assert.Equal("Admin By Request", (await w.Db.SponsorInfos.IgnoreQueryFilters().SingleAsync(i => i.SponsorCompanyId == "43")).CompanyName);
    }

    /// <summary>
    /// 🔴 §1312 — operator 2026-09-30: "only thing i was asking for was, if i can also easily manage that field. but it must be
    /// the same field". The organizer's website / LinkedIn / X edit on the company page reaches the sponsor record (what Zoho,
    /// the site and SoMe read, and what the sponsor edits in Get Started), made https://.
    /// </summary>
    [Fact]
    public async Task The_organizer_edits_the_same_links_the_sponsor_edits()
    {
        var w = await NewAsync();
        w.Db.ShopCompanies.Add(new ShopCompany { EventId = w.EventId, SponsorCompanyId = "13", Source = ShopCompanySource.CompanyManager, LegalName = "Identity Stack A/S", PublicName = "Identity Stack" });
        w.Db.SponsorInfos.Add(new SponsorInfo { EventId = w.EventId, SponsorCompanyId = "13", CompanyName = "Identity Stack", WebsiteUrl = "http://identitystack.dk" });
        await w.Db.SaveChangesAsync();

        Assert.True((await w.Directory.UpdateCehDataAsync(w.EventId, "13", "Identity Stack", "http://identitystack.dk",
            "linkedin.com/company/identitystack", "https://x.com/identitystack", null, null)).Ok);

        var info = await w.Db.SponsorInfos.IgnoreQueryFilters().SingleAsync(i => i.SponsorCompanyId == "13");
        Assert.Equal("https://identitystack.dk", info.WebsiteUrl);
        Assert.Equal("https://linkedin.com/company/identitystack", info.LinkedInUrl);
        Assert.Equal("https://x.com/identitystack", info.TwitterUrl);
    }

    private static async Task<string?> SignerAsync(World w, string companyId) =>
        await w.Db.SponsorCompanyContacts.Where(l => l.SponsorCompanyId == companyId && l.IsSigner)
            .Select(l => l.Participant.Email).SingleOrDefaultAsync();

    [Fact]
    public async Task A_cvr_match_by_someone_the_company_already_knows_joins_directly()
    {
        // §1309 C — operator 2026-09-27: the confirmed address is one of the company's known people, so no approval wait.
        var w = await NewAsync();
        await PreloadAsync(w);

        Assert.True(await w.Directory.IsKnownPersonOfAsync(w.EventId, "101", "Signe@RedHat.com"));   // known from the webshop
        Assert.False(await w.Directory.IsKnownPersonOfAsync(w.EventId, "101", "someone@gmail.com"));
        Assert.False(await w.Directory.IsKnownPersonOfAsync(w.EventId, "102", "signe@redhat.com"));  // another company's person
    }

    /// <summary>
    /// §1312 — operator 2026-09-27: "it must be 100% clear that next step is that they register (self-service) and can
    /// complete the order themselves". The offer's address becomes a known person of THAT company, so its registration by
    /// CVR joins directly (no approval wait) and a PIN sign-in works too — with no user, no role and no mail created by the
    /// offer itself; a gmail-type address never makes other gmail registrants match the company.
    /// </summary>
    [Fact]
    public async Task An_offer_recipient_can_complete_the_order_self_service_and_nothing_else_changes()
    {
        var w = await NewAsync();
        await PreloadAsync(w);
        Assert.False(await w.Directory.IsKnownPersonOfAsync(w.EventId, "101", "buyer@gmail.com"));   // before: join request

        await w.Directory.RememberOfferRecipientAsync(w.EventId, "101", " Buyer@Gmail.com ");
        await w.Directory.RememberOfferRecipientAsync(w.EventId, "101", "buyer@gmail.com");            // once

        Assert.True(await w.Directory.IsKnownPersonOfAsync(w.EventId, "101", "buyer@gmail.com"));     // registration joins
        Assert.False(await w.Directory.IsKnownPersonOfAsync(w.EventId, "102", "buyer@gmail.com"));    // only THAT company
        var row = await w.Db.ShopKnownContacts.SingleAsync(k => k.Email == "buyer@gmail.com");
        Assert.False(row.IsSigner || row.IsEventCoordinator);                                           // no role
        Assert.Empty(await w.Directory.FindByEmailDomainAsync(w.EventId, "other@gmail.com"));          // no gmail-wide match
        Assert.Empty(await w.Db.Participants.ToListAsync());                                            // no user until they come
        Assert.Empty(w.Mail.Sent);

        // …and they can simply sign in with that address as well.
        Assert.True(await w.Directory.EnsureKnownPersonCanSignInAsync(w.EventId, "buyer@gmail.com"));
        Assert.Equal("101", (await w.Db.Participants.SingleAsync()).SponsorCompanyId);
        Assert.Empty(w.Mail.Sent);
    }

    // ---- Sign-in by default — and NO mail because of it (operator 2026-09-27: "we cannot send emails to 110 potential
    // sponsors or other roles due to this change. important it is working as intended, otherwise we dont do it") -------

    [Fact]
    public async Task Asking_for_a_pin_makes_only_that_known_person_a_contact_and_nobody_is_mailed()
    {
        var w = await NewAsync();
        await PreloadAsync(w);

        Assert.True(await w.Directory.EnsureKnownPersonCanSignInAsync(w.EventId, "Carl@RedHat.com"));
        Assert.False(await w.Directory.EnsureKnownPersonCanSignInAsync(w.EventId, "carl@redhat.com"));      // once
        Assert.False(await w.Directory.EnsureKnownPersonCanSignInAsync(w.EventId, "stranger@redhat.com"));  // not known → nobody

        var people = await w.Db.Participants.ToListAsync();
        Assert.Single(people);                                           // ONLY the person who asked — never the 3 known
        Assert.Equal(ParticipantRole.Sponsor, people[0].Role);
        Assert.Equal("101", people[0].SponsorCompanyId);
        Assert.True((await w.Db.SponsorCompanyContacts.SingleAsync()).IsEventCoordinator);   // the webshop's role, on a potential sponsor
        Assert.False((await w.Db.SponsorInfos.IgnoreQueryFilters().SingleAsync()).IsSponsor); // hidden record → no welcome
        Assert.Empty(w.Mail.Sent);
    }

    [Fact]
    public async Task After_known_people_sign_in_the_sponsor_welcome_over_EVERY_company_sends_nothing()
    {
        var w = await NewAsync();
        await PreloadAsync(w);
        foreach (var e in new[] { "signe@redhat.com", "carl@redhat.com", "lena@lenovo.com" })
            await w.Directory.EnsureKnownPersonCanSignInAsync(w.EventId, e);

        var templates = new EmailTemplateProvider(Options.Create(new EmailTemplateOptions { TemplateDirectory = RepoPaths.EmailTemplates() }));
        var welcome = new SponsorWelcomeEmailService(w.Db, new SponsorRecipientResolver(w.Db),
            new WelcomeEmailService(w.Db, templates, w.Mail, TimeProvider.System));
        var results = await welcome.SendForAllSponsorsAsync(w.EventId);    // what the 15-minute job runs

        Assert.Equal(0, results.Sum(r => r.Sent));
        Assert.Empty(w.Mail.Sent);
        // The generic welcome job skips the Sponsor role, and the Get Started chaser needs a welcome first — so these
        // people get nothing until their company signs and becomes a sponsor.
        Assert.All(await w.Db.Participants.ToListAsync(), p => Assert.Null(p.WelcomeWithLoginSentAt));
    }

    [Fact]
    public async Task A_known_person_of_a_company_that_is_already_a_sponsor_joins_as_a_plain_contact()
    {
        var w = await NewAsync();
        w.Db.ShopCompanies.Add(new ShopCompany { EventId = w.EventId, SponsorCompanyId = "101", LegalName = "Red Hat", State = ShopCompanyState.Sponsor,
            Source = ShopCompanySource.CompanyManager, CreatedAt = DateTimeOffset.UtcNow });
        w.Db.SponsorInfos.Add(new SponsorInfo { EventId = w.EventId, SponsorCompanyId = "101", CompanyName = "Red Hat", IsSponsor = true });
        await w.Db.SaveChangesAsync();
        await PreloadAsync(w);

        await w.Directory.EnsureKnownPersonCanSignInAsync(w.EventId, "carl@redhat.com");

        var link = await w.Db.SponsorCompanyContacts.SingleAsync();
        Assert.False(link.IsEventCoordinator);   // a sponsor's roles are not changed by someone signing in
        Assert.False(link.IsSigner);
        Assert.Empty(w.Mail.Sent);
    }

    /// <summary>
    /// §1344 — the sponsor's public-name edit (Get Started / Company Details) and the organizer's (company page) are ONE
    /// field (operator 2026-10-14: "it must the same field as if i as an organizer see it and chg it"). Each side sees the
    /// other's change; a blank never clears it; the legal name is untouched.
    /// </summary>
    [Fact]
    public async Task Sponsor_and_organizer_edit_and_see_the_same_public_name()
    {
        var w = await NewAsync();
        await PreloadAsync(w);
        var info = new SponsorInfo { EventId = w.EventId, SponsorCompanyId = "101", CompanyName = "Red Hat", IsSponsor = true };
        w.Db.SponsorInfos.Add(info);
        await w.Db.SaveChangesAsync();

        // The sponsor renames → the organizer's field shows it.
        Assert.True(await ShopCompanyLinks.ApplyPublicNameAsync(w.Db, w.EventId, "101", info, "  Red Hat Nordics ", default));
        await w.Db.SaveChangesAsync();
        var shop = await w.Db.ShopCompanies.SingleAsync(c => c.EventId == w.EventId && c.SponsorCompanyId == "101");
        Assert.Equal("Red Hat Nordics", shop.PublicName);
        Assert.Equal("Red Hat Nordics", info.CompanyName);
        Assert.Equal("Red Hat Denmark ApS", shop.LegalName);

        // A blank keeps it.
        Assert.False(await ShopCompanyLinks.ApplyPublicNameAsync(w.Db, w.EventId, "101", info, "   ", default));
        Assert.Equal("Red Hat Nordics", info.CompanyName);

        // The organizer renames in the companies table → the sponsor's screen shows it.
        Assert.True((await w.Directory.SetPublicNameAsync(w.EventId, "101", "RH", default)).Ok);
        Assert.Equal("RH", await ShopCompanyLinks.PublicNameAsync(w.Db, w.EventId, "101", default));

        // …and on the company page.
        await w.Directory.UpdateCehDataAsync(w.EventId, "101", "Red Hat", shop.Website, shop.LinkedInUrl, shop.TwitterUrl,
            shop.BillingReference, shop.Notes, default);
        Assert.Equal("Red Hat", await ShopCompanyLinks.PublicNameAsync(w.Db, w.EventId, "101", default));
    }
}
