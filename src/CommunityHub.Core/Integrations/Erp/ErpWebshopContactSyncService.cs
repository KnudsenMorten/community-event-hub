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
    /// <summary>
    /// §1041b — optional; null means "assume writes are allowed", which is the pre-guard behaviour.
    /// </summary>
    private readonly IExternalWriteGuard? _writes;

    private readonly Data.CommunityHubDbContext? _db;
    private readonly Organizer.ParticipantDeactivationService? _deactivate;

    public ErpWebshopContactSyncService(
        EconomicContactAdminService erp, CompanyManagerClient cm, CompanyManagerOptions cmOptions,
        IEmailSender email, ILogger<ErpWebshopContactSyncService> log,
        Data.CommunityHubDbContext? db = null,
        Organizer.ParticipantDeactivationService? deactivate = null,
        // §891.4 — the address block lives on the e-conomic CUSTOMER, which only this client reads.
        // 🔒 Optional so an unconfigured/absent invoice client cannot stop the contact sync running;
        // the billing comparison then simply covers the e-mail fields it already has.
        IEconomicInvoiceClient? invoice = null,
        // §921 — the consecutive-failure gate, PER COMPANY. Optional so an unconfigured caller
        // still runs; without it the old behaviour (report every blip) applies.
        Diagnostics.JobFailureTracker? failures = null,
        // 🔴 §1041b — needed to tell "WordPress refused the value" apart from "this host is not
        // allowed to write at all". See the billing block for why that distinction is not cosmetic.
        IExternalWriteGuard? writes = null)
    {
        _writes = writes;
        _erp = erp;
        _cm = cm;
        _cmOptions = cmOptions;
        _email = email;
        _log = log;
        _db = db;
        _deactivate = deactivate;
        _invoice = invoice;
        _failures = failures;
    }

    private readonly IEconomicInvoiceClient? _invoice;
    private readonly Diagnostics.JobFailureTracker? _failures;

    /// <summary>
    /// 🔴 §921 — HOW LONG A COMPANY MUST KEEP FAILING BEFORE IT IS WORTH AN E-MAIL.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-06, the <b>NINTH</b> escalation of this same alert: <i>"if for some
    /// reason an api is down, I dont want to be boughtered with it when it happend 1 time, 2 time,
    /// 3 times, after 1 hour of issues like 6 x 10 min, it is ok to get notification … i have said
    /// it now 9 times!"</i></para>
    ///
    /// <para>🔴 <b>Why raising the threshold to 3 in §784.4 did not work, and why he had to say it
    /// again.</b> <see cref="Diagnostics.JobFailureTracker"/> gates the JOB — it is consulted when a
    /// job THROWS. This loop <b>catches the per-company exception on purpose</b> so one bad company
    /// cannot stop the fleet reconciling, so the job never throws, so the gate was never consulted.
    /// The note went straight into the alert every single time. <b>The fix was applied to the right
    /// idea and the wrong code path — twice.</b></para>
    ///
    /// <para>🔒 Six consecutive runs at the reconcile's 10-minute cadence is his stated hour.</para>
    /// </remarks>
    private const int CompanyFailureAlertThreshold = 6;

    /// <summary>
    /// §891.4 — the ERP customer's address block. Returns null on any failure: a billing address we
    /// could not READ must never be treated as a billing address that changed.
    /// </summary>
    /// <summary>
    /// §897 — read one Company Manager field back by its API key, so a write can be VERIFIED rather
    /// than trusted. Kept beside the push it serves: if a new field is added to the billing block
    /// and not added here, it reads as "refused" and is reported — which is the safe direction.
    /// </summary>
    private static string? ReadField(CompanyManagerCompany c, string apiKey) => apiKey switch
    {
        "billing_email" => c.BillingEmail,
        "billing_address_1" => c.BillingAddress1,
        "billing_address_2" => c.BillingAddress2,
        "billing_city" => c.BillingCity,
        "billing_state" => c.BillingState,
        "billing_postcode" => c.BillingPostcode,
        "billing_country" => c.BillingCountry,
        "billing_company" => c.BillingCompany,
        "email" => c.Email,
        "name" => c.Name,
        "company_name_public" => c.PublicName,
        _ => null,
    };

    private async Task<EconomicCustomerDetail?> SafeGetCustomerAsync(int customerNumber, CancellationToken ct)
    {
        try { return await _invoice!.GetCustomerAsync(customerNumber, ct); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ERP sync: could not read customer {Num} for billing compare.", customerNumber);
            return null;
        }
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
        // §1041b — how many companies had a billing write refused because this host may not write
        // to the webshop. Counted, then reported as ONE line rather than 53 misleading ones.
        var webshopWritesBlocked = 0;
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

            // 🔴 §891 — PROPAGATE A RENAME. Operator renamed CVR 32559735 from "SoftwareCentral A/S"
            // to "RoboPack A/S" in e-conomic and it reached nothing: this sync matched the company by
            // erp_customer_number and then only ever synced CONTACTS — the customer name was used in
            // log lines and never written anywhere.
            //
            // 🔑 The match key stays the ERP customer number. A rename is not a new customer, and
            // matching on NAME would both miss this and risk pairing two different companies.
            //
            // 🔑 §891.3 — A LEGAL-NAME CHANGE IS THE *ONLY* EVENT THAT LETS CEH TOUCH THE NAMES.
            // Operator 2026-08-06, final wording: *"the only time where i allow CEH to change the
            // public name is if the legal name (rename of company) happens"* · corrected to
            // *"otherwise company PUBLIC name must be owned by CM"*.
            //
            // So the trigger is the EVENT, not the field: nothing is written on an ordinary run, and
            // a rename writes BOTH names. That is why this compares first and pushes second —
            // syncing the legal name unconditionally would make CM's copy unowned, and pushing the
            // public name unconditionally would stamp on his marketing string
            // ("Robopack - empowered by SOFTWARECENTRAL") on every single run.
            var cmCompany = await _cm.GetCompanyAsync(company.Id, ct);
            var legalRenamed = cmCompany is not null
                && !string.IsNullOrWhiteSpace(cu.Name)
                && !string.Equals(cmCompany.Name, cu.Name, StringComparison.Ordinal);

            if (legalRenamed)
            {
                var push = new Dictionary<string, object?> { ["name"] = cu.Name };

                // The public name follows ONLY here — it usually contains the old company name, so
                // a rename is exactly when it becomes wrong. It is left alone every other run.
                var publicFollows = !string.IsNullOrWhiteSpace(cmCompany!.PublicName)
                    && !string.Equals(cmCompany.PublicName, cu.Name, StringComparison.Ordinal);
                if (publicFollows) push["company_name_public"] = cu.Name;

                var renameOk = false;
                try { renameOk = await _cm.UpdateCompanyAsync(company.Id, push, ct); }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "ERP sync: rename failed for company {Co}.", company.Id);
                }

                // §56 — reported, naming both old values, because a rename changes what sponsors
                // see on their own material.
                notes.Add(renameOk
                    ? $"RENAMED from ERP — e-conomic #{cu.CustomerNumber}: legal name "
                      + $"<i>{E(cmCompany.Name)}</i> → <b>{E(cu.Name)}</b>"
                      + (publicFollows
                            ? $", and the public name <i>{E(cmCompany.PublicName)}</i> followed it "
                              + "(a rename is the only time CEH touches that field — edit it if you "
                              + "want different wording)."
                            : ".")
                      + " Zoho is not touched by this sync."
                    : $"{E(cu.Name)} (e-conomic #{cu.CustomerNumber}): renamed in e-conomic (was "
                      + $"<i>{E(cmCompany!.Name)}</i>) but the Company Manager update FAILED — it will retry.");
            }

            // 🔑 §891.4 — BILLING FIELDS FOLLOW ERP ON EVERY RUN, UNCONDITIONALLY.
            // Operator 2026-08-06: *"This comparison must happen at every sync and be updated, as
            // contacts in the webshop will be wrong if data is not updated - erp is master"*.
            // ⚠️ Deliberately NOT event-driven like the names above (§891.3): CM owns the public
            // name, but it owns nothing in the billing block. Two rules on one record, on purpose.
            // 🔒 Field names are the REAL ones, read from GET /companies/{id} — not inferred from
            // the UI labels. A wrong key here is a silent no-op on a billing address.
            if (cmCompany is not null)
            {
                var billing = new Dictionary<string, object?>();
                void Follow(string key, string? erpValue, string? cmValue)
                {
                    if (string.IsNullOrWhiteSpace(erpValue)) return;          // ERP blank ⇒ leave CM alone
                    if (string.Equals(erpValue.Trim(), cmValue?.Trim(), StringComparison.OrdinalIgnoreCase)) return;
                    billing[key] = erpValue.Trim();
                }

                // 🔴 §897 — `email` IS NOT WRITABLE, PROVEN. Company Manager answers 200 with the
                // full record and even moves `updated_at`, but the value never changes. Pushing it
                // therefore "succeeded" on every run and reported a BILLING update that had not
                // happened — a mail to him every ten minutes, for twenty companies.
                // Measured 2026-08-06 on company 10: POST {email:"penaw@robopack.com"} → 200, and a
                // re-read still says penaw@softwarecentral.com.
                // 🔒 `billing_email` IS writable and is the one invoices actually use, so that is the
                // field we keep. Do NOT re-add `email` without re-running that read-back test.
                Follow("billing_email", cu.Email, cmCompany.BillingEmail);

                var detail = _invoice is null ? null
                    : await SafeGetCustomerAsync(cu.CustomerNumber, ct);
                if (detail is not null)
                {
                    // 🔑 §899 — e-conomic keeps ONE address field and lets it hold NEWLINES
                    // ("c/o Per Skanne\nSvennevägen 13"); Company Manager has TWO single-line
                    // fields. Pushing the whole thing into line 1 stored only part of it, so the
                    // §897 read-back correctly reported a value that "did not change" — and six
                    // companies were mailed as un-writable every ten minutes.
                    //
                    // 🔒 The field was never the problem: single-line addresses wrote perfectly.
                    // We were sending a two-line value into a one-line field. Split it.
                    var lines = (detail.Address ?? string.Empty)
                        .Replace("\r\n", "\n")
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => l.Trim())
                        .Where(l => l.Length > 0)
                        .ToList();

                    Follow("billing_address_1", lines.FirstOrDefault(), cmCompany.BillingAddress1);
                    // Everything after the first line goes to line 2 — joined rather than dropped,
                    // because an address that loses its "c/o" line is worse than a long line 2.
                    if (lines.Count > 1)
                        Follow("billing_address_2", string.Join(", ", lines.Skip(1)), cmCompany.BillingAddress2);

                    Follow("billing_city", detail.City, cmCompany.BillingCity);
                    Follow("billing_postcode", detail.Zip, cmCompany.BillingPostcode);

                    // ✅ §894 — MAP the country instead of refusing it. e-conomic stores free text
                    // ("Danmark", "USA", "United States Of America", "United Kingdom (UK)") while
                    // Company Manager stores an ISO-2 code.
                    //
                    // 🔴 The first version simply refused anything that was not already 2 letters and
                    // reported it — which produced SIXTY "not a 2-letter code" lines in one e-mail.
                    // A safety valve that becomes the noise it was meant to prevent is not a safety
                    // valve (operator: *"make a Country function mapper ... and dont throw this at me"*).
                    //
                    // 🔒 An UNMAPPED spelling still writes nothing and says nothing — mapping is the
                    // fix for names we recognise, not a licence to guess (§582).
                    var iso = CountryCodeMapper.ToIso2(detail.Country);
                    if (iso is not null) Follow("billing_country", iso, cmCompany.BillingCountry);
                }

                // 🔴 §1041b — IF THIS HOST MAY NOT WRITE, SAY THAT, AND SAY IT ONCE.
                //
                // ⚠️ Without this the §897 read-back logic below produces a message that is both
                // WRONG and HARMFUL. The guard refuses the write, `UpdateCompanyAsync` returns
                // false, `after` is null, and every field lands in `refused` — which is reported as
                // *"Company Manager did NOT store … the call succeeded but the value did not change.
                // Set it by hand"*. The call did not succeed; it was never made. On DEV that told
                // the operator to hand-fix 53 companies that were perfectly correct.
                //
                // 🔑 §897's read-back is right for what it was built for — WordPress accepting a
                // field and keeping the old value. It simply cannot distinguish "refused upstream"
                // from "never sent", because both look like "no read-back". So the caller has to.
                if (billing.Count > 0 && _writes is not null
                    && !await _writes.AllowAsync(ExternalSystems.Webshop, "BillingSync", ct))
                {
                    webshopWritesBlocked++;
                    billing.Clear();   // nothing below runs; no per-company hand-entry noise
                }

                if (billing.Count > 0)
                {
                    var ok = false;
                    try { ok = await _cm.UpdateCompanyAsync(company.Id, billing, ct); }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "ERP sync: billing update failed for company {Co}.", company.Id);
                    }

                    // 🔑 §897 — VERIFY THE WRITE BY READING IT BACK. A 200 is not evidence a field
                    // was stored: Company Manager accepts `email`, echoes it in the response, moves
                    // `updated_at`, and keeps the old value. Trusting the status code made the sync
                    // announce twenty BILLING updates every ten minutes that had never happened.
                    //
                    // 🔒 So the mail now reports what CHANGED, measured — never what we asked for.
                    // A field that silently refuses the write is named as such, once, instead of
                    // being re-announced as a success for ever.
                    var after = ok ? await _cm.GetCompanyAsync(company.Id, ct) : null;
                    var applied = new List<string>();
                    var refused = new List<string>();

                    foreach (var kv in billing)
                    {
                        var want = kv.Value as string;
                        var now = after is null ? null : ReadField(after, kv.Key);
                        if (after is null) { refused.Add(kv.Key); continue; }
                        if (string.Equals(now?.Trim(), want?.Trim(), StringComparison.OrdinalIgnoreCase))
                            applied.Add(kv.Key);
                        else
                            refused.Add(kv.Key);
                    }

                    if (applied.Count > 0)
                        notes.Add($"BILLING updated from ERP — {E(cu.Name)} (e-conomic #{cu.CustomerNumber}): "
                                  + E(string.Join(", ", applied)) + ".");

                    if (refused.Count > 0)
                        notes.Add($"⚠️ {E(cu.Name)} (e-conomic #{cu.CustomerNumber}): Company Manager "
                                  + $"did NOT store {E(string.Join(", ", refused))} — the call succeeded "
                                  + "but the value did not change. Set it by hand in Company Manager; "
                                  + "CEH cannot write that field.");
                }
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
                    else
                    {
                        // 🔴 §891.5 — THIS USED TO BE SILENT, AND THAT IS THE BUG HE HIT.
                        // Operator 2026-08-06 renamed three contacts' addresses in e-conomic; the
                        // creates were rejected, nothing was said, and the only visible symptom was
                        // a later "no Role:1 contact" alert about people who are plainly still there.
                        // A create we could not perform is a FAILURE, not a no-op (§854).
                        //
                        // ⚠️ The 2026-06-24 decision to stay quiet was about a SHARED person
                        // legitimately belonging to another company. That is still not an error —
                        // but it must be VISIBLE, because it is also exactly what a renamed address
                        // looks like, and he is the only one who can tell them apart.
                        _log.LogInformation(
                            "ERP sync: skipped {Email} for {Customer} — webshop rejected the create.",
                            c.Email, cu.Name);
                        notes.Add(
                            $"NOT LINKED — {E(cu.Name)} (e-conomic #{cu.CustomerNumber}): "
                            + $"<b>{E(c.Name)}</b> &lt;{E(c.Email)}&gt; could not be added to the webshop. "
                            + "Either that address already exists as a user (a person can belong to "
                            + "only ONE company), or the address was recently CHANGED in e-conomic and "
                            + "the old user still holds the seat. "
                            + "<b>Your action:</b> delete or re-link the old user in WordPress, then "
                            + "re-run — the new address will be created and linked. "
                            + "<i>Until then this contact holds no role here.</i>");
                    }
                }
                catch (Exception ex)
                {
                    _log.LogInformation(ex, "ERP sync: create rejected for {Email}.", c.Email);
                    notes.Add(
                        $"NOT LINKED — {E(cu.Name)} (e-conomic #{cu.CustomerNumber}): "
                        + $"<b>{E(c.Name)}</b> &lt;{E(c.Email)}&gt; was rejected by the webshop "
                        + $"({E(ex.Message)}). <b>Your action:</b> check that address in WordPress.");
                }
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

            // 🔴 §894.2 — A DELETED DEFAULT IS NOT AN ORPHAN, AND THAT IS WHY IT NEVER GOT FIXED.
            // "Orphan" means a user still LINKED to the company that the ERP no longer lists. When
            // the operator DELETED the old WordPress users (2026-08-06, to free the renamed
            // addresses), those ids stopped being linked at all — so they were never in the orphan
            // set, the guard below never fired, and Company Manager kept pointing default_signer_id
            // at user 115 and the coordinator at 51, both of which no longer exist.
            //
            // 🔑 The real question is not "was this user orphaned" but "is this user still one of
            // ours". A default that names nobody on the company is stale however it got that way —
            // deleted, unlinked, or moved to another company.
            var currentUserIds = emailToUserId.Values.ToHashSet();
            bool IsStale(int userId) => userId > 0
                && (orphanUserIds.Contains(userId) || !currentUserIds.Contains(userId));

            var signerOrphaned = IsStale(company.DefaultSignerUserId);
            var coordinatorOrphaned = IsStale(company.EventCoordinationDefaultContactUserId);

            var fields = new Dictionary<string, object?>();
            if ((company.DefaultSignerUserId <= 0 || signerOrphaned) && signer is int s)
                fields["default_signer_id"] = s;
            if ((company.EventCoordinationDefaultContactUserId <= 0 || coordinatorOrphaned) && coordinator is int co)
                fields["event_coordination_default_contact_id"] = co;

            // Say it out loud when a curated default was REPLACED — that is a change to something
            // a human chose, so it must never happen silently.
            if (signerOrphaned)
                notes.Add($"{E(cu.Name)} (e-conomic #{cu.CustomerNumber}): the default SIGNER no longer exists on this company "
                          + (signer is null
                             ? "and there is no Role:1 contact to replace them — <b>the company now has NO default signer</b>. Add a Signer in e-conomic."
                             : "— it has been reassigned to the current Role:1 contact."));
            if (coordinatorOrphaned)
                notes.Add($"{E(cu.Name)} (e-conomic #{cu.CustomerNumber}): the default EVENT COORDINATOR no longer exists on this company "
                          + (coordinator is null
                             ? "and there is no Role:2 contact to replace them — <b>the company now has NO default coordinator</b>. Add an Event Coordinator in e-conomic."
                             : "— it has been reassigned to the current Role:2 contact."));
            if (fields.Count > 0)
            {
                try { if (await _cm.UpdateCompanyAsync(company.Id, fields, ct)) defaultsSet++; }
                catch (Exception ex) { _log.LogWarning(ex, "ERP sync: set defaults failed for company {Co}.", company.Id); }
            }

            // 🔒 §921 — this company reconciled, so its failure streak is over. Without this reset
            // the counter would creep up across unrelated blips weeks apart and eventually alert on
            // a company that is perfectly healthy — an alert with no incident behind it, which is
            // the same deafness by a slower route.
            if (_failures is not null)
            {
                await _failures.RecordSuccessAsync($"erp-webshop-cm:{cu.CustomerNumber}", ct);
            }
          }
          catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
          {
            // One company failed (after the HttpClient already retried any transient
            // upstream error). Log + note it and KEEP GOING so the rest of the fleet
            // still reconciles; the next run reconverges this company.
            // 🔒 ALWAYS logged — observability is not what he objected to. The log is where a
            // transient blip belongs; his inbox is not.
            _log.LogWarning(ex,
                "ERP sync: company {Customer} (e-conomic #{Num}) failed; skipping and continuing.",
                cu.Name, cu.CustomerNumber);

            // 🔴 §921 — ONLY REPORT A COMPANY THAT HAS BEEN FAILING FOR AN HOUR.
            //
            // The HttpClient already retried the transient fault (TransientFaultRetryHandler:
            // 5xx/408/429/timeout), so arriving here means the retries were also exhausted — which
            // for a 503 simply means the far end is down right now. There is nothing he can do
            // about that, and telling him on the first tick is what taught him to skim these mails.
            var key = $"erp-webshop-cm:{cu.CustomerNumber}";
            var shouldReport = true;
            var consecutive = 0;

            if (_failures is not null)
            {
                var decision = await _failures.RecordFailureAsync(
                    key, ex.Message, ct, CompanyFailureAlertThreshold);
                shouldReport = decision.ShouldAlert;
                consecutive = decision.ConsecutiveFailures;
            }

            if (shouldReport)
            {
                notes.Add($"{E(cu.Name)} (e-conomic #{cu.CustomerNumber}): Company Manager has been "
                    + $"failing for <b>{consecutive} consecutive runs</b> (about "
                    + $"{consecutive * 10} minutes) — {E(ex.Message)}. This one needs looking at; "
                    + "everything else reconciled normally.");
            }
            else
            {
                _log.LogInformation(
                    "§921: company {Num} failed {Count}/{Threshold} consecutive runs — not reported yet.",
                    cu.CustomerNumber, consecutive, CompanyFailureAlertThreshold);
            }
          }
        }

        // 🔴 §1041b — LOGGED, NOT MAILED. Operator 2026-08-10: *"it was refused because it was the
        // dev env so it was positive"* … *"but this report is not relevant to see in dev, can we
        // turn it off"*.
        //
        // ⚠️ The mail this replaced said *"Company Manager did NOT store … set it by hand"* for all
        // 53 companies — describing a failure that had not happened and asking him to fix records
        // that were already correct. A policy refusal is not a finding.
        //
        // 🔑 §335 ("the only symptom is that nothing happens") is still satisfied without a mail:
        // the guard itself logs every refusal with the system and operation, this line gives the
        // per-run total, and the startup banner states the host's whole posture. What is removed is
        // the ALERT, not the evidence — and an alert nobody should act on trains people to ignore
        // the ones they should.
        if (webshopWritesBlocked > 0)
        {
            _log.LogInformation(
                "ERP→webshop: billing NOT pushed for {Count} company(ies) — this host may not write "
                + "to Company Manager (Integrations:ExternalWrites:Webshop). Expected on DEV; no "
                + "action needed. Not mailed (§1041b).",
                webshopWritesBlocked);
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

            // 🔑 §894.1 — THE HEADER AND FOOTER MUST NOT DESCRIBE A PROBLEM THAT IS NOT IN THE LIST.
            // Operator 2026-08-06: *"last line is also impossible to work with - who is the company /
            // contact"*. The mail always claimed the items were "mostly contacts missing a role" and
            // always closed with "fix the role in e-conomic" — regardless of what was actually in it.
            // On the run he complained about, the list was 60 country lines and a dozen billing
            // updates, and not one missing role. A standing instruction that names nobody is
            // unactionable, and it teaches him to stop reading the last line.
            var roleNotes = notes.Where(n =>
                n.Contains("Role:1", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Signer role", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Event Coordinator role", StringComparison.OrdinalIgnoreCase)
                || n.Contains("default SIGNER", StringComparison.Ordinal)
                || n.Contains("default EVENT COORDINATOR", StringComparison.Ordinal)).ToList();

            var html = "<p>The ERP→webshop sponsor reconcile has "
                + $"<b>{notes.Count}</b> item(s) to report:</p>"
                + $"<ul style=\"padding-left:18px;\">{items}</ul>"
                + (roleNotes.Count > 0
                    // Only when a role really IS missing — and it says how many, so the instruction
                    // points at lines above it rather than at nothing.
                    ? $"<p><b>{roleNotes.Count}</b> of the item(s) above are about a missing role. "
                      + "For those, set the contact's notes in e-conomic to <code>Role:1</code> "
                      + "(Signer) and/or <code>Role:2</code> (Event Coordinator) — the next sync then "
                      + "sets the default automatically.</p>"
                    // Everything else already carries its own action, so the mail ends there.
                    : "<p>Each line above states its own action. Nothing here needs a role change.</p>");
            // 🔑 §900 — THE SUBJECT MUST MATCH THE CONTENT. It read "action needed" on every run,
            // including one whose six lines were all "BILLING updated from ERP" — work CEH had
            // already done for him. Operator 2026-08-06: *"but why subject with ACTION NEEDED"*.
            // A subject that always cries wolf is one he stops opening, and this is the mail that
            // carries the genuine blockers: a contact that could not be linked, a missing role, a
            // field Company Manager refused.
            //
            // 🔒 Actionable = something he must DO. "Updated"/"Renamed from ERP" is a receipt.
            var actionable = notes.Count(n =>
                !n.StartsWith("BILLING updated from ERP", StringComparison.Ordinal)
                && !n.StartsWith("RENAMED from ERP", StringComparison.Ordinal));

            var subject = actionable > 0
                ? $"Sponsor ERP/webshop reconcile — {actionable} need(s) your attention [ELDK27]"
                : $"Sponsor ERP/webshop reconcile — {notes.Count} updated, nothing to do [ELDK27]";

            await _email.SendAsync(AlertEmail, subject, html, ct);
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
