using CommunityHub.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Branding;

public sealed class ActiveEventNameProvider
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly object _gate = new();
    private string? _communityName;
    private string? _eventCode;
    private DateTimeOffset _expires = DateTimeOffset.MinValue;

    public ActiveEventNameProvider(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public string GetCommunityName()
    {
        Refresh();
        return _communityName ?? "Community Hub";
    }

    /// <summary>
    /// The active edition's short CODE (e.g. "ELDK27"), cached like the community name.
    /// Used for the browser tab / bookmark title (REQUIREMENTS §263) so a favourite reads
    /// short + identifiable instead of the long community name. Falls back to the community
    /// name, then "Event Hub", if no code is set.
    /// </summary>
    public string GetEventCode()
    {
        Refresh();
        return !string.IsNullOrWhiteSpace(_eventCode) ? _eventCode!
             : (!string.IsNullOrWhiteSpace(_communityName) ? _communityName! : "Event Hub");
    }

    private void Refresh()
    {
        lock (_gate)
        {
            if (_communityName is not null && DateTimeOffset.UtcNow < _expires)
                return;
        }

        string communityName;
        string? code;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();
            var ev = db.Events
                .Where(e => e.IsActive)
                .Select(e => new { e.CommunityName, e.Code })
                .FirstOrDefault();
            communityName = ev?.CommunityName ?? "Community Hub";
            code = ev?.Code;
        }
        catch
        {
            communityName = "Community Hub";
            code = null;
        }

        lock (_gate)
        {
            _communityName = communityName;
            _eventCode = code;
            _expires = DateTimeOffset.UtcNow.Add(CacheTtl);
        }
    }
}
