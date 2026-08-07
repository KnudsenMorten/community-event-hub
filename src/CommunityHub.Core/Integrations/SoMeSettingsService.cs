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
        CancellationToken ct = default,
        // §824.19 — the three values every post template ends with.
        // 🔒 Behind an explicit <paramref name="updatePostCopy"/> flag rather than "null means leave
        // alone": every OTHER field on this method is unconditional, so a caller that omits these
        // would otherwise be silently deciding whether they survive. An existing caller that knows
        // nothing about post copy must not be able to WIPE the tag block by saving the LinkedIn
        // wiring — and that is exactly what would happen with plain optional parameters.
        string? eventSystemUrl = null,
        string? eventTags = null,
        string? organizerCredits = null,
        // §885 — the call-to-action phrase catalog, under the same ownership flag and for the same
        // reason: a caller that knows nothing about post copy must not wipe it by omission.
        string? actionCatalog = null,
        // §888.2 — the city+country line, under the same ownership rule.
        string? eventVenueCityCountry = null,
        // §927 — the session-title exclusion list, under the same ownership flag.
        string? excludedSessionTitlePatterns = null,
        bool updatePostCopy = false,
        // §918 — auto-approval, under the SAME ownership rule as the post copy above and for the
        // same reason: a caller that knows nothing about it must not be able to switch it on (or
        // off) by omission. Null means "leave as it is".
        bool? autoApproveEnabled = null,
        int? autoApproveLeadDays = null,
        // §928 — the two announcement date gates, under an ownership FLAG rather than "null means
        // leave alone", because for these two null is a real value: it is how a gate is CLEARED.
        //
        // 🔴 The flag is the guard §925.1 asked for by name. `SpeakerAnnouncementFrom` (7 Sep) is
        // *"load-bearing, do not clear it"* — it is the only thing keeping eight tracks from being
        // announced today naming 1–2 speakers out of an expected hundred. With plain optional
        // parameters, ANY caller that saved the LinkedIn wiring and knew nothing about announcement
        // dates would wipe it by omission, and the damage would be invisible until the campaign
        // published early.
        DateOnly? speakerAnnouncementFrom = null,
        DateOnly? masterClassAnnouncementFrom = null,
        // §925.2 — the CfS close date rides the same flag: it is the third value that decides WHEN a
        // category may be announced, and clearing it by omission would quietly restore the §925.1
        // defect (every track reading as settled while the intake had not started).
        DateOnly? callForSpeakersClosesOn = null,
        bool updateAnnouncementWindows = false)
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

        if (updatePostCopy)
        {
            // Blanking a field here IS meaningful — it is how he removes the tag block — so these
            // are assigned exactly as given once the caller has said it owns them.
            row.EventSystemUrl = Trim(eventSystemUrl);
            row.EventTags = Trim(eventTags);
            row.OrganizerCredits = Trim(organizerCredits);
            // 🔒 NOT Trim()ed to a single line — this one is deliberately multi-line (one phrase per
            // line). Trimming only the ends preserves the list while dropping stray whitespace.
            row.ActionCatalog = string.IsNullOrWhiteSpace(actionCatalog) ? null : actionCatalog.Trim();
            row.EventVenueCityCountry = Trim(eventVenueCityCountry);
            // §927 — multi-line like the action catalog, and for the same reason: one pattern per
            // line. Blank means exclude nothing.
            row.ExcludedSessionTitlePatterns =
                string.IsNullOrWhiteSpace(excludedSessionTitlePatterns)
                    ? null : excludedSessionTitlePatterns.Trim();
        }

        if (updateAnnouncementWindows)
        {
            // §928 — assigned exactly as given, null included: clearing a gate is a decision he is
            // allowed to make, once he has said he owns the fields.
            row.SpeakerAnnouncementFrom = speakerAnnouncementFrom;
            row.MasterClassAnnouncementFrom = masterClassAnnouncementFrom;
            row.CallForSpeakersClosesOn = callForSpeakersClosesOn;
        }

        // §918 — only when the caller says so (see the parameter note).
        if (autoApproveEnabled is { } auto) row.AutoApproveEnabled = auto;
        // 🔒 Floored at 1: a zero lead would mean "approve posts that are due now", which is exactly
        // the §889.1 burst the guard exists to prevent. Refused here as well as in the service, so
        // the stored value can never express it.
        if (autoApproveLeadDays is { } lead) row.AutoApproveLeadDays = Math.Max(1, lead);

        row.UpdatedAt = now;
        row.LastUpdatedByEmail = Trim(byEmail);

        await _db.SaveChangesAsync(ct);
        return row;
    }

    private static string? Trim(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
