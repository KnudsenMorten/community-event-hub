namespace CommunityHub.Core.Integrations;

/// <summary>
/// One booked master-class participant fetched from Zoho Booking, flattened
/// for the one-way Booking → hub sync (REQUIREMENTS § 6c).
/// </summary>
/// <param name="BookingRecordId">The Zoho Booking record/appointment id — the idempotency key.</param>
/// <param name="Email">The booked person's email (used to match/create the hub participant).</param>
/// <param name="Name">The booked person's display name.</param>
/// <param name="Status">The raw booking status (upcoming / completed / cancelled / …).</param>
public sealed record MasterClassBooking(
    string BookingRecordId,
    string Email,
    string Name,
    string Status);

/// <summary>
/// The per-master-class Zoho Booking fetch seam (REQUIREMENTS § 6c): given a
/// master class's configured Zoho Booking endpoint URI, pull the bookings for
/// that class (one-way Booking → hub).
///
/// Follows the established repo gated-seam pattern (cf.
/// <see cref="IRoomQrProvider"/> / <see cref="IBackstageSpeakerEmailApi"/>): a
/// clean interface with a no-op <see cref="NullMasterClassBookingFetcher"/>
/// default that performs NO Booking call and NEVER fakes participants.
///
/// HONEST STATUS (🟡 live wiring pending): the real Zoho Booking endpoint URI is
/// per-master-class operator config (organizer-set in the admin area), and the
/// fetch creds (OAuth) are operator config (Key Vault / gitignored), not in this
/// repo — so the default registration is the Null fetcher
/// (<see cref="CanFetch"/> = false). A live fetcher (e.g. over the existing
/// <see cref="ZohoClient"/> Bookings call) returns real bookings; no caller
/// changes.
/// </summary>
public interface IMasterClassBookingFetcher
{
    /// <summary>
    /// Whether this implementation can actually call Zoho Booking. False for the
    /// null default (no wired endpoint/creds) — the sync then reports "not
    /// configured" rather than faking bookings.
    /// </summary>
    bool CanFetch { get; }

    /// <summary>
    /// Fetch the bookings for one master class from its configured Zoho Booking
    /// endpoint. <paramref name="bookingEndpointUri"/> is the per-master-class
    /// endpoint the organizer mapped. Only called when <see cref="CanFetch"/> is
    /// true and the endpoint is non-blank.
    /// </summary>
    Task<IReadOnlyList<MasterClassBooking>> FetchAsync(
        string bookingEndpointUri,
        CancellationToken ct = default);
}

/// <summary>
/// Default no-op implementation: there is no wired Zoho Booking endpoint/creds
/// for the master-class participant sync (operator config not in this repo), so
/// this cannot fetch and performs no call. The hub still stores the per-master-
/// class endpoint mapping the organizer enters; the live wiring is 🟡 (pending)
/// and no Booking call — nor any participant — is ever faked.
/// </summary>
public sealed class NullMasterClassBookingFetcher : IMasterClassBookingFetcher
{
    public bool CanFetch => false;

    public Task<IReadOnlyList<MasterClassBooking>> FetchAsync(
        string bookingEndpointUri, CancellationToken ct = default) =>
        throw new InvalidOperationException(
            "No wired Zoho Booking endpoint/creds for the master-class participant "
            + "sync (operator config not in this repo). Do not call FetchAsync when "
            + "CanFetch is false.");
}
