using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations;

/// <summary>
/// An ORDER-level row from the Zoho Backstage v3 /orders pull (REQUIREMENTS §125):
/// the order's stable id, buyer/billing, Zoho status string, source created time and
/// the full raw JSON. One order owns many tickets/attendees. This is the order half of
/// the authoritative one-way Zoho→CEH mirror (the attendee half is
/// <see cref="BackstageAttendee"/>); both come from the SAME v3 pull.
/// </summary>
public sealed record BackstageOrder(
    string OrderId,
    string? BuyerName,
    string? BuyerEmail,
    string? CompanyName,
    string? Country,
    string? CountryCode,
    string? City,
    string? Postcode,
    string? TaxId,
    string? OrderStatus,
    DateTimeOffset? SourceCreatedAt,
    string? RawJson);

/// <summary>
/// A fully-enriched Backstage attendee: the ticket (stable id), contact details +
/// ALL custom fields, and the order's company/country/tax. One row per ticket.
/// </summary>
public sealed record BackstageAttendee(
    string TicketId,
    string OrderId,
    string Email,
    string FirstName,
    string LastName,
    string TicketClassName,
    bool Attending,
    string? CompanyName,
    string? JobTitle,
    string? Phone,
    string? Country,
    string? CountryCode,
    string? City,
    string? Postcode,
    string? TaxId,
    string? CustomFieldsJson,
    // The ticket's created_time as Backstage returns it ("MM/dd/yyyy HH:mm:ss"), for the
    // telemetry sales-over-time graph. Null when absent.
    string? CreatedTimeRaw = null,
    // §326as — Zoho's RAW per-ticket status_string. Confirmed values on a real event
    // (ELDK26, 1204 tickets): "attending" and "not_attending". Carried verbatim rather
    // than collapsed into <see cref="Attending"/> because cancellation must act ONLY on
    // the value Zoho actually documents — an unrecognised value must never be read as
    // "cancel this person".
    string? StatusString = null,
    // §447 (operator 2026-07-27) — Zoho's STABLE ticket_class_id, carried alongside the name.
    // The class NAME is a label the operator can rename (and did: "2-day (Pre-day + Main Event)"),
    // so 1-day/2-day classification keys on this id when the edition configures one. Null when a
    // payload supplies none — the name rule then decides, exactly as before.
    string? TicketClassId = null);

/// <summary>
/// One Zoho Backstage AGENDA session (REQUIREMENTS §38e), flattened to the fields the
/// change-detection engine compares: the stable Backstage agenda/session id, the
/// scheduled start/end, and the resolved hall/room NAME. This is the CURRENT-state
/// snapshot pulled from Backstage; CEH stores its own last-known copy on
/// <c>Session.Backstage*</c> and diffs the two.
/// </summary>
public sealed record BackstageSession(
    string SessionId,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    string? Room,
    // The session TITLE, used by the §38e engine to first-populate-match an unlinked
    // CEH session by normalized title (Backstage exposes no CEH/Sessionize external id).
    string? Title = null);

/// <summary>
/// The outcome of a Backstage agenda pull (REQUIREMENTS §38e). <see cref="IsAvailable"/>
/// is false when the live agenda API could not be read — a transport or auth FAILURE observed at
/// call time — so the engine no-ops gracefully instead of treating an empty pull as "everything was
/// deleted". When available, <see cref="Sessions"/> is the current agenda.
/// <para>🗑 §754.5: this used to say the refresh token "lacks the ZohoBackstage.agenda.READ scope".
/// It does not, and never did — the credentials carry every permission CEH needs.</para>
/// </summary>
public sealed record BackstageSessionsResult(
    bool IsAvailable,
    IReadOnlyList<BackstageSession> Sessions,
    string? UnavailableReason = null)
{
    public static BackstageSessionsResult Unavailable(string reason) =>
        new(false, Array.Empty<BackstageSession>(), reason);

    public static BackstageSessionsResult Available(IReadOnlyList<BackstageSession> sessions) =>
        new(true, sessions, null);
}

/// <summary>
/// One Zoho Backstage SPEAKER (REQUIREMENTS §38e/§58), flattened to the fields the §58
/// Zoho→CEH speaker change-detection engine compares: the stable Backstage speaker id, the
/// display name (first + last), the tagline (Zoho <c>designation</c>), the bio (Zoho
/// <c>description</c>), country, and the linkedin/twitter social URLs. This is the
/// CURRENT-state snapshot pulled from Backstage; CEH stores its own last-known copy on
/// <c>SpeakerProfile.Backstage*</c> and diffs the two — exactly as <see cref="BackstageSession"/>
/// backs the §38e SESSION change detection.
/// </summary>
public sealed record BackstageSpeaker(
    string SpeakerId,
    string? Name,
    string? Tagline,
    string? Bio,
    string? Country,
    string? LinkedIn,
    string? Twitter,
    // §623 — added for the CEH↔Zoho gap report. Both ARE returned by the speakers API (verified
    // live 2026-07-29 against the list AND the per-id endpoint).
    string? Company = null,
    string? Skills = null,
    string? Email = null)
{
    /// <summary>
    /// 🔒 §623 / §582 — <b>COUNTRY IS NOT READABLE FROM ZOHO. NEVER DIFF IT.</b>
    /// </summary>
    /// <remarks>
    /// Verified live 2026-07-29 against BOTH the speakers list and the per-id record: the response
    /// carries `id, email, first_name, last_name, status, featured, joined_on, added_on, company,
    /// designation, description, skills, telephone, alternate_telephone, twitter, facebook,
    /// telegram, linkedin, instagram, medium` — and **no `country` at all**. <see cref="Country"/>
    /// is therefore always null on a read, whatever Zoho actually holds.
    ///
    /// <para>Comparing it would report EVERY speaker as missing a country, forever, and no action
    /// could ever close it — the §594 "Tags missing" mail all over again, which the operator
    /// received for tags he had already entered. §582 is the rule: honour the declared limitation,
    /// because a false gap is worse than no gap — he acts on it.</para>
    /// </remarks>
    public static bool CountryIsReadable => false;
}

/// <summary>
/// The outcome of a Backstage speaker pull (REQUIREMENTS §38e/§58). <see cref="IsAvailable"/>
/// is false when the live speakers API could not be read — a transport or auth FAILURE observed at
/// call time — so the engine no-ops gracefully instead of treating an empty pull as "every speaker
/// was deleted". Mirrors <see cref="BackstageSessionsResult"/>.
/// <para>🗑 §754.5: the "missing ZohoBackstage.speaker.READ scope" story was never true — see the
/// note on <see cref="ZohoOptions"/>.</para>
/// </summary>
public sealed record BackstageSpeakersResult(
    bool IsAvailable,
    IReadOnlyList<BackstageSpeaker> Speakers,
    string? UnavailableReason = null)
{
    public static BackstageSpeakersResult Unavailable(string reason) =>
        new(false, Array.Empty<BackstageSpeaker>(), reason);

    public static BackstageSpeakersResult Available(IReadOnlyList<BackstageSpeaker> speakers) =>
        new(true, speakers, null);
}

/// <summary>A Zoho Bookings appointment, flattened.</summary>
public sealed record ZohoAppointment(
    string CustomerEmail,
    string CustomerName,
    string ServiceName,
    string Status,
    string? SummaryUrl);

/// <summary>Zoho integration settings (CONTEXT.md 9z). EU data centre.</summary>
public sealed class ZohoOptions
{
    public const string SectionName = "Zoho";

    public bool Enabled { get; set; }
    public string ApiDomain { get; set; } = "https://www.zohoapis.eu";
    public string TokenEndpoint { get; set; } = "https://accounts.zoho.eu/oauth/v2/token";
    public string BackstagePortalId { get; set; } = string.Empty;
    public string BackstageEventId { get; set; } = string.Empty;
    public string BookingServiceNameRegex { get; set; } = "(?i)master\\s*class";
    // The "2-day" (Master-Class eligibility) definition is NO LONGER a regex here:
    // it was unified into the single MasterClassTicketPolicy (REQUIREMENTS §125). The
    // old TwoDayTicketNameRegex option was removed to stop the three definitions drifting.
    public string MasterClassDate { get; set; } = "2027-02-09";
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;

    // 🗑 §754.5 — `AgendaReadEnabled` and `SpeakerReadEnabled` are DELETED (2026-08-01).
    //
    // 🔒 DO NOT REINTRODUCE THEM, AND DO NOT WRITE ANOTHER COMMENT SAYING A BACKSTAGE READ SCOPE IS
    // MISSING. **The Zoho Backstage credentials already carry every permission CEH needs.** They
    // have carried them the whole time. The two flags defaulted FALSE and claimed the token lacked
    // `ZohoBackstage.agenda.READ` / `.speaker.READ`, which was never verified against the live API —
    // and the claim then spread into five other files as settled fact.
    //
    // Operator 2026-08-01: *"this error has bitten me 10 times now due to you dont update this wrong
    // assumption in the docs. the zoho backstage creds have all the needed permissions already."*
    //
    // The evidence it was false was always in this file: `GetLiveSessionMapAsync` and
    // `GetSpeakerIdsByEmailAsync` read `/agendas`, `/sessions?day=N` and `/speakers` UNGATED on the
    // strict path — where a 401 THROWS — and the §301b self-heal has depended on them in production
    // for months. The §585 matrix live-probed all five agenda-family endpoints at 200 against PROD.
    //
    // What still gates these engines is what always should have: the per-edition FEATURE SWITCH
    // (`session-change-alerts` / `speaker-change-alerts`, both off by default) and the sync
    // direction. Those are real controls an organiser can see and reason about. A phantom scope was
    // not.

    /// <summary>
    /// 🔴 §791.4 — push <c>company_social_pages</c> on an exhibitor UPDATE. <b>Defaults OFF, and the
    /// reason is measured, not assumed.</b>
    /// </summary>
    /// <remarks>
    /// <para>§791.3, four controlled calls against PROD 2026-08-04: the v3 exhibitor PUT returns
    /// <b>200 and echoes the field back</b>, and a subsequent GET shows it <b>absent</b> — including
    /// with Zoho's own documented sample key (<c>facebook</c>), and including on a record whose
    /// <c>linkedin</c> was already set. <b>The field is not writable over v3.</b> CEH's payload was
    /// correct the whole time (§791.3), which is why this is a switch and not a bug fix.</para>
    ///
    /// <para>⚠️ Leaving it ON cost three sessions: every pass re-sent it, the log said *"Updated
    /// exhibitor"*, and Zoho kept nothing (§784.13 → §791). The values now reach Backstage as the
    /// §792 hand-entry mail instead — a list somebody can act on beats a sync that reports success
    /// for ever.</para>
    ///
    /// <para>🔒 Flip this to <c>true</c> if Zoho ever fixes the endpoint — one config setting, no
    /// deploy. Do NOT delete the code path: the measurement above is what makes re-testing cheap.</para>
    /// </remarks>
    public bool PushExhibitorSocialPages { get; set; }

    /// <summary>
    /// The PUBLIC Zoho Backstage event-site base URL (REQUIREMENTS §52). The "View
    /// public session page" link on the Speaker hub points at
    /// <c>{BackstagePublicBaseUrl}#/sessions/{BackstageSessionId}</c> when the session
    /// has a Backstage id; else it stays on the internal page. Non-secret config; the
    /// default is this edition's portal. (NavBuilder hardcodes the same host for the
    /// exhibitor dashboard — both should move to per-edition config for the mirror.)
    /// </summary>
    public string BackstagePublicBaseUrl { get; set; } = "https://eldk27.expertslive.dk/";

    // ---- Zoho CRM leads pull (sponsor leads pipeline) -------------------
    // Off by default: enabling requires the refresh token to carry the
    // ZohoCRM.modules.READ scope and the CRM to tag each record with the
    // sponsor company id (custom field named by CrmSponsorCompanyIdField).

    /// <summary>Master switch for the CRM lead pull. Default off.</summary>
    public bool CrmEnabled { get; set; }

    /// <summary>Comma-separated CRM modules to pull (Leads, Contacts, ...).</summary>
    public string CrmModules { get; set; } = "Leads";

    /// <summary>CRM field (API name) holding the sponsor company id each lead belongs to.</summary>
    public string CrmSponsorCompanyIdField { get; set; } = "Sponsor_Company_Id";

    /// <summary>
    /// The §57 stage-2 (CEH→Zoho) SESSION-PUSH session type. Backstage REQUIRES a session
    /// type on create (live-verified 2026-06-25: absent → 400 "you have not entered a session
    /// type") and exposes no list endpoint to enumerate the event's types, so the value is
    /// operator config. The GET shape returns it as e.g. <c>"PRESENTATION"</c>. Default
    /// <c>PRESENTATION</c>; override per edition if the event uses a different type.
    /// </summary>
    public string PushSessionType { get; set; } = "PRESENTATION";

    /// <summary>
    /// Sessionize→Zoho TRACK-NAME map (operator 2026-07-23): the Backstage tracks carry
    /// SHORTER names for the two AI tracks than Sessionize does — everything else matches
    /// by exact (case-insensitive) name. Resolution: exact → this map → CREATE-if-missing
    /// with the mapped (else Sessionize) name. Case-insensitive on the Sessionize label;
    /// overridable/extendable via <c>Zoho__TrackNameMap__&lt;label&gt;</c> app settings.
    /// Edition-config candidate (the labels are edition facts, not code).
    /// </summary>
    public Dictionary<string, string> TrackNameMap { get; set; } =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["AI for Engineers/Developers (Build Your Own AI)"] = "AI for Engineers & Developers",
            ["AI for Makers (Copilot & Agents)"] = "AI for Makers",
        };


    // ---- Zoho Backstage ORDER-CHANGE WEBHOOK (REQUIREMENTS §128) -----------
    // Real-time leg of the authoritative one-way mirror: Backstage POSTs to the
    // ZohoOrderWebhook Function on Event Order / Attendee changes, which runs an
    // INCREMENTAL (single-order) reconcile. The hourly AttendeeBackstageSyncJob
    // remains the drift safety-net for missed webhooks. Read-only — the webhook
    // NEVER writes back to Zoho.

    /// <summary>
    /// Master switch for the order-change webhook RECEIVER (REQUIREMENTS §128). Default
    /// FALSE: the <c>ZohoOrderWebhook</c> Function no-ops (returns 200) until the operator
    /// registers the webhook in Backstage AND sets <c>Zoho:WebhookEnabled=true</c>. The
    /// hourly full reconcile is unaffected and keeps the mirror correct on its own.
    /// </summary>
    public bool WebhookEnabled { get; set; }

    /// <summary>
    /// Shared secret that authorizes an inbound webhook call (REQUIREMENTS §128). Zoho
    /// Backstage webhooks expose no custom-header/HMAC mechanism, so the secret is carried
    /// in the registered Endpoint URL as a query-string token (default param
    /// <see cref="WebhookSecretQueryParam"/>) — e.g.
    /// <c>https://…/api/zoho/order-webhook?token=SECRET</c> — and/or the
    /// <see cref="WebhookSecretHeader"/> header (for tests/proxies). NEVER hard-coded:
    /// supplied via the <c>Zoho__WebhookSecret</c> app setting (Key-Vault-backed). Empty ⇒
    /// every call is rejected (401) so an unconfigured endpoint can't be driven.
    /// </summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>Query-string parameter name carrying <see cref="WebhookSecret"/> (default <c>token</c>).</summary>
    public string WebhookSecretQueryParam { get; set; } = "token";

    /// <summary>Optional request header carrying <see cref="WebhookSecret"/> (default <c>X-Webhook-Secret</c>).</summary>
    public string WebhookSecretHeader { get; set; } = "X-Webhook-Secret";
}

/// <summary>One Zoho CRM record, flattened for the sponsor-leads sync.</summary>
public sealed record ZohoCrmLead(
    string ZohoRecordId,
    string Module,
    string SponsorCompanyId,
    string FirstName,
    string LastName,
    string FullName,
    string Email,
    string Phone,
    string Company,
    string JobTitle,
    string City,
    string Country,
    string Source,
    string Notes,
    DateTimeOffset CreatedTime);

/// <summary>
/// Zoho client (CONTEXT.md 9z) - the C# port of the source PowerShell
/// reconciliation scripts. Refreshes an OAuth access token, fetches Backstage
/// ticket orders, and fetches Bookings Master Class appointments. Read-only.
///
/// The PowerShell scripts remain the behavioural specification: same EU OAuth
/// endpoint, same multipart fetchappointment call, same paging.
/// </summary>
public sealed class ZohoClient
{
    /// <summary>
    /// §609 — the exact error a write returns when the §340-H guard blocked it. A NAMED constant so
    /// callers can recognise "the environment refused this" without matching prose.
    /// </summary>
    /// <remarks>
    /// 🔒 THIS IS NOT A FAILURE — it is the guard working. The push jobs must count it as SKIPPED,
    /// never Failed. Operator 2026-07-28: a DEV deploy mailed him a red *"Stage-2 CEH→Zoho session
    /// push: failures"* listing 11 sessions, every line reading "External writes are disabled for
    /// this host". DEV is SUPPOSED to refuse (*"we cannot have external writes from DEV !!!!"*), so
    /// alerting on it trains him to ignore the alert that matters — and he received it repeatedly.
    /// </remarks>
    public const string ExternalWritesDisabledError =
        "External writes are disabled for this host (§340-H).";

