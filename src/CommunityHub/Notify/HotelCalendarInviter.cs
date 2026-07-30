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
        string? hotelConfirmationNumber = null,
        bool attachCalendarInvite = false)
    {
        // All-day events: DTSTART = check-in date (midnight UTC), DTEND = day after check-out.
        var startUtc = new DateTimeOffset(checkInDate.Year, checkInDate.Month, checkInDate.Day, 0, 0, 0, TimeSpan.Zero);
        var endUtc   = new DateTimeOffset(checkOutDate.Year, checkOutDate.Month, checkOutDate.Day, 0, 0, 0, TimeSpan.Zero).AddDays(1);

        // §257: when the automatic invite is off (the default), the hotel confirmation
        // carries a manual "Add to calendar" link instead of an attached METHOD:REQUEST
        // invite. Build the link block up front so the (unit-tested) Core builder can fold
        // it into the body.
        var addToCalendarHtml = attachCalendarInvite
            ? null
            : CalendarLinkBuilder.AddToCalendarHtml(
                title: $"{eventCode} Hotel",
                startUtc: startUtc,
                endUtc: endUtc,
                details: $"Your hotel reservation for {eventCode}.",
                location: hotelName);

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
            hotelConfirmationNumber: hotelConfirmationNumber,
            inviteAttached: attachCalendarInvite,
            addToCalendarHtml: addToCalendarHtml);

        // §705.15 — this is the ORGANIZER-TRIGGERED guest mail: he enters a hotel confirmation number
        // and EVERY guest placed in that hotel is written to. It keeps a ring precisely because the
        // guest did not ask for it (§705.14c), unlike the "Add to calendar" self-send which is
        // user-initiated and exempt.
        //
        // 🔒 TemplateName is what carries the mail identity, so this mail now has its own row + ring
        // under a name that says what it is. The old generic "hotel-invite" stood for THREE different
        // behaviours, which is why it read as "the hotel mail" to everyone.
        using (_context?.Set(new EmailContext(
            "hotel-invite", eventId, participantId, fullName,
            TemplateName: "hotel-confirmation-guest", FeatureKey: "hotel-invite")))
        {
            if (!attachCalendarInvite)
            {
                await _emailSender.SendAsync(toEmail, content.Subject, content.HtmlBody, ct);
                return;
            }

            var uid = $"hotel-{eventId}-{participantId}@eventhub.expertslive.dk";
            // §234 6: name the RECIPIENT as ATTENDEE so mail clients process the
            // METHOD:REQUEST as a real invitation (auto-add / Accept), not a dead attachment.
            var ics = IcsCalendarBuilder.BuildVEvent(
                uid: uid,
                summary: content.Subject,
                description: content.IcsDescription,
                location: content.Location,
                startUtc: startUtc,
                endUtc: endUtc,
                organizerEmail: _emailOptions.FromAddress,
                organizerName: _emailOptions.FromDisplayName,
                allDay: false,
                attendeeEmail: toEmail,
                attendeeName: fullName);

            await _emailSender.SendWithIcsAsync(
                toEmail, content.Subject, content.HtmlBody, ics, "hotel.ics", ct);
        }
    }
}
