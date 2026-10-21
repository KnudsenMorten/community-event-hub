using System.Globalization;
using System.Net;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Integrations.Sessions;
using CommunityHub.Core.Tests.Scenario;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1363 — Zoho returns a speaker's first name as <c>first_name</c>; the hub read <c>name</c>, which Zoho leaves blank,
/// so every stored Backstage name was the last name alone. Operator 2026-10-20: *"can you verify speaker 331 is linked
/// with zoho speaker"* → *"it is already correct, here from zoho"* (Edit Speaker: "Matias Julian" / "Christoffersen").
/// FAKE names only.
/// </summary>
public sealed class BackstageSpeakerFirstNameTests
{
    private const int EventId = 1;

    private sealed class SpeakersHandler(string json) : HttpMessageHandler
    {
        // Live behaviour: the speakers endpoint takes no paging parameter (400 "Extra param found").
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(request.RequestUri!.Query.Contains("page=")
                ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("""{"status_code":"400","message":"Extra param found"}"""),
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
                });
    }

    private static ZohoClient NewZoho(string json) =>
        new(new HttpClient(new SpeakersHandler(json)), new ZohoOptions { BackstagePortalId = "P", BackstageEventId = "E" });

    [Fact]
    public async Task The_first_name_is_read_from_first_name()
    {
        // The live shape: first_name set, no `name` key at all.
        var zoho = NewZoho("""
            { "speakers": [
              { "id": "sp-1", "email": "ada@example.test", "first_name": "Ada Maria", "last_name": "Lovelace" },
              { "id": "sp-2", "email": "old@example.test", "name": "Grace", "last_name": "Hopper" }
            ] }
            """);

        var result = await zoho.GetBackstageSpeakersAsync("token");

        var ada = Assert.Single(result.Speakers, s => s.SpeakerId == "sp-1");
        Assert.Equal("Ada Maria Lovelace", ada.Name);
        Assert.Equal("Ada Maria", ada.FirstName);
        Assert.Equal("Lovelace", ada.LastName);
        // The older `name` key is still understood.
        Assert.Equal("Grace Hopper", Assert.Single(result.Speakers, s => s.SpeakerId == "sp-2").Name);
    }

    [Theory]
    [InlineData("Lovelace", true)]             // what the old reader stored
    [InlineData("lovelace ", true)]
    [InlineData("Ada Maria Lovelace", false)]  // already right
    [InlineData("Byron", false)]               // a real difference — a change, not the reader's mistake
    [InlineData(null, false)]
    public void Only_a_last_name_only_baseline_is_corrected_quietly(string? stored, bool expected)
    {
        var current = new BackstageSpeaker("sp-1", "Ada Maria Lovelace", null, null, null, null, null,
            FirstName: "Ada Maria", LastName: "Lovelace");
        Assert.Equal(expected, SpeakerChangeDetectionService.IsLastNameOnlyBaseline(stored, current));
    }

    /// <summary>A name change carries Zoho's own split (kept for the read side; §1364 retired the apply into CEH).</summary>
    [Fact]
    public void A_name_change_carries_zohos_own_first_and_last_name()
    {
        var changes = SyncDeltaQueueService.BuildSpeakerZohoChanges(
            "Anna Berg", null, null, null, null, null,
            "Anna van der Berg", null, null, null, null, null,
            newFirstName: "Anna", newLastName: "van der Berg");
        Assert.Contains(changes, c => c.Field == SyncDeltaQueueService.FieldFirstName && c.NewValue == "Anna");
        Assert.Contains(changes, c => c.Field == SyncDeltaQueueService.FieldLastName && c.NewValue == "van der Berg");
    }

    /// <summary>No name change ⇒ no first/last entries, so an unchanged speaker still produces no delta.</summary>
    [Fact]
    public void No_name_change_adds_no_name_fields() =>
        Assert.Empty(SyncDeltaQueueService.BuildSpeakerZohoChanges(
            "Ada Maria Lovelace", "T", null, null, null, null,
            "Ada Maria Lovelace", "T", null, null, null, null,
            newFirstName: "Ada Maria", newLastName: "Lovelace"));
}
