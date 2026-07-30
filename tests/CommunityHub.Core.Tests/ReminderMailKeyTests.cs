using System.Text.RegularExpressions;
using CommunityHub.Core.Email;
using CommunityHub.Core.Reminders;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §707 — every reminder carries its MAIL IDENTITY, so it resolves its own <c>(mail × role)</c> ring
/// instead of falling back to a feature ring.
/// </summary>
/// <remarks>
/// 🔴 THE BUG THIS CLOSES. The ring gate uses the per-mail ring only when a send sets
/// <c>EmailContext.TemplateName</c>. <c>ReminderEngine</c> never set it and <c>ReminderMessage</c> had
/// no field for it, so the builders rendered a template and then dropped its identity before sending.
/// Every reminder therefore resolved by its FEATURE ring, and the per-mail rings shown on
/// /Organizer/Settings for <c>task-deadline-reminder</c>, <c>getstarted-digest</c>,
/// <c>hotel-cutoff-reminder</c> and <c>app-game-gift-reminder</c> were <b>never consulted</b> — a
/// control displaying an audience it did not govern (§326bx), on the busiest mail path in the system.
///
/// <para>It also blocked the per-ROLE control the operator asked for: *"reminder to speaker is NOT the
/// same as reminder to organizer"* is exactly a reminder.</para>
/// </remarks>
public sealed class ReminderMailKeyTests
{
    /// <summary>A message with a MailKey exposes it for the engine to pass as TemplateName.</summary>
    [Fact]
    public void A_reminder_carries_its_mail_identity()
    {
        var msg = new ReminderMessage(
            "a@b.test", "task-deadline", "occ", "subj", "<p>x</p>",
            FeatureKey: "reminder-jobs", MailKey: "task-deadline-reminder");

        Assert.Equal("task-deadline-reminder", msg.MailKey);
    }

    /// <summary>
    /// 🔒 A null MailKey keeps the OLD feature-ring behaviour rather than failing closed. An
    /// un-migrated builder must degrade to what it did before, not go silent — silence is the failure
    /// mode nobody notices.
    /// </summary>
    [Fact]
    public void A_reminder_without_a_mail_identity_is_allowed_and_degrades_to_the_old_behaviour()
    {
        var msg = new ReminderMessage("a@b.test", "t", "occ", "s", "<p>x</p>", FeatureKey: "reminder-jobs");

        Assert.Null(msg.MailKey);
    }

    /// <summary>
    /// 🔒 THE GUARD. Every <c>new ReminderMessage(...)</c> in the shipped source must pass a MailKey.
    /// A new builder that forgets it would silently rejoin the feature-ring path — invisible in tests,
    /// invisible in logs, and only detectable by noticing a ring on the Settings page doing nothing.
    /// </summary>
    [Fact]
    public void Every_shipped_reminder_construction_passes_a_MailKey()
    {
        var root = FindRepoRoot();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            if (!text.Contains("new ReminderMessage(", StringComparison.Ordinal)) continue;

            var constructions = Regex.Matches(text, @"new ReminderMessage\(").Count;
            var keyed = Regex.Matches(text, @"MailKey:").Count;

            if (keyed < constructions)
            {
                offenders.Add($"{Path.GetFileName(file)}: {keyed}/{constructions} carry a MailKey");
            }
        }

        Assert.True(offenders.Count == 0,
            "Every reminder must carry its mail identity, or it silently resolves by its FEATURE ring "
            + "and its per-mail ring on /Organizer/Settings governs nothing:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Every MailKey used by a reminder must be a REAL registered mail — an unregistered key falls
    /// through to the feature ring exactly as a missing one does, so a typo is equally silent.
    /// </summary>
    [Fact]
    public void Every_reminder_MailKey_is_a_registered_mail()
    {
        var root = FindRepoRoot();
        var bad = new List<string>();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            // Only literal keys can be checked statically; `MailKey: TemplateName` resolves to the
            // const the builder also renders, which EmailInventoryCurrencyTests already pins.
            foreach (Match m in Regex.Matches(text, @"MailKey:\s*""([a-z0-9-]+)"""))
            {
                var key = m.Groups[1].Value;
                if (!EmailTemplateCatalog.Map.ContainsKey(key))
                {
                    bad.Add($"{Path.GetFileName(file)} -> '{key}'");
                }
            }
        }

        Assert.True(bad.Count == 0,
            "A reminder names a mail that is not in the registry, so its ring would never be found:\n  "
            + string.Join("\n  ", bad));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src"))
                && Directory.Exists(Path.Combine(dir.FullName, "tests")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the repo root from " + AppContext.BaseDirectory);
    }
}
