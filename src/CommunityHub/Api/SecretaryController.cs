using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Api;

/// <summary>
/// Secretary secure-token entry point.
///
///   GET /s/{token}
///       Resolves a write-scoped <see cref="ParticipantSecretaryToken"/> and, if
///       valid (not revoked, not expired, active participant), signs the visitor
///       in AS that ONE participant in an acting-as session marked
///       <see cref="ImpersonationActorKind.SecretaryToken"/>, then lands them on
///       the onboarding wizard so they can fill in that person's tasks on their
///       behalf. An invalid / revoked / expired token returns 404 — it is
///       indistinguishable from a never-issued one (no token-existence oracle).
///
/// The token IS the credential; no prior login is required. The resulting
/// session is single-person scoped (it can only ever touch this participant's
/// own data), is NOT an organizer (cannot reach organizer-only areas), and
/// cannot start a further impersonation.
/// </summary>
// Anonymous to the cookie scheme: the secure secretary token IS the credential and no
// prior login is required (it MINTS the acting-as session). An invalid/expired token
// returns 404. Must opt out of the fail-closed FallbackPolicy.
[ApiController]
[AllowAnonymous]
public sealed class SecretaryController : ControllerBase
{
    private readonly SecretaryTokenService _tokens;
    private readonly ImpersonationAuditService _audit;
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public SecretaryController(
        SecretaryTokenService tokens,
        ImpersonationAuditService audit,
        CommunityHubDbContext db,
        TimeProvider clock)
    {
        _tokens = tokens;
        _audit = audit;
        _db = db;
        _clock = clock;
    }

    [HttpGet("/s/{token}")]
    public async Task<IActionResult> EnterAsync(string token, CancellationToken ct)
    {
        var grant = await _tokens.ResolveAsync(token, ct);
        if (grant is null)
        {
            // 404 (not 401) — do not reveal whether a token ever existed.
            return NotFound();
        }

        var target = grant.Participant
            ?? await _db.Participants.FirstOrDefaultAsync(p => p.Id == grant.ParticipantId, ct);
        if (target is null) return NotFound();

        var label = string.IsNullOrWhiteSpace(grant.Label)
            ? $"Secretary for {target.FullName}"
            : grant.Label!;

        await ImpersonationSignIn.SignInAsTargetAsync(
            HttpContext, target,
            ImpersonationActorKind.SecretaryToken,
            actorParticipantId: null,
            actorLabel: label,
            _clock);

        await _audit.RecordAsync(
            grant.EventId, ImpersonationActorKind.SecretaryToken,
            actorParticipantId: null, actorLabel: label,
            targetParticipantId: target.Id,
            action: ImpersonationAuditService.ActionSecretaryUse,
            detail: "Secure link used to enter the participant's context.",
            ct: ct);

        // Land on the onboarding wizard: the secretary's whole job is to fill in
        // this person's onboarding/tasks on their behalf.
        return LocalRedirect("/Forms/OnboardingWizard");
    }
}
