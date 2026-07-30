using CommunityHub.Core.Email;
using CommunityHub.Notify;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §257 — the AUTOMATIC hotel calendar invite is off by default. When off, the hotel
/// confirmation goes out as a plain e-mail (NO attached METHOD:REQUEST invite) carrying a
/// manual "Add to calendar" Google/Outlook link; when the organizer turns auto invites on,
/// the .ics is attached as before. Verified on <see cref="HotelCalendarInviter"/> directly.
/// </summary>
public sealed class HotelCalendarInviterAutoInviteTests
{
    private sealed class RecordingSender : IEmailSender
    {
        public List<(string To, string Subject, string Html)> Plain { get; } = new();
        public List<(string To, string Subject, string Html, string Ics)> Ics { get; } = new();

        public Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default)
        { Plain.Add((toEmail, subject, htmlBody)); return Task.CompletedTask; }
        public Task SendAsync(string toEmail, string subject, string htmlBody, IReadOnlyCollection<string>? cc, CancellationToken ct = default)
        { Plain.Add((toEmail, subject, htmlBody)); return Task.CompletedTask; }
        public Task SendAsync(string toEmail, string subject, string htmlBody, string textBody, CancellationToken ct = default)
        { Plain.Add((toEmail, subject, htmlBody)); return Task.CompletedTask; }
        public Task SendWithIcsAsync(string toEmail, string subject, string htmlBody, string icsContent, string icsFileName, CancellationToken ct = default)
        { Ics.Add((toEmail, subject, htmlBody, icsContent)); return Task.CompletedTask; }
        public Task SendWithAttachmentsAsync(string toEmail, string subject, string htmlBody, IReadOnlyCollection<EmailAttachment> attachments, CancellationToken ct = default)
        => Task.CompletedTask;
    }

    private static readonly DateOnly CheckIn = new(2027, 2, 8);
    private static readonly DateOnly CheckOut = new(2027, 2, 11);

    private static HotelCalendarInviter NewInviter(RecordingSender sender) =>
        new(sender, Options.Create(new EmailOptions
        {
            FromAddress = "info@example.test",
            FromDisplayName = "Test Community",
        }));

    [Fact]
    public async Task Default_off_sends_plain_email_with_add_to_calendar_link_and_no_ics()
    {
        var sender = new RecordingSender();
        await NewInviter(sender).SendAsync(
            eventCode: "ELDK27", toEmail: "guest@x.test", fullName: "Guest Fake",
            checkInDate: CheckIn, checkOutDate: CheckOut, confirmed: false,
            confirmationNumber: null, roomType: null, participantId: 7, eventId: 1,
            ct: default, hotelName: "Central Plaza Hotel", hotelAddress: null,
            hotelConfirmationNumber: null /* attachCalendarInvite defaults to false */);

        Assert.Empty(sender.Ics);                       // no METHOD:REQUEST invite pushed
        var m = Assert.Single(sender.Plain);
        Assert.Equal("guest@x.test", m.To);
        Assert.Contains(">Google Calendar</a>", m.Html); // manual option is present
        Assert.Contains(">Outlook</a>", m.Html);
        Assert.DoesNotContain("calendar invitation to your inbox", m.Html);
    }

    [Fact]
    public async Task Auto_on_attaches_the_ics_invite()
    {
        var sender = new RecordingSender();
        await NewInviter(sender).SendAsync(
            eventCode: "ELDK27", toEmail: "guest@x.test", fullName: "Guest Fake",
            checkInDate: CheckIn, checkOutDate: CheckOut, confirmed: false,
            confirmationNumber: null, roomType: null, participantId: 7, eventId: 1,
            ct: default, hotelName: "Central Plaza Hotel", hotelAddress: null,
            hotelConfirmationNumber: null, attachCalendarInvite: true);

        Assert.Empty(sender.Plain);
        var m = Assert.Single(sender.Ics);
        Assert.Contains("BEGIN:VCALENDAR", m.Ics);
        Assert.Contains("METHOD:REQUEST", m.Ics);
    }
}