    private readonly HttpClient _http;
    private readonly ZohoOptions _options;
    private readonly ILogger<ZohoClient>? _log;

    // §340-H: the environment-level external-write switch. Optional so every existing
    // construction (and every test) keeps compiling with unchanged behaviour; null ⇒
    // allow, exactly as before. DI always supplies the real guard.
    private readonly IExternalWriteGuard _writes;

    // §524 — the operator must be TOLD when the Zoho credential dies, by the service itself:
    // "the service should fix this, otherwise i need to have an email with this kind of error
    // immediately. dont do any tricks and try manually. service/background job must handle it".
    // Optional + last so every existing construction and test keeps compiling; null ⇒ log only.
    private readonly Email.EngineAlertSender? _alerts;

    // §525 — the SHARED access-token cache. Must be a singleton and must outlive this client:
    // ZohoClient is created per-resolution via AddHttpClient, so an instance field would cache
    // nothing and every call would mint a fresh token, which is the bug. Optional so tests and
    // legacy constructions keep the old uncached behaviour.
    private readonly ZohoAccessTokenCache? _tokenCache;

    // 🔒 §783.12b — the ENVIRONMENT posture, used to block Zoho entirely on a non-writing host.
    // Deliberately the raw OPTIONS and not IExternalWriteGuard: the guard folds in the per-edition
    // organizer override, and no override may put DEV back onto the shared token budget.
    // Optional + last so every existing construction and test keeps compiling; null ⇒ allowed.
    private readonly ExternalWriteOptions? _externalOptions;

    public ZohoClient(
        HttpClient http, ZohoOptions options, ILogger<ZohoClient>? log = null,
        IExternalWriteGuard? writes = null,
        Email.EngineAlertSender? alerts = null,
        ZohoAccessTokenCache? tokenCache = null,
        ExternalWriteOptions? externalOptions = null)
    {
        _http = http;
        _options = options;
        _log = log;
        _writes = writes ?? new AllowAllExternalWrites();
        _alerts = alerts;
        _tokenCache = tokenCache;
        _externalOptions = externalOptions;
    }

    /// <summary>
    /// §783.12b — may this HOST talk to Zoho at all? False only when the environment explicitly
    /// says it may not reach third parties.
    /// </summary>
    /// <remarks>
    /// <para>🔒 <b>Why this is checked at the TOKEN and not per call.</b> Every one of this client's
    /// ~21 outbound calls needs an access token first, so the exchange is the one chokepoint that
    /// covers all of them. Gating each call site instead would mean 21 chances to forget one — and
    /// forgetting one is exactly how this happened (the write guard covers 3 of the 21).</para>
    ///
    /// <para>⚠️ <b>Why READS had to be gated, when §340-H deliberately did not gate them.</b> That
    /// decision rested on <i>"a read changes nothing outside CEH"</i>. For a METERED API the premise
    /// is false: DEV's reads spend the same 10-requests-per-10-minutes token budget as PROD, on the
    /// SAME refresh token (verified byte-identical 2026-08-03), and exhausting it takes PROD down
    /// with 401s that read exactly like a revoked credential. DEV cannot corrupt Zoho's DATA — which
    /// is what the write guard was reasoned about — but it can and did exhaust Zoho's QUOTA.</para>
    ///
    /// <para>🔑 Null options ⇒ ALLOWED, so tests and legacy constructions are unaffected. Only an
    /// explicit <c>AllowExternalWrites=false</c> blocks — which both DEV hosts already carry from
    /// bicep, so there is no new app setting to deploy and nothing for a config copy to get wrong.</para>
    /// </remarks>
    public bool HostMayReachZoho => _externalOptions?.AllowExternalWrites != false;

    /// <summary>
    /// §340-H — may this host perform the Zoho WRITE <paramref name="operation"/>?
    ///
    /// <para>Applied per write METHOD rather than as an HTTP handler blocking non-GET,
    /// because two Zoho POSTs are actually READS — the OAuth token refresh and the
    /// Bookings appointments query — and blocking those would break DEV's read paths
    /// instead of protecting anything. Reads are never gated: pulling data INTO CEH
    /// changes nothing outside it.</para>
    ///
    /// <para>Every mutating method below calls this, and <c>ExternalWriteGuardTests</c>
    /// pins the set so a new write method cannot silently escape the switch.</para>
    /// </summary>
    private Task<bool> MayWriteAsync(string operation, CancellationToken ct) =>
        _writes.AllowAsync("Zoho Backstage", operation, ct);

    /// <summary>One Backstage exhibitor — its system id + company name (for matching by
    /// name) + its currently assigned <c>booth_id</c> (null/empty = no booth selected),
    /// so the provision fix-up can SKIP an assignment Zoho already has (§302: "if values
    /// are already set, then skip").</summary>
    public sealed record BackstageExhibitor(string Id, string CompanyName, string? BoothId = null);

    /// <summary>
    /// List every exhibitor in the configured Backstage event (id + company_name +
    /// booth_id) so a sponsor can be matched by company name and its live booth
    /// compared. Scope: <c>ZohoBackstage.exhibitor.READ</c>. Returns empty on
    /// auth/HTTP failure.
    /// </summary>
    public async Task<IReadOnlyList<BackstageExhibitor>> GetExhibitorsAsync(
        string accessToken, CancellationToken ct = default)
    {
        var list = new List<BackstageExhibitor>();
        await foreach (var el in PageV3Async("exhibitors", "exhibitors", accessToken, ct))
        {
            var id = el.TryGetProperty("id", out var i) ? i.GetString() : null;
            var name = el.TryGetProperty("company_name", out var n) ? n.GetString() : null;
            var boothId = NullIf(GetString(el, "booth_id"));
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                list.Add(new BackstageExhibitor(id!, name!, boothId));
        }
        return list;
    }

    /// <summary>
    /// PUT updated profile fields onto a Backstage exhibitor. Only non-null
    /// fields are sent. Scope: <c>ZohoBackstage.exhibitor.UPDATE</c>. Returns
    /// whether the update succeeded.
    /// </summary>
    public async Task<bool> UpdateExhibitorAsync(
        string accessToken, string exhibitorId,
        string? companyOverview, string? companyShortDescription,
        CancellationToken ct = default,
        string? companyName = null, string? contactFirstName = null, string? contactLastName = null,
        string? websiteUrl = null, string? linkedInUrl = null, string? twitterUrl = null,
        string? contactEmail = null, string? contactMobile = null)
    {
        if (!await MayWriteAsync(nameof(UpdateExhibitorAsync), ct)) return false;
        var url = $"{_options.ApiDomain}/backstage/v3/portals/{_options.BackstagePortalId}"
            + $"/events/{_options.BackstageEventId}/exhibitors/{exhibitorId}";
        var payload = new Dictionary<string, object?>();

        // 🔴 §802.2 — CAP BEFORE SENDING. Zoho rejects the ENTIRE update when one field is too long
        // (measured §801.2: `shortDescription` 80, `company_overview` 1000), so an over-long
        // description does not just lose itself — it takes the website and every other field in the
        // same PUT down with it.
        //
        // 🔒 The FORM refuses and the SYNC truncates, on purpose: a person typing can choose which
        // words to cut (§802.1), while this is a value already in the database — from before the
        // rule, from an import, or set by an organizer — where the only alternatives are a truncated
        // value or a 400 that lands nothing at all.
        //
        // ⚠️ Truncation is REPORTED (the warning below), never silent: a cap nobody is told about is
        // how the public event site ends up with a sentence that stops mid-word.
        if (companyOverview is not null)
        {
            payload["company_overview"] = ZohoExhibitorLimits.Cap(
                companyOverview, ZohoExhibitorLimits.Overview);
        }
        if (companyShortDescription is not null)
        {
            payload["company_short_description"] = ZohoExhibitorLimits.Cap(
                companyShortDescription, ZohoExhibitorLimits.ShortDescription);
        }

        if (ZohoExhibitorLimits.IsTooLong(companyOverview, ZohoExhibitorLimits.Overview)
            || ZohoExhibitorLimits.IsTooLong(companyShortDescription, ZohoExhibitorLimits.ShortDescription))
        {
            _log.LogWarning(
                "§802: exhibitor {Id} — a description exceeded Zoho's limit and was TRUNCATED for the "
                + "push (overview {OvLen}/{OvMax}, short {ShLen}/{ShMax}). The stored CEH value is "
                + "unchanged; shorten it on the sponsor's Company Details page so Backstage carries "
                + "the whole sentence.",
                exhibitorId, companyOverview?.Length ?? 0, ZohoExhibitorLimits.Overview,
                companyShortDescription?.Length ?? 0, ZohoExhibitorLimits.ShortDescription);
        }

        if (!string.IsNullOrWhiteSpace(websiteUrl)) payload["website_url"] = websiteUrl;

        // 🔴 §791.3/§791.4 — company_social_pages is SILENTLY DISCARDED by the v3 exhibitor PUT.
        // Measured twice: four controlled calls on 2026-08-03 (including Zoho's own documented
        // `facebook` sample key) and again on 2026-08-04 — 200 every time, echoed back in the
        // response, and absent from the very next GET. CEH's payload shape is correct (§791.3), so
        // this is a switch and not a fix, and it ships OFF: the values reach Backstage as the §792
        // hand-entry mail instead.
        //
        // 🔒 The code path stays so re-testing is one config setting away if Zoho ever repairs it.
        if (_options.PushExhibitorSocialPages
            && (!string.IsNullOrWhiteSpace(linkedInUrl) || !string.IsNullOrWhiteSpace(twitterUrl)))
        {
            var social = new Dictionary<string, object?>();
            if (!string.IsNullOrWhiteSpace(linkedInUrl)) social["linkedin"] = linkedInUrl;
            if (!string.IsNullOrWhiteSpace(twitterUrl)) social["twitter"] = twitterUrl;
            payload["company_social_pages"] = social;
        }
        // Re-push the correct UTF-8 company + contact name so Zoho's mojibake
        // (æøåÆØÅ shown as "?") is overwritten. JsonContent serializes UTF-8.
        if (!string.IsNullOrWhiteSpace(companyName)) payload["company_name"] = companyName;
        // The contact EMAIL is sent on UPDATE ONLY when the CALLER passes a non-blank value
        // ⚰️ §791.5 / §802.4(3) — THE CONTACT BLOCK IS GONE FROM THE UPDATE, AND MUST NOT COME BACK.
        //
        // Operator 2026-08-04: *"we should NEWER have an api trying to update contact details for
        // both sponsor and exhibitor"* (and earlier: *"dont send the contact details again due to
        // zoho limitations as mentioned in code"*). Zoho hard-caps contact e-mail updates at THREE
        // attempts and a no-op resend burns one, so a block that travelled along on every unrelated
        // profile push was spending a budget nobody was watching.
        //
        // 🔒 The parameters are kept so callers compile, and are DELIBERATELY IGNORED here rather
        // than removed: a future caller passing a contact name must not silently start sending it
        // again. The contact is set at CREATE time and changed by hand in Backstage.

        using var req = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = System.Net.Http.Json.JsonContent.Create(payload),
        };
        req.Headers.Add("Authorization", $"Zoho-oauthtoken {accessToken}");
        using var resp = await _http.SendAsync(req, ct);

        // 🔴 §802.4(2) — READ THE FAILURE BODY. This used to be `return resp.IsSuccessStatusCode`,
        // and Zoho had been naming the problem the whole time:
        //   400 {"status_code":"400","message":"`shortDescription` is too long"}
        // Three sessions of §784.13 were spent guessing at a per-record mystery that the response
        // body would have answered on the first run (§801.2).
        if (!resp.IsSuccessStatusCode)
        {
            string detail;
            try { detail = await resp.Content.ReadAsStringAsync(ct); }
            catch { detail = "<body unreadable>"; }

            _log.LogWarning(
                "Zoho exhibitor UPDATE {Id} failed: {Status}. Zoho said: {Detail}",
                exhibitorId, (int)resp.StatusCode, detail);
            return false;
        }

