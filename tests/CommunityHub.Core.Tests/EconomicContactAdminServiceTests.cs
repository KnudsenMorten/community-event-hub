using CommunityHub.Core.Integrations.Erp;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// The e-conomic contact-CRUD orchestration (<see cref="EconomicContactAdminService"/>).
/// e-conomic is the MASTER: a contact's role (Signer / Event Coordinator) is encoded
/// in the contact's notes as Role:1,2; the service writes it on create/update and
/// parses it on list. Faked live client; no real e-conomic.
/// </summary>
public class EconomicContactAdminServiceTests
{
    /// <summary>In-memory fake of the live e-conomic client; records writes (incl. notes).</summary>
    private sealed class FakeClient : IEconomicContactAdminClient
    {
        public bool CanWrite { get; set; } = true;
        public List<EconomicContactRow> Contacts { get; } = new();
        public int NextNumber = 100;
        public int Deleted;
        public (int customer, int contact, EconomicContactInput input)? LastUpdate;

        public Task<IReadOnlyList<EconomicCustomerRow>> ListCustomersAsync(string? search, int? customerGroup = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EconomicCustomerRow>>(new[] { new EconomicCustomerRow(1, "Acme A/S", "acc@acme.test") });

        public Task<IReadOnlyList<EconomicContactRow>> ListContactsAsync(int customerNumber, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EconomicContactRow>>(Contacts.ToList());

        public Task<int> CreateContactAsync(int customerNumber, EconomicContactInput input, CancellationToken ct = default)
        {
            var n = NextNumber++;
            Contacts.Add(new EconomicContactRow(n, input.Name, input.Email, input.Phone, input.Notes));
            return Task.FromResult(n);
        }

        public Task UpdateContactAsync(int customerNumber, int contactNumber, EconomicContactInput input, CancellationToken ct = default)
        {
            LastUpdate = (customerNumber, contactNumber, input);
            Contacts.RemoveAll(c => c.ContactNumber == contactNumber);
            Contacts.Add(new EconomicContactRow(contactNumber, input.Name, input.Email, input.Phone, input.Notes));
            return Task.CompletedTask;
        }

        public Task DeleteContactAsync(int customerNumber, int contactNumber, CancellationToken ct = default)
        {
            Deleted++;
            Contacts.RemoveAll(c => c.ContactNumber == contactNumber);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Create_encodes_role_into_economic_notes_then_list_parses_it()
    {
        var client = new FakeClient();
        var svc = new EconomicContactAdminService(client);

        var num = await svc.CreateAsync(1, "Jane Doe", "jane@acme.test", "+45 1234",
            signer: false, eventCoordinator: true);

        Assert.True(num >= 100);
        var row = Assert.Single(await svc.ListContactsAsync(1));
        Assert.Equal("Jane Doe", row.Name);
        Assert.False(row.IsSigner);
        Assert.True(row.IsEventCoordinator);     // parsed back from e-conomic notes (Role:2)
        Assert.Contains("Role:2", row.Notes);
    }

    [Fact]
    public async Task Update_can_set_both_roles_and_merges_into_notes()
    {
        var client = new FakeClient();
        var svc = new EconomicContactAdminService(client);
        var num = await svc.CreateAsync(1, "A", "a@x.test", null, signer: false, eventCoordinator: false);

        await svc.UpdateAsync(1, num, "A Renamed", "a2@x.test", "+45 9",
            signer: true, eventCoordinator: true, existingNotes: null);

        Assert.Equal(1, client.LastUpdate!.Value.customer);
        var row = Assert.Single(await svc.ListContactsAsync(1));
        Assert.Equal("A Renamed", row.Name);
        Assert.True(row.IsSigner);
        Assert.True(row.IsEventCoordinator);
        Assert.Contains("Role:1,2", row.Notes);
    }

    [Fact]
    public async Task Update_preserves_existing_free_text_notes()
    {
        var client = new FakeClient();
        var svc = new EconomicContactAdminService(client);
        var num = await svc.CreateAsync(1, "B", "b@x.test", null, signer: false, eventCoordinator: false);

        await svc.UpdateAsync(1, num, "B", "b@x.test", null,
            signer: true, eventCoordinator: false, existingNotes: "VIP - call first; Role:2");

        var row = Assert.Single(await svc.ListContactsAsync(1));
        Assert.Contains("VIP - call first", row.Notes);   // free text kept
        Assert.True(row.IsSigner);
        Assert.False(row.IsEventCoordinator);             // Role replaced (was 2, now 1)
    }

    [Fact]
    public async Task Delete_removes_the_economic_contact()
    {
        var client = new FakeClient();
        var svc = new EconomicContactAdminService(client);
        var num = await svc.CreateAsync(1, "Z", "z@x.test", null, signer: true, eventCoordinator: false);

        await svc.DeleteAsync(1, num);

        Assert.Equal(1, client.Deleted);
        Assert.Empty(await svc.ListContactsAsync(1));
    }

    [Fact]
    public void CanWrite_reflects_the_underlying_client()
    {
        Assert.True(new EconomicContactAdminService(new FakeClient { CanWrite = true }).CanWrite);
        Assert.False(new EconomicContactAdminService(new FakeClient { CanWrite = false }).CanWrite);
    }
}
