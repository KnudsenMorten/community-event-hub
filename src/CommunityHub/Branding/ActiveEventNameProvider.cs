using CommunityHub.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Branding;

public sealed class ActiveEventNameProvider
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly object _gate = new();
    private string? _communityName;
    private DateTimeOffset _expires = DateTimeOffset.MinValue;

    public ActiveEventNameProvider(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public string GetCommunityName()
    {
        lock (_gate)
        {
            if (_communityName is not null && DateTimeOffset.UtcNow < _expires)
                return _communityName;
        }

        string resolved;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();
            resolved = db.Events
                .Where(e => e.IsActive)
                .Select(e => e.CommunityName)
                .FirstOrDefault() ?? "Community Hub";
        }
        catch
        {
            resolved = "Community Hub";
        }

        lock (_gate)
        {
            _communityName = resolved;
            _expires = DateTimeOffset.UtcNow.Add(CacheTtl);
        }
        return resolved;
    }
}
