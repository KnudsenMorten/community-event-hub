using System.Text.Json;
using System.Text.RegularExpressions;
using CommunityHub.Core.Config;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Tasks.Definitions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 2026-09-14 — the sanitized starter files a new community copies (<c>config-examples/</c> and
/// <c>infra/*.parameters.example.json</c>) are read by the REAL loaders and cover what the code asks
/// for.
/// </summary>
/// <remarks>
/// <para>A community reported the public edition could not be set up: its guide pointed at files
/// the public mirror does not ship. Example files fix that only while they stay in step with the code
/// — an example with a mistyped key or a missing deadline rule parses as "not configured" and fails
/// silently in someone else's deployment, where nobody here will see it. These facts run in BOTH
/// repositories: the examples are published, so they need nothing private to pass.</para>
/// </remarks>
public class ConfigExamplesParseTests
{
    private static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CommunityHub.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("CommunityHub.sln not found above " + AppContext.BaseDirectory);
    }

    private static string Example(string name)
    {
        var path = Path.Combine(RepoRoot(), "config-examples", name);
        Assert.True(File.Exists(path), $"config-examples/{name} is missing.");
        return path;
    }

    [Fact]
    public void The_event_example_parses_with_the_real_loader()
    {
        var cfg = new EventEditionConfigLoader().Load(Example("event.example.json"));

        Assert.Equal("DEMO27", cfg.Code);
        Assert.NotNull(cfg.Dates);
        Assert.NotNull(cfg.SharePoint);
        Assert.True(cfg.Placeholders.ContainsKey("eventSiteUrl"));
        Assert.DoesNotContain(cfg.Placeholders.Keys, k => k.StartsWith('_'));
        Assert.NotNull(cfg.Volunteer);
        Assert.Single(cfg.Volunteer!.ExtraAvailabilityDays);
    }

    /// <summary>
    /// 🔒 Every deadline rule a shipped sponsor task names must exist in the example. A missing rule
    /// leaves that task with no due date in a fresh deployment — which never errors, it just never
    /// chases.
    /// </summary>
    [Fact]
    public void The_sponsor_example_parses_and_defines_every_deadline_rule_the_code_names()
    {
        var cfg = new SponsorConfigLoader().Load(Example("sponsor.example.json"));
        var rules = cfg.DeadlineRules();

        var named = TaskDefinitionRegistry.Shipped.All
            .Select(d => d.Due).OfType<TaskDue.FromConfig>()
            .Select(d => d.RuleName).Distinct().ToList();
        Assert.NotEmpty(named);
        foreach (var rule in named)
        {
            Assert.True(rules.ContainsKey(rule), $"config-examples/sponsor.example.json has no deadline rule '{rule}'.");
        }

        Assert.NotNull(cfg.ProductClassification);
        Assert.NotNull(cfg.BoothWallSpecs);
        Assert.NotNull(cfg.SwagCatalog);
    }

    /// <summary>🔒 Every speaker task's deadline key resolves, with the title the SourceKey is derived from.</summary>
    [Fact]
    public void The_speaker_deadlines_example_covers_every_speaker_task()
    {
        var cfg = JsonSerializer.Deserialize<SpeakerDeadlineConfig>(
            File.ReadAllText(Example("speaker-deadlines.example.json")), CaseInsensitive)!;
        Assert.NotNull(cfg.GetStartedDeadline);

        foreach (var definition in TaskDefinitionRegistry.Shipped.All.Where(d => d.Due is TaskDue.FromSpeakerConfig))
        {
            var key = ((TaskDue.FromSpeakerConfig)definition.Due).DeadlineKey;
            var entry = cfg.Deadlines.SingleOrDefault(d => d.Key == key);
            Assert.True(entry is not null, $"config-examples/speaker-deadlines.example.json has no entry for '{key}'.");
            Assert.Equal(definition.Title, entry!.Title);
            Assert.NotEqual(default, entry.DueDate);
        }
    }

    [Fact]
    public void The_signal_groups_example_puts_the_listed_roles_in_scope()
    {
        var provider = new SignalGroupsProvider(new SignalGroupsOptions { ConfigPath = Example("signal-groups.example.json") });

        Assert.True(provider.InScope(ParticipantRole.Speaker));
        Assert.True(provider.InScope(ParticipantRole.Volunteer));
        Assert.False(provider.InScope(ParticipantRole.Organizer));
    }

    /// <summary>
    /// The infra parameter examples name only real <c>main.bicep</c> parameters and supply every
    /// parameter that has no default — otherwise <c>scripts/deploy.sh</c> fails on a fresh copy.
    /// </summary>
    [Theory]
    [InlineData("main.dev.parameters.example.json", "dev")]
    [InlineData("main.prod.parameters.example.json", "prod")]
    public void The_infra_parameter_examples_match_main_bicep(string file, string environment)
    {
        var root = RepoRoot();
        var bicep = File.ReadAllText(Path.Combine(root, "infra", "main.bicep"));
        var declared = Regex.Matches(bicep, @"(?m)^param\s+(\w+)\s+\w+(\s*=)?")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Success);

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "infra", file)));
        var supplied = doc.RootElement.GetProperty("parameters").EnumerateObject().Select(p => p.Name).ToList();

        foreach (var name in supplied)
            Assert.True(declared.ContainsKey(name), $"infra/{file} supplies '{name}', which main.bicep does not declare.");
        foreach (var required in declared.Where(kv => !kv.Value).Select(kv => kv.Key))
            Assert.True(supplied.Contains(required), $"infra/{file} is missing required parameter '{required}'.");

        Assert.Equal(environment, doc.RootElement.GetProperty("parameters").GetProperty("environmentName").GetProperty("value").GetString());
        var baseName = doc.RootElement.GetProperty("parameters").GetProperty("baseName").GetProperty("value").GetString()!;
        Assert.Matches("^[a-z0-9]{1,12}$", baseName);
    }

    /// <summary>
    /// 🔒 The examples are PUBLISHED. Any e-mail address in them must be on a reserved example domain,
    /// so a real address pasted in while editing one cannot reach the public mirror unnoticed.
    /// </summary>
    [Fact]
    public void Every_address_in_the_examples_is_on_an_example_domain()
    {
        var root = RepoRoot();
        var files = Directory.GetFiles(Path.Combine(root, "config-examples"), "*.json")
            .Concat(Directory.GetFiles(Path.Combine(root, "infra"), "*.example.json"));

        foreach (var file in files)
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"[A-Za-z0-9._%+-]+@([A-Za-z0-9.-]+\.[A-Za-z]{2,})"))
            {
                Assert.True(m.Groups[1].Value.EndsWith(".example", StringComparison.OrdinalIgnoreCase),
                    $"{Path.GetFileName(file)} contains '{m.Value}', which is not on an example domain.");
            }
        }
    }
}
