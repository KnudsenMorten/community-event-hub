namespace CommunityHub.Core.Integrations.Sessions;

/// <summary>
/// 🔴 §1246 — turn a Zoho speaker-create rejection into a line the ORGANIZER CAN FORWARD TO THE
/// SPEAKER.
/// </summary>
/// <remarks>
/// <para>Operator 2026-09-20, on four failed pushes: *"send correct error message so we can escalate
/// to speakers"*. What the mail said was Zoho's own text:</para>
///
/// <code>
/// chris@chris-brumm.com — Zoho speaker create failed: HTTP 400 —
///   {"status_code":"400","message":"Sorry, an unexpected error has occurred. Please try again later."}
/// </code>
///
/// <para>⚠️ That message is worse than useless: it names no field, suggests the fault is transient
/// ("please try again later") when it is permanent, and cannot be forwarded to anybody. Three of the
/// four failures carried it, and all three were a malformed social URL.</para>
///
/// <para>🔑 <b>The diagnosis comes from the DATA WE SENT, not from parsing Zoho's prose.</b> Zoho's
/// wording is not a contract and will drift; the speaker's own LinkedIn and Twitter values are
/// right here and can be checked against the constraints we have actually observed. So this reads
/// the profile, not the error — and when it recognises nothing it says so plainly rather than
/// inventing a cause.</para>
///
/// <para>✅ <b>Observed on PROD 2026-09-20</b> — every one of these produced a create failure, and
/// every speaker WITHOUT one succeeded (no counter-examples in a 77-speaker push):</para>
/// <list type="bullet">
///   <item>a query string on the profile URL — <c>twitter.com/cbrhh?s=09</c>,
///         <c>twitter.com/paulismama?lang=de</c></item>
///   <item>an <c>@</c> in the path — <c>twitter.com/@ErlendRushfeldt</c></item>
///   <item>a LinkedIn URL over 125 characters (Zoho names this one itself) — 127, entirely because
///         of an <c>?utm_source=share…</c> suffix pasted from the mobile app</item>
/// </list>
/// </remarks>
public static class SpeakerPushDiagnostics
{
    /// <summary>Zoho's stated LinkedIn limit — it is the one constraint it reports by name.</summary>
    public const int LinkedInMaxLength = 125;

    /// <summary>
    /// A speaker-facing explanation of why a create was rejected, or null when nothing in the
    /// profile matches a known cause.
    /// </summary>
    /// <remarks>
    /// 🔒 Returns NULL rather than a guess. An explanation that is confidently wrong is worse than
    /// Zoho's own text, because the organizer would forward it to a speaker and ask them to change
    /// something that is fine.
    /// </remarks>
    public static string? Explain(string? linkedIn, string? twitter)
    {
        var problems = new List<string>();

        Check("LinkedIn", linkedIn);
        Check("X / Twitter", twitter);

        if (problems.Count == 0) return null;

        return "Likely cause — " + string.Join("  ALSO: ", problems);

        void Check(string label, string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            var value = url.Trim();

            // Length first: it is the one Zoho names, and it is usually CAUSED by the query string,
            // so reporting the length alone would send the speaker hunting for a shorter profile
            // when all they need to do is delete the share-tracking suffix.
            var tooLong = label.StartsWith("LinkedIn", StringComparison.Ordinal)
                          && value.Length > LinkedInMaxLength;

            var queryAt = value.IndexOf('?');
            var hasQuery = queryAt >= 0;
            // An '@' AFTER the host — "twitter.com/@name". Zoho rejects it; the handle alone works.
            var slashAt = value.IndexOf("//", StringComparison.Ordinal);
            var pathStart = slashAt >= 0 ? value.IndexOf('/', slashAt + 2) : -1;
            var hasAtInPath = pathStart >= 0 && value.IndexOf('@', pathStart) >= 0;

            if (!tooLong && !hasQuery && !hasAtInPath) return;

            var cleaned = Clean(value);
            var what = new List<string>();
            if (hasQuery) what.Add("it carries a tracking query string (the \"?\" and everything after it)");
            if (hasAtInPath) what.Add("it has an \"@\" in the address, which belongs only in the handle");
            if (tooLong) what.Add($"it is {value.Length} characters and the limit is {LinkedInMaxLength}");

            problems.Add(
                $"the {label} address is not accepted: {string.Join(", and ", what)}. "
                + $"Ask the speaker to change it in Sessionize from\n      {value}\n  to\n      {cleaned}");
        }
    }

    /// <summary>
    /// The address with the share-tracking query string and a stray path <c>@</c> removed — i.e. the
    /// plain profile URL. Pure and side-effect free; used for the SUGGESTION in the message.
    /// </summary>
    /// <remarks>
    /// ⚠️ Deliberately NOT applied automatically to what we push. Sending Zoho a value the hub does
    /// not itself hold would make CEH and Zoho disagree for ever, and the hourly difference mail
    /// would chase a mismatch nobody can resolve. The speaker owns their profile; this only tells
    /// them what to type.
    /// </remarks>
    public static string Clean(string url) =>
        // 🔒 §1250 — delegates to the ONE normaliser the importer uses, so the URL this message
        // tells a speaker to type is byte-identical to the one the hub would have produced itself.
        // Two implementations of "clean this URL" would drift, and the drift would show up as
        // advice that does not match what the system does.
        SocialUrlNormalizer.Clean(url) ?? (url ?? string.Empty).Trim();
}
