using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Erp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Webshop;

/// <summary>What one role sync did.</summary>
public sealed record ShopErpRoleSyncResult(int Companies, int PeopleAdded, int RolesChanged, IReadOnlyList<string> Problems, int Warnings = 0);

/// <summary>
/// 🔴 §1312 — e-conomic is THE source of every company's people and their signer / event-coordinator roles (operator
/// 2026-09-30: "in erp, we have the people under each company as contact" · "we dont use company manager anymore" · "i
/// need you to change that"). Company Manager is no longer read or written.
/// </summary>
/// <remarks>
/// <para>Per company with an e-conomic customer number (withdrawn companies skipped):</para>
/// <list type="bullet">
/// <item>The billing identity follows e-conomic (<see cref="ShopCompanyService.ApplyErpBilling"/>).</item>
/// <item>The company's <b>known people</b> (<see cref="ShopKnownContact"/>, Source "Economic") ARE e-conomic's contacts with
/// their roles — the list the registration, company page and order dropdowns offer. Nobody is created or mailed by it.</item>
/// <item>A person already LINKED to the company who is an e-conomic contact carries e-conomic's roles (an edit there wins).</item>
/// <item><b>Only for a SIGNED sponsor</b> (and a company registered in CEH, whose e-conomic contacts are its CEH
/// registrants): an e-conomic contact not in the hub yet is created and linked with its roles. A POTENTIAL sponsor's
/// contacts are not — operator 2026-09-30: "these contact must be for old sponsors in erp, potential sponsor contact. but
/// they are not a sponsor today so they should not get welcome".</item>
/// </list>
/// <para>🔒 <b>A role is never removed silently</b> (operator 2026-09-30: "then you must warn me of missing roles"). For a
/// signed sponsor the organizers are WARNED — on the company page and by one mail when the finding is new — when CEH has a
/// signer / coordinator who is not that in e-conomic, when e-conomic names no signer or no coordinator, and when an
/// e-conomic contact carries the name of a linked person under another e-mail (never created twice).</para>
/// <para>🔒 An EMPTY contact list from e-conomic changes nothing. Read-only toward e-conomic.</para>
/// </remarks>
public sealed class ShopErpRoleSyncService
{
    public const string KnownSource = "Economic";

    private readonly CommunityHubDbContext _db;
    private readonly IEconomicContactAdminClient _contacts;
    private readonly SponsorCompanyMembershipService _membership;
    private readonly TimeProvider _clock;
    private readonly ILogger<ShopErpRoleSyncService>? _log;
    private readonly IEconomicErpClient? _erp;
    private readonly ShopMailer? _mail;

    public ShopErpRoleSyncService(
        CommunityHubDbContext db, IEconomicContactAdminClient contacts, SponsorCompanyMembershipService membership,
        TimeProvider clock, ILogger<ShopErpRoleSyncService>? log = null, IEconomicErpClient? erp = null, ShopMailer? mail = null)
    {
        _erp = erp;
        _db = db;
        _contacts = contacts;
        _membership = membership;
        _clock = clock;
        _log = log;
        _mail = mail;
    }

