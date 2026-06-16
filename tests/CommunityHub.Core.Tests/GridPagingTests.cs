using CommunityHub.Core.Organizer;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Pure paging-maths for the organizer grids (REQUIREMENTS §21): the grids page
/// server-side, so <see cref="GridPaging.Resolve"/> must clamp a (possibly
/// hand-edited) page request into range and compute the right SQL <c>Skip</c>.
/// </summary>
public sealed class GridPagingTests
{
    [Fact]
    public void Resolve_first_page_skips_zero_and_reports_range()
    {
        var p = GridPaging.Resolve(requestedPage: 1, requestedSize: 25, totalItems: 60);

        Assert.Equal(1, p.Page);
        Assert.Equal(0, p.Skip);
        Assert.Equal(3, p.TotalPages);          // 60 / 25 = 3 pages
        Assert.Equal(1, p.FirstRowNumber);
        Assert.Equal(25, p.LastRowNumber);
        Assert.False(p.HasPrevious);
        Assert.True(p.HasNext);
    }

    [Fact]
    public void Resolve_middle_page_computes_skip_and_both_neighbours()
    {
        var p = GridPaging.Resolve(requestedPage: 2, requestedSize: 25, totalItems: 60);

        Assert.Equal(2, p.Page);
        Assert.Equal(25, p.Skip);
        Assert.Equal(26, p.FirstRowNumber);
        Assert.Equal(50, p.LastRowNumber);
        Assert.True(p.HasPrevious);
        Assert.True(p.HasNext);
    }

    [Fact]
    public void Resolve_last_partial_page_clamps_last_row_to_total()
    {
        var p = GridPaging.Resolve(requestedPage: 3, requestedSize: 25, totalItems: 60);

        Assert.Equal(3, p.Page);
        Assert.Equal(50, p.Skip);
        Assert.Equal(51, p.FirstRowNumber);
        Assert.Equal(60, p.LastRowNumber);      // not 75
        Assert.False(p.HasNext);
    }

    [Fact]
    public void Resolve_overshoot_page_is_clamped_to_last_page()
    {
        var p = GridPaging.Resolve(requestedPage: 999, requestedSize: 25, totalItems: 60);
        Assert.Equal(3, p.Page);
        Assert.Equal(50, p.Skip);
    }

    [Fact]
    public void Resolve_zero_or_negative_page_is_clamped_to_one()
    {
        Assert.Equal(1, GridPaging.Resolve(0, 25, 60).Page);
        Assert.Equal(1, GridPaging.Resolve(-5, 25, 60).Page);
    }

    [Fact]
    public void Resolve_empty_result_is_one_page_with_zero_rows()
    {
        var p = GridPaging.Resolve(requestedPage: 1, requestedSize: 25, totalItems: 0);
        Assert.Equal(1, p.Page);
        Assert.Equal(1, p.TotalPages);
        Assert.Equal(0, p.FirstRowNumber);
        Assert.Equal(0, p.LastRowNumber);
        Assert.False(p.HasNext);
        Assert.False(p.HasPrevious);
    }

    [Fact]
    public void Resolve_caps_runaway_page_size_at_max()
    {
        var p = GridPaging.Resolve(requestedPage: 1, requestedSize: 100_000, totalItems: 500);
        Assert.Equal(GridPaging.MaxPageSize, p.PageSize);
    }

    [Fact]
    public void Resolve_zero_size_falls_back_to_default()
    {
        var p = GridPaging.Resolve(requestedPage: 1, requestedSize: 0, totalItems: 500);
        Assert.Equal(GridPaging.DefaultPageSize, p.PageSize);
    }
}
