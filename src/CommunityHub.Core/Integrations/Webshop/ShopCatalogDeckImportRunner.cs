using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Webshop;

/// <summary>
/// §1312 — runs the sponsor-deck import in the BACKGROUND and remembers the outcome for the organizer page. The deck is
/// ~190 MB with ~90 pictures to scale and store: longer than a web request may take (App Service cuts at 230 s). One run
/// at a time; the result stays until the next run.
/// </summary>
public sealed class ShopCatalogDeckImportRunner
{
    public sealed record Status(bool Running, bool DryRun, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt,
        ShopCatalogDeckImporter.ImportResult? Result);

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;
    private readonly ILogger<ShopCatalogDeckImportRunner>? _log;
    private readonly object _gate = new();
    private Status? _status;

    public ShopCatalogDeckImportRunner(IServiceScopeFactory scopes, TimeProvider clock, ILogger<ShopCatalogDeckImportRunner>? log = null)
    {
        _scopes = scopes;
        _clock = clock;
        _log = log;
    }

    public Status? Current { get { lock (_gate) return _status; } }

    /// <summary>Start a run; false when one is already running.</summary>
    public bool Start(int eventId, string byEmail, bool dryRun)
    {
        lock (_gate)
        {
            if (_status is { Running: true }) return false;
            _status = new Status(true, dryRun, _clock.GetUtcNow(), null, null);
        }
        _ = Task.Run(async () =>
        {
            ShopCatalogDeckImporter.ImportResult result;
            try
            {
                using var scope = _scopes.CreateScope();
                var importer = scope.ServiceProvider.GetRequiredService<ShopCatalogDeckImporter>();
                result = await importer.ImportAsync(eventId, byEmail, dryRun);
            }
            catch (Exception ex)
            {
                _log?.LogError(ex, "§1312 sponsor deck import failed.");
                result = new(false, "The import failed: " + ex.Message, Array.Empty<string>());
            }
            lock (_gate) _status = _status! with { Running = false, FinishedAt = _clock.GetUtcNow(), Result = result };
        });
        return true;
    }
}
