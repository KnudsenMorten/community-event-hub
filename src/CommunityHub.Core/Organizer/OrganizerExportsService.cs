using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Export;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Organizer;

// ---------------------------------------------------------------------------
// Run-sheet row shapes (one per on-site artifact). Each is a flat, display-ready
// projection of EXISTING data — never a new persisted entity. The screen view and
// the CSV export both consume the same rows so what an organizer prints matches
// what downloads.
// ---------------------------------------------------------------------------

/// <summary>One attendee on the on-site attendee list (REQUIREMENTS §20 Organizer
/// "Exports &amp; printable run-sheets"). Read-only projection of <see cref="Attendee"/>.</summary>
public sealed record AttendeeListRow(
    string Name,
    string Email,
    string TicketStatus,
    string? TicketClass,
    string MasterClass,
    bool TwoDay);

/// <summary>One lunch-headcount line per pre-conference day. The venue needs a
/// count of who eats on Setup-day and Pre-day; this rolls up the
/// <see cref="LunchSignup"/> rows. NOT an event check-in — it reports the
/// preference already collected.</summary>
public sealed record LunchHeadcountRow(string Day, int Count);

/// <summary>One person on the lunch run-sheet (who eats which pre-day), so the
/// caterer / desk has names, not just a number.</summary>
public sealed record LunchPersonRow(string Name, string Role, bool SetupDay, bool PreDay);

/// <summary>One room/session sheet line: the printable per-room running order. The
/// room QR token (where a session already carries one in the model) is rendered —
/// this is display only, never a check-in tool.</summary>
public sealed record RoomSheetRow(
    string Room,
    string Title,
    string Type,
    string Length,
    string Speakers,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    string? RoomQrUrl,
    string? PublicToken);

/// <summary>One volunteer-rota line: who works what, where and when, from the
/// volunteer work structure + assignments (who/where/when).</summary>
public sealed record VolunteerRotaRow(
    string Volunteer,
    string Bucket,
    string Subcategory,
    string Task,
    DateOnly? Due,
    string? Shift,
    string? TimeEnd,
    string Status);

/// <summary>One badge-data line — the fields a badge printer / mail-merge needs.
/// Read-only projection of <see cref="Participant"/>; carries no secrets.</summary>
public sealed record BadgeRow(string Name, string Role, string? Company);

/// <summary>The Appreciation-Dinner headcount the venue is ordered against:
/// confirmed people, their guests, and the seat total (§326bh Defect 3 — before
/// this, the only dinner number in the product was a Command Center tile).</summary>
public sealed record DinnerHeadcountRow(int Attending, int PlusOnes, int Total);

/// <summary>One person on the Appreciation-Dinner run-sheet, so the venue gets
/// names + guest counts + the allergy detail per seat rather than a single integer.
/// <paramref name="Allergens"/> is the comma-joined structured allergen set;
/// <paramref name="OtherRequirements"/> merges the structured free-text with the
/// legacy <see cref="DinnerSignup.AllergyNotes"/> so nothing collected is lost.</summary>
public sealed record DinnerPersonRow(
    string Name,
    string Role,
    int PlusOnes,
    string Diet,
    string Allergens,
    string? OtherRequirements,
    string? Comments);

/// <summary>One catering roll-up line — how many people need a given diet or a given
/// allergen avoided, per occasion. This is the surface <see cref="DietaryAggregator"/>
/// was written for (§21 Participant [H]) and, until §326bh, never had.</summary>
public sealed record DietaryCountRow(string Occasion, string Kind, string Item, int Count);

