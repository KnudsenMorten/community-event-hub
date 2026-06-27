using CommunityHub.Assistant;
using CommunityHub.Auth;
using CommunityHub.Core.Assistant;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Api;

/// <summary>
/// The AI Community Helper's chat endpoint (REQUIREMENTS §129). <c>POST /api/ai-helper</c>,
/// authenticated.
///
/// SECURITY: the participant (id + role + event) is resolved SERVER-SIDE from the
/// signed-in principal — the request body carries ONLY the question. The grounding is
/// then assembled with authorization-at-retrieval (role-scoped content + own rows
/// only) by <see cref="IAiHelperGroundingBuilder"/>, so a client cannot widen its own
/// scope or read another person's data. A simple in-memory per-participant rate limit
/// guards against abuse.
/// </summary>
[Authorize]
public sealed class AiHelperModel : PageModel
{
    private readonly IAiHelperAssistant _assistant;
    private readonly IAiHelperGroundingBuilder _grounding;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly AiHelperRateLimiter _rateLimiter;
    private readonly FeedbackIntakeService _feedback;

    public AiHelperModel(
        IAiHelperAssistant assistant,
        IAiHelperGroundingBuilder grounding,
        ICurrentParticipantAccessor participant,
        AiHelperRateLimiter rateLimiter,
        FeedbackIntakeService feedback)
    {
        _assistant = assistant;
        _grounding = grounding;
        _participant = participant;
        _rateLimiter = rateLimiter;
        _feedback = feedback;
    }

    public sealed class AiHelperRequest
    {
        public string? Question { get; set; }

        /// <summary>
        /// When true this is the explicit "send my message to the organizers" path
        /// (REQUIREMENTS §137): the message is forwarded to the organizers instead of
        /// answered by the assistant. Identity is still server-resolved.
        /// </summary>
        public bool ContactOrganizers { get; set; }

        /// <summary>The page the user is on, for feed/intake context (not security-sensitive).</summary>
        public string? PageUrl { get; set; }
    }

    public async Task<IActionResult> OnPostAsync(
        [FromBody] AiHelperRequest? request, CancellationToken ct)
    {
        // Identity comes from the cookie principal — NEVER from the request body.
        var me = _participant.Current;
        if (me is null)
        {
            return new JsonResult(new { answer = "Please sign in to chat with the Community Helper." })
            {
                StatusCode = StatusCodes.Status401Unauthorized,
            };
        }

        var question = (request?.Question ?? string.Empty).Trim();
        if (question.Length == 0)
        {
            return new JsonResult(new { answer = "Ask me something and I'll do my best to help. 😊" });
        }
        // Cap input length defensively (the grounding, not a long prompt, drives answers).
        if (question.Length > 1000)
        {
            question = question.Substring(0, 1000);
        }

        if (!_rateLimiter.TryAcquire(me.ParticipantId))
        {
            return new JsonResult(new
            {
                answer = "You're asking a lot quickly! Give me a moment and try again shortly.",
            })
            {
                StatusCode = StatusCodes.Status429TooManyRequests,
            };
        }

        // Page context for the feed item (not security-sensitive; identity is server-resolved).
        var pageUrl = request?.PageUrl;
        if (pageUrl is { Length: > 1000 }) pageUrl = pageUrl.Substring(0, 1000);
        var origin = new FeedbackOrigin(me.EventId, me.ParticipantId, me.Role, pageUrl);

        // §137 EXPLICIT "contact the organizers" path: forward the message to the
        // organizers (the §136 replacement contact channel) — no assistant answer.
        if (request?.ContactOrganizers == true)
        {
            var sent = await _feedback.SendToOrganizersAsync(question, origin, ct);
            return new JsonResult(new
            {
                answer = sent.ConfirmationMessage,
                available = true,
                captured = true,
                kind = sent.Kind?.ToString(),
            });
        }

        // Authorization-at-retrieval: grounding built from the SERVER role + id.
        var context = await _grounding.BuildAsync(me.EventId, me.ParticipantId, me.Role, ct);
        var answer = await _assistant.AskAsync(question, context, ct);

        // §137 INTAKE: detect a bug/feature report in the user's message; if so, capture it
        // to the CEH feed + email the team, and confirm to the user (works even when the
        // assistant itself is unconfigured, so reports are never lost).
        var intake = await _feedback.TryIntakeAsync(question, origin, ct);
        var text = answer.Text;
        if (intake.Captured)
        {
            text = answer.Available
                ? answer.Text + "\n\n" + intake.ConfirmationMessage
                : intake.ConfirmationMessage;
        }

        return new JsonResult(new
        {
            answer = text,
            available = answer.Available,
            captured = intake.Captured,
            kind = intake.Kind?.ToString(),
        });
    }
}
