using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Auth;

/// <summary>
/// The PIN implementation of <see cref="IIdentityProvider"/> (CONTEXT.md
/// section 5 / 5a). It verifies a 6-digit PIN that was issued by
/// <see cref="PinLoginService"/>: the PIN must match the stored hash, be
/// unexpired, and unused. On success the PIN is marked consumed (single-use).
///
/// This is the FIRST and currently only identity provider. A future verified
/// Backstage SSO provider implements the same interface and slots in beside
/// it - the rest of the app depends only on <see cref="IIdentityProvider"/>.
/// An unsigned / forgeable claim never reaches success here: only a PIN that
/// verifies against a freshly-issued, unexpired, unused hash does.
/// </summary>
public sealed class PinIdentityProvider : IIdentityProvider
{
    /// <summary>
    /// A PIN is locked after this many wrong guesses.
    ///
    /// <para><b>Operator decision 2026-07-27: 5 → 10.</b> Asked for as <i>"pin failed guess increase
    /// to 10 per hour (not 1000)"</i> — deliberately a RAISE, not a removal, unlike the other limits
    /// changed the same day.</para>
    ///
    /// <para><b>One clarification, because it is not per hour:</b> this counter lives on the
    /// individual PIN, and a PIN expires 15 minutes after it is issued. So it is 10 wrong guesses
    /// per LOGIN ATTEMPT, and requesting a fresh PIN starts a fresh 10. That delivers what was
    /// asked — somebody fat-fingering a code now has real room — while keeping the property that
    /// matters: one PIN cannot be ground down indefinitely.</para>
    ///
    /// <para>This is the ONLY brute-force control on sign-in, which is why it stays small. The
    /// request-rate cap in <c>PinLoginService</c> was effectively removed the same day; that one
    /// governed how many PINs could be MAILED, not how many could be GUESSED.</para>
    /// </summary>
    private const int MaxFailedAttempts = 10;

    private readonly CommunityHubDbContext _db;
    private readonly PinService _pinService;
    private readonly TimeProvider _clock;

    public PinIdentityProvider(
        CommunityHubDbContext db,
        PinService pinService,
        TimeProvider clock)
    {
        _db = db;
        _pinService = pinService;
        _clock = clock;
    }

    public string Name => "pin";

    public async Task<IdentityResult> EstablishIdentityAsync(
        int eventId,
        IdentityClaim claim,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(claim.Pin))
        {
            return IdentityResult.Fail("No PIN supplied.");
        }

        var email = PinLoginService.NormalizeEmail(claim.Email);
        var now = _clock.GetUtcNow();

        // Match the PRIMARY email or the optional ALTERNATE login email (§26d). The
        // resolved participant's primary Email stays the canonical cookie identity.
        var participant = await _db.Participants
            .FirstOrDefaultAsync(
                p => p.EventId == eventId
                     && (p.Email == email || p.AlternateEmail == email)
                     && p.IsActive
                     // Onboarding gate: a not-yet-activated queue entry cannot
                     // sign in (login requires IsActive AND lifecycle Active).
                     && p.LifecycleState == ParticipantLifecycleState.Active,
                cancellationToken);

        if (participant is null)
        {
            // Same generic failure as a wrong PIN - do not reveal whether the
            // email is registered.
            return IdentityResult.Fail("Invalid email or code.");
        }

        // The newest still-redeemable PIN for this participant.
        var loginPin = await _db.LoginPins
            .Where(p => p.ParticipantId == participant.Id
                        && p.ConsumedAt == null
                        && p.ExpiresAt > now
                        && p.FailedAttempts < MaxFailedAttempts)
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (loginPin is null)
        {
            return IdentityResult.Fail("Invalid email or code.");
        }

        if (!_pinService.VerifyPin(claim.Pin, loginPin.PinHash))
        {
            // Count the miss; a PIN locks itself after MaxFailedAttempts so a
            // single PIN cannot be brute-forced.
            loginPin.FailedAttempts++;
            await _db.SaveChangesAsync(cancellationToken);
            return IdentityResult.Fail("Invalid email or code.");
        }

        // Success - consume the PIN (single-use) and record the login.
        loginPin.ConsumedAt = now;
        participant.LastLoginAt = now;
        await _db.SaveChangesAsync(cancellationToken);

        return IdentityResult.Success(participant);
    }
}
