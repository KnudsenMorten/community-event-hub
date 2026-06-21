using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Reminders;

/// <summary>Outcome of one sponsor-company welcome resend.</summary>
public sealed record SponsorWelcomeResult(
    string SponsorCompanyId,
    int CoordinatorsResolved,
    int Sent,
    int Skipped);

/// <summary>
/// The sponsor-facing welcome/intro send + reset, honouring the universal
/// sponsor-email audience rule (REQUIREMENTS §7c): the welcome goes to every
/// EVENT-COORDINATOR contact of a sponsor company (signer-only excluded,
/// both-roles included, all coordinators), resolved by the shared
/// <see cref="SponsorRecipientResolver"/>. Wraps the existing idempotent
/// <see cref="WelcomeEmailService"/> (one welcome per participant, tracked in
/// <c>SentReminder</c> <c>ReminderType="welcome"</c>) so a resend only fires
/// after the welcome flag is reset.
///
/// Two organizer operations:
///   * <see cref="SendForCompanyAsync"/> — (re)send the welcome to a company's
///     coordinators (idempotent: already-welcomed coordinators are skipped, so
///     call <see cref="ResetForCompanyAsync"/> first for a true resend).
///   * <see cref="ResetForCompanyAsync"/> — delete the matching <c>welcome</c>
///     <c>SentReminder</c> rows so the next send actually goes out.
/// </summary>
public sealed class SponsorWelcomeEmailService
{
    private const string WelcomeReminderType = "welcome";

    private readonly CommunityHubDbContext _db;
    private readonly SponsorRecipientResolver _recipients;
    private readonly WelcomeEmailService _welcome;

    public SponsorWelcomeEmailService(
        CommunityHubDbContext db,
        SponsorRecipientResolver recipients,
        WelcomeEmailService welcome)
    {
        _db = db;
        _recipients = recipients;
        _welcome = welcome;
    }

    /// <summary>
    /// Send the welcome to all coordinator contacts of one sponsor company.
    /// Idempotent per coordinator (the underlying <see cref="WelcomeEmailService"/>
    /// no-ops if that participant has already been welcomed) — so to force a
    /// resend, reset first via <see cref="ResetForCompanyAsync"/>. Returns counts
    /// of resolved coordinators, actual sends, and idempotent skips.
    /// </summary>
    public async Task<SponsorWelcomeResult> SendForCompanyAsync(
        int eventId, string sponsorCompanyId, CancellationToken ct = default)
    {
        var coordinators = await _recipients.ResolveAsync(eventId, sponsorCompanyId, ct);
        int sent = 0, skipped = 0;
        foreach (var c in coordinators)
        {
            var didSend = await _welcome.SendWelcomeAsync(c.ParticipantId, ct);
            if (didSend) sent++; else skipped++;
        }
        return new SponsorWelcomeResult(
            sponsorCompanyId, coordinators.Count, sent, skipped);
    }

    /// <summary>
    /// Send the welcome to every sponsor company's coordinators in the edition
    /// (each company resolved independently). Returns the aggregated per-company
    /// results. Used by the organizer "resend to all sponsors" action.
    /// </summary>
    public async Task<IReadOnlyList<SponsorWelcomeResult>> SendForAllSponsorsAsync(
        int eventId, CancellationToken ct = default)
    {
        var companyIds = await DistinctSponsorCompanyIdsAsync(eventId, ct);
        var results = new List<SponsorWelcomeResult>();
        foreach (var companyId in companyIds)
        {
            results.Add(await SendForCompanyAsync(eventId, companyId, ct));
        }
        return results;
    }

    /// <summary>
    /// Clear the <c>welcome</c> <see cref="SentReminder"/> rows for a sponsor
    /// company's coordinator contacts so a subsequent
    /// <see cref="SendForCompanyAsync"/> actually re-sends (the welcome send is
    /// otherwise once-ever per participant). Only the coordinators' welcome rows
    /// are removed — no other reminder type is touched. Returns the number of
    /// ledger rows deleted.
    /// </summary>
    public async Task<int> ResetForCompanyAsync(
        int eventId, string sponsorCompanyId, CancellationToken ct = default)
    {
        var coordinators = await _recipients.ResolveAsync(eventId, sponsorCompanyId, ct);
        if (coordinators.Count == 0) return 0;

        var emails = coordinators.Select(c => c.Email).ToList();
        var rows = await _db.SentReminders
            .Where(s => s.EventId == eventId
                        && s.ReminderType == WelcomeReminderType
                        && emails.Contains(s.RecipientEmail))
            .ToListAsync(ct);
        if (rows.Count == 0) return 0;

        _db.SentReminders.RemoveRange(rows);
        await _db.SaveChangesAsync(ct);
        return rows.Count;
    }

    /// <summary>Clear the welcome ledger for ALL sponsor companies' coordinators in the edition.</summary>
    public async Task<int> ResetForAllSponsorsAsync(
        int eventId, CancellationToken ct = default)
    {
        var companyIds = await DistinctSponsorCompanyIdsAsync(eventId, ct);
        int total = 0;
        foreach (var companyId in companyIds)
        {
            total += await ResetForCompanyAsync(eventId, companyId, ct);
        }
        return total;
    }

    private async Task<List<string>> DistinctSponsorCompanyIdsAsync(
        int eventId, CancellationToken ct) =>
        await _db.Participants
            .Where(p => p.EventId == eventId
                        && p.Role == ParticipantRole.Sponsor
                        && p.SponsorCompanyId != null
                        && p.SponsorCompanyId != "")
            .Select(p => p.SponsorCompanyId!)
            .Distinct()
            .ToListAsync(ct);
}
