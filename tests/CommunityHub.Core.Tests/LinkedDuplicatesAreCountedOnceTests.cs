using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.DocLibrary;
using CommunityHub.Core.Tests.Scenario;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1257 — one human with two participant rows is ordered for ONCE, and the rule lives in the
/// SHARED audience every logistics surface already asks.
///
/// <para><b>Why here and not in a report.</b> <c>OrderCountService</c> has excluded a linked
/// duplicate since it was written, while the hotel, swag, lunch, food and expo producers — and the
/// Excel exports — did not: they asked <see cref="LogisticsAudience"/>, which knew only about
/// active, non-test rows. So the entitlement tally and the order sheets could disagree about the
/// same person, with nothing to say which was right. Putting the rule in the shared filter makes
/// every consumer agree by construction, and makes a de-duplication report unnecessary.</para>
///
/// <para>Measured in PROD 2026-09-20 before this shipped: 329 participants, 3 humans holding two
/// ACTIVE rows, 0 rows linked — so this changes no number until an organizer links someone, which
/// is exactly the safety property wanted on a live edition.</para>
/// </summary>
public class LinkedDuplicatesAreCountedOnceTests
{
    private const int EventId = 1;

    private static Participant Row(int id, string name, bool active = true, int? sameAs = null) =>
        new()
        {
            Id = id, EventId = EventId, FullName = name, Email = $"{id}@x.dk",
            Role = ParticipantRole.Speaker, IsActive = active, SamePersonAsId = sameAs,
        };

    private static async Task<CommunityHub.Core.Data.CommunityHubDbContext> SeedAsync()
    {
        var db = ScenarioFixture.NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        });
        db.Participants.AddRange(
            Row(1, "Ronni Pedersen"),                     // primary
            Row(2, "Ronni Pedersen", sameAs: 1),          // the second row, linked
            Row(3, "Someone Else"));
        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task A_linked_row_is_not_countable()
    {
        using var db = await SeedAsync();

        var countable = await LogisticsAudience.Countable(db.Participants, EventId)
            .Select(p => p.Id).ToListAsync();

        Assert.Equal(new[] { 1, 3 }, countable.OrderBy(i => i));
    }

    [Fact]
    public async Task The_excluded_list_names_exactly_who_was_left_out()
    {
        // The pair a person ordering needs: the number, and who is deliberately not in it.
        using var db = await SeedAsync();

        var excluded = await LogisticsAudience.ExcludedDuplicates(db.Participants, EventId)
            .Select(p => p.Id).ToListAsync();

        Assert.Equal(new[] { 2 }, excluded);
    }

    [Fact]
    public void The_in_memory_twin_agrees_with_the_query()
    {
        // ⚠️ Two expressions of one rule is the shape that drifts — the class says so itself.
        Assert.True(LogisticsAudience.Counts(Row(1, "A"), EventId));
        Assert.False(LogisticsAudience.Counts(Row(2, "A", sameAs: 1), EventId));
    }

    [Fact]
    public async Task An_UNLINKED_second_row_still_counts_twice_and_that_is_deliberate()
    {
        // 🔒 Nothing is inferred from a shared name: PROD holds two rows called "Finance" that are
        // two different companies' invoice inboxes. Only an organizer's LINK removes a row from a
        // count — so this rule can never quietly under-order.
        using var db = ScenarioFixture.NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        });
        db.Participants.AddRange(Row(1, "Finance"), Row(2, "Finance"));
        await db.SaveChangesAsync();

        var countable = await LogisticsAudience.Countable(db.Participants, EventId).CountAsync();

        Assert.Equal(2, countable);
    }

    [Fact]
    public async Task A_linked_row_that_is_INACTIVE_is_excluded_by_the_older_rule_too()
    {
        // Belt and braces: the new clause must not accidentally re-admit anyone.
        using var db = ScenarioFixture.NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, CommunityName = "C", DisplayName = "C 2027", Code = "C27", IsActive = true,
            StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 10),
        });
        db.Participants.AddRange(Row(1, "A"), Row(2, "A", active: false, sameAs: 1));
        await db.SaveChangesAsync();

        Assert.Equal(1, await LogisticsAudience.Countable(db.Participants, EventId).CountAsync());
        Assert.Empty(await LogisticsAudience.ExcludedDuplicates(db.Participants, EventId).ToListAsync());
    }
}
