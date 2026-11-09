namespace CommunityHub.Core.Domain;

/// <summary>
/// §1379.8 — an EXISTING WordPress post that CEH re-renders from its own data and updates in place
/// (same post id, same URL) when <see cref="RefreshWeekly"/> is on.
/// </summary>
/// <remarks>
/// <para>Operator 2026-11-07: <i>"it could be great the the wekkly update works on specific posts - like a
/// flag in ceh wordpress module"</i> · <i>"so it can recalculate and update when new speakers are added"</i>
/// · <i>"remember the count must be dynamic"</i>.</para>
/// <para>🔒 The post itself is created in WordPress (the operator's 19 drafts were); CEH only LINKS it by
/// <see cref="WordPressPostId"/>. §31's draft-only rule is lifted for a flagged post only: an unflagged
/// row is never written to WordPress by the weekly run.</para>
/// </remarks>
public class WordPressRefreshPost
{
    public int Id { get; set; }

    public int EventId { get; set; }

    /// <summary>The WordPress post id (e.g. 10950) this row keeps up to date.</summary>
    public long WordPressPostId { get; set; }

    /// <summary>An organizer-facing label (the post's title, or what it is for). Not pushed to WordPress.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// The post body as HTML with <c>{{Token}}</c> placeholders, rendered by
    /// <see cref="Integrations.WordPressPostTokenRenderer"/> on every refresh.
    /// </summary>
    public string BodyTemplate { get; set; } = string.Empty;

    /// <summary>When on, the weekly run (and "Refresh now") rewrites the post's content in place.</summary>
    public bool RefreshWeekly { get; set; }

    /// <summary>When the post was last ATTEMPTED (pushed, unchanged, or refused) — drives the 7-day cadence.</summary>
    public DateTimeOffset? LastRefreshedAt { get; set; }

    /// <summary>The outcome of that attempt, in words.</summary>
    public string? LastRefreshResult { get; set; }

    /// <summary>SHA-256 of the HTML last pushed, so an unchanged render is not re-sent.</summary>
    public string? LastPushedHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
}
