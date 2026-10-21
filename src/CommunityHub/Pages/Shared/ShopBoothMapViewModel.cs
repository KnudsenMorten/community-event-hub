using CommunityHub.Core.Integrations.Webshop;

namespace CommunityHub.Pages.Shared;

/// <summary>§1305 item 11 — what <c>_ShopBoothMap.cshtml</c> draws.</summary>
/// <param name="LinkFor">Where a FREE booth links to (buy / read more); null ⇒ no links.</param>
/// <param name="ShowList">Also show the booths as a table (organizer view, and for screen readers).</param>
public sealed record ShopBoothMapViewModel(
    IReadOnlyList<ShopBoothView> Booths,
    Func<ShopBoothView, string>? LinkFor,
    string? Highlight = null,
    bool ShowList = false);
