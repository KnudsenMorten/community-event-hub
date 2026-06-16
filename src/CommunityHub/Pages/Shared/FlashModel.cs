namespace CommunityHub.Pages.Shared;

/// <summary>
/// View model for the shared <c>_Flash</c> toast partial (REQUIREMENTS §21 shared
/// UX components). A page that re-renders <c>Page()</c> (rather than redirecting)
/// can pass an explicit message + kind:
/// <code>&lt;partial name="_Flash" model='new FlashModel(Model.Message, "success")' /&gt;</code>
/// Pages that redirect after a POST should instead set
/// <c>TempData["Flash"]</c> / <c>TempData["FlashKind"]</c> and render
/// <c>&lt;partial name="_Flash" /&gt;</c> — the partial reads TempData when no
/// model is supplied.
/// </summary>
/// <param name="Message">The human-friendly outcome text (already localized).</param>
/// <param name="Kind">"success" (default) or "error".</param>
public sealed record FlashModel(string? Message, string Kind = "success")
{
    /// <summary>Convenience for the common success case.</summary>
    public static FlashModel Success(string? message) => new(message, "success");

    /// <summary>Convenience for the error case.</summary>
    public static FlashModel Error(string? message) => new(message, "error");
}
