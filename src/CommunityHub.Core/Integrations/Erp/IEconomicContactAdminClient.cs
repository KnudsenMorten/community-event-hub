namespace CommunityHub.Core.Integrations.Erp;

/// <summary>
/// A focused, organizer-facing seam for managing the CONTACTS on an existing
/// e-conomic customer (REQUIREMENTS §6 — "manage e-conomic customer contacts").
/// Kept separate from <see cref="IEconomicErpClient"/> (the sponsor-sync/order
/// seam) so the contact-admin GUI can list customers + CRUD contacts without
/// touching the sync services. Role + free-text notes have no native e-conomic
/// field, so they are stored hub-side (see <c>EconomicContactAnnotation</c>);
/// this client carries only what e-conomic actually stores: name, email, phone.
///
/// NEVER hard-codes an endpoint/secret — base URL + the App-Secret / Agreement-
/// Grant tokens come from <see cref="EconomicErpOptions"/> (KV-backed). When not
/// configured, <see cref="CanWrite"/> is false and callers must not invoke it.
/// </summary>
public interface IEconomicContactAdminClient
{
    /// <summary>True only when base URL + both tokens are configured (live writes possible).</summary>
    bool CanWrite { get; }

    /// <summary>All e-conomic customers (paged through), optionally name/number filtered.</summary>
    Task<IReadOnlyList<EconomicCustomerRow>> ListCustomersAsync(
        string? search, CancellationToken ct = default);

    /// <summary>The contacts on one customer.</summary>
    Task<IReadOnlyList<EconomicContactRow>> ListContactsAsync(
        int customerNumber, CancellationToken ct = default);

    /// <summary>Create a contact on the customer; returns the new customerContactNumber.</summary>
    Task<int> CreateContactAsync(
        int customerNumber, EconomicContactInput input, CancellationToken ct = default);

    /// <summary>Update an existing contact (name / email / phone).</summary>
    Task UpdateContactAsync(
        int customerNumber, int contactNumber, EconomicContactInput input, CancellationToken ct = default);

    /// <summary>Delete a contact from the customer.</summary>
    Task DeleteContactAsync(
        int customerNumber, int contactNumber, CancellationToken ct = default);
}

/// <summary>One e-conomic customer row for the selection grid.</summary>
public sealed record EconomicCustomerRow(int CustomerNumber, string Name, string? Email);

/// <summary>One e-conomic customer contact as stored in e-conomic.</summary>
public sealed record EconomicContactRow(
    int ContactNumber, string Name, string? Email, string? Phone);

/// <summary>The e-conomic-side fields for a create/update.</summary>
public sealed record EconomicContactInput(string Name, string Email, string? Phone);
