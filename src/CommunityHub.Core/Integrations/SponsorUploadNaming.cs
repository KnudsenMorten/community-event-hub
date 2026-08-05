namespace CommunityHub.Uploads;

/// <summary>
/// §768.14 — THE naming contract for a versioned sponsor upload: <c>{sponsor}-{kind}-{version}.{ext}</c>.
/// One place builds a name, one place reads one back, and the graphics matcher uses the same code as
/// the four writers.
/// </summary>
/// <remarks>
/// <para>🔒 <b>Why this is a type and not a format string.</b> Before §768.14 the writers built
/// <c>{Prefix}{Sponsor}_v{N}.{ext}</c> in four hand-copied places, and
/// <c>SoMeBundleBuildService.ResolveNewestSponsorLogo</c> took the name apart again with a
/// <i>separate</i>, heuristic reader — it guessed the upload prefix as "everything up to the first
/// underscore". A writer and a reader that agree only by inspection are exactly the failure this
/// audit exists to remove: when they part company the matcher finds NOTHING, no exception is thrown,
/// and every sponsor graphic silently stops building. Build and parse now share this file, so a
/// change to one is a change to both.</para>
///
/// <para>⚠️ <b>Parsing runs right-to-left, and must stay that way.</b> A sponsor slug may itself
/// contain hyphens ("Acme-Corp" ⇒ <c>acme-corp-logo-web-3.png</c>), so the version and the kind
/// infix are peeled off the END; whatever remains is the sponsor. Reading left-to-right would split
/// that name at the first hyphen and match nothing.</para>
///
/// <para>The version token is a bare integer: the old <c>_v3</c> form is gone (operator 2026-08-02,
/// §768.8). <see cref="ParseVersion"/> alone still reads it, because historical
/// <c>SponsorUploadAudit</c> rows carry old names and are DISPLAYED — nothing is ever written or
/// matched under the legacy shape.</para>
/// </remarks>
public static class SponsorUploadNaming
{
    /// <summary>The filename infix per upload kind — the middle of <c>{sponsor}-{infix}-{version}</c>.</summary>
    /// <remarks>
    /// <c>"zoho"</c> is deliberately absent: §6.7 merged the lead-system logo into
    /// <c>Logo/Web</c>, so one file now serves both. A stray "zoho" upload must fail to resolve
    /// rather than land somewhere plausible.
    /// </remarks>
    private static readonly Dictionary<string, string> Infixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["some"] = "logo-web",
        ["print"] = "logo-print",
        ["wall"] = "exhibitor-wall",
    };

    /// <summary>The filename infix for a kind, or null when the kind is not a versioned upload.</summary>
    public static string? InfixFor(string? kind) =>
        kind is not null && Infixes.TryGetValue(kind, out var infix) ? infix : null;

    /// <summary>
    /// Lowercase, hyphen-joined form of a sponsor name — the identity a stored file carries.
    /// </summary>
    /// <remarks>
    /// 🔒 The SAME function must slug the company name on the write side and the file name on the
    /// read side. Before §768.14 the writer kept the name's spaces and casing while the matcher
    /// slugged it, and the two happened to agree only for single-word sponsors.
    /// </remarks>
    public static string Slug(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var chars = value.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        return string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// The file name for a sponsor upload, e.g. <c>acme-corp-logo-web-2.png</c>. The extension is
    /// taken as given (with or without its dot) and lowercased.
    /// </summary>
    /// <exception cref="ArgumentException">The kind is not a versioned upload kind.</exception>
    public static string Build(string kind, string? sponsorName, int version, string ext)
    {
        var infix = InfixFor(kind)
            ?? throw new ArgumentException($"'{kind}' is not a versioned sponsor upload kind.", nameof(kind));

        var sponsor = Slug(sponsorName);
        if (sponsor.Length == 0) sponsor = "sponsor";
        if (version < 1) version = 1;

        var suffix = (ext ?? string.Empty).Trim().ToLowerInvariant();
        if (suffix.Length > 0 && suffix[0] != '.') suffix = "." + suffix;

        return $"{sponsor}-{infix}-{version}{suffix}";
    }

    /// <summary>
    /// Read a stored name back: true when <paramref name="fileName"/> is a file of this
    /// <paramref name="kind"/>, yielding the sponsor slug and version it carries.
    /// </summary>
    public static bool TryParse(string? fileName, string kind, out string sponsorSlug, out int version)
    {
        sponsorSlug = string.Empty;
        version = 0;

        var infix = InfixFor(kind);
        if (infix is null || string.IsNullOrWhiteSpace(fileName)) return false;

        var stem = Path.GetFileNameWithoutExtension(fileName);

        // Right-to-left: the trailing "-{digits}" is the version.
        var dash = stem.LastIndexOf('-');
        if (dash <= 0) return false;
        if (!int.TryParse(stem[(dash + 1)..], out version) || version < 1) return false;

        // What precedes it must end with "-{infix}"; everything before THAT is the sponsor.
        var head = stem[..dash];
        var tail = "-" + infix;
        if (!head.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) return false;

        sponsorSlug = head[..^tail.Length];
        return sponsorSlug.Length > 0;
    }

    /// <summary>
    /// True when the file is this kind's upload for this sponsor — the matcher's question, asked in
    /// one place so the write side and the read side cannot drift apart.
    /// </summary>
    public static bool Matches(string? fileName, string kind, string? sponsorName, out int version) =>
        TryParse(fileName, kind, out var slug, out version)
        && slug.Equals(Slug(sponsorName), StringComparison.OrdinalIgnoreCase)
        && slug.Length > 0;

    /// <summary>
    /// The version a stored name carries, defaulting to 1.
    /// </summary>
    /// <remarks>
    /// ⚠️ DISPLAY ONLY, and deliberately tolerant of the retired <c>_v{N}</c> form — audit rows
    /// written before §768.14 still carry those names and are shown back to the sponsor. Never use
    /// it to decide where to write or what to match: use <see cref="TryParse"/>, which accepts the
    /// current contract and nothing else.
    /// </remarks>
    public static int ParseVersion(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return 1;
        var stem = Path.GetFileNameWithoutExtension(fileName);

        // Legacy "_v{N}" first — an old name may also end in a hyphen-digit run by coincidence.
        var v = stem.LastIndexOf("_v", StringComparison.OrdinalIgnoreCase);
        if (v >= 0 && int.TryParse(stem[(v + 2)..], out var legacy) && legacy > 0) return legacy;

        var dash = stem.LastIndexOf('-');
        return dash > 0 && int.TryParse(stem[(dash + 1)..], out var current) && current > 0 ? current : 1;
    }
}
