using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Volunteers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1156 — the availability grid is grouped, and each group counts itself.
///
/// <para>Operator 2026-08-31: <i>"i need to have all preselected grouped together and then not
/// preselected for easier overview. We still need to see total at the button."</i></para>
///
/// <para>🔑 <b>The subtotal is the point, not the grouping.</b> He preselects from this page, so the
/// question he is really asking is "do the people I have already picked cover this shift?" — and a
/// single combined total cannot answer it. The group counts say what the shortlist gives him; the
/// grand total says what the whole pool could.</para>
///
/// <para>NO real names — Ada / Grace / Katherine only.</para>
/// </summary>
public sealed class VolunteerAvailabilityGroupingTests
{
    private const int EventId = 1;
    private static readonly DateOnly Day = new(2027, 2, 10);

    private static CommunityHubDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseInMemoryDatabase($"vol-group-{Guid.NewGuid():N}").Options);

    private static async Task<int> AddAsync(
        CommunityHubDbContext db, string name, ParticipantLifecycleState state, string slot)
    {
        var p = new Participant
        {
            EventId = EventId, FullName = name, Email = $"{Guid.NewGuid():N}@example.test",
            Role = ParticipantRole.Volunteer, LifecycleState = state,
        };
        db.Participants.Add(p);
        await db.SaveChangesAsync();

        db.VolunteerDayAvailabilities.Add(new VolunteerDayAvailability
        {
            EventId = EventId, ParticipantId = p.Id, Day = Day,
            Level = slot == "Full day" ? VolunteerAvailabilityLevel.Full
                  : slot == "Not able to help" ? VolunteerAvailabilityLevel.Unavailable
                  : VolunteerAvailabilityLevel.Half,
            Note = $"[{slot}]",
        });
        await db.SaveChangesAsync();
        return p.Id;
    }

    [Fact]
    public async Task Preselected_comes_FIRST_and_the_rest_follow()
    {
        using var db = NewDb();
        await AddAsync(db, "Ada Lovelace", ParticipantLifecycleState.Inactive, "Full day");
        await AddAsync(db, "Grace Hopper", ParticipantLifecycleState.Preselected, "Full day");

        var view = await new VolunteerAvailabilityOverviewService(db).BuildAsync(EventId);

        Assert.NotNull(view.Groups);
        Assert.Equal(2, view.Groups!.Count);
        Assert.StartsWith("Preselected", view.Groups[0].Label);
        Assert.StartsWith("Not preselected", view.Groups[1].Label);
    }

    /// <summary>
    /// 🔑 The subtotal must describe ITS OWN group, not the whole page.
    /// </summary>
    /// <remarks>
    /// This is the number he staffs a shift from: "the four I have shortlisted cover the morning"
    /// is a different fact from "seventeen people could".
    /// </remarks>
    [Fact]
    public async Task Each_group_counts_only_its_own_people()
    {
        using var db = NewDb();
        await AddAsync(db, "Grace Hopper", ParticipantLifecycleState.Preselected, "Full day");
        await AddAsync(db, "Katherine Johnson", ParticipantLifecycleState.Preselected, "Full day");
        await AddAsync(db, "Ada Lovelace", ParticipantLifecycleState.Inactive, "Full day");

        var view = await new VolunteerAvailabilityOverviewService(db).BuildAsync(EventId);

        var preselected = view.Groups!.Single(g => g.Label.StartsWith("Preselected"));
        var rest = view.Groups!.Single(g => g.Label.StartsWith("Not preselected"));

        Assert.Equal(2, preselected.Totals[0].Morning);
        Assert.Equal(1, rest.Totals[0].Morning);

        // 🔒 "We still need to see total at the button" — the grand total still covers everybody.
        Assert.Equal(3, view.Totals[0].Morning);
    }

    [Fact]
    public async Task The_group_label_carries_its_head_count()
    {
        using var db = NewDb();
        await AddAsync(db, "Grace Hopper", ParticipantLifecycleState.Preselected, "Full day");
        await AddAsync(db, "Katherine Johnson", ParticipantLifecycleState.Preselected, "Morning 6:40–12");

        var view = await new VolunteerAvailabilityOverviewService(db).BuildAsync(EventId);

        Assert.Equal("Preselected (2)", view.Groups!.Single().Label);
    }

    /// <summary>
    /// 🔒 An EMPTY group is DROPPED, never rendered as a heading with nothing under it — that reads
    /// as a bug on a page scanned quickly. The queue never contains onboarded people, so that block
    /// must simply be absent there.
    /// </summary>
    [Fact]
    public async Task A_group_with_nobody_in_it_is_not_shown()
    {
        using var db = NewDb();
        await AddAsync(db, "Grace Hopper", ParticipantLifecycleState.Preselected, "Full day");

        var view = await new VolunteerAvailabilityOverviewService(db).BuildAsync(EventId);

        Assert.Single(view.Groups!);
        Assert.DoesNotContain(view.Groups!, g => g.Label.StartsWith("Not preselected"));
        Assert.DoesNotContain(view.Groups!, g => g.Label.StartsWith("Onboarded"));
    }

    [Fact]
    public async Task Onboarded_volunteers_get_their_own_group()
    {
        // They appear on the all-volunteers page but never in the queue.
        using var db = NewDb();
        await AddAsync(db, "Ada Lovelace", ParticipantLifecycleState.Active, "Full day");
        await AddAsync(db, "Grace Hopper", ParticipantLifecycleState.Preselected, "Full day");

        var view = await new VolunteerAvailabilityOverviewService(db).BuildAsync(EventId);

        Assert.Equal(2, view.Groups!.Count);
        Assert.StartsWith("Preselected", view.Groups[0].Label);
        Assert.StartsWith("Onboarded", view.Groups[1].Label);
    }

    [Fact]
    public async Task Every_row_appears_in_exactly_one_group()
    {
        using var db = NewDb();
        await AddAsync(db, "Ada Lovelace", ParticipantLifecycleState.Inactive, "Full day");
        await AddAsync(db, "Grace Hopper", ParticipantLifecycleState.Preselected, "Morning 6:40–12");
        await AddAsync(db, "Katherine Johnson", ParticipantLifecycleState.Active, "Not able to help");

        var view = await new VolunteerAvailabilityOverviewService(db).BuildAsync(EventId);

        // 🔒 The groups must partition the rows: nobody lost, nobody counted twice — otherwise the
        // subtotals cannot add up to the total he reads underneath them.
        var grouped = view.Groups!.SelectMany(g => g.Rows).Select(r => r.ParticipantId).ToList();
        Assert.Equal(view.Rows.Count, grouped.Count);
        Assert.Equal(grouped.Count, grouped.Distinct().Count());
        Assert.Equal(
            view.Rows.Select(r => r.ParticipantId).OrderBy(x => x),
            grouped.OrderBy(x => x));
    }
}
