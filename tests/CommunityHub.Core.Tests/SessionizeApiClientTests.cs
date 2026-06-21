using CommunityHub.Core.Integrations;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for <see cref="SessionizeApiClient.ParseSpeakers"/> - the JSON
/// mapping from a Sessionize v2 view response to <see cref="SessionizeSpeaker"/>.
/// No network: the JSON is inline, mirroring the real Speakers / All view shapes
/// (incl. the "speaker emails" advanced field that surfaces an email property).
/// </summary>
public sealed class SessionizeApiClientTests
{
    // Speakers-view shape: a top-level array. Email present (advanced field on).
    private const string SpeakersViewJson = """
    [
      {
        "id": "11111111-1111-1111-1111-111111111111",
        "firstName": "Ada",
        "lastName": "Lovelace",
        "fullName": "Ada Lovelace",
        "bio": "Pioneer of computing.",
        "tagLine": "Mathematician",
        "profilePicture": "https://example.test/ada.jpg",
        "email": "Ada@Example.TEST",
        "isTopSpeaker": true,
        "links": [
          { "title": "LinkedIn", "url": "https://linkedin.com/in/ada", "linkType": "LinkedIn" },
          { "title": "Twitter",  "url": "https://twitter.com/ada",     "linkType": "Twitter" },
          { "title": "Blog",     "url": "https://ada.example.test",    "linkType": "Company_Website" }
        ]
      },
      {
        "id": "22222222-2222-2222-2222-222222222222",
        "firstName": "Grace",
        "lastName": "Hopper",
        "fullName": "Grace Hopper",
        "bio": "Compiler inventor.",
        "tagLine": "Rear Admiral",
        "profilePicture": "",
        "email": "grace@example.test",
        "links": []
      }
    ]
    """;

    // All-view shape: object with nested "speakers" array.
    private const string AllViewJson = """
    {
      "sessions": [],
      "speakers": [
        {
          "firstName": "Alan",
          "lastName": "Turing",
          "fullName": "Alan Turing",
          "bio": "",
          "tagLine": "",
          "email": "alan@example.test",
          "links": [ { "title": "X", "url": "https://x.com/alan", "linkType": "Twitter" } ]
        }
      ],
      "categories": [],
      "questions": []
    }
    """;

    [Fact]
    public void Parses_speakers_view_array()
    {
        var result = SessionizeApiClient.ParseSpeakers(SpeakersViewJson);

        Assert.Null(result.Error);
        Assert.Equal(2, result.Speakers.Count);
    }

    [Fact]
    public void Maps_core_fields_and_lowercases_email()
    {
        var result = SessionizeApiClient.ParseSpeakers(SpeakersViewJson);
        var ada = result.Speakers.Single(s => s.LastName == "Lovelace");

        // Email is lowercased so the email-match upsert is case-insensitive.
        Assert.Equal("ada@example.test", ada.Email);
        Assert.Equal("Ada", ada.FirstName);
        Assert.Equal("Mathematician", ada.TagLine);
        Assert.Equal("Pioneer of computing.", ada.Biography);
        Assert.Equal("https://example.test/ada.jpg", ada.ProfilePictureUrl);
    }

    [Fact]
    public void Splits_links_by_linktype()
    {
        var result = SessionizeApiClient.ParseSpeakers(SpeakersViewJson);
        var ada = result.Speakers.Single(s => s.LastName == "Lovelace");

        Assert.Equal("https://linkedin.com/in/ada", ada.LinkedIn);
        Assert.Equal("https://twitter.com/ada", ada.Twitter);
        // Company_Website maps into the Blog slot.
        Assert.Equal("https://ada.example.test", ada.Blog);
    }

    [Fact]
    public void Empty_optional_fields_become_null()
    {
        var result = SessionizeApiClient.ParseSpeakers(SpeakersViewJson);
        var grace = result.Speakers.Single(s => s.LastName == "Hopper");

        Assert.Null(grace.ProfilePictureUrl); // "" -> null
        Assert.Null(grace.LinkedIn);
        Assert.Null(grace.Twitter);
    }

    [Fact]
    public void Parses_all_view_nested_speakers()
    {
        var result = SessionizeApiClient.ParseSpeakers(AllViewJson);

        Assert.Null(result.Error);
        var alan = Assert.Single(result.Speakers);
        Assert.Equal("alan@example.test", alan.Email);
        Assert.Equal("https://x.com/alan", alan.Twitter);
    }

