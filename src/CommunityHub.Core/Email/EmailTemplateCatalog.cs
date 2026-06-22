namespace CommunityHub.Core.Email;

/// <summary>
/// Static metadata for the shipped email templates (REQUIREMENTS §25h): a friendly title
/// and the FeatureCatalog key whose released ring governs each template. The editor uses
/// this to show, per template, a human name + the per-template ring (and to let the
/// organizer dial that ring). Templates with no specific feature map to the
/// <c>outbound-email</c> transport (kill-switch only). Keys are the on-disk file names
/// without ".html".
/// </summary>
public static class EmailTemplateCatalog
{
    /// <summary>templateKey → (Title, FeatureKey). Many templates share one feature.</summary>
    public static readonly IReadOnlyDictionary<string, (string Title, string FeatureKey)> Map =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["welcome"]                    = ("Welcome", "welcome-email"),
            ["welcome-login"]              = ("Welcome (auto-login)", "welcome-email"),
            ["welcome-speaker"]            = ("Welcome: speaker", "welcome-email"),
            ["welcome-volunteer"]          = ("Welcome: volunteer", "welcome-email"),
            ["welcome-sponsor"]            = ("Welcome: sponsor", "welcome-email"),
            ["welcome-media"]              = ("Welcome: media", "welcome-email"),
            ["welcome-eventpartner"]       = ("Welcome: event partner", "welcome-email"),
            ["masterclass-selection-invite"] = ("Master Class selection invite", "masterclass-invites"),
            ["masterclass-confirmed"]      = ("Master Class confirmed seat", "masterclass-invites"),
            ["invitation"]                 = ("Invitation", "invitation-email"),
            ["broadcast"]                  = ("Broadcast", "broadcast-email"),
            ["task-deadline-reminder"]     = ("Task deadline reminder", "reminder-jobs"),
            ["task-manual-reminder"]       = ("Manual task reminder", "reminder-jobs"),
            ["speaker-question-digest"]    = ("Speaker Q&A digest", "digest-emails"),
            ["onboarding-getting-started"] = ("Onboarding: getting started", "welcome-email"),
            ["onboarding-your-tasks"]      = ("Onboarding: your tasks", "welcome-email"),
            ["onboarding-step-reset"]      = ("Onboarding step reset", "onboarding-step-reset"),
            ["travel-reimbursement-paid"]  = ("Travel reimbursement paid", "travel-reimbursement-email"),
            ["group-photo-invite"]         = ("Group-photo invite", "group-photo-invites"),
            ["app-game-gift-reminder"]     = ("App-game gift reminder", "sponsor-reminders"),
            ["volunteer-help-raised"]      = ("Volunteer help raised", "outbound-email"),
            ["sponsor-leads-digest"]       = ("Sponsor leads digest", "sponsor-leads"),
            ["attendee-missing-booking"]   = ("Attendee: missing booking", "attendee-welcome"),
            ["attendee-missing-ticket"]    = ("Attendee: missing ticket", "attendee-welcome"),
            ["attendee-duplicate-booking"] = ("Attendee: duplicate booking", "attendee-welcome"),
        };

    /// <summary>The feature key governing a template's ring (outbound-email transport when unmapped).</summary>
    public static string FeatureKeyFor(string templateKey) =>
        Map.TryGetValue(templateKey, out var v) ? v.FeatureKey : "outbound-email";

    /// <summary>A friendly title for a template (the key itself when unmapped).</summary>
    public static string TitleFor(string templateKey) =>
        Map.TryGetValue(templateKey, out var v) ? v.Title : templateKey;
}
