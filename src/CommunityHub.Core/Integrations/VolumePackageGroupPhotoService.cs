using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations;

/// <summary>Which days a company's people are actually at the event — the slot hint.</summary>
/// <param name="TwoDay">Attendees holding a 2-day ticket: present on the pre-day AND the main day.</param>
/// <param name="OneDay">Attendees holding a 1-day ticket: present on the MAIN DAY only.</param>
public sealed record GroupPhotoDayHint(int TwoDay, int OneDay)
{
    /// <summary>A pre-day slot is only possible if somebody is actually there on the pre-day.</summary>
    public bool PreDayPossible => TwoDay > 0;

    /// <summary>⚠️ Everyone is present on the main day, whichever ticket they hold.</summary>
    public bool MainDayPossible => TwoDay + OneDay > 0;

    /// <summary>
    /// The honest sentence for the organizer. 🔑 It SUGGESTS and never assigns: the operator's rule
    /// was <i>"slots spread over pre-day and main day, decided by which ticket types the company
    /// bought"</i>, and which company goes where is a scheduling decision with a photographer, a
    /// room and a running order behind it — none of which this knows.
    /// </summary>
    public string Advice =>
        TwoDay + OneDay == 0 ? "No active attendees found for this company."
        : !PreDayPossible
            ? $"Main day only — all {OneDay} of their people hold 1-day tickets and are not there on the pre-day."
        : OneDay == 0
            ? $"Either day — all {TwoDay} of their people hold 2-day tickets."
        : $"Pre-day would reach {TwoDay} of them; the main day reaches all {TwoDay + OneDay}.";
}

/// <summary>
/// §1077 stage 4 — the GROUP PHOTO for a volume-package company: one registration per company,
/// which day it can be taken, and the calendar file the coordinator forwards THEMSELVES.
/// </summary>
/// <remarks>
/// <para>🔑 <b>This does not invent a group-photo model — there already is one.</b>
/// <see cref="GroupPhotoRegistration"/> (§25f, June) holds the slot, the length, the location and
/// the stable-UID calendar invite, and <c>/Organizer/GroupPhotos</c> already schedules and sends it.
/// Stage 4 LINKS the volume-package entity to that row rather than adding a second slot, a second
/// contact and a second invite to drift apart. ⚠️ §1076 is the lesson being applied: the last time
/// something looked missing it was missing BY DECISION, and building the obvious thing would have
/// resurrected a model that had been deliberately retired.</para>
///
/// <para>🔒 <b>CEH must never mail the attendees about the photo</b> (his compliance rule — some
/// will not want to be photographed). So the coordinator gets a calendar FILE to forward inside
/// their own company, and nothing here writes to anyone but that one coordinator.</para>
/// </remarks>
public sealed class VolumePackageGroupPhotoService
{
    private readonly CommunityHubDbContext _db;
    private readonly VolumePackageQualificationService _qualify;
    private readonly TimeProvider _clock;

