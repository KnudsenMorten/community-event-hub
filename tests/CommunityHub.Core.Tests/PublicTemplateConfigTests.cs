using System.Text.Json;
using System.Text.RegularExpressions;
using CommunityHub.Core.Config;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Tasks.Definitions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// 2026-09-14 — the NEUTRAL default content set the public template ships (task bodies, welcome copy,
/// info pages, edition config, field maps, surveys) is read by the real loaders, covers what the code
/// asks for, stays in step with the private set, and carries nothing of the upstream event.
/// </summary>
/// <remarks>
/// <para>In the private repository the set lives in <c>public-template/</c> and the publish script maps
/// it onto <c>config/</c> and <c>App_Data/Surveys/</c> in the mirror; in the public template it IS
/// <c>config/</c> (marked by <c>config/PUBLIC-TEMPLATE.md</c>). <see cref="TemplateRoot"/> resolves
/// whichever applies, so these facts run in both.</para>
///
/// <para>🔑 Why this exists: the defaults are a SECOND copy of content the upstream event edits
/// constantly. A default that silently falls behind — a task body the code gained last month, a
/// deadline rule nobody added — does not fail anywhere a maintainer looks; it fails in somebody
/// else's deployment as a task with no instructions or no due date.</para>
/// </remarks>
public class PublicTemplateConfigTests
{
    private static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    private static string RepoRoot() =>
        PrivateContent.RepoRoot() ?? throw new DirectoryNotFoundException("CommunityHub.sln not found above " + AppContext.BaseDirectory);

    /// <summary>The root the default set's public paths are relative to.</summary>
    private static string TemplateRoot()
    {
        var root = RepoRoot();
        var privateCopy = Path.Combine(root, "public-template");
        return Directory.Exists(privateCopy) ? privateCopy : root;
    }

    private static string TemplateFile(string relative)
    {
        var path = Path.Combine(TemplateRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"default content file missing: {relative} (under {TemplateRoot()})");
        return path;
    }

    [Fact]
    public void The_default_set_carries_its_marker()
    {
        TemplateFile("config/PUBLIC-TEMPLATE.md");
    }

    [Fact]
    public void The_event_default_parses_with_the_real_loader()
    {
        var cfg = new EventEditionConfigLoader().Load(TemplateFile("config/event.eldk27.json"));

        Assert.False(string.IsNullOrWhiteSpace(cfg.Code));
        Assert.NotNull(cfg.Dates);
        Assert.NotNull(cfg.SharePoint);
        Assert.True(cfg.Placeholders.ContainsKey("eventSiteUrl"));
        Assert.DoesNotContain(cfg.Placeholders.Keys, k => k.StartsWith('_'));
        Assert.NotNull(cfg.Volunteer);
    }

    /// <summary>
    /// 🔒 Every deadline rule a shipped sponsor task names exists in the default. A missing rule leaves
    /// that task with no due date — which never errors, it just never chases.
    /// </summary>
    [Fact]
    public void The_sponsor_default_defines_every_deadline_rule_the_code_names()
    {
        var cfg = new SponsorConfigLoader().Load(TemplateFile("config/sponsor.eldk27.json"));
        var rules = cfg.DeadlineRules();

        var named = TaskDefinitionRegistry.Shipped.All
            .Select(d => d.Due).OfType<TaskDue.FromConfig>()
            .Select(d => d.RuleName).Distinct().ToList();
        Assert.NotEmpty(named);
        foreach (var rule in named)
        {
            Assert.True(rules.ContainsKey(rule), $"the default sponsor.eldk27.json has no deadline rule '{rule}'.");
        }

        Assert.NotNull(cfg.ProductClassification);
        Assert.NotNull(cfg.BoothWallSpecs);
        Assert.NotNull(cfg.SwagCatalog);
    }

    /// <summary>🔒 Every speaker task's deadline key resolves, with the title its SourceKey derives from.</summary>
    [Fact]
    public void The_speaker_deadlines_default_covers_every_speaker_task()
    {
        var cfg = JsonSerializer.Deserialize<SpeakerDeadlineConfig>(
            File.ReadAllText(TemplateFile("config/speaker-deadlines.eldk27.json")), CaseInsensitive)!;
        Assert.NotNull(cfg.GetStartedDeadline);

        foreach (var definition in TaskDefinitionRegistry.Shipped.All.Where(d => d.Due is TaskDue.FromSpeakerConfig))
        {
            var key = ((TaskDue.FromSpeakerConfig)definition.Due).DeadlineKey;
            var entry = cfg.Deadlines.SingleOrDefault(d => d.Key == key);
            Assert.True(entry is not null, $"the default speaker-deadlines.eldk27.json has no entry for '{key}'.");
            Assert.Equal(definition.Title, entry!.Title);
            Assert.NotEqual(default, entry.DueDate);
        }
    }