    /// <param name="onlyErpCustomerNumber">Only the company with this e-conomic customer (the e-conomic contacts page's save).</param>
    public async Task<ShopErpRoleSyncResult> RunAsync(int eventId, CancellationToken ct = default, int? onlyErpCustomerNumber = null)
    {
        var withdrawn = (await _db.SponsorInfos.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.EventId == eventId && s.Status == SponsorStatus.Withdrawn)
                .Select(s => s.SponsorCompanyId).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        var only = onlyErpCustomerNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var companies = (await _db.ShopCompanies
                .Where(c => c.EventId == eventId && c.ErpCustomerNumber != null && (only == null || c.ErpCustomerNumber == only))
                .ToListAsync(ct))
            .Where(c => !withdrawn.Contains(c.SponsorCompanyId)).ToList();
        int added = 0, changed = 0, warned = 0;
        var problems = new List<string>();
        var newWarnings = new List<(ShopCompany Company, string Warning)>();
        var now = _clock.GetUtcNow();

        foreach (var company in companies)
        {
            if (!int.TryParse(company.ErpCustomerNumber, out var customerNumber)) continue;

            if (_erp is not null)
            {
                try
                {
                    var erp = await _erp.GetCustomerAsync(company.ErpCustomerNumber!, ct);
                    if (erp is not null && ShopCompanyService.ApplyErpBilling(company, erp.Name, erp.CorporateIdentificationNumber,
                            erp.Address, erp.Zip, erp.City, erp.Country, erp.Email, erp.Currency,
                            int.TryParse(erp.VatZone, out var zone) ? zone : null))
                    {
                        await _db.SaveChangesAsync(ct);
                        changed++;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    problems.Add($"{company.LegalName}: e-conomic customer could not be read ({ex.Message}).");
                }
            }
            IReadOnlyList<EconomicContactRow> rows;
            try { rows = await _contacts.ListContactsAsync(customerNumber, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                problems.Add($"{company.LegalName}: e-conomic contacts could not be read ({ex.Message}).");
                continue;
            }
            var erpPeople = rows.Where(r => !string.IsNullOrWhiteSpace(r.Email))
                .Select(r => (Row: r, Email: r.Email!.Trim().ToLowerInvariant(),
                    Signer: EconomicContactNotesRoles.IsSigner(r.Notes), Coordinator: EconomicContactNotesRoles.IsEventCoordinator(r.Notes)))
                .GroupBy(p => p.Email).Select(g => g.First()).ToList();
            if (erpPeople.Count == 0) continue;   // 🔒 an empty answer is not "everybody left"

            // The known people = e-conomic's contacts (replacing the retired Company Manager list).
            var known = await _db.ShopKnownContacts
                .Where(k => k.EventId == eventId && k.SponsorCompanyId == company.SponsorCompanyId
                            && (k.Source == KnownSource || k.Source == "CompanyManager"))
                .ToListAsync(ct);
            if (!SameKnown(known, erpPeople.Select(p => (p.Email, p.Row.Name, p.Signer, p.Coordinator))))
            {
                _db.ShopKnownContacts.RemoveRange(known);
                foreach (var p in erpPeople)
                    _db.ShopKnownContacts.Add(new ShopKnownContact
                    {
                        EventId = eventId, SponsorCompanyId = company.SponsorCompanyId, Email = p.Email,
                        FullName = string.IsNullOrWhiteSpace(p.Row.Name) ? p.Email : p.Row.Name.Trim(), Phone = p.Row.Phone,
                        IsSigner = p.Signer, IsEventCoordinator = p.Coordinator, Source = KnownSource, ImportedAt = now,
                    });
                await _db.SaveChangesAsync(ct);
            }

            var fullSync = company.State == ShopCompanyState.Sponsor || company.Source == ShopCompanySource.Registered;
            var linkedPeople = await _db.SponsorCompanyContacts
                .Where(l => l.EventId == eventId && l.SponsorCompanyId == company.SponsorCompanyId)
                .Join(_db.Participants, l => l.ParticipantId, p => p.Id, (l, p) => new { Link = l, Person = p })
                .ToListAsync(ct);
            var warnings = new List<string>();
            var nameMatched = new HashSet<int>();

            foreach (var p in erpPeople)
            {
                var person = await _db.Participants.FirstOrDefaultAsync(x => x.EventId == eventId && x.Email == p.Email, ct);

                // 🔴 §1312 — the e-conomic CONTACT NUMBER is the identity (§895): a contact whose e-mail was changed in e-conomic is
                // the same ACTIVE linked person in CEH, renamed in place — never a second person (operator 2026-10-01: "sync any
                // changes from erp to ceh, including billing fields, contacts etc - it must compare the fields and update if
                // different"). The new address already belonging to someone else in CEH is warned, never taken over.
                if (person is null && linkedPeople.FirstOrDefault(x => x.Person.IsActive && x.Link.ErpContactNumber == p.Row.ContactNumber
                                                                      && x.Person.Role == ParticipantRole.Sponsor)?.Person is { } byNumber)
                {
                    person = byNumber;
                    person.Email = p.Email;
                    changed++;
                }
                else if (person is not null && linkedPeople.FirstOrDefault(x => x.Person.IsActive && x.Person.Id != person.Id
                             && x.Link.ErpContactNumber == p.Row.ContactNumber && x.Person.Role == ParticipantRole.Sponsor)?.Person is { } other
                         && !string.Equals(other.Email, p.Email, StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add($"e-conomic contact {p.Row.ContactNumber} ({p.Row.Name}) now has the e-mail {p.Email}, which CEH already has "
                                 + $"for {person.FullName}; CEH still has {other.FullName} as {other.Email} — merge or deactivate one of them.");
                }
                var isLinked = person is not null && linkedPeople.Any(x => x.Person.Id == person.Id);
                if (!isLinked && !fullSync) continue;   // a potential sponsor's contact stays a known person only

                if (person is null)
                {
                    var sameName = linkedPeople.FirstOrDefault(x => x.Person.IsActive && SameName(x.Person.FullName, p.Row.Name));
                    if (sameName is not null)
                    {
                        nameMatched.Add(sameName.Person.Id);
                        warnings.Add($"e-conomic has {p.Row.Name} as {p.Email}, but CEH has {sameName.Person.FullName} as "
                                     + $"{sameName.Person.Email} — the same person? Correct the e-mail in e-conomic or in CEH.");
                        continue;
                    }
                    person = new Participant
                    {
                        EventId = eventId, Email = p.Email, FullName = string.IsNullOrWhiteSpace(p.Row.Name) ? p.Email : p.Row.Name.Trim(),
                        Phone = p.Row.Phone, Role = ParticipantRole.Sponsor, SponsorCompanyId = company.SponsorCompanyId,
                        IsSigner = p.Signer, IsEventCoordinator = p.Coordinator, ErpContactNumber = p.Row.ContactNumber,
                        IsActive = true, LifecycleState = ParticipantLifecycleState.Active, CreatedAt = now,
                    };
                    _db.Participants.Add(person);
                    await _db.SaveChangesAsync(ct);
                    added++;
                }

                var link = linkedPeople.FirstOrDefault(x => x.Person.Id == person.Id)?.Link;
                if (link is null || link.IsSigner != p.Signer || link.IsEventCoordinator != p.Coordinator || link.ErpContactNumber != p.Row.ContactNumber)
                {
                    await _membership.LinkAsync(eventId, person.Id, company.SponsorCompanyId, isSigner: p.Signer,
                        isEventCoordinator: p.Coordinator, erpContactNumber: p.Row.ContactNumber,
                        isPrimary: person.SponsorCompanyId == company.SponsorCompanyId, ct: ct);
                    // The person-level flags describe the PRIMARY company (the SoMe tags and older readers use them).
                    if (person.SponsorCompanyId == company.SponsorCompanyId)
                    {
                        person.IsSigner = p.Signer;
                        person.IsEventCoordinator = p.Coordinator;
                    }
                    changed++;
                }

                // 🔴 §1312 — the person's NAME and PHONE follow the e-conomic contact too, a blank phone included (operator
                // 2026-10-01: "we prefer phone number pe contact, not company number"). Only a sponsor row: a speaker / organizer
                // who also serves a company keeps the name their own role owns.
                if (person.Role == ParticipantRole.Sponsor)
                {
                    var name = string.IsNullOrWhiteSpace(p.Row.Name) ? null : p.Row.Name.Trim();
                    if (name is not null && name != person.FullName) { person.FullName = name; changed++; }
                    var phone = string.IsNullOrWhiteSpace(p.Row.Phone) ? null : p.Row.Phone.Trim();
                    if (phone != (string.IsNullOrWhiteSpace(person.Phone) ? null : person.Phone.Trim())) { person.Phone = phone; changed++; }
                }
            }
            await _db.SaveChangesAsync(ct);

            if (fullSync)
            {
                // 🔒 Never stripped — warned. A role CEH holds that e-conomic does not back.
                var erpEmails = erpPeople.Select(p => p.Email).ToHashSet(StringComparer.Ordinal);
                // An INACTIVE person (deactivated in CEH) is never warned about (operator 2026-09-30, on two who had left).
                foreach (var x in linkedPeople.Where(x => x.Person.IsActive && (x.Link.IsSigner || x.Link.IsEventCoordinator) && !nameMatched.Contains(x.Person.Id)
                                                          && !erpEmails.Contains((x.Person.Email ?? "").Trim().ToLowerInvariant())))
                    warnings.Add($"{x.Person.FullName} ({x.Person.Email}) is {Roles(x.Link.IsSigner, x.Link.IsEventCoordinator)} in CEH "
                                 + "but not a contact in e-conomic — add them there with the role, or remove the role in CEH.");
                if (!erpPeople.Any(p => p.Signer)) warnings.Add("e-conomic names no signer (Role:1) for this company.");
                if (!erpPeople.Any(p => p.Coordinator)) warnings.Add("e-conomic names no event coordinator (Role:2) for this company.");
            }

            var warning = warnings.Count == 0 ? null : string.Join("\n", warnings);
            if (warning is not null) warned++;
            if (!string.Equals(warning, company.ErpRoleWarning, StringComparison.Ordinal))
            {
                if (warning is not null) newWarnings.Add((company, warning));
                company.ErpRoleWarning = warning;
                await _db.SaveChangesAsync(ct);
            }
        }

        await NotifyAsync(eventId, newWarnings, ct);
        if (changed > 0 || added > 0 || newWarnings.Count > 0)
            _log?.LogInformation("§1312 people and roles from e-conomic (event {EventId}): {Added} person(s) added, {Changed} link(s) changed, {Warnings} new warning(s).",
                eventId, added, changed, newWarnings.Count);
        return new ShopErpRoleSyncResult(companies.Count, added, changed, problems, warned);
    }

    /// <summary>One organizer mail per run, listing only the companies whose warning is NEW — never every hour.</summary>
    private async Task NotifyAsync(int eventId, List<(ShopCompany Company, string Warning)> fresh, CancellationToken ct)
    {
        if (_mail is null || fresh.Count == 0) return;
        try
        {
            string E(string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);
            var body = new System.Text.StringBuilder("<p>People and roles in CEH and e-conomic do not agree. Nothing was removed — please check:</p>");
            foreach (var (c, w) in fresh)
            {
                body.Append($"<p><b><a href=\"{_mail.HubOrigin}/Organizer/ShopCompany?Id={Uri.EscapeDataString(c.SponsorCompanyId)}\">{E(c.LegalName)}</a></b> "
                            + $"(e-conomic {E(c.ErpCustomerNumber)})</p><ul>");
                foreach (var line in w.Split('\n')) body.Append($"<li>{E(line)}</li>");
                body.Append("</ul>");
            }
            await _mail.NotifyOrganizersAsync(eventId, $"Roles to check: {fresh.Count} compan{(fresh.Count == 1 ? "y" : "ies")}", body.ToString(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.LogWarning(ex, "§1312 the role-warning notice could not be sent.");
        }
    }

    private static string Roles(bool signer, bool coordinator) =>
        signer && coordinator ? "signer and event coordinator" : signer ? "signer" : "event coordinator";

    internal static bool SameName(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
        && string.Equals(string.Join(' ', a.Split(' ', StringSplitOptions.RemoveEmptyEntries)),
                         string.Join(' ', b.Split(' ', StringSplitOptions.RemoveEmptyEntries)), StringComparison.OrdinalIgnoreCase);

    private static bool SameKnown(List<ShopKnownContact> known, IEnumerable<(string Email, string Name, bool Signer, bool Coordinator)> erp)
    {
        var want = erp.ToList();
        return known.Count == want.Count && known.All(k => k.Source == KnownSource)
               && want.All(w => known.Any(k => k.Email == w.Email && k.IsSigner == w.Signer && k.IsEventCoordinator == w.Coordinator
                                               && k.FullName == (string.IsNullOrWhiteSpace(w.Name) ? w.Email : w.Name.Trim())));
    }
}
