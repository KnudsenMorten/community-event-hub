using System.Net;
using System.Text;
using CommunityHub.Core.Integrations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §754 — <see cref="ZohoClient.GetBackstageAgendaAsync"/>, the COMPLETE agenda pull the venue
/// screens are built on. Driven through a fake HTTP handler with the live-verified v3 shapes.
/// </summary>
/// <remarks>
/// <para>🔒 <b>The most important test here is
/// <see cref="No_ZohoOptions_flag_may_gate_a_Backstage_READ_behind_a_missing_scope"/>.</b> Six
/// places in this codebase used to state that the Backstage agenda and speaker endpoints need a
/// READ scope that "is not yet granted", and every one was wrong — the credentials have always
/// carried every permission CEH needs. It cost the operator ten separate occasions of work being
/// declared blocked on him. §754.5 deleted the flags and that guard now fails the build if anything
/// shaped like them returns; a comment saying "do not reintroduce" is what we had, and it did not
/// hold.</para>
/// </remarks>
public sealed class ZohoSignageAgendaPullTests
{
    private const string Portal = "P1";
    private const string Event = "E1";

    private sealed class RouteHandler : HttpMessageHandler
    {
        private readonly Func<string, (HttpStatusCode, string)> _respond;
        public List<string> Requests { get; } = new();
        public RouteHandler(Func<string, (HttpStatusCode, string)> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.PathAndQuery;
            Requests.Add(url);
            var (status, body) = _respond(url);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static ZohoClient NewClient(RouteHandler handler) =>
        new(new HttpClient(handler),
            new ZohoOptions
            {
                Enabled = true,
                ApiDomain = "https://zoho.test",
                BackstagePortalId = Portal,
                BackstageEventId = Event,
            },
            NullLogger<ZohoClient>.Instance);

    private const string Halls = "{\"halls\":[{\"id\":\"h1\",\"name\":\"Room-01-Floor-1\"}]}";
    private const string Tracks = "{\"tracks\":[{\"track_id\":\"t1\",\"name\":\"Cloud & Infrastructure\"}]}";
    private const string Speakers =
        "{\"speakers\":[" +
        "{\"id\":\"sp1\",\"name\":\"Ada\",\"last_name\":\"Lovelace\",\"email\":\"ada@example.test\"}," +
        "{\"id\":\"sp2\",\"name\":\"Grace\",\"last_name\":\"Hopper\",\"email\":\"grace@example.test\"}]}";
    private const string Agendas = "{\"agendas\":[{\"agenda_id\":\"a0\",\"index\":0},{\"agenda_id\":\"a1\",\"index\":1}]}";

    private static RouteHandler StandardHandler(string day1, string day2 = "{\"sessions\":[]}") =>
        new(url =>
        {
            if (url.Contains("/halls")) return (HttpStatusCode.OK, Halls);
            if (url.Contains("/tracks")) return (HttpStatusCode.OK, Tracks);
            if (url.Contains("/speakers")) return (HttpStatusCode.OK, Speakers);
            if (url.Contains("/agendas")) return (HttpStatusCode.OK, Agendas);
            if (url.Contains("/sessions?day=1")) return (HttpStatusCode.OK, day1);
            if (url.Contains("/sessions?day=2")) return (HttpStatusCode.OK, day2);
            return (HttpStatusCode.OK, "{\"sessions\":[]}");
        });

    [Fact]
    public async Task Resolves_hall_track_and_speaker_ids_to_the_names_a_screen_prints()
    {
        var day1 =
            "{\"sessions\":[{\"id\":\"s1\",\"title\":\"Opening Keynote\"," +
            "\"start_time\":\"2027-02-09T08:00:00Z\",\"duration\":60," +
            "\"venue\":\"h1\",\"track\":\"t1\",\"session_type\":\"KEYNOTE\"," +
            "\"speakers\":[\"sp1\",\"sp2\"]}]}";

        var activities = await NewClient(StandardHandler(day1))
            .GetBackstageAgendaAsync("tok");

        var s1 = Assert.Single(activities);
        Assert.Equal("Opening Keynote", s1.Title);
        Assert.Equal("Room-01-Floor-1", s1.Room);              // venue id → hall name
        Assert.Equal("Cloud & Infrastructure", s1.Track);      // track id → track name
        Assert.Equal("KEYNOTE", s1.ActivityType);              // session_type, snake_case
        Assert.Equal(new[] { "Ada Lovelace", "Grace Hopper" }, s1.Speakers);
        Assert.Equal(60, s1.DurationMinutes);
        Assert.Equal(1, s1.DayIndex);
    }

    /// <summary>
    /// 🔒 §754.5 — THE GUARD. No config flag may claim a Backstage READ scope is missing.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-01: <i>"this error has bitten me 10 times now due to you dont update
    /// this wrong assumption in the docs. the zoho backstage creds have all the needed permissions
    /// already."</i></para>
    ///
    /// <para><c>AgendaReadEnabled</c> and <c>SpeakerReadEnabled</c> both defaulted FALSE on a claim
    /// that was never verified against the live API, and the claim spread into five more files as
    /// settled fact — so every session that met it concluded the work was blocked on him. This test
    /// fails if either flag, or anything shaped like them, comes back. A comment saying "do not
    /// reintroduce" is what we had; it did not hold.</para>
    /// </remarks>
    [Fact]
    public void No_ZohoOptions_flag_may_gate_a_Backstage_READ_behind_a_missing_scope()
    {
        var offenders = typeof(ZohoOptions).GetProperties()
            .Select(p => p.Name)
            .Where(n => n.EndsWith("ReadEnabled", StringComparison.Ordinal))
            .ToList();

        Assert.True(offenders.Count == 0,
            "These ZohoOptions flags gate a Backstage READ: " + string.Join(", ", offenders)
            + ".\n\nThe Zoho Backstage credentials already carry every permission CEH needs — they "
            + "always have. A flag that waits for a scope to be granted can only ever block work "
            + "that already works. If a read genuinely fails, let the API say so at call time.");
    }

    [Fact]
    public async Task The_agenda_pull_reads_with_no_config_flag_in_the_way()
    {
        var day1 =
            "{\"sessions\":[{\"id\":\"s1\",\"title\":\"Talk\"," +
            "\"start_time\":\"2027-02-09T08:00:00Z\",\"duration\":60}]}";

        var handler = StandardHandler(day1);
        var activities = await NewClient(handler).GetBackstageAgendaAsync("tok");

        // Exactly like the §301b self-heal does in production every 5 minutes.
        Assert.Single(activities);
        Assert.Contains(handler.Requests, r => r.Contains("/sessions?day=1"));
    }

    [Fact]
    public async Task Every_agenda_day_is_enumerated_and_aggregated()
    {
        var day1 =
            "{\"sessions\":[{\"id\":\"s1\",\"title\":\"Day one\"," +
            "\"start_time\":\"2027-02-09T08:00:00Z\",\"duration\":60}]}";
        var day2 =
            "{\"sessions\":[{\"id\":\"s2\",\"title\":\"Day two\"," +
            "\"start_time\":\"2027-02-10T08:00:00Z\",\"duration\":60}]}";

        var handler = StandardHandler(day1, day2);
        var activities = await NewClient(handler).GetBackstageAgendaAsync("tok");

        Assert.Equal(2, activities.Count);
        Assert.Equal(2, activities.Single(a => a.SessionId == "s2").DayIndex);
        // /sessions REQUIRES ?day= — a bare call is a 400 the pager would swallow into "no agenda".
        Assert.DoesNotContain(handler.Requests, r => r.EndsWith("/sessions"));
    }

    [Fact]
    public async Task A_failed_page_THROWS_so_a_partial_agenda_can_never_be_applied()
    {
        var handler = new RouteHandler(url =>
        {
            if (url.Contains("/halls")) return (HttpStatusCode.OK, Halls);
            if (url.Contains("/tracks")) return (HttpStatusCode.OK, Tracks);
            if (url.Contains("/speakers")) return (HttpStatusCode.OK, Speakers);
            if (url.Contains("/agendas")) return (HttpStatusCode.OK, Agendas);
            if (url.Contains("/sessions?day=1"))
                return (HttpStatusCode.OK,
                    "{\"sessions\":[{\"id\":\"s1\",\"title\":\"T\",\"start_time\":\"2027-02-09T08:00:00Z\",\"duration\":60}]}");
            // Day 2 is down. The non-strict pager would end the enumeration here and report a
            // one-day agenda as complete — and the sync would then DELETE every day-2 activity.
            return (HttpStatusCode.ServiceUnavailable, "{\"message\":\"Service Unavailable\"}");
        });

        await Assert.ThrowsAnyAsync<Exception>(
            () => NewClient(handler).GetBackstageAgendaAsync("tok"));
    }

    [Fact]
    public async Task An_unresolvable_speaker_EMAIL_is_dropped_rather_than_printed_on_a_wall()
    {
        var day1 =
            "{\"sessions\":[{\"id\":\"s1\",\"title\":\"Talk\"," +
            "\"start_time\":\"2027-02-09T08:00:00Z\",\"duration\":60," +
            "\"speakers\":[\"ada@example.test\",\"nobody@example.test\",\"Katherine Johnson\"]}]}";

        var activities = await NewClient(StandardHandler(day1))
            .GetBackstageAgendaAsync("tok");

        var speakers = Assert.Single(activities).Speakers;
        // A known e-mail resolves to the person's name; an UNKNOWN one is discarded — these cards
        // are two metres tall in a public corridor and a photographed e-mail cannot be recalled.
        Assert.Contains("Ada Lovelace", speakers);
        Assert.DoesNotContain(speakers, s => s.Contains('@'));
        // A plain name that is not an identifier is kept — Backstage has been seen returning those,
        // and dropping them would silently empty the speaker line.
        Assert.Contains("Katherine Johnson", speakers);
    }

    [Fact]
    public async Task An_unresolvable_track_or_hall_id_renders_as_nothing_not_as_a_raw_id()
    {
        var day1 =
            "{\"sessions\":[{\"id\":\"s1\",\"title\":\"Talk\"," +
            "\"start_time\":\"2027-02-09T08:00:00Z\",\"duration\":60," +
            "\"venue\":\"h-unknown\",\"track\":\"t-unknown\"}]}";

        var activities = await NewClient(StandardHandler(day1))
            .GetBackstageAgendaAsync("tok");

        var s1 = Assert.Single(activities);
        Assert.Null(s1.Room);
        Assert.Null(s1.Track);   // "4823901000000123456" on a wall is worse than no track line
    }

    [Fact]
    public async Task A_speaker_object_rather_than_an_id_string_is_also_resolved()
    {
        // The field has been seen carrying ids, e-mails and objects; all three must work.
        var day1 =
            "{\"sessions\":[{\"id\":\"s1\",\"title\":\"Talk\"," +
            "\"start_time\":\"2027-02-09T08:00:00Z\",\"duration\":60," +
            "\"speakers\":[{\"id\":\"sp2\"},{\"name\":\"Katherine\",\"last_name\":\"Johnson\"}]}]}";

        var activities = await NewClient(StandardHandler(day1))
            .GetBackstageAgendaAsync("tok");

        Assert.Equal(new[] { "Grace Hopper", "Katherine Johnson" }, Assert.Single(activities).Speakers);
    }
}