/// <summary>
/// Builds the organizer on-site EXPORTS / run-sheets (REQUIREMENTS §20 Organizer —
/// "Exports &amp; printable run-sheets"). Every projection is a <b>pure, read-only,
/// edition-scoped</b> view over entities that already exist — attendees, lunch
/// sign-ups, sessions, the volunteer work structure and participants. Nothing is
/// persisted and calling a build twice yields the same rows.
///
/// It does NOT introduce any event check-in / live-headcount capability (explicitly
/// out of scope, §20): the lunch numbers are the preference already collected and
/// the room QR token is merely RENDERED from the existing <see cref="Session"/>
/// model. The screen page renders these rows print-friendly (<c>@@media print</c>);
/// the page model serves the same rows as CSV via the shared
/// <see cref="CsvWriter"/>.
///
/// Distinct from <see cref="OrganizerOverviewService"/> /
/// <see cref="CommandCenterService"/> (which AGGREGATE into counts): this flattens
/// to row-level artifacts an organizer prints or downloads.
/// </summary>
public sealed class OrganizerExportsService
{
    private static readonly string LunchSetupDay = "Setup day";
    private static readonly string LunchPreDay = "Pre-day (Master Class day)";

    private readonly CommunityHubDbContext _db;

    public OrganizerExportsService(CommunityHubDbContext db) => _db = db;

    // --- Attendee list ------------------------------------------------------

    /// <summary>The on-site attendee list, ordered by name.</summary>
    public async Task<IReadOnlyList<AttendeeListRow>> BuildAttendeeListAsync(
        int eventId, CancellationToken ct = default)
    {
        // §253 G13: only the ACTIVE mirror set — a soft-cancelled ticket (§128:
        // MirrorState.Cancelled, TicketStatus intact) must not print on the on-site
        // list. Same filter the organizer Attendees page uses (§216).
        var rows = await _db.Attendees
            .Where(a => a.EventId == eventId && a.MirrorState == MirrorState.Active)
            .OrderBy(a => a.LastName).ThenBy(a => a.FirstName)
            .Select(a => new
            {
                a.FirstName,
                a.LastName,
                a.Email,
                a.TicketStatus,
                a.TicketClassName,
                a.MasterClassName,
            })
            .ToListAsync(ct);

        return rows
            .Select(a => new AttendeeListRow(
                FullName(a.FirstName, a.LastName),
                a.Email,
                a.TicketStatus.ToString(),
                a.TicketClassName,
                a.MasterClassName ?? string.Empty,
                a.TicketStatus == TicketStatus.TwoDay))
            .ToList();
    }

    public async Task<string> BuildAttendeeListCsvAsync(int eventId, CancellationToken ct = default)
    {
        var rows = await BuildAttendeeListAsync(eventId, ct);
        return CsvWriter.Write(
            new[] { "Name", "Email", "TicketStatus", "TicketClass", "MasterClass", "TwoDay" },
            rows.Select(r => (IReadOnlyList<string>)new[]
            {
                r.Name, r.Email, r.TicketStatus, r.TicketClass ?? string.Empty,
                r.MasterClass, r.TwoDay ? "yes" : "",
            }));
    }

    // --- Lunch headcount ----------------------------------------------------

    /// <summary>Lunch headcount per pre-conference day (Setup + Pre-day).</summary>
    public async Task<IReadOnlyList<LunchHeadcountRow>> BuildLunchHeadcountAsync(
        int eventId, CancellationToken ct = default)
    {
        // ACTIVE people only (§253 G4): the caterer order must never count a
        // sign-up whose person has been deactivated.
        var signups = await _db.LunchSignups
            .Where(l => l.EventId == eventId && l.Participant.IsActive)
            .Select(l => new { l.LunchSetupDay, l.LunchPreDay })
            .ToListAsync(ct);

        return new List<LunchHeadcountRow>
        {
            new(LunchSetupDay, signups.Count(s => s.LunchSetupDay)),
            new(LunchPreDay, signups.Count(s => s.LunchPreDay)),
        };
    }

