using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Domain.Evaluation;
using CommunityHub.Core.Evaluation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1303 — deleting an evaluation device that joined through the approval queue.
/// </summary>
/// <remarks>
/// Operator 2026-09-25: deleting a test <c>KIOSK-…</c> row on /Organizer/EvaluationDevices returned
/// HTTP 500. Its approval request (and any device-specific telemetry window) points at it through a
/// NoAction FK. SQLite enforces foreign keys, so this reproduces what the in-memory provider hides.
/// </remarks>
public sealed class EvaluationDeviceDeleteRelationalTests : IDisposable
{
    private readonly SqliteConnection _conn;

    public EvaluationDeviceDeleteRelationalTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        using var db = NewDb();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _conn.Dispose();

    private CommunityHubDbContext NewDb() => new(new DbContextOptionsBuilder<CommunityHubDbContext>()
        .UseSqlite(_conn).EnableSensitiveDataLogging().Options);

    private static readonly DateTimeOffset T0 = new(2027, 2, 9, 10, 0, 0, TimeSpan.Zero);

    private async Task<int> SeedJoinedDeviceAsync()
    {
        using var db = NewDb();
        var ev = new Event { CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true };
        db.Events.Add(ev);
        await db.SaveChangesAsync();

        var device = new EvaluationDevice
        {
            EventId = ev.Id, SerialNumber = "KIOSK-D388C177A3", Label = "Lenovo1", IsActive = true, IsKiosk = true,
            KeyHash = EvaluationIngestService.HashKey("k"), CreatedAt = T0, UpdatedAt = T0,
        };
        db.EvaluationDevices.Add(device);
        await db.SaveChangesAsync();

        db.EvaluationDeviceProvisionRequests.Add(new EvaluationDeviceProvisionRequest
        {
            EventId = ev.Id, SerialNumber = device.SerialNumber, Status = EvaluationProvisionStatus.Approved,
            EvaluationDeviceId = device.Id, KeyIssuedAt = T0, RequestedAt = T0, CreatedAt = T0, UpdatedAt = T0,
        });
        await db.SaveChangesAsync();
        return device.Id;
    }

    [Fact]
    public async Task Removing_only_the_device_is_refused_by_the_database_which_was_the_500()
    {
        var id = await SeedJoinedDeviceAsync();
        using var db = NewDb();
        db.EvaluationDevices.Remove(await db.EvaluationDevices.SingleAsync(d => d.Id == id));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task DeleteDevice_removes_the_device_and_its_approval_request()
    {
        var id = await SeedJoinedDeviceAsync();
        using (var db = NewDb())
        {
            var svc = new EvaluationProvisioningService(db);
            await svc.DeleteDeviceAsync(await db.EvaluationDevices.SingleAsync(d => d.Id == id));
        }

        using var check = NewDb();
        Assert.Empty(check.EvaluationDevices);
        // Deleted, not unlinked: an approved request without a device would mint a new one on the
        // tablet's next poll.
        Assert.Empty(check.EvaluationDeviceProvisionRequests);
    }
}
