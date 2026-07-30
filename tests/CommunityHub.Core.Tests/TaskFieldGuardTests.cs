using System.Text.Json;
using CommunityHub.Core.Config;
using CommunityHub.Core.Domain;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §682 — the guard, and the regression test that would have caught the incident.
///
/// On 2026-07-29 the §675 DSV shipment copy (2135 characters) was seeded into a
/// 2000-character column. EF threw, and because the task re-seed saves alongside the
/// WooCommerce ORDER pull, the entire background job died — sponsor order sync with it.
/// </summary>
public class TaskFieldGuardTests
{
    [Fact]
    public void Fits_accepts_a_description_exactly_at_the_limit()
    {
        var atLimit = new string('x', ParticipantTask.DescriptionMaxLength);

        Assert.True(TaskFieldGuard.Fits("Title", atLimit, out var reason));
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void Fits_refuses_a_description_one_character_over_and_says_which_field()
    {
        var overLimit = new string('x', ParticipantTask.DescriptionMaxLength + 1);

        Assert.False(TaskFieldGuard.Fits("Title", overLimit, out var reason));

        // The reason has to name the FIELD and both numbers: the whole point is that the
        // operator's log line points at the config edit, not at an EF truncation error
        // several layers away naming only a column.
        Assert.Contains("description", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains((ParticipantTask.DescriptionMaxLength + 1).ToString(), reason, StringComparison.Ordinal);
        Assert.Contains(ParticipantTask.DescriptionMaxLength.ToString(), reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Fits_refuses_an_over_long_title_too()
    {
        var overLimit = new string('x', ParticipantTask.TitleMaxLength + 1);

        Assert.False(TaskFieldGuard.Fits(overLimit, "short body", out var reason));
        Assert.Contains("title", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Fits_tolerates_nulls()
    {
        // A deadline with no body at all is legitimate — it must not be reported as too long.
        Assert.True(TaskFieldGuard.Fits(null, null, out _));
    }

    [Fact]
    public void The_incident_body_would_have_been_refused_by_the_old_2000_column()
    {
        // The exact shape of the outage: 2135 > 2000. Documents WHY the column moved, so a
        // future "why is this 4000?" has an answer that is executable rather than folklore.
        const int oldColumnSize = 2000;
        var incidentBody = new string('x', 2135);

        Assert.True(incidentBody.Length > oldColumnSize);
        Assert.True(incidentBody.Length <= ParticipantTask.DescriptionMaxLength);
    }

    /// <summary>
    /// The one that actually protects PROD: every SHIPPED sponsor task body must fit the
    /// column it is seeded into. §669 is about to add bold markers to all of these, and the
    /// next-longest body is already 1771 characters — without this test that sweep would
    /// re-break the same job, the same way, and again only be found by a crash mail.
    /// </summary>
    /// <remarks>
    /// <para>🔒 §686.2 — <b>the scope of this guard has genuinely CHANGED, and narrowing it is the
    /// correct response rather than a concession to a red test.</b> A MIGRATED task stores no
    /// rendered prose at all (§684.14): its body lives in <c>config/tasks/…md</c> and renders on
    /// view, so there is no column for it to overflow. The §682 incident is not merely fixed for
    /// those tasks — it is unreachable.</para>
    ///
    /// <para>What is still stored, and therefore still guarded, is the <b>TITLE</b>. And the guard
    /// still applies in full to any task body left in JSON, which is why both sources are walked:
    /// the day a role's tasks have not migrated yet, this must still protect them.</para>
    /// </remarks>
    [Fact]
    public void Every_shipped_sponsor_task_body_fits_the_column()
    {
        var configPath = Path.Combine(FindRepoRoot(), "config", "sponsor.eldk27.json");
        Assert.True(File.Exists(configPath), $"sponsor config not found at {configPath}");

        using var doc = JsonDocument.Parse(File.ReadAllText(configPath));

        var offences = new List<string>();
        var checkedCount = 0;

        // MIGRATED definitions: the body is not stored, but the TITLE is — and a title is
        // substituted before storage ("{{expectedAttendees}}" becomes "1250"), so it is checked
        // with a generous stand-in rather than as authored.
        foreach (var definition in CommunityHub.Core.Tasks.Definitions
                     .TaskDefinitionRegistry.Shipped.All)
        {
            checkedCount++;
            var renderedTitle = definition.Title.Replace("{{expectedAttendees}}", "100000");

            if (!TaskFieldGuard.Fits(renderedTitle, description: null, out var titleReason))
            {
                offences.Add($"registry/{definition.Key}: {titleReason}");
            }
        }

        // Any task body still defined in JSON — none for sponsors today, but the guard must not
        // evaporate the moment a file happens to be empty.
        if (doc.RootElement.TryGetProperty("taskSets", out var taskSets))
        {
            foreach (var set in taskSets.EnumerateObject())
            {
                if (set.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var index = 0;
                foreach (var task in set.Value.EnumerateArray())
                {
                    var title = task.TryGetProperty("title", out var t) ? t.GetString() : null;
                    var description = task.TryGetProperty("description", out var d) ? d.GetString() : null;
                    checkedCount++;

                    if (!TaskFieldGuard.Fits(title, description, out var reason))
                    {
                        offences.Add($"taskSets/{set.Name}[{index}] ({title}): {reason}");
                    }

                    index++;
                }
            }
        }

        Assert.True(
            checkedCount > 0,
            "no sponsor tasks were checked at all — neither the registry nor the JSON produced "
            + "any, so this guard is protecting nothing and the catalogue shape has changed.");
        Assert.True(
            offences.Count == 0,
            "Sponsor task bodies that will not fit the Tasks.Description column — these abort the "
            + "WooCommerce pull job (§682):\n  " + string.Join("\n  ", offences));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))
                && Directory.Exists(Path.Combine(dir.FullName, "config")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("repo root not found from " + AppContext.BaseDirectory);
    }
}