    /// <summary>🔒 Every body file a shipped task definition points at exists in the default set.</summary>
    [Fact]
    public void Every_shipped_task_body_has_a_default()
    {
        var missing = TaskDefinitionRegistry.Shipped.All
            .Select(d => $"config/tasks/eldk27/{d.BodyRef}.md")
            .Distinct()
            .Where(rel => !File.Exists(Path.Combine(TemplateRoot(), rel.Replace('/', Path.DirectorySeparatorChar))))
            .ToList();

        Assert.True(missing.Count == 0, "default task bodies missing:\n  " + string.Join("\n  ", missing));
    }

    [Fact]
    public void The_signal_groups_default_puts_the_listed_roles_in_scope()
    {
        var provider = new SignalGroupsProvider(new SignalGroupsOptions { ConfigPath = TemplateFile("config/signal-groups.eldk27.json") });

        Assert.True(provider.InScope(ParticipantRole.Speaker));
        Assert.True(provider.InScope(ParticipantRole.Volunteer));
        Assert.False(provider.InScope(ParticipantRole.Organizer));
    }

    /// <summary>
    /// 🔒 LOCKSTEP with the private set. Every task body, welcome file, info page, field map and survey the
    /// upstream event ships has a default counterpart — so a file added for the event is a build failure
    /// here until the public template gets one too. Private repository only (the public template has no
    /// private set to compare with).
    /// </summary>
    [Fact]
    public void Every_private_content_file_has_a_default_counterpart()
    {
        var root = RepoRoot();
        var template = Path.Combine(root, "public-template");
        if (!Directory.Exists(template)) return; // the public template: nothing private to compare against

        var folders = new[]
        {
            ("config/tasks/eldk27", "*.md"), ("config/welcome/eldk27", "*.md"), ("config/content/eldk27", "*.md"),
            ("config/integrations", "*.json"), ("src/CommunityHub/App_Data/Surveys", "eldk27-*.json"),
        };
        var missing = new List<string>();
        foreach (var (folder, pattern) in folders)
        {
            var dir = Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(Directory.Exists(dir), $"private content folder missing: {folder}");
            foreach (var file in Directory.GetFiles(dir, pattern, SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, file);
                if (!File.Exists(Path.Combine(template, rel))) missing.Add(rel.Replace('\\', '/'));
            }
        }

        Assert.True(missing.Count == 0,
            "the public default set has no counterpart for:\n  " + string.Join("\n  ", missing)
            + "\nAdd a neutral version under public-template/ (same relative path).");
    }

    /// <summary>
    /// 🔒 The default set is PUBLISHED. It must carry nothing of the upstream event — its name, domains,
    /// venue, organizers — and every e-mail address in it must be on a reserved example domain.
    /// </summary>
    [Fact]
    public void The_default_set_names_no_upstream_event_person_or_address()
    {
        var root = TemplateRoot();
        var files = new[] { "config", Path.Combine("src", "CommunityHub", "App_Data", "Surveys") }
            .Select(d => Path.Combine(root, d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.GetFiles(d, "*.*", SearchOption.AllDirectories))
            .Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".json", StringComparison.OrdinalIgnoreCase));

