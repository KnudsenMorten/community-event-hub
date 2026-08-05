using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.DocLibrary;

/// <summary>
/// §769 — a singleton, synchronously-readable snapshot of the persisted path / file-name overrides,
/// so <see cref="DocLibraryPathResolver"/> can stay a fast, synchronous, non-async call.
/// </summary>
/// <remarks>
/// <para><b>Why a snapshot at all.</b> <c>TryResolve</c> is called on hot paths (every graphic, every
/// upload, every page render that shows a file) and is synchronous by contract. Making it hit the
/// database would put a query behind every one of those, and making it async would ripple through
/// every caller. So the values are read once and cached.</para>
///
/// <para>🔒 <b>THE PROPAGATION RULE, and it is a real limitation — state it, do not hide it.</b> CEH
/// runs in TWO processes: the web host, where the operator edits, and the jobs host, where the
/// sweeps run. An in-process invalidation reaches only the first. So the snapshot also EXPIRES on a
/// timer, and <see cref="Ttl"/> is therefore the honest answer to "how long until my edit affects the
/// nightly jobs?" — up to <see cref="Ttl"/>, not instantly. The page says so out loud rather than
/// letting an operator watch a job use the old folder and conclude the save failed.</para>
///
/// <para>⚠️ <b>Fail-safe, never fail-shut.</b> If the database cannot be read the snapshot keeps its
/// last known values (or the empty set on a cold start, i.e. registry defaults) and logs once. A
/// document library must not be able to take down sign-in, and "the DB blipped" must not silently
/// become "every folder moved".</para>
/// </remarks>
public sealed class DocLibraryOverrideCache
{
    /// <summary>How long a snapshot lives before it is re-read. Also the cross-host lag.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;
    private readonly ILogger<DocLibraryOverrideCache>? _log;
    private readonly object _gate = new();

    private Dictionary<string, string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _fileNames = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;
    private bool _loadFailedLogged;

    public DocLibraryOverrideCache(
        IServiceScopeFactory scopes, TimeProvider? clock = null,
        ILogger<DocLibraryOverrideCache>? log = null)
    {
        _scopes = scopes;
        _clock = clock ?? TimeProvider.System;
        _log = log;
    }

    /// <summary>Persisted path overrides, by key. Empty ⇒ every path is its registered default.</summary>
    public IReadOnlyDictionary<string, string> Paths
    {
        get { EnsureFresh(); lock (_gate) return _paths; }
    }

    /// <summary>Persisted file-name overrides, by key.</summary>
    public IReadOnlyDictionary<string, string> FileNames
    {
        get { EnsureFresh(); lock (_gate) return _fileNames; }
    }

    /// <summary>Drop the snapshot — called after a save, so THIS host sees the edit immediately.</summary>
    public void Invalidate()
    {
        lock (_gate) _loadedAt = DateTimeOffset.MinValue;
    }

    private void EnsureFresh()
    {
        lock (_gate)
        {
            if (_clock.GetUtcNow() - _loadedAt < Ttl) return;
        }

        List<DocLibrarySettingOverride> rows;
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CommunityHubDbContext>();
            rows = db.DocLibrarySettingOverrides.AsNoTracking().ToList();
        }
        catch (Exception ex)
        {
            // Keep the last known good snapshot. Logged ONCE: this runs behind every path
            // resolution, so a persistent DB problem would otherwise write a log line per graphic.
            lock (_gate)
            {
                _loadedAt = _clock.GetUtcNow();      // back off; try again after the TTL
                if (_loadFailedLogged) return;
                _loadFailedLogged = true;
            }
            _log?.LogWarning(ex,
                "DocLibrary: the path overrides could not be read; the previously loaded values "
                + "(or the registered defaults) stay in force.");
            return;
        }

        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            if (string.IsNullOrWhiteSpace(r.Value)) continue;    // a blank row is not an override
            var target = r.Kind == DocLibrarySettingKind.FileName ? names : paths;
            target[r.Key] = r.Value.Trim();
        }

        lock (_gate)
        {
            _paths = paths;
            _fileNames = names;
            _loadedAt = _clock.GetUtcNow();
            _loadFailedLogged = false;
        }
    }
}
