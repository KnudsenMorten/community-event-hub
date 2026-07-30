using CommunityHub.Core.Domain;
using CommunityHub.Core.Tasks;
using CommunityHub.Core.Tasks.Data;
using CommunityHub.Core.Tasks.Definitions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §684.21 / §684.23 — THE KEY-CHANGE TRAP, pinned.
/// </summary>
/// <remarks>
/// <para>§684.21 called this out before a line was written, because it had already bitten once:
/// <c>SourceKey</c> is the dedup identity for BOTH task upserts and reminder occasions, so changing
/// it carelessly re-notifies every participant about tasks they have held for months. That is
/// §664.1 exactly — a ledger key mismatch producing a wrong notification decision on live data.</para>
///
/// <para>§684.23 then relaxed the requirement to <i>"purge-and-re-seed is the default; do the key
/// mapping only if it stays trivial"</i>. It turned out there is NO mapping to do at all: a migrated
/// definition keeps its title, the key derives from the title, so the key is unchanged. These tests
/// are what keeps that true — and what makes the corollary enforceable: <b>do not rename a task
/// while migrating it.</b></para>
/// </remarks>
public class SponsorTaskKeyIdentityTests
{
    /// <summary>
    /// The key shape <c>SponsorOrderPullService</c> has always produced, reproduced INDEPENDENTLY
    /// here. Deliberately a second implementation rather than a call to the shared helper: a test
    /// that calls the code under test to compute its own expectation cannot detect a change to it.
    /// </summary>
    private static string LegacySourceKey(string companyId, string title)
    {
        var chars = title.ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c == ' ')
            .ToArray();
        var slug = new string(chars).Replace(' ', '-');
        if (slug.Length > 60) slug = slug[..60];
        return $"sponsor:{companyId}:{slug}";
    }

    [Fact]
    public void Every_migrated_definition_keeps_the_key_its_json_row_already_had()
    {
        // 🔒 If this fails, the migration is about to re-notify people. The row a company already
        // holds would be pruned as an orphan and re-created under a new key, its completion state
        // lost and its reminder ledger blank — so the chase mail fires again for a task that was
        // finished months ago.
        foreach (var definition in TaskDefinitionRegistry.Shipped.All)
        {
            Assert.Equal(
                LegacySourceKey("42", definition.Title),
                SponsorTaskKeys.For("42", definition.Title));
        }
    }

    [Fact]
    public void The_key_is_derived_from_the_UNSUBSTITUTED_title()
    {
        // The JSON path slugs the title BEFORE placeholder substitution, so the attendee-bag task's
        // "{{expectedAttendees}}" slugs to "expectedattendees", not to "1250". A migration that
        // keyed off the RENDERED title would silently give every such task a new key.
        Assert.Equal(
            "sponsor:42:brochures-for-expectedattendees-attendee-bags",
            SponsorTaskKeys.For("42", "Brochures for {{expectedAttendees}} attendee bags"));
    }

    [Fact]
    public void The_key_stays_inside_the_prefix_the_orphan_prune_scans()
    {
        // The pull deletes this company's pull-managed rows that the CURRENT config no longer
        // produces, matched on the "sponsor:{companyId}:" prefix. A migrated key outside that
        // prefix would survive as an undeletable orphan; one inside it but missing from the desired
        // set would be deleted on the very next run.
        foreach (var definition in TaskDefinitionRegistry.Shipped.All)
        {
            Assert.StartsWith(
                SponsorTaskKeys.PrefixFor("42"), SponsorTaskKeys.For("42", definition.Title));
        }
    }

    [Fact]
    public void A_row_seeded_by_the_old_json_path_resolves_to_its_new_definition()
    {
        // This is what lets an EXISTING row start rendering the new body without being re-created:
        // TaskBodyService matches on the title slug, so the row a sponsor already has is recognised
        // as migrated the moment the definition ships.
        var service = new TaskBodyService(
            TaskDefinitionRegistry.Shipped,
            new TaskBodyStore(),
            new TaskDataResolver(
                Array.Empty<ITaskDataProvider>(),
                new MemoryCache(new MemoryCacheOptions()),
                NullLogger<TaskDataResolver>.Instance));

        foreach (var definition in TaskDefinitionRegistry.Shipped.All)
        {
            var legacyRow = new ParticipantTask
            {
                Title = definition.Title,
                SourceKey = LegacySourceKey("42", definition.Title),
                Description = "the old stored prose",
            };

            Assert.True(service.IsMigrated(legacyRow));
            Assert.Equal(definition.Key, service.DefinitionFor(legacyRow)!.Key);
        }
    }

    [Fact]
    public void An_unmigrated_task_is_left_entirely_alone()
    {
        // 🔒 §684.21 is a REGRESSION GATE: an unmigrated task must keep rendering exactly as it did.
        // The service returns null so the caller stays on the legacy path.
        var service = new TaskBodyService(
            TaskDefinitionRegistry.Shipped,
            new TaskBodyStore(),
            new TaskDataResolver(
                Array.Empty<ITaskDataProvider>(),
                new MemoryCache(new MemoryCacheOptions()),
                NullLogger<TaskDataResolver>.Instance));

        var row = new ParticipantTask
        {
            Title = "Some task that has not been migrated yet",
            SourceKey = "sponsor:42:some-task-that-has-not-been-migrated-yet",
            Description = "**still** rendered the old way",
        };

        Assert.False(service.IsMigrated(row));
        Assert.Null(service.DefinitionFor(row));
    }
}