    /// <summary>The per-person lunch run-sheet: who eats which day, ordered by name.
    /// Only rows that opted into at least one day are listed.</summary>
    public async Task<IReadOnlyList<LunchPersonRow>> BuildLunchPeopleAsync(
        int eventId, CancellationToken ct = default)
    {
        // ACTIVE people only (§253 G4) — matches BuildLunchHeadcountAsync so the
        // run-sheet names always sum to the headcount.
        var rows = await _db.LunchSignups
            .Where(l => l.EventId == eventId && (l.LunchSetupDay || l.LunchPreDay)
                        && l.Participant.IsActive)
            .Select(l => new
            {
                l.Participant.FullName,
                l.Participant.Email,
                Role = l.Participant.Role,
                l.LunchSetupDay,
                l.LunchPreDay,
            })
            .ToListAsync(ct);

        return rows
            .Select(l => new LunchPersonRow(
                string.IsNullOrWhiteSpace(l.FullName) ? l.Email : l.FullName,
                l.Role.ToString(),
                l.LunchSetupDay,
                l.LunchPreDay))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<string> BuildLunchCsvAsync(int eventId, CancellationToken ct = default)
    {
        var people = await BuildLunchPeopleAsync(eventId, ct);
        return CsvWriter.Write(
            new[] { "Name", "Role", "SetupDay", "PreDay" },
            people.Select(p => (IReadOnlyList<string>)new[]
            {
                p.Name, p.Role, p.SetupDay ? "yes" : "", p.PreDay ? "yes" : "",
            }));
    }

    // --- Appreciation Dinner + catering dietary roll-up ---------------------
    // §326bh: the dinner had NO run-sheet and NO export, and the structured
    // dietary capture had no reader at all — allergies were collected from every
    // dinner respondent and reached nobody who orders food. Both are served here,
    // ACTIVE people only (§253 G6), so the venue order and the allergy list are
    // built from the same rows the headcount counts.

    /// <summary>Who is confirmed for the Appreciation Dinner, and how many seats
    /// that is once plus-ones are added.</summary>
    public async Task<DinnerHeadcountRow> BuildDinnerHeadcountAsync(
        int eventId, CancellationToken ct = default)
    {
        var plusOnes = await _db.DinnerSignups
            .Where(d => d.EventId == eventId
                        && d.Rsvp == DinnerRsvp.Yes
                        && d.Participant.IsActive)
            .Select(d => d.PlusOneCount)
            .ToListAsync(ct);

        var guests = plusOnes.Sum(n => Math.Max(0, n));
        return new DinnerHeadcountRow(plusOnes.Count, guests, plusOnes.Count + guests);
    }

    /// <summary>The per-person dinner run-sheet — a confirmed (RSVP = Yes) seat per
    /// row with its guest count and dietary detail, ordered by name. Only RSVP=Yes
    /// is listed: a Maybe has not taken a seat, and ordering food for one is the
    /// same waste as ordering for a drop-out.</summary>
    public async Task<IReadOnlyList<DinnerPersonRow>> BuildDinnerRunSheetAsync(
        int eventId, CancellationToken ct = default)
    {
        var signups = await _db.DinnerSignups
            .Where(d => d.EventId == eventId
                        && d.Rsvp == DinnerRsvp.Yes
                        && d.Participant.IsActive)
            .Select(d => new
            {
                d.ParticipantId,
                d.Participant.FullName,
                d.Participant.Email,
                Role = d.Participant.Role,
                d.PlusOneCount,
                d.AllergyNotes,
                d.Comments,
            })
            .ToListAsync(ct);
        if (signups.Count == 0) return Array.Empty<DinnerPersonRow>();

        var ids = signups.Select(s => s.ParticipantId).ToList();
        var dietById = (await _db.DietaryRequirements
                .Where(x => x.EventId == eventId
                            && x.Surface == DietarySurface.Dinner
                            && ids.Contains(x.ParticipantId))
                .ToListAsync(ct))
            .ToDictionary(x => x.ParticipantId);

        return signups
            .Select(s =>
            {
                dietById.TryGetValue(s.ParticipantId, out var d);
                return new DinnerPersonRow(
                    string.IsNullOrWhiteSpace(s.FullName) ? s.Email : s.FullName,
                    s.Role.ToString(),
                    Math.Max(0, s.PlusOneCount),
                    DietLabel(d),
                    AllergenLabel(d),
                    // The structured "other" plus the legacy free-text note (no longer
                    // written, but historic rows still carry one) — a caterer must see both.
                    MergeNotes(d?.OtherAllergens, s.AllergyNotes),
                    string.IsNullOrWhiteSpace(s.Comments) ? null : s.Comments);
            })
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The catering roll-up per occasion: diet head-counts, allergen
    /// head-counts, and the two totals a kitchen needs (how many people carry any
    /// requirement, and how many free-text notes a human must still read).
    ///
    /// The Dinner occasion counts only people whose RSVP is Yes, so its numbers
    /// reconcile exactly with <see cref="BuildDinnerRunSheetAsync"/> — a diet filled
    /// in by someone who then declined must not reach the kitchen order.</summary>
    public async Task<IReadOnlyList<DietaryCountRow>> BuildDietaryHeadcountAsync(
        int eventId, CancellationToken ct = default)
    {
        var rows = await _db.DietaryRequirements
            .Where(x => x.EventId == eventId && x.Participant.IsActive)
            .ToListAsync(ct);
        if (rows.Count == 0) return Array.Empty<DietaryCountRow>();

        var dinnerAttending = (await _db.DinnerSignups
                .Where(d => d.EventId == eventId
                            && d.Rsvp == DinnerRsvp.Yes
                            && d.Participant.IsActive)
                .Select(d => d.ParticipantId)
                .ToListAsync(ct))
            .ToHashSet();

        var result = new List<DietaryCountRow>();

        // Dinner first (the order with a deadline), then day-catering. A surface with
        // no rows is skipped rather than printed as a row of zeros.
        foreach (var surface in new[] { DietarySurface.Dinner, DietarySurface.SpeakerCatering })
        {
            var forSurface = rows
                .Where(r => r.Surface == surface)
                .Where(r => surface != DietarySurface.Dinner
                            || dinnerAttending.Contains(r.ParticipantId))
                .ToList();
            if (forSurface.Count == 0) continue;

            var summary = DietaryAggregator.Aggregate(forSurface);
            var occasion = OccasionLabel(surface);

            foreach (var diet in summary.Diets)
                result.Add(new DietaryCountRow(occasion, "Diet", diet.Key, diet.Count));
            foreach (var allergen in summary.Allergens)
                result.Add(new DietaryCountRow(occasion, "Allergen", allergen.Key, allergen.Count));

            result.Add(new DietaryCountRow(
                occasion, "Total", "People with any requirement", summary.PeopleWithAnyRequirement));
            result.Add(new DietaryCountRow(
                occasion, "Total", "Free-text notes to read", summary.FreeTextCount));
        }

        return result;
    }

    /// <summary>The dinner run-sheet as CSV. The headcount line is carried in the
    /// header comment-free by design (CSV stays machine-readable) — the screen view
    /// and <see cref="BuildDinnerHeadcountAsync"/> report the totals.</summary>
    public async Task<string> BuildDinnerCsvAsync(int eventId, CancellationToken ct = default)
    {
        var people = await BuildDinnerRunSheetAsync(eventId, ct);
        return CsvWriter.Write(
            new[] { "Name", "Role", "PlusOnes", "Diet", "Allergens", "OtherRequirements", "Comments" },
            people.Select(p => (IReadOnlyList<string>)new[]
            {
                p.Name, p.Role, p.PlusOnes.ToString(), p.Diet, p.Allergens,
                p.OtherRequirements ?? string.Empty, p.Comments ?? string.Empty,
            }));
    }

    /// <summary>The catering dietary roll-up as CSV — the sheet that goes to the kitchen.</summary>
    public async Task<string> BuildDietaryCsvAsync(int eventId, CancellationToken ct = default)
    {
        var rows = await BuildDietaryHeadcountAsync(eventId, ct);
        return CsvWriter.Write(
            new[] { "Occasion", "Kind", "Item", "Count" },
            rows.Select(r => (IReadOnlyList<string>)new[]
            {
                r.Occasion, r.Kind, r.Item, r.Count.ToString(),
            }));
    }

    private static string OccasionLabel(DietarySurface surface) => surface switch
    {
        DietarySurface.Dinner => "Appreciation Dinner",
        DietarySurface.SpeakerCatering => "Speaker / crew day catering",
        _ => surface.ToString(),
    };

    private static string DietLabel(DietaryRequirement? d) =>
        !string.IsNullOrWhiteSpace(d?.DietChoice) && d.DietChoice != "None"
            ? d.DietChoice!
            : string.Empty;

    private static string AllergenLabel(DietaryRequirement? d) =>
        d is null
            ? string.Empty
            : string.Join(", ", d.Allergens().Where(a => a.IsSet).Select(a => a.Token));

    private static string? MergeNotes(string? structured, string? legacy)
    {
        var parts = new[] { structured, legacy }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return parts.Count == 0 ? null : string.Join(" | ", parts);
    }

    // --- Room / session sheets ---------------------------------------------

    /// <summary>The room/session run-sheet, ordered by room then start time. Carries
    /// the room QR url + the session public token where the model already has them
    /// (rendered for printing onto a room sheet — never a check-in surface).</summary>
    public async Task<IReadOnlyList<RoomSheetRow>> BuildRoomSheetsAsync(
        int eventId, CancellationToken ct = default)
    {
        var sessions = await _db.Sessions
            .Where(s => s.EventId == eventId && !s.IsServiceSession)
            .Select(s => new
            {
                s.Room,
                s.Title,
                s.Type,
                s.Length,
                s.LengthMinutes,
                s.StartsAt,
                s.EndsAt,
                s.RoomQrUrl,
                s.PublicToken,
                Speakers = s.SessionSpeakers
                    .Select(ss => ss.Participant.FullName)
                    .ToList(),
            })
            .ToListAsync(ct);

        return sessions
            .Select(s => new RoomSheetRow(
                string.IsNullOrWhiteSpace(s.Room) ? "(unassigned)" : s.Room,
                s.Title,
                s.Type.ToString(),
                // §299.8/b7: the exact minutes are the source of truth ("37 min",
                // "420 min"); the legacy bucket label only covers old rows without one.
                s.LengthMinutes is int lm ? $"{lm} min" : LengthLabel(s.Length),
                string.Join(", ", s.Speakers.Where(n => !string.IsNullOrWhiteSpace(n))),
                s.StartsAt,
                s.EndsAt,
                s.RoomQrUrl,
                s.PublicToken))
            .OrderBy(r => r.Room, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.StartsAt ?? DateTimeOffset.MaxValue)
            .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<string> BuildRoomSheetsCsvAsync(int eventId, CancellationToken ct = default)
    {
        var rows = await BuildRoomSheetsAsync(eventId, ct);
        return CsvWriter.Write(
            new[] { "Room", "Title", "Type", "Length", "Speakers", "StartsUtc", "EndsUtc", "RoomQrUrl", "PublicToken" },
            rows.Select(r => (IReadOnlyList<string>)new[]
            {
                r.Room, r.Title, r.Type, r.Length, r.Speakers,
                r.StartsAt?.UtcDateTime.ToString("yyyy-MM-dd HH:mm") ?? string.Empty,
                r.EndsAt?.UtcDateTime.ToString("yyyy-MM-dd HH:mm") ?? string.Empty,
                r.RoomQrUrl ?? string.Empty, r.PublicToken ?? string.Empty,
            }));
    }

    // --- Volunteer rota -----------------------------------------------------

    /// <summary>The volunteer rota — one row per (volunteer, assigned task) — with
    /// where/when from the work structure. Ordered by volunteer, then due, then
    /// shift window. Cancelled tasks are excluded.</summary>
    public async Task<IReadOnlyList<VolunteerRotaRow>> BuildVolunteerRotaAsync(
        int eventId, CancellationToken ct = default)
    {
        // EFFECTIVE assignments only (§253 G7): a withdrawn (deactivated)
        // volunteer — or a shift the volunteer declined — must not print on the
        // day-of rota the crew works from.
        var rows = await _db.VolunteerTaskAssignments
            .Where(a => a.EventId == eventId
                        && a.Task.Status != VolunteerTaskStatus.Cancelled
                        && a.Participant.IsActive
                        && a.DecisionStatus != ShiftDecisionStatus.Declined)
            .Select(a => new
            {
                a.Participant.FullName,
                a.Participant.Email,
                Bucket = a.Task.Subcategory.Category.Name,
                Subcategory = a.Task.Subcategory.Name,
                Task = a.Task.Title,
                a.Task.DueDate,
                a.Task.Shift,
                a.Task.TimeEnd,
                Status = a.Task.Status,
            })
            .ToListAsync(ct);

        return rows
            .Select(a => new VolunteerRotaRow(
                string.IsNullOrWhiteSpace(a.FullName) ? a.Email : a.FullName,
                string.IsNullOrWhiteSpace(a.Bucket) ? "(uncategorized)" : a.Bucket,
                a.Subcategory,
                a.Task,
                a.DueDate,
                string.IsNullOrWhiteSpace(a.Shift) ? null : a.Shift,
                string.IsNullOrWhiteSpace(a.TimeEnd) ? null : a.TimeEnd,
                a.Status.ToString()))
            .OrderBy(r => r.Volunteer, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Due ?? DateOnly.MaxValue)
            .ThenBy(r => r.Shift ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Task, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<string> BuildVolunteerRotaCsvAsync(int eventId, CancellationToken ct = default)
    {
        var rows = await BuildVolunteerRotaAsync(eventId, ct);
        return CsvWriter.Write(
            new[] { "Volunteer", "Bucket", "Subcategory", "Task", "Due", "Shift", "TimeEnd", "Status" },
            rows.Select(r => (IReadOnlyList<string>)new[]
            {
                r.Volunteer, r.Bucket, r.Subcategory, r.Task,
                r.Due?.ToString("yyyy-MM-dd") ?? string.Empty,
                r.Shift ?? string.Empty, r.TimeEnd ?? string.Empty, r.Status,
            }));
    }

    // --- Badge data ---------------------------------------------------------

    /// <summary>The badge-data export: every ACTIVE participant's name + role
    /// (+ sponsor company id where set), ordered by name. The minimal field set a
    /// badge printer / mail-merge needs — no contact details, no secrets.</summary>
    public async Task<IReadOnlyList<BadgeRow>> BuildBadgeDataAsync(
        int eventId, CancellationToken ct = default)
    {
        var rows = await _db.Participants
            .Where(p => p.EventId == eventId && p.IsActive && !p.IsTestUser)
            .OrderBy(p => p.FullName)
            .Select(p => new { p.FullName, p.Email, Role = p.Role, p.SponsorCompanyId })
            .ToListAsync(ct);

        return rows
            .Select(p => new BadgeRow(
                string.IsNullOrWhiteSpace(p.FullName) ? p.Email : p.FullName,
                p.Role.ToString(),
                string.IsNullOrWhiteSpace(p.SponsorCompanyId) ? null : p.SponsorCompanyId))
            .ToList();
    }

    public async Task<string> BuildBadgeDataCsvAsync(int eventId, CancellationToken ct = default)
    {
        var rows = await BuildBadgeDataAsync(eventId, ct);
        return CsvWriter.Write(
            new[] { "Name", "Role", "Company" },
            rows.Select(r => (IReadOnlyList<string>)new[]
            {
                r.Name, r.Role, r.Company ?? string.Empty,
            }));
    }

    // --- helpers ------------------------------------------------------------

    private static string FullName(string first, string last)
        => string.Join(' ', new[] { first, last }.Where(s => !string.IsNullOrWhiteSpace(s)));

    private static string LengthLabel(SessionLength length) => length switch
    {
        SessionLength.FullDay => "Full day",
        SessionLength.TwentyMin => "20 min",
        SessionLength.FiftyMin => "50 min",
        SessionLength.SixtyMin => "60 min",
        _ => length.ToString(),
    };
}
