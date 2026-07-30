using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Config;

/// <summary>
/// §682 — the one place a config-authored task body is checked against the storage it has
/// to fit into.
///
/// <para>
/// WHY THIS EXISTS: on 2026-07-29 a 2135-character task description (the §675 DSV shipment
/// copy) was seeded into a 2000-character column. EF threw on <c>SaveChangesAsync</c>, and
/// because the task re-seed shares a transaction-scope with the WooCommerce ORDER pull, the
/// whole background job died — sponsor order sync included. One paragraph of prose took out
/// an entire integration, and the failure surfaced only as a truncation error naming a
/// column, several layers away from the config file that actually caused it.
/// </para>
///
/// <para>
/// 🔒 NOTHING HERE TRUNCATES. Task bodies carry shipping addresses, prices and box-marking
/// strings; a body silently cut mid-sentence would render half an address with full
/// confidence, which is worse than a missing task. The contract is: refuse the ONE bad row,
/// report it, and let the rest of the run finish. Same principle as §555 — "could not" must
/// never be dressed up as fact.
/// </para>
/// </summary>
public static class TaskFieldGuard
{
    /// <summary>
    /// True when this title/description pair fits <see cref="ParticipantTask"/>'s columns.
    /// When false, <paramref name="reason"/> names the offending field with both numbers, so
    /// the log line points at the config edit that fixes it rather than at EF.
    /// </summary>
    /// <remarks>
    /// Always call this on the FINAL, placeholder-substituted strings. A body that fits in
    /// config can still overflow once a long SharePoint upload URL or company name is spliced
    /// in, so the authored length is not the length that matters.
    /// </remarks>
    public static bool Fits(string? title, string? description, out string reason)
    {
        if (title is { Length: > ParticipantTask.TitleMaxLength })
        {
            reason = $"title is {title.Length} characters, limit {ParticipantTask.TitleMaxLength}";
            return false;
        }

        if (description is { Length: > ParticipantTask.DescriptionMaxLength })
        {
            reason = $"description is {description.Length} characters, limit {ParticipantTask.DescriptionMaxLength}";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}
