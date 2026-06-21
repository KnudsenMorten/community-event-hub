using CommunityHub.Core.Config;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for the override-aware loader overloads — the runtime wiring of
/// the HYBRID config model. Each test writes a shipped-default JSON file to a
/// temp path, then asserts that:
///   * with NO override the effective config equals the shipped default
///     (byte-for-byte behaviour, additive), and
///   * with an override the effective value is the deep-merged result, and
///   * a malformed override fails safe to the shipped default (no throw).
/// </summary>
public sealed class ConfigOverrideLoaderTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"ceh-cfgoverride-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private void Write(string json) => File.WriteAllText(_path, json);

    // --- Event loader -------------------------------------------------------

    [Fact]
    public void Event_no_override_equals_shipped_default()
    {
        Write("""{ "edition": { "code": "ELDK27", "expectedAttendees": 400 } }""");
        var loader = new EventEditionConfigLoader();

        var def = loader.Load(_path);
        var eff = loader.Load(_path, overrideJson: null);

        Assert.Equal("ELDK27", eff.Code);
        Assert.Equal(def.Code, eff.Code);
        Assert.Equal(def.ExpectedAttendees, eff.ExpectedAttendees);
    }

    [Fact]
    public void Event_override_changes_the_effective_scalar()
    {
        Write("""{ "edition": { "code": "ELDK27", "expectedAttendees": 400 } }""");
        var loader = new EventEditionConfigLoader();

        var eff = loader.Load(_path,
            overrideJson: """{ "edition": { "expectedAttendees": 600 } }""");

        Assert.Equal("ELDK27", eff.Code);          // untouched key survives
        Assert.Equal(600, eff.ExpectedAttendees);  // overridden
    }

    [Fact]
    public void Event_override_can_add_a_sibling_section()
    {
        Write("""{ "edition": { "code": "ELDK27" } }""");
        var loader = new EventEditionConfigLoader();

        var eff = loader.Load(_path,
            overrideJson: """{ "ticketSale": { "enabled": true, "ticketUrl": "https://example.test/buy" } }""");

        Assert.NotNull(eff.TicketSale);
        Assert.True(eff.TicketSale!.Enabled);
        Assert.Equal("https://example.test/buy", eff.TicketSale.TicketUrl);
    }

    [Fact]
    public void Event_malformed_override_falls_back_to_default_no_throw()
    {
        Write("""{ "edition": { "code": "ELDK27", "expectedAttendees": 400 } }""");
        var loader = new EventEditionConfigLoader();

        var eff = loader.Load(_path, overrideJson: "{ not valid json ]");

        Assert.Equal("ELDK27", eff.Code);
        Assert.Equal(400, eff.ExpectedAttendees);
    }

    // --- Sponsor loader -----------------------------------------------------

    [Fact]
    public void Sponsor_no_override_equals_shipped_default()
    {
        Write("""
        { "deadlineRules": { "logo": { "basis": "eventMinus", "days": 30 } },
          "taskSets": { "core": [ { "title": "Send logo", "deadline": "logo" } ] } }
        """);
        var loader = new SponsorConfigLoader();

        var def = loader.Load(_path);
        var eff = loader.Load(_path, overrideJson: null);

        Assert.Single(eff.TaskSet("core"));
        Assert.Equal(def.TaskSet("core").Count, eff.TaskSet("core").Count);
    }

    [Fact]
    public void Sponsor_override_replaces_a_task_set_array_wholesale()
    {
        Write("""
        { "deadlineRules": { "logo": { "basis": "eventMinus", "days": 30 } },
          "taskSets": { "core": [ { "title": "Send logo", "deadline": "logo" },
                                  { "title": "Send bio", "deadline": "logo" } ] } }
        """);
        var loader = new SponsorConfigLoader();

        var eff = loader.Load(_path,
            overrideJson: """{ "taskSets": { "core": [ { "title": "Only this", "deadline": "logo" } ] } }""");

        var core = eff.TaskSet("core");
        Assert.Single(core);                       // array REPLACED, not merged
        Assert.Equal("Only this", core[0].Title);
    }

    [Fact]
    public void Sponsor_override_merges_a_nested_deadline_rule_value()
    {
        Write("""{ "deadlineRules": { "logo": { "basis": "eventMinus", "days": 30 } } }""");
        var loader = new SponsorConfigLoader();

        var eff = loader.Load(_path,
            overrideJson: """{ "deadlineRules": { "logo": { "days": 14 } } }""");

        var rule = eff.DeadlineRules()["logo"];
        Assert.Equal("eventMinus", rule.Basis);    // untouched
        Assert.Equal(14, rule.Days);               // overridden
    }

    [Fact]
    public void Sponsor_malformed_override_falls_back_to_default_no_throw()
    {
        Write("""{ "taskSets": { "core": [ { "title": "Keep", "deadline": "x" } ] } }""");
        var loader = new SponsorConfigLoader();

        var eff = loader.Load(_path, overrideJson: "}{ broken");

        Assert.Single(eff.TaskSet("core"));
        Assert.Equal("Keep", eff.TaskSet("core")[0].Title);
    }

    [Fact]
    public void Sponsor_missing_file_still_throws_even_with_override()
    {
        var loader = new SponsorConfigLoader();
        var missing = Path.Combine(Path.GetTempPath(), $"ceh-none-{Guid.NewGuid():N}.json");

        Assert.Throws<FileNotFoundException>(() =>
            loader.Load(missing, overrideJson: """{ "taskSets": {} }"""));
    }

    // --- Integrations loader ------------------------------------------------

    [Fact]
    public void Integrations_no_override_equals_shipped_default()
    {
        Write("""{ "woocommerce": { "enabled": true, "pageSize": 100 } }""");
        var loader = new IntegrationsConfigLoader();

        var eff = loader.Load(_path, overrideJson: null);

        Assert.True((bool)eff["woocommerce"]!["enabled"]!);
        Assert.Equal(100, (int)eff["woocommerce"]!["pageSize"]!);
    }

    [Fact]
    public void Integrations_override_toggles_an_integration_off()
    {
        Write("""{ "woocommerce": { "enabled": true, "pageSize": 100 } }""");
        var loader = new IntegrationsConfigLoader();

        var eff = loader.Load(_path,
            overrideJson: """{ "woocommerce": { "enabled": false } }""");

        Assert.False((bool)eff["woocommerce"]!["enabled"]!);  // overridden
        Assert.Equal(100, (int)eff["woocommerce"]!["pageSize"]!); // sibling survives
    }

    [Fact]
    public void Integrations_override_stores_a_secret_NAME_not_a_value()
    {
        // The override carries the Key Vault secret NAME, never the secret value
        // — mirroring the shipped file's *SecretName convention.
        Write("""{ "woocommerce": { "enabled": true, "consumerKeySecretName": "woocommerce-consumer-key" } }""");
        var loader = new IntegrationsConfigLoader();

        var eff = loader.Load(_path,
            overrideJson: """{ "woocommerce": { "consumerKeySecretName": "woocommerce-consumer-key-2027" } }""");

        Assert.Equal("woocommerce-consumer-key-2027",
            (string)eff["woocommerce"]!["consumerKeySecretName"]!);
    }

    [Fact]
    public void Integrations_malformed_override_falls_back_to_default_no_throw()
    {
        Write("""{ "woocommerce": { "enabled": true } }""");
        var loader = new IntegrationsConfigLoader();

        var eff = loader.Load(_path, overrideJson: "nonsense");

        Assert.True((bool)eff["woocommerce"]!["enabled"]!);
    }
}
