namespace CommunityHub.Pages.Media;

/// <summary>§1078 — display helpers for the media libraries.</summary>
public static class MediaFormat
{
    /// <summary>
    /// A file size a person reads at a glance. ⚠️ Video is the reason this is not "bytes": a
    /// photographer needs to see 1.4 GB, not 1,503,238,553.
    /// </summary>
    public static string Size(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824d:0.#} GB",
        >= 1_048_576 => $"{bytes / 1_048_576d:0.#} MB",
        >= 1024 => $"{bytes / 1024d:0.#} KB",
        _ => $"{bytes} B",
    };
}
