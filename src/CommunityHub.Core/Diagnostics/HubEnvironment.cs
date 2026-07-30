namespace CommunityHub.Core.Diagnostics;

/// <summary>
/// §702 — WHICH EDITION-ENVIRONMENT is this process? The answer that goes in an alert subject
/// (<c>[DEV]</c> / <c>[PROD]</c>) and on the Settings page's environment badge (§703).
///
/// <para>Operator 2026-07-29: <i>"can we include [DEV] and [PROD] in all alert mails so i can see
/// which env is sending the alert / impacted"</i>. §701 is why: three engine-failure mails arrived
/// and there was no way to tell dev from prod without querying both databases.</para>
///
/// 🔒 <b>DO NOT REPLACE THIS WITH <c>IHostEnvironment.EnvironmentName</c>.</b> Verified against the
/// live apps 2026-07-29: <c>ASPNETCORE_ENVIRONMENT</c> is <c>Production</c> on the <b>DEV</b> web app
/// as well as prod, and the two Functions apps set no environment variable at all (so they default
/// to <c>Production</c> too). Using it would stamp <c>[PROD]</c> on every DEV alert — confidently
/// wrong about the single thing the tag exists to answer, which is worse than no tag.
///
/// <para>Resolution order, most explicit first:</para>
/// <list type="number">
///   <item><c>Hub:EnvironmentLabel</c> (app setting <c>Hub__EnvironmentLabel</c>) — the intended
///         source. Set it on BOTH web slots: app settings swap with the slot, so a
///         production-slot-only setting is lost at the next swap.</item>
///   <item><c>WEBSITE_SITE_NAME</c> — the App Service built-in, present on web AND Functions hosts
///         (<c>eldk27hub-fn-devz237e</c> / <c>eldk27hub-web-prodpdrq</c>). A safety net so the tag
///         is right even before the setting is deployed.</item>
///   <item><see cref="Unknown"/> — when neither resolves (a local run, or a host we do not
///         recognise). 🔑 It reports <c>UNKNOWN</c> rather than guessing: an alert that names the
///         wrong environment sends someone to the wrong place, and §701 shows that is expensive.</item>
/// </list>
/// </summary>
public sealed class HubEnvironment
{
    public const string Dev = "DEV";
    public const string Prod = "PROD";
    public const string Unknown = "UNKNOWN";

    /// <summary>The resolved label — <c>DEV</c>, <c>PROD</c>, or <c>UNKNOWN</c>.</summary>
    public string Label { get; }

    /// <summary>True only when the environment is positively known (not <see cref="Unknown"/>).</summary>
    public bool IsKnown => !string.Equals(Label, Unknown, StringComparison.Ordinal);

    /// <summary>The bracketed subject prefix, e.g. <c>[DEV]</c>. Always non-empty.</summary>
    public string SubjectTag => $"[{Label}]";

    public HubEnvironment(string? configuredLabel, string? siteName)
    {
        Label = Resolve(configuredLabel, siteName);
    }

    /// <summary>
    /// Pure resolution, so the rules are testable without a host. <paramref name="configuredLabel"/>
    /// is <c>Hub:EnvironmentLabel</c>; <paramref name="siteName"/> is <c>WEBSITE_SITE_NAME</c>.
    /// </summary>
    public static string Resolve(string? configuredLabel, string? siteName)
    {
        var configured = (configuredLabel ?? string.Empty).Trim();
        if (configured.Length > 0)
        {
            // Accept the obvious spellings an operator might type, but never invent a third value:
            // anything unrecognised is surfaced UPPERCASED as-is rather than silently mapped, so a
            // typo shows up in the subject line instead of masquerading as a real environment.
            if (LooksProd(configured)) return Prod;
            if (LooksDev(configured)) return Dev;
            return configured.ToUpperInvariant();
        }

        var site = (siteName ?? string.Empty).Trim();
        if (site.Length > 0)
        {
            // 🔒 Order matters: check PROD first. "prod" cannot appear inside a dev site name, but
            // matching "dev" first would mislabel any future site whose name contained both.
            if (LooksProd(site)) return Prod;
            if (LooksDev(site)) return Dev;
        }

        return Unknown;
    }

    private static bool LooksProd(string s) =>
        s.Contains("prod", StringComparison.OrdinalIgnoreCase);

    private static bool LooksDev(string s) =>
        s.Contains("dev", StringComparison.OrdinalIgnoreCase);
}