        var forbidden = new Regex(
            @"experts\s*live|expertslive|\beldk2\d\b|eldk\d\d\.|bella\s*(center|sky)|copenhagen|denmark|morten|byskov|hedegaard|agerlund|2linkit|\btwoday\b|signal\.group/#(?!REPLACE_WITH_)[A-Za-z0-9]",
            RegexOptions.IgnoreCase);
        var hits = new List<string>();
        foreach (var file in files)
        {
            // The two folder READMEs explain the (code-fixed) edition folder name; they are docs, not content.
            var isDoc = Path.GetFileName(file) is "README.md" or "PUBLIC-TEMPLATE.md";
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                // Code-level identifiers that happen to contain the edition slug or a country are not content,
                // and the open-source project's own public repository address is not an upstream-event detail.
                var line = Regex.Replace(lines[i], @"(/content/|config/(tasks|welcome|content)/|/survey/|""slug"":\s*"")eldk27", "$1edition");
                line = Regex.Replace(line, @"eldk27-(topics|post-[a-z]+)|[a-z-]+\.eldk27\.[a-z.]+|eldk-hub/|nonDenmarkOnly|github\.com/KnudsenMorten/community-event-hub", "id");
                if (isDoc) line = Regex.Replace(line, @"\beldk27\b", "edition");
                if (forbidden.IsMatch(line)) hits.Add($"{Path.GetRelativePath(root, file)}:{i + 1}: {forbidden.Match(line).Value}");
                foreach (Match m in Regex.Matches(lines[i], @"[A-Za-z0-9._%+-]+@([A-Za-z0-9.-]+\.[A-Za-z]{2,})"))
                {
                    if (!m.Groups[1].Value.EndsWith(".example", StringComparison.OrdinalIgnoreCase))
                        hits.Add($"{Path.GetRelativePath(root, file)}:{i + 1}: {m.Value}");
                }
            }
        }

        Assert.True(hits.Count == 0, "upstream-specific content in the public default set:\n  " + string.Join("\n  ", hits));
    }

    /// <summary>
    /// 2026-09-15 — <c>scripts/first-organizer.sql</c> (public-only) inserts into exactly the columns the
    /// REAL EF model maps, and supplies every column that is required and has no database default — so
    /// the one SQL step of the README install cannot fail on a renamed or newly required column. Checked
    /// against the SQL Server model offline; no database is opened.
    /// </summary>
    [Fact]
    public void The_first_organizer_sql_matches_the_database_model()
    {
        var sql = File.ReadAllText(TemplateFile("scripts/first-organizer.sql"));
        TemplateFile("scripts/grant-db-access.sh");

        // The real schema is whatever the migrations create, so read it from the migrations script EF
        // generates for SQL Server — produced in-process, no database connection.
        var options = new DbContextOptionsBuilder<CommunityHub.Core.Data.CommunityHubDbContext>()
            .UseSqlServer("Server=offline;Database=none;").Options;
        using var db = new CommunityHub.Core.Data.CommunityHubDbContext(options);
        var migrations = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions
            .GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(db).GenerateScript();

        var inserts = Regex.Matches(sql, @"INSERT INTO \[(?<table>\w+)\]\s*\((?<cols>[^)]*)\)");
        Assert.Equal(2, inserts.Count);
        foreach (Match insert in inserts)
        {
            var table = insert.Groups["table"].Value;
            var columns = SchemaFromMigrations(migrations, table);
            Assert.NotEmpty(columns);
            var supplied = Regex.Matches(insert.Groups["cols"].Value, @"\[(\w+)\]").Select(m => m.Groups[1].Value).ToList();

            foreach (var column in supplied)
                Assert.True(columns.ContainsKey(column), $"first-organizer.sql inserts [{table}].[{column}], which the migrations do not create.");

            // A NOT NULL column with no DEFAULT and no IDENTITY must be in the INSERT, or the statement fails.
            var required = columns.Where(kv => kv.Value).Select(kv => kv.Key).Where(c => !supplied.Contains(c)).ToList();
            Assert.True(required.Count == 0,
                $"first-organizer.sql does not supply required column(s) of [{table}]: {string.Join(", ", required)}");
        }
    }

    /// <summary>
    /// Column name → "required with no default" for <paramref name="table"/>, replaying the CREATE TABLE,
    /// ADD, DROP COLUMN, ALTER COLUMN and sp_rename statements of a migrations script in order.
    /// </summary>
    private static Dictionary<string, bool> SchemaFromMigrations(string script, string table)
    {
        var cols = new Dictionary<string, bool>(StringComparer.Ordinal);
        static bool Required(string definition) =>
            Regex.IsMatch(definition, @"\bNOT NULL\b") && !Regex.IsMatch(definition, @"\b(DEFAULT|IDENTITY)\b");

        var create = Regex.Match(script, @"CREATE TABLE \[" + Regex.Escape(table) + @"\] \((?<body>.*?)\n\);", RegexOptions.Singleline);
        if (create.Success)
        {
            foreach (Match m in Regex.Matches(create.Groups["body"].Value, @"^\s*\[(?<name>\w+)\] (?<def>[^\r\n]*?),?\r?$", RegexOptions.Multiline))
                cols[m.Groups["name"].Value] = Required(m.Groups["def"].Value);
        }
        var t = Regex.Escape(table);
        var statements = new Regex(
            @"ALTER TABLE \[" + t + @"\] ADD \[(?<add>\w+)\] (?<adddef>[^;]*);"
            + @"|ALTER TABLE \[" + t + @"\] DROP COLUMN \[(?<drop>\w+)\];"
            + @"|ALTER TABLE \[" + t + @"\] ALTER COLUMN \[(?<alter>\w+)\] (?<alterdef>[^;]*);"
            + @"|sp_rename N'\[" + t + @"\]\.\[(?<from>\w+)\]', N'(?<to>\w+)', 'COLUMN'");
        foreach (Match m in statements.Matches(script))
        {
            if (m.Groups["add"].Success) cols[m.Groups["add"].Value] = Required(m.Groups["adddef"].Value);
            else if (m.Groups["drop"].Success) cols.Remove(m.Groups["drop"].Value);
            else if (m.Groups["alter"].Success && cols.ContainsKey(m.Groups["alter"].Value))
            {
                // ALTER COLUMN keeps an existing default constraint; only a change to NULL relaxes it.
                if (!Regex.IsMatch(m.Groups["alterdef"].Value, @"\bNOT NULL\b")) cols[m.Groups["alter"].Value] = false;
            }
            else if (m.Groups["from"].Success && cols.Remove(m.Groups["from"].Value, out var req)) cols[m.Groups["to"].Value] = req;
        }
        return cols;
    }

    /// <summary>
    /// The infra parameter examples name only real <c>main.bicep</c> parameters and supply every parameter
    /// without a default — otherwise <c>scripts/deploy.sh</c> fails on a fresh copy.
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

        foreach (Match m in Regex.Matches(File.ReadAllText(Path.Combine(root, "infra", file)), @"[A-Za-z0-9._%+-]+@([A-Za-z0-9.-]+\.[A-Za-z]{2,})"))
            Assert.EndsWith(".example", m.Groups[1].Value, StringComparison.OrdinalIgnoreCase);
    }
}
