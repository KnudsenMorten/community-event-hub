using CommunityHub.Core.Email;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for <see cref="CalendarLinkBuilder"/> — the manual "add to calendar"
/// option (REQUIREMENTS §257): Google + Outlook web links that OPEN a pre-filled event
/// (nothing is pushed into the recipient's calendar). Times are emitted in UTC.
/// </summary>
public sealed class CalendarLinkBuilderTests
{
    private static readonly DateTimeOffset Start = new(2027, 2, 9, 17, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = new(2027, 2, 9, 21, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AddToCalendarHtml_contains_google_and_outlook_open_links()
    {
        var html = CalendarLinkBuilder.AddToCalendarHtml(
            "ELDK27 Appreciation Dinner", Start, End,
            details: "See you there", location: "AC Hotel Bella Sky");

        Assert.Contains("calendar.google.com/calendar/render?action=TEMPLATE", html);
        Assert.Contains("outlook.office.com/calendar", html);
        Assert.Contains(">Google Calendar</a>", html);
        Assert.Contains(">Outlook</a>", html);
        // It is a self-add option, not an "attached invitation".
        Assert.Contains("Add this to your calendar", html);
    }

    [Fact]
    public void AddToCalendarHtml_encodes_the_urls_for_safe_embedding()
    {
        var html = CalendarLinkBuilder.AddToCalendarHtml("A & B <talk>", Start, End);
        // The raw ampersands of the query string are HTML-encoded (&amp;) so the anchor
        // href is valid markup.
        Assert.Contains("&amp;", html);
        Assert.DoesNotContain("<talk>", html); // title got url+html encoded, no raw tag leaks
    }
}
