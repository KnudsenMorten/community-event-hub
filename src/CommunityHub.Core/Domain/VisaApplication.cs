namespace CommunityHub.Core.Domain;

/// <summary>
/// §1374 — one person's answers to the visa form (<c>/Forms/Visa</c>), one row per participant.
/// </summary>
/// <remarks>
/// <para>Operator 2026-10-28: speakers and volunteers who need a visa to Denmark fill this in; the
/// organizers copy it into the "Invitation of a visa applicant" form on nyidanmark.dk and hand back the
/// case number as <see cref="ApplicationId"/>, which the applicant uses when they apply. Who sees the
/// form is <see cref="Participant.NeedsVisa"/>.</para>
/// <para>🔒 A simple form, not a survey and not an e-mail flow (operator: <i>"instead of a survey, it can
/// be a simple form"</i>). The applicant sees the application ID on their own form page.</para>
/// <para>Countries and nationality are stored as the ISO code from <see cref="Countries"/>; the
/// dropdown answers as the text from <see cref="VisaFormOptions"/>.</para>
/// </remarks>
public class VisaApplication
{
    public int Id { get; set; }

    /// <summary>The edition. Plain column — the participant row already carries the event cascade.</summary>
    public int EventId { get; set; }

    public int ParticipantId { get; set; }
    public Participant Participant { get; set; } = null!;

    // --- 1. About you ---------------------------------------------------------------------------
    public string? FirstNames { get; set; }
    public string? LastName { get; set; }
    public DateOnly? DateOfBirth { get; set; }
    public string? Gender { get; set; }
    public string? Nationality { get; set; }
    public string? HomeStreet { get; set; }
    public string? HomePostalCode { get; set; }
    public string? HomeCity { get; set; }
    public string? HomeCountry { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }

    // --- 2. About your company ------------------------------------------------------------------
    public string? CompanyName { get; set; }
    public string? CompanyStreet { get; set; }
    public string? CompanyPostalCode { get; set; }
    public string? CompanyCity { get; set; }
    public string? CompanyCountry { get; set; }
    public string? CompanyPhone { get; set; }
    public string? CompanyEmail { get; set; }
    public string? CompanyWebsite { get; set; }
    public string? DepartmentAndRole { get; set; }

    // --- 3. Relationship between our companies ---------------------------------------------------
    public bool? VisitedBefore { get; set; }
    public string? CompanyActivity { get; set; }
    public string? ContactOrigin { get; set; }
    public string? RelationshipDuration { get; set; }
    public string? FinancialScope { get; set; }

    // --- 4. About the visit ---------------------------------------------------------------------
    public DateOnly? ArrivalDate { get; set; }
    public int? LengthOfVisitDays { get; set; }
    public string? Entries { get; set; }
    public string? Purpose { get; set; }
    public string? SpecialCircumstances { get; set; }

    // --- 5. Expenses and accommodation ----------------------------------------------------------
    public string? TravelPaidBy { get; set; }
    public string? StayPaidBy { get; set; }
    public string? Accommodation { get; set; }
    public bool? HostGuarantees { get; set; }

    // --- Status ---------------------------------------------------------------------------------
    /// <summary>First time the applicant sent the form. Null = saved by an organizer only (application ID).</summary>
    public DateTimeOffset? SubmittedAt { get; set; }
    /// <summary>Last time the applicant changed their answers.</summary>
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>The case number from nyidanmark.dk, entered by an organizer; shown on the applicant's form page.</summary>
    public string? ApplicationId { get; set; }
    public DateTimeOffset? ApplicationIdSetAt { get; set; }
}

/// <summary>§1374 — the fixed answers behind the visa form's dropdowns, and the text limits.</summary>
public static class VisaFormOptions
{
    public const int LongTextMax = 500;
    public const int ShortTextMax = 200;

    public static readonly IReadOnlyList<string> Genders = new[] { "Female", "Male", "Other" };

    public static readonly IReadOnlyList<string> Entries = new[] { "Single", "Double", "Multiple" };

    public static readonly IReadOnlyList<string> PaidBy = new[]
    {
        "Myself", "My employer", "The event organizer", "Shared between me and the event organizer",
    };

    /// <summary>
    /// The stay is always paid by the event (operator 2026-10-28: <i>"who pays for your stay is The Event
    /// organizer"</i>) — shown on the form, not asked, and set on every save.
    /// </summary>
    public const string StayPaidByOrganizer = "The event organizer";

    /// <summary>
    /// The host does not guarantee travel and stay financially (operator 2026-10-28: <i>"… is No"</i>) —
    /// shown on the form, not asked, and set on every save.
    /// </summary>
    public const bool HostGuarantees = false;

    public static readonly IReadOnlyList<string> RelationshipDurations = new[]
    {
        "This is the first contact", "Less than 1 year", "1–3 years", "More than 3 years",
    };

    /// <summary>The country name for a stored ISO code, or the stored value itself when unknown.</summary>
    public static string CountryName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return string.Empty;
        foreach (var (c, name) in Countries.All)
            if (string.Equals(c, code, StringComparison.OrdinalIgnoreCase)) return name;
        return code;
    }

    public static string YesNo(bool? value) => value switch { true => "Yes", false => "No", _ => string.Empty };
}
