using CommunityHub.Core.Email;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// Offline tests for <see cref="HotelEmailContentBuilder"/> — the multi-hotel
/// email/calendar enrichment (REQUIREMENTS §3): the hotel/onboarding email must
/// carry the participant's assigned hotel NAME + ADDRESS + CONFIRMATION NUMBER.
/// FAKE hotel + person names only.
/// </summary>
public sealed class HotelEmailContentTests
{
    private static readonly DateOnly CheckIn = new(2027, 2, 8);
    private static readonly DateOnly CheckOut = new(2027, 2, 11);

    [Fact]
    public void Email_includes_assigned_hotel_name_address_and_confirmation()
    {
        var c = HotelEmailContentBuilder.Build(
            eventCode: "ELDK27",
            fullName: "Ada Fake",
            checkInDate: CheckIn,
            checkOutDate: CheckOut,
            vendorConfirmed: false,
            vendorConfirmationNumber: null,
            roomType: null,
            hotelName: "Central Plaza Hotel",
            hotelAddress: "1 Main Street, Springfield",
            hotelConfirmationNumber: "RES-99887");

        // Subject + HTML name the actual hotel, not a hard-coded venue.
        Assert.Contains("Central Plaza Hotel", c.Subject);
        Assert.Contains("Central Plaza Hotel", c.HtmlBody);
        Assert.Contains("1 Main Street, Springfield", c.HtmlBody);
        Assert.Contains("RES-99887", c.HtmlBody);

        // The .ics description carries the same trio + the location line.
        Assert.Contains("Central Plaza Hotel", c.IcsDescription);
        Assert.Contains("1 Main Street, Springfield", c.IcsDescription);
        Assert.Contains("RES-99887", c.IcsDescription);
        Assert.Equal("Central Plaza Hotel, 1 Main Street, Springfield", c.Location);

        // A non-blank per-person confirmation number marks the booking confirmed.
        Assert.True(c.IsConfirmed);
        Assert.Equal("RES-99887", c.EffectiveConfirmationNumber);
        Assert.Contains("[CONFIRMED]", c.Subject);
    }

    [Fact]
    public void Per_person_confirmation_number_wins_over_legacy_vendor_number()
    {
        var c = HotelEmailContentBuilder.Build(
            eventCode: "ELDK27",
            fullName: "Bo Fake",
            checkInDate: CheckIn,
            checkOutDate: CheckOut,
            vendorConfirmed: true,
            vendorConfirmationNumber: "LEGACY-001",
            roomType: "Double",
            hotelName: "Beta Hotel",
            hotelAddress: "2 Side Road",
            hotelConfirmationNumber: "ORG-555");

        Assert.Equal("ORG-555", c.EffectiveConfirmationNumber);
        Assert.Contains("ORG-555", c.HtmlBody);
        Assert.DoesNotContain("LEGACY-001", c.HtmlBody);
    }

    [Fact]
    public void Falls_back_to_vendor_number_when_no_per_person_number()
    {
        var c = HotelEmailContentBuilder.Build(
            eventCode: "ELDK27",
            fullName: "Cy Fake",
            checkInDate: CheckIn,
            checkOutDate: CheckOut,
            vendorConfirmed: true,
            vendorConfirmationNumber: "VEND-7",
            roomType: null,
            hotelName: "Gamma Hotel",
            hotelAddress: null,
            hotelConfirmationNumber: null);

        Assert.Equal("VEND-7", c.EffectiveConfirmationNumber);
        Assert.Contains("VEND-7", c.IcsDescription);
        // No address line when the hotel has no address.
        Assert.DoesNotContain("Address:", c.IcsDescription);
    }

    [Fact]
    public void Unassigned_hotel_renders_not_confirmed_placeholder()
    {
        var c = HotelEmailContentBuilder.Build(
            eventCode: "ELDK27",
            fullName: "Di Fake",
            checkInDate: CheckIn,
            checkOutDate: CheckOut,
            vendorConfirmed: false,
            vendorConfirmationNumber: null,
            roomType: null,
            hotelName: null,
            hotelAddress: null,
            hotelConfirmationNumber: null);

        Assert.False(c.IsConfirmed);
        Assert.Null(c.EffectiveConfirmationNumber);
        Assert.Contains("[PLACEHOLDER]", c.Subject);
        Assert.Contains("your assigned hotel", c.HtmlBody);
    }

    // §257 — when the automatic invite is NOT attached, the email must not claim an
    // invitation was added to the inbox, and must carry the manual add-to-calendar block.
    [Fact]
    public void No_attached_invite_drops_invitation_wording_and_embeds_add_to_calendar_block()
    {
        var c = HotelEmailContentBuilder.Build(
            eventCode: "ELDK27",
            fullName: "Ada Fake",
            checkInDate: CheckIn,
            checkOutDate: CheckOut,
            vendorConfirmed: false,
            vendorConfirmationNumber: null,
            roomType: null,
            hotelName: "Central Plaza Hotel",
            hotelAddress: null,
            hotelConfirmationNumber: null,
            inviteAttached: false,
            addToCalendarHtml: "<p>ADD-TO-CAL-MARKER</p>");

        Assert.DoesNotContain("calendar invitation to your inbox", c.HtmlBody);
        Assert.Contains("ADD-TO-CAL-MARKER", c.HtmlBody);
    }

    // §257 — the default (invite attached) path keeps the legacy wording and does NOT
    // append an add-to-calendar block, so existing behaviour is byte-for-byte preserved.
    [Fact]
    public void Attached_invite_keeps_invitation_wording_and_no_add_to_calendar_block()
    {
        var c = HotelEmailContentBuilder.Build(
            eventCode: "ELDK27",
            fullName: "Ada Fake",
            checkInDate: CheckIn,
            checkOutDate: CheckOut,
            vendorConfirmed: false,
            vendorConfirmationNumber: null,
            roomType: null,
            hotelName: "Central Plaza Hotel",
            hotelAddress: null,
            hotelConfirmationNumber: null);

        Assert.Contains("calendar invitation to your inbox", c.HtmlBody);
        Assert.DoesNotContain("ADD-TO-CAL-MARKER", c.HtmlBody);
    }
}
