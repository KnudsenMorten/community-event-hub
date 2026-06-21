using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Erp;
using CommunityHub.Core.Tests.Scenario;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// The organizer e-conomic contact-CRUD orchestration (<see cref="EconomicContactAdminService"/>):
/// the live e-conomic write (faked here) plus the hub-side role + notes annotation
/// kept in lock-step — list merges them, create/update/delete write both sides.
/// EF in-memory; no real e-conomic.
/// </summary>
public class EconomicContactAdminServiceTests
{
    /// <summary>In-memory fake of the live e-conomic client; records writes.</summary>
    private sealed class FakeClient : IEconomicContactAdminClient
    {
        public bool CanWrite { get; set; } = true;
        public List<EconomicContactRow> Contacts { get; } = new();
        public int NextNumber = 100;
        public int Deleted;
        public (int customer, int contact, EconomicContactInput input)? LastUpdate;

        public Task<IReadOnlyList<EconomicCustomerRow>> ListCustomersAsync(string? search, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EconomicCustomerRow>>(new[] { new EconomicCustomerRow(1, "Acme A/S", "acc@acme.test") });

        public Task<IReadOnlyList<EconomicContactRow>> ListContactsAsync(int customerNumber, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EconomicContactRow>>(Contacts.ToList());

        public Task<int> CreateContactAsync(int customerNumber, EconomicContactInput input, CancellationToken ct = default)
        {
            var n = NextNumber++;
            Contacts.Add(new EconomicContactRow(n, input.Name, input.Email, input.Phone));
            return Task.FromResult(n);
        }

        public Task UpdateContactAsync(int customerNumber, int contactNumber, EconomicContactInput input, CancellationToken ct = default)
        {
            LastUpdate = (customerNumber, contactNumber, input);
            Contacts.RemoveAll(c => c.ContactNumber == contactNumber);
            Contacts.Add(new EconomicContactRow(contactNumber, input.Name, input.Email, input.Phone));
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
    public async Task Create_writes_economic_and_persists_role_notes_then_list_merges_them()
    {
        using var db = ScenarioFixture.NewDb();
        var client = new FakeClient();
        var svc = new EconomicContactAdminService(client, db);

        var num = await svc.CreateAsync(1,
            new EconomicContactInput("Jane Doe", "jane@acme.test", "+45 1234"),
            ErpContactRole.EventCoordinator, "VIP - call first", "mok@expertslive.dk");

        Assert.True(num >= 100);
        Assert.Single(db.EconomicContactAnnotations);

        var listed = await svc.ListContactsAsync(1);
        var row = Assert.Single(listed);
        Assert.Equal("Jane Doe", row.Name);
        Assert.Equal(ErpContactRole.EventCoordinator, row.Role);   // from the hub annotation
        Assert.Equal("VIP - call first", row.Notes);
    }

    [Fact]
    public async Task Update_writes_economic_and_upserts_the_annotation()
    {
        using var db = ScenarioFixture.NewDb();
        var client = new FakeClient();
        var svc = new EconomicContactAdminService(client, db);
        var num = await svc.CreateAsync(1, new EconomicContactInput("A", "a@x.test", null),
            ErpContactRole.Contact, null, "org@x");

        await svc.UpdateAsync(1, num, new EconomicContactInput("A Renamed", "a2@x.test", "+45 9"),
            ErpContactRole.Signer, "now the signer", "org@x");

        Assert.Equal((1, num, new EconomicContactInput("A Renamed", "a2@x.test", "+45 9")), client.LastUpdate);
        var row = Assert.Single(await svc.ListContactsAsync(1));
        Assert.Equal("A Renamed", row.Name);
        Assert.Equal(ErpContactRole.Signer, row.Role);
        Assert.Equal("now the signer", row.Notes);
        Assert.Single(db.EconomicContactAnnotations);   // upsert, not a 2nd row
    }

    [Fact]
    public async Task Delete_removes_economic_contact_and_its_annotation()
    {
        using var db = ScenarioFixture.NewDb();
        var client = new FakeClient();
        var svc = new EconomicContactAdminService(client, db);
        var num = await svc.CreateAsync(1, new EconomicContactInput("Z", "z@x.test", null),
            ErpContactRole.Signer, "note", "org@x");

        await svc.DeleteAsync(1, num);

        Assert.Equal(1, client.Deleted);
        Assert.Empty(await svc.ListContactsAsync(1));
        Assert.Empty(db.EconomicContactAnnotations);    // annotation cleaned up too
    }

    [Fact]
    public void CanWrite_reflects_the_underlying_client()
    {
        using var db = ScenarioFixture.NewDb();
        Assert.True(new EconomicContactAdminService(new FakeClient { CanWrite = true }, db).CanWrite);
        Assert.False(new EconomicContactAdminService(new FakeClient { CanWrite = false }, db).CanWrite);
    }
}
