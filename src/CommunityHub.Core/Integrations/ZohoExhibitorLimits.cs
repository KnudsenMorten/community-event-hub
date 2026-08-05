namespace CommunityHub.Core.Integrations;

/// <summary>
/// §802.3 — the field limits Zoho Backstage enforces on an exhibitor, in ONE place.
/// </summary>
/// <remarks>
/// <para>🔑 <b>MEASURED against the live v3 API on 2026-08-04 (§801.2), not guessed.</b> A binary
/// probe on a real exhibitor, restored afterwards:</para>
///
/// <list type="bullet">
///   <item><c>company_short_description</c>: 80 → <b>200</b>, 81 → <b>400</b>.</item>
///   <item><c>company_overview</c>: 1000 → <b>200</b>, 1024 → <b>400</b>.</item>
/// </list>
///
/// <para>Zoho names the failure itself — <c>{"status_code":"400","message":"`shortDescription` is too
/// long"}</c> — and the whole update is rejected, not just the offending field. ⚠️ That is why this
/// matters: one over-long short description means the website, the overview and everything else in
/// the same PUT never land either.</para>
///
/// <para>🔴 <b>Why a shared constant and not a number per file.</b> The limit lived in THREE places
/// (the sponsor wizard step, the Company Details page, and a hard-coded "80" in that page's
/// character counter) and now needs a fourth — the sync. Four copies of a number that a third party
/// controls is three chances to be wrong the day Zoho changes it.</para>
///
/// <para>⚠️ <b>Admin By Request's live short description is 78 characters</b> — two from the cap. The
/// next slightly longer edit would have 400'd the entire exhibitor update.</para>
/// </remarks>
public static class ZohoExhibitorLimits
{
    /// <summary>Zoho <c>company_short_description</c> — 80 characters, measured.</summary>
    public const int ShortDescription = 80;

    /// <summary>Zoho <c>company_overview</c> — 1000 characters, measured (1024 rejected).</summary>
    public const int Overview = 1000;

    /// <summary>
    /// CEH's own limit for the social-media branding text. ⚠️ Not a Zoho field — it never goes to
    /// Backstage, so it is not part of the measurement above.
    /// </summary>
    public const int SocialBrandingText = 600;

    /// <summary>CEH's own limit for a stored URL.</summary>
    public const int Url = 400;

    /// <summary>
    /// §802.2 — cut a value down to a Zoho limit for the PUT.
    /// </summary>
    /// <remarks>
    /// <para>🔒 <b>The SYNC truncates; the FORM refuses.</b> They are deliberately different answers
    /// to the same number: a person typing has words they can choose to cut (§802.1), while the sync
    /// is dealing with a value already in the database — from before the rule, from an import, or set
    /// by an organizer — and its only alternatives are a truncated value or a 400 that also throws
    /// away every other field in the same call.</para>
    ///
    /// <para>⚠️ <b>Never silently.</b> Callers must report a truncation: a cap nobody is told about is
    /// how the public event site ends up carrying a sentence that stops mid-word and nobody wrote.</para>
    /// </remarks>
    /// <returns>The value, cut to <paramref name="max"/>; null/blank passes through untouched.</returns>
    public static string? Cap(string? value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max) return value;
        return value[..max];
    }

    /// <summary>True when this value would be rejected by Zoho for being too long.</summary>
    public static bool IsTooLong(string? value, int max) => (value?.Length ?? 0) > max;

    /// <summary>
    /// §802.1 — the refusal a PERSON reads, with the numbers in it.
    /// </summary>
    /// <remarks>
    /// Operator 2026-08-04: *"tell the exhibitor the issue including number of chars and block them
    /// from saving until fixed"*. *"must be 80 characters or fewer"* leaves them counting by hand;
    /// **"84 characters — the limit is 80. Remove 4."** tells them exactly what to do.
    /// </remarks>
    public static string TooLongMessage(string label, string? value, int max)
    {
        var length = value?.Length ?? 0;
        var over = length - max;
        return $"{label} is {length} characters — the limit is {max}. "
             + $"Remove {over} character{(over == 1 ? string.Empty : "s")}. "
             + "This is Zoho Backstage's own limit, and it rejects the whole update when it is exceeded.";
    }
}
