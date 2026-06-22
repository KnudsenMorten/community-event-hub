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
    private readonly IEmailContextAccessor? _context;

    public HotelCalendarInviter(
        IEmailSender emailSender, IOptions<EmailOptions> emailOptions,
        IEmailContextAccessor? context = null)
    {
        _emailSender = emailSender;
        _emailOptions = emailOptions.Value;
        _context = context;
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
        CancellationToken ct = default,
        string? hotelName = null,
        string? hotelAddress = null,
        string? hotelConfirmationNumber = null)
    {
        // All-day events: DTSTART = check-in date (midnight UTC), DTEND = day after check-out.
        var startUtc = new DateTimeOffset(checkInDate.Year, checkInDate.Month, checkInDate.Day, 0, 0, 0, TimeSpan.Zero);
        var endUtc   = new DateTimeOffset(checkOutDate.Year, checkOutDate.Month, checkOutDate.Day, 0, 0, 0, TimeSpan.Zero).AddDays(1);

        // Build the email + calendar text via the pure Core builder (unit-tested):
        // it folds the organizer-assigned hotel name + address + the per-person
        // confirmation number into the venue/subject/body (multi-hotel placement).
        var content = HotelEmailContentBuilder.Build(
            eventCode: eventCode,
            fullName: fullName,
            checkInDate: checkInDate,
            checkOutDate: checkOutDate,
            vendorConfirmed: confirmed,
            vendorConfirmationNumber: confirmationNumber,
            roomType: roomType,
            hotelName: hotelName,
            hotelAddress: hotelAddress,
            hotelConfirmationNumber: hotelConfirmationNumber);

        var uid = $"hotel-{eventId}-{participantId}@eventhub.expertslive.dk";
        var ics = IcsCalendarBuilder.BuildVEvent(
            uid: uid,
            summary: content.Subject,
            description: content.IcsDescription,
            location: content.Location,
            startUtc: startUtc,
            endUtc: endUtc,
            organizerEmail: _emailOptions.FromAddress,
            organizerName: _emailOptions.FromDisplayName);

        // Ring-governed by the hotel-invite feature (operator 2026-06-22).
        using (_context?.Set(new EmailContext(
            "hotel-invite", eventId, participantId, fullName, FeatureKey: "hotel-invite")))
        {
            await _emailSender.SendWithIcsAsync(
                toEmail, content.Subject, content.HtmlBody, ics, "hotel.ics", ct);
        }
    }
}
