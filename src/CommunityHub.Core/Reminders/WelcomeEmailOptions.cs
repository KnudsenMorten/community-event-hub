namespace CommunityHub.Core.Reminders;

/// <summary>
/// Options for the welcome email (the per-role variant welcome sent by
/// <see cref="WelcomeWithLoginEmailService"/>).
/// </summary>
public sealed class WelcomeEmailOptions
{
    public const string SectionName = "WelcomeEmail";

    /// <summary>
    /// When <c>true</c> the welcome email carries a one-tap <b>auto-login</b>
    /// magic-link (a single-use token that authenticates the recipient). When
    /// <c>false</c> (the DEFAULT, operator "disable welcome mail with login") NO
    /// token is minted and the plain hub URL is used for the CTA instead, so the
    /// recipient signs in normally with their email + one-time code.
    /// </summary>
    public bool AutoLoginEnabled { get; set; }
}
