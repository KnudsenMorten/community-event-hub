namespace CommunityHub.Core.Domain;

/// <summary>§1386 — who caused a hotel change.</summary>
public enum HotelChangeSource
{
    /// <summary>The participant changed their own hotel form.</summary>
    Participant = 0,
    /// <summary>An organizer changed a placement, extra nights, an allocation, a state.</summary>
    Organizer = 1,
    /// <summary>The hotel's returned spreadsheet was imported.</summary>
    Import = 2,
    /// <summary>CEH did it (auto-placement, the contract seed, freezing the original reservation).</summary>
    System = 3,
}

/// <summary>
/// §1386 — one line in the HOTEL CHANGE LOG (operator 2026-11-08: <i>"we need to have a change log
/// that shows any changes, both made by a participant changing hotel + organizer changing hotel like
/// extra agreed hotel dates + any allocation changes"</i>).
/// </summary>
/// <remarks>
/// <para>🔑 It doubles as the OUTBOX for the hotel-ops change mail: an entry with
/// <see cref="AffectsGuestList"/> and no <see cref="NotifiedAt"/> is a change the hotel has not been
/// sent yet. The job mails the hotel's ops recipients the updated spreadsheet and stamps it. Entries
/// that do not change a guest row (an allocation change) are mailed as information, without a
/// spreadsheet requirement.</para>
/// <para>Append-only. Nothing edits or deletes a line once written.</para>
/// </remarks>
public class HotelChangeLogEntry
{
    public long Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>The hotel this change is about, when there is one (null = not placed / edition-wide).</summary>
    public int? HotelId { get; set; }

    /// <summary>The guest this change is about, when there is one.</summary>
    public int? ParticipantId { get; set; }

    /// <summary>Denormalised name so the log still reads after a person is deleted.</summary>
    public string? ParticipantName { get; set; }

    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;

    public HotelChangeSource Source { get; set; }

    /// <summary>Who did it — an e-mail address, or "system".</summary>
    public string? Actor { get; set; }

    /// <summary>A short machine-readable kind, see <see cref="HotelChangeKinds"/>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>One human-readable line, e.g. "Dates 8 Feb → 10 Feb changed to 7 Feb → 10 Feb".</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>True when the hotel's guest spreadsheet changes because of this entry.</summary>
    public bool AffectsGuestList { get; set; }

    /// <summary>When the hotel-ops change mail carried this entry. Null = not mailed yet.</summary>
    public DateTimeOffset? NotifiedAt { get; set; }
}

/// <summary>§1386 — the change-log kinds. Strings, so a new kind needs no migration.</summary>
public static class HotelChangeKinds
{
    public const string BookingCreated = "booking-created";
    public const string BookingChanged = "booking-changed";
    public const string BookingCancelled = "booking-cancelled";
    public const string BookingReinstated = "booking-reinstated";
    public const string Placed = "placed";
    public const string ExtraNights = "extra-nights";
    public const string Allocation = "allocation";
    public const string OriginalFrozen = "original-frozen";
    public const string Forecast = "forecast";
    public const string ConfirmationImported = "confirmation-imported";
    public const string CancellationImported = "cancellation-imported";
    public const string StateChanged = "state-changed";
    public const string Seed = "seed";
    /// <summary>A deadline warning was mailed — the key carries cut-off id and lead, so it is sent once.</summary>
    public const string DeadlineWarning = "deadline-warning";
    /// <summary>§1386 — an organizer marked the hotel's spreadsheet as sent to the hotel.</summary>
    public const string SentToHotel = "sent-to-hotel";
}

/// <summary>
/// §1386 — organizer-entered EXTRA rooms expected for one night, on top of the reservations already
/// made (operator: <i>"we might get 5 more speakers + 5 more volunteers … here we need to be able to
/// say 8th feb 2027 +6, 9th feb 2027 +8 so we know how many extras to block"</i>). Edition-wide —
/// a forecast is about people who have not chosen anything yet, so it belongs to no hotel.
/// </summary>
public class HotelForecastExtra
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>The night slept (same convention as <see cref="HotelAllotment.Night"/>).</summary>
    public DateOnly Night { get; set; }

    /// <summary>Extra rooms expected that night. Never negative.</summary>
    public int ExtraRooms { get; set; }

    /// <summary>Why ("5 more speakers + 1 volunteer").</summary>
    public string? Note { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? UpdatedByEmail { get; set; }
}
