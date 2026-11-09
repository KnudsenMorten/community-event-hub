using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Organizer;

/// <summary>§1386 — the <c>contractSeed</c> block of <c>config/hotel.&lt;edition&gt;.json</c>.</summary>
public sealed class HotelContractSeed
{
    /// <summary>Applied ONCE per key per edition. Change the key to apply a new seed.</summary>
    [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;

    [JsonPropertyName("hotels")] public List<HotelSeedEntry> Hotels { get; set; } = new();

    /// <summary>After seeding, place everyone reserved-and-unassigned in the primary hotel.</summary>
    [JsonPropertyName("placeUnassignedInPrimary")] public bool PlaceUnassignedInPrimary { get; set; }
}

public sealed class HotelSeedEntry
{
    /// <summary>Exact hotel name — matched first; also the name a NEW hotel gets.</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;

    /// <summary>Fallback match: an existing hotel whose name contains this (and not <see cref="MatchExcludes"/>).</summary>
    [JsonPropertyName("matchContains")] public string? MatchContains { get; set; }
    [JsonPropertyName("matchExcludes")] public string? MatchExcludes { get; set; }

    [JsonPropertyName("priority")] public int Priority { get; set; } = 1;
    [JsonPropertyName("address")] public string? Address { get; set; }
    [JsonPropertyName("contactEmail")] public string? ContactEmail { get; set; }
    [JsonPropertyName("agreementNumber")] public string? AgreementNumber { get; set; }
    [JsonPropertyName("agreementLabel")] public string? AgreementLabel { get; set; }
    [JsonPropertyName("bookingReference")] public string? BookingReference { get; set; }
    [JsonPropertyName("notifyEmails")] public string? NotifyEmails { get; set; }
    [JsonPropertyName("notes")] public string? Notes { get; set; }

    /// <summary>Night (yyyy-MM-dd) → rooms.</summary>
    [JsonPropertyName("allotments")] public Dictionary<string, int> Allotments { get; set; } = new();

    [JsonPropertyName("cutoffs")] public List<HotelSeedCutoff> Cutoffs { get; set; } = new();
}

public sealed class HotelSeedCutoff
{
    [JsonPropertyName("date")] public string Date { get; set; } = string.Empty;
    [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;
    [JsonPropertyName("releasePercent")] public int? ReleasePercent { get; set; }
    [JsonPropertyName("notes")] public string? Notes { get; set; }
    [JsonPropertyName("warnDaysBefore")] public int? WarnDaysBefore { get; set; }
}

/// <summary>
/// §1386 — applies a hotel contract (priorities, references, per-night allocation, deadlines) from the
/// edition's PRIVATE config file, once (operator 2026-11-08: <i>"you add al info autom"</i>).
/// </summary>
/// <remarks>
/// <para>🔒 <b>Once per key, then hands off.</b> Every later change is an organizer's, made in the hub;
/// a seed that re-applied itself would revert those every run (the §1352 lesson: a timer must not
/// overwrite organizer edits). The applied key is recorded in the change log.</para>
/// <para>The values live in <c>config/hotel.&lt;edition&gt;.json</c>, which the public-mirror denylist
/// strips — contract numbers and mailboxes never reach the public code.</para>
/// </remarks>
public sealed class HotelContractSeeder
{
    private readonly CommunityHubDbContext _db;
    private readonly HotelAllotmentService _allotments;
    private readonly HotelPlacementService _placement;
    private readonly HotelChangeLogService _log;
    private readonly TimeProvider _clock;
    private readonly EventConfigOptions? _options;

    public HotelContractSeeder(
        CommunityHubDbContext db, HotelAllotmentService allotments, HotelPlacementService placement,
        HotelChangeLogService log, TimeProvider clock, EventConfigOptions? options = null)
    {
        _db = db;
        _allotments = allotments;
        _placement = placement;
        _log = log;
        _clock = clock;
        _options = options;
    }

    public static string SeedMarker(string key) => $"[seed {key}]";

    /// <summary>The hotel config next to the event config (<c>event.X.json</c> → <c>hotel.X.json</c>).</summary>
    public string HotelConfigPath()
    {
        var eventPath = _options?.EventConfigPath ?? "config/event.eldk27.json";
        var dir = Path.GetDirectoryName(eventPath) ?? "config";
        var file = Path.GetFileName(eventPath);
        var hotelFile = file.StartsWith("event.", StringComparison.OrdinalIgnoreCase) ? "hotel." + file[6..] : "hotel.json";
        return ConfigPaths.Resolve(Path.Combine(dir, hotelFile));
    }

