namespace CommunityHub.Core.Domain;

/// <summary>
/// One lead / inquiry / booth-meeting pulled from Zoho CRM into the
/// hub. Scoped to (EventId, SponsorCompanyId). The Zoho-side id is
/// preserved on <see cref="ZohoRecordId"/> so the daily sync is
/// idempotent -- re-pulling the same Zoho row updates this entity in
/// place rather than creating a duplicate.
///
/// Per-lead processing state is local to the hub (Zoho stays the
/// source of truth for content; the hub layers status / notifications /
/// AI screening / email-reply audit on top).
/// </summary>
public class SponsorLead
{
    public int Id { get; set; }

    public int EventId { get; set; }
    public string SponsorCompanyId { get; set; } = string.Empty;

    /// <summary>Zoho CRM record id (Leads / Inquiries / Meetings). Stable across re-syncs.</summary>
    public string ZohoRecordId { get; set; } = string.Empty;

    public SponsorLeadKind LeadKind { get; set; }

    // ---- Zoho fields (kept loose; whatever Zoho ships) -----------------
    public string FirstName { get; set; } = string.Empty;
    public string LastName  { get; set; } = string.Empty;
    public string FullName  { get; set; } = string.Empty;
    public string Email     { get; set; } = string.Empty;
    public string Phone     { get; set; } = string.Empty;
    public string Company   { get; set; } = string.Empty;
    public string JobTitle  { get; set; } = string.Empty;
    public string City      { get; set; } = string.Empty;
    public string Country   { get; set; } = string.Empty;
    public string Source    { get; set; } = string.Empty;
    public string Notes     { get; set; } = string.Empty;

    /// <summary>When the lead was captured in Zoho (Zoho's Created_Time).</summary>
    public DateTimeOffset CapturedAt { get; set; }

    /// <summary>When the hub last pulled this record from Zoho.</summary>
    public DateTimeOffset LastSyncedAt { get; set; }

    // ---- Capture provenance -------------------------------------------

    /// <summary>
    /// How this lead entered the hub. Zoho-pulled leads carry a
    /// <see cref="ZohoRecordId"/>; booth-captured leads are entered by a
    /// sponsor contact in the hub and have an empty Zoho id (so the
    /// filtered-unique index does not collide).
    /// </summary>
    public SponsorLeadCaptureMethod CaptureMethod { get; set; } = SponsorLeadCaptureMethod.ZohoSync;

    /// <summary>
    /// Email of the booth-staff sponsor contact who captured a manual lead.
    /// Null for Zoho-synced leads. Used for provenance / audit only — it is
    /// NOT exposed on the sponsor download feed.
    /// </summary>
    public string? CapturedByEmail { get; set; }

    // ---- Hub-local processing state -----------------------------------

    public SponsorLeadStatus Status { get; set; } = SponsorLeadStatus.Open;

    /// <summary>Set by the organizer or by the AI screen when status flips to Processed/Junk.</summary>
    public string? StatusNote { get; set; }

    public DateTimeOffset? StatusChangedAt { get; set; }
    public string?         StatusChangedByEmail { get; set; }

    // ---- AI screen -----------------------------------------------------

    /// <summary>0..100; high = looks like a real prospect. Null = not screened.</summary>
    public int? AiScreenScore { get; set; }

    /// <summary>Short human-readable verdict (e.g. "looks-legit", "spam-pattern", "competitor").</summary>
    public string? AiScreenLabel { get; set; }

    public DateTimeOffset? AiScreenedAt { get; set; }

    // ---- Email-reply audit --------------------------------------------

    /// <summary>UTC timestamp of the most recent outbound reply the hub sent on behalf of the sponsor / organizer.</summary>
    public DateTimeOffset? LastReplyAt { get; set; }

    /// <summary>Sender on the most recent reply (sponsor contact or org operator).</summary>
    public string? LastReplyByEmail { get; set; }
}

/// <summary>
/// What kind of Zoho record this is. Affects the column mapping at sync
/// time + the icon shown in the leads admin grid.
/// </summary>
public enum SponsorLeadKind
{
    Lead       = 0,
    Inquiry    = 1,
    Meeting    = 2,
}

/// <summary>
/// How a <see cref="SponsorLead"/> entered the hub.
/// </summary>
public enum SponsorLeadCaptureMethod
{
    /// <summary>Pulled from Zoho CRM by the nightly sync (the default).</summary>
    ZohoSync   = 0,

    /// <summary>Entered by a sponsor's booth staff directly in the hub.</summary>
    ManualBooth = 1,
}

/// <summary>
/// Local processing state. Status drives the "status" column shown on
/// the leads admin grid + filters the sponsor-facing CSV / JSON feed.
/// NOTHING is hard-deleted -- every lead row stays in the DB so the AI
/// screen model can learn from operator overrides over time. The
/// previously-named "Delete" action sets <see cref="Ignore"/> (operator
/// chose not to pursue) or <see cref="Junk"/> (AI screen or operator
/// flagged as spam / irrelevant) instead of removing the row.
/// </summary>
public enum SponsorLeadStatus
{
    /// <summary>Default. New lead, not yet acted on.</summary>
    Open = 0,

    /// <summary>Operator or sponsor has replied / contacted the lead -- closed the loop.</summary>
    Processed = 1,

    /// <summary>Operator flagged "interesting -- come back to this". Kept on the grid.</summary>
    Interest = 2,

    /// <summary>Operator decided not to pursue. Hidden from the default grid view but row preserved.</summary>
    Ignore = 3,

    /// <summary>AI screen or operator marked the lead as junk / spam. Hidden from sponsor feeds by default.</summary>
    Junk = 4,
}