    [Fact]
    public void Speaker_with_no_email_is_skipped_with_a_warning()
    {
        // Advanced "speaker emails" field NOT enabled -> no email property.
        const string json = """
        [ { "firstName": "No", "lastName": "Email", "fullName": "No Email", "links": [] } ]
        """;

        var result = SessionizeApiClient.ParseSpeakers(json);

        Assert.Null(result.Error);
        Assert.Empty(result.Speakers);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("No Email", warning);
        Assert.Contains("speaker emails", warning);
    }

    [Fact]
    public void Duplicate_emails_are_deduped_and_fields_backfilled()
    {
        // Same speaker twice; second row carries a tagLine the first lacked.
        const string json = """
        [
          { "firstName": "Dup", "lastName": "Licate", "email": "dup@example.test", "tagLine": "", "links": [] },
          { "firstName": "Dup", "lastName": "Licate", "email": "DUP@example.test", "tagLine": "Engineer", "links": [] }
        ]
        """;

        var result = SessionizeApiClient.ParseSpeakers(json);

        var only = Assert.Single(result.Speakers);
        Assert.Equal("dup@example.test", only.Email);
        Assert.Equal("Engineer", only.TagLine); // back-filled from the 2nd row
    }

    [Fact]
    public void Email_in_question_answers_is_recognised()
    {
        // Some endpoints surface the email as a custom question answer.
        const string json = """
        [
          {
            "firstName": "Qa",
            "lastName": "Email",
            "links": [],
            "questionAnswers": [
              { "question": "Speaker Email", "answer": "qa@example.test" }
            ]
          }
        ]
        """;

        var result = SessionizeApiClient.ParseSpeakers(json);

        var only = Assert.Single(result.Speakers);
        Assert.Equal("qa@example.test", only.Email);
    }

    // The real ELDK27 shape: the main view carries full data but NO email (PII
    // withheld); the token-protected SpeakersEmails side-view supplies it, keyed
    // by the Sessionize speaker id.
    private const string MainViewNoEmailJson = """
    [
      {
        "id": "eee7c4bd-de60-428e-b5b9-6eef8aa5fd04",
        "firstName": "Morten",
        "lastName": "Knudsen",
        "fullName": "Morten Knudsen",
        "bio": "Triple MVP.",
        "tagLine": "Architect",
        "links": []
      }
    ]
    """;

    private const string EmailsViewJson = """
    [
      { "id": "eee7c4bd-de60-428e-b5b9-6eef8aa5fd04", "firstName": "Morten", "lastName": "Knudsen", "email": "MOK@MortenKnudsen.net" }
    ]
    """;

    [Fact]
    public void ParseEmailMap_maps_id_to_lowercased_email()
    {
        var map = SessionizeApiClient.ParseEmailMap(EmailsViewJson);

        Assert.Single(map);
        Assert.Equal("mok@mortenknudsen.net",
            map["eee7c4bd-de60-428e-b5b9-6eef8aa5fd04"]);
    }

    [Fact]
    public void ParseEmailMap_tolerates_bad_json()
    {
        Assert.Empty(SessionizeApiClient.ParseEmailMap("{ not json"));
    }

    [Fact]
    public void Email_is_joined_from_the_emails_view_by_sessionize_id()
    {
        var emailById = SessionizeApiClient.ParseEmailMap(EmailsViewJson);

        var result = SessionizeApiClient.ParseSpeakers(MainViewNoEmailJson, emailById);

        Assert.Null(result.Error);
        var spk = Assert.Single(result.Speakers);
        Assert.Equal("mok@mortenknudsen.net", spk.Email);
        Assert.Equal("Architect", spk.TagLine); // full data still from main view
    }

    [Fact]
    public void Without_the_emails_map_a_no_email_main_view_speaker_is_skipped()
    {
        // No join map supplied -> falls back to the existing skip-with-warning.
        var result = SessionizeApiClient.ParseSpeakers(MainViewNoEmailJson);

        Assert.Empty(result.Speakers);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void Invalid_json_returns_error_not_throw()
    {
        var result = SessionizeApiClient.ParseSpeakers("{ not json");

        Assert.NotNull(result.Error);
        Assert.Empty(result.Speakers);
    }

    [Fact]
    public void Unexpected_shape_returns_a_clear_error()
    {
        // An object with no speakers array (e.g. an error payload).
        var result = SessionizeApiClient.ParseSpeakers("""{ "message": "nope" }""");

        Assert.NotNull(result.Error);
        Assert.Contains("speakers", result.Error!);
    }
}
