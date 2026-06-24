namespace CommunityHub.Core.Integrations.Erp;

/// <summary>
/// Orchestrates the e-conomic contact-CRUD used by the organizer admin and the
/// sponsor self-service (REQUIREMENTS §6). e-conomic is the MASTER: a contact's
/// role (Signer / Event Coordinator, both allowed) is encoded in the e-conomic
/// contact <c>notes</c> field as <c>Role:1,2</c> via
/// <see cref="EconomicContactNotesRoles"/> — no hub-side role store. Listing reads
/// + parses notes; create/update write name/email/phone + the role-encoded notes.
/// </summary>
public sealed class EconomicContactAdminService
{
    private readonly IEconomicContactAdminClient _client;

    public EconomicContactAdminService(IEconomicContactAdminClient client)
    {
        _client = client;
    }

    public bool CanWrite => _client.CanWrite;

    public sealed record ContactView(
        int ContactNumber, string Name, string? Email, string? Phone,
        bool IsSigner, bool IsEventCoordinator, string? Notes)
    {
        /// <summary>The e-conomic Role string, e.g. "Role:1,2" (empty when none).</summary>
        public string RoleDisplay =>
            EconomicContactNotesRoles.WithRoles(null, IsSigner, IsEventCoordinator) is { Length: > 0 } r ? r : "—";
    }

    /// <summary>Customers in the given group (1 = sponsors), optionally text-filtered.</summary>
    public Task<IReadOnlyList<EconomicCustomerRow>> ListCustomersAsync(
        string? search, int? customerGroup = null, CancellationToken ct = default) =>
        _client.ListCustomersAsync(search, customerGroup, ct);

    /// <summary>Live contacts for a customer, with roles parsed from e-conomic notes.</summary>
    public async Task<IReadOnlyList<ContactView>> ListContactsAsync(
        int customerNumber, CancellationToken ct = default)
    {
        var live = await _client.ListContactsAsync(customerNumber, ct);
        return live.Select(c => new ContactView(
            c.ContactNumber, c.Name, c.Email, c.Phone,
            EconomicContactNotesRoles.IsSigner(c.Notes),
            EconomicContactNotesRoles.IsEventCoordinator(c.Notes),
            c.Notes)).ToList();
    }

    /// <summary>Create a contact in e-conomic with the role encoded in notes. Returns its number.</summary>
    public Task<int> CreateAsync(
        int customerNumber, string name, string? email, string? phone,
        bool signer, bool eventCoordinator, CancellationToken ct = default)
    {
        var notes = EconomicContactNotesRoles.WithRoles(null, signer, eventCoordinator);
        var input = new EconomicContactInput(name.Trim(), (email ?? string.Empty).Trim(), phone?.Trim(),
            string.IsNullOrWhiteSpace(notes) ? null : notes);
        return _client.CreateContactAsync(customerNumber, input, ct);
    }

    /// <summary>Update a contact in e-conomic, merging the role into its existing notes.</summary>
    public Task UpdateAsync(
        int customerNumber, int contactNumber, string name, string? email, string? phone,
        bool signer, bool eventCoordinator, string? existingNotes, CancellationToken ct = default)
    {
        var notes = EconomicContactNotesRoles.WithRoles(existingNotes, signer, eventCoordinator);
        var input = new EconomicContactInput(name.Trim(), (email ?? string.Empty).Trim(), phone?.Trim(),
            string.IsNullOrWhiteSpace(notes) ? null : notes);
        return _client.UpdateContactAsync(customerNumber, contactNumber, input, ct);
    }

    /// <summary>Delete a contact from e-conomic.</summary>
    public Task DeleteAsync(int customerNumber, int contactNumber, CancellationToken ct = default) =>
        _client.DeleteContactAsync(customerNumber, contactNumber, ct);
}
