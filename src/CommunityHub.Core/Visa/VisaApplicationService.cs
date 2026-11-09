using System.Globalization;
using System.Text;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Visa;

/// <summary>One printed/exported line of a visa application.</summary>
public sealed record VisaField(string Section, string Label, Func<VisaApplication, string> Value, bool Required);

/// <summary>A flagged (or answered) person on the organizer list.</summary>
public sealed record VisaRow(int ParticipantId, string FullName, string Email, ParticipantRole Role, bool NeedsVisa, VisaApplication? Application)
{
    public bool Submitted => Application?.SubmittedAt is not null;
}

/// <summary>
/// §1374 — the visa form: who may see it, saving the applicant's answers, the organizer list, the
/// application ID, and the Excel/PDF content.
/// </summary>
/// <remarks>
/// <para>🔑 <see cref="Fields"/> is the ONE list of what the form holds. The required-field check, the
/// organizer detail view, the Excel export and the PDF all read it, so a field added to the form cannot
/// be missing from an export.</para>
/// <para>The applicant gets no mail (operator 2026-10-28: a form, not an e-mail flow); they see their
/// application ID and any letter (§1383) on <c>/Forms/Visa</c>. Organizers see submissions on
/// <c>/Organizer/VisaApplications</c>, and since §1383 the organizers' mailbox is told on submit
/// (<see cref="VisaNotifier"/>).</para>
/// </remarks>
public sealed class VisaApplicationService
{
    public const string SectionYou = "1. About you";
    public const string SectionCompany = "2. About your company";
    public const string SectionRelationship = "3. Relationship between our companies";
    public const string SectionVisit = "4. About the visit";
    public const string SectionExpenses = "5. Expenses and accommodation";

