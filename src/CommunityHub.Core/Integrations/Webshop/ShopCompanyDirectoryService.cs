using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Webshop;

/// <summary>A company found for a registrant: shown as name + city only (read-only).</summary>
public sealed record ShopCompanyMatch(string SponsorCompanyId, string Name, string? City);

/// <summary>One person in the join dropdown — the e-mail is MASKED, the registrant has only proven their own address.</summary>
public sealed record ShopPersonOption(string Key, string Name, string MaskedEmail, bool IsSigner, bool IsEventCoordinator);

/// <summary>
/// §1309 — the company directory: find a registrant's company and let a verified registrant JOIN it (§1312 — the pre-load
/// from the old webshop's Company Manager is retired; companies and their known people come from e-conomic).
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-27: <i>"yes keep preload, build it"</i> · <i>"and no emails goes out right !"</i> ·
/// <i>"i dont want edit capability in the registration form, as then anyone cna change any company"</i> · <i>"pull
/// already registrered people for the company as well (dropdown) … so we dont get the same person registrered twice"</i>.</para>
/// <para>🔒 <b>No mail can leave this service</b> — it has no mailer except for the one organizer notice about a
/// join request waiting for approval. The import writes companies and a LOOKUP list of people (never participants).</para>
/// <para>🔒 <b>A join never changes the company.</b> It only adds the registrant (and, for a company with nobody in the
/// hub yet, the signer / coordinator the registrant picks from the known people) as contacts.</para>
/// </remarks>
public sealed class ShopCompanyDirectoryService
{
    /// <summary>E-mail domains that identify a PERSON, never a company — they never match a company.</summary>
    public static readonly HashSet<string> PublicDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail.com", "googlemail.com", "outlook.com", "hotmail.com", "hotmail.dk", "live.com", "live.dk", "msn.com",
        "yahoo.com", "yahoo.dk", "icloud.com", "me.com", "mac.com", "aol.com", "proton.me", "protonmail.com", "gmx.com",
        "gmx.net", "mail.dk", "jubii.dk", "sol.dk", "outlook.dk", "email.dk",
    };

    private readonly CommunityHubDbContext _db;
    private readonly SponsorCompanyMembershipService _membership;
    private readonly TimeProvider _clock;
    private readonly ShopMailer? _mail;
    private readonly ILogger<ShopCompanyDirectoryService>? _log;

    public ShopCompanyDirectoryService(
        CommunityHubDbContext db, SponsorCompanyMembershipService membership, TimeProvider clock,
        ShopMailer? mail = null, ILogger<ShopCompanyDirectoryService>? log = null)
    {
        _db = db;
        _membership = membership;
        _clock = clock;
        _mail = mail;
        _log = log;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ------------------------------------------------------------------------------------------------------------
    // §1311 · The organizer company page
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Make <paramref name="participantId"/> THE default signer (or event coordinator) of the company — every other contact of
    /// the company loses that role. Roles are per company link, so the same person keeps their roles in other companies.
    /// </summary>
    public async Task SetExclusiveRoleAsync(int eventId, string companyId, int? participantId, bool signer, CancellationToken ct = default)
    {
        var links = await _db.SponsorCompanyContacts.Include(l => l.Participant)
            .Where(l => l.EventId == eventId && l.SponsorCompanyId == companyId).ToListAsync(ct);
        foreach (var l in links)
        {
            var on = participantId is int p && l.ParticipantId == p;
            if (signer) l.IsSigner = on; else l.IsEventCoordinator = on;
            // The participant-level flag describes the PRIMARY company — keep it in step for people whose primary this is.
            if (l.Participant.SponsorCompanyId == companyId)
            {
                if (signer) l.Participant.IsSigner = on; else l.Participant.IsEventCoordinator = on;
            }
        }
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Set the default signer and coordinator from the organizer page ("p:{id}", "k:{id}" or "" for nobody).</summary>
    public async Task<ShopResult> SetDefaultsAsync(int eventId, string companyId, string? signerKey, string? coordinatorKey, CancellationToken ct = default)
    {
        if (!await _db.ShopCompanies.AnyAsync(c => c.EventId == eventId && c.SponsorCompanyId == companyId, ct))
            return ShopResult.Fail("That company is not known.");
        var company = await _db.ShopCompanies.FirstAsync(c => c.EventId == eventId && c.SponsorCompanyId == companyId, ct);
        await EnsureHiddenSponsorRecordAsync(eventId, company, _clock.GetUtcNow(), ct);
        foreach (var (key, isSigner) in new[] { (signerKey, true), (coordinatorKey, false) })
        {
            if (string.IsNullOrWhiteSpace(key)) { await SetExclusiveRoleAsync(eventId, companyId, null, isSigner, ct); continue; }
            var person = await PickAsync(eventId, companyId, key, _clock.GetUtcNow(), ct);
            if (person is null) return ShopResult.Fail("That person cannot be chosen (not found, or in another role in the hub).");
            await _membership.LinkAsync(eventId, person.Id, companyId, isPrimary: person.SponsorCompanyId == companyId, ct: ct);
            await _db.SaveChangesAsync(ct);
            await SetExclusiveRoleAsync(eventId, companyId, person.Id, isSigner, ct);
        }
        return ShopResult.Success("Default signer and event coordinator saved.");
    }

    /// <summary>Link a person to the company: a known person, or a new / existing sponsor contact by e-mail.</summary>
    public async Task<ShopResult> LinkPersonAsync(int eventId, string companyId, string? knownKey, string? email, string? name, CancellationToken ct = default)
    {
        var company = await _db.ShopCompanies.FirstOrDefaultAsync(c => c.EventId == eventId && c.SponsorCompanyId == companyId, ct);
        if (company is null) return ShopResult.Fail("That company is not known.");
        var now = _clock.GetUtcNow();
        await EnsureHiddenSponsorRecordAsync(eventId, company, now, ct);
        Participant? person;
        if (!string.IsNullOrWhiteSpace(knownKey))
            person = await PickAsync(eventId, companyId, knownKey, now, ct);
        else
        {
            var e = email?.Trim().ToLowerInvariant();
            if (!System.Net.Mail.MailAddress.TryCreate(e, out _)) return ShopResult.Fail("Enter a valid e-mail address.");
            person = await _db.Participants.FirstOrDefaultAsync(p => p.EventId == eventId && p.Email == e, ct);
            if (person is not null && person.Role != ParticipantRole.Sponsor)
                return ShopResult.Fail($"{e} is already in the hub as {person.Role}.");
            if (person is null)
            {
                person = NewPerson(eventId, companyId, e!, name ?? e!, null, now);
                _db.Participants.Add(person);
                await _db.SaveChangesAsync(ct);
            }
        }
        if (person is null) return ShopResult.Fail("That person cannot be linked.");
        await _membership.LinkAsync(eventId, person.Id, companyId, isPrimary: person.SponsorCompanyId == companyId, ct: ct);
        person.SponsorCompanyId ??= companyId;
        await _db.SaveChangesAsync(ct);
        return ShopResult.Success($"{person.FullName} is linked to {company.PublicName ?? company.LegalName}.");
    }

    /// <summary>
    /// Remove a person from the company. The person is never deleted; if this was their primary company another of their
    /// companies becomes primary (or none), so they no longer act for it.
    /// </summary>
    public async Task<ShopResult> UnlinkPersonAsync(int eventId, string companyId, int participantId, CancellationToken ct = default)
    {
        if (!await _membership.UnlinkAsync(eventId, participantId, companyId, ct)) return ShopResult.Fail("That person is not linked.");
        var p = await _db.Participants.FirstOrDefaultAsync(x => x.Id == participantId && x.EventId == eventId, ct);
        if (p is not null && p.SponsorCompanyId == companyId)
        {
            p.SponsorCompanyId = await _db.SponsorCompanyContacts.Where(l => l.EventId == eventId && l.ParticipantId == participantId && l.SponsorCompanyId != companyId)
                .Select(l => l.SponsorCompanyId).FirstOrDefaultAsync(ct);
            await _db.SaveChangesAsync(ct);
        }
        return ShopResult.Success("Removed from the company. The person is kept in the hub.");
    }

    /// <summary>The CEH-owned company fields (e-conomic owns legal name, CVR, address, currency, VAT zone).</summary>
    /// <summary>
    /// 🔴 §1312 — the organizer sets a company's PUBLIC name (SoMe, Zoho, the event site, the app) from the companies table.
    /// Operator 2026-09-29: "remember to add a place in ceh, where i can edit this field" — the automatic rule is right ~9 in
    /// 10, and once set here nothing automatic changes it again. Copied to the sponsor record at once.
    /// </summary>
    public async Task<ShopResult> SetPublicNameAsync(int eventId, string companyId, string? publicName, CancellationToken ct = default)
    {
        var name = Blank(publicName);
        if (name is null) return ShopResult.Fail("The public name cannot be empty.");
        if (name.Length > 200) return ShopResult.Fail("The public name is too long (200 characters at most).");
        var c = await _db.ShopCompanies.FirstOrDefaultAsync(x => x.EventId == eventId && x.SponsorCompanyId == companyId, ct);
        if (c is null) return ShopResult.Fail("That company is not known.");
        c.PublicName = name;
        c.UpdatedAt = _clock.GetUtcNow();
        var info = await _db.SponsorInfos.IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.EventId == eventId && i.SponsorCompanyId == companyId, ct);
        if (info is not null) info.CompanyName = name;
        await _db.SaveChangesAsync(ct);
        return ShopResult.Success($"Public name saved: {name}.");
    }

    public async Task<ShopResult> UpdateCehDataAsync(
        int eventId, string companyId, string? publicName, string? website, string? linkedIn, string? twitter,
        string? billingReference, string? notes, CancellationToken ct = default, bool? soMeTagContacts = null)
    {
        var c = await _db.ShopCompanies.FirstOrDefaultAsync(x => x.EventId == eventId && x.SponsorCompanyId == companyId, ct);
        if (c is null) return ShopResult.Fail("That company is not known.");
        foreach (var (label, url) in new[] { ("Website", website), ("LinkedIn", linkedIn), ("X / Twitter", twitter) })
            if (!string.IsNullOrWhiteSpace(url) && !Uri.TryCreate(url.Contains("://") ? url.Trim() : "https://" + url.Trim(), UriKind.Absolute, out _))
                return ShopResult.Fail($"{label} is not a valid address.");
        c.PublicName = Blank(publicName);
        // 🔴 §1312 — the public name edited HERE is the one everything public uses (event site, app, SoMe, Zoho, uploads), for
        // every company: the CEH shop is the source, not Company Manager (operator 2026-09-29: "we are not pulling orders
        // from CM anymore, we use ceh new webshop"). Copied now so it shows at once; the order pull reads the same value.
        if (c.PublicName is not null)
        {
            var info = await _db.SponsorInfos.IgnoreQueryFilters()
                .FirstOrDefaultAsync(i => i.EventId == eventId && i.SponsorCompanyId == companyId, ct);
            if (info is not null) info.CompanyName = c.PublicName;
        }
        c.Website = HttpsUrl.Normalize(website);   // §1312 — always https://
        c.LinkedInUrl = HttpsUrl.Normalize(linkedIn);
        c.TwitterUrl = HttpsUrl.Normalize(twitter);
        // §1312 — the SAME fields the sponsor edits: copied to the sponsor record Zoho, the site and SoMe read.
        await ShopCompanyLinks.MirrorToInfoAsync(_db, eventId, companyId, c, ct);
        // §1312 — CodeTwo asked to be tagged only as a company on LinkedIn, not its signer / coordinators.
        if (soMeTagContacts is bool tag)
        {
            var info = await _db.SponsorInfos.IgnoreQueryFilters()
                .FirstOrDefaultAsync(i => i.EventId == eventId && i.SponsorCompanyId == companyId, ct);
            if (info is not null) info.SoMeTagContacts = tag;
        }
        c.BillingReference = Blank(billingReference);
        c.Notes = Blank(notes);
        c.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return ShopResult.Success("Company saved.");
    }

    // ------------------------------------------------------------------------------------------------------------
    // 2 · Find the registrant's company
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>The company-identifying domain of an address, or null for a public (gmail-type) address.</summary>
    public static string? CompanyDomainOf(string? email)
    {
        var at = email?.Trim().LastIndexOf('@') ?? -1;
        if (at < 1) return null;
        var domain = email!.Trim()[(at + 1)..].ToLowerInvariant();
        return domain.Length == 0 || PublicDomains.Contains(domain) ? null : domain;
    }

    private static string? HostOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var u = url.Trim();
        if (!u.Contains("://", StringComparison.Ordinal)) u = "https://" + u;
        if (!Uri.TryCreate(u, UriKind.Absolute, out var uri)) return null;
        var host = uri.Host.ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    /// <summary>
    /// Companies whose own domain equals the verified e-mail's domain — from the company's website, its company and
    /// invoice e-mail, its known people and its people in the hub. A public address never matches.
    /// </summary>
    public async Task<IReadOnlyList<ShopCompanyMatch>> FindByEmailDomainAsync(int eventId, string verifiedEmail, CancellationToken ct = default)
    {
        var domain = CompanyDomainOf(verifiedEmail);
        if (domain is null) return Array.Empty<ShopCompanyMatch>();
        // 🔒 §1312 — the organizers' own domain never identifies a sponsor: a sponsor's contacts can have test addresses
        // there, and a domain match lets the registrant JOIN without approval — so any organizer mailbox could join them.
        if (_mail?.OrganizerDomains().Contains(domain) == true) return Array.Empty<ShopCompanyMatch>();
        var suffix = "@" + domain;

        var companies = await _db.ShopCompanies.AsNoTracking().Where(c => c.EventId == eventId).ToListAsync(ct);
        var viaPeople = await _db.ShopKnownContacts.AsNoTracking()
            .Where(k => k.EventId == eventId && k.Email.EndsWith(suffix)).Select(k => k.SponsorCompanyId).ToListAsync(ct);
        var viaHub = await _db.SponsorCompanyContacts.AsNoTracking()
            .Where(l => l.EventId == eventId && l.Participant.Email.EndsWith(suffix)).Select(l => l.SponsorCompanyId).ToListAsync(ct);
        var ids = viaPeople.Concat(viaHub).ToHashSet(StringComparer.Ordinal);

        return companies
            .Where(c => ids.Contains(c.SponsorCompanyId)
                        || HostOf(c.Website) == domain
                        || CompanyDomainOf(c.CompanyEmail) == domain
                        || CompanyDomainOf(c.InvoiceEmail) == domain)
            .Select(c => new ShopCompanyMatch(c.SponsorCompanyId, c.LegalLabel, c.City))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The company with this CVR / VAT number (country prefix and spacing ignored), or null.</summary>
    public async Task<ShopCompanyMatch?> FindByCvrAsync(int eventId, string? cvr, CancellationToken ct = default)
    {
        var n = Erp.LiveEconomicErpClient.NormaliseCvr(cvr);
        if (n is null) return null;
        var c = await _db.ShopCompanies.AsNoTracking().FirstOrDefaultAsync(x => x.EventId == eventId && x.Cvr == n, ct);
        return c is null ? null : new ShopCompanyMatch(c.SponsorCompanyId, c.LegalLabel, c.City);
    }

    // ------------------------------------------------------------------------------------------------------------
    // 3 · The people dropdown
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>"j•••@company.com" — enough to recognise a colleague, not enough to harvest an address.</summary>
    public static string Mask(string email)
    {
        var e = email.Trim();
        var at = e.IndexOf('@');
        return at < 1 ? "•••" : $"{e[0]}•••{e[at..]}";
    }

    /// <summary>
    /// §1312 — a name shown to a registrant never carries a readable e-mail address: an address inside a stored name (a test
    /// user named "x (x@company.dk)") is masked like the address column.
    /// </summary>
    internal static string SafeName(string? name) =>
        System.Text.RegularExpressions.Regex.Replace(name ?? string.Empty, @"[^\s()<>,;]+@[^\s()<>,;]+", m => Mask(m.Value));

    /// <summary>
    /// The people already known for the company — its contacts in the hub first ("p:{id}"), then the imported people
    /// not yet in the hub ("k:{id}") — so the registrant picks a person instead of registering them again.
    /// </summary>
    public async Task<IReadOnlyList<ShopPersonOption>> PeopleForAsync(int eventId, string companyId, CancellationToken ct = default)
    {
        var hub = await _db.SponsorCompanyContacts.AsNoTracking()
            .Where(l => l.EventId == eventId && l.SponsorCompanyId == companyId && l.Participant.IsActive)
            .Select(l => new { l.ParticipantId, l.Participant.FullName, l.Participant.Email, l.IsSigner, l.IsEventCoordinator })
            .ToListAsync(ct);
        var inHub = hub.Select(h => h.Email.ToLowerInvariant()).ToHashSet();
        var known = await _db.ShopKnownContacts.AsNoTracking()
            .Where(k => k.EventId == eventId && k.SponsorCompanyId == companyId).ToListAsync(ct);
        return hub.Select(h => new ShopPersonOption($"p:{h.ParticipantId}", SafeName(h.FullName), Mask(h.Email), h.IsSigner, h.IsEventCoordinator))
            .Concat(known.Where(k => !inHub.Contains(k.Email.ToLowerInvariant()))
                .Select(k => new ShopPersonOption($"k:{k.Id}", SafeName(k.FullName), Mask(k.Email), k.IsSigner, k.IsEventCoordinator)))
            .OrderByDescending(o => o.IsSigner || o.IsEventCoordinator).ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Sign-in by default for a potential sponsor's people (operator 2026-09-27: <i>"sign-in must be enable by default for
    /// any potential sponsor and reservations. otherwise they cannot login and flip to order or make reservations"</i>).
    /// Called when someone asks for a login PIN: an address with no participant yet that is a KNOWN person of one or more
    /// companies becomes a sponsor contact of those companies right then — so the PIN they asked for can be sent.
    /// </summary>
    /// <remarks>
    /// <para>🔒 Just in time, never in bulk: the 110 imported people are not made participants up front (they would appear in
    /// organizer lists and headcounts). Only the person who asks, when they ask.</para>
    /// <para>🔒 No mail except the PIN they requested: a potential sponsor gets a HIDDEN sponsor record first, which keeps it
    /// out of the welcome (§1309.0). For a company that already IS a sponsor the person joins as a plain contact — its
    /// signer / coordinator stay as they are. On a potential sponsor a role (signer / coordinator) the old webshop gave them
    /// is applied only when nobody in the hub holds it yet.</para>
    /// <para>Returns true when a participant was created.</para>
    /// </remarks>
    public async Task<bool> EnsureKnownPersonCanSignInAsync(int eventId, string? email, CancellationToken ct = default)
    {
        var e = email?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(e) || !e.Contains('@')) return false;
        if (await _db.Participants.AnyAsync(p => p.EventId == eventId && p.Email == e, ct)) return false;
        var known = await _db.ShopKnownContacts.Where(k => k.EventId == eventId && k.Email == e).ToListAsync(ct);
        if (known.Count == 0) return false;

        var now = _clock.GetUtcNow();
        var companies = await _db.ShopCompanies.Where(c => c.EventId == eventId && known.Select(k => k.SponsorCompanyId).Contains(c.SponsorCompanyId))
            .ToListAsync(ct);
        if (companies.Count == 0) return false;
        var primary = companies.OrderBy(c => c.State == ShopCompanyState.Sponsor ? 0 : 1).First();
        var person = NewPerson(eventId, primary.SponsorCompanyId, e, known[0].FullName, known[0].Phone, now);
        _db.Participants.Add(person);
        await _db.SaveChangesAsync(ct);

        foreach (var company in companies)
        {
            await EnsureHiddenSponsorRecordAsync(eventId, company, now, ct);
            var k = known.First(x => x.SponsorCompanyId == company.SponsorCompanyId);
            var potential = company.State != ShopCompanyState.Sponsor;
            var links = await _db.SponsorCompanyContacts.Where(l => l.EventId == eventId && l.SponsorCompanyId == company.SponsorCompanyId).ToListAsync(ct);
            var signer = potential && k.IsSigner && !links.Any(l => l.IsSigner);
            var coordinator = potential && k.IsEventCoordinator && !links.Any(l => l.IsEventCoordinator);
            await _membership.LinkAsync(eventId, person.Id, company.SponsorCompanyId, isSigner: signer, isEventCoordinator: coordinator,
                isPrimary: company.SponsorCompanyId == primary.SponsorCompanyId, ct: ct);
            if (company.SponsorCompanyId == primary.SponsorCompanyId) { person.IsSigner = signer; person.IsEventCoordinator = coordinator; }
        }
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>§1309 C — is this address one of the company's people (known from e-conomic / the old webshop, or a contact)?</summary>
    public async Task<bool> IsKnownPersonOfAsync(int eventId, string companyId, string email, CancellationToken ct = default)
    {
        var e = email.Trim().ToLowerInvariant();
        return await _db.ShopKnownContacts.AnyAsync(k => k.EventId == eventId && k.SponsorCompanyId == companyId && k.Email == e, ct)
            || await _db.SponsorCompanyContacts.AnyAsync(l => l.EventId == eventId && l.SponsorCompanyId == companyId && l.Participant.Email == e, ct);
    }

    /// <summary>
    /// §1312 — the address an organizer sent an OFFER to is a known person of that company, so its self-service
    /// registration joins the company (and finds the offer in the cart) instead of waiting in the join-request queue.
    /// Operator 2026-09-27: "it must be 100% clear that next step is that they register (self-service) and can complete the
    /// order themselves". A lookup row only: no participant, no role, no mail. An address already known is left as it is.
    /// </summary>
    public async Task RememberOfferRecipientAsync(int eventId, string companyId, string email, CancellationToken ct = default)
    {
        var e = email.Trim().ToLowerInvariant();
        if (!System.Net.Mail.MailAddress.TryCreate(e, out _)) return;
        if (await IsKnownPersonOfAsync(eventId, companyId, e, ct)) return;
        _db.ShopKnownContacts.Add(new ShopKnownContact
        {
            EventId = eventId, SponsorCompanyId = companyId, Email = e, FullName = e[..e.IndexOf('@')],
            Source = "Offer", ImportedAt = _clock.GetUtcNow(),
        });
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>True when nobody of the company is in the hub yet — only then may a joiner pick its signer and coordinator.</summary>
    public async Task<bool> HasNoHubContactsAsync(int eventId, string companyId, CancellationToken ct = default) =>
        !await _db.SponsorCompanyContacts.AnyAsync(l => l.EventId == eventId && l.SponsorCompanyId == companyId, ct);

    // ------------------------------------------------------------------------------------------------------------
    // 4 · Join
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A VERIFIED registrant joins an existing company as a contact. When nobody of the company is in the hub yet, the
    /// signer and coordinator they picked (themselves "me", or a known person) are set up too; otherwise the company's
    /// existing roles stay as they are. The company's data is never changed. Returns the registrant's participant id.
    /// </summary>
    public async Task<(ShopResult Result, int? ParticipantId)> JoinAsync(
        int eventId, string companyId, string verifiedEmail, string fullName, string? phone,
        string? signerKey, string? coordinatorKey, CancellationToken ct = default,
        bool claimSigner = false, bool claimCoordinator = false)
    {
        var company = await _db.ShopCompanies.FirstOrDefaultAsync(c => c.EventId == eventId && c.SponsorCompanyId == companyId, ct);
        if (company is null) return (ShopResult.Fail("That company is not known."), null);
        var email = verifiedEmail.Trim().ToLowerInvariant();
        var now = _clock.GetUtcNow();

        var existing = await _db.Participants.FirstOrDefaultAsync(p => p.EventId == eventId && p.Email == email, ct);
        if (existing is not null && existing.Role != ParticipantRole.Sponsor)
            return (ShopResult.Fail("This address is already in the hub in another role. Please contact the organizers."), null);

        var firstInHub = await HasNoHubContactsAsync(eventId, companyId, ct);
        await EnsureHiddenSponsorRecordAsync(eventId, company, now, ct);

        var me = existing ?? NewPerson(eventId, companyId, email, fullName, phone, now);
        if (existing is null) { _db.Participants.Add(me); await _db.SaveChangesAsync(ct); }
        if (me.SponsorCompanyId is null) me.SponsorCompanyId = companyId;

        bool meSigner = false, meCoord = false;
        if (firstInHub)
        {
            meSigner = signerKey is null or "me";
            meCoord = coordinatorKey is null or "me";
            var signer = await PickAsync(eventId, companyId, signerKey, now, ct);
            var coord = await PickAsync(eventId, companyId, coordinatorKey, now, ct);
            if (signer is not null && coord is not null && signer.Id == coord.Id)
                await _membership.LinkAsync(eventId, signer.Id, companyId, isSigner: true, isEventCoordinator: true, isPrimary: signer.SponsorCompanyId == companyId, ct: ct);
            else
            {
                if (signer is not null) await _membership.LinkAsync(eventId, signer.Id, companyId, isSigner: true, isEventCoordinator: false, isPrimary: signer.SponsorCompanyId == companyId, ct: ct);
                if (coord is not null) await _membership.LinkAsync(eventId, coord.Id, companyId, isSigner: false, isEventCoordinator: true, isPrimary: coord.SponsorCompanyId == companyId, ct: ct);
            }
        }
        else
        {
            // §1312 — a later joiner ticks their own role(s) (operator 2026-09-27: "i am missing the role here, either signer or
            // event coordinator or both - tick off"). Added to THIS person only; colleagues keep theirs.
            meSigner = claimSigner;
            meCoord = claimCoordinator;
        }
        await _membership.LinkAsync(eventId, me.Id, companyId, isSigner: meSigner, isEventCoordinator: meCoord,
            isPrimary: me.SponsorCompanyId == companyId, ct: ct);
        await _db.SaveChangesAsync(ct);
        return (ShopResult.Success($"You are now a contact of {company.PublicName ?? company.LegalName}."), me.Id);
    }

    /// <summary>
    /// §1312 — a KNOWN person (from e-conomic) picked on an order as signer / coordinator: made a hub contact of the company
    /// now, linked with the roles e-conomic gives them. Returns the participant id, or null when the row is unknown or the
    /// address belongs to a non-sponsor in the hub. Nobody is mailed by this.
    /// </summary>
    public async Task<int?> KnownToContactAsync(int eventId, string companyId, int knownId, CancellationToken ct = default)
    {
        var company = await _db.ShopCompanies.FirstOrDefaultAsync(c => c.EventId == eventId && c.SponsorCompanyId == companyId, ct);
        if (company is null) return null;
        var known = await _db.ShopKnownContacts.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == knownId && x.EventId == eventId && x.SponsorCompanyId == companyId, ct);
        if (known is null) return null;
        var now = _clock.GetUtcNow();
        var person = await PickAsync(eventId, companyId, "k:" + knownId, now, ct);
        if (person is null) return null;
        await EnsureHiddenSponsorRecordAsync(eventId, company, now, ct);
        var linked = await _db.SponsorCompanyContacts.AnyAsync(l => l.EventId == eventId && l.SponsorCompanyId == companyId && l.ParticipantId == person.Id, ct);
        if (!linked)
            await _membership.LinkAsync(eventId, person.Id, companyId, isSigner: known.IsSigner, isEventCoordinator: known.IsEventCoordinator,
                isPrimary: person.SponsorCompanyId == companyId, ct: ct);
        await _db.SaveChangesAsync(ct);
        return person.Id;
    }

    /// <summary>A person picked in the dropdown: an existing hub contact, or a known person made a participant now.</summary>
    private async Task<Participant?> PickAsync(int eventId, string companyId, string? key, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key) || key == "me") return null;
        if (key.StartsWith("p:", StringComparison.Ordinal) && int.TryParse(key[2..], out var pid))
            return await _db.Participants.FirstOrDefaultAsync(p => p.Id == pid && p.EventId == eventId
                && _db.SponsorCompanyContacts.Any(l => l.ParticipantId == pid && l.SponsorCompanyId == companyId), ct);
        if (key.StartsWith("k:", StringComparison.Ordinal) && int.TryParse(key[2..], out var kid))
        {
            var k = await _db.ShopKnownContacts.FirstOrDefaultAsync(x => x.Id == kid && x.EventId == eventId && x.SponsorCompanyId == companyId, ct);
            if (k is null) return null;
            var p = await _db.Participants.FirstOrDefaultAsync(x => x.EventId == eventId && x.Email == k.Email, ct);
            if (p is not null) return p.Role == ParticipantRole.Sponsor ? p : null;
            p = NewPerson(eventId, companyId, k.Email, k.FullName, k.Phone, now);
            _db.Participants.Add(p);
            await _db.SaveChangesAsync(ct);
            return p;
        }
        return null;
    }

    private static Participant NewPerson(int eventId, string companyId, string email, string name, string? phone, DateTimeOffset now) => new()
    {
        EventId = eventId, Email = email, FullName = string.IsNullOrWhiteSpace(name) ? email : name.Trim(),
        Phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim(), Role = ParticipantRole.Sponsor,
        SponsorCompanyId = companyId, IsActive = true, LifecycleState = ParticipantLifecycleState.Active, CreatedAt = now,
    };

    /// <summary>
    /// A company that gets people in the hub needs its sponsor record — HIDDEN (IsSponsor = false) unless it already is a
    /// sponsor. That record is what keeps a potential sponsor out of the welcome, tasks and sponsor lists (§1309.0).
    /// </summary>
    private async Task EnsureHiddenSponsorRecordAsync(int eventId, ShopCompany company, DateTimeOffset now, CancellationToken ct)
    {
        if (await _db.SponsorInfos.IgnoreQueryFilters().AnyAsync(s => s.EventId == eventId && s.SponsorCompanyId == company.SponsorCompanyId, ct))
            return;
        _db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = eventId, SponsorCompanyId = company.SponsorCompanyId, CompanyName = company.PublicLabel,
            IsSponsor = company.State == ShopCompanyState.Sponsor, CreatedAt = now,
        });
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Matched only by CVR (or the registrant's address does not prove the domain): store a request and tell the
    /// organizers. The registrant gets NO access until an organizer approves.
    /// </summary>
    public async Task<ShopResult> RequestJoinAsync(
        int eventId, string companyId, string verifiedEmail, string fullName, string? phone, string matchedBy, CancellationToken ct = default)
    {
        var email = verifiedEmail.Trim().ToLowerInvariant();
        var company = await _db.ShopCompanies.AsNoTracking().FirstOrDefaultAsync(c => c.EventId == eventId && c.SponsorCompanyId == companyId, ct);
        if (company is null) return ShopResult.Fail("That company is not known.");
        if (!await _db.ShopJoinRequests.AnyAsync(r => r.EventId == eventId && r.SponsorCompanyId == companyId && r.Email == email && r.Approved == null, ct))
        {
            _db.ShopJoinRequests.Add(new ShopJoinRequest
            {
                EventId = eventId, SponsorCompanyId = companyId, Email = email,
                FullName = string.IsNullOrWhiteSpace(fullName) ? email : fullName.Trim(), Phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim(),
                MatchedBy = matchedBy, RequestedAt = _clock.GetUtcNow(),
            });
            await _db.SaveChangesAsync(ct);
            if (_mail is not null)
                await _mail.NotifyOrganizersAsync(eventId, $"Join request: {fullName} → {company.OrganizerLabel}",
                    $"<p><b>{System.Net.WebUtility.HtmlEncode(fullName)}</b> ({System.Net.WebUtility.HtmlEncode(email)}) asks to join "
                    + $"<b>{System.Net.WebUtility.HtmlEncode(company.OrganizerLabel)}</b> (matched by {matchedBy}). "
                    + "Their e-mail does not prove the company's domain, so they have no access until you approve.</p>"
                    + $"<p><a href=\"{_mail.HubOrigin}/Organizer/ShopCompanies\">Approve or reject</a></p>", ct);
        }
        return ShopResult.Success($"Your request to join {company.PublicName ?? company.LegalName} is with the organizers. "
                                  + "When it is approved you can sign in with this e-mail address.");
    }

    /// <summary>An organizer approves (the registrant becomes a contact — no roles, no mail) or rejects a join request.</summary>
    public async Task<ShopResult> DecideAsync(int eventId, int requestId, bool approve, string byEmail, string? note, CancellationToken ct = default)
    {
        var r = await _db.ShopJoinRequests.FirstOrDefaultAsync(x => x.EventId == eventId && x.Id == requestId && x.Approved == null, ct);
        if (r is null) return ShopResult.Fail("That request is already decided or gone.");
        if (approve)
        {
            var hasContacts = !await HasNoHubContactsAsync(eventId, r.SponsorCompanyId, ct);
            // Approved as a plain contact; with nobody in the hub yet they also become signer + coordinator so the company
            // can act. Either way: no welcome (a potential sponsor gets none — §1309.0).
            var (res, _) = await JoinAsync(eventId, r.SponsorCompanyId, r.Email, r.FullName, r.Phone,
                hasContacts ? "none" : "me", hasContacts ? "none" : "me", ct);
            if (!res.Ok) return res;
        }
        r.Approved = approve;
        r.DecidedAt = _clock.GetUtcNow();
        r.DecidedByEmail = byEmail;
        r.DecisionNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        await _db.SaveChangesAsync(ct);
        return ShopResult.Success(approve ? $"{r.Email} can now sign in as a contact of the company." : "Request rejected.");
    }
}
