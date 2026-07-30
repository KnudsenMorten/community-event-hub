using System.Text.RegularExpressions;

namespace CommunityHub.Core.Config;

/// <summary>
/// §299.8/b7 — resolves the per-edition SESSION OPTION config (length quick-picks,
/// the custom-length max, and the numeric-coded level list) from
/// <c>event.&lt;edition&gt;.json</c>. Pure config, no DB — the public pages stay
/// fast. The config is read once per service instance (scoped), following the
/// sharepoint/dates sibling-block pattern.
///
/// <b>Lengths:</b> the GUI offers the configured quick-picks (15/20/30/40/45/50/
/// 60/420 for this edition) but ANY positive integer of minutes up to
/// <see cref="MaxMinutes"/> validates — a closed length enum has already failed
/// twice (40 and 30 were both missing), so the enum is display-only now.
///
/// <b>Levels:</b> label + NUMERIC code (Advanced 300 / Expert 400 / Black Belt
/// 500). All sorting/comparison uses the code, never the label alphabetically
/// (alphabetical puts Black Belt before Expert, which is wrong). Other
/// communities configure 100/200 levels this edition doesn't offer.
/// </summary>
public sealed class SessionOptionsService
{
    private readonly EventEditionConfig _config;

    /// <summary>DI construction: loads the active edition's event config from disk.</summary>
    public SessionOptionsService(
        EventEditionConfigLoader loader, EventConfigOptions? options = null)
        : this(loader.Load((options ?? new EventConfigOptions()).EventConfigPath))
    {
    }

    /// <summary>Direct construction from an already-loaded config (tests).</summary>
    public SessionOptionsService(EventEditionConfig config) => _config = config;

    /// <summary>The configured length quick-picks (label + minutes), in config order.</summary>
    public IReadOnlyList<SessionLengthOption> LengthQuickPicks => _config.SessionLengths;

    /// <summary>Inclusive max for a custom length in minutes (config; default 600).</summary>
    public int MaxMinutes => _config.SessionLengthMaxMinutes;

    /// <summary>The configured levels, sorted by NUMERIC code (never alphabetically).</summary>
    public IReadOnlyList<SessionLevelOption> Levels =>
        _config.SessionLevels.OrderBy(l => l.Code).ToList();

    /// <summary>True when <paramref name="minutes"/> is a valid session length:
    /// any positive integer up to <see cref="MaxMinutes"/> (quick-pick or custom).</summary>
    public bool IsValidLength(int minutes) => minutes > 0 && minutes <= MaxMinutes;

    /// <summary>
    /// Derive the NUMERIC level code for a source level label (e.g. "Expert (400)"
    /// → 400): first a case-insensitive match against the configured level labels
    /// (the session label CONTAINING the config label, so "Expert (400)" matches
    /// "Expert"), then a "(NNN)" digits parse as fallback. Null when the label is
    /// blank or matches nothing — unknown labels keep their string-only behaviour.
    /// </summary>
    public int? DeriveLevelCode(string? levelLabel) =>
        DeriveLevelCode(levelLabel, _config.SessionLevels);

    /// <summary>Pure static derivation core — see <see cref="DeriveLevelCode(string?)"/>.</summary>
    public static int? DeriveLevelCode(
        string? levelLabel, IReadOnlyList<SessionLevelOption> levels)
    {
        if (string.IsNullOrWhiteSpace(levelLabel)) return null;
        var label = levelLabel.Trim();

        // Config match first: the SOURCE label contains the configured label
        // ("Expert (400)" ⊇ "Expert"). Prefer the LONGEST configured label so a
        // hypothetical "Expert" never shadows a longer "Black Belt Expert".
        var match = levels
            .Where(l => !string.IsNullOrWhiteSpace(l.Label)
                        && label.Contains(l.Label.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(l => l.Label.Trim().Length)
            .FirstOrDefault();
        if (match is not null) return match.Code;

        // Fallback: a "(NNN)" digits hint written into the label itself.
        var m = Regex.Match(label, @"\((\d{2,4})\)");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var code) && code > 0)
            return code;

        return null;
    }
}
