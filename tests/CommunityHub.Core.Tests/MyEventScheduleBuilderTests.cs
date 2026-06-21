using CommunityHub.Core.Attendees;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for <see cref="MyEventScheduleBuilder"/> — the pure projection
/// behind the attendee My-event "My sessions / Full agenda" surface (§20 Attendee).
/// Every input is explicit (the public session rows + the attendee record), so the
/// "which session is mine" matching, the deep-link shaping and the empty states are
/// deterministic without a DbContext.
/// </summary>
public sealed class MyEventScheduleBuilderTests
{
    private static PublicSessionRow PublicSession(
        int id, string title, string? askToken = "tok-" + "x",
        DateTimeOffset? startsAt = null, params string[] speakers) =>
        new(
            Id: id,
            Title: title,
            Abstract: null,
            Type: SessionType.CommunityTechSession,
            Length: SessionLength.SixtyMin,
            Room: "Room A",
            Track: null,
            StartsAt: startsAt,
            EndsAt: startsAt?.AddHours(1),
            Speakers: speakers.Select((n, i) => new PublicSessionSpeaker(i + 1, n, true)).ToList(),
            PublicSlug: null,
            AskToken: askToken);

    private static Attendee Booked(string masterClassName) => new()
    {
        EventId = 1,
        Email = "a@example.test",
        TicketStatus = TicketStatus.TwoDay,
        BookingStatus = MasterClassBookingStatus.Booked,
        MasterClassName = masterClassName,
    };

    // --- Empty states ------------------------------------------------------

    [Fact]
    public void No_public_sessions_yields_empty_agenda_and_no_my_sessions()
    {
        var s = MyEventScheduleBuilder.Build(System.Array.Empty<PublicSessionRow>(), Booked("AI Master Class"));

        Assert.True(s.AgendaIsEmpty);
        Assert.True(s.HasNoMySessions);
        Assert.Empty(s.Agenda);
        Assert.Empty(s.MySessions);
    }

    [Fact]
    public void Null_attendee_record_means_full_agenda_but_no_mine()
    {
        var sessions = new[] { PublicSession(1, "Some Talk", speakers: "Speaker One") };

        var s = MyEventScheduleBuilder.Build(sessions, record: null);

        Assert.False(s.AgendaIsEmpty);
        Assert.Single(s.Agenda);
        Assert.True(s.HasNoMySessions);
        Assert.False(s.Agenda[0].IsMine);
    }

    [Fact]
    public void Attendee_with_no_booking_has_agenda_but_no_my_sessions()
    {
        var sessions = new[] { PublicSession(1, "AI Master Class", speakers: "Speaker One") };
        var rec = Booked("AI Master Class");
        rec.MasterClassName = null; // booked status but name not yet reconciled

        var s = MyEventScheduleBuilder.Build(sessions, rec);

        Assert.True(s.HasNoMySessions);
        Assert.False(s.Agenda[0].IsMine);
    }

    // --- "Mine" matching ---------------------------------------------------

    [Fact]
    public void Booked_master_class_is_matched_and_highlighted_in_the_agenda()
    {
        var sessions = new[]
        {
            PublicSession(1, "Kubernetes Deep Dive", speakers: "Speaker One"),
            PublicSession(2, "AI Master Class", speakers: "Speaker Two"),
        };

        var s = MyEventScheduleBuilder.Build(sessions, Booked("AI Master Class"));

        Assert.Single(s.MySessions);
        Assert.Equal(2, s.MySessions[0].Id);
        Assert.True(s.MySessions[0].IsMine);
        // The agenda still lists both, with only the booked one flagged.
        Assert.Equal(2, s.Agenda.Count);
        Assert.True(s.Agenda.Single(r => r.Id == 2).IsMine);
        Assert.False(s.Agenda.Single(r => r.Id == 1).IsMine);
    }

    [Fact]
    public void Matching_is_case_and_whitespace_insensitive()
    {
        var sessions = new[] { PublicSession(1, "  AI Master Class  ", speakers: "S") };

        var s = MyEventScheduleBuilder.Build(sessions, Booked("ai master class"));

        Assert.Single(s.MySessions);
    }

    [Fact]
    public void Double_booked_comma_list_matches_each_session()
    {
        var sessions = new[]
        {
            PublicSession(1, "AI Master Class", speakers: "A"),
            PublicSession(2, "Security Master Class", speakers: "B"),
            PublicSession(3, "Other Talk", speakers: "C"),
        };

        var s = MyEventScheduleBuilder.Build(sessions, Booked("AI Master Class, Security Master Class"));

        Assert.Equal(2, s.MySessions.Count);
        Assert.Contains(s.MySessions, r => r.Id == 1);
        Assert.Contains(s.MySessions, r => r.Id == 2);
        Assert.False(s.Agenda.Single(r => r.Id == 3).IsMine);
    }

    // --- Deep-link shaping -------------------------------------------------

    [Fact]
    public void Detail_link_is_always_present_ask_and_evaluate_only_with_a_token()
    {
        var withToken = PublicSession(7, "Has Token", askToken: "abc123", speakers: "A");
        var noToken = PublicSession(8, "No Token", askToken: null, speakers: "B");

        var s = MyEventScheduleBuilder.Build(new[] { withToken, noToken }, record: null);

        var a = s.Agenda.Single(r => r.Id == 7);
        Assert.Equal("/Sessions/7", a.DetailUrl);
        Assert.Equal("/sessions/abc123/ask", a.AskUrl);
        Assert.Equal("/sessions/abc123/evaluate", a.EvaluateUrl);

        var b = s.Agenda.Single(r => r.Id == 8);
        Assert.Equal("/Sessions/8", b.DetailUrl);
        Assert.Null(b.AskUrl);
        Assert.Null(b.EvaluateUrl);
    }

    [Fact]
    public void Row_without_token_reports_ask_evaluate_not_available_so_the_view_shows_a_hint()
    {
        // Bug-fix §20: before a token is minted the attendee must see a hint, not
        // a silent gap. AskEvaluateAvailable is the single flag the view branches on.
        var noToken = PublicSession(8, "No Token", askToken: null, speakers: "B");

        var s = MyEventScheduleBuilder.Build(new[] { noToken }, record: null);

        var row = s.Agenda.Single(r => r.Id == 8);
        Assert.False(row.AskEvaluateAvailable);   // → view renders the "opens at the session" hint
        Assert.Null(row.AskUrl);
        Assert.Null(row.EvaluateUrl);
    }

    [Fact]
    public void Row_with_token_reports_ask_evaluate_available_so_the_view_shows_live_links()
    {
        var withToken = PublicSession(7, "Has Token", askToken: "abc123", speakers: "A");

        var s = MyEventScheduleBuilder.Build(new[] { withToken }, record: null);

        var row = s.Agenda.Single(r => r.Id == 7);
        Assert.True(row.AskEvaluateAvailable);   // → view renders the active ask/evaluate links
        Assert.Equal("/sessions/abc123/ask", row.AskUrl);
        Assert.Equal("/sessions/abc123/evaluate", row.EvaluateUrl);
    }

    [Fact]
    public void Speakers_are_joined_for_display()
    {
        var sessions = new[] { PublicSession(1, "Talk", speakers: new[] { "Alice", "Bob" }) };

        var s = MyEventScheduleBuilder.Build(sessions, record: null);

        Assert.Equal("Alice, Bob", s.Agenda[0].Speakers);
    }
}
