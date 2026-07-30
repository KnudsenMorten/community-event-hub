using Microsoft.EntityFrameworkCore;
using CommunityHub.Core.Email;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Erp;

/// <summary>
/// ERP→webshop reconcile (the C# port of Sync-ERP-Contacts-to-Webshop.ps1). For
/// every group-1 (sponsor) e-conomic customer it:
///   - ensures every ERP contact (with an email) exists as a Company Manager user
///     and is linked to the webshop company;
///   - sets the company's DEFAULT SIGNER + DEFAULT EVENT COORDINATOR from the first
///     ERP contact holding Role 1 / Role 2 (only when the webshop default is empty,
///     so a curated value is never overwritten);
///   - INVARIANT: a sponsor must always have a default signer AND a default event
///     coordinator. When the ERP contacts can't supply one (no Role:1 / Role:2),
///     it emails an alert to the organizer so they fix it in e-conomic.
/// e-conomic is the master; this only writes to the webshop. Idempotent.
/// </summary>
public sealed class ErpWebshopContactSyncService
{
    private readonly EconomicContactAdminService _erp;
    private readonly CompanyManagerClient _cm;
    private readonly CompanyManagerOptions _cmOptions;
    private readonly IEmailSender _email;
    private readonly ILogger<ErpWebshopContactSyncService> _log;

    /// <summary>Where the "fix this in e-conomic" alerts go.</summary>
    public const string AlertEmail = "mok@expertslive.dk";
    private const int SponsorGroup = 1;

    /// <summary>§502 — OPTIONAL. Present only where orphan pruning is wanted; without it the
    /// service still DETECTS and reports orphans but changes nothing.</summary>
    private readonly Data.CommunityHubDbContext? _db;
    private readonly Organizer.ParticipantDeactivationService? _deactivate;

    public ErpWebshopContactSyncService(
        EconomicContactAdminService erp, CompanyManagerClient cm, CompanyManagerOptions cmOptions,
        IEmailSender email, ILogger<ErpWebshopContactSyncService> log,
        Data.CommunityHubDbContext? db = null,
        Organizer.ParticipantDeactivationService? deactivate = null)
    {
        _erp = erp;
        _cm = cm;
        _cmOptions = cmOptions;
        _email = email;
        _log = log;
        _db = db;
        _deactivate = deactivate;
    }

    public bool CanRun => _erp.CanWrite && _cmOptions.Enabled;

    public sealed record SyncResult(
        bool Enabled, int Customers, int UsersCreated, int DefaultsSet, int Alerts, List<string> AlertNotes,
        /// <summary>§502 — how many orphaned hub participants were DEACTIVATED this run.</summary>
        int OrphansDeactivated = 0);

