using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Email;

/// <summary>One resolved sponsor-mail recipient (a coordinator contact).</summary>
public sealed record SponsorRecipient(
    int ParticipantId,
    string Email,
    string FullName,
    string? SecondaryEmail)
{
    /// <summary>
    /// The Company Manager / WordPress <c>user_id</c> id-link for this contact
    /// (<see cref="Participant.CmUserId"/>), carried so the CM → hub correlation
    /// keys on the unique id rather than name/email (REQUIREMENTS §7c). Null when
    /// the contact has not yet been synced from CM. The audience SELECTION is
    /// unchanged — this only surfaces the id for downstream correlation/audit.
    /// </summary>
    public int? CmUserId { get; init; }


    /// <summary>First word of the full name, or "there" when blank.</summary>
    public string FirstName =>
        string.IsNullOrWhiteSpace(FullName)
            ? "there"
            : FullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } parts
                ? parts[0]
                : "there";
}

/// <summary>
/// The single authority for "who at a sponsor company should receive this email"
/// (the universal sponsor-email audience rule, REQUIREMENTS §7c).
///
/// THE RULE: every email to a sponsor company goes to ALL of that company's
/// EVENT-COORDINATOR contacts. Signer-only contacts (signer true, coordinator
/// false) are NEVER included; a contact who is BOTH signer and coordinator IS
/// included (because they are a coordinator). A company can have several
/// coordinators — all are returned.
///
/// <para><b>Audience source — e-conomic ERP roles (primary), CM default
/// (fallback).</b> Company Manager cannot be extended to hold per-user roles, so
/// the coordinator audience is resolved READ-ONLY from e-conomic ERP role data:
/// every contact carrying <b>Role 2 (event coordinator)</b> for the company
/// (resolved via <see cref="ISponsorErpCoordinatorSource"/>), matched to the
/// company's hub <see cref="Participant"/> rows by email. When ERP roles are
/// unavailable (disabled / unreachable / empty) the resolver FALLS BACK to the
/// hub's manual <see cref="Participant.IsEventCoordinator"/> flags — which are
/// themselves seeded from Company Manager's single
/// <c>event_coordination_default_contact_id</c> at sync time — so the feature
/// still works. The organizer-set per-contact flags are also honoured as a
/// manual OVERRIDE on top of ERP Role 2 (a flagged contact is always included).</para>
///
/// Every sponsor email path (welcome/intro, sponsor-overdue, sponsor
/// task-deadline-reminder, organizer sponsor broadcast) routes through here so
/// the audience rule lives in exactly one place. This resolver only PICKS the
/// audience — it does not send and does not bypass the email allowlist;
/// <see cref="BrevoEmailSender"/>'s redirect/allowlist still gates every actual
/// delivery.
///
/// The pure <see cref="Select"/> overload is unit-testable without a database;
/// the DB overload scopes to the sponsor company + edition and is the seam the
/// send paths call.
/// </summary>
public sealed class SponsorRecipientResolver
{
    private readonly CommunityHubDbContext _db;
    private readonly ISponsorErpCoordinatorSource? _erpCoordinators;

    public SponsorRecipientResolver(CommunityHubDbContext db)
        : this(db, erpCoordinators: null)
    {
    }

    public SponsorRecipientResolver(
        CommunityHubDbContext db,
        ISponsorErpCoordinatorSource? erpCoordinators)
    {
        _db = db;
        _erpCoordinators = erpCoordinators;
    }

    /// <summary>
    /// Pure audience selection from the hub's manual role flags: from a set of a
    /// company's sponsor contacts, keep the ones with event-coordinator status
    /// (<see cref="Participant.IsEventCoordinator"/>). Signer-only excluded,
    /// both-roles included, multiple coordinators returned. Deduplicated by email
    /// (case-insensitive), ordered by email for a stable preview. Inactive
    /// contacts are excluded (they cannot be mailed). This is the FALLBACK +
    /// manual-override path; <see cref="ResolveAsync"/> additionally folds in the
    /// e-conomic ERP Role-2 set when it is available.
    /// </summary>
    public static IReadOnlyList<SponsorRecipient> Select(IEnumerable<Participant> contacts) =>
        Select(contacts, erpCoordinatorEmails: null);

