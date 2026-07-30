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
    /// <paramref name="cardUrl"/> (§318c): a PUBLIC page whose OpenGraph card should carry
    /// the post's image (e.g. the session page serving the graphic as og:image) — the X
    /// intent adds it as the carded url= param.
    /// <paramref name="intentText"/> (§322p): a COMPACT prefill for the LinkedIn composer
    /// that SURVIVES LinkedIn's ~300-char prefill trim and CONTAINS the card URL — LinkedIn
    /// renders the URL's OpenGraph card (the graphic) AND keeps the text, the §196-proven
    /// combination. <paramref name="text"/> stays the FULL post body (clipboard/paste).
    /// </summary>
    SocialShareDraft BuildDraft(
        SocialNetwork network, string text, string? graphicUrl,
        string? cardUrl = null, string? intentText = null);
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

    public SocialShareDraft BuildDraft(
        SocialNetwork network, string text, string? graphicUrl,
        string? cardUrl = null, string? intentText = null)
    {
        var intentUrl = network switch
        {
            // §322p (operator: "graphics came as a url … and the text is gone — do
            // better"): the ONE web-intent combination that yields BOTH a picture and
            // text on LinkedIn is the feed text-prefill whose text CONTAINS the session
            // URL — LinkedIn renders that URL's OpenGraph card (the graphic) and keeps
            // the text (§196-proven, the pre-§281 working state). The prefill must stay
            // under LinkedIn's ~300-char trim ⇒ use the COMPACT intentText; the FULL body
            // rides the clipboard (§318c auto-copy). share-offsite (card, no text) is the
            // fallback when no compact text was provided.
            SocialNetwork.LinkedIn when intentText is not null =>
                "https://www.linkedin.com/feed/?shareActive=true&text=" + Uri.EscapeDataString(intentText),
            SocialNetwork.LinkedIn when cardUrl is not null =>
                "https://www.linkedin.com/sharing/share-offsite/?url=" + Uri.EscapeDataString(cardUrl),
            // LinkedIn feed share composer — opens prefilled, user posts (no card).
            SocialNetwork.LinkedIn =>
                "https://www.linkedin.com/feed/?shareActive=true&text=" + Uri.EscapeDataString(text),
            // X web-intent — prefilled text (280-char cap is X's), plus the carded url when given.
            SocialNetwork.X =>
                "https://twitter.com/intent/tweet?text=" + Uri.EscapeDataString(intentText ?? text)
                + (cardUrl is not null ? "&url=" + Uri.EscapeDataString(cardUrl) : string.Empty),
            _ => throw new ArgumentOutOfRangeException(nameof(network), network, "Unknown network."),
        };

        return new SocialShareDraft(network, text, graphicUrl, intentUrl);
    }
}
