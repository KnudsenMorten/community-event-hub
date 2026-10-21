using CommunityHub.Core.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace CommunityHub.Auth;

/// <summary>
/// §1316 — event partners have the organizer area READ-ONLY (operator 2026-10-02, after §1312 gave them full access:
/// "make the organizer gui read only … for event partners" · "it is critical they can access the export options. they are
/// the ones that need them").
///
/// One global page filter instead of 333 handler edits: a signed-in event partner (not acting-as) may VIEW every organizer
/// page and run every export (all exports are GET handlers), but any change — a POST / PUT / PATCH / DELETE under
/// <c>/Organizer</c> or <c>/Sponsor/Shop</c> (an organizer may confirm a sponsor's order there) — is refused before the
/// handler runs. "Switch to user" is a POST, so a partner cannot step into someone else's session and edit as them either.
/// The one GET that writes (the LinkedIn connect callback stores a token) is refused by name.
/// </summary>
public sealed class EventPartnerReadOnlyFilter : IAsyncPageFilter
{
    /// <summary>Query flag the organizer layout reads to say "that change was not saved".</summary>
    public const string BlockedQueryKey = "readonly";

    private static readonly string[] GuardedPrefixes = { "/Organizer", "/Sponsor/Shop" };

    /// <summary>GET handlers in the organizer area that store something (by handler name).</summary>
    private static readonly HashSet<string> WritingGetHandlers = new(StringComparer.OrdinalIgnoreCase) { "Callback" };

    /// <summary>
    /// POST handlers that only hand out a file, so they stay open: the reservation-rights export
    /// (<c>/Organizer/ShopReservations</c> "Export") and the order PDF (<c>/Sponsor/Shop/Cart</c> "Pdf"). Every other export
    /// is a GET. A new POST download must be named Export… or Pdf… to be reachable for partners — the layout's script uses
    /// the same prefixes to leave its button enabled.
    /// </summary>
    public static readonly string[] ReadingPostHandlerPrefixes = { "Export", "Pdf" };

    private readonly ICurrentParticipantAccessor _participant;

    public EventPartnerReadOnlyFilter(ICurrentParticipantAccessor participant) => _participant = participant;

    /// <summary>True when the request is a change an event partner may not make. Pure — the rule the tests pin.</summary>
    public static bool Blocks(ParticipantRole role, bool isActingAs, string method, string path, string? handler)
    {
        if (role != ParticipantRole.EventPartner || isActingAs) return false;
        if (!GuardedPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return false;

        var isMutation = HttpMethods.IsPost(method) || HttpMethods.IsPut(method)
            || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);
        if (isMutation)
            return handler is null
                || !ReadingPostHandlerPrefixes.Any(p => handler.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        return handler is not null && WritingGetHandlers.Contains(handler);
    }

    /// <summary>True for the signed-in person the layout shows the read-only banner to.</summary>
    public static bool IsReadOnlyViewer(CurrentParticipant? me) =>
        me is not null && me.Role == ParticipantRole.EventPartner && !me.IsActingAs;

    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var me = _participant.Current;
        var request = context.HttpContext.Request;
        if (me is null || !Blocks(me.Role, me.IsActingAs, request.Method, request.Path.Value ?? string.Empty,
                context.HandlerMethod?.Name))
        {
            await next();
            return;
        }

        // A script call gets a plain 403; a form post goes back to the page it came from, which says it was not saved.
        var accept = request.Headers.Accept.ToString();
        var isScript = request.Headers.XRequestedWith == "XMLHttpRequest"
            || (!string.IsNullOrEmpty(accept) && !accept.Contains("text/html", StringComparison.OrdinalIgnoreCase));
        if (isScript)
        {
            context.Result = new ObjectResult("Read-only access: event partners can view and export, not change.")
                { StatusCode = StatusCodes.Status403Forbidden };
            return;
        }

        context.Result = new RedirectResult(BackTo(request));
    }

    /// <summary>The page the form was on (same site only, its own query kept), else the posted path.</summary>
    private static string BackTo(HttpRequest request)
    {
        var target = $"{request.PathBase}{request.Path}";
        if (Uri.TryCreate(request.Headers.Referer.ToString(), UriKind.Absolute, out var referer)
            && string.Equals(referer.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase))
        {
            target = referer.PathAndQuery;
        }
        if (target.Contains($"{BlockedQueryKey}=1", StringComparison.Ordinal)) return target;
        return target + (target.Contains('?') ? "&" : "?") + $"{BlockedQueryKey}=1";
    }
}
