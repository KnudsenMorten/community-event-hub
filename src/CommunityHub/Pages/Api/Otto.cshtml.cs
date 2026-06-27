using CommunityHub.Assistant;
using CommunityHub.Auth;
using CommunityHub.Core.Assistant;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Api;

/// <summary>
/// Otto's chat endpoint (REQUIREMENTS §129). <c>POST /api/otto</c>, authenticated.
///
/// SECURITY: the participant (id + role + event) is resolved SERVER-SIDE from the
/// signed-in principal — the request body carries ONLY the question. The grounding is
/// then assembled with authorization-at-retrieval (role-scoped content + own rows
/// only) by <see cref="IOttoGroundingBuilder"/>, so a client cannot widen its own
/// scope or read another person's data. A simple in-memory per-participant rate limit
/// guards against abuse.
/// </summary>
[Authorize]
public sealed class OttoModel : PageModel
{
    private readonly IOttoAssistant _assistant;
    private readonly IOttoGroundingBuilder _grounding;
    private readonly ICurrentParticipantAccessor _participant;
    private readonly OttoRateLimiter _rateLimiter;

    public OttoModel(
        IOttoAssistant assistant,
        IOttoGroundingBuilder grounding,
        ICurrentParticipantAccessor participant,
        OttoRateLimiter rateLimiter)
    {
        _assistant = assistant;
        _grounding = grounding;
        _participant = participant;
        _rateLimiter = rateLimiter;
    }

    public sealed class OttoRequest
    {
        public string? Question { get; set; }
    }

    public async Task<IActionResult> OnPostAsync(
        [FromBody] OttoRequest? request, CancellationToken ct)
    {
        // Identity comes from the cookie principal — NEVER from the request body.
        var me = _participant.Current;
        if (me is null)
        {
            return new JsonResult(new { answer = "Please sign in to chat with Otto." })
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

        // Authorization-at-retrieval: grounding built from the SERVER role + id.
        var context = await _grounding.BuildAsync(me.EventId, me.ParticipantId, me.Role, ct);
        var answer = await _assistant.AskAsync(question, context, ct);

        return new JsonResult(new { answer = answer.Text, available = answer.Available });
    }
}
