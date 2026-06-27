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
    string? CreatedTimeRaw = null);

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
/// is false when the live agenda API cannot be read (today: the refresh token lacks the
/// <c>ZohoBackstage.agenda.READ</c> scope, so get-all-sessions / get-all-halls both 401)
/// — the engine then no-ops gracefully instead of treating an empty pull as "everything
/// was deleted". When available, <see cref="Sessions"/> is the current agenda.
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
    string? Twitter);

/// <summary>
/// The outcome of a Backstage speaker pull (REQUIREMENTS §38e/§58). <see cref="IsAvailable"/>
/// is false when the live speakers API cannot be read (the refresh token lacks the
/// <c>ZohoBackstage.speaker.READ</c> scope, or <see cref="ZohoOptions.SpeakerReadEnabled"/> is
/// off) — the engine then no-ops gracefully instead of treating an empty pull as "every
/// speaker was deleted". Mirrors <see cref="BackstageSessionsResult"/>.
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

    /// <summary>
    /// Master switch for the Backstage AGENDA pull (REQUIREMENTS §38e/§6). Default
    /// FALSE: the agenda endpoints (get-all-sessions / get-all-halls) require the
    /// <c>ZohoBackstage.agenda.READ</c> scope on the refresh token, which is not yet
    /// granted. Flip to true (Zoho:AgendaReadEnabled=true) only AFTER the operator
    /// extends the token; until then <see cref="ZohoClient.GetBackstageSessionsAsync"/>
    /// reports unavailable and never fakes data, so the §38e engine no-ops gracefully.
    /// </summary>
    public bool AgendaReadEnabled { get; set; }

    /// <summary>
    /// Master switch for the Backstage SPEAKER pull (REQUIREMENTS §38e/§58 — the
    /// Zoho→CEH speaker change-detection source). Mirrors
    /// <see cref="AgendaReadEnabled"/>: the speakers list/get endpoints require the
    /// <c>ZohoBackstage.speaker.READ</c> scope on the refresh token. Default FALSE so
    /// <see cref="ZohoClient.GetBackstageSpeakersAsync"/> reports unavailable and never
    /// fakes data — the §58 speaker change-detection engine then no-ops gracefully
    /// (exactly as §38e does when the agenda scope is missing). Flip to true
    /// (Zoho:SpeakerReadEnabled=true) only AFTER the operator extends the token.
    /// </summary>
    public bool SpeakerReadEnabled { get; set; }

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
    private readonly HttpClient _http;
    private readonly ZohoOptions _options;
    private readonly ILogger<ZohoClient>? _log;

    public ZohoClient(HttpClient http, ZohoOptions options, ILogger<ZohoClient>? log = null)
    {
        _http = http;
        _options = options;
        _log = log;
    }

    /// <summary>One Backstage exhibitor — its system id + company name (for matching by name).</summary>
    public sealed record BackstageExhibitor(string Id, string CompanyName);

    /// <summary>
    /// List every exhibitor in the configured Backstage event (id + company_name)
    /// so a sponsor can be matched by company name. Scope:
    /// <c>ZohoBackstage.exhibitor.READ</c>. Returns empty on auth/HTTP failure.
    /// </summary>
    public async Task<IReadOnlyList<BackstageExhibitor>> GetExhibitorsAsync(
        string accessToken, CancellationToken ct = default)
    {
        var list = new List<BackstageExhibitor>();
        await foreach (var el in PageV3Async("exhibitors", "exhibitors", accessToken, ct))
        {
            var id = el.TryGetProperty("id", out var i) ? i.GetString() : null;
            var name = el.TryGetProperty("company_name", out var n) ? n.GetString() : null;
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                list.Add(new BackstageExhibitor(id!, name!));
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
        var url = $"{_options.ApiDomain}/backstage/v3/portals/{_options.BackstagePortalId}"
            + $"/events/{_options.BackstageEventId}/exhibitors/{exhibitorId}";
        var payload = new Dictionary<string, object?>();
        if (companyOverview is not null) payload["company_overview"] = companyOverview;
        if (companyShortDescription is not null) payload["company_short_description"] = companyShortDescription;
        if (!string.IsNullOrWhiteSpace(websiteUrl)) payload["website_url"] = websiteUrl;
        // company_social_pages is an object keyed by platform (linkedin / twitter / facebook).
        if (!string.IsNullOrWhiteSpace(linkedInUrl) || !string.IsNullOrWhiteSpace(twitterUrl))
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
        // (REQUIREMENTS §41a). Zoho hard-caps email updates at 3 attempts — even a no-op resend
        // burns one — so the caller passes contactEmail ONLY when it actually CHANGED vs the last
        // value sent (SponsorInfo.ZohoContactEmail). Name + mobile updates are always allowed.
        if (!string.IsNullOrWhiteSpace(contactFirstName) || !string.IsNullOrWhiteSpace(contactLastName)
            || !string.IsNullOrWhiteSpace(contactMobile) || !string.IsNullOrWhiteSpace(contactEmail))
        {
            var contact = new Dictionary<string, object?>
            {
                ["first_name"] = contactFirstName ?? string.Empty,
                ["last_name"] = contactLastName ?? string.Empty,
            };
            if (!string.IsNullOrWhiteSpace(contactMobile)) contact["mobile_no"] = contactMobile;
            if (!string.IsNullOrWhiteSpace(contactEmail)) contact["email"] = contactEmail;
            payload["contact"] = contact;
        }

        using var req = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = System.Net.Http.Json.JsonContent.Create(payload),
        };
        req.Headers.Add("Authorization", $"Zoho-oauthtoken {accessToken}");
        using var resp = await _http.SendAsync(req, ct);
        return resp.IsSuccessStatusCode;
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
        var url = $"{_options.ApiDomain}/backstage/v3/portals/{_options.BackstagePortalId}"
            + $"/events/{_options.BackstageEventId}/sponsors/{sponsorId}";
        var payload = new Dictionary<string, object?>();
        if (description is not null) payload["description"] = description;
        if (!string.IsNullOrWhiteSpace(websiteUrl)) payload["website_url"] = websiteUrl;
        if (!string.IsNullOrWhiteSpace(companyName)) payload["company_name"] = companyName;
        // The contact EMAIL is sent on UPDATE ONLY when the CALLER passes a non-blank value
        // (operator 2026-06-25, confirmed by Zoho: "sponsor email addresses can only be updated
        // up to 3 times — hard-coded limit"; REQUIREMENTS §41a). Even a no-op resend burns one of
        // the 3, so the caller passes contactEmail ONLY when it actually CHANGED vs the last value
        // sent (SponsorInfo.ZohoContactEmail). Name updates are always allowed.
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
    public sealed record BackstageSponsorDetail(
        string? WebsiteUrl, string? Description, string? LinkedInUrl, string? TwitterUrl);

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
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("Authorization", $"Zoho-oauthtoken {accessToken}");
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;

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

        return new BackstageSponsorDetail(
            WebsiteUrl: NullIf(GetString(root, "website_url")),
            Description: NullIf(GetString(root, descriptionProp)),
            LinkedInUrl: linkedIn,
            TwitterUrl: twitter);
    }

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
    /// POST create an AGENDA session in Zoho Backstage (REQUIREMENTS §57 stage 2). Returns
    /// the new session id on success, else the Zoho HTTP status + body so the caller can put
    /// the real reason in its alert. The session is created on the given 1-based agenda
    /// <paramref name="day"/> (POST …/sessions?day={day}). <paramref name="durationMinutes"/>
    /// is sent as <c>duration</c> (Backstage stores duration, not an explicit end).
    /// <paramref name="trackId"/> is the Backstage track ID (resolve via
    /// <see cref="GetTracksAsync"/>); <paramref name="sessionType"/> is the required
    /// event-specific session type. Scope: agenda CREATE. A 2xx with no parseable session id
    /// is treated as a failure (not created).
    /// </summary>
    public async Task<ZohoCreateResult> CreateSessionAsync(
        string accessToken, int day, string title, string? description,
        DateTimeOffset? startTime, int? durationMinutes, string? trackId, string? sessionType,
        CancellationToken ct = default)
    {
        if (day < 1) day = 1;
        var url = $"{_options.ApiDomain}/backstage/v3/portals/{_options.BackstagePortalId}"
            + $"/events/{_options.BackstageEventId}/sessions?day={day}";
        var payload = BuildSessionPayload(title, description, startTime, durationMinutes, trackId, sessionType);

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
    /// </summary>
    public async Task<bool> UpdateSessionAsync(
        string accessToken, string sessionId, string title, string? description,
        DateTimeOffset? startTime, int? durationMinutes, string? trackId, string? sessionType,
        CancellationToken ct = default)
    {
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
    /// the Backstage TRACK ID (live-verified: the name is rejected); <c>sessionType</c> is the
    /// required event-specific session type. <c>start_time</c> is the ISO-8601 instant;
    /// <c>duration</c> is whole minutes. Exposed for the stage-2 unit test to assert the
    /// payload shape without HTTP.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> BuildSessionPayload(
        string title, string? description, DateTimeOffset? startTime, int? durationMinutes,
        string? trackId, string? sessionType)
    {
        var payload = new Dictionary<string, object?> { ["title"] = (title ?? string.Empty).Trim() };
        if (!string.IsNullOrWhiteSpace(description)) payload["description"] = description!.Trim();
        // `track` carries the Backstage track ID (live-verified the create rejects a name).
        if (!string.IsNullOrWhiteSpace(trackId)) payload["track"] = trackId!.Trim();
        if (!string.IsNullOrWhiteSpace(sessionType)) payload["sessionType"] = sessionType!.Trim();
        if (startTime is { } st)
            payload["start_time"] = st.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ",
                System.Globalization.CultureInfo.InvariantCulture);
        if (durationMinutes is { } d && d > 0) payload["duration"] = d;
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
    /// but id unparsed; null = failed).
    /// </summary>
    public async Task<string?> CreateSpeakerAsync(
        string accessToken, string email, string? firstName, string? lastName,
        string? country, string? tagline, string? bio, string? linkedIn, string? twitter,
        string? skills, bool featured, CancellationToken ct = default)
    {
        var url = $"{_options.ApiDomain}/backstage/v3/portals/{_options.BackstagePortalId}"
            + $"/events/{_options.BackstageEventId}/speakers";
        // HARD GATE: 'featured' tracks the hub's SelectedForPublish — an unselected
        // speaker is created non-featured (never highlighted/published).
        var payload = new Dictionary<string, object?> { ["email"] = email, ["featured"] = featured };
        void Set(string k, string? v) { if (!string.IsNullOrWhiteSpace(v)) payload[k] = v!.Trim(); }
        Set("name", firstName);
        Set("last_name", lastName);
        Set("country", string.IsNullOrWhiteSpace(country) ? null : country!.Trim().ToUpperInvariant());
        Set("designation", tagline);
        Set("description", bio);
        Set("linkedin", linkedIn);
        Set("twitter", twitter);
        Set("skills", skills);

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = System.Net.Http.Json.JsonContent.Create(payload),
        };
        req.Headers.Add("Authorization", $"Zoho-oauthtoken {accessToken}");
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        try
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (root.TryGetProperty("speaker", out var sp) && sp.ValueKind == JsonValueKind.Object) root = sp;
            if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String) return idEl.GetString();
        }
        catch { /* created but id unparsed */ }
        return string.Empty;
    }

    /// <summary>
    /// Fetch the CURRENT Zoho Backstage SPEAKERS (REQUIREMENTS §38e/§58) — one row per
    /// speaker with its id, name (first + last), tagline (designation), bio (description),
    /// country and linkedin/twitter. This is the SOURCE the §58 Zoho→CEH speaker
    /// change-detection engine diffs against the CEH stored snapshot.
    ///
    /// <b>AVAILABILITY (fail-soft, mirrors <see cref="GetBackstageSessionsAsync"/>).</b> The
    /// speakers list endpoint requires the <c>ZohoBackstage.speaker.READ</c> scope on the
    /// refresh token; until the operator extends the token AND sets
    /// <see cref="ZohoOptions.SpeakerReadEnabled"/>, this returns
    /// <see cref="BackstageSpeakersResult.Unavailable"/> and NEVER fakes data, so the engine
    /// no-ops instead of mistaking an empty pull for "all speakers changed/were removed".
    /// </summary>
    public async Task<BackstageSpeakersResult> GetBackstageSpeakersAsync(
        string accessToken, CancellationToken ct = default)
    {
        // The speaker scope is not on the token yet ⇒ report unavailable (do not fake).
        if (!_options.SpeakerReadEnabled)
        {
            return BackstageSpeakersResult.Unavailable(
                "Zoho Backstage speaker API is not enabled — it requires the "
                + "ZohoBackstage.speaker.READ OAuth scope on the refresh token and "
                + "Zoho:SpeakerReadEnabled=true. Speakers were not pulled.");
        }

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
    /// country / social fields (REQUIREMENTS §38e/§58). Scope:
    /// <c>ZohoBackstage.speaker.READ</c>. Returns null on auth/HTTP failure or when the
    /// speaker scope is not enabled (fail-soft, like <see cref="GetSponsorByIdAsync"/>).
    /// </summary>
    public async Task<BackstageSpeaker?> GetSpeakerByIdAsync(
        string accessToken, string speakerId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(speakerId) || !_options.SpeakerReadEnabled) return null;
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
            Country: NullIf(GetString(el, "country")),
            LinkedIn: linkedIn,
            Twitter: twitter);
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
    /// POST create booth members in bulk for an exhibitor.
    /// Body: <c>{ members: [ { role, first_name, last_name, email, company_name } ] }</c>
    /// (role = "ADMIN" | "staff"). Scope: <c>ZohoBackstage.exhibitor.CREATE</c>.
    /// </summary>
    public async Task<bool> CreateBoothMembersAsync(
        string accessToken, string exhibitorId,
        IReadOnlyList<(string FirstName, string LastName, string Email, string Role, string? CompanyName)> members,
        CancellationToken ct = default)
    {
        if (members.Count == 0) return true;
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
        return resp.IsSuccessStatusCode;
    }

    /// <summary>Exchange the refresh token for a short-lived access token.</summary>
    public async Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["refresh_token"] = _options.RefreshToken,
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
            ["grant_type"] = "refresh_token",
        });

        using var resp = await _http.PostAsync(_options.TokenEndpoint, form, ct);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, default, ct);
        return doc.RootElement.TryGetProperty("access_token", out var t)
            ? t.GetString()
            : null;
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
        await foreach (var order in PageV3Async("orders", "orders", accessToken, ct))
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
        await foreach (var order in PageV3Async("orders", "orders", accessToken, ct))
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
        await foreach (var a in PageV3Async("attendees", "attendees", accessToken, ct))
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
                CompanyName: NullIf(GetString(contact, "company_name")) ?? oi.Company,
                JobTitle: NullIf(GetString(contact, "designation")),
                Phone: NullIf(GetString(contact, "mobile_no")),
                Country: oi.Country, CountryCode: oi.CountryCode, City: oi.City, Postcode: oi.Postcode,
                TaxId: oi.Tax,
                CustomFieldsJson: customJson,
                CreatedTimeRaw: NullIf(GetString(a, "created_time"))));
        }
        return list;
    }

    /// <summary>
    /// Fetch the CURRENT Zoho Backstage AGENDA sessions (REQUIREMENTS §38e) — one row
    /// per agenda session with its id, start/end and resolved hall/room name. This is
    /// the time/location SOURCE the §38e change-detection engine diffs against the CEH
    /// stored values.
    ///
    /// <b>AVAILABILITY (critical dependency).</b> The Backstage agenda endpoints
    /// (<c>get-all-sessions</c> / <c>get-all-halls</c>) require the
    /// <c>ZohoBackstage.agenda.READ</c> OAuth scope on the refresh token; without it both
    /// 401 (REQUIREMENTS §6, mirrored by <c>BackstageSessionSource.IsAvailable</c>). Until
    /// the operator extends the token AND sets <see cref="ZohoOptions.AgendaReadEnabled"/>,
    /// this returns <see cref="BackstageSessionsResult.Unavailable"/> and NEVER fakes
    /// data, so the engine no-ops instead of mistaking an empty pull for "all sessions
    /// changed/were removed". When enabled, it pulls halls → id→name, then sessions, and
    /// resolves each session's hall id to a room name (shape per
    /// <see cref="Sessions.BackstageSessionParser"/>).
    /// </summary>
    public async Task<BackstageSessionsResult> GetBackstageSessionsAsync(
        string accessToken, CancellationToken ct = default)
    {
        // The agenda scope is not on the token yet ⇒ report unavailable (do not fake).
        if (!_options.AgendaReadEnabled)
        {
            return BackstageSessionsResult.Unavailable(
                "Zoho Backstage agenda API is not enabled — it requires the "
                + "ZohoBackstage.agenda.READ OAuth scope on the refresh token and "
                + "Zoho:AgendaReadEnabled=true. Sessions were not pulled.");
        }

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
        var url = $"{_options.ApiDomain}/backstage/v3/portals/{_options.BackstagePortalId}/events/{_options.BackstageEventId}/{resource}";
        var safety = 0;
        while (url is not null && safety++ < 200)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Authorization", $"Zoho-oauthtoken {accessToken}");
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) yield break;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
                foreach (var el in root.EnumerateArray()) yield return el.Clone();
            else if (root.TryGetProperty(arrayProp, out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var el in arr.EnumerateArray()) yield return el.Clone();
            url = root.ValueKind == JsonValueKind.Object
                  && root.TryGetProperty("pagination", out var p)
                  && p.TryGetProperty("nextPage", out var n) && n.ValueKind == JsonValueKind.String
                  ? n.GetString() : null;
        }
    }

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
