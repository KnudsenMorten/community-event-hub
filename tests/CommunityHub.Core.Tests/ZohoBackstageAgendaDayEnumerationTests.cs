using System.Net;
using System.Text;
using CommunityHub.Core.Integrations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §38e BUG 1 — <see cref="ZohoClient.GetBackstageSessionsAsync"/> must ENUMERATE agenda
/// days and pull <c>/sessions?day={index}</c> per day, aggregating them. The live Backstage
/// v3 API returns HTTP 400 ("Please enter the valid agenda day") for <c>/sessions</c> with
/// no <c>?day=</c>, which the old code swallowed → 0 sessions. These tests drive the REAL
/// client through a fake HTTP handler (no live Zoho) with the verified field shapes:
///   • /agendas → <c>{"agendas":[{"agenda_id":..,"index":0},{"index":1}]}</c>
///   • /sessions?day=N → <c>{"sessions":[{"id":..,"title":..,"start_time":"..Z","duration":N,"venue":null}]}</c>
///     (NO end_time; end is start + duration minutes; venue is the hall ref → room name).
///   • /halls → <c>{"halls":[{"id":..,"name":..}]}</c>
/// </summary>
public sealed class ZohoBackstageAgendaDayEnumerationTests
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
                AgendaReadEnabled = true,
            },
            NullLogger<ZohoClient>.Instance);

    [Fact]
    public async Task Enumerates_two_agenda_days_and_aggregates_all_sessions()
    {
        var day1 =
            "{\"sessions\":[" +
            "{\"id\":\"s1\",\"title\":\"Opening Keynote\",\"start_time\":\"2027-02-09T08:00:00Z\",\"duration\":60,\"venue\":null}," +
            "{\"id\":\"s2\",\"title\":\"Identity Master Class\",\"start_time\":\"2027-02-09T09:00:00Z\",\"duration\":420,\"venue\":\"h1\"}]}";
        var day2 =
            "{\"sessions\":[" +
            "{\"id\":\"s3\",\"title\":\"Closing\",\"start_time\":\"2027-02-10T15:30:00Z\",\"duration\":45,\"venue\":null}]}";

        var handler = new RouteHandler(url =>
        {
            if (url.Contains("/halls"))
                return (HttpStatusCode.OK, "{\"halls\":[{\"id\":\"h1\",\"name\":\"Hall One\"}]}");
            if (url.Contains("/agendas"))
                return (HttpStatusCode.OK,
                    "{\"agendas\":[{\"agenda_id\":\"a0\",\"index\":0},{\"agenda_id\":\"a1\",\"index\":1}]}");
            // The bug: /sessions with NO ?day= must 400 (and must NOT be the call we make).
            if (url.EndsWith("/sessions"))
                return (HttpStatusCode.BadRequest,
                    "{\"status_code\":\"400\",\"message\":\"Please enter the valid agenda day\"}");
            if (url.Contains("/sessions?day=1")) return (HttpStatusCode.OK, day1);
            if (url.Contains("/sessions?day=2")) return (HttpStatusCode.OK, day2);
            // day=0 (and any other) is empty.
            return (HttpStatusCode.OK, "{\"sessions\":[]}");
        });

        var client = NewClient(handler);
        var result = await client.GetBackstageSessionsAsync("tok");

        Assert.True(result.IsAvailable);
        Assert.Equal(3, result.Sessions.Count); // aggregated across BOTH days

        // We queried day=1 and day=2 (1-based), never bare /sessions.
        Assert.Contains(handler.Requests, r => r.Contains("/sessions?day=1"));
        Assert.Contains(handler.Requests, r => r.Contains("/sessions?day=2"));
        Assert.DoesNotContain(handler.Requests, r => r.EndsWith("/sessions"));

        // Field mapping: end = start + duration minutes; venue id → hall name.
        var s2 = Assert.Single(result.Sessions, s => s.SessionId == "s2");
        Assert.Equal("Identity Master Class", s2.Title);
        Assert.Equal(new DateTimeOffset(2027, 2, 9, 9, 0, 0, TimeSpan.Zero), s2.StartsAt);
        Assert.Equal(new DateTimeOffset(2027, 2, 9, 16, 0, 0, TimeSpan.Zero), s2.EndsAt); // +420 min
        Assert.Equal("Hall One", s2.Room);

        var s1 = Assert.Single(result.Sessions, s => s.SessionId == "s1");
        Assert.Null(s1.Room); // venue null → blank room
        Assert.Equal(new DateTimeOffset(2027, 2, 9, 9, 0, 0, TimeSpan.Zero), s1.EndsAt); // 08:00 +60
    }

    [Fact]
    public async Task Bare_sessions_400_is_not_treated_as_zero_when_days_enumerate()
    {
        // Regression for the swallowed-400 bug: even though /sessions (no day) 400s, the
        // day-enumeration path still returns the real sessions.
        var handler = new RouteHandler(url =>
        {
            if (url.Contains("/halls")) return (HttpStatusCode.OK, "{\"halls\":[]}");
            if (url.Contains("/agendas")) return (HttpStatusCode.OK, "{\"agendas\":[{\"index\":0}]}");
            if (url.EndsWith("/sessions"))
                return (HttpStatusCode.BadRequest, "{\"message\":\"Please enter the valid agenda day\"}");
            if (url.Contains("/sessions?day=1"))
                return (HttpStatusCode.OK,
                    "{\"sessions\":[{\"id\":\"x\",\"title\":\"T\",\"start_time\":\"2027-02-09T08:00:00Z\",\"duration\":30}]}");
            return (HttpStatusCode.OK, "{\"sessions\":[]}");
        });

        var result = await NewClient(handler).GetBackstageSessionsAsync("tok");

        Assert.True(result.IsAvailable);
        Assert.Single(result.Sessions);
    }

    [Fact]
    public async Task Agenda_read_disabled_returns_unavailable_without_calling()
    {
        var handler = new RouteHandler(_ => (HttpStatusCode.OK, "{}"));
        var client = new ZohoClient(new HttpClient(handler),
            new ZohoOptions
            {
                Enabled = true,
                ApiDomain = "https://zoho.test",
                BackstagePortalId = Portal,
                BackstageEventId = Event,
                AgendaReadEnabled = false, // gate closed
            },
            NullLogger<ZohoClient>.Instance);

        var result = await client.GetBackstageSessionsAsync("tok");

        Assert.False(result.IsAvailable);
        Assert.Empty(handler.Requests); // never hit the network
    }
}
