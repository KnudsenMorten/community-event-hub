using System.Text.Json;
using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Integrations;

/// <summary>One Zoho id that has been reported "not found".</summary>
/// <param name="Id">The Zoho record id.</param>
/// <param name="Strikes">Counted reports — each at least <see cref="ZohoDeadLinkStrikes.MinimumGap"/> after the last.</param>
/// <param name="FirstSeenUtc">When the first report was counted.</param>
/// <param name="LastCountedUtc">When the most recent report was counted.</param>
public sealed record ZohoDeadLinkStrike(string Id, int Strikes, DateTimeOffset FirstSeenUtc, DateTimeOffset LastCountedUtc);

/// <summary>
/// §1221 — a Zoho link is removed only after Zoho has said "not found" repeatedly, far apart.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-14: <i>"it must have been failed like 5 times with min 12 hr apart before
/// removing to rule out api issues"</i>.</para>
///
/// <para>🔑 <b>Why not trust one answer.</b> The same Zoho endpoint that answers a deleted record with
/// 400 "Sponsor not found" also answers LIVE records with bursts of bare 400s (§1220). A link removed
/// on the strength of one bad afternoon is a company un-linked from a record that still exists — and
/// the provisioner then creates a duplicate beside it. Five reports at least twelve hours apart span
/// two days, which no burst so far has come close to.</para>
///
/// <para>🔒 <b>Reports inside the gap are not counted</b> — the job runs every ten minutes, so without
/// the gap five strikes would take under an hour. 🔒 <b>A successful read clears the entry</b>: the
/// strikes must be consecutive evidence, not a lifetime total. A failure that is NOT a "not found"
/// (5xx, bare 400) neither counts nor clears — it says nothing either way.</para>
/// </remarks>
public static class ZohoDeadLinkStrikes
{
    /// <summary>Counted "not found" reports before the link is removed.</summary>
    public const int StrikesToRemove = 5;

    /// <summary>The minimum time between two counted reports.</summary>
    public static readonly TimeSpan MinimumGap = TimeSpan.FromHours(12);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Everything currently struck for this company. Never throws.</summary>
    public static IReadOnlyList<ZohoDeadLinkStrike> Read(SponsorInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.ZohoDeadLinkStrikesJson)) return Array.Empty<ZohoDeadLinkStrike>();
        try
        {
            return JsonSerializer.Deserialize<List<ZohoDeadLinkStrike>>(info.ZohoDeadLinkStrikesJson!, Json)?
                       .Where(s => !string.IsNullOrWhiteSpace(s.Id)).ToList()
                   ?? (IReadOnlyList<ZohoDeadLinkStrike>)Array.Empty<ZohoDeadLinkStrike>();
        }
        catch (JsonException)
        {
            // 🔒 Unreadable = no strikes. The cost is starting the count again; the cost of guessing
            // is removing a link on evidence we cannot read.
            return Array.Empty<ZohoDeadLinkStrike>();
        }
    }

    private static void Write(SponsorInfo info, List<ZohoDeadLinkStrike> entries) =>
        info.ZohoDeadLinkStrikesJson = entries.Count == 0 ? null : JsonSerializer.Serialize(entries, Json);

    /// <summary>
    /// Record a "not found" report for <paramref name="id"/>. Returns true once the link has earned removal.
    /// </summary>
    public static bool RecordNotFound(SponsorInfo info, string id, DateTimeOffset nowUtc)
    {
        var entries = Read(info).ToList();
        var i = entries.FindIndex(e => string.Equals(e.Id, id, StringComparison.Ordinal));

        ZohoDeadLinkStrike entry;
        if (i < 0)
        {
            entry = new ZohoDeadLinkStrike(id, 1, nowUtc, nowUtc);
            entries.Add(entry);
        }
        else
        {
            entry = entries[i];
            if (nowUtc - entry.LastCountedUtc >= MinimumGap)
            {
                entry = entry with { Strikes = entry.Strikes + 1, LastCountedUtc = nowUtc };
                entries[i] = entry;
            }
        }

        Write(info, entries);
        return entry.Strikes >= StrikesToRemove;
    }

    /// <summary>The record was read successfully (or the link is gone) — forget it. True when something changed.</summary>
    public static bool Clear(SponsorInfo info, string id)
    {
        var entries = Read(info).ToList();
        if (entries.RemoveAll(e => string.Equals(e.Id, id, StringComparison.Ordinal)) == 0) return false;
        Write(info, entries);
        return true;
    }
}
