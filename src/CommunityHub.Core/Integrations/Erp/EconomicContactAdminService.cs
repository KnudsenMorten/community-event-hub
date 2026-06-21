using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations.Erp;

/// <summary>
/// Orchestrates the organizer e-conomic contact-CRUD GUI (REQUIREMENTS §6): the
/// live e-conomic write (name/email/phone via <see cref="IEconomicContactAdminClient"/>)
/// plus the hub-side role + notes annotation kept in lock-step. Listing merges the
/// live contacts with their annotations; create/update/delete write both sides.
/// </summary>
public sealed class EconomicContactAdminService
{
    private readonly IEconomicContactAdminClient _client;
    private readonly CommunityHubDbContext _db;

    public EconomicContactAdminService(
        IEconomicContactAdminClient client, CommunityHubDbContext db)
    {
        _client = client;
        _db = db;
    }

    public bool CanWrite => _client.CanWrite;

    public sealed record ContactView(
        int ContactNumber, string Name, string? Email, string? Phone,
        ErpContactRole Role, string? Notes);

    public Task<IReadOnlyList<EconomicCustomerRow>> ListCustomersAsync(
        string? search, CancellationToken ct = default) =>
        _client.ListCustomersAsync(search, ct);

    /// <summary>Live contacts for a customer, merged with their hub role + notes.</summary>
    public async Task<IReadOnlyList<ContactView>> ListContactsAsync(
        int customerNumber, CancellationToken ct = default)
    {
        var live = await _client.ListContactsAsync(customerNumber, ct);
        var notes = await _db.EconomicContactAnnotations.AsNoTracking()
            .Where(a => a.CustomerNumber == customerNumber)
            .ToDictionaryAsync(a => a.ContactNumber, a => a, ct);

        return live.Select(c =>
        {
            notes.TryGetValue(c.ContactNumber, out var a);
            return new ContactView(c.ContactNumber, c.Name, c.Email, c.Phone,
                a?.Role ?? ErpContactRole.Contact, a?.Notes);
        }).ToList();
    }

    /// <summary>Create a contact (e-conomic) + persist its role/notes annotation.</summary>
    public async Task<int> CreateAsync(
        int customerNumber, EconomicContactInput input, ErpContactRole role,
        string? notes, string? byEmail, CancellationToken ct = default)
    {
        var contactNumber = await _client.CreateContactAsync(customerNumber, input, ct);
        await UpsertAnnotationAsync(customerNumber, contactNumber, role, notes, byEmail, ct);
        return contactNumber;
    }

    /// <summary>Update a contact (e-conomic) + its role/notes annotation.</summary>
    public async Task UpdateAsync(
        int customerNumber, int contactNumber, EconomicContactInput input,
        ErpContactRole role, string? notes, string? byEmail, CancellationToken ct = default)
    {
        await _client.UpdateContactAsync(customerNumber, contactNumber, input, ct);
        await UpsertAnnotationAsync(customerNumber, contactNumber, role, notes, byEmail, ct);
    }

    /// <summary>Delete a contact (e-conomic) + drop its annotation.</summary>
    public async Task DeleteAsync(
        int customerNumber, int contactNumber, CancellationToken ct = default)
    {
        await _client.DeleteContactAsync(customerNumber, contactNumber, ct);
        var ann = await _db.EconomicContactAnnotations
            .FirstOrDefaultAsync(a => a.CustomerNumber == customerNumber
                                      && a.ContactNumber == contactNumber, ct);
        if (ann is not null)
        {
            _db.EconomicContactAnnotations.Remove(ann);
            await _db.SaveChangesAsync(ct);
        }
    }

    private async Task UpsertAnnotationAsync(
        int customerNumber, int contactNumber, ErpContactRole role,
        string? notes, string? byEmail, CancellationToken ct)
    {
        var ann = await _db.EconomicContactAnnotations
            .FirstOrDefaultAsync(a => a.CustomerNumber == customerNumber
                                      && a.ContactNumber == contactNumber, ct);
        if (ann is null)
        {
            ann = new EconomicContactAnnotation
            {
                CustomerNumber = customerNumber,
                ContactNumber = contactNumber,
            };
            _db.EconomicContactAnnotations.Add(ann);
        }
        ann.Role = role;
        ann.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        ann.UpdatedByEmail = byEmail;
        ann.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }
}