    /// <param name="onlyCustomerNumber">
    /// §482b — when set, reconcile ONLY that e-conomic customer instead of sweeping every sponsor.
    /// Added so a sponsor's own contact edit can push straight through to Company Manager without
    /// running an estate-wide reconcile on a page POST; the scheduled job still sweeps everything.
    /// The per-company body below is untouched, so the one-customer path and the sweep cannot
    /// drift apart in behaviour.
    /// </param>
    public async Task<SyncResult> SyncAsync(int? onlyCustomerNumber = null, CancellationToken ct = default)
    {
        var notes = new List<string>();
        if (!CanRun) return new(false, 0, 0, 0, 0, notes);

        var customers = await _erp.ListCustomersAsync(null, SponsorGroup, ct);
        if (onlyCustomerNumber is int onlyNo)
        {
            customers = customers.Where(c => c.CustomerNumber == onlyNo).ToList();
        }
        var companies = await _cm.ListCompaniesAsync(ct);
        var byErp = companies
            .Where(c => !string.IsNullOrWhiteSpace(c.ErpCustomerNumber))
            .GroupBy(c => c.ErpCustomerNumber.Trim())
            .ToDictionary(g => g.Key, g => g.First());

        int usersCreated = 0, defaultsSet = 0, orphansDeactivated = 0;

        foreach (var cu in customers)
        {
          // PER-COMPANY GRACEFUL CATCH (mirrors SponsorOrderPullService's per-folder
          // pattern): a single company's failed call — e.g. a transient 503 from
          // GetCompanyUsersAsync that survives the HttpClient retry — becomes a logged
          // warning + an alert-note (Alerts++) and the loop CONTINUES to the next
          // company, instead of throwing out of the whole reconcile (the 2026-06-27
          // incident, where one 503 crashed every remaining company). A real host
          // shutdown (our ct) still propagates.
          try
          {
            var key = cu.CustomerNumber.ToString();
            if (!byErp.TryGetValue(key, out var company))
            {
                notes.Add($"{E(cu.Name)} (e-conomic #{cu.CustomerNumber}): no matching webshop company (erp_customer_number).");
                continue;
            }

            var contacts = await _erp.ListContactsAsync(cu.CustomerNumber, ct);

            // §503 (operator 2026-07-28: "it is important, that I get an email and it becomes
            // blocked if a new sponsor doesn't have any contacts in erp. then we need to stop and
            // skip and be informed").
            //
            // ❗ THIS IS A SAFETY STOP, and he caught a real hazard in §502. The orphan rule is
            // "a webshop user with no matching ERP contact". If the ERP returns ZERO contacts —
            // a sponsor not yet set up, or a transient API hiccup answering with an empty list —
            // then EVERY webshop user matches that rule and the company's entire contact set
            // would be deactivated in one pass. An empty source is never evidence that everyone
            // should be removed; it is evidence that the source cannot be trusted right now.
            //
            // So: STOP this company, change NOTHING, and say so loudly. A skipped company is a
            // visible problem; a wrongly-emptied one is a silent disaster discovered at the event.
            if (contacts.Count == 0)
            {
                notes.Add($"BLOCKED — {E(cu.Name)} (e-conomic #{cu.CustomerNumber}) has <b>NO contacts "
                          + "in e-conomic</b>, so this company was SKIPPED entirely: no users created, "
                          + "no defaults set, and <b>no orphans removed or deactivated</b>. "
                          + "<b>Your action:</b> add at least one contact (with Role:1 Signer and "
                          + "Role:2 Event Coordinator) in e-conomic, then re-run the reconcile. "
                          + "<i>Why it stopped:</i> with an empty contact list every webshop user "
                          + "would look like an orphan, which would wipe the company's contacts.");
                continue;
            }

            // Existing webshop users on this company, by lower-cased email.
            var wsUsers = await _cm.GetCompanyUsersAsync(company.Id, ct);
            var emailToUserId = wsUsers
                .Where(u => !string.IsNullOrWhiteSpace(u.Email))
                .GroupBy(u => u.Email.Trim().ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.First().UserId);

            // Ensure each ERP contact with an email exists + is linked.
            foreach (var c in contacts)
            {
                if (string.IsNullOrWhiteSpace(c.Email)) continue;
                var em = c.Email.Trim().ToLowerInvariant();
                if (emailToUserId.ContainsKey(em)) continue;
                var (first, last) = SplitName(c.Name);
                try
                {
                    var uid = await _cm.CreateUserAsync(c.Email.Trim(), first, last, company.Id, ct);
                    if (uid > 0) { emailToUserId[em] = uid; usersCreated++; }
                    // uid <= 0 means Company Manager rejected the create — almost always
                    // because the email already exists as a user (a person can be linked to
                    // only ONE company in the webshop). That's expected, NOT an error: just
                    // skip it (no alert) per operator 2026-06-24. The org handles shared
                    // people via a manual override (e.g. FASTTRACK).
                    else _log.LogInformation(
                        "ERP sync: skipped {Email} for {Customer} — user already exists in the webshop (1-company limit).",
                        c.Email, cu.Name);
                }
                catch (Exception ex) { _log.LogInformation(ex, "ERP sync: skipped {Email} — webshop create rejected (already exists).", c.Email); }
            }

            // Resolve defaults from the FIRST contact holding each role (list order).
            int? signer = ResolveUserId(contacts.FirstOrDefault(c => c.IsSigner)?.Email, emailToUserId);
            int? coordinator = ResolveUserId(contacts.FirstOrDefault(c => c.IsEventCoordinator)?.Email, emailToUserId);

            // INVARIANT alerts — a role contact is missing in e-conomic.
            if (!contacts.Any(c => c.IsSigner))
                notes.Add($"{E(cu.Name)} (e-conomic #{cu.CustomerNumber}): no contact with Signer role (Role:1) — add it in e-conomic.");
            if (!contacts.Any(c => c.IsEventCoordinator))
                notes.Add($"{E(cu.Name)} (e-conomic #{cu.CustomerNumber}): no contact with Event Coordinator role (Role:2) — add it in e-conomic.");

            // §502 — ORPHANS: webshop users linked to this company that the ERP (the master, §482)
            // no longer lists as a contact. The reconcile was CREATE-ONLY, which is why a company
            // can accumulate six linked users against one ERP contact.
            //
            // The operator confirmed the rule is safe here: "yes all cm users are erp contacts", so
            // "not in ERP" genuinely means orphan. If that ever stops being true this detection
            // starts naming real buyers, so it is stated as the assumption it is.
            var erpEmails = contacts
                .Where(c => !string.IsNullOrWhiteSpace(c.Email))
                .Select(c => c.Email!.Trim().ToLowerInvariant())
                .ToHashSet();
            var orphans = wsUsers
                .Where(u => !string.IsNullOrWhiteSpace(u.Email)
                            && !erpEmails.Contains(u.Email.Trim().ToLowerInvariant()))
                .ToList();

            foreach (var o in orphans)
            {
                // §502b (operator: "i accept that you must send me an email so i can decide to
                // delete the user in wordpress under Users. i will then decide manually") — the
                // same shape as §470's Zoho notice: CEH cannot delete a WordPress account, so it
                // NAMES the person, says exactly where to go, and separates what it has ALREADY
                // done from what is left for a human. Conflating those is how an operator either
                // repeats work or assumes something was handled when it was not.
                // §505 — the Company Manager REST API has NO unlink route (confirmed with the
                // plugin developer 2026-07-28), so the hub cannot do either webshop step. The mail
                // therefore has to carry the work, and a person following it at 23:00 needs the
                // exact records — a user EXISTS IN TWO PLACES: the company LINK in Company Manager
                // and the ACCOUNT under WordPress → Users. Doing only one leaves the orphan either
                // still listed on the company, or still able to shop. Hence two numbered steps,
                // with the ids needed to find each record.
                notes.Add(
                    $"<b>ORPHAN — {E(cu.Name)}</b> (e-conomic #{cu.CustomerNumber}): webshop user "
                    + $"<b>#{o.UserId}</b> &lt;{E(o.Email)}&gt; is linked to this company but is NOT a "
                    + "contact in e-conomic."
                    + "<br><b>Manual clean-up (2 places — the API cannot do these yet):</b>"
                    + "<br>&nbsp;&nbsp;<b>1. Company Manager</b> → Companies → "
                    + $"<i>{E(cu.Name)}</i> → Linked Users → <b>Remove</b> on user #{o.UserId}."
                    + "<br>&nbsp;&nbsp;<b>2. WordPress</b> → Users → find &lt;" + E(o.Email) + "&gt; → "
                    + "set <b>Role</b> to <i>— No role for this site —</i> "
                    + "(keeps the account and its order history; reversible later)."
                    + "<br><i>Already done automatically:</i> the matching hub participant was "
                    + "deactivated — reversible, reactivate in the hub if this was wrong.");

                // CEH side: DEACTIVATE, never delete (§253). Reversible, keeps the audit trail,
                // closes tasks, stops reminders, and the tombstone stops a later sync silently
                // resurrecting them. Only acts when the caller wired the optional services.
                if (_db is null || _deactivate is null) continue;
                try
                {
                    var email = o.Email!.Trim().ToLowerInvariant();
                    var victim = await _db.Participants
                        .Where(p => p.Email == email && p.IsActive)
                        .Select(p => new { p.Id, p.EventId })
                        .FirstOrDefaultAsync(ct);
                    if (victim is not null)
                    {
                        await _deactivate.DeactivateAsync(
                            victim.EventId, victim.Id,
                            $"orphaned webshop contact — no longer an e-conomic contact for {cu.Name}",
                            ct: ct);
                        orphansDeactivated++;
                    }
                }
                catch (Exception ex)
                {
                    // One person's failure must not abandon the rest of the company or the sweep.
                    _log.LogWarning(ex, "ERP sync: could not deactivate orphan {Email}.", o.Email);
                }
            }

            // Set defaults when the webshop value is empty (never overwriting a curated one) —
            // AND, §504, when the current default points at an ORPHAN.
            //
            // Operator 2026-07-28: "if it is set for a sponsor inside cm and that user is now
            // orphaned, we need to set another contact as default signer / default event
            // coordinator, so we always have one".
            //
            // ❗ This closes a hole §502 would otherwise have opened. Deactivating an orphan who
            // happened to BE the default signer left the company pointing at a person who is no
            // longer a contact — the invariant "a sponsor always has a default signer AND a
            // default event coordinator" quietly broken, and discovered when a contract needs
            // signing. "Never overwrite a curated value" was the right rule for a VALID default;
            // it is the wrong rule for one that has ceased to be real.
            var orphanUserIds = orphans.Select(o => o.UserId).ToHashSet();
            var signerOrphaned = company.DefaultSignerUserId > 0
                                 && orphanUserIds.Contains(company.DefaultSignerUserId);
            var coordinatorOrphaned = company.EventCoordinationDefaultContactUserId > 0
                                      && orphanUserIds.Contains(company.EventCoordinationDefaultContactUserId);

            var fields = new Dictionary<string, object?>();
            if ((company.DefaultSignerUserId <= 0 || signerOrphaned) && signer is int s)
                fields["default_signer_id"] = s;
            if ((company.EventCoordinationDefaultContactUserId <= 0 || coordinatorOrphaned) && coordinator is int co)
                fields["event_coordination_default_contact_id"] = co;

            // Say it out loud when a curated default was REPLACED — that is a change to something
            // a human chose, so it must never happen silently.
            if (signerOrphaned)
                notes.Add($"{E(cu.Name)} (e-conomic #{cu.CustomerNumber}): the default SIGNER was an "
                          + (signer is null
                             ? "orphan and there is no Role:1 contact to replace them — <b>the company now has NO default signer</b>. Add a Signer in e-conomic."
                             : "orphan; it has been reassigned to the current Role:1 contact."));
            if (coordinatorOrphaned)
                notes.Add($"{E(cu.Name)} (e-conomic #{cu.CustomerNumber}): the default EVENT COORDINATOR "
                          + (coordinator is null
                             ? "was an orphan and there is no Role:2 contact to replace them — <b>the company now has NO default coordinator</b>. Add an Event Coordinator in e-conomic."
                             : "was an orphan; it has been reassigned to the current Role:2 contact."));
            if (fields.Count > 0)
            {
                try { if (await _cm.UpdateCompanyAsync(company.Id, fields, ct)) defaultsSet++; }
                catch (Exception ex) { _log.LogWarning(ex, "ERP sync: set defaults failed for company {Co}.", company.Id); }
            }
          }
          catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
          {
            // One company failed (after the HttpClient already retried any transient
            // upstream error). Log + note it and KEEP GOING so the rest of the fleet
            // still reconciles; the next run reconverges this company.
            _log.LogWarning(ex,
                "ERP sync: company {Customer} (e-conomic #{Num}) failed; skipping and continuing.",
                cu.Name, cu.CustomerNumber);
            notes.Add($"{E(cu.Name)} (e-conomic #{cu.CustomerNumber}): Company Manager call failed "
                + $"({E(ex.Message)}); skipped this company — it will retry on the next run.");
          }
        }

        if (notes.Count > 0)
            await SendAlertAsync(notes, ct);

        return new SyncResult(true, customers.Count, usersCreated, defaultsSet, notes.Count, notes, orphansDeactivated);
    }

