namespace CommunityHub.Core.Integrations;

/// <summary>A sponsor/exhibitor to be present in Zoho Backstage.</summary>
public sealed record ExhibitorRecord(
    string CompanyId,
    string CompanyName,
    string? ContactEmail);

/// <summary>The outcome of looking up / creating one exhibitor.</summary>
public enum ExhibitorSyncOutcome
{
    /// <summary>Already present in Backstage - nothing to do.</summary>
    AlreadyExists,
    /// <summary>Was missing and has now been created in Backstage.</summary>
    Created,
    /// <summary>Was missing; creation was not performed (TESTMODE, or no API).</summary>
    WouldCreate,
    /// <summary>Lookup or creation failed.</summary>
    Failed,
}

/// <summary>Result of syncing one exhibitor.</summary>
public sealed record ExhibitorSyncItem(
    ExhibitorRecord Exhibitor,
    ExhibitorSyncOutcome Outcome,
    string? Detail);

/// <summary>
/// The Zoho Backstage exhibitor API seam (CONTEXT.md - Backstage exhibitor
/// sync). Two implementations: a TESTMODE one that performs no real calls,
/// and a live one against the Backstage v3 Exhibitor Requests endpoint.
/// </summary>
public interface IBackstageExhibitorApi
{
    /// <summary>
    /// True if an exhibitor with this company id / name already exists in
    /// the Backstage event. NOTE: Backstage has no documented find-by-company
    /// lookup, so the live implementation currently returns false (treats
    /// every exhibitor as missing) - the coordinator email is the duplicate
    /// safeguard.
    /// </summary>
    Task<bool> ExistsAsync(ExhibitorRecord exhibitor, CancellationToken ct);

    /// <summary>
    /// Create the exhibitor in the Backstage event. Implementations that
    /// cannot perform a real write return <c>false</c> from
    /// <see cref="CanCreate"/> and must not be called here.
    /// </summary>
    Task CreateAsync(ExhibitorRecord exhibitor, CancellationToken ct);

    /// <summary>
    /// Whether this implementation can actually perform a creation. False for
    /// TESTMODE, and false for the live API until its portal/event/booth
    /// config is set - the sync then records
    /// <see cref="ExhibitorSyncOutcome.WouldCreate"/>.
    /// </summary>
    bool CanCreate { get; }
}