    public VolumePackageGroupPhotoService(
        CommunityHubDbContext db, VolumePackageQualificationService qualify, TimeProvider? clock = null)
    {
        _db = db;
        _qualify = qualify;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The registration for one volume-package company, or null.</summary>
    public Task<GroupPhotoRegistration?> ForCompanyAsync(int companyId, CancellationToken ct = default) =>
        _db.GroupPhotoRegistrations
            .FirstOrDefaultAsync(r => r.VolumePackageCompanyId == companyId, ct);

    /// <summary>
    /// Create the registration for a company that approved the group photo, or refresh the one that
    /// exists. Returns null when the company has not approved it — a photo nobody asked for is not
    /// scheduled.
    /// </summary>
    /// <remarks>
    /// 🔑 <b>The ticket count is now DERIVED.</b> The June entity says of it: <i>"entered manually by
    /// the organizer (there is no automated ticket-volume feed)"</i>. Stage 1 built exactly that
    /// feed, so the number is taken from the qualification rather than retyped — one fewer figure to
    /// keep in step, and one fewer way for the photo list and the volume-package page to disagree.
    ///
    /// <para>🔒 The SLOT is never touched here. Refreshing contact details must not silently move a
    /// time a photographer and a company have already been told about.</para>
    /// </remarks>
    public async Task<GroupPhotoRegistration?> SyncFromCompanyAsync(
        int companyId, CancellationToken ct = default)
    {
        var company = await _db.VolumePackageCompanies
            .FirstOrDefaultAsync(c => c.Id == companyId, ct);

        if (company is null || !company.ApprovedGroupPhoto || company.DeclinedAt is not null)
            return null;

        var registration = await ForCompanyAsync(companyId, ct);
        if (registration is null)
        {
            registration = new GroupPhotoRegistration
            {
                EventId = company.EventId,
                VolumePackageCompanyId = company.Id,
            };
            _db.GroupPhotoRegistrations.Add(registration);
        }

        registration.CompanyName = company.CustomName;
        registration.TicketCount = company.LastQualifiedCount;

        // The coordinator the COMPANY named in the wizard; the approver only until they name one.
        registration.ContactName =
            company.GroupPhotoContactName ?? company.ApproverName ?? string.Empty;
        registration.ContactEmail =
            company.GroupPhotoContactEmail ?? company.ApproverEmail ?? string.Empty;

        await _db.SaveChangesAsync(ct);
        return registration;
    }

    /// <summary>
    /// Which days this company's people are present, from the live attendee mirror.
    /// </summary>
    /// <remarks>
    /// ⚠️ A 1-day holder is not at the pre-day, so a pre-day slot would photograph an empty space.
    /// This counts the SAME attendee set the qualification counts, so the hint cannot disagree with
    /// the number the organizer is looking at on the same row.
    /// </remarks>
    public async Task<GroupPhotoDayHint> DayHintAsync(int companyId, CancellationToken ct = default)
    {
        var company = await _db.VolumePackageCompanies
            .FirstOrDefaultAsync(c => c.Id == companyId, ct);
        if (company is null) return new GroupPhotoDayHint(0, 0);

        var result = (await _qualify.ComputeAllAsync(company.EventId, ct))
            .FirstOrDefault(r => r.CompanyId == companyId);
        if (result is null || result.Emails.Count == 0) return new GroupPhotoDayHint(0, 0);

        var emails = result.Emails.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var tickets = await _db.Attendees
            .AsNoTracking()
            .Where(a => a.EventId == company.EventId && a.MirrorState == MirrorState.Active)
            .Select(a => new { a.Email, a.TicketStatus })
            .ToListAsync(ct);

        var mine = tickets.Where(t => emails.Contains(t.Email)).ToList();
        return new GroupPhotoDayHint(
            TwoDay: mine.Count(t => t.TicketStatus == TicketStatus.TwoDay),
            OneDay: mine.Count(t => t.TicketStatus != TicketStatus.TwoDay));
    }

    /// <summary>
    /// The calendar file for the coordinator to forward internally, or null when there is no slot.
    /// </summary>
    /// <remarks>
    /// 🔒 <b>Same stable UID as the organizer's invite</b> (<c>group-photo-{eventId}-{id}</c>), so a
    /// slot that moves UPDATES the entry a coordinator already forwarded rather than adding a second
    /// one to every colleague's calendar.
    /// ⚠️ <b>No ATTENDEE line</b>, unlike the organizer's invite: this file is meant to be passed on
    /// to people we are not writing to, and naming one recipient as the attendee would turn every
    /// forwarded copy into an accept/decline on somebody else's behalf.
    /// </remarks>
    public async Task<string?> CoordinatorIcsAsync(int companyId, CancellationToken ct = default)
    {
        var registration = await ForCompanyAsync(companyId, ct);
        if (registration?.ScheduledAtUtc is null) return null;

        var ev = await _db.Events
            .Where(e => e.Id == registration.EventId)
            .Select(e => new { e.DisplayName, e.VenueName, e.CommunityName })
            .FirstOrDefaultAsync(ct);

        var start = registration.ScheduledAtUtc.Value;
        return IcsCalendarBuilder.BuildVEvent(
            uid: $"group-photo-{registration.EventId}-{registration.Id}@communityhub",
            summary: $"Group photo - {registration.CompanyName} ({ev?.DisplayName})",
            // 🔴 The operator's own invitation wording (§1077.8). It travels with a file that gets
            // FORWARDED to colleagues we never write to, so the three permissions — optional for
            // each individual, we do not use the photo, it is the company's own — have to be inside
            // the invitation rather than in a covering mail only one person read.
            description: string.IsNullOrWhiteSpace(registration.Notes)
                ? GroupPhotoInviteText.Build(ev?.DisplayName, ev?.CommunityName)
                : registration.Notes,
            location: string.IsNullOrWhiteSpace(registration.Location)
                ? ev?.VenueName ?? string.Empty
                : registration.Location!,
            startUtc: start,
            endUtc: start.AddMinutes(registration.DurationMinutes),
            organizerEmail: null,
            organizerName: ev?.DisplayName,
            attendeeEmail: null,
            attendeeName: null);
    }
}
