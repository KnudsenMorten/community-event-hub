using System.Reflection;
using CommunityHub.Auth;
using CommunityHub.Core.Audit;
using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace CommunityHub.Audit;

/// <summary>
/// Global Razor page filter that AUTO-CAPTURES every user action for the unified audit
/// trail (REQUIREMENTS §24). It records one <see cref="AuditEntry"/> per MUTATING
/// request (POST/PUT/PATCH/DELETE) — who, what page+handler, when, and the outcome —
/// so "any action made by any user" is logged with zero per-handler code. Reads
/// (GET/HEAD) are NOT recorded (they would flood the trail and carry no change);
/// high-value reads like calendar sync are instrumented explicitly at their source.
///
/// PRIVACY: it stores the route + handler + actor + outcome, never the posted form
/// values — so PINs, email bodies and other payloads never land in the trail.
///
/// Resilience: the audit write is best-effort (the service swallows its own errors) and
/// happens AFTER the handler runs; the filter never alters the response or suppresses an
/// exception (it records Failure then lets it propagate).
/// </summary>
public sealed class AuditPageFilter : IAsyncPageFilter
{
    private readonly IAuditTrail _audit;
    private readonly ICurrentParticipantAccessor _participant;

    public AuditPageFilter(IAuditTrail audit, ICurrentParticipantAccessor participant)
    {
        _audit = audit;
        _participant = participant;
    }

    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(
        PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var method = context.HttpContext.Request.Method;
        var isMutation = HttpMethods.IsPost(method) || HttpMethods.IsPut(method)
            || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);

        if (!isMutation)
        {
            await next();
            return;
        }

        var executed = await next();   // run the handler, then record the outcome

        try
        {
            var ct = context.HttpContext.RequestAborted;
            var path = context.HttpContext.Request.Path.Value ?? string.Empty;
            var handler = context.HandlerMethod?.Name;   // e.g. OnPostAssign
            var me = _participant.Current;

            var outcome = AuditOutcome.Success;
            if (executed.Exception is not null && !executed.ExceptionHandled)
                outcome = AuditOutcome.Failure;
            else if (executed.Result is ForbidResult
                     || (executed.Result is StatusCodeResult sc && sc.StatusCode == 403)
                     || (executed.Result is ObjectResult or && or.StatusCode == 403))
                outcome = AuditOutcome.Denied;

            var isAuth = path.StartsWith("/Login", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/Logout", StringComparison.OrdinalIgnoreCase);

            // Opt-in richer metadata: an [Audit("…")] on the handler overrides the
            // generic summary/category/target (REQUIREMENTS §24).
            var attr = context.HandlerMethod?.MethodInfo
                .GetCustomAttribute<AuditAttribute>(inherit: false);

            var entry = new AuditEntry
            {
                EventId = me?.EventId ?? 0,
                Category = attr is not null ? attr.Category : (isAuth ? AuditCategory.Auth : AuditCategory.UserAction),
                Action = handler is null ? $"{method} {path}" : $"{method} {path} [{handler}]",
                Summary = attr?.Summary
                    ?? (handler is null ? $"{method} {path}" : $"{Verb(handler)} on {path}"),
                TargetType = attr?.TargetType,
                Outcome = outcome,
                Source = AuditSource.Web,
                HttpMethod = method,
                Path = path,
            };

            if (me is null)
            {
                entry.ActorEmail = "anonymous";
            }
            else if (me.IsActingAs && me.ActingAs is not null)
            {
                // The REAL actor is the impersonator; the session identity is the target.
                entry.ActorParticipantId = me.ActingAs.ActorParticipantId;
                entry.ActorEmail = string.IsNullOrWhiteSpace(me.ActingAs.ActorLabel)
                    ? "acting-as" : me.ActingAs.ActorLabel;
                entry.ActorRole = "Organizer";
                entry.IsActingAs = true;
                entry.OnBehalfOf = me.Email;
            }
            else
            {
                entry.ActorParticipantId = me.ParticipantId;
                entry.ActorEmail = string.IsNullOrWhiteSpace(me.Email) ? "(unknown)" : me.Email;
                entry.ActorRole = me.Role.ToString();
            }

            await _audit.RecordAsync(entry, ct);
        }
        catch
        {
            // Never let auditing affect the request outcome.
        }
    }

    // Turn "OnPostAssignVolunteer" -> "Assign volunteer" for the human summary.
    private static string Verb(string handler)
    {
        var h = handler;
        foreach (var p in new[] { "OnPost", "OnPut", "OnPatch", "OnDelete", "OnGet" })
            if (h.StartsWith(p, StringComparison.Ordinal)) { h = h.Substring(p.Length); break; }
        if (h.EndsWith("Async", StringComparison.Ordinal)) h = h.Substring(0, h.Length - 5);
        if (string.IsNullOrWhiteSpace(h)) return "Submit";
        // Space the PascalCase: "AssignVolunteer" -> "Assign Volunteer".
        var spaced = System.Text.RegularExpressions.Regex.Replace(h, "(?<=[a-z0-9])(?=[A-Z])", " ");
        return spaced;
    }
}
