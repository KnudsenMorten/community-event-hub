namespace CommunityHub.Core.Integrations;

/// <summary>
/// Configuration for the WordPress posts connector (REQUIREMENTS §31). Binds from
/// the <c>"WordPress"</c> section; the env-var form replaces <c>:</c> with <c>__</c>
/// (e.g. <c>WordPress__AppPassword</c>). Secrets come from Key Vault app settings,
/// never source. The connector self-gates on <see cref="IWordPressPublisher.CanWrite"/>,
/// so an unconfigured deploy is inert (no calls, nothing faked).
/// </summary>
public sealed class WordPressOptions
{
    public const string SectionName = "WordPress";

    /// <summary>Master switch. When false the connector never calls WordPress.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The site root (e.g. <c>https://expertslive.dk</c>). The REST posts endpoint
    /// is derived as <c>{SiteUrl}/wp-json/wp/v2/posts</c>.
    /// </summary>
    public string SiteUrl { get; set; } = string.Empty;

    /// <summary>WordPress user the Application Password belongs to.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// A WordPress <b>Application Password</b> (Users → Profile → Application
    /// Passwords) — NOT the account password. Used for Basic auth over HTTPS.
    /// </summary>
    public string AppPassword { get; set; } = string.Empty;

    /// <summary>
    /// Default WordPress category ids to file generated drafts under (optional).
    /// </summary>
    public int[] CategoryIds { get; set; } = System.Array.Empty<int>();
}

/// <summary>A post to create in WordPress. v1 ALWAYS creates it as a draft.</summary>
/// <param name="Title">The post title.</param>
/// <param name="ContentHtml">The post body (HTML / Gutenberg-compatible markup).</param>
/// <param name="Excerpt">Optional excerpt / teaser.</param>
public sealed record WordPressDraft(string Title, string ContentHtml, string? Excerpt = null);

/// <summary>Outcome of a WordPress draft-create attempt (honest, never optimistic).</summary>
/// <param name="Created">True when a draft was actually created.</param>
/// <param name="PostId">The WordPress post id, when created.</param>
/// <param name="EditUrl">The wp-admin edit URL for the operator to validate the draft.</param>
/// <param name="Message">Human-readable status.</param>
public sealed record WordPressPublishResult(
    bool Created, long? PostId, string? EditUrl, string Message);

/// <summary>
/// The gated seam for creating WordPress posts (REQUIREMENTS §31). v1 is
/// <b>draft-only</b>: the live impl always posts with <c>status=draft</c> so the
/// operator validates in wp-admin before anything publishes. <see cref="CanWrite"/>
/// keys off configuration so an unconfigured deploy is a no-op.
/// </summary>
public interface IWordPressPublisher
{
    /// <summary>True only when WordPress is enabled AND fully configured (URL + creds).</summary>
    bool CanWrite { get; }

    /// <summary>Create the post as a DRAFT. Throws/returns honest result; never publishes.</summary>
    Task<WordPressPublishResult> CreateDraftAsync(WordPressDraft draft, CancellationToken ct = default);
}

/// <summary>
/// The default no-op publisher used until WordPress is configured. Mirrors
/// <c>NullBackstageSpeakerBioApi</c> / <c>NullLinkedInPostPublisher</c>: never
/// pretends to have posted.
/// </summary>
public sealed class NullWordPressPublisher : IWordPressPublisher
{
    public bool CanWrite => false;

    public Task<WordPressPublishResult> CreateDraftAsync(WordPressDraft draft, CancellationToken ct = default) =>
        Task.FromResult(new WordPressPublishResult(
            false, null, null, "WordPress connector is not configured — no draft created."));
}