    private static string D(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
    private static string S(string? s) => s?.Trim() ?? string.Empty;

    public static readonly IReadOnlyList<VisaField> Fields = new VisaField[]
    {
        new(SectionYou, "First name(s)", a => S(a.FirstNames), true),
        new(SectionYou, "Last name", a => S(a.LastName), true),
        new(SectionYou, "Date of birth", a => D(a.DateOfBirth), true),
        new(SectionYou, "Gender", a => S(a.Gender), true),
        new(SectionYou, "Nationality", a => VisaFormOptions.CountryName(a.Nationality), true),
        new(SectionYou, "Home address (street and number)", a => S(a.HomeStreet), true),
        new(SectionYou, "Postal code", a => S(a.HomePostalCode), true),
        new(SectionYou, "City", a => S(a.HomeCity), true),
        new(SectionYou, "Country", a => VisaFormOptions.CountryName(a.HomeCountry), true),
        new(SectionYou, "Phone number", a => S(a.Phone), true),
        new(SectionYou, "Email address", a => S(a.Email), true),

        new(SectionCompany, "Company name", a => S(a.CompanyName), true),
        new(SectionCompany, "Company address", a => S(a.CompanyStreet), true),
        new(SectionCompany, "Postal code", a => S(a.CompanyPostalCode), true),
        new(SectionCompany, "City", a => S(a.CompanyCity), true),
        new(SectionCompany, "Country", a => VisaFormOptions.CountryName(a.CompanyCountry), true),
        new(SectionCompany, "Telephone number", a => S(a.CompanyPhone), false),
        new(SectionCompany, "Email address", a => S(a.CompanyEmail), true),
        new(SectionCompany, "Website", a => S(a.CompanyWebsite), true),
        new(SectionCompany, "Department and role/function in the company", a => S(a.DepartmentAndRole), true),

        new(SectionRelationship, "Visited Experts Live Denmark before", a => VisaFormOptions.YesNo(a.VisitedBefore), true),
        new(SectionRelationship, "What does your company do", a => S(a.CompanyActivity), true),
        new(SectionRelationship, "How the contact between our companies came about", a => S(a.ContactOrigin), true),
        new(SectionRelationship, "How long the business relationship has existed", a => S(a.RelationshipDuration), true),
        new(SectionRelationship, "Estimated financial scope of the business relationship", a => S(a.FinancialScope), true),

        new(SectionVisit, "Planned arrival date", a => D(a.ArrivalDate), true),
        new(SectionVisit, "Expected length of visit (days)", a => a.LengthOfVisitDays?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, true),
        new(SectionVisit, "Number of entries needed", a => S(a.Entries), true),
        new(SectionVisit, "Purpose of the visit", a => S(a.Purpose), true),
        new(SectionVisit, "Special circumstances or events", a => S(a.SpecialCircumstances), false),

        new(SectionExpenses, "Who pays for the travel", a => S(a.TravelPaidBy), true),
        new(SectionExpenses, "Who pays for the stay", a => S(a.StayPaidBy), true),
        new(SectionExpenses, "Where they will stay during the visit", a => S(a.Accommodation), true),
        new(SectionExpenses, "Host company guarantees travel and stay financially", a => VisaFormOptions.YesNo(a.HostGuarantees), true),
    };

    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;
    private readonly VisaNotifier? _notifier;

    /// <param name="notifier">§1383 — tells the organizers' mailbox about a submit; null = no mail
    /// (the task sync callers that only touch the task list).</param>
    public VisaApplicationService(CommunityHubDbContext db, TimeProvider? clock = null, VisaNotifier? notifier = null)
    {
        _db = db;
        _clock = clock ?? TimeProvider.System;
        _notifier = notifier;
    }

    /// <summary>True when this participant is flagged <see cref="Participant.NeedsVisa"/> (and active).</summary>
    public Task<bool> MaySeeFormAsync(int eventId, int participantId, CancellationToken ct = default) =>
        _db.Participants.AnyAsync(p => p.Id == participantId && p.EventId == eventId && p.NeedsVisa && p.IsActive, ct);

    public Task<VisaApplication?> GetAsync(int eventId, int participantId, CancellationToken ct = default) =>
        _db.VisaApplications.AsNoTracking()
            .FirstOrDefaultAsync(v => v.ParticipantId == participantId && v.EventId == eventId, ct);

    /// <summary>The names of the required fields left blank (empty = complete).</summary>
    public static IReadOnlyList<string> MissingRequired(VisaApplication a) =>
        Fields.Where(f => f.Required && string.IsNullOrWhiteSpace(f.Value(a)))
              .Select(f => $"{f.Label} ({f.Section[3..]})")
              .ToList();

    /// <summary>Problems beyond a blank field: lengths, dates, numbers.</summary>
    public static IReadOnlyList<string> Invalid(VisaApplication a, DateOnly today)
    {
        var errors = new List<string>();
        if (S(a.Purpose).Length > VisaFormOptions.LongTextMax) errors.Add($"Purpose of the visit is longer than {VisaFormOptions.LongTextMax} characters.");
        if (S(a.SpecialCircumstances).Length > VisaFormOptions.LongTextMax) errors.Add($"Special circumstances is longer than {VisaFormOptions.LongTextMax} characters.");
        if (S(a.CompanyActivity).Length > VisaFormOptions.LongTextMax) errors.Add($"What does your company do is longer than {VisaFormOptions.LongTextMax} characters.");
        if (a.LengthOfVisitDays is < 1 or > 90) errors.Add("Length of visit must be between 1 and 90 days (a short-stay visa covers at most 90 days).");
        if (a.DateOfBirth is DateOnly dob && dob >= today) errors.Add("Date of birth must be in the past.");
        if (!string.IsNullOrWhiteSpace(a.Email) && !a.Email.Contains('@')) errors.Add("Email address is not valid.");
        if (!string.IsNullOrWhiteSpace(a.CompanyEmail) && !a.CompanyEmail.Contains('@')) errors.Add("Company email address is not valid.");
        if (!string.IsNullOrWhiteSpace(a.Gender) && !VisaFormOptions.Genders.Contains(a.Gender)) errors.Add("Choose a gender from the list.");
        if (!string.IsNullOrWhiteSpace(a.Entries) && !VisaFormOptions.Entries.Contains(a.Entries)) errors.Add("Choose the number of entries from the list.");
        return errors;
    }

    /// <summary>
    /// Save the applicant's answers. Returns the problems (nothing saved) or an empty list (saved).
    /// The organizer-owned <see cref="VisaApplication.ApplicationId"/> is never touched here.
    /// </summary>
    public async Task<IReadOnlyList<string>> SubmitAsync(int eventId, int participantId, VisaApplication input, CancellationToken ct = default)
    {
        if (!await MaySeeFormAsync(eventId, participantId, ct))
            return new[] { "The visa form is not open for you. Contact the organizers if you need a visa." };

        var now = _clock.GetUtcNow();
        input.StayPaidBy = VisaFormOptions.StayPaidByOrganizer;   // fixed answers (operator 2026-10-28)
        input.HostGuarantees = VisaFormOptions.HostGuarantees;
        var problems = MissingRequired(input).Select(m => $"Please fill in: {m}")
            .Concat(Invalid(input, DateOnly.FromDateTime(now.UtcDateTime))).ToList();
        if (problems.Count > 0) return problems;

        var row = await _db.VisaApplications
            .FirstOrDefaultAsync(v => v.ParticipantId == participantId && v.EventId == eventId, ct);
        if (row is null)
        {
            row = new VisaApplication { EventId = eventId, ParticipantId = participantId };
            _db.VisaApplications.Add(row);
        }
        // §1383 — first send vs a later edit, decided BEFORE the answers are overwritten.
        var firstSubmit = row.SubmittedAt is null;
        var before = firstSubmit ? null : Fields.Select(f => f.Value(row)).ToArray();
        CopyAnswers(input, row);
        row.SubmittedAt ??= now;
        row.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        await SyncTaskAsync(eventId, participantId, ct);

        // §1383 — the organizers' mailbox hears about it: always on the first send; on a later save only
        // when an answer changed (a re-save of the same answers is not news), naming the changed fields.
        if (_notifier is not null)
        {
            var changed = ChangedFields(before, row);
            if (firstSubmit || changed.Count > 0)
                await _notifier.NotifyAsync(eventId, participantId, firstSubmit ? Array.Empty<string>() : changed, ct);
        }
        return Array.Empty<string>();
    }

    /// <summary>
    /// §1383 — the labels (with their section) of the fields whose value differs from <paramref name="before"/>
    /// (the <see cref="Fields"/> values in order). Empty when <paramref name="before"/> is null.
    /// </summary>
    public static IReadOnlyList<string> ChangedFields(IReadOnlyList<string>? before, VisaApplication after)
    {
        if (before is null) return Array.Empty<string>();
        var changed = new List<string>();
        for (var i = 0; i < Fields.Count && i < before.Count; i++)
            if (!string.Equals(before[i], Fields[i].Value(after), StringComparison.Ordinal))
                changed.Add($"{Fields[i].Label} ({Fields[i].Section[3..]})");
        return changed;
    }

    // --- The task ("Fill in your visa application") ------------------------------------------------

    /// <summary>SourceKey prefix of the visa task (operator 2026-10-28: <i>"add a visa task to their task list"</i>).</summary>
    public const string TaskKeyPrefix = "visa-form";

    public static string TaskSourceKeyFor(int participantId) => $"{TaskKeyPrefix}:{participantId}";

    /// <summary>
    /// Bring every participant's visa task in line with the flag and the form. Flagged ⇒ the task exists
    /// (Done once they have submitted); flag removed ⇒ an open task is retired (closed, never deleted).
    /// 🔒 No due date, on purpose: the operator chose <i>"no deadline"</i>, and a dated task is what the
    /// reminder job mails about. Idempotent; returns how many rows changed.
    /// </summary>
    public async Task<int> SyncTasksAsync(int eventId, CancellationToken ct = default)
    {
        var flagged = await _db.Participants
            .Where(p => p.EventId == eventId && p.NeedsVisa && p.IsActive)
            .Select(p => p.Id).ToListAsync(ct);
        var withTask = await _db.Tasks
            .Where(t => t.EventId == eventId && t.SourceKey != null && t.SourceKey.StartsWith(TaskKeyPrefix + ":")
                        && t.AssignedParticipantId != null)
            .Select(t => t.AssignedParticipantId!.Value).ToListAsync(ct);
        var changed = 0;
        foreach (var id in flagged.Union(withTask).Distinct())
            changed += await SyncTaskAsync(eventId, id, ct);
        return changed;
    }

    /// <summary>The one-person form of <see cref="SyncTasksAsync"/> (a page load, a submit, a flag change).</summary>
    public async Task<int> SyncTaskAsync(int eventId, int participantId, CancellationToken ct = default)
    {
        var person = await _db.Participants
            .Where(p => p.Id == participantId && p.EventId == eventId)
            .Select(p => new { p.NeedsVisa, p.IsActive }).FirstOrDefaultAsync(ct);
        if (person is null) return 0;

        var key = TaskSourceKeyFor(participantId);
        var task = await _db.Tasks.FirstOrDefaultAsync(
            t => t.EventId == eventId && t.AssignedParticipantId == participantId && t.SourceKey == key, ct);
        var submitted = await _db.VisaApplications.AnyAsync(
            v => v.EventId == eventId && v.ParticipantId == participantId && v.SubmittedAt != null, ct);
        var now = _clock.GetUtcNow();

        if (!person.NeedsVisa || !person.IsActive)
        {
            // Retire, never delete; a task already done by the person stays done.
            if (task is null || task.State == TaskState.Done) return 0;
            task.State = TaskState.Done;
            task.ClosedReason = TaskClosedReason.RetiredFromCatalog;
            task.CompletedAt ??= now;
            await _db.SaveChangesAsync(ct);
            return 1;
        }

        if (task is null)
        {
            _db.Tasks.Add(new ParticipantTask
            {
                EventId = eventId,
                AssignedParticipantId = participantId,
                Title = "Fill in your visa application",
                Description = "We need a few details to invite you to Denmark for your visa. Open **Visa application** "
                              + "in the menu and fill in the form. Sending the form marks this done. When the "
                              + "organizers have registered your invitation, your application ID appears at the top "
                              + "of the form.",
                DueDate = null,
                IsMandatory = true,
                State = submitted ? TaskState.Done : TaskState.Open,
                CompletedAt = submitted ? now : null,
                SourceKey = key,
                CreatedAt = now,
            });
            await _db.SaveChangesAsync(ct);
            return 1;
        }

        var wantDone = submitted;
        if (wantDone && task.State != TaskState.Done)
        {
            task.State = TaskState.Done;
            task.CompletedAt ??= now;
            task.ClosedReason = null;
        }
        else if (!wantDone && (task.State == TaskState.Done))
        {
            // Re-flagged after a retirement, or the answers are gone: open again.
            task.State = TaskState.Open;
            task.CompletedAt = null;
            task.ClosedReason = null;
        }
        else if (wantDone && task.ClosedReason is not null)
        {
            task.ClosedReason = null;
        }
        else return 0;

        await _db.SaveChangesAsync(ct);
        return 1;
    }

    private static string? T(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static void CopyAnswers(VisaApplication from, VisaApplication to)
    {
        to.FirstNames = T(from.FirstNames); to.LastName = T(from.LastName); to.DateOfBirth = from.DateOfBirth;
        to.Gender = T(from.Gender); to.Nationality = T(from.Nationality); to.HomeStreet = T(from.HomeStreet);
        to.HomePostalCode = T(from.HomePostalCode); to.HomeCity = T(from.HomeCity); to.HomeCountry = T(from.HomeCountry);
        to.Phone = T(from.Phone); to.Email = T(from.Email)?.ToLowerInvariant();
        to.CompanyName = T(from.CompanyName); to.CompanyStreet = T(from.CompanyStreet); to.CompanyPostalCode = T(from.CompanyPostalCode);
        to.CompanyCity = T(from.CompanyCity); to.CompanyCountry = T(from.CompanyCountry); to.CompanyPhone = T(from.CompanyPhone);
        to.CompanyEmail = T(from.CompanyEmail)?.ToLowerInvariant(); to.CompanyWebsite = T(from.CompanyWebsite);
        to.DepartmentAndRole = T(from.DepartmentAndRole);
        to.VisitedBefore = from.VisitedBefore; to.CompanyActivity = T(from.CompanyActivity); to.ContactOrigin = T(from.ContactOrigin);
        to.RelationshipDuration = T(from.RelationshipDuration); to.FinancialScope = T(from.FinancialScope);
        to.ArrivalDate = from.ArrivalDate; to.LengthOfVisitDays = from.LengthOfVisitDays; to.Entries = T(from.Entries);
        to.Purpose = T(from.Purpose); to.SpecialCircumstances = T(from.SpecialCircumstances);
        to.TravelPaidBy = T(from.TravelPaidBy); to.StayPaidBy = T(from.StayPaidBy); to.Accommodation = T(from.Accommodation);
        to.HostGuarantees = from.HostGuarantees;
    }

    /// <summary>
    /// The "where will you stay" choices: this edition's hotels as "Name, Address" — the text stored and
    /// exported, since the authorities need the address (operator 2026-10-28: <i>"it should show the hotels
    /// registered in CEH as dropdown and show the name + address once selected"</i>).
    /// </summary>
    public async Task<IReadOnlyList<(string Name, string? Address, string Value)>> HotelChoicesAsync(int eventId, CancellationToken ct = default) =>
        (await _db.Hotels.AsNoTracking().Where(h => h.EventId == eventId).OrderBy(h => h.Name)
            .Select(h => new { h.Name, h.Address }).ToListAsync(ct))
        .Select(h => (h.Name, h.Address, HotelValue(h.Name, h.Address)))
        .ToList();

    public static string HotelValue(string name, string? address) =>
        string.IsNullOrWhiteSpace(address) ? name.Trim() : $"{name.Trim()}, {address.Trim()}";

    /// <summary>The hotel an organizer placed this person in, as a <see cref="HotelValue"/> (null when none).</summary>
    public Task<string?> AssignedHotelValueAsync(int eventId, int participantId, CancellationToken ct = default) =>
        _db.Participants.AsNoTracking()
            .Where(p => p.Id == participantId && p.EventId == eventId && p.Hotel != null)
            .Select(p => p.Hotel!.Address == null || p.Hotel.Address == "" ? p.Hotel.Name : p.Hotel.Name + ", " + p.Hotel.Address)
            .FirstOrDefaultAsync(ct);

    /// <summary>Everyone flagged, plus anyone unflagged who already answered (so nothing goes missing).</summary>
    public async Task<IReadOnlyList<VisaRow>> ListAsync(int eventId, CancellationToken ct = default)
    {
        var apps = await _db.VisaApplications.AsNoTracking().Where(v => v.EventId == eventId).ToListAsync(ct);
        var answered = apps.Select(a => a.ParticipantId).ToHashSet();
        var people = await _db.Participants.AsNoTracking()
            .Where(p => p.EventId == eventId && (p.NeedsVisa || answered.Contains(p.Id)))
            .Select(p => new { p.Id, p.FullName, p.Email, p.Role, p.NeedsVisa })
            .ToListAsync(ct);
        var byPerson = apps.ToDictionary(a => a.ParticipantId);
        return people
            .Select(p => new VisaRow(p.Id, p.FullName, p.Email, p.Role, p.NeedsVisa, byPerson.GetValueOrDefault(p.Id)))
            .OrderBy(r => r.Submitted).ThenBy(r => r.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Active speakers + volunteers not yet flagged — the organizer page's "add a person" list.</summary>
    public async Task<IReadOnlyList<(int Id, string Label)>> CandidatesAsync(int eventId, CancellationToken ct = default) =>
        (await _db.Participants.AsNoTracking()
            .Where(p => p.EventId == eventId && p.IsActive && !p.NeedsVisa
                        && (p.Role == ParticipantRole.Speaker || p.Role == ParticipantRole.Volunteer))
            .OrderBy(p => p.FullName)
            .Select(p => new { p.Id, p.FullName, p.Email, p.Role })
            .ToListAsync(ct))
        .Select(p => (p.Id, $"{p.FullName} ({p.Role}, {p.Email})"))
        .ToList();

    public async Task<bool> SetNeedsVisaAsync(int eventId, int participantId, bool needsVisa, CancellationToken ct = default)
    {
        var p = await _db.Participants.FirstOrDefaultAsync(x => x.Id == participantId && x.EventId == eventId, ct);
        if (p is null) return false;
        p.NeedsVisa = needsVisa;
        await _db.SaveChangesAsync(ct);
        await SyncTaskAsync(eventId, participantId, ct);
        return true;
    }

    /// <summary>Store (or clear, with blank) the nyidanmark.dk case number for one applicant.</summary>
    public async Task<bool> SetApplicationIdAsync(int eventId, int participantId, string? applicationId, CancellationToken ct = default)
    {
        if (!await _db.Participants.AnyAsync(p => p.Id == participantId && p.EventId == eventId, ct)) return false;
        var row = await _db.VisaApplications
            .FirstOrDefaultAsync(v => v.ParticipantId == participantId && v.EventId == eventId, ct);
        if (row is null)
        {
            row = new VisaApplication { EventId = eventId, ParticipantId = participantId };
            _db.VisaApplications.Add(row);
        }
        row.ApplicationId = T(applicationId);
        row.ApplicationIdSetAt = row.ApplicationId is null ? null : _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>The export as CSV — one row per person, every <see cref="Fields"/> entry a column.</summary>
    public static string BuildCsv(IEnumerable<VisaRow> rows)
    {
        var sb = new StringBuilder();
        var head = new[] { "Participant", "Participant email", "Role", "Status", "Submitted (UTC)", "Application ID" }
            .Concat(Fields.Select(f => $"{f.Section[3..]}: {f.Label}"));
        sb.Append(string.Join(",", head.Select(Csv))).Append("\r\n");
        foreach (var r in rows)
        {
            var a = r.Application ?? new VisaApplication();
            var cells = new[]
            {
                r.FullName, r.Email, r.Role.ToString(), r.Submitted ? "Submitted" : "Not submitted",
                a.SubmittedAt?.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? string.Empty,
                a.ApplicationId ?? string.Empty,
            }.Concat(Fields.Select(f => r.Application is null ? string.Empty : f.Value(a)));
            sb.Append(string.Join(",", cells.Select(Csv))).Append("\r\n");
        }
        return sb.ToString();
    }

    private static string Csv(string s) =>
        s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
