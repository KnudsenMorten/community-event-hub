using System.Text.Json.Nodes;
using CommunityHub.Core.Config;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline unit tests for <see cref="ConfigScalarEditor"/> — the Phase 2 scalar
/// flattener + override-fragment builder behind the admin config GUI. Proves:
///   • scalar leaves are enumerated by dotted path, arrays/objects are skipped;
///   • secret-bearing + doc (_*) keys are NEVER surfaced;
///   • effective value = default merged with override, with the overridden flag set;
///   • applying a change deep-merges (no clobber of sibling keys) + re-types
///     numbers/bools/urls correctly;
///   • reset removes the key and prunes empty parents;
///   • validation rejects bad URLs/numbers and accepts blank URLs.
/// </summary>
public sealed class ConfigScalarEditorTests
{
    private const string EventDefault = """
    {
      "_doc": "documentation, never editable",
      "edition": {
        "code": "ELDK27",
        "expectedAttendees": 1250,
        "isActive": true,
        "_note": "doc key"
      },
      "community": {
        "brandColor": "#0a3d62",
        "logoUrl": ""
      },
      "crewDays": [ "2027-02-08", "2027-02-09" ]
    }
    """;

    [Fact]
    public void Enumerate_flattens_scalars_by_dotted_path_skips_arrays_and_docs()
    {
        var fields = ConfigScalarEditor.Enumerate(EventDefault, overrideJson: null);
        var paths = fields.Select(f => f.Path).ToHashSet();

        Assert.Contains("edition.code", paths);
        Assert.Contains("edition.expectedAttendees", paths);
        Assert.Contains("edition.isActive", paths);
        Assert.Contains("community.brandColor", paths);
        Assert.Contains("community.logoUrl", paths);

        // Documentation keys are never surfaced.
        Assert.DoesNotContain("_doc", paths);
        Assert.DoesNotContain("edition._note", paths);
        // Arrays are Phase 3 — skipped entirely.
        Assert.DoesNotContain("crewDays", paths);
    }

    [Fact]
    public void Enumerate_infers_kinds()
    {
        var fields = ConfigScalarEditor.Enumerate(EventDefault, null)
            .ToDictionary(f => f.Path);

        Assert.Equal(ScalarKind.String, fields["edition.code"].Kind);
        Assert.Equal(ScalarKind.Number, fields["edition.expectedAttendees"].Kind);
        Assert.Equal(ScalarKind.Bool, fields["edition.isActive"].Kind);
        Assert.Equal(ScalarKind.Url, fields["community.logoUrl"].Kind); // key ends "Url"
    }

    [Fact]
    public void Effective_value_reflects_override_and_flags_overridden()
    {
        var ovr = """{ "edition": { "expectedAttendees": 750 } }""";
        var fields = ConfigScalarEditor.Enumerate(EventDefault, ovr)
            .ToDictionary(f => f.Path);

        var attendees = fields["edition.expectedAttendees"];
        Assert.Equal("750", attendees.EffectiveValue);
        Assert.Equal("1250", attendees.DefaultValue);
        Assert.True(attendees.IsOverridden);

        var code = fields["edition.code"];
        Assert.Equal("ELDK27", code.EffectiveValue);
        Assert.False(code.IsOverridden);
    }

    [Fact]
    public void Secret_bearing_keys_are_excluded()
    {
        const string integrations = """
        {
          "woocommerce": {
            "baseUrl": "https://shop.example.test",
            "consumerKeySecretName": "woo-key",
            "consumerSecretSecretName": "woo-secret"
          },
          "email": {
            "smtp": {
              "host": "smtp.example.test",
              "port": 587,
              "passwordSecretName": "smtp-pw",
              "usernameSecretName": "smtp-user"
            }
          }
        }
        """;
        var paths = ConfigScalarEditor.Enumerate(integrations, null)
            .Select(f => f.Path).ToHashSet();

        Assert.Contains("woocommerce.baseUrl", paths);
        Assert.Contains("email.smtp.host", paths);
        Assert.Contains("email.smtp.port", paths);

        // Never expose anything that names a secret.
        Assert.DoesNotContain("woocommerce.consumerKeySecretName", paths);
        Assert.DoesNotContain("woocommerce.consumerSecretSecretName", paths);
        Assert.DoesNotContain("email.smtp.passwordSecretName", paths);
        Assert.DoesNotContain("email.smtp.usernameSecretName", paths);
    }

