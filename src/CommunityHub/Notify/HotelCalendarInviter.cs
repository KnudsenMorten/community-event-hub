using CommunityHub.Core.Email;
using Microsoft.Extensions.Options;

namespace CommunityHub.Notify;

/// <summary>
/// Builds + sends the hotel calendar invitation (.ics) when a participant
/// submits or updates a hotel booking. Stable UID per (participant, event)
/// so the receiver's calendar UPDATES the existing entry on re-issue —
/// participants don't accumulate duplicates, and the same UID is reused
/// later when the organizer flips state from NotConfirmed to Confirmed.
/// </summary>
public sealed class HotelCalendarInviter
{
    private readonly IEmailSender _emailSender;
    private readonly EmailOptions _emailOptions;

    public HotelCalendarInviter(
        IEmailSender emailSender, IOptions<EmailOptions> emailOptions)
    {
        _emailSender = emailSender;
        _emailOptions = emailOptions.Value;
    }

    public async Task SendAsync(
        string eventCode,
        string toEmail,
        string fullName,
        DateOnly checkInDate,
        DateOnly checkOutDate,
        bool confirmed,
        string? confirmationNumber,
        string? roomType,
        int participantId,
        int eventId,
        CancellationToken ct = default)
    {
        // All-day events: DTSTART = check-in date (midnight UTC), DTEND = day after check-out.
        var startUtc = new DateTimeOffset(checkInDate.Year, checkInDate.Month, checkInDate.Day, 0, 0, 0, TimeSpan.Zero);
        var endUtc   = new DateTimeOffset(checkOutDate.Year, checkOutDate.Month, checkOutDate.Day, 0, 0, 0, TimeSpan.Zero).AddDays(1);

        var venue = "AC Hotel Bella Sky Copenhagen, Center Boulevard 5, 2300 Copenhagen S";
        var stateTag = confirmed ? "[CONFIRMED]" : "[NOT CONFIRMED]";
        var summary  = $"{stateTag} {eventCode} Hotel - AC Hotel Bella Sky";

        var confLine = confirmed
            ? $"Hotel confirmation: {confirmationNumber ?? "(pending vendor)"} - Room type: {roomType ?? "(per vendor)"}\n"
            : "Status: Not yet confirmed by hotel. We'll update this entry once the hotel returns the confirmation number.\n";

        var description =
            $"Your hotel reservation at AC Hotel Bella Sky Copenhagen for {eventCode}.\n\n" +
            $"Check-in:  {checkInDate:yyyy-MM-dd}\n" +
            $"Check-out: {checkOutDate:yyyy-MM-dd}\n" +
            confLine + "\n" +
            "Questions: info@expertslive.dk\n\nCheers,\nELDK-team";

        var uid = $"hotel-{eventId}-{participantId}@eventhub.expertslive.dk";
        var ics = IcsCalendarBuilder.BuildVEvent(
            uid: uid,
            summary: summary,
            description: description,
            location: venue,
            startUtc: startUtc,
            endUtc: endUtc,
            organizerEmail: _emailOptions.FromAddress,
            organizerName: _emailOptions.FromDisplayName);

        var firstName = string.IsNullOrWhiteSpace(fullName) ? "there" : fullName.Split(' ')[0];
        var encName   = System.Net.WebUtility.HtmlEncode(firstName);

        var subject = summary;
        var htmlBody =
            $"<p>Hi {encName},</p>" +
            (confirmed
                ? $"<p>Your hotel reservation at <strong>AC Hotel Bella Sky Copenhagen</strong> for {eventCode} is now <strong>CONFIRMED</strong>.</p>"
                : $"<p>Thanks for submitting your hotel preference for {eventCode}. We've added a placeholder calendar invitation to your inbox.</p>") +
            $"<p><strong>Check-in:</strong> {checkInDate:yyyy-MM-dd}<br/>" +
            $"<strong>Check-out:</strong> {checkOutDate:yyyy-MM-dd}<br/>" +
            (confirmed
                ? $"<strong>Confirmation number:</strong> {System.Net.WebUtility.HtmlEncode(confirmationNumber ?? "")}<br/>" +
                  $"<strong>Room type:</strong> {System.Net.WebUtility.HtmlEncode(roomType ?? "")}</p>"
                : "<strong>Status:</strong> Awaiting hotel confirmation &mdash; we'll update this entry once the hotel returns the confirmation number.</p>") +
            "<p>Cheers,<br/>ELDK-team</p>";

        await _emailSender.SendWithIcsAsync(
            toEmail, subject, htmlBody, ics, "hotel.ics", ct);
    }
}
