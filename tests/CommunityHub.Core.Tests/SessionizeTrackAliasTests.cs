using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §971 — A RENAMED SESSIONIZE TRACK IS NORMALISED ON IMPORT.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-09: he renamed a track in Zoho — <i>"Data Compliance &amp; Security"</i> →
/// <i>"Data Compliance"</i> — and wants any Sessionize session on the old label to carry the new one
/// <b>inside CEH and onward to Zoho</b>.</para>
///
/// <para>🔑 <b>Renamed on the way IN.</b> CEH then stores ONE name, so the organizer grid, the
/// agenda, the public programme, the graphics and the Zoho push cannot disagree. That is why this is
/// not <see cref="ZohoClient.TrackNameMap"/>, which rewrites a name only as it is pushed to
/// Backstage and leaves CEH showing the Sessionize spelling — correct for the two AI tracks, where
/// Zoho deliberately carries shorter names, and wrong for a real rename.</para>
///
/// <para>🔴 <b>The JSON form is the one that ships.</b> Config reaches the app through Azure APP
/// SETTINGS — there is no <c>AddJsonFile</c>, so <c>integrations.&lt;edition&gt;.json</c> is
/// documentation. The per-key form would need a setting named
/// <c>Sessionize__TrackAliases__Data Compliance &amp; Security</c>, an env-var name with spaces and
/// an ampersand. Hence <c>TrackAliasesJson</c>, and hence a test on it specifically.</para>
/// </remarks>
public sealed class SessionizeTrackAliasTests
{
    private const string SessionsJson = """
    {
      "sessions": [
        { "id": "1", "title": "Purview in practice", "categoryItems": [10] },
        { "id": "2", "title": "Conditional Access", "categoryItems": [11] }
      ],
      "categories": [
        { "id": 1, "title": "Suggested Event Track", "items": [
            { "id": 10, "name": "Data Compliance & Security" },
            { "id": 11, "name": "Security" } ] }
      ]
    }
    """;

    private static string? TrackOf(SessionizeSessionsParseResult r, string id) =>
        r.Sessions.FirstOrDefault(s => s.SessionizeId == id)?.Track;

    [Fact]
    public void Without_a_map_the_sessionize_label_is_kept()
    {
        var r = SessionizeApiClient.ParseSessions(SessionsJson);

        Assert.Equal("Data Compliance & Security", TrackOf(r, "1"));
        Assert.Equal("Security", TrackOf(r, "2"));
    }

    /// <summary>🔴 The operator's actual case.</summary>
    [Fact]
    public void The_renamed_track_is_stored_under_its_new_name()
    {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Data Compliance & Security"] = "Data Compliance",
        };

        var r = SessionizeApiClient.ParseSessions(SessionsJson, aliases);

        Assert.Equal("Data Compliance", TrackOf(r, "1"));
        // 🔒 An unmapped track is untouched — a rename must not disturb the other seven.
        Assert.Equal("Security", TrackOf(r, "2"));
    }

    /// <summary>
    /// 🔴 THE DEPLOYABLE FORM. `TrackAliasesJson` is what an Azure app setting can actually hold,
    /// so if this works and the nested form does not, the feature still ships.
    /// </summary>
    [Fact]
    public void The_json_app_setting_form_resolves()
    {
        var opts = new SessionizeApiOptions
        {
            TrackAliasesJson = """{"Data Compliance & Security":"Data Compliance"}""",
        };

        var r = SessionizeApiClient.ParseSessions(SessionsJson, opts.ResolvedTrackAliases);

        Assert.Equal("Data Compliance", TrackOf(r, "1"));
    }

    /// <summary>
    /// 🔒 Trimmed and case-insensitive on BOTH sides. The alias is typed by a human into config and
    /// the label is typed by a human into Sessionize; a mismatch on a trailing space would be
    /// invisible — the session just keeps the old track and nothing reports it.
    /// </summary>
    [Fact]
    public void Matching_tolerates_case_and_surrounding_space()
    {
        var opts = new SessionizeApiOptions
        {
            TrackAliasesJson = """{"  data compliance & security  ":"  Data Compliance  "}""",
        };

        var r = SessionizeApiClient.ParseSessions(SessionsJson, opts.ResolvedTrackAliases);

        Assert.Equal("Data Compliance", TrackOf(r, "1"));
    }

    /// <summary>
    /// ⚠️ Malformed JSON must not take the import down — an unparseable alias is ignored and every
    /// session keeps its Sessionize label. (It is silent, which is why the config note says to verify
    /// the rename on a real session rather than assume it landed.)
    /// </summary>
    [Fact]
    public void Malformed_json_is_ignored_rather_than_fatal()
    {
        var opts = new SessionizeApiOptions { TrackAliasesJson = "{ not json" };

        var r = SessionizeApiClient.ParseSessions(SessionsJson, opts.ResolvedTrackAliases);

        Assert.Equal("Data Compliance & Security", TrackOf(r, "1"));
        Assert.Empty(opts.ResolvedTrackAliases);
    }

    /// <summary>The JSON form wins over the nested form — it is the deployable one.</summary>
    [Fact]
    public void The_json_form_overrides_the_nested_form()
    {
        var opts = new SessionizeApiOptions
        {
            TrackAliases = { ["Data Compliance & Security"] = "WRONG" },
            TrackAliasesJson = """{"Data Compliance & Security":"Data Compliance"}""",
        };

        Assert.Equal("Data Compliance", opts.ResolvedTrackAliases["Data Compliance & Security"]);
    }
}
