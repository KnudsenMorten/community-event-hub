using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §585 — pins the LIVE-MEASURED Zoho Backstage v3 "does this resource accept ?page" matrix.
///
/// <para>WHY THIS TEST EXISTS. The non-strict pager <c>yield break</c>s on any non-2xx response, and
/// a resource that rejects <c>?page</c> answers <c>400 {"message":"Extra param found"}</c>. So a
/// resource missing from the reject list does not fail loudly — it returns an EMPTY LIST, with no
/// error, no log line and no failure count. The caller concludes the resource is empty.</para>
///
/// <para>That is not hypothetical. <b>halls</b> was missing, and it is the §585 bug the operator
/// reported on 2026-07-28 — *"bug: room was not added in zoho when synchronized from ceh"*.
/// <c>GetHallsAsync</c> returned empty on every pass, so no CEH room could match a Backstage hall
/// even though all 11 halls existed with names matching CEH's room strings character for character,
/// and all 9 live sessions were created with <c>venue: null</c>. <b>speakers</b> was missing too and
/// had not been noticed at all, because every consumer of the live speaker index fails SAFE on an
/// empty result (self-heal skips, adopt-by-email never adopts).</para>
///
/// <para>The matrix below was measured against PROD on 2026-07-28 by requesting each resource bare
/// and then with <c>?page=1</c>. Re-measure before changing it — do not reason about it.</para>
/// </summary>
public class ZohoPageParamMatrixTests
{
    /// <summary>Resources that REJECT ?page (400 "Extra param found") and must be read bare.</summary>
    [Theory]
    [InlineData("agendas")]
    [InlineData("sessions")]
    [InlineData("tracks")]
    [InlineData("halls")]      // §585 — was missing: the room/venue bug
    [InlineData("speakers")]   // §585 — was missing: silently empty speaker index
    public void Resources_that_reject_the_page_param_are_read_bare(string resource) =>
        Assert.True(ZohoClient.ResourceRejectsPageParam(resource),
            $"'{resource}' answers 400 \"Extra param found\" when ?page is sent. It MUST be read "
            + "bare, or the pager yield-breaks and the caller silently sees an empty list.");

    /// <summary>Resources that genuinely paginate — these MUST keep sending ?page.</summary>
    [Theory]
    [InlineData("booths")]
    [InlineData("sponsors")]
    [InlineData("exhibitors")]
    [InlineData("attendees")]
    [InlineData("orders")]
    public void Resources_that_paginate_still_send_the_page_param(string resource) =>
        Assert.False(ZohoClient.ResourceRejectsPageParam(resource),
            $"'{resource}' really does paginate. Suppressing ?page here would cap every read at "
            + "page one — the §326ar bug that would have soft-cancelled 704 of 1204 attendees.");

    /// <summary>
    /// The query-string tolerance the agenda read depends on: the pager is called as
    /// "sessions?day=1", so the check must compare the PATH only.
    /// </summary>
    [Fact]
    public void Resource_with_a_query_string_is_matched_on_its_path()
    {
        Assert.True(ZohoClient.ResourceRejectsPageParam("sessions?day=1"));
        Assert.True(ZohoClient.ResourceRejectsPageParam("/halls"));
        Assert.False(ZohoClient.ResourceRejectsPageParam("attendees?since=2026-01-01"));
    }
}
