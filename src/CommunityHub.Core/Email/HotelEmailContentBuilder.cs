namespace CommunityHub.Core.Email;

/// <summary>The rendered parts of a hotel invitation email + its .ics body.</summary>
public sealed record HotelEmailContent(
    string Subject,
    string HtmlBody,
    string IcsDescription,
    string Location,
    bool IsConfirmed,
    string? EffectiveConfirmationNumber);

/// <summary>
/// Pure (no I/O) builder for the hotel invitation email + calendar description.
/// Lives in Core so it is unit-testable without the web host. The web-side
/// <c>HotelCalendarInviter</c> calls this, then wraps the description in an
/// <see cref="IcsCalendarBuilder"/> VEVENT and sends via the gated
/// <see cref="IEmailSender"/> (DEV-redirected).
///
/// Multi-hotel rules (REQUIREMENTS §3): when the participant has been placed in a
/// specific <c>Hotel</c>, its name + address drive the venue/email, and the
/// organizer-set per-person confirmation number takes priority over any legacy
/// vendor number. A non-blank effective confirmation number is itself treated as
/// "confirmed".
/// </summary>
public static class HotelEmailContentBuilder
{
    public static HotelEmailContent Build(
        string eventCode,
        string fullName,
        DateOnly checkInDate,
        DateOnly checkOutDate,
        bool vendorConfirmed,
        string? vendorConfirmationNumber,
        string? roomType,
        string? hotelName,
        string? hotelAddress,
        string? hotelConfirmationNumber)
    {
        var hasAssignedHotel = !string.IsNullOrWhiteSpace(hotelName);
        var hotelLabel = hasAssignedHotel ? hotelName!.Trim() : "your assigned hotel";
        var address = string.IsNullOrWhiteSpace(hotelAddress) ? null : hotelAddress.Trim();

        var location = hasAssignedHotel
            ? (address is null ? hotelLabel : $"{hotelLabel}, {address}")
            : "your assigned hotel";

        // The per-person organizer-set number wins over the legacy vendor number.
        var effectiveConf = !string.IsNullOrWhiteSpace(hotelConfirmationNumber)
            ? hotelConfirmationNumber!.Trim()
            : (string.IsNullOrWhiteSpace(vendorConfirmationNumber) ? null : vendorConfirmationNumber!.Trim());
        var isConfirmed = vendorConfirmed || effectiveConf is not null;

        var stateTag = isConfirmed ? "[CONFIRMED]" : "[NOT CONFIRMED]";
        var subject = $"{stateTag} {eventCode} Hotel - {hotelLabel}";

        var confLine = isConfirmed
            ? $"Hotel confirmation: {effectiveConf ?? "(pending hotel)"}\n"
            : "Status: Not yet confirmed by hotel. We'll update this entry once the hotel returns the confirmation number.\n";
        var roomTypeLine = string.IsNullOrWhiteSpace(roomType) ? "" : $"Room type: {roomType.Trim()}\n";

        var description =
            $"Your hotel reservation at {location} for {eventCode}.\n\n" +
            $"Hotel:     {hotelLabel}\n" +
            (address is null ? "" : $"Address:   {address}\n") +
            $"Check-in:  {checkInDate:dd/MM/yyyy}\n" +
            $"Check-out: {checkOutDate:dd/MM/yyyy}\n" +
            confLine + roomTypeLine + "\n" +
            "Questions: info@expertslive.dk\n\nCheers,\nELDK-team";

        var firstName = string.IsNullOrWhiteSpace(fullName) ? "there" : fullName.Split(' ')[0];
        var encName = System.Net.WebUtility.HtmlEncode(firstName);
        var encHotelName = System.Net.WebUtility.HtmlEncode(hotelLabel);
        var encAddress = System.Net.WebUtility.HtmlEncode(address ?? "");

        var htmlBody =
            $"<p>Hi {encName},</p>" +
            (isConfirmed
                ? $"<p>Your hotel reservation at <strong>{encHotelName}</strong> for {eventCode} is now <strong>CONFIRMED</strong>.</p>"
                : $"<p>Thanks for submitting your hotel preference for {eventCode}. We've added a placeholder calendar invitation to your inbox.</p>") +
            $"<p><strong>Hotel:</strong> {encHotelName}<br/>" +
            (address is null ? "" : $"<strong>Address:</strong> {encAddress}<br/>") +
            $"<strong>Check-in:</strong> {checkInDate:dd/MM/yyyy}<br/>" +
            $"<strong>Check-out:</strong> {checkOutDate:dd/MM/yyyy}<br/>" +
            (isConfirmed
                ? $"<strong>Confirmation number:</strong> {System.Net.WebUtility.HtmlEncode(effectiveConf ?? "")}<br/>" +
                  (string.IsNullOrWhiteSpace(roomType) ? "" : $"<strong>Room type:</strong> {System.Net.WebUtility.HtmlEncode(roomType.Trim())}") + "</p>"
                : "<strong>Status:</strong> Awaiting hotel confirmation &mdash; we'll update this entry once the hotel returns the confirmation number.</p>") +
            "<p>Cheers,<br/>ELDK-team</p>";

        return new HotelEmailContent(subject, htmlBody, description, location, isConfirmed, effectiveConf);
    }
}