    [Theory]
    [InlineData("consumerKeySecretName", true)]
    [InlineData("apiKeySecretName", true)]
    [InlineData("connectionString", true)]
    [InlineData("smtpPassword", true)]
    [InlineData("refreshToken", true)]
    [InlineData("_doc", true)]
    [InlineData("baseUrl", false)]
    [InlineData("fromEmail", false)]
    public void IsExcludedKey_gate(string key, bool excluded) =>
        Assert.Equal(excluded, ConfigScalarEditor.IsExcludedKey(key));

    [Fact]
    public void ApplyChange_deep_merges_without_clobbering_siblings()
    {
        // Existing override already changed code; now change attendees too. The
        // existing key must survive (deep-merge, not replace).
        var existing = """{ "edition": { "code": "CUSTOM" } }""";
        var merged = ConfigScalarEditor.ApplyChange(
            existing, "edition.expectedAttendees", "999", ScalarKind.Number);

        var obj = JsonNode.Parse(merged)!.AsObject();
        var edition = obj["edition"]!.AsObject();
        Assert.Equal("CUSTOM", edition["code"]!.GetValue<string>());     // sibling kept
        Assert.Equal(999, edition["expectedAttendees"]!.GetValue<long>()); // added, integer
    }

    [Fact]
    public void ApplyChange_creates_nested_path_from_empty_override()
    {
        var merged = ConfigScalarEditor.ApplyChange(
            existingOverrideJson: null, "email.smtp.host", "relay.example.test",
            ScalarKind.String);

        var host = JsonNode.Parse(merged)!["email"]!["smtp"]!["host"]!.GetValue<string>();
        Assert.Equal("relay.example.test", host);
    }

    [Fact]
    public void ApplyChange_bool_roundtrips_as_json_boolean()
    {
        var merged = ConfigScalarEditor.ApplyChange(
            null, "edition.isActive", "false", ScalarKind.Bool);
        Assert.False(JsonNode.Parse(merged)!["edition"]!["isActive"]!.GetValue<bool>());
    }

    [Fact]
    public void ApplyChange_invalid_number_throws()
    {
        Assert.Throws<FormatException>(() =>
            ConfigScalarEditor.ApplyChange(null, "x.y", "not-a-number", ScalarKind.Number));
    }

    [Fact]
    public void RemovePath_removes_key_and_prunes_empty_parents()
    {
        var existing = """{ "edition": { "expectedAttendees": 750 } }""";
        var remaining = ConfigScalarEditor.RemovePath(existing, "edition.expectedAttendees");

        // Only key in only object removed → whole fragment collapses to empty.
        Assert.True(string.IsNullOrEmpty(remaining));
    }

    [Fact]
    public void RemovePath_keeps_siblings()
    {
        var existing = """{ "edition": { "code": "X", "expectedAttendees": 750 } }""";
        var remaining = ConfigScalarEditor.RemovePath(existing, "edition.expectedAttendees");

        var edition = JsonNode.Parse(remaining)!["edition"]!.AsObject();
        Assert.True(edition.ContainsKey("code"));
        Assert.False(edition.ContainsKey("expectedAttendees"));
    }

    [Theory]
    [InlineData("https://ok.example.test", null)]
    [InlineData("", null)]              // blank URL clears — valid
    [InlineData("   ", null)]           // whitespace treated as blank — valid
    [InlineData("not a url", "err")]
    [InlineData("ftp://x.example.test", "err")] // non-http(s) scheme rejected
    public void Validate_url(string value, string? expectErr)
    {
        var result = ConfigScalarEditor.Validate(value, ScalarKind.Url);
        if (expectErr is null) Assert.Null(result); else Assert.NotNull(result);
    }

    [Theory]
    [InlineData("500", null)]
    [InlineData("12.5", null)]
    [InlineData("abc", "err")]
    [InlineData("", "err")]
    public void Validate_number(string value, string? expectErr)
    {
        var result = ConfigScalarEditor.Validate(value, ScalarKind.Number);
        if (expectErr is null) Assert.Null(result); else Assert.NotNull(result);
    }

    [Fact]
    public void Enumerate_on_bad_default_returns_empty_never_throws()
    {
        Assert.Empty(ConfigScalarEditor.Enumerate("not json", null));
        Assert.Empty(ConfigScalarEditor.Enumerate("[1,2,3]", null)); // array root, not object
    }

    [Fact]
    public void Enumerate_ignores_a_bad_override_falls_back_to_default()
    {
        var fields = ConfigScalarEditor.Enumerate(EventDefault, "{ broken json")
            .ToDictionary(f => f.Path);
        Assert.Equal("1250", fields["edition.expectedAttendees"].EffectiveValue);
        Assert.False(fields["edition.expectedAttendees"].IsOverridden);
    }
}