    public static HotelContractSeed? ReadSeed(string path)
    {
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (!doc.RootElement.TryGetProperty("contractSeed", out var el)) return null;
        return el.Deserialize<HotelContractSeed>();
    }

    /// <summary>Apply the seed from the config file if it has not been applied. Returns a summary, or null when nothing ran.</summary>
    public async Task<string?> ApplyFromConfigAsync(int eventId, CancellationToken ct = default)
    {
        var seed = ReadSeed(HotelConfigPath());
        return seed is null ? null : await ApplyAsync(eventId, seed, ct);
    }

    public async Task<string?> ApplyAsync(int eventId, HotelContractSeed seed, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(seed.Key) || seed.Hotels.Count == 0) return null;
        var marker = SeedMarker(seed.Key);
        var done = await _db.HotelChangeLog.AnyAsync(
            e => e.EventId == eventId && e.Kind == HotelChangeKinds.Seed && e.Summary.StartsWith(marker), ct);
        if (done) return null;

        var notes = new List<string>();
        foreach (var entry in seed.Hotels.OrderBy(h => h.Priority))
        {
            var hotel = await FindAsync(eventId, entry, ct);
            var created = hotel is null;
            if (hotel is null)
            {
                hotel = new Hotel { EventId = eventId, Name = entry.Name.Trim(), CreatedAt = _clock.GetUtcNow() };
                _db.Hotels.Add(hotel);
            }

            hotel.Priority = entry.Priority;
            if (entry.Address is not null) hotel.Address = entry.Address;
            if (entry.ContactEmail is not null) hotel.ContactEmail = entry.ContactEmail;
            if (entry.AgreementNumber is not null) hotel.AgreementNumber = entry.AgreementNumber;
            if (entry.AgreementLabel is not null) hotel.AgreementLabel = entry.AgreementLabel;
            if (entry.BookingReference is not null) hotel.BookingReference = entry.BookingReference;
            if (entry.NotifyEmails is not null) hotel.NotifyEmails = entry.NotifyEmails;
            if (entry.Notes is not null) hotel.Notes = entry.Notes;
            hotel.UpdatedAt = _clock.GetUtcNow();
            await _db.SaveChangesAsync(ct);
            notes.Add($"{(created ? "created" : "updated")} {hotel.Name} (prio {hotel.Priority})");

            var map = new Dictionary<DateOnly, int?>();
            foreach (var (k, v) in entry.Allotments)
                if (DateOnly.TryParseExact(k, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var night))
                    map[night] = v;
            if (map.Count > 0)
                await _allotments.SetAllotmentsAsync(eventId, hotel.Id, map, ct, "system", HotelChangeSource.System);

            foreach (var c in entry.Cutoffs)
            {
                if (!DateOnly.TryParseExact(c.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
                await _allotments.SaveCutoffAsync(eventId, hotel.Id, d, c.Label, c.ReleasePercent, c.Notes, ct, c.WarnDaysBefore);
            }
        }

        var placed = seed.PlaceUnassignedInPrimary
            ? await _placement.PlaceUnassignedInPrimaryAsync(eventId, "system", ct)
            : 0;
        if (placed > 0) notes.Add($"placed {placed} reserved guest(s) in the primary hotel");

        _log.Add(eventId, HotelChangeSource.System, "system", HotelChangeKinds.Seed,
            $"{marker} Hotel contract applied from config: {string.Join("; ", notes)}", notify: false);
        await _db.SaveChangesAsync(ct);
        return string.Join("; ", notes);
    }

    private async Task<Hotel?> FindAsync(int eventId, HotelSeedEntry entry, CancellationToken ct)
    {
        var hotels = await _db.Hotels.Where(h => h.EventId == eventId).OrderBy(h => h.Id).ToListAsync(ct);
        var exact = hotels.FirstOrDefault(h => string.Equals(h.Name.Trim(), entry.Name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        if (string.IsNullOrWhiteSpace(entry.MatchContains)) return null;
        return hotels.FirstOrDefault(h =>
            h.Name.Contains(entry.MatchContains, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(entry.MatchExcludes)
                || !h.Name.Contains(entry.MatchExcludes, StringComparison.OrdinalIgnoreCase)));
    }
}
