using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// Loads + saves the per-edition <see cref="SoMeSettings"/> (REQUIREMENTS §19).
/// One row per edition, upserted. The settings hold only operator config
/// (on/off, company-page URL/id, pre-alert organizer, notification array) — the
/// LinkedIn OAuth access token is a SECRET and never lives here (it is read from
/// Key Vault by the live publisher).
/// </summary>
public sealed class SoMeSettingsService
{
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public SoMeSettingsService(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>Get the edition's settings, or null if never saved.</summary>
    public Task<SoMeSettings?> GetAsync(int eventId, CancellationToken ct = default) =>
        _db.SoMeSettings.FirstOrDefaultAsync(s => s.EventId == eventId, ct);

    /// <summary>
    /// Get the edition's settings or a fresh (unsaved) default — so a page never
    /// has to null-check. The default is disabled (nothing posts).
    /// </summary>
    public async Task<SoMeSettings> GetOrDefaultAsync(int eventId, CancellationToken ct = default) =>
        await GetAsync(eventId, ct) ?? new SoMeSettings { EventId = eventId };

    /// <summary>
    /// Upsert the edition's settings. Returns the persisted row. The access token
    /// is intentionally NOT a parameter — it is never stored in this row.
    /// </summary>
    public async Task<SoMeSettings> SaveAsync(
        int eventId,
        bool enabled,
        string? companyPageUrlOrOrgId,
        string? speakerPreAlertOrganizerEmail,
        string? notificationEmails,
        bool notifyOnPublish,
        string? byEmail,
        CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var row = await GetAsync(eventId, ct);
        if (row is null)
        {
            row = new SoMeSettings { EventId = eventId, CreatedAt = now };
            _db.SoMeSettings.Add(row);
        }

        row.Enabled = enabled;
        row.CompanyPageUrlOrOrgId = Trim(companyPageUrlOrOrgId);
        row.SpeakerPreAlertOrganizerEmail = Trim(speakerPreAlertOrganizerEmail);
        row.NotificationEmails = Trim(notificationEmails);
        row.NotifyOnPublish = notifyOnPublish;
        row.UpdatedAt = now;
        row.LastUpdatedByEmail = Trim(byEmail);

        await _db.SaveChangesAsync(ct);
        return row;
    }

    private static string? Trim(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
