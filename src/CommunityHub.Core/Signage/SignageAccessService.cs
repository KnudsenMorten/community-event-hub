using System.Security.Cryptography;
using System.Text;
using CommunityHub.Core.Config;
using CommunityHub.Core.Domain.Signage;

namespace CommunityHub.Core.Signage;

/// <summary>
/// §754 §10 — decides whether a screen gets CONTENT or the HOLDING SCREEN. Pure: every input
/// (settings, token, clock, timezone) is passed in, so the whole access contract is unit-testable
/// without a request.
/// </summary>
/// <remarks>
/// <para>🔒 <b>There is no "denied" outcome, only "holding".</b> Every negative — switched off,
/// outside the schedule, wrong token, missing token, no settings row yet — resolves to the same
/// HTTP 200 holding screen. An OptiSigns player has no way to report a failed asset to a human; it
/// displays whatever came back. So a 401 or a 404 does not protect anything — it renders an error
/// page two metres tall in a public corridor, which is strictly worse than a logo and a date.</para>
///
/// <para>🔑 The <see cref="HoldingReason"/> exists for the ADMIN page and the logs, not for the
/// screen. The screen never states why it is holding: "invalid token" on a wall tells a passer-by
/// there is a token to guess.</para>
/// </remarks>
public static class SignageAccessService
{
    /// <summary>Why a screen is showing the holding page. Never rendered to the screen itself.</summary>
    public enum HoldingReason
    {
        None = 0,
        NotConfigured,
        TokenMissing,
        TokenInvalid,
        ViewSwitchedOff,
        OutsideSchedule,
    }

    /// <summary>The outcome of one access check.</summary>
    public sealed record Decision(bool ShowContent, HoldingReason Reason)
    {
        public static readonly Decision Allowed = new(true, HoldingReason.None);
        public static Decision Holding(HoldingReason reason) => new(false, reason);
    }

    /// <summary>
    /// Resolve one request. <paramref name="nowUtc"/> is the SERVER's clock — the schedule is an
    /// operator control and must not be movable by a screen's own clock, unlike the §5 slot rollover
    /// which is deliberately driven by the player.
    /// </summary>
    public static Decision Check(
        SignageSettings? settings,
        SignageOrientation orientation,
        SignageView view,
        string? token,
        DateTimeOffset nowUtc,
        string? timezoneId)
    {
        // No settings row ⇒ signage has never been configured for this edition. Holding, not an
        // error: pointing a player at a URL before an organiser has set it up is ordinary sequencing.
        if (settings is null) return Decision.Holding(HoldingReason.NotConfigured);

        if (string.IsNullOrWhiteSpace(token)) return Decision.Holding(HoldingReason.TokenMissing);

        var expected = orientation == SignageOrientation.Portrait
            ? settings.PortraitToken
            : settings.LandscapeToken;

        // 🔒 An UNSET token never matches — otherwise a blank column plus a blank query string would
        // authorise every screen on the internet. Checked before the compare, because a
        // constant-time compare of two empty strings succeeds.
        if (string.IsNullOrWhiteSpace(expected)) return Decision.Holding(HoldingReason.NotConfigured);

        if (!FixedTimeEquals(token!, expected)) return Decision.Holding(HoldingReason.TokenInvalid);

        if (!IsViewEnabled(settings, orientation, view))
            return Decision.Holding(HoldingReason.ViewSwitchedOff);

        if (!IsWithinSchedule(settings, nowUtc, timezoneId))
            return Decision.Holding(HoldingReason.OutsideSchedule);

        return Decision.Allowed;
    }

    /// <summary>The six on/off switches, resolved.</summary>
    public static bool IsViewEnabled(
        SignageSettings s, SignageOrientation orientation, SignageView view) =>
        orientation == SignageOrientation.Portrait
            ? view switch
            {
                SignageView.Now => s.PortraitNowEnabled,
                SignageView.Next => s.PortraitNextEnabled,
                SignageView.Feedback => s.PortraitFeedbackEnabled,
                _ => false,
            }
            : view switch
            {
                SignageView.Now => s.LandscapeNowEnabled,
                SignageView.Next => s.LandscapeNextEnabled,
                SignageView.Feedback => s.LandscapeFeedbackEnabled,
                _ => false,
            };

    /// <summary>
    /// The §10 schedule: a venue-local daily window inside an optional date range. Every part is
    /// optional and an unset part imposes no restriction.
    /// </summary>
    /// <remarks>
    /// 🔒 <b>Unset means ALWAYS ON, never always off.</b> The failure direction matters: a
    /// misconfigured schedule that blanks every screen during the keynote is unrecoverable in the
    /// moment, while one that leaves them on outside event hours is merely untidy.
    ///
    /// <para>🔑 A window whose end is BEFORE its start is read as crossing midnight (22:00 → 02:00),
    /// not as an empty window — an evening party is the obvious case, and reading it as empty would
    /// switch the screens off exactly when the venue is busiest.</para>
    /// </remarks>
    public static bool IsWithinSchedule(
        SignageSettings s, DateTimeOffset nowUtc, string? timezoneId)
    {
        var local = EventLocalTime.ToLocal(nowUtc, timezoneId);
        var localDate = DateOnly.FromDateTime(local.DateTime);
        var localTime = TimeOnly.FromDateTime(local.DateTime);

        if (s.ActiveFromLocal is { } from && localDate < from) return false;
        if (s.ActiveToLocal is { } to && localDate > to) return false;

        if (s.DailyFromLocal is not { } dayFrom || s.DailyToLocal is not { } dayTo) return true;

        return dayFrom <= dayTo
            ? localTime >= dayFrom && localTime <= dayTo          // an ordinary same-day window
            : localTime >= dayFrom || localTime <= dayTo;         // crosses midnight
    }

    /// <summary>
    /// Mint a token: 32 CSPRNG bytes, URL-safe base64. Same shape and same reasoning as the §753
    /// device provisioning secret.
    /// </summary>
    /// <remarks>
    /// 🔑 Long and opaque because it lives in a URL pasted into a third-party playlist and travels
    /// through logs and screenshots — it must survive being seen without being guessable, and it is
    /// never typed by a human, so length costs nothing.
    /// </remarks>
    public static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    // Constant-time comparison — a token check that returns early leaks its prefix to anyone who
    // can time the response, and these pages are anonymous by design.
    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
