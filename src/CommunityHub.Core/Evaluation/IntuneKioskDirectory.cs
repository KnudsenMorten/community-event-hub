using System.Text.Json;
using CommunityHub.Core.Integrations;

namespace CommunityHub.Core.Evaluation;

/// <summary>
/// §1303 — the kiosk tablets Intune knows about, so a tablet is enrolled by TAPPING its Intune name
/// instead of an organizer typing it (operator 2026-09-25: <i>"use the intune device name during
/// enrollment … so i dont have to manually enter same name"</i>).
/// </summary>
/// <remarks>
/// <para>A web page cannot learn which Intune device it runs on (single-app kiosk: the app opens one
/// fixed address, and Chrome on Android has no per-origin managed configuration), so the page shows
/// the list and a human with the organizer PIN picks the name. This reads through the SAME Graph app
/// registration CEH already uses for SharePoint; that registration needs the application permission
/// <c>DeviceManagementManagedDevices.Read.All</c>.</para>
/// <para>🔒 Read-only, and <see langword="null"/> — not an exception — when Graph is unconfigured or
/// refuses: the page then falls back to typing a name, so a missing permission degrades the flow
/// instead of breaking setup.</para>
/// </remarks>
public class IntuneKioskDirectory
{
    private readonly SharePointUploadClient _graph;

    public IntuneKioskDirectory(SharePointUploadClient graph) => _graph = graph;

    /// <summary>Names of Intune managed devices starting with <paramref name="prefix"/>, sorted; null when unavailable.</summary>
    public virtual async Task<IReadOnlyList<string>?> ListAsync(string prefix, CancellationToken ct = default)
    {
        if (!_graph.IsConfigured || string.IsNullOrWhiteSpace(prefix)) return null;

        var safe = prefix.Trim().Replace("'", "''");
        var url = "https://graph.microsoft.com/beta/deviceManagement/managedDevices"
                  + $"?$filter=startswith(deviceName,'{Uri.EscapeDataString(safe)}')&$select=deviceName&$top=999";
        try
        {
            using var doc = await _graph.GetGraphJsonAsync(url, ct);
            if (!doc.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();
            return value.EnumerateArray()
                .Select(v => v.TryGetProperty("deviceName", out var n) ? n.GetString() : null)
                .Where(n => !string.IsNullOrWhiteSpace(n)
                            && n!.StartsWith(prefix.Trim(), StringComparison.OrdinalIgnoreCase))
                .Select(n => n!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (SharePointUploadException)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }
}
