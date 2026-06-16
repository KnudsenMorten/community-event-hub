namespace CommunityHub.Core.Integrations.Graphics;

/// <summary>The social network a share targets.</summary>
public enum SocialNetwork
{
    LinkedIn = 0,
    X = 1,
}

/// <summary>
/// A built share DRAFT (REQUIREMENTS §18 step 5). NEVER an auto-post: this carries
/// the prefilled text + the graphic to attach + an "open the composer" intent URL,
/// which the speaker finalizes and posts himself in his own context.
/// </summary>
/// <param name="Network">Which network the draft is for.</param>
/// <param name="Text">The prefilled post body (date, ticket URL, session info).</param>
/// <param name="GraphicUrl">Link to the graphic the speaker attaches (may be null).</param>
/// <param name="IntentUrl">
/// A "share-intent" URL that opens the network's composer prefilled — the speaker
/// reviews + posts. For LinkedIn this is the feed share/compose URL; for X the
/// web-intent tweet URL. Opening it does NOT post anything.
/// </param>
public sealed record SocialShareDraft(
    SocialNetwork Network,
    string Text,
    string? GraphicUrl,
    string IntentUrl);

/// <summary>
/// The social-share seam (REQUIREMENTS §18 step 5). Builds a DRAFT only — it never
/// auto-posts on a user's behalf. Per-user OAuth ("post directly in your own
/// context") is a future slice: <see cref="CanPost"/> defaults FALSE, so the only
/// thing offered is the prefilled draft + a download. A live implementation with
/// per-user OAuth would set <see cref="CanPost"/> true; even then the "I'm speaking
/// at ELDK27" button always produces a DRAFT the speaker finalizes — never an
/// automatic post.
///
/// Clean seam + null default per the repo pattern. The default
/// <see cref="DraftOnlySocialShareGateway"/> builds drafts and cannot post.
/// </summary>
public interface ISocialShareGateway
{
    /// <summary>
    /// Whether per-user OAuth posting is wired. FALSE by default (no OAuth) — the
    /// UI then offers download + the prefilled draft/intent only, never an auto-post.
    /// </summary>
    bool CanPost { get; }

    /// <summary>
    /// Build a share DRAFT (text + graphic + composer intent URL). Pure — no
    /// network call, no posting. Always available regardless of <see cref="CanPost"/>.
    /// </summary>
    SocialShareDraft BuildDraft(SocialNetwork network, string text, string? graphicUrl);
}

/// <summary>
/// Default share gateway: builds drafts + composer-intent URLs but CANNOT post
/// (no per-user OAuth wired). This is the safe default — the speaker always
/// reviews + posts himself. Live per-user OAuth posting is ◻ (pending) —
/// REQUIREMENTS §18.
/// </summary>
public sealed class DraftOnlySocialShareGateway : ISocialShareGateway
{
    public bool CanPost => false;

    public SocialShareDraft BuildDraft(SocialNetwork network, string text, string? graphicUrl)
    {
        var intentUrl = network switch
        {
            // LinkedIn feed share composer — opens prefilled, user posts. (LinkedIn's
            // share-offsite URL only takes a url param; the article/compose composer
            // is the closest prefilled-text surface, so we point at the feed share.)
            SocialNetwork.LinkedIn =>
                "https://www.linkedin.com/feed/?shareActive=true&text=" + Uri.EscapeDataString(text),
            // X web-intent — opens the composer prefilled with text, user posts.
            SocialNetwork.X =>
                "https://twitter.com/intent/tweet?text=" + Uri.EscapeDataString(text),
            _ => throw new ArgumentOutOfRangeException(nameof(network), network, "Unknown network."),
        };

        return new SocialShareDraft(network, text, graphicUrl, intentUrl);
    }
}
