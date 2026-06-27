using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Config;

/// <summary>One role's Signal chat link from signal-groups.&lt;edition&gt;.json (§109).</summary>
public sealed class SignalGroupRole
{
    /// <summary>Display label for the role's chat group button (e.g. "ELDK27 Speakers – Chat").</summary>
    [JsonPropertyName("chatLabel")]
    public string? ChatLabel { get; set; }

    /// <summary>The signal.group invite URL for the role's chat group. Null/blank = broadcast-only role.</summary>
    [JsonPropertyName("chatUrl")]
    public string? ChatUrl { get; set; }
}

/// <summary>The signal-groups config file (§109).</summary>
public sealed class SignalGroupsConfig
{
    [JsonPropertyName("broadcastLabel")]
    public string BroadcastLabel { get; set; } = "Broadcast";

    [JsonPropertyName("broadcastUrl")]
    public string? BroadcastUrl { get; set; }

    /// <summary>
    /// Role → chat link. A role's PRESENCE here means it is in scope for the
    /// "Join Signal groups" step/task (and always gets the Broadcast button); a
    /// non-blank chatUrl additionally renders the role's Chat button.
    /// </summary>
    [JsonPropertyName("roles")]
    public Dictionary<string, SignalGroupRole> Roles { get; set; } = new();
}

/// <summary>Where the signal-groups config file is.</summary>
public sealed class SignalGroupsOptions
{
    public const string SectionName = "SignalGroups";

    public string ConfigPath { get; set; } = "config/signal-groups.eldk27.json";
}

/// <summary>One participant's resolved Signal links (§109): the role chat (optional) + broadcast.</summary>
public sealed record SignalGroupLinks(
    string? ChatLabel, string? ChatUrl, string BroadcastLabel, string? BroadcastUrl)
{
    /// <summary>True when this role gets a chat group link (broadcast-only roles → false).</summary>
    public bool HasChat => !string.IsNullOrWhiteSpace(ChatUrl);

    /// <summary>True when a broadcast link is configured.</summary>
    public bool HasBroadcast => !string.IsNullOrWhiteSpace(BroadcastUrl);
}

/// <summary>
/// Reads signal-groups.&lt;edition&gt;.json and resolves the role-appropriate Signal
/// links (REQUIREMENTS §109). The config is read once and cached; a missing/empty
/// file makes every role out-of-scope (the step/task simply never appears). Used by
/// the wizards (is this role in scope?) and the Signal step page (render buttons).
/// </summary>
public sealed class SignalGroupsProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly SignalGroupsOptions _options;
    private readonly Lazy<SignalGroupsConfig> _config;

    public SignalGroupsProvider(SignalGroupsOptions options)
    {
        _options = options;
        _config = new Lazy<SignalGroupsConfig>(Load);
    }

    /// <summary>Construct directly from an in-memory config (tests / non-file callers).</summary>
    public SignalGroupsProvider(SignalGroupsConfig config)
    {
        _options = new SignalGroupsOptions();
        _config = new Lazy<SignalGroupsConfig>(() => config);
    }

    private SignalGroupsConfig Load()
    {
        try
        {
            if (!File.Exists(_options.ConfigPath))
                return new SignalGroupsConfig();
            return JsonSerializer.Deserialize<SignalGroupsConfig>(
                File.ReadAllText(_options.ConfigPath), JsonOptions) ?? new SignalGroupsConfig();
        }
        catch
        {
            // A malformed/locked config must never break the wizard — just disable
            // the Signal step (role-out-of-scope) until the file is fixed.
            return new SignalGroupsConfig();
        }
    }

    /// <summary>True when this role is in scope for the Signal step/task (§109).</summary>
    public bool InScope(ParticipantRole role) =>
        _config.Value.Roles.ContainsKey(role.ToString());

    /// <summary>
    /// The role-appropriate Signal links, or null when the role is out of scope
    /// (Organizer/Sponsor/Attendee, or no config). Broadcast is shared across roles;
    /// the chat link is per role (absent for broadcast-only roles like Media).
    /// </summary>
    public SignalGroupLinks? GetForRole(ParticipantRole role)
    {
        var cfg = _config.Value;
        if (!cfg.Roles.TryGetValue(role.ToString(), out var roleCfg))
            return null;
        return new SignalGroupLinks(
            roleCfg.ChatLabel, roleCfg.ChatUrl, cfg.BroadcastLabel, cfg.BroadcastUrl);
    }
}