    /// <summary>
    /// Pure audience selection with the optional e-conomic Role-2 coordinator
    /// email set folded in. A contact is included when it is active, has an email,
    /// and EITHER carries the manual <see cref="Participant.IsEventCoordinator"/>
    /// flag (organizer override) OR its email is in
    /// <paramref name="erpCoordinatorEmails"/> (the ERP Role-2 set, case-insensitive).
    /// When <paramref name="erpCoordinatorEmails"/> is <c>null</c> this is exactly
    /// the flag-only fallback. Signer-only contacts are excluded, both-roles
    /// contacts included, multiple coordinators returned. Deduplicated by email,
    /// ordered by email.
    /// </summary>
    public static IReadOnlyList<SponsorRecipient> Select(
        IEnumerable<Participant> contacts,
        IReadOnlyCollection<string>? erpCoordinatorEmails)
    {
        var erpSet = erpCoordinatorEmails is { Count: > 0 }
            ? new HashSet<string>(
                erpCoordinatorEmails.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()),
                StringComparer.OrdinalIgnoreCase)
            : null;

        return contacts
            .Where(p => p.IsActive)
            .Where(p => !string.IsNullOrWhiteSpace(p.Email))
            // event-coordinator if flagged in the hub (manual override / CM default)
            // OR present in the e-conomic Role-2 set. Signer-only contacts have
            // neither and are excluded; both-roles contacts pass on the flag/ERP.
            .Where(p => p.IsEventCoordinator || (erpSet is not null && erpSet.Contains(p.Email.Trim())))
            .Select(p => new SponsorRecipient(
                p.Id, p.Email.Trim(), p.FullName, p.SecondaryEmail)
            {
                // Carry the CM id-link so CM → hub correlation keys on the id.
                CmUserId = p.CmUserId,
            })
            .GroupBy(r => r.Email, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(r => r.Email, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Resolve the coordinator recipients for one sponsor company in one edition.
    /// Loads the edition's Sponsor-role participants whose
    /// <see cref="Participant.SponsorCompanyId"/> matches, asks the read-only ERP
    /// role source for that company's event-coordinator (Role 2) email set, and
    /// applies <see cref="Select(IEnumerable{Participant}, IReadOnlyCollection{string})"/>.
    /// When the ERP source is unavailable (disabled / unreachable / no data) the
    /// resolver falls back to the manual <see cref="Participant.IsEventCoordinator"/>
    /// flags (themselves seeded from the Company Manager default). Returns an empty
    /// list when the company has no coordinator contacts at all (the caller then
    /// sends nothing — never falls back to signers).
    /// </summary>
    public async Task<IReadOnlyList<SponsorRecipient>> ResolveAsync(
        int eventId, string sponsorCompanyId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sponsorCompanyId))
        {
            return Array.Empty<SponsorRecipient>();
        }

        var contacts = await _db.Participants
            .Where(p => p.EventId == eventId
                        && p.Role == ParticipantRole.Sponsor
                        && p.SponsorCompanyId == sponsorCompanyId)
            .ToListAsync(ct);

        // PRIMARY: e-conomic ERP Role-2 set (read-only). Null => unavailable,
        // fall back to the flag-only Select (CM default + manual overrides).
        IReadOnlyCollection<string>? erpCoordinatorEmails = null;
        if (_erpCoordinators is { IsEnabled: true })
        {
            erpCoordinatorEmails = await _erpCoordinators
                .GetCoordinatorEmailsAsync(sponsorCompanyId, ct);
        }

        return Select(contacts, erpCoordinatorEmails);
    }
}