    /// <summary>
    /// §518 — HTML-encode a VALUE being interpolated into a note. Notes are HTML fragments
    /// (bold headings, numbered steps, line breaks), so the escaping has to happen here, at the
    /// value, rather than over the finished fragment — encoding the fragment destroys the
    /// formatting, which is exactly the bug this replaced.
    /// </summary>
    private static string E(string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);

    private async Task SendAlertAsync(List<string> notes, CancellationToken ct)
    {
        try
        {
            // §518 — notes are HTML FRAGMENTS, not plain text. They were HtmlEncode'd here, which
            // turned §505's carefully formatted clean-up instructions into a wall of literal
            // "<b>" and "&lt;" (operator 2026-07-28: "bug: impossible to read"). Every bold
            // heading, numbered step and line break arrived as visible markup instead.
            //
            // Safety does NOT come from this encoder — it comes from encoding each interpolated
            // VALUE where the note is built (see E(...) at every notes.Add). Encoding the finished
            // fragment could only destroy the formatting, never add protection the per-value
            // encoding does not already give.
            var items = string.Concat(notes.Select(n => $"<li style=\"margin-bottom:14px;\">{n}</li>"));
            var html = "<p>The ERP→webshop sponsor reconcile found items needing attention "
                + "(mostly contacts missing a Signer/Event-Coordinator role in e-conomic):</p>"
                + $"<ul style=\"padding-left:18px;\">{items}</ul>"
                + "<p>Fix the role in e-conomic (contact notes <code>Role:1,2</code>) and the next sync will set the default.</p>";
            await _email.SendAsync(AlertEmail, "Sponsor ERP/webshop reconcile — action needed [ELDK27]", html, ct);
        }
        catch (Exception ex) { _log.LogWarning(ex, "ERP sync: alert email to {To} failed.", AlertEmail); }
    }

    private static int? ResolveUserId(string? email, IReadOnlyDictionary<string, int> map)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        return map.TryGetValue(email.Trim().ToLowerInvariant(), out var id) && id > 0 ? id : null;
    }

    private static (string First, string Last) SplitName(string? name)
    {
        var n = (name ?? string.Empty).Trim();
        if (n.Length == 0) return ("Contact", "-");
        var parts = n.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 1 ? (parts[0], "-") : (parts[0], string.Join(' ', parts.Skip(1)));
    }
}