        return true;
    }

    /// <summary>
    /// GET the event's booths as a map of booth_label → booth id. The Backstage exhibitor
    /// field that selects a booth is <c>booth_id</c> (an internal id, e.g.
    /// 14880000003591145); the human label "E-4" lives on the BOOTH object. To UPDATE an
    /// existing exhibitor's booth we must resolve the label to that id first. Keys are
    /// trimmed and matched case-insensitively. Booths are a small finite set, but we page
    /// (mirroring <see cref="PageV3Async"/>) in case the API pages. Field names vary across
    /// Zoho events, so the label falls back to name/label like the sponsorship-type parser.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> GetBoothsAsync(
        string accessToken, CancellationToken ct = default)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var el in PageV3Async("booths", "booths", accessToken, ct))
        {
            var id = FirstNonEmpty(GetString(el, "id"), GetString(el, "booth_id"));
            var label = FirstNonEmpty(
                GetString(el, "booth_label"), GetString(el, "name"), GetString(el, "label"));
            if (id.Length > 0 && label.Length > 0)
                map[label] = id; // FirstNonEmpty already trims; OrdinalIgnoreCase comparer handles case
        }
        return map;
    }

    /// <summary>
    /// PUT only the booth slot onto an EXISTING exhibitor — assigns the booth (e.g. "E-26")
    /// to an exhibitor that was created before its booth was set (Zoho showed "No booth
    /// selected"). The exhibitor field is <c>booth_id</c> (NOT booth_label), so we first
    /// resolve the label to its booth id via <see cref="GetBoothsAsync"/> (or a pre-fetched
    /// <paramref name="boothMap"/> the caller passes to avoid refetching /booths per company)
    /// and PUT <c>{ "booth_id": id }</c>. If the label can't be resolved to a booth id we log
    /// a warning and return false rather than PUT a label that won't take. Minimal payload so
    /// no other field — and never the email — is touched. Returns whether it succeeded.
    /// </summary>
    public async Task<bool> AssignExhibitorBoothAsync(
        string accessToken, string exhibitorId, string boothLabel, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? boothMap = null)
    {
        if (!await MayWriteAsync(nameof(AssignExhibitorBoothAsync), ct)) return false;
        if (string.IsNullOrWhiteSpace(exhibitorId) || string.IsNullOrWhiteSpace(boothLabel)) return false;

        var label = boothLabel.Trim();
        boothMap ??= await GetBoothsAsync(accessToken, ct);
        if (!boothMap.TryGetValue(label, out var boothId) || string.IsNullOrWhiteSpace(boothId))
        {
            _log?.LogWarning(
                "Zoho AssignExhibitorBooth {Id}: booth label '{Booth}' did not resolve to a booth id "
                + "({Count} booths known) — not assigned.", exhibitorId, label, boothMap.Count);
            return false;
        }

        var url = $"{_options.ApiDomain}/backstage/v3/portals/{_options.BackstagePortalId}"
            + $"/events/{_options.BackstageEventId}/exhibitors/{exhibitorId}";
        using var req = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = System.Net.Http.Json.JsonContent.Create(new Dictionary<string, object?> { ["booth_id"] = boothId }),
        };
        req.Headers.Add("Authorization", $"Zoho-oauthtoken {accessToken}");
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            string body; try { body = await resp.Content.ReadAsStringAsync(ct); } catch { body = "(unreadable)"; }
            _log?.LogWarning("Zoho AssignExhibitorBooth {Id} '{Booth}' (booth_id {BoothId}) failed: HTTP {Status} — {Body}",
                exhibitorId, label, boothId, (int)resp.StatusCode, body.Length > 300 ? body[..300] : body);
        }
        return resp.IsSuccessStatusCode;
    }

    /// <summary>One Backstage sponsor — its system id + company name (for matching by name).</summary>
    public sealed record BackstageSponsor(string Id, string CompanyName);

    /// <summary>
    /// List every sponsor in the configured Backstage event (id + company_name) so a
    /// CEH sponsor can be matched by company name. Scope: <c>ZohoBackstage.sponsor.READ</c>.
    /// Returns empty on auth/HTTP failure.
    /// </summary>
    public async Task<IReadOnlyList<BackstageSponsor>> GetSponsorsAsync(
        string accessToken, CancellationToken ct = default)
    {
        var list = new List<BackstageSponsor>();
        await foreach (var el in PageV3Async("sponsors", "sponsors", accessToken, ct))
        {
            var id = el.TryGetProperty("id", out var i) ? i.GetString() : null;
            var name = el.TryGetProperty("company_name", out var n) ? n.GetString() : null;
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                list.Add(new BackstageSponsor(id!, name!));
        }
        return list;
    }

    /// <summary>
    /// PUT updated profile fields onto a Backstage SPONSOR record. Only non-null
    /// fields are sent. Scope: <c>ZohoBackstage.sponsor.UPDATE</c>. Returns whether
    /// the update succeeded.
    /// </summary>
    public async Task<bool> UpdateSponsorAsync(
        string accessToken, string sponsorId,
        string? description, string? websiteUrl, string? companyName,
        CancellationToken ct = default,
        string? contactFirstName = null, string? contactLastName = null, string? contactEmail = null)
    {
        if (!await MayWriteAsync(nameof(UpdateSponsorAsync), ct)) return false;
        var url = $"{_options.ApiDomain}/backstage/v3/portals/{_options.BackstagePortalId}"
            + $"/events/{_options.BackstageEventId}/sponsors/{sponsorId}";
        var payload = new Dictionary<string, object?>();
        if (description is not null) payload["description"] = description;
        if (!string.IsNullOrWhiteSpace(websiteUrl)) payload["website_url"] = websiteUrl;
        if (!string.IsNullOrWhiteSpace(companyName)) payload["company_name"] = companyName;
        // ⚰️ §791.5 / §802.4(3) — THE CONTACT BLOCK IS GONE FROM THE SPONSOR UPDATE TOO.
        //
        // Operator 2026-08-04: *"we should NEWER have an api trying to update contact details for
        // both sponsor and exhibitor"* — both, explicitly. Zoho hard-caps sponsor e-mail updates at
        // THREE and a no-op resend burns one, so a contact block riding along on an unrelated
        // description push spends a budget nobody is watching.
        //
        // 🔒 The parameters are kept and DELIBERATELY IGNORED, so a future caller passing a name
        // cannot silently restart it. The contact is set at CREATE and changed by hand in Backstage.

        using var req = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = System.Net.Http.Json.JsonContent.Create(payload),
        };
        req.Headers.Add("Authorization", $"Zoho-oauthtoken {accessToken}");
        using var resp = await _http.SendAsync(req, ct);
        return resp.IsSuccessStatusCode;
    }

    /// <summary>
    /// A sponsor's/exhibitor's CURRENT social/web/description fields as Zoho holds
    /// them — used by the fill-blank reconcile (REQUIREMENTS §41b) to decide which
    /// fields are blank in Zoho and therefore safe to push from CEH. All values are
    /// trimmed; an absent/blank field comes back as <c>null</c>.
    /// </summary>
    /// <param name="ShortDescription">
    /// 🔑 §801.2 — the exhibitor's <c>company_short_description</c>, as Zoho actually returns it.
    /// <b>The GET does echo it</b>: measured 2026-08-04, present on every exhibitor that has one.
    /// The older claim that this endpoint *"NEVER echoes them back"* was never verified and is
    /// false — the same shape as the phantom read-scope (§754.5). Null for a sponsor (that record
    /// has no such field) and for an exhibitor that has none.
    /// </param>
    public sealed record BackstageSponsorDetail(
        string? WebsiteUrl, string? Description, string? LinkedInUrl, string? TwitterUrl,
        string? ShortDescription = null);

    /// <summary>
    /// GET a single Backstage SPONSOR by id and read its current website /
    /// description / social (linkedin, twitter) fields. Scope:
    /// <c>ZohoBackstage.sponsor.READ</c>. Returns null on auth/HTTP failure.
    /// </summary>
    public async Task<BackstageSponsorDetail?> GetSponsorByIdAsync(
        string accessToken, string sponsorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sponsorId)) return null;
        var url = $"{_options.ApiDomain}/backstage/v3/portals/{_options.BackstagePortalId}"
            + $"/events/{_options.BackstageEventId}/sponsors/{sponsorId}";
        return await GetSocialDetailAsync(url, accessToken, "sponsor", "description", ct);
    }

    /// <summary>
    /// GET a single Backstage EXHIBITOR by id and read its current website /
    /// overview / social (linkedin, twitter) fields. Scope:
    /// <c>ZohoBackstage.exhibitor.READ</c>. Returns null on auth/HTTP failure.
    /// (Exhibitor description is <c>company_overview</c>, not <c>description</c>.)
    /// </summary>
    public async Task<BackstageSponsorDetail?> GetExhibitorByIdAsync(
        string accessToken, string exhibitorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(exhibitorId)) return null;
        var url = $"{_options.ApiDomain}/backstage/v3/portals/{_options.BackstagePortalId}"
            + $"/events/{_options.BackstageEventId}/exhibitors/{exhibitorId}";
        return await GetSocialDetailAsync(url, accessToken, "exhibitor", "company_overview", ct);
    }

    /// <summary>
    /// Shared GET-by-id reader for the social/web/description fields of a sponsor or
    /// exhibitor. The record may sit at the root or be nested under
    /// <paramref name="rootProp"/> ("sponsor"/"exhibitor"); social links live in the
    /// <c>company_social_pages</c> object (keys linkedin/twitter) with a flat
    /// linkedin_url/twitter_url fallback.
    ///
    /// READ↔WRITE SYMMETRY (REQUIREMENTS §41b — the linkedin/twitter fill-blank gap):
    /// <see cref="UpdateExhibitorAsync"/> WRITES social to <c>company_social_pages.{linkedin,twitter}</c>,
    /// so this reader keys on the SAME path. The blank-detection that drives the §41b
    /// "push only when Zoho is blank" gate (<c>BlankInZoho(z?.LinkedInUrl)</c>) therefore
    /// sees exactly what the write produces. A social entry is treated as set whether Zoho
    /// returns it as a plain string (<c>"linkedin":"https://…"</c>) OR as an object that
    /// wraps the URL (<c>"linkedin":{"url":"https://…"}</c> / <c>{"link":…}</c> /
    /// <c>{"value":…}</c>) — both shapes occur across Zoho Backstage schemas; reading only
    /// the string shape would mis-classify a populated object-shaped value as blank.
    /// </summary>
    private async Task<BackstageSponsorDetail?> GetSocialDetailAsync(
        string url, string accessToken, string rootProp, string descriptionProp, CancellationToken ct)
        => (await GetSocialDetailProbedAsync(url, accessToken, rootProp, descriptionProp, ct)).Detail;

    /// <summary>
    /// §553 — the same read, but reporting WHY it came back empty.
    /// </summary>
    /// <remarks>
    /// 🔒 <c>GetSocialDetailAsync</c> returns <c>null</c> for a deleted record AND for an expired
    /// token AND for a 500 — three very different facts collapsed into one. That is precisely the
    /// conflation §553 forbids: *"'not found' and 'could not look' are NOT the same thing, and
    /// conflating them is how a self-heal turns into data loss"*. A caller that unlinked on
    /// <c>null</c> would wipe every sponsor link during an outage.
    /// </remarks>
    private async Task<(BackstageSponsorDetail? Detail, ExternalLinkResult Link)> GetSocialDetailProbedAsync(
        string url, string accessToken, string rootProp, string descriptionProp, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("Authorization", $"Zoho-oauthtoken {accessToken}");
        using var resp = await _http.SendAsync(req, ct);

        if (!resp.IsSuccessStatusCode)
        {
            // A 404 for a specific id is the ONE definite answer: Zoho looked and it is not there.
            var gone = resp.StatusCode == System.Net.HttpStatusCode.NotFound;
            return (null, ExternalLinkProbe.ProbeOne(
                foundExplicitly: false,
                notFoundExplicitly: gone,
                detail: gone
                    ? $"Zoho answered 404 for this {rootProp} id — it was deleted there."
                    : $"Zoho answered {(int)resp.StatusCode} reading this {rootProp} — no conclusion drawn."));
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        if (root.TryGetProperty(rootProp, out var nested) && nested.ValueKind == JsonValueKind.Object)
            root = nested;

        string? linkedIn = null, twitter = null;
        if (root.TryGetProperty("company_social_pages", out var social) && social.ValueKind == JsonValueKind.Object)
        {
            linkedIn = GetSocialUrl(social, "linkedin");
            twitter = GetSocialUrl(social, "twitter");
        }
        linkedIn ??= NullIf(GetString(root, "linkedin_url"));
        twitter ??= NullIf(GetString(root, "twitter_url"));

        return (new BackstageSponsorDetail(
                WebsiteUrl: NullIf(GetString(root, "website_url")),
                Description: NullIf(GetString(root, descriptionProp)),
                LinkedInUrl: linkedIn,
                TwitterUrl: twitter,
                // §801.2 — measured: the exhibitor GET returns this whenever it is set. A sponsor
                // record simply has no such property, so it reads null there.
                ShortDescription: NullIf(GetString(root, "company_short_description"))),
            ExternalLinkProbe.ProbeOne(foundExplicitly: true, notFoundExplicitly: false,
                detail: $"Zoho returned this {rootProp} — the link is good."));
    }

    /// <summary>
    /// §553 — probe a stored SPONSOR link: does the record our id points at still exist in Zoho?
    /// </summary>
    public Task<(BackstageSponsorDetail? Detail, ExternalLinkResult Link)> ProbeSponsorAsync(
        string accessToken, string sponsorId, CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(sponsorId)
            ? Task.FromResult<(BackstageSponsorDetail?, ExternalLinkResult)>(
                (null, ExternalLinkProbe.ProbeOne(false, false, "no stored sponsor id")))
            : GetSocialDetailProbedAsync(
                $"{_options.ApiDomain}/backstage/v3/portals/{_options.BackstagePortalId}"
                + $"/events/{_options.BackstageEventId}/sponsors/{sponsorId}",
                accessToken, "sponsor", "description", ct);

    /// <summary>
    /// §553 — probe a stored EXHIBITOR link: does the record our id points at still exist in Zoho?
    /// </summary>
    public Task<(BackstageSponsorDetail? Detail, ExternalLinkResult Link)> ProbeExhibitorAsync(
        string accessToken, string exhibitorId, CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(exhibitorId)
            ? Task.FromResult<(BackstageSponsorDetail?, ExternalLinkResult)>(
                (null, ExternalLinkProbe.ProbeOne(false, false, "no stored exhibitor id")))
            : GetSocialDetailProbedAsync(
                $"{_options.ApiDomain}/backstage/v3/portals/{_options.BackstagePortalId}"
                + $"/events/{_options.BackstageEventId}/exhibitors/{exhibitorId}",
                accessToken, "exhibitor", "company_overview", ct);

    /// <summary>
    /// Read one <c>company_social_pages</c> entry (e.g. "linkedin"/"twitter") as a URL,
    /// accepting BOTH shapes Zoho returns: a plain string, or an object wrapping the URL
    /// under <c>url</c>/<c>link</c>/<c>value</c>. Returns null when absent/blank. Keeps the
    /// §41b blank-detection in lock-step with the write path (which sets the same keys), so a
    /// populated Zoho social value is never mis-read as blank.
    /// </summary>
    private static string? GetSocialUrl(JsonElement social, string key)
    {
        if (!social.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.String) return NullIf(v.GetString() ?? string.Empty);
        if (v.ValueKind == JsonValueKind.Object)
            foreach (var sub in new[] { "url", "link", "value" })
                if (v.TryGetProperty(sub, out var u) && u.ValueKind == JsonValueKind.String)
                {
                    var s = NullIf(u.GetString() ?? string.Empty);
                    if (s is not null) return s;
                }
        return null;
    }

    /// <summary>One Zoho sponsorship type / sponsor category (id + name).</summary>
    public sealed record BackstageSponsorshipType(string Id, string Name);

    /// <summary>
    /// GET the event's sponsorship types (sponsor categories) — name → id, used to
    /// set <c>sponsorship_type</c> when creating a sponsor. Small finite set, no paging.
    /// </summary>
    public async Task<IReadOnlyList<BackstageSponsorshipType>> GetSponsorshipTypesAsync(
        string accessToken, CancellationToken ct = default)
    {
        var url = $"{_options.ApiDomain}/backstage/v3/portals/{_options.BackstagePortalId}"
            + $"/events/{_options.BackstageEventId}/sponsorship_types";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("Authorization", $"Zoho-oauthtoken {accessToken}");
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return Array.Empty<BackstageSponsorshipType>();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var list = new List<BackstageSponsorshipType>();
        foreach (var prop in new[] { "sponsorship_types", "sponsor_categories", "data" })
        {
            if (!doc.RootElement.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
            foreach (var el in arr.EnumerateArray())
            {
                // Field names vary across Zoho events — try the same candidates the
                // legacy script used (name/category_name/title, id/sponsorship_type_id/…).
                var id = FirstNonEmpty(
                    GetString(el, "id"), GetString(el, "sponsorship_type_id"),
                    GetString(el, "sponsor_category_id"), GetString(el, "category_id"));
                var name = FirstNonEmpty(
                    GetString(el, "name"), GetString(el, "category_name"),
                    GetString(el, "title"), GetString(el, "sponsorship_type_name"));
                if (id.Length > 0 && name.Length > 0)
                    list.Add(new BackstageSponsorshipType(id, name));
            }
            if (list.Count > 0) break;
        }
        return list;
    }

    /// <summary>
    /// POST create a sponsor. Returns the new sponsor id, or null on failure.
    /// Body: company_name, website_url, description, sponsorship_type (category id),
    /// contact{first_name,last_name,email}. (currency_code/language are server-set.)
    /// </summary>
    public async Task<string?> CreateSponsorAsync(
        string accessToken, string companyName, string? websiteUrl, string? description,
        string sponsorshipTypeId, string? contactFirstName, string? contactLastName, string? contactEmail,
        CancellationToken ct = default)
    {
        if (!await MayWriteAsync(nameof(CreateSponsorAsync), ct)) return null;
        var url = $"{_options.ApiDomain}/backstage/v3/portals/{_options.BackstagePortalId}"
            + $"/events/{_options.BackstageEventId}/sponsors";
        var payload = new Dictionary<string, object?>
        {
            ["company_name"] = companyName,
            ["sponsorship_type"] = sponsorshipTypeId,
        };
        if (!string.IsNullOrWhiteSpace(websiteUrl)) payload["website_url"] = websiteUrl;
        if (!string.IsNullOrWhiteSpace(description)) payload["description"] = description;
        if (!string.IsNullOrWhiteSpace(contactFirstName) || !string.IsNullOrWhiteSpace(contactLastName)
            || !string.IsNullOrWhiteSpace(contactEmail))
        {
            var contact = new Dictionary<string, object?>
            {
                ["first_name"] = contactFirstName ?? string.Empty,
                ["last_name"] = contactLastName ?? string.Empty,
            };
            if (!string.IsNullOrWhiteSpace(contactEmail)) contact["email"] = contactEmail;
            payload["contact"] = contact;
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = System.Net.Http.Json.JsonContent.Create(payload),
        };
        req.Headers.Add("Authorization", $"Zoho-oauthtoken {accessToken}");
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;

        // 2xx — but Zoho can answer 200 WITHOUT creating (e.g. the contact email is already
        // in use). A 200 with no sponsor id is NOT a success: log the body and fail so it is
        // counted/alerted and we can see what Zoho returned.
        var okBody = await resp.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(okBody);
            var root = doc.RootElement;
            // The created sponsor may be at the root or nested under "sponsor".
            if (root.TryGetProperty("sponsor", out var sp) && sp.ValueKind == JsonValueKind.Object) root = sp;
            foreach (var key in new[] { "id", "sponsor_id" })
                if (root.TryGetProperty(key, out var idEl) && idEl.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(idEl.GetString()))
                    return idEl.GetString();
        }
        catch { /* fall through to the no-id failure log */ }

        var trimmed = okBody.Length > 600 ? okBody[..600] : okBody;
        _log?.LogWarning(
            "Zoho CreateSponsor for '{Company}' returned HTTP {Status} but NO sponsor id "
            + "(likely not created — email already in use). Body: {Body}",
            companyName, (int)resp.StatusCode, trimmed);
        return null;
    }

    /// <summary>Result of a Zoho create: the new id on success, else the error message
    /// (the Zoho HTTP status + body) so the caller can put the REAL reason in its alert
    /// email (operator 2026-06-25: "all error emails must include error message").</summary>
    public sealed record ZohoCreateResult(string? Id, string? Error)
    {
        public bool Ok => !string.IsNullOrEmpty(Id);
    }

    /// <summary>
    /// POST create an EXHIBITOR (the booth record). Zoho REQUIRES <c>exhibitor_category_id</c>
    /// (else HTTP 400 "Booth category ID is required") — the pinned booth-category id for the
    /// company's tier (REQUIREMENTS §41a). Body: exhibitor_category_id, company_name,
    /// website_url, description, contact{first,last,email}. <c>exhibitor_type</c> is NOT sent
    /// (Zoho derives it from the category; sending it causes "category not found"). Scope:
    /// <c>ZohoBackstage.exhibitor.CREATE</c>.
    /// </summary>
    public async Task<ZohoCreateResult> CreateExhibitorAsync(
        string accessToken, string companyName, string? websiteUrl, string? description,
        string? exhibitorCategoryId, string? contactFirstName, string? contactLastName, string? contactEmail,
        string? boothLabel = null, CancellationToken ct = default)
    {
        if (!await MayWriteAsync(nameof(CreateExhibitorAsync), ct))
            return new(null, ExternalWritesDisabledError);
        if (string.IsNullOrWhiteSpace(exhibitorCategoryId))
            return new(null, "No booth/exhibitor category id configured for this tier "
                + "(pin it in zohoBoothCategoryIds — REQUIREMENTS §41a).");

        var url = $"{_options.ApiDomain}/backstage/v3/portals/{_options.BackstagePortalId}"
            + $"/events/{_options.BackstageEventId}/exhibitors";
        var payload = new Dictionary<string, object?>
        {
            ["company_name"] = companyName,
            ["exhibitor_category_id"] = exhibitorCategoryId,
        };
        // Assign the physical booth slot (e.g. "E-26") so Zoho doesn't show "No booth selected".
        if (!string.IsNullOrWhiteSpace(boothLabel)) payload["booth_label"] = boothLabel;
        if (!string.IsNullOrWhiteSpace(websiteUrl)) payload["website_url"] = websiteUrl;
        if (!string.IsNullOrWhiteSpace(description)) payload["description"] = description;
        if (!string.IsNullOrWhiteSpace(contactFirstName) || !string.IsNullOrWhiteSpace(contactLastName)
            || !string.IsNullOrWhiteSpace(contactEmail))
        {
            var contact = new Dictionary<string, object?>
            {
                ["first_name"] = contactFirstName ?? string.Empty,
                ["last_name"] = contactLastName ?? string.Empty,
            };
            if (!string.IsNullOrWhiteSpace(contactEmail)) contact["email"] = contactEmail;
            payload["contact"] = contact;
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = System.Net.Http.Json.JsonContent.Create(payload),
        };
        req.Headers.Add("Authorization", $"Zoho-oauthtoken {accessToken}");
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            string body;
            try { body = await resp.Content.ReadAsStringAsync(ct); } catch { body = "(unreadable)"; }
            if (body.Length > 600) body = body[..600];
            var err = $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} — {body}";
            _log?.LogWarning("Zoho CreateExhibitor failed for '{Company}': {Error}", companyName, err);
            return new(null, err);
        }

        // 2xx — but Zoho can answer 200 WITHOUT creating (contact email already in use:
        // "email is the key"). A 200 with no id is NOT a success.
        var okBody = await resp.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(okBody);
            var root = doc.RootElement;
            if (root.TryGetProperty("exhibitor", out var ex) && ex.ValueKind == JsonValueKind.Object) root = ex;
            foreach (var key in new[] { "id", "exhibitor_id" })
                if (root.TryGetProperty(key, out var idEl) && idEl.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(idEl.GetString()))
                    return new(idEl.GetString(), null);   // genuine create
        }
        catch { /* fall through to the no-id failure */ }

        var trimmed = okBody.Length > 600 ? okBody[..600] : okBody;
        var noIdErr = $"HTTP {(int)resp.StatusCode} but no exhibitor id (email already in use?). Body: {trimmed}";
        _log?.LogWarning("Zoho CreateExhibitor for '{Company}': {Error}", companyName, noIdErr);
        return new(null, noIdErr);
    }

    // ===================== AGENDA SESSIONS (CEH→Zoho push, §57 stage 2) =====================
    // The CEH→Zoho SESSION push (REQUIREMENTS §57 stage 2 = CehToZoho). Unlike speakers,
    // the agenda sessions API DOES support per-id update: POST creates
    // (…/sessions?day={1-based}), PUT updates (…/sessions/{sessionId}). The day index is
    // 1-based (agenda day 0 is empty — same convention the §38e READ uses). Body fields:
    // title, description (abstract), start_time, duration (minutes, derived from start/end),
    // track, sessionType, day. Empty fields are stripped so a blank CEH value never clobbers
    // Zoho.
    //
    // LIVE-VERIFIED 2026-06-25 (the create contract is stricter than the §38e READ shape):
    //   • `track` must be the Backstage TRACK ID (a number id), NOT the track NAME. Sending a
    //     name → HTTP 400 "Please enter a valid `trackId`". Resolve name→id via
    //     GetTracksAsync (mirrors GetBoothsAsync). A blank/unresolvable track is omitted.
    //   • `sessionType` is REQUIRED on create → HTTP 400 "you have not entered a session type"
    //     when absent. It is an event-specific value (the GET shape returns e.g.
    //     session_type="PRESENTATION"); the create value is operator config because Backstage
    //     v3 exposes no session-types list endpoint to enumerate it. Passed through verbatim.

    /// <summary>One Zoho Backstage agenda TRACK — its id + display name (for name→id resolve).</summary>
    public sealed record BackstageTrack(string Id, string Name);

    /// <summary>
    /// GET the event's agenda tracks (track_id + name). Used to resolve a CEH session's
    /// track NAME to the Backstage track ID the create/update endpoint requires. Small finite
    /// set; tolerant of id/name field aliases. Returns empty on auth/HTTP failure.
    /// </summary>
    public async Task<IReadOnlyList<BackstageTrack>> GetTracksAsync(
        string accessToken, CancellationToken ct = default)
    {
        var url = $"{_options.ApiDomain}/backstage/v3/portals/{_options.BackstagePortalId}"
            + $"/events/{_options.BackstageEventId}/tracks";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("Authorization", $"Zoho-oauthtoken {accessToken}");
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return Array.Empty<BackstageTrack>();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var list = new List<BackstageTrack>();
        var root = doc.RootElement;
        var arr = root.ValueKind == JsonValueKind.Array ? root
            : (root.TryGetProperty("tracks", out var t) && t.ValueKind == JsonValueKind.Array ? t : default);
        if (arr.ValueKind == JsonValueKind.Array)
            foreach (var el in arr.EnumerateArray())
            {
                var id = FirstNonEmpty(GetString(el, "track_id"), GetString(el, "id"));
                var name = FirstNonEmpty(GetString(el, "name"), GetString(el, "title"));
                if (id.Length > 0 && name.Length > 0) list.Add(new BackstageTrack(id, name));
            }
        return list;
    }

    /// <summary>
    /// GET the event's HALLS (venue rooms) — id + display name. Used by the stage-2 session
    /// push to resolve a CEH session's room name to the Backstage hall id that the create
    /// endpoint's <c>venue</c> field carries. Tolerant of id/name field aliases (mirrors the
    /// halls read in <see cref="GetBackstageSessionsAsync"/>). Empty on auth/HTTP failure.
    /// </summary>
    public async Task<IReadOnlyList<(string Id, string Name)>> GetHallsAsync(
        string accessToken, CancellationToken ct = default)
    {
        var list = new List<(string Id, string Name)>();
        await foreach (var h in PageV3Async("halls", "halls", accessToken, ct))
        {
            var id = FirstNonEmpty(GetString(h, "id"), GetString(h, "hall_id"));
            var name = FirstNonEmpty(GetString(h, "name"), GetString(h, "title"));
            if (id.Length > 0 && name.Length > 0) list.Add((id, name));
        }
        return list;
    }

    /// <summary>
    /// POST create a HALL (venue room) in Zoho Backstage. LIVE-VERIFIED 2026-07-23 (stage-2
    /// pilot): the body is <c>{name, capacity}</c> and <c>capacity</c> is REQUIRED — the pilot
    /// created "Room-5-7-Floor 1-Max 122-MC" (capacity 122) exactly this way. Returns the new
    /// hall id on success, else the Zoho HTTP status + body so the caller can surface the real
    /// reason. A 2xx with no parseable hall id is treated as a failure (not created).
    /// </summary>
    /// <summary>
    /// POST create an agenda TRACK by name (operator 2026-07-23: "use track from Sessionize —
    /// create if missing"). The Sessionize track label IS the Backstage track name — no
    /// mapping, no fuzzy matching; the operator prunes unwanted tracks in the Backstage UI
    /// (each create is reported in the ops change mail). Same URL family as
    /// <see cref="GetTracksAsync"/>; id parsed with the track_id alias tolerance.
    /// </summary>
    public async Task<ZohoCreateResult> CreateTrackAsync(
        string accessToken, string name, CancellationToken ct = default)
    {
        if (!await MayWriteAsync(nameof(CreateTrackAsync), ct))
            return new(null, ExternalWritesDisabledError);
        var url = $"{_options.ApiDomain}/backstage/v3/portals/{_options.BackstagePortalId}"
            + $"/events/{_options.BackstageEventId}/tracks";
        var payload = new Dictionary<string, object?> { ["name"] = (name ?? string.Empty).Trim() };

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = System.Net.Http.Json.JsonContent.Create(payload),
        };
        req.Headers.Add("Authorization", $"Zoho-oauthtoken {accessToken}");
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            string body; try { body = await resp.Content.ReadAsStringAsync(ct); } catch { body = "(unreadable)"; }
            if (body.Length > 600) body = body[..600];
            var err = $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} — {body}";
            _log?.LogWarning("Zoho CreateTrack '{Name}' failed: {Error}", name, err);
            return new(null, err);
        }

        var okBody = await resp.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(okBody);
            var root = doc.RootElement;
            if (root.TryGetProperty("track", out var t) && t.ValueKind == JsonValueKind.Object) root = t;
            foreach (var key in new[] { "id", "track_id" })
                if (root.TryGetProperty(key, out var idEl) && idEl.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(idEl.GetString()))
                    return new(idEl.GetString(), null);
        }
        catch { /* fall through */ }

        var trimmedT = okBody.Length > 600 ? okBody[..600] : okBody;
        var noTrackIdErr = $"HTTP {(int)resp.StatusCode} but no track id. Body: {trimmedT}";
        _log?.LogWarning("Zoho CreateTrack '{Name}': {Error}", name, noTrackIdErr);
        return new(null, noTrackIdErr);
    }

    public async Task<ZohoCreateResult> CreateHallAsync(
        string accessToken, string name, int capacity, CancellationToken ct = default)
    {
        if (!await MayWriteAsync(nameof(CreateHallAsync), ct))
            return new(null, ExternalWritesDisabledError);
        var url = $"{_options.ApiDomain}/backstage/v3/portals/{_options.BackstagePortalId}"
            + $"/events/{_options.BackstageEventId}/halls";
        var payload = new Dictionary<string, object?>
        {
            ["name"] = (name ?? string.Empty).Trim(),
            ["capacity"] = capacity,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = System.Net.Http.Json.JsonContent.Create(payload),
        };
        req.Headers.Add("Authorization", $"Zoho-oauthtoken {accessToken}");
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            string body; try { body = await resp.Content.ReadAsStringAsync(ct); } catch { body = "(unreadable)"; }
            if (body.Length > 600) body = body[..600];
            var err = $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} — {body}";
            _log?.LogWarning("Zoho CreateHall '{Name}' failed: {Error}", name, err);
            return new(null, err);
        }

        var okBody = await resp.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(okBody);
            var root = doc.RootElement;
            if (root.TryGetProperty("hall", out var h) && h.ValueKind == JsonValueKind.Object) root = h;
            foreach (var key in new[] { "id", "hall_id" })
                if (root.TryGetProperty(key, out var idEl) && idEl.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(idEl.GetString()))
                    return new(idEl.GetString(), null);
        }
        catch { /* fall through */ }

        var trimmed = okBody.Length > 600 ? okBody[..600] : okBody;
        var noIdErr = $"HTTP {(int)resp.StatusCode} but no hall id. Body: {trimmed}";
        _log?.LogWarning("Zoho CreateHall '{Name}': {Error}", name, noIdErr);
        return new(null, noIdErr);
    }

    /// <summary>
    /// POST create an AGENDA session in Zoho Backstage (REQUIREMENTS §57 stage 2). Returns
    /// the new session id on success, else the Zoho HTTP status + body so the caller can put
    /// the real reason in its alert. The session is created on the given 1-based agenda
    /// <paramref name="day"/> (POST …/sessions?day={day}). <paramref name="durationMinutes"/>
    /// is sent as <c>duration</c> (Backstage stores duration, not an explicit end).
    /// <paramref name="trackId"/> is the Backstage track ID (resolve via
    /// <see cref="GetTracksAsync"/>); <paramref name="sessionType"/> is the required
    /// event-specific session type. <paramref name="venueId"/> is the Backstage HALL id
    /// (resolve via <see cref="GetHallsAsync"/>, create via <see cref="CreateHallAsync"/>);
    /// <paramref name="speakerEmails"/> are the session's speaker e-mail addresses — both are
    /// CREATE-accepted fields (live-verified 2026-07-23, the pilot's COMPLETE session carried
    /// both). Scope: agenda CREATE. A 2xx with no parseable session id is treated as a failure
    /// (not created).
    /// </summary>
    public async Task<ZohoCreateResult> CreateSessionAsync(
        string accessToken, int day, string title, string? description,
        DateTimeOffset? startTime, int? durationMinutes, string? trackId, string? sessionType,
        string? venueId = null, IReadOnlyList<string>? speakerEmails = null,
        CancellationToken ct = default)
    {
        if (!await MayWriteAsync(nameof(CreateSessionAsync), ct))
            return new(null, ExternalWritesDisabledError);
        if (day < 1) day = 1;
        // LIVE-VERIFIED 2026-07-23 (stage-2 pilot): when `start_time` is in the payload the
        // `?day=` query parameter is rejected as HTTP 400 "Extra param found" — the day is
        // derived from start_time. Send ?day= ONLY for an untimed session.
        var url = $"{_options.ApiDomain}/backstage/v3/portals/{_options.BackstagePortalId}"
            + $"/events/{_options.BackstageEventId}/sessions"
            + (startTime is null ? $"?day={day}" : string.Empty);
        // Create-only field set — description is not accepted on create (see BuildSessionPayload).
        var payload = BuildSessionPayload(
            title, description, startTime, durationMinutes, trackId, sessionType,
            includeDescription: false, venueId: venueId, speakerEmails: speakerEmails);

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = System.Net.Http.Json.JsonContent.Create(payload),
        };
        req.Headers.Add("Authorization", $"Zoho-oauthtoken {accessToken}");
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            string body; try { body = await resp.Content.ReadAsStringAsync(ct); } catch { body = "(unreadable)"; }
            if (body.Length > 600) body = body[..600];
            var err = $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} — {body}";
            _log?.LogWarning("Zoho CreateSession '{Title}' (day {Day}) failed: {Error}", title, day, err);
            return new(null, err);
        }

        var okBody = await resp.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(okBody);
            var root = doc.RootElement;
            if (root.TryGetProperty("session", out var s) && s.ValueKind == JsonValueKind.Object) root = s;
            foreach (var key in new[] { "id", "session_id" })
                if (root.TryGetProperty(key, out var idEl) && idEl.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(idEl.GetString()))
                    return new(idEl.GetString(), null);
        }
        catch { /* fall through */ }

        var trimmed = okBody.Length > 600 ? okBody[..600] : okBody;
        var noIdErr = $"HTTP {(int)resp.StatusCode} but no session id. Body: {trimmed}";
        _log?.LogWarning("Zoho CreateSession '{Title}': {Error}", title, noIdErr);
        return new(null, noIdErr);
    }

    /// <summary>
    /// PUT update an EXISTING agenda session by its Backstage session id (REQUIREMENTS §57
    /// stage 2). Only non-empty fields are sent. Returns whether the update succeeded; logs
    /// the status + body on failure. Scope: agenda UPDATE.
    /// ⚠ LIVE-VERIFIED 2026-07-23 (stage-2 pilot): the per-id sessions endpoint currently
    /// refuses PUT AND PATCH alike (404 "Please provide valid method") — the v3 sessions API
    /// is CREATE-ONLY today, exactly like the speakers API, and Zoho documents no
    /// update-a-session page. This method therefore returns an HONEST failure; in-place agenda
    /// edits happen manually in the Backstage UI until Zoho ships an update method.
    /// </summary>
    public async Task<bool> UpdateSessionAsync(
        string accessToken, string sessionId, string title, string? description,
        DateTimeOffset? startTime, int? durationMinutes, string? trackId, string? sessionType,
        CancellationToken ct = default)
    {
        if (!await MayWriteAsync(nameof(UpdateSessionAsync), ct)) return false;
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        var url = $"{_options.ApiDomain}/backstage/v3/portals/{_options.BackstagePortalId}"
            + $"/events/{_options.BackstageEventId}/sessions/{sessionId}";
        var payload = BuildSessionPayload(title, description, startTime, durationMinutes, trackId, sessionType);

        using var req = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = System.Net.Http.Json.JsonContent.Create(payload),
        };
        req.Headers.Add("Authorization", $"Zoho-oauthtoken {accessToken}");
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            string body; try { body = await resp.Content.ReadAsStringAsync(ct); } catch { body = "(unreadable)"; }
            _log?.LogWarning("Zoho UpdateSession {Id} '{Title}' failed: HTTP {Status} — {Body}",
                sessionId, title, (int)resp.StatusCode, body.Length > 300 ? body[..300] : body);
        }
        return resp.IsSuccessStatusCode;
    }

    /// <summary>
    /// Build the agenda-session request body shared by create + update. Title is always
    /// sent (Backstage requires it); description/trackId/sessionType/start_time/duration are
    /// sent only when non-empty so a blank CEH value never overwrites Zoho. <c>track</c> is
    /// the Backstage TRACK ID (live-verified: the name is rejected); <c>session_type</c> is the
    /// required event-specific session type. <c>start_time</c> is the ISO-8601 instant;
    /// <c>duration</c> is whole minutes. Exposed for the stage-2 unit test to assert the
    /// payload shape without HTTP.
    ///
    /// <para>LIVE-VERIFIED 2026-07-23 (stage-2 pilot): the CREATE endpoint accepts ONLY the
    /// documented fields — an unknown key fails the whole call with HTTP 400 "Extra param
    /// found". <c>description</c> is NOT accepted on create (<paramref name="includeDescription"/>
    /// = false there); it is not settable via the API at all today, since the per-id session
    /// update refuses PUT and PATCH alike (404 "Please provide valid method" — the sessions API
    /// is CREATE-ONLY, exactly like the speakers API). Descriptions are entered manually in the
    /// Backstage UI until Zoho ships an update method.</para>
    ///
    /// <para>LIVE-VERIFIED 2026-07-23 (stage-2 pilot v2): the CREATE endpoint DOES accept
    /// <c>venue</c> (the Backstage HALL id, a string) and <c>speakers</c> (an array of speaker
    /// e-mail strings — accepted even before a speaker record exists). Both are emitted only
    /// when non-empty.</para>
    /// </summary>
    public static IReadOnlyDictionary<string, object?> BuildSessionPayload(
        string title, string? description, DateTimeOffset? startTime, int? durationMinutes,
        string? trackId, string? sessionType, bool includeDescription = true,
        string? venueId = null, IReadOnlyList<string>? speakerEmails = null)
    {
        var payload = new Dictionary<string, object?> { ["title"] = (title ?? string.Empty).Trim() };
        if (includeDescription && !string.IsNullOrWhiteSpace(description)) payload["description"] = description!.Trim();
        // `track` carries the Backstage track ID (live-verified the create rejects a name).
        if (!string.IsNullOrWhiteSpace(trackId)) payload["track"] = trackId!.Trim();
        // LIVE-VERIFIED 2026-07-23 (stage-2 pilot): the v3 API is snake_case — a camelCase
        // "sessionType" is silently IGNORED and the create fails with HTTP 400
        // "You have not entered a session type". The GET shape confirms `session_type`.
        if (!string.IsNullOrWhiteSpace(sessionType)) payload["session_type"] = sessionType!.Trim();
        if (startTime is { } st)
            payload["start_time"] = st.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ",
                System.Globalization.CultureInfo.InvariantCulture);
        if (durationMinutes is { } d && d > 0) payload["duration"] = d;
        // LIVE-VERIFIED 2026-07-23: `venue` (hall id) + `speakers` (e-mail strings) are
        // CREATE-accepted; emitted only when non-empty so nothing extra is ever sent.
        if (!string.IsNullOrWhiteSpace(venueId)) payload["venue"] = venueId!.Trim();
        // LOWERCASED (live-verified 2026-07-24, defence-in-depth with the push engines):
        // Zoho stores speaker e-mails lowercased and matches this attach list
        // CASE-SENSITIVELY — a mixed-case e-mail is silently dropped (no link, no error).
        var speakers = speakerEmails?
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (speakers is { Length: > 0 }) payload["speakers"] = speakers;
        return payload;
    }

    // ===================== SPEAKERS (create-only API) =====================
    // Verified live 2026-06-25: the Backstage v3 speakers API supports POST (create)
    // only — per-id POST/PUT/PATCH and DELETE all return 404 "Please provide valid
    // method". So there is NO in-place update: an existing speaker must be updated
    // manually in the Backstage UI (the sync blocks + alerts instead of duplicating).

    /// <summary>
    /// Index of existing Backstage speakers by lower-cased email → speaker id. Used to
    /// decide create-vs-block (we never create a duplicate for an email already present).
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> GetSpeakerIdsByEmailAsync(
        string accessToken, CancellationToken ct = default)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var el in PageV3Async("speakers", "speakers", accessToken, ct))
        {
            var id = el.TryGetProperty("id", out var i) ? i.GetString() : null;
            string? email = null;
            foreach (var f in new[] { "email", "email_address" })
                if (el.TryGetProperty(f, out var e) && e.ValueKind == JsonValueKind.String) { email = e.GetString(); break; }
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(email))
                map[email!.Trim()] = id!;
        }
        return map;
    }

    /// <summary>
    /// POST create a Backstage SPEAKER. Mirrors the legacy
    /// Sync-Sessionize-Speakers-to-Zoho-Backstage.ps1 payload (name=first, last_name,
    /// designation=tagline, description=bio, country, linkedin, twitter, + skills).
    /// Empty fields are stripped. Returns the new speaker id (empty string = created
    /// but id unparsed) or the HTTP status + body on failure so the caller's alert
    /// mail carries the REAL Zoho reason (e.g. the duplicate-e-mail refusal when a
    /// stub record for the same e-mail already exists in Backstage).
    /// </summary>
    public async Task<ZohoCreateResult> CreateSpeakerAsync(
        string accessToken, string email, string? firstName, string? lastName,
        string? country, string? tagline, string? bio, string? linkedIn, string? twitter,
        string? skills, bool featured, CancellationToken ct = default,
        string? company = null)
    {
        if (!await MayWriteAsync(nameof(CreateSpeakerAsync), ct))
            return new(null, ExternalWritesDisabledError);
        var url = $"{_options.ApiDomain}/backstage/v3/portals/{_options.BackstagePortalId}"
            + $"/events/{_options.BackstageEventId}/speakers";
        // HARD GATE: 'featured' tracks the hub's SelectedForPublish — an unselected
        // speaker is created non-featured (never highlighted/published).
        // Field names come from ZohoFieldMap (§302b) — the live speaker record carries
        // "company" (GUI "Company Name"), optional; sent only when CEH captured it.
        var payload = new Dictionary<string, object?> { ["email"] = email, ["featured"] = featured };
        void Set(string k, string? v) { if (!string.IsNullOrWhiteSpace(v)) payload[k] = v!.Trim(); }
        Set(ZohoFieldMap.Speaker.FirstName.ApiField, firstName);
        Set(ZohoFieldMap.Speaker.LastName.ApiField, lastName);
        Set(ZohoFieldMap.Speaker.Company.ApiField, company);
        Set(ZohoFieldMap.Speaker.Country.ApiField, string.IsNullOrWhiteSpace(country) ? null : country!.Trim().ToUpperInvariant());
        Set(ZohoFieldMap.Speaker.Tagline.ApiField, tagline);
        Set(ZohoFieldMap.Speaker.Biography.ApiField, bio);
        Set(ZohoFieldMap.Speaker.LinkedIn.ApiField, linkedIn);
        Set(ZohoFieldMap.Speaker.Twitter.ApiField, twitter);
        Set(ZohoFieldMap.Speaker.Skills.ApiField, skills);

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = System.Net.Http.Json.JsonContent.Create(payload),
        };
        req.Headers.Add("Authorization", $"Zoho-oauthtoken {accessToken}");
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            string body; try { body = await resp.Content.ReadAsStringAsync(ct); } catch { body = "(unreadable)"; }
            if (body.Length > 600) body = body[..600];
            var err = $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} — {body}";
            _log?.LogWarning("Zoho CreateSpeaker '{Email}' failed: {Error}", email, err);
            return new(null, err);
        }
        try
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (root.TryGetProperty("speaker", out var sp) && sp.ValueKind == JsonValueKind.Object) root = sp;
            if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                return new(idEl.GetString(), null);
        }
        catch { /* created but id unparsed */ }
        return new(string.Empty, null);
    }

    /// <summary>
    /// Fetch the CURRENT Zoho Backstage SPEAKERS (REQUIREMENTS §38e/§58) — one row per
    /// speaker with its id, name (first + last), tagline (designation), bio (description),
    /// country and linkedin/twitter. This is the SOURCE the §58 Zoho→CEH speaker
    /// change-detection engine diffs against the CEH stored snapshot.
    ///
    /// <b>AVAILABILITY (fail-soft, mirrors <see cref="GetBackstageSessionsAsync"/>).</b> An HTTP
    /// failure yields <see cref="BackstageSpeakersResult.Unavailable"/> and NEVER fakes data, so the
    /// engine no-ops instead of mistaking an empty pull for "all speakers changed/were removed".
    /// <para>🗑 §754.5 — this is no longer gated on a config flag. The credentials have the
    /// permission; the real gate is the <c>speaker-change-alerts</c> feature switch.</para>
    /// </summary>
    public async Task<BackstageSpeakersResult> GetBackstageSpeakersAsync(
        string accessToken, CancellationToken ct = default)
    {
        // 🗑 §754.5 — the "the speaker scope is not on the token yet" early return is GONE, for the
        // same reason as the agenda one: it was never true. See the note on ZohoOptions.

        var list = new List<BackstageSpeaker>();
        await foreach (var el in PageV3Async("speakers", "speakers", accessToken, ct))
        {
            var sp = ParseSpeaker(el);
            if (sp is not null) list.Add(sp);
        }
        return BackstageSpeakersResult.Available(list);
    }

    /// <summary>
    /// GET a single Backstage SPEAKER by id and read its current name / tagline / bio /
    /// country / social fields (REQUIREMENTS §38e/§58). Returns null on an auth/HTTP failure
    /// (fail-soft, like <see cref="GetSponsorByIdAsync"/>) — 🗑 §754.5: no longer on a config flag,
    /// because the Backstage credentials have the permission.
    /// </summary>
    public async Task<BackstageSpeaker?> GetSpeakerByIdAsync(
        string accessToken, string speakerId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(speakerId)) return null;
        var url = $"{_options.ApiDomain}/backstage/v3/portals/{_options.BackstagePortalId}"
            + $"/events/{_options.BackstageEventId}/speakers/{speakerId}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("Authorization", $"Zoho-oauthtoken {accessToken}");
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        // The speaker may sit at the root or be nested under "speaker".
        if (root.TryGetProperty("speaker", out var nested) && nested.ValueKind == JsonValueKind.Object)
            root = nested;
        return ParseSpeaker(root);
    }

    /// <summary>
    /// Flatten one Backstage speaker JSON element to a <see cref="BackstageSpeaker"/>. Name is
    /// first (<c>name</c>) + <c>last_name</c> joined; tagline is <c>designation</c>; bio is
    /// <c>description</c>; social URLs come from <c>company_social_pages.{linkedin,twitter}</c>
    /// (same path <see cref="CreateSpeakerAsync"/> writes / <see cref="GetSocialUrl"/> reads)
    /// with a flat <c>linkedin</c>/<c>twitter</c> fallback. Returns null when the element has
    /// no id (cannot be matched to a CEH speaker).
    /// </summary>
    private static BackstageSpeaker? ParseSpeaker(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        var id = GetString(el, "id");
        if (id.Length == 0) return null;

        var first = GetString(el, "name");
        var last = GetString(el, "last_name");
        var name = $"{first} {last}".Trim();

        string? linkedIn = null, twitter = null;
        if (el.TryGetProperty("company_social_pages", out var social) && social.ValueKind == JsonValueKind.Object)
        {
            linkedIn = GetSocialUrl(social, "linkedin");
            twitter = GetSocialUrl(social, "twitter");
        }
        linkedIn ??= NullIf(GetString(el, "linkedin"));
        twitter ??= NullIf(GetString(el, "twitter"));

        return new BackstageSpeaker(
            SpeakerId: id,
            Name: NullIf(name),
            Tagline: NullIf(GetString(el, "designation")),
            Bio: NullIf(GetString(el, "description")),
            // ⚠️ §623 — ALWAYS NULL: the API returns no `country` on the list OR the per-id record.
            // Kept so the shape is stable; see BackstageSpeaker.CountryIsReadable. Never diff it.
            Country: NullIf(GetString(el, "country")),
            LinkedIn: linkedIn,
            Twitter: twitter,
            // §623 — both ARE returned, and both are what the operator asked about for Per Larsen.
            Company: NullIf(GetString(el, "company")),
            Skills: NullIf(GetString(el, "skills")),
            Email: NullIf(GetString(el, "email")));
    }

    /// <summary>
    /// One exhibitor booth member as Zoho returns it. <see cref="Id"/> is the Zoho member
    /// id — CEH stores only the member's email (not this id), so a delete resolves email→id
    /// via <see cref="GetBoothMembersAsync"/> and then DELETEs by id.
    /// </summary>
    public sealed record BackstageBoothMember(
        string Id, string Email, string FirstName, string LastName, string Role);

    /// <summary>
    /// GET all booth members of an exhibitor. Response shape:
    /// <c>{ members: [ { id, role, status, contact: { first_name, last_name, email } } ] }</c>.
    /// Captures the member <c>id</c> so callers can resolve an email to its Zoho member id
    /// (needed to DELETE a member — CEH stores email, Zoho addresses members by id).
    /// Scope: <c>ZohoBackstage.exhibitor.READ</c>. Empty on failure.
    /// </summary>
    public async Task<IReadOnlyList<BackstageBoothMember>> GetBoothMembersAsync(
        string accessToken, string exhibitorId, CancellationToken ct = default)
    {
        var url = $"{_options.ApiDomain}/backstage/v3/portals/{_options.BackstagePortalId}"
            + $"/events/{_options.BackstageEventId}/exhibitors/{exhibitorId}/members";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("Authorization", $"Zoho-oauthtoken {accessToken}");
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return Array.Empty<BackstageBoothMember>();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var list = new List<BackstageBoothMember>();
        if (doc.RootElement.TryGetProperty("members", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in arr.EnumerateArray())
            {
                var contact = m.TryGetProperty("contact", out var c) ? c : default;
                var email = Lower(GetString(contact, "email"));
                if (email.Length == 0) continue;
                list.Add(new BackstageBoothMember(
                    Id: FirstNonEmpty(GetString(m, "id"), GetString(m, "member_id")),
                    Email: email,
                    FirstName: GetString(contact, "first_name"),
                    LastName: GetString(contact, "last_name"),
                    Role: GetString(m, "role")));
            }
        }
        return list;
    }

    /// <summary>
    /// DELETE a single booth MEMBER from an exhibitor (REQUIREMENTS §41a/§56 — member
    /// delete only; NEVER the exhibitor/sponsor RECORD). Zoho now supports this:
    /// <c>DELETE …/exhibitors/{exhibitorId}/members/{memberId}</c> → 200
    /// <c>{"status":"success"}</c>. Scope: <c>ZohoBackstage.exhibitor.DELETE</c>.
    /// Returns true on a 2xx whose body reports success (or a 2xx with no parseable
    /// status); logs the status + body and returns false otherwise. Never throws on a
    /// non-2xx — the caller (hub delete) must stay fail-soft.
    /// </summary>
    public async Task<bool> DeleteBoothMemberAsync(
        string accessToken, string exhibitorId, string memberId, CancellationToken ct = default)
    {
        if (!await MayWriteAsync(nameof(DeleteBoothMemberAsync), ct)) return false;
        if (string.IsNullOrWhiteSpace(exhibitorId) || string.IsNullOrWhiteSpace(memberId)) return false;

        var url = $"{_options.ApiDomain}/backstage/v3/portals/{_options.BackstagePortalId}"
            + $"/events/{_options.BackstageEventId}/exhibitors/{exhibitorId}/members/{memberId}";
        using var req = new HttpRequestMessage(HttpMethod.Delete, url);
        req.Headers.Add("Authorization", $"Zoho-oauthtoken {accessToken}");
        using var resp = await _http.SendAsync(req, ct);

        string body; try { body = await resp.Content.ReadAsStringAsync(ct); } catch { body = string.Empty; }

        if (!resp.IsSuccessStatusCode)
        {
            _log?.LogWarning("Zoho DeleteBoothMember {Exhibitor}/{Member} failed: HTTP {Status} — {Body}",
                exhibitorId, memberId, (int)resp.StatusCode, body.Length > 300 ? body[..300] : body);
            return false;
        }

        // 2xx — accept {"status":"success"}; also accept a 2xx with no/empty/unparseable
        // body (some Zoho deletes answer 200/204 with no JSON), but reject an explicit
        // non-success status in the body.
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("status", out var st)
                    && st.ValueKind == JsonValueKind.String
                    && !string.Equals(st.GetString(), "success", StringComparison.OrdinalIgnoreCase))
                {
                    _log?.LogWarning("Zoho DeleteBoothMember {Exhibitor}/{Member}: HTTP {Status} but body status='{BodyStatus}' — {Body}",
                        exhibitorId, memberId, (int)resp.StatusCode, st.GetString(), body.Length > 300 ? body[..300] : body);
                    return false;
                }
            }
            catch { /* non-JSON 2xx body — treat the 2xx as success */ }
        }
        return true;
    }

    /// <summary>
    /// 🔴 §793 — what Zoho actually did with each requested member. <b>A 200 is NOT "all created".</b>
    /// </summary>
    /// <param name="Ok">The HTTP call itself succeeded.</param>
    /// <param name="Skipped">
    /// The addresses Zoho REFUSED, each with the reason IT gave — read from the response body's
    /// <c>skipped_emails</c>. Empty when everything was created.
    /// </param>
    public sealed record BoothMemberCreateResult(
        bool Ok, IReadOnlyList<(string Email, string Reason)> Skipped)
    {
        /// <summary>True only when the call worked AND Zoho refused nobody.</summary>
        public bool AllCreated => Ok && Skipped.Count == 0;
    }

    /// <summary>
    /// POST create booth members in bulk for an exhibitor.
    /// Body: <c>{ members: [ { role, first_name, last_name, email, company_name } ] }</c>
    /// (role = "ADMIN" | "staff"). Scope: <c>ZohoBackstage.exhibitor.CREATE</c>.
    /// </summary>
    /// <remarks>
    /// <para>🔴 <b>§793 — THIS RETURNED A BARE <c>IsSuccessStatusCode</c> AND THAT LOST REAL DATA.</b>
    /// Measured against PROD 2026-08-04, creating a member on an exhibitor at its limit:</para>
    /// <code>
    /// HTTP 200
    /// {"members":[],"skipped_emails":[{"email":"…","reason":
    ///   "The booth member limit has been reached. To add more, please modify the Exhibitor Benefits."}]}
    /// </code>
    /// <para>⇒ 200 with an EMPTY members array and a precise, human-readable refusal. The old code
    /// read that as success, the caller stamped <c>SyncedToZoho = true</c>, and the member never
    /// existed in Zoho — with nobody told, ever. Same shape as §784.13's discarded social pages,
    /// except here <b>Zoho explained itself and we threw the explanation away</b>.</para>
    ///
    /// <para>🔒 So the refusals are parsed and returned. A caller must check
    /// <see cref="BoothMemberCreateResult.AllCreated"/>, never just <c>Ok</c>.</para>
    /// </remarks>
    public async Task<BoothMemberCreateResult> CreateBoothMembersAsync(
        string accessToken, string exhibitorId,
        IReadOnlyList<(string FirstName, string LastName, string Email, string Role, string? CompanyName)> members,
        CancellationToken ct = default)
    {
        var none = Array.Empty<(string, string)>();
        if (!await MayWriteAsync(nameof(CreateBoothMembersAsync), ct)) return new(false, none);
        if (members.Count == 0) return new(true, none);
        var url = $"{_options.ApiDomain}/backstage/v3/portals/{_options.BackstagePortalId}"
            + $"/events/{_options.BackstageEventId}/exhibitors/{exhibitorId}/members";
        var payload = new Dictionary<string, object?>
        {
            ["members"] = members.Select(m => new Dictionary<string, object?>
            {
                ["role"] = m.Role,
                ["first_name"] = m.FirstName,
                ["last_name"] = m.LastName,
                ["email"] = m.Email,
                ["company_name"] = m.CompanyName ?? string.Empty,
            }).ToList(),
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = System.Net.Http.Json.JsonContent.Create(payload),
        };
        req.Headers.Add("Authorization", $"Zoho-oauthtoken {accessToken}");
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return new(false, none);

        // §793 — read WHO Zoho refused and WHY. Parsing is FAIL-SOFT: an unreadable body must not
        // turn a successful create into a reported failure, so it degrades to "created everything",
        // which is exactly what this method assumed unconditionally before.
        var skipped = new List<(string Email, string Reason)>();
        try
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("skipped_emails", out var arr)
                && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    var email = GetString(el, "email");
                    var reason = GetString(el, "reason");
                    if (email.Length > 0) skipped.Add((email, reason));
                }
            }
        }
        catch { /* fail-soft — see above */ }

        if (skipped.Count > 0)
        {
            _log?.LogWarning(
                "Zoho REFUSED {Count} booth member(s) on exhibitor {Exhibitor} despite HTTP 200: {Detail}",
                skipped.Count, exhibitorId,
                string.Join("; ", skipped.Select(s => $"{s.Email} — {s.Reason}")));
        }

        return new(true, skipped);
    }

    /// <summary>
    /// The Zoho access token — from the shared cache when one is still valid, otherwise by
    /// exchanging the refresh token.
    ///
    /// <para>§525: this has <b>27 call sites</b> and used to mint a NEW token on every one of them,
    /// though a token lasts about an hour. Zoho rate-limits refresh grants, so the app tripped the
    /// limit in bursts and every Zoho sync went dark while the same credential worked perfectly
    /// when tried once by hand. The cache is a singleton because this client is created
    /// per-resolution; when it is not wired (tests, legacy constructions) behaviour is unchanged.</para>
    /// </summary>
    public async Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
    {
        // 🔒 §783.12b — the DEV cut-off. Refused BEFORE the cache and before any HTTP, so a blocked
        // host never spends a single request from the shared 10-per-10-minutes token budget.
        //
        // Logged at Warning, not swallowed: per §335 ("the only symptom is that nothing happens"), a
        // blocked integration must SAY it is blocked. On DEV this line is the expected, correct
        // state; seeing it in PROD means Integrations:AllowExternalWrites is missing there.
        if (!HostMayReachZoho)
        {
            _log?.LogWarning(
                "Zoho BLOCKED for this host — no access token requested. "
                + "Integrations:AllowExternalWrites is false, so this environment may not reach "
                + "Zoho at all (§783.12b: DEV and PROD share ONE refresh token, and Zoho meters "
                + "token requests at 10 per 10 minutes — DEV spending them is what 401s PROD). "
                + "Expected in DEV; in PROD it means the app setting is missing.");
            return null;
        }

        if (_tokenCache is null) return (await RefreshAccessTokenAsync(ct)).Token;
        return await _tokenCache.GetAsync(RefreshAccessTokenAsync, ct);
    }

    /// <summary>
    /// §525 — the real refresh-token exchange, always a live call. Returns Zoho's
    /// <c>expires_in</c> alongside the token so the cache knows when to renew.
    /// </summary>
    private async Task<ZohoTokenResult> RefreshAccessTokenAsync(CancellationToken ct)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["refresh_token"] = _options.RefreshToken,
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
            ["grant_type"] = "refresh_token",
        });

        using var resp = await _http.PostAsync(_options.TokenEndpoint, form, ct);

        // §524 — READ THE BODY EITHER WAY. Zoho's token endpoint returns **HTTP 200 with
        // {"error":"invalid_code"}** for a revoked/expired refresh token, so a status check alone
        // sees a healthy response and then silently finds no access_token. Both routes previously
        // returned a bare null, so every caller could say only "No Zoho access token (token
        // refresh failed)" with no cause recorded anywhere — the entire Zoho integration went dark
        // with nothing to act on.
        var body = string.Empty;
        try { body = await resp.Content.ReadAsStringAsync(ct); } catch { /* diagnostics only */ }

        string? accessToken = null;
        var expiresIn = 0;
        if (resp.IsSuccessStatusCode && body.Length > 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("access_token", out var t))
                    accessToken = t.GetString();
                // §525: Zoho reports the lifetime in SECONDS as expires_in (3600). Some responses
                // type it as a string, so accept either rather than silently falling back.
                if (doc.RootElement.TryGetProperty("expires_in", out var e))
                {
                    expiresIn = e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n) ? n
                        : e.ValueKind == JsonValueKind.String && int.TryParse(e.GetString(), out var s) ? s
                        : 0;
                }
            }
            catch (JsonException) { /* not JSON — handled as a failure below */ }
        }

        if (!string.IsNullOrEmpty(accessToken))
            return new ZohoTokenResult(accessToken, expiresIn);

        await ReportTokenFailureAsync((int)resp.StatusCode, body, ct);
        return new ZohoTokenResult(null, 0);
    }

    /// <summary>
    /// §524 — log AND e-mail the operator when the Zoho credential stops working.
    ///
    /// <para>Operator 2026-07-28: <i>"the service should fix this, otherwise i need to have an
    /// email with this kind of error immediately. dont do any tricks and try manually.
    /// service/background job must handle it"</i>. A dead refresh token cannot self-heal — it needs
    /// re-consent — so the service's job is to say so LOUDLY and immediately, rather than let every
    /// Zoho job fail quietly and be discovered days later on a queue page.</para>
    ///
    /// <para>Ring-exempt via <c>EngineAlertSender</c> (an ops alert must never be ring-dropped),
    /// and throttled on a stable key so a job retrying every 5 minutes cannot flood the inbox.
    /// The body carries no secret — the credentials are in the REQUEST; this is Zoho's own error
    /// response.</para>
    /// </summary>
    private async Task ReportTokenFailureAsync(int status, string body, CancellationToken ct)
    {
        var snippet = body.Length > 400 ? body[..400] : body;

        // The three causes that look identical without the body.
        var cause =
            snippet.Contains("invalid_code", StringComparison.OrdinalIgnoreCase)
                ? "The REFRESH TOKEN is revoked or expired — re-consent in Zoho and set a new Zoho__RefreshToken."
            : snippet.Contains("invalid_client", StringComparison.OrdinalIgnoreCase)
                ? "Zoho__ClientId / Zoho__ClientSecret no longer match the Zoho app."
            : snippet.TrimStart().StartsWith("<", StringComparison.Ordinal)
                ? "An HTML page came back — the TokenEndpoint is pointing at the wrong Zoho data centre."
                : "Unrecognised response — see the body below.";

        _log?.LogError(
            "Zoho token refresh FAILED: HTTP {Status} from {Endpoint} — {Body}. {Cause} "
            + "Every Zoho sync (agenda push, attendee mirror, order pull) is BLOCKED until this is fixed.",
            status, _options.TokenEndpoint, snippet, cause);

        if (_alerts is null) return;
        try
        {
            // NOTE: a dead refresh token is the ONE Zoho fault that cannot self-heal — OAuth only
            // reissues one through re-consent. Everything else in this integration does self-heal
            // (see §301b: a stale Backstage id is nulled and the record re-created). So the
            // service's job here is to fail LOUDLY and immediately, which is what this mail is.
            var html =
                "<p><b>The Zoho connection is down.</b> The hub cannot obtain an access token, so "
                + "<b>every Zoho sync is blocked</b> — the agenda/session push, the attendee mirror "
                + "and the order pull all fail until this is fixed.</p>"
                + $"<p><b>What to do:</b> {System.Net.WebUtility.HtmlEncode(cause)}</p>"
                + $"<p>HTTP {status} from <code>{System.Net.WebUtility.HtmlEncode(_options.TokenEndpoint)}</code></p>"
                + $"<pre>{System.Net.WebUtility.HtmlEncode(snippet)}</pre>"
                + "<p>This cannot self-heal: a refresh token is only reissued by re-consenting in Zoho.</p>";

            await _alerts.AlertAsync(
                "Zoho connection DOWN — all Zoho sync blocked [ELDK27]",
                html, ct, throttleKey: "zoho-token-refresh-failed");
        }
        catch (Exception ex)
        {
            // An alert that throws must never take the caller down with it.
            _log?.LogWarning(ex, "Zoho token-failure alert could not be sent.");
        }
    }

    /// <summary>
    /// Fetch the full Backstage ORDER dataset (v3 /orders) — one row per order with its
    /// buyer/billing, Zoho status string, source created time and the full raw JSON
    /// (REQUIREMENTS §125). This is the order half of the authoritative one-way Zoho→CEH
    /// mirror; <see cref="GetBackstageAttendeesAsync"/> returns the ticket/attendee half
    /// (joined back to these orders by order id). Read-only — CEH never writes Zoho.
    /// </summary>
    public async Task<IReadOnlyList<BackstageOrder>> GetBackstageOrdersAsync(
        string accessToken, CancellationToken ct = default)
    {
        var list = new List<BackstageOrder>();
        // §326ao: STRICT pager. This feeds the FULL-dataset reconcile, which soft-cancels
        // every local row it does not see — so a silently truncated read (HTTP 401/429/5xx,
        // or a failure on page 3 of 5) would cancel real orders. Throw instead; the caller
        // aborts and the next run (10 min) reconciles from a complete read.
        await foreach (var order in StrictPageV3Async("orders", "orders", accessToken, ct))
        {
            var id = GetString(order, "id");
            if (id.Length == 0) continue;

            var billing = order.TryGetProperty("billing_address", out var b) ? b : default;
            string? country = null, code = null;
            if (billing.ValueKind == JsonValueKind.Object
                && billing.TryGetProperty("country_data", out var cd) && cd.ValueKind == JsonValueKind.Object)
            { country = NullIf(GetString(cd, "display_name")); code = NullIf(GetString(cd, "code")); }
            var contact = order.TryGetProperty("contact", out var oc) ? oc : default;

            DateTimeOffset? created = null;
            if (DateTimeOffset.TryParse(GetString(order, "created_time"), out var dto)) created = dto;

            list.Add(new BackstageOrder(
                OrderId: id,
                BuyerName: NullIf(GetString(billing, "name")) ?? NullIf(GetString(contact, "name")),
                BuyerEmail: NullIf(Lower(GetString(contact, "email"))),
                CompanyName: NullIf(GetString(contact, "company_name")) ?? NullIf(GetString(billing, "company_name")),
                Country: country ?? NullIf(GetString(billing, "country")),
                CountryCode: code,
                City: NullIf(GetString(billing, "city")),
                Postcode: NullIf(GetString(billing, "zipcode")),
                TaxId: NullIf(GetString(contact, "tax_registration_no")),
                OrderStatus: NullIf(FirstNonEmpty(GetString(order, "status"), GetString(order, "status_string"))),
                SourceCreatedAt: created,
                RawJson: order.GetRawText()));
        }
        return list;
    }

    /// <summary>
    /// Fetch all Backstage attendees (v3) — one enriched row per ticket: the stable
    /// ticket id, contact details + ALL custom fields, and the order's company /
    /// country / tax (joined by order id). The source of truth for the attendee +
    /// Master Class flow (keyed on ticket id).
    /// </summary>
    public async Task<IReadOnlyList<BackstageAttendee>> GetBackstageAttendeesAsync(
        string accessToken, CancellationToken ct = default)
    {
        // Known contact keys — everything else on the contact is a CUSTOM field.
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "first_name", "last_name", "email", "company_name", "designation", "mobile_no" };

        // 1. Orders -> billing/company/country/tax by order id.
        var orderInfo = new Dictionary<string, (string? Company, string? Country, string? CountryCode, string? City, string? Postcode, string? Tax)>(StringComparer.Ordinal);
        // §326ao: STRICT on both reads — see GetBackstageOrdersAsync. A partial ATTENDEE
        // read is the dangerous one (it soft-cancels tickets, releases Master Class seats
        // and fires waitlist promotion mail), and a partial ORDER read silently drops the
        // company/country enrichment for every attendee on the missing pages.
        await foreach (var order in StrictPageV3Async("orders", "orders", accessToken, ct))
        {
            var id = GetString(order, "id");
            if (id.Length == 0) continue;
            var billing = order.TryGetProperty("billing_address", out var b) ? b : default;
            string? country = null, code = null;
            if (billing.ValueKind == JsonValueKind.Object && billing.TryGetProperty("country_data", out var cd) && cd.ValueKind == JsonValueKind.Object)
            { country = NullIf(GetString(cd, "display_name")); code = NullIf(GetString(cd, "code")); }
            var contact = order.TryGetProperty("contact", out var oc) ? oc : default;
            orderInfo[id] = (
                NullIf(GetString(billing, "name")),
                country ?? NullIf(GetString(billing, "country")),
                code,
                NullIf(GetString(billing, "city")),
                NullIf(GetString(billing, "zipcode")),
                NullIf(GetString(contact, "tax_registration_no")));
        }

        // 2. Attendees -> one enriched row per ticket.
        var list = new List<BackstageAttendee>();
        await foreach (var a in StrictPageV3Async("attendees", "attendees", accessToken, ct))   // §326ao
        {
            var ticketId = FirstNonEmpty(GetString(a, "ticket_id"), GetString(a, "id"));
            if (ticketId.Length == 0) continue;
            var orderId = GetString(a, "order_id");
            var contact = a.TryGetProperty("contact", out var c) ? c : default;

            string? customJson = null;
            if (contact.ValueKind == JsonValueKind.Object)
            {
                var custom = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var prop in contact.EnumerateObject())
                    if (!known.Contains(prop.Name) && prop.Value.ValueKind == JsonValueKind.String)
                        custom[prop.Name] = prop.Value.GetString() ?? "";
                if (custom.Count > 0) customJson = JsonSerializer.Serialize(custom);
            }

            orderInfo.TryGetValue(orderId, out var oi);
            var statusStr = GetString(a, "status_string");
            list.Add(new BackstageAttendee(
                TicketId: ticketId,
                OrderId: orderId,
                Email: Lower(GetString(contact, "email")),
                FirstName: GetString(contact, "first_name"),
                LastName: GetString(contact, "last_name"),
                TicketClassName: FirstNonEmpty(GetString(a, "ticket_name"), GetString(contact, "ticket_name")),
                Attending: string.Equals(statusStr, "attending", StringComparison.OrdinalIgnoreCase),
                StatusString: NullIf(statusStr),   // §326as
                CompanyName: NullIf(GetString(contact, "company_name")) ?? oi.Company,
                JobTitle: NullIf(GetString(contact, "designation")),
                Phone: NullIf(GetString(contact, "mobile_no")),
                Country: oi.Country, CountryCode: oi.CountryCode, City: oi.City, Postcode: oi.Postcode,
                TaxId: oi.Tax,
                CustomFieldsJson: customJson,
                CreatedTimeRaw: NullIf(GetString(a, "created_time")),
                // §447: the stable class id sits on the same ticket object as ticket_name.
                // Verified against a real PROD order: ticket_class_id "14880000003485482"
                // alongside ticket_name "2-day (Pre-day  + Main Event)".
                TicketClassId: NullIf(GetString(a, "ticket_class_id"))));
        }
        return list;
    }

    /// <summary>
    /// Fetch the CURRENT Zoho Backstage AGENDA sessions (REQUIREMENTS §38e) — one row
    /// per agenda session with its id, start/end and resolved hall/room name. This is
    /// the time/location SOURCE the §38e change-detection engine diffs against the CEH
    /// stored values.
    ///
    /// <b>AVAILABILITY (fail-soft).</b> It pulls halls → id→name, then sessions per agenda day, and
    /// resolves each session's hall id to a room name (shape per
    /// <see cref="Sessions.BackstageSessionParser"/>). A read FAILURE yields
    /// <see cref="BackstageSessionsResult.Unavailable"/> and NEVER fakes data, so the engine no-ops
    /// instead of mistaking an empty pull for "all sessions changed/were removed".
    /// <para>🗑 §754.5 — no longer gated on a config flag. The Backstage credentials carry the
    /// agenda permission and always have; the real gate is the <c>session-change-alerts</c> feature
    /// switch. See the note on <see cref="ZohoOptions"/>.</para>
    /// </summary>
    /// <summary>
    /// Live session-id set (§301b self-heal): every session id currently in the Backstage agenda,
    /// across all agenda days. The stage-2 push
    /// engines use it to detect a stored id whose record was DELETED in the Backstage UI
    /// (NULL the link and re-create). ⚠ FAIL-SAFE CONTRACT: an EMPTY set is
    /// indistinguishable from a failed read — callers MUST skip healing on an empty set,
    /// never treat it as "everything was deleted".
    /// </summary>
    public async Task<HashSet<string>> GetLiveSessionIdSetAsync(
        string accessToken, CancellationToken ct = default) =>
        (await GetLiveSessionMapAsync(accessToken, ct)).Keys
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>One LIVE Backstage session's diff-relevant fields (§302/§302c
    /// change-detection): title, start, duration minutes, the venue (hall) id, the
    /// track id, the description and the tag names (both GuiOnly — readable here,
    /// unwritable via the API; the ACTION mail is their only channel).</summary>
    public sealed record LiveSession(
        string Id, string? Title, DateTimeOffset? StartTime, int? DurationMinutes,
        string? VenueId, string? TrackId,
        string? Description = null, IReadOnlyList<string>? Tags = null);

    /// <summary>
    /// UNGATED live session map by id (§301b self-heal + §302 change-detection): every
    /// session currently in the Backstage agenda with its diff-relevant fields, across
    /// all agenda days. STRICT reads: unlike <see cref="PageV3Async"/> (which silently
    /// stops on an HTTP failure), ANY failed page THROWS — a silently-partial set would
    /// make the self-heal declare live sessions "deleted" and re-create them as
    /// DUPLICATES (Zoho refuses duplicate speaker e-mails, but happily duplicates
    /// sessions). ⚠ FAIL-SAFE CONTRACT: an EMPTY map is indistinguishable from a failed
    /// read — callers MUST skip healing/diffing on an empty map.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, LiveSession>> GetLiveSessionMapAsync(
        string accessToken, CancellationToken ct = default)
    {
        var map = new Dictionary<string, LiveSession>(StringComparer.OrdinalIgnoreCase);
        // The sessions endpoint REQUIRES ?day= (1-based); enumerate the agenda days first.
        var dayCount = 0;
        await foreach (var a in StrictPageV3Async("agendas", "agendas", accessToken, ct)) dayCount++;
        var maxDay = dayCount > 0 ? dayCount : 10;
        for (var day = 1; day <= maxDay; day++)
        {
            var any = false;
            await foreach (var s in StrictPageV3Async($"sessions?day={day}", "sessions", accessToken, ct))
            {
                any = true;
                var id = GetString(s, "id");
                if (id.Length == 0) continue;
                int? dur = null;
                if (s.TryGetProperty("duration", out var d))
                {
                    if (d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out var di)) dur = di;
                    else if (d.ValueKind == JsonValueKind.String && int.TryParse(d.GetString(), out var ds)) dur = ds;
                }
                // §302c: tags may be absent (no tags set), an array of strings, or an
                // array of objects carrying name/tag_name — tolerate all shapes.
                var tags = new List<string>();
                if (s.TryGetProperty("tags", out var tg) && tg.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in tg.EnumerateArray())
                    {
                        var name = t.ValueKind == JsonValueKind.String
                            ? t.GetString()
                            : t.ValueKind == JsonValueKind.Object
                                ? FirstNonEmpty(GetString(t, "name"), GetString(t, "tag_name"), GetString(t, "label"))
                                : null;
                        if (!string.IsNullOrWhiteSpace(name)) tags.Add(name!.Trim());
                    }
                }
                map[id] = new LiveSession(
                    Id: id,
                    Title: NullIf(GetString(s, "title")),
                    StartTime: ParseTimeAny(s, "start_time", "startTime", "startsAt", "startsOn"),
                    DurationMinutes: dur,
                    VenueId: NullIf(FirstNonEmpty(GetString(s, "venue"), GetString(s, "hallId"), GetString(s, "hall"))),
                    TrackId: NullIf(GetString(s, "track")),
                    Description: NullIf(GetString(s, "description")),
                    Tags: tags);
            }
            if (dayCount == 0 && !any) break;   // probing mode: stop at the first empty day
        }
        return map;
    }

    /// <summary>
    /// §754 — ONE activity on the Backstage agenda, as the SIGNAGE screens need it: the card's
    /// four lines plus the fields the hour-slot logic runs on. Room/track/speakers arrive here
    /// RESOLVED to display names, not as the ids the raw session carries.
    /// </summary>
    public sealed record BackstageAgendaActivity(
        string SessionId, string Title, DateTimeOffset? StartsAt, int? DurationMinutes,
        string? Room, string? Track, string? ActivityType,
        IReadOnlyList<string> Speakers, int DayIndex);

    /// <summary>
    /// §754 — pull the COMPLETE Backstage agenda for signage: every activity on every agenda day,
    /// including the breaks, registration, lunch and party that CEH's own sessions table does not
    /// model. Halls, tracks and speakers are resolved to display names here so the caller stores
    /// text a screen can print.
    /// </summary>
    /// <remarks>
    /// <para>🔒 <b>No scope gate, because there was never a missing scope.</b> The Backstage
    /// credentials carry every permission CEH needs (§754.5). The flags that once claimed otherwise
    /// are deleted — do not add another. Evidence, if it is ever doubted again: the §301b self-heal
    /// reads <c>/agendas</c> + <c>/sessions?day=N</c> on the strict path, where a 401 THROWS, and has
    /// done so in production for months; the §585 matrix live-probed
    /// <c>agendas/sessions/tracks/halls/speakers</c> at 200 against PROD.</para>
    ///
    /// <para>🔒 <b>STRICT reads throughout: any failed page THROWS.</b> The caller's fail-safe
    /// depends on it. A silently-partial agenda is worse here than no agenda at all — the sync would
    /// delete every activity the failed page would have carried, and 15 screens would confidently
    /// display a half-empty conference. The non-strict pager <c>yield break</c>s on a non-2xx, which
    /// is exactly the §585 silent-empty-list failure; it must never be used for this.</para>
    ///
    /// <para>⚠️ <b>An unresolvable speaker e-mail is DROPPED, never printed.</b> The session
    /// <c>speakers</c> array may hold ids, e-mails or objects. Ids and e-mails are resolved against
    /// the <c>/speakers</c> index; anything left that looks like an e-mail address is discarded
    /// rather than shown, because these cards render two metres tall in a public corridor and a
    /// leaked personal e-mail cannot be recalled from a photograph.</para>
    /// </remarks>
    public async Task<IReadOnlyList<BackstageAgendaActivity>> GetBackstageAgendaAsync(
        string accessToken, CancellationToken ct = default)
    {
        // 1. Lookups first — a session references its hall, track and speakers by id.
        var halls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var h in StrictPageV3Async("halls", "halls", accessToken, ct))
        {
            var id = FirstNonEmpty(GetString(h, "id"), GetString(h, "hall_id"));
            var name = FirstNonEmpty(GetString(h, "name"), GetString(h, "title"));
            if (id.Length > 0 && name.Length > 0) halls[id] = name;
        }

        var tracks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var t in StrictPageV3Async("tracks", "tracks", accessToken, ct))
        {
            var id = FirstNonEmpty(GetString(t, "track_id"), GetString(t, "id"));
            var name = FirstNonEmpty(GetString(t, "name"), GetString(t, "title"));
            if (id.Length > 0 && name.Length > 0) tracks[id] = name;
        }

        // Speakers are indexed by BOTH id and lower-cased e-mail, because the session's
        // `speakers` array has been observed carrying either and the create endpoint takes
        // e-mails. One index, two key spaces, so the resolve never depends on which it is.
        var speakerNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var s in StrictPageV3Async("speakers", "speakers", accessToken, ct))
        {
            var name = $"{GetString(s, "name")} {GetString(s, "last_name")}".Trim();
            if (name.Length == 0) continue;
            var id = GetString(s, "id");
            if (id.Length > 0) speakerNames[id] = name;
            foreach (var f in new[] { "email", "email_address" })
            {
                var mail = GetString(s, f);
                if (mail.Length > 0) speakerNames[mail.Trim()] = name;
            }
        }

        // 2. Agenda days. /sessions REQUIRES ?day= (1-based); enumerate /agendas to learn how
        //    many there are. Unlike the probing fallback elsewhere, a day count of zero here is
        //    a genuine "this event has no agenda" and yields an empty list — the caller's
        //    fail-safe decides what that means, not this method.
        var dayCount = 0;
        await foreach (var _ in StrictPageV3Async("agendas", "agendas", accessToken, ct)) dayCount++;

        // 3. The activities themselves.
        var list = new List<BackstageAgendaActivity>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var day = 1; day <= dayCount; day++)
        {
            await foreach (var s in StrictPageV3Async($"sessions?day={day}", "sessions", accessToken, ct))
            {
                var id = GetString(s, "id");
                if (id.Length == 0 || !seen.Add(id)) continue;

                int? duration = null;
                if (s.TryGetProperty("duration", out var d))
                {
                    if (d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out var di)) duration = di;
                    else if (d.ValueKind == JsonValueKind.String && int.TryParse(d.GetString(), out var ds)) duration = ds;
                }

                var hallId = FirstNonEmpty(GetString(s, "venue"), GetString(s, "hallId"), GetString(s, "hall"));
                var trackId = GetString(s, "track");

                list.Add(new BackstageAgendaActivity(
                    SessionId: id,
                    Title: FirstNonEmpty(GetString(s, "title"), GetString(s, "name")),
                    StartsAt: ParseTimeAny(s, "start_time", "startTime", "startsAt", "startsOn"),
                    DurationMinutes: duration,
                    Room: hallId.Length > 0 && halls.TryGetValue(hallId, out var room) ? room : null,
                    // An UNRESOLVED track id is dropped rather than printed: "4823901000000123456"
                    // on a wall is worse than a card with no track line.
                    Track: trackId.Length > 0 && tracks.TryGetValue(trackId, out var tr) ? tr : null,
                    ActivityType: NullIf(GetString(s, "session_type")),
                    Speakers: ResolveSpeakerNames(s, speakerNames),
                    DayIndex: day));
            }
        }
        return list;
    }

    /// <summary>
    /// §754 — turn a session's <c>speakers</c> array into display names. Tolerates the three shapes
    /// the field has been seen in (id strings, e-mail strings, objects) and drops anything that
    /// cannot be resolved to a human name.
    /// </summary>
    /// <remarks>
    /// ⚠️ An e-mail that resolves to no speaker record is DISCARDED, not shown. Everything that
    /// reaches here is printed on a public screen, so "unknown" must fail to blank rather than to
    /// raw data. A non-e-mail string that resolves to nothing IS kept — Backstage has been seen
    /// returning plain names, and dropping those would silently empty the speaker line.
    /// </remarks>
    private static IReadOnlyList<string> ResolveSpeakerNames(
        JsonElement session, IReadOnlyDictionary<string, string> index)
    {
        var names = new List<string>();
        if (!session.TryGetProperty("speakers", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return names;

        foreach (var el in arr.EnumerateArray())
        {
            string? name = null;
            if (el.ValueKind == JsonValueKind.String)
            {
                var raw = (el.GetString() ?? string.Empty).Trim();
                if (raw.Length == 0) continue;
                if (index.TryGetValue(raw, out var hit)) name = hit;
                else if (!raw.Contains('@')) name = raw;   // a plain name, not an identifier
            }
            else if (el.ValueKind == JsonValueKind.Object)
            {
                var id = GetString(el, "id");
                var mail = FirstNonEmpty(GetString(el, "email"), GetString(el, "email_address"));
                var composed = $"{GetString(el, "name")} {GetString(el, "last_name")}".Trim();
                if (id.Length > 0 && index.TryGetValue(id, out var byId)) name = byId;
                else if (mail.Length > 0 && index.TryGetValue(mail, out var byMail)) name = byMail;
                else if (composed.Length > 0 && !composed.Contains('@')) name = composed;
                else name = NullIf(FirstNonEmpty(GetString(el, "full_name"), GetString(el, "title")));
            }

            if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name!, StringComparer.OrdinalIgnoreCase))
                names.Add(name!.Trim());
        }
        return names;
    }

    /// <summary>
    /// STRICT variant of <see cref="PageV3Async"/> for the §301b self-heal reads: an HTTP
    /// failure on ANY page THROWS instead of silently ending the enumeration, so callers
    /// can never mistake a partial read for the complete live state.
    /// </summary>
    private async IAsyncEnumerable<JsonElement> StrictPageV3Async(
        string resource, string arrayProp, string accessToken,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var el in PageV3CoreAsync(resource, arrayProp, accessToken, strict: true, ct))
            yield return el;
    }

    public async Task<BackstageSessionsResult> GetBackstageSessionsAsync(
        string accessToken, CancellationToken ct = default)
    {
        // 🗑 §754.5 — the "the agenda scope is not on the token yet" early return is GONE. It was
        // never true; the credentials have always had the permission. See the note on ZohoOptions.

        // 1. Halls → id → display name (rooms are "halls" in Backstage; a session
        //    references its hall via the `venue` field). Tolerant of id/name field aliases.
        var halls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var h in PageV3Async("halls", "halls", accessToken, ct))
        {
            var id = GetString(h, "id");
            var name = FirstNonEmpty(GetString(h, "name"), GetString(h, "title"));
            if (id.Length > 0 && name.Length > 0) halls[id] = name;
        }

        // 2. Agenda DAYS. The Backstage v3 sessions endpoint REQUIRES a ?day= index —
        //    GET /sessions with no day returns HTTP 400 ("Please enter the valid agenda
        //    day"), which the old code swallowed → 0 sessions (the bug). Enumerate the
        //    agenda days first, then pull sessions per day and aggregate. The /agendas
        //    objects carry a 0-based `index`; the /sessions query is 1-based (day=0 is
        //    empty), so we query days 1..N where N = the number of agenda days. If the
        //    agendas list can't be read we fall back to probing days 1.. until empty.
        var dayCount = 0;
        await foreach (var _ in PageV3Async("agendas", "agendas", accessToken, ct)) dayCount++;

        // 3. Sessions → flattened rows, hall (venue) id resolved to a room name, end
        //    derived from start + duration (Backstage gives `duration` minutes, no end).
        var list = new List<BackstageSession>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // When /agendas returned nothing, probe up to a small bounded number of days
        // (stop on the first empty day) so the pull still works if that call is gated.
        var maxDay = dayCount > 0 ? dayCount : 50;
        for (var day = 1; day <= maxDay; day++)
        {
            var any = false;
            await foreach (var s in PageV3Async($"sessions?day={day}", "sessions", accessToken, ct))
            {
                any = true;
                var id = GetString(s, "id");
                if (id.Length == 0 || !seen.Add(id)) continue;
                // `venue` is the hall reference (currently null until rooms are assigned);
                // keep the legacy aliases too. Resolve to a room name via the halls map.
                var hallId = FirstNonEmpty(GetString(s, "venue"), GetString(s, "hallId"), GetString(s, "hall"));
                var room = hallId.Length > 0 && halls.TryGetValue(hallId, out var n) ? n : null;
                var start = ParseTimeAny(s, "start_time", "startTime", "startsAt", "startsOn");
                list.Add(new BackstageSession(
                    SessionId: id,
                    StartsAt: start,
                    // Backstage has no end_time — derive from start + duration (minutes).
                    EndsAt: ParseTimeAny(s, "end_time", "endTime", "endsAt", "endsOn")
                            ?? AddDurationMinutes(start, s, "duration"),
                    Room: room,
                    Title: NullIf(FirstNonEmpty(GetString(s, "title"), GetString(s, "name")))));
            }
            // When we're probing (no agenda count), stop at the first empty day.
            if (dayCount == 0 && !any) break;
        }
        return BackstageSessionsResult.Available(list);
    }

    /// <summary>start + duration-minutes (when both are present), else null. Backstage v3
    /// returns a session <c>duration</c> in minutes and NO explicit end time.</summary>
    private static DateTimeOffset? AddDurationMinutes(DateTimeOffset? start, JsonElement e, string name)
    {
        if (start is null) return null;
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v))
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var mins) && mins > 0)
                return start.Value.AddMinutes(mins);
            if (v.ValueKind == JsonValueKind.String
                && int.TryParse(v.GetString(), out var m) && m > 0)
                return start.Value.AddMinutes(m);
        }
        return null;
    }

    private static DateTimeOffset? ParseTimeAny(JsonElement e, params string[] names)
    {
        foreach (var n in names)
        {
            if (e.ValueKind == JsonValueKind.Object
                && e.TryGetProperty(n, out var v)
                && v.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(v.GetString(), out var dto))
            {
                return dto;
            }
        }
        return null;
    }

    /// <summary>Enumerate a paginated v3 Backstage collection, following pagination.nextPage.
    /// <paramref name="resource"/> may carry a query string (e.g. <c>"sessions?day=1"</c>);
    /// these flat agenda endpoints have no pagination wrapper so they return a single page.
    /// A non-success status (e.g. the 400 when ?day= is missing/out of range) yields nothing
    /// for that call rather than throwing, so the agenda day-loop is resilient.</summary>
    private async IAsyncEnumerable<JsonElement> PageV3Async(
        string resource, string arrayProp, string accessToken,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var el in PageV3CoreAsync(resource, arrayProp, accessToken, strict: false, ct))
            yield return el;
    }

    /// <summary>
    /// §326ar — the ONE place that knows how Zoho Backstage v3 paginates.
    ///
    /// <para>THE BUG THIS REPLACES (operator-verified against the ELDK26 event, 2026-07-25):
    /// the loop used to advance on <c>pagination.nextPage</c>, which is an <b>e-conomic</b>
    /// convention (see <c>EconomicErpClient</c>) — Zoho NEVER returns that property. The next
    /// URL was therefore always null and EVERY v3 read stopped after page one: 500 attendees,
    /// 100 orders, and whatever the first page holds for speakers/sponsors/exhibitors/booths/
    /// halls/sessions. Against ELDK26's real data that is 500 of 1204 attendees and 100 of 664
    /// orders. Because the attendee reconcile treats "not in the pull" as cancelled, crossing
    /// one page would have soft-cancelled everyone beyond it — releasing Master Class seats,
    /// mailing waitlist promotions and revoking logins — on a schedule, once ticket sales
    /// passed ~100 orders / ~500 attendees.</para>
    ///
    /// <para>Zoho's actual contract, confirmed live:
    /// <c>{"total_count":1204,"page":1,"per_page":500,"total_pages":3,"has_more_items":true}</c>
    /// with pages selected by a <c>page=N</c> query parameter.</para>
    ///
    /// <para><paramref name="strict"/> adds the guarantee the reconcile needs: any non-2xx page
    /// throws, AND the row count is checked against Zoho's own <c>total_count</c> at the end.
    /// That turns "did I read everything?" from a heuristic into a proof — a short read can no
    /// longer be mistaken for "these people are gone". Rows arriving DURING the read only ever
    /// make the collected count larger, so only a SHORTFALL is treated as a failure.</para>
    /// </summary>
    private async IAsyncEnumerable<JsonElement> PageV3CoreAsync(
        string resource, string arrayProp, string accessToken, bool strict,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var baseUrl = $"{_options.ApiDomain}/backstage/v3/portals/{_options.BackstagePortalId}/events/{_options.BackstageEventId}/{resource}";
        // A few callers already carry a query string (e.g. "sessions?day=1").
        var joiner = baseUrl.Contains('?') ? '&' : '?';

        var page = 1;
        var yielded = 0;
        int? totalCount = null;

        // §554 — NOT EVERY v3 RESOURCE ACCEPTS ?page. The AGENDA endpoints reject it outright:
        //     GET /sessions?day=2&page=1  →  400 {"message":"Extra param found"}
        //     GET /sessions?day=2         →  200, the full day
        // The agenda is paginated by DAY, not by page. Because the live-session read is STRICT
        // (any failed page throws, so a partial set can never look like "everything was deleted"),
        // that 400 threw on EVERY call — and the session self-heal caught it silently, so nine
        // sessions kept ids pointing at records that no longer existed and were never re-created.
        // Verified live against PROD 2026-07-28.
        var supportsPaging = !RejectsPageParam(resource);

        while (page <= MaxV3Pages)
        {
            var url = supportsPaging ? $"{baseUrl}{joiner}page={page}" : baseUrl;
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Authorization", $"Zoho-oauthtoken {accessToken}");
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                if (strict)
                    throw new HttpRequestException(
                        $"Zoho GET {resource} page {page} failed: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
                yield break;   // legacy fail-open (read-only callers only)
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                // A bare array carries no pagination envelope — one page by definition.
                foreach (var el in root.EnumerateArray()) { yielded++; yield return el.Clone(); }
                yield break;
            }

            if (root.TryGetProperty(arrayProp, out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var el in arr.EnumerateArray()) { yielded++; yield return el.Clone(); }

            var hasMore = false;
            if (root.TryGetProperty("pagination", out var p) && p.ValueKind == JsonValueKind.Object)
            {
                if (p.TryGetProperty("total_count", out var tc) && tc.TryGetInt32(out var tcv))
                    totalCount = tcv;
                hasMore = p.TryGetProperty("has_more_items", out var hm)
                          && hm.ValueKind == JsonValueKind.True;
                // Belt and braces: honour total_pages even if has_more_items is ever absent.
                if (!hasMore && p.TryGetProperty("total_pages", out var tp)
                             && tp.TryGetInt32(out var tpv) && page < tpv)
                    hasMore = true;
            }

            // §554 — a resource that rejects ?page has no pagination envelope either: one request
            // IS the whole set for that day, so stop rather than asking for a page it will refuse.
            if (!supportsPaging || !hasMore) break;
            page++;
        }

        // §326ar completeness proof — strict callers must never reconcile a short read.
        if (strict && totalCount is int expected && yielded < expected)
            throw new HttpRequestException(
                $"Zoho GET {resource} returned {yielded} rows but reported total_count {expected} "
                + "— refusing to treat an incomplete read as the live set.");
    }

    /// <summary>Safety stop for the page loop (Zoho's largest page size is 500).</summary>
    private const int MaxV3Pages = 500;

    /// <summary>
    /// §554 — the v3 resources that answer <c>400 {"message":"Extra param found"}</c> when a
    /// <c>page</c> parameter is present. The AGENDA family is paginated by <c>day</c>, not by page.
    ///
    /// <para>Kept as an explicit list rather than "retry without page on a 400", so the behaviour
    /// is predictable and a genuine 400 still surfaces as a failure instead of being retried into
    /// a different shape. Orders and attendees DO page — that is why the rule is per-resource
    /// (see the §326ao strict pager, which those callers depend on).</para>
    /// </summary>
    /// <summary>
    /// §585 — the v3 resources that REJECT <c>?page</c> with 400 "Extra param found".
    /// </summary>
    /// <remarks>
    /// 🔒 THIS LIST IS LIVE-MEASURED, NOT GUESSED. Probed against PROD 2026-07-28, bare GET vs
    /// <c>?page=1</c>:
    /// <code>
    ///   agendas    200 / 400  → REJECTS      booths      200 / 200  → pages
    ///   sessions   (needs ?day) / 400 → REJECTS   sponsors    200 / 200  → pages
    ///   tracks     200 / 400  → REJECTS      exhibitors  200 / 200  → pages
    ///   halls      200 / 400  → REJECTS      attendees   200 / 200  → pages
    ///   speakers   200 / 400  → REJECTS      orders      200 / 200  → pages
    /// </code>
    ///
    /// <para>❗ WHY THIS MATTERS SO MUCH: a rejected page is a 400, and the non-strict pager
    /// <c>yield break</c>s on any non-2xx — so a missing entry here turns a working endpoint into
    /// a SILENT EMPTY LIST. There is no error, no log, no failure count; the caller simply believes
    /// the resource is empty.</para>
    ///
    /// <para><b>halls</b> was missing, and that is the §585 bug the operator reported ("room was not
    /// added in zoho when synchronized from ceh"): <c>GetHallsAsync</c> returned EMPTY on every
    /// pass, so no CEH room could ever match a Backstage hall — even though all 11 halls exist with
    /// names matching CEH's room strings character for character — and all 9 sessions were created
    /// with <c>venue: null</c>.</para>
    ///
    /// <para><b>speakers</b> was missing too, and nobody had reported it yet: it silently empties
    /// the live speaker index used by the §301b self-heal, the §304 adopt-by-email, and the speaker
    /// change detection. Those all "fail safe" on an empty index, so the damage is invisible —
    /// adopt-by-email simply never adopts, and duplicate-e-mail creates fail instead.</para>
    ///
    /// <para>Adding a resource to the pager without probing it is therefore a silent-outage
    /// generator. Probe first (bare vs <c>?page=1</c>), then add.</para>
    /// </remarks>
    private static bool RejectsPageParam(string resource)
    {
        // resource may carry a query string ("sessions?day=1") — compare the path only.
        var path = resource.Split('?')[0].Trim('/');
        return path.Equals("agendas", StringComparison.OrdinalIgnoreCase)
            || path.Equals("sessions", StringComparison.OrdinalIgnoreCase)
            || path.Equals("tracks", StringComparison.OrdinalIgnoreCase)
            || path.Equals("halls", StringComparison.OrdinalIgnoreCase)
            || path.Equals("speakers", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// §585 — the live-measured page-param matrix, exposed so a test can assert it. Keys are v3
    /// resource paths; <c>true</c> = the endpoint REJECTS <c>?page</c> and must be read bare.
    /// </summary>
    public static bool ResourceRejectsPageParam(string resource) => RejectsPageParam(resource);

    private static string? NullIf(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    // NOTE: the legacy v1 email-keyed ticket pull (GetBackstageTicketsAsync → ZohoTicket)
    // was RETIRED (REQUIREMENTS §125): it was the second, competing attendee writer
    // (AttendeeReconcileJob). The single authoritative sync now pulls the richer v3
    // /orders + /attendees via GetBackstageOrdersAsync + GetBackstageAttendeesAsync.

    /// <summary>
    /// Fetch Bookings appointments for the Master Class date window. Mirrors
    /// the source script's multipart fetchappointment call.
    /// </summary>
    public async Task<IReadOnlyList<ZohoAppointment>> GetBookingsAppointmentsAsync(
        string accessToken, CancellationToken ct = default)
    {
        var appointments = new List<ZohoAppointment>();
        var page = 1;

        while (true)
        {
            var fromTime = $"{FormatDate(_options.MasterClassDate)} 00:00:00";
            var toTime = $"{FormatDate(_options.MasterClassDate)} 23:59:59";

            var payload = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["from_time"] = fromTime,
                ["to_time"] = toTime,
                ["page"] = page,
                ["per_page"] = 100,
            });

            var boundary = Guid.NewGuid().ToString();
            var body =
                $"--{boundary}\r\n" +
                "Content-Disposition: form-data; name=\"data\"\r\n\r\n" +
                $"{payload}\r\n--{boundary}--\r\n";

            var url = $"{_options.ApiDomain}/bookings/v1/json/fetchappointment";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Add("Authorization", $"Zoho-oauthtoken {accessToken}");
            req.Content = new StringContent(body, Encoding.UTF8);
            req.Content.Headers.Remove("Content-Type");
            req.Content.Headers.TryAddWithoutValidation(
                "Content-Type", $"multipart/form-data; boundary={boundary}");

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                break;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, default, ct);

            if (!TryGetReturnData(doc.RootElement, out var data)
                || data.GetArrayLength() == 0)
            {
                break;
            }

            foreach (var appt in data.EnumerateArray())
            {
                appointments.Add(new ZohoAppointment(
                    CustomerEmail: Lower(GetString(appt, "customer_email")),
                    CustomerName: GetString(appt, "customer_name"),
                    ServiceName: GetString(appt, "service_name"),
                    Status: GetString(appt, "status"),
                    SummaryUrl: GetString(appt, "summary_url")));
            }

            if (data.GetArrayLength() < 100)
            {
                break;
            }
            page++;
        }

        return appointments;
    }

    /// <summary>
    /// Pull every record from one Zoho CRM module (standard v2 REST paging).
    /// Records without a value in the sponsor-company-id field are skipped —
    /// a lead the CRM hasn't attributed to a sponsor can't be routed.
    /// </summary>
    public async Task<IReadOnlyList<ZohoCrmLead>> GetCrmLeadsAsync(
        string accessToken, string module, CancellationToken ct = default)
    {
        var leads = new List<ZohoCrmLead>();
        var page = 1;

        while (true)
        {
            var url = $"{_options.ApiDomain}/crm/v2/{module}?page={page}&per_page=200";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Authorization", $"Zoho-oauthtoken {accessToken}");

            using var resp = await _http.SendAsync(req, ct);
            // 204 = module empty; anything non-200 ends the page loop.
            if (resp.StatusCode != System.Net.HttpStatusCode.OK)
            {
                break;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, default, ct);
            if (!doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array
                || data.GetArrayLength() == 0)
            {
                break;
            }

            foreach (var rec in data.EnumerateArray())
            {
                var sponsorId = GetString(rec, _options.CrmSponsorCompanyIdField);
                if (string.IsNullOrWhiteSpace(sponsorId)) continue;

                var first = GetString(rec, "First_Name");
                var last  = GetString(rec, "Last_Name");
                var full  = GetString(rec, "Full_Name");
                if (string.IsNullOrWhiteSpace(full))
                {
                    full = $"{first} {last}".Trim();
                }

                DateTimeOffset created = DateTimeOffset.UtcNow;
                var createdRaw = GetString(rec, "Created_Time");
                if (!string.IsNullOrWhiteSpace(createdRaw)
                    && DateTimeOffset.TryParse(createdRaw, out var parsed))
                {
                    created = parsed;
                }

                leads.Add(new ZohoCrmLead(
                    ZohoRecordId: GetString(rec, "id"),
                    Module: module,
                    SponsorCompanyId: sponsorId.Trim(),
                    FirstName: first,
                    LastName: last,
                    FullName: full,
                    Email: Lower(GetString(rec, "Email")),
                    Phone: GetString(rec, "Phone"),
                    Company: GetString(rec, "Company"),
                    JobTitle: GetString(rec, "Designation"),
                    City: GetString(rec, "City"),
                    Country: GetString(rec, "Country"),
                    Source: GetString(rec, "Lead_Source"),
                    Notes: GetString(rec, "Description"),
                    CreatedTime: created));
            }

            // CRM "info.more_records" is authoritative; fall back to page size.
            var more = doc.RootElement.TryGetProperty("info", out var info)
                       && info.TryGetProperty("more_records", out var mr)
                       && mr.ValueKind == JsonValueKind.True;
            if (!more) break;
            page++;
        }

        return leads;
    }

    private static bool TryGetReturnData(JsonElement root, out JsonElement data)
    {
        data = default;
        if (root.TryGetProperty("response", out var response)
            && response.TryGetProperty("returnvalue", out var rv)
            && rv.TryGetProperty("data", out var d)
            && d.ValueKind == JsonValueKind.Array)
        {
            data = d;
            return true;
        }
        return false;
    }

    private static string FormatDate(string isoDate) =>
        DateTime.TryParse(isoDate, out var dt)
            ? dt.ToString("dd-MMM-yyyy",
                System.Globalization.CultureInfo.InvariantCulture)
            : isoDate;

    private static string GetString(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(prop, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    private static string FirstNonEmpty(params string[] xs)
    {
        foreach (var x in xs) if (!string.IsNullOrWhiteSpace(x)) return x.Trim();
        return string.Empty;
    }

    private static string Lower(string s) =>
        (s ?? string.Empty).Trim().ToLowerInvariant();
}
