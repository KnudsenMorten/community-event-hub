namespace CommunityHub.Core.Organizer;

/// <summary>
/// Shared server-side paging maths for the high-traffic organizer grids
/// (Participants / Speakers / Attendees). Keeps the page-models honest about
/// clamping the requested page into range and computing the SQL <c>Skip</c> —
/// the grids filter + sort + page in the database, never load everything into
/// memory (REQUIREMENTS §21 Organizer "search/filter/sort + Attendees
/// pagination"). Pure + side-effect free so it can be unit-tested directly.
/// </summary>
public static class GridPaging
{
    /// <summary>Default rows per page for the organizer grids.</summary>
    public const int DefaultPageSize = 25;

    /// <summary>Hard upper bound so a hand-edited <c>?pageSize=</c> can't ask for everything.</summary>
    public const int MaxPageSize = 200;

    /// <summary>
    /// Resolve a request (raw page + size + the post-filter total) into a clamped
    /// <see cref="GridPage"/>: page is forced into <c>1..TotalPages</c>, size into
    /// <c>1..MaxPageSize</c>, and <see cref="GridPage.Skip"/> is ready for EF
    /// <c>.Skip(...).Take(...)</c>.
    /// </summary>
    public static GridPage Resolve(int requestedPage, int requestedSize, int totalItems)
    {
        var size = requestedSize <= 0 ? DefaultPageSize : Math.Min(requestedSize, MaxPageSize);
        var total = Math.Max(0, totalItems);
        var totalPages = total == 0 ? 1 : (int)Math.Ceiling(total / (double)size);

        var page = requestedPage < 1 ? 1 : requestedPage;
        if (page > totalPages) page = totalPages;

        return new GridPage(page, size, total, totalPages, (page - 1) * size);
    }
}

/// <summary>A resolved, clamped grid page (1-based <see cref="Page"/>).</summary>
public readonly record struct GridPage(
    int Page, int PageSize, int TotalItems, int TotalPages, int Skip)
{
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < TotalPages;

    /// <summary>1-based index of the first row shown (0 when empty).</summary>
    public int FirstRowNumber => TotalItems == 0 ? 0 : Skip + 1;

    /// <summary>1-based index of the last row shown (0 when empty).</summary>
    public int LastRowNumber => Math.Min(Skip + PageSize, TotalItems);
}
