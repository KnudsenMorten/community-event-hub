namespace CommunityHub.Core.Config;

/// <summary>
/// §299.6/b5 — the per-edition room REGISTRY, read from
/// <c>event.&lt;edition&gt;.json → sessionRooms</c>. Pure config (no DB, no FK):
/// <c>Session.Room</c> deliberately STAYS a free-form string — the room name is
/// deeply load-bearing across the call-for-speakers import, the external-event
/// push and the QR/eval plumbing — and this registry validates it WARN-ONLY
/// (option A): import warnings, an organizer "unknown room" badge + name
/// datalist, and a log-warn on the agenda push. Nothing ever blocks on an
/// unknown name.
///
/// Names must be byte-identical across systems, so lookups are EXACT
/// (ordinal, trimmed only). Venue rooms carry a capacity; expo locations have
/// <c>Capacity = null</c> BY DESIGN — callers must tolerate the null and never
/// treat it as 0. An empty registry (block absent) keeps every check quiet.
/// </summary>
public sealed class RoomRegistryService
{
    private readonly IReadOnlyList<SessionRoomOption> _rooms;
    private readonly Dictionary<string, SessionRoomOption> _byName;

    /// <summary>DI construction: loads the active edition's event config from disk.</summary>
    public RoomRegistryService(
        EventEditionConfigLoader loader, EventConfigOptions? options = null)
        : this(loader.Load((options ?? new EventConfigOptions()).EventConfigPath))
    {
    }

    /// <summary>Direct construction from an already-loaded config (tests).</summary>
    public RoomRegistryService(EventEditionConfig config)
    {
        _rooms = config.SessionRooms;
        // Exact (ordinal) name lookup — the whole point of the registry is that
        // names are byte-identical across systems; a case-insensitive "match"
        // would hide exactly the drift the validation exists to surface.
        _byName = new Dictionary<string, SessionRoomOption>(StringComparer.Ordinal);
        foreach (var r in _rooms)
        {
            var name = r.Name.Trim();
            if (name.Length > 0 && !_byName.ContainsKey(name)) _byName[name] = r;
        }
    }

    /// <summary>All registry entries (venue rooms + expo locations), config order.</summary>
    public IReadOnlyList<SessionRoomOption> Rooms => _rooms;

    /// <summary>True when the registry has any entries (block present in config).</summary>
    public bool HasEntries => _byName.Count > 0;

    /// <summary>All registered names (for the organizer room-input datalist), config order.</summary>
    public IReadOnlyList<string> Names => _rooms
        .Select(r => r.Name.Trim())
        .Where(n => n.Length > 0)
        .Distinct(StringComparer.Ordinal)
        .ToList();

    /// <summary>True when <paramref name="name"/> exactly matches a registered room
    /// (trimmed, ordinal). Blank names are "known" (nothing to validate); when the
    /// registry itself is EMPTY every name is treated as known (validation off).</summary>
    public bool IsKnown(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        if (!HasEntries) return true;
        return _byName.ContainsKey(name.Trim());
    }

    /// <summary>Exact-name lookup. Null when the name is blank or not registered.</summary>
    public SessionRoomOption? Find(string? name) =>
        !string.IsNullOrWhiteSpace(name) && _byName.TryGetValue(name.Trim(), out var room)
            ? room
            : null;

    /// <summary>
    /// The registered capacity for a room name: an int for a venue room, NULL for
    /// an expo location (by design) AND for an unknown/blank name. Callers must
    /// tolerate the null — never treat a missing capacity as 0.
    /// </summary>
    public int? CapacityOf(string? name) => Find(name)?.Capacity;
}
