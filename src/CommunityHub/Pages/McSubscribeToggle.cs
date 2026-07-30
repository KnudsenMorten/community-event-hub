using CommunityHub.Core.Domain;

namespace CommunityHub.Pages;

/// <summary>
/// §383 — render model for the per-box notification toggle on <c>/MasterClassPage</c>.
/// </summary>
/// <param name="SessionId">The master class the toggle belongs to.</param>
/// <param name="Token">The emailed bearer token, preserved so a no-login attendee stays signed in
/// across the round-trip (dropping it would bounce them to the access-denied state).</param>
/// <param name="Kind">Which of the two notification streams this toggle governs.</param>
/// <param name="Subscribed">Current state — true by default, since a subscription is the absence of
/// an opt-out row.</param>
/// <param name="Description">Screen-reader text saying what the e-mails actually are; the visible
/// label only has room for the state.</param>
public sealed record McSubscribeToggle(
    int SessionId,
    string Token,
    MasterClassNotificationKind Kind,
    bool Subscribed,
    string Description);
