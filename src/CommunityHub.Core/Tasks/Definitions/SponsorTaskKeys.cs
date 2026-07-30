namespace CommunityHub.Core.Tasks.Definitions;

/// <summary>
/// The per-company <c>ParticipantTask.SourceKey</c> for a sponsor task — <b>the same shape the
/// JSON-driven pull has always produced</b>.
/// </summary>
/// <remarks>
/// <para>🔒 <b>THIS IS THE ANSWER TO §684.21's KEY-CHANGE TRAP, and it is the cheap one.</b> §684.21
/// warned that giving every definition a new stable <c>Key</c> would change
/// <c>SourceKey</c> — the dedup identity for BOTH task upserts and reminder occasions — and that
/// changing it carelessly re-notifies every participant about tasks they have held for months. That
/// is §664.1 exactly: a ledger key mismatch producing a wrong notification decision on live
/// data.</para>
///
/// <para>§684.23 then relaxed it to <i>"purge-and-re-seed is the default; do the key mapping only if
/// it stays trivial"</i>. <b>It turned out to be trivial in the best possible way: there is no
/// mapping at all.</b> A migrated definition keeps its TITLE, and the key is derived from the title,
/// so the key a registry-seeded row gets is byte-identical to the one the JSON-seeded row already
/// has. The row is UPDATED in place, not replaced:</para>
/// <list type="bullet">
///   <item><description>completion state survives — nobody's finished task reopens;</description></item>
///   <item><description>the <c>SentReminders</c> dedup ledger keys still match, so nothing re-fires;</description></item>
///   <item><description>the orphan prune below the upsert sees the key in its desired set and leaves the row alone.</description></item>
/// </list>
///
/// <para>⚠️ <b>The corollary is a rule, not a nicety: DO NOT RENAME A TASK WHILE MIGRATING IT.</b>
/// Change the title in the same commit as the migration and the key changes with it — the old row is
/// pruned as an orphan, a new one is created, and the reminder ledger treats it as never-chased.
/// Migrate first, rename afterwards as its own visible change.
/// <c>SponsorTaskKeyIdentityTests</c> pins this.</para>
/// </remarks>
public static class SponsorTaskKeys
{
    /// <summary>The pull-managed key prefix for one company's sponsor tasks.</summary>
    public static string PrefixFor(string companyId) => $"sponsor:{companyId}:";

    /// <summary>The <c>SourceKey</c> for one sponsor task of one company.</summary>
    public static string For(string companyId, string title) =>
        $"{PrefixFor(companyId)}{Slug(title)}";

    /// <summary>The key prefix for one speaker's deadline tasks.</summary>
    /// <remarks>
    /// 🔒 <b>`speakerdl:` — the shape <c>SpeakerDeadlineSeeder</c> has always written.</b> Phase 2
    /// migrates speaker deadlines into the registry, and the same reasoning as the sponsor pass
    /// applies in full: the key is the dedup identity for BOTH the task upsert and the reminder
    /// occasion, so preserving it byte-for-byte is what makes the migration produce ZERO new mail
    /// (§684.21/§684.23). Change the prefix or the slug and every speaker is re-notified about
    /// deadlines they have had for months.
    /// </remarks>
    public static string SpeakerPrefixFor(int speakerId) => $"speakerdl:{speakerId}:";

    /// <summary>The <c>SourceKey</c> for one speaker deadline task.</summary>
    public static string ForSpeaker(int speakerId, string title) =>
        $"{SpeakerPrefixFor(speakerId)}{Slug(title)}";

    /// <summary>
    /// The title slug. 🔒 <b>Byte-for-byte the algorithm <c>SponsorOrderPullService.Slug</c> has
    /// always used</b> — lower-cased, letters/digits/spaces only, spaces to hyphens, truncated at 60.
    /// It is duplicated NOWHERE: the pull service now calls this. Two copies of a key derivation is
    /// how the two would silently diverge, and a diverged key is a re-notification storm.
    /// </summary>
    public static string Slug(string title)
    {
        var chars = title.ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c == ' ')
            .ToArray();
        var slug = new string(chars).Replace(' ', '-');
        return slug.Length > 60 ? slug[..60] : slug;
    }
}
