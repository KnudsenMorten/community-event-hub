using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Erp;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// §787.2 — <b>Coupon invoicing</b>: which Backstage coupon is billed to which e-conomic customer.
/// </summary>
/// <remarks>
/// <para>Replaces <c>Settings\ZohoBackstageCoupons_Invoicing.csv</c> on the operator's VM
/// (<c>Couponname;CouponBillingDetails;ErpCustomerNumberInvoicing</c>), which he edited by hand.</para>
///
/// <para>🔑 <b>Why a page and not a config file</b> (his decision 2026-08-04): the retired script
/// mails *"ACTION REQUIRED: Coupon missing ERP mapping"* the moment an unmapped coupon is claimed,
/// and that alert is only useful if whoever receives it can act on it. A config JSON would mean the
/// alert arrives about something only a DEPLOY can fix, with the ticket uninvoiced until then.
/// <b>The alert and the fix belong in the same place.</b></para>
///
/// <para>🔴 <b>The ⚠ rows at the top are the whole point of this page.</b> A coupon that has been
/// CLAIMED but not mapped is a ticket nobody can bill — it is not a tidy-up task, it is lost revenue
/// with a clock on it. Everything else here is ordinary maintenance.</para>
///
/// <para>🔒 Read-mostly by design: it edits the MAPPING only. It never invoices, never contacts
/// e-conomic, and never marks anything as billed — that is <c>CouponInvoiceJob</c>'s work, gated by
/// its own feature key and by <c>Invoicing:DryRun</c> (§787.4/§788).</para>
/// </remarks>
[Authorize]
public class CouponInvoicingModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;
    private readonly IEconomicContactAdminClient? _economic;
    private readonly IEconomicInvoiceClient? _invoices;

    public CouponInvoicingModel(
        ICurrentParticipantAccessor participant, CommunityHubDbContext db, TimeProvider clock,
        IEconomicContactAdminClient? economic = null,
        IEconomicInvoiceClient? invoices = null)
    {
        _participant = participant;
        _db = db;
        _clock = clock;
        _economic = economic;
        _invoices = invoices;
    }

    /// <summary>
    /// §787.13 — the e-conomic customers, BY NAME, straight from the API.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-04: *"csv customer name mapper. are we not using api integr and a db
    /// table now. no files should be needed"*. He is right, and it was a fair hit: the point of
    /// §787.2 was to leave the CSV behind, and a paste box in CSV syntax quietly dragged
    /// file-thinking back in. A customer is chosen by NAME from the live API — nobody should be
    /// typing an account number read off a spreadsheet.</para>
    ///
    /// <para>🔒 It also makes the §787.13 defect impossible to repeat by hand: the CSV carried
    /// customer <c>111111111</c> for CBS, which does not exist in e-conomic at all. A number typed
    /// from a file can be wrong in a way a picked name cannot.</para>
    ///
    /// <para>⚠️ Empty when e-conomic is not configured — the page still works, it just falls back to
    /// the number box rather than pretending the list is empty.</para>
    /// </remarks>
    public IReadOnlyList<EconomicCustomerRow> Customers { get; private set; }
        = Array.Empty<EconomicCustomerRow>();

    public bool CustomerPickerAvailable => Customers.Count > 0;

    /// <summary>
    /// §795.4 — true when the invoice scan actually reached e-conomic. ⚠️ The distinction matters:
    /// "no invoice number" and "we could not ask" look identical on screen, and only one of them
    /// means the ticket is uninvoiced.
    /// </summary>
    public bool EconomicReachable { get; private set; }

    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }
    public string? Error { get; private set; }

    /// <summary>
    /// §797.3 — the flash that survives the POST → REDIRECT → GET.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-04, pasting <c>…/CouponInvoicing?handler=Save</c>: every handler used
    /// to return <c>Page()</c>, so the address bar kept the handler and <b>F5 re-submitted the
    /// form</b> — a second save, or a "already has a rule" error for something he had just done.</para>
    ///
    /// <para>🔒 The message has to live in <c>TempData</c> because the redirect throws the page model
    /// away; <see cref="Message"/> / <see cref="Error"/> stay as the properties the view reads, and
    /// the GET copies the flash into them. That keeps the view unchanged and the flash one-shot —
    /// TempData is read-and-remove, so a refresh does not re-show a stale "saved".</para>
    /// </remarks>
    /// <remarks>
    /// ⚠️ Backed by <c>TempData</c> DIRECTLY rather than by <c>[TempData]</c>: the attribute is
    /// applied by an MVC page filter, so the value only round-trips inside the full pipeline — the
    /// property alone carries nothing. That makes the one behaviour worth testing (does the message
    /// actually survive the redirect?) untestable, and it fails silently if the filter is ever not
    /// in play. The dictionary is the real thing; this just names the keys.
    /// </remarks>
    public string? Flash
    {
        get => TempData[FlashKey] as string;
        set => Set(FlashKey, value);
    }

    public string? FlashError
    {
        get => TempData[FlashErrorKey] as string;
        set => Set(FlashErrorKey, value);
    }

    private const string FlashKey = "CouponInvoicing.Flash";
    private const string FlashErrorKey = "CouponInvoicing.FlashError";
    private const string DraftKey = "CouponInvoicing.DraftCouponName";

    // ⚠️ Storing a null would leave an entry that reads as "there is a flash" to anything checking
    // for the key. Removing keeps "no message" and "no key" the same statement.
    private void Set(string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) TempData.Remove(key);
        else TempData[key] = value;
    }

    /// <summary>
    /// §797.3 — the coupon name he typed into the ADD form when the save was refused.
    /// </summary>
    /// <remarks>
    /// ⚠️ A redirect loses the posted fields. Every other input on this page is re-rendered from the
    /// row it belongs to, so this is the ONE genuinely typed value that would otherwise have to be
    /// typed again — and it is lost at exactly the moment somebody is being told they got it wrong.
    /// </remarks>
    public string? DraftCouponName
    {
        get => TempData[DraftKey] as string;
        set => Set(DraftKey, value);
    }

    /// <summary>
    /// One coupon as the page shows it: its rule, plus what the live order mirror says about it.
    /// </summary>
    /// <param name="Setting">The stored rule. Null when the coupon has been claimed but never mapped.</param>
    /// <param name="CouponName">The promo code, from the rule or from the claim.</param>
    /// <param name="ClaimCount">How many claimed tickets carry this coupon right now.</param>
    /// <param name="ClaimedValueDkk">
    /// What those claims are worth. ⚠️ From <c>base_price</c>, never <c>total</c> — a coupon that
    /// covers the ticket entirely leaves <c>total</c> at 0, and billing that would invoice the
    /// partner 0.00 DKK (§787.6, proven against the live feed).
    /// </param>
    /// <param name="Invoices">
    /// §795.4 — the e-conomic invoices raised for this coupon's claims, read back from the same scan
    /// the sweep already does. 🔒 NOT stored: the numbers live in e-conomic, and copying them into
    /// CEH would create a second source of truth that goes stale the moment one is booked, credited
    /// or deleted there.
    /// </param>
    /// <summary>
    /// §798.4 — one pool as the page needs it: the derived balance, plus the PURCHASES it is the sum
    /// of, because each one carries its own invoice number and can be topped up separately.
    /// </summary>
    public sealed record PoolView(
        CouponPoolBalance Balance,
        IReadOnlyList<CouponPrepaidPurchase> Purchases);

    /// <param name="DormantPools">
    /// 🔴 §799 — allocations left behind from when this coupon WAS prepaid. They do not count
    /// anywhere: not in the balance, not in the attention list, not in any alert.
    /// </param>
    public sealed record Row(
        CouponInvoicingSetting? Setting,
        string CouponName,
        int ClaimCount,
        decimal ClaimedValueDkk,
        IReadOnlyList<PoolView> Pools,
        IReadOnlyList<EconomicInvoiceReference> Invoices,
        IReadOnlyList<PoolView> DormantPools)
    {
        /// <summary>
        /// §794/§795.1 — a prepaid pool that is empty, negative, or nearly gone. A pool CLOSED BY
        /// HAND is excluded (<see cref="CouponPoolBalance.IsLow"/>): it is a decision, not a problem.
        /// </summary>
        public bool HasPoolTrouble => Pools.Any(p => p.Balance.IsLow);

        /// <summary>
        /// §795.2/§798.4 — a prepaid PURCHASE nobody has confirmed was billed. Per purchase, so a
        /// pool topped up after being invoiced once does not read as settled.
        /// </summary>
        public bool HasUnbilledPool => Pools.Any(p => !p.Balance.IsBilled);
        public CouponBillingType BillingType =>
            Setting?.BillingType ?? CouponBillingType.Unmapped;

        public int? ErpCustomerNumber => Setting?.ErpCustomerNumber;

        /// <summary>
        /// Claimed AND not billable, or a prepaid pool in trouble — the rows this page surfaces.
        /// </summary>
        /// <remarks>
        /// §794 — a pool at or below zero belongs at the top too: the next claimant on an exhausted
        /// pool gets a ticket nobody has paid for, and it is otherwise discovered by the attendee.
        /// </remarks>
        public bool NeedsAttention =>
            (ClaimCount > 0 && (Setting is null || Setting.NeedsAttention))
            || HasPoolTrouble
            // §795.2 — a prepayment nobody has recorded an invoice number for. It is not a mapping
            // gap, but it is the same KIND of problem: money that reaches nobody, silently. The
            // reminder mail chases it; this is where it gets fixed.
            || HasUnbilledPool;
    }

    public IReadOnlyList<Row> Rows { get; private set; } = Array.Empty<Row>();
    public int AttentionCount => Rows.Count(r => r.NeedsAttention);

    /// <summary>Total claims found in the mirrored orders — the number §787.4 says to sanity-check.</summary>
    public int TotalClaims { get; private set; }

    /// <summary>
    /// §794 — ticket class id → its last-seen name, discovered from the order mirror so the pool
    /// editor can offer real classes instead of asking anyone to type an 18-digit id.
    /// </summary>
    /// <remarks>
    /// ⚠️ Display and discovery only. Everything MATCHES on the id (§787.16); a class renamed in
    /// Backstage changes this label and nothing else.
    /// </remarks>
    public IReadOnlyDictionary<string, string> TicketClassLabels { get; private set; }
        = new Dictionary<string, string>();

    /// <summary>
    /// §798.1 — the e-conomic contacts of every customer named on this page, keyed by customer
    /// number, so each coupon's requester dropdown offers ONLY its own customer's people.
    /// </summary>
    /// <remarks>
    /// ⚠️ Fetched once per DISTINCT customer, not per coupon: partners repeat, and a call per row
    /// would multiply e-conomic traffic by the length of the list. 🔒 Fail-soft — an unreachable
    /// e-conomic leaves the dropdown empty and the coupon fully editable.
    /// </remarks>
    public IReadOnlyDictionary<int, IReadOnlyList<EconomicContactRow>> ContactsByCustomer
    { get; private set; } = new Dictionary<int, IReadOnlyList<EconomicContactRow>>();

    [BindProperty] public int SettingId { get; set; }
    [BindProperty] public string? CouponName { get; set; }
    [BindProperty] public CouponBillingType BillingType { get; set; }
    [BindProperty] public int? ErpCustomerNumber { get; set; }
    [BindProperty] public int? RequesterContactNumber { get; set; }
    [BindProperty] public string? Notes { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        // §797.3 — the flash from the POST that redirected here. Read-and-remove, so a refresh does
        // not re-show a stale "saved" for something that happened two page loads ago.
        Message = Flash;
        Error = FlashError;

        await LoadAsync(me.EventId, ct);
        return Page();
    }

    /// <summary>
    /// §797.3 — end a POST the same way every time: flash the outcome, then REDIRECT.
    /// </summary>
    /// <remarks>
    /// 🔒 Errors redirect too. Leaving the failure path on <c>Page()</c> would have been less work
    /// and would have left <c>?handler=Save</c> in the address bar for exactly the case somebody is
    /// most likely to press F5 on — the one that just told them something went wrong.
    /// </remarks>
    /// <remarks>
    /// ⚠️ NOT named <c>Redirect</c>: <see cref="PageModel"/> already has one, and hiding it would
    /// make <c>Redirect("…")</c> mean something different here than everywhere else in the app.
    /// </remarks>
    private IActionResult RedirectWithFlash(string? message = null, string? error = null)
    {
        Flash = message;
        FlashError = error;
        return RedirectToPage();
    }

    /// <summary>Create or update ONE coupon's rule.</summary>
    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var name = (CouponName ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            Error = "A coupon name is required.";
            return RedirectWithFlash(Message, Error);
        }

        // §797.3 — the redirect loses the posted fields, so the ADD form's typed name is carried
        // over. It is the only input on this page that is not re-rendered from its own row, and it
        // would be lost at exactly the moment somebody is being told they got it wrong.
        DraftCouponName = SettingId > 0 ? null : name;

        // 🔒 A billing type that BILLS must name who to bill. Refusing here rather than at invoice
        // time is deliberate: the job's refusal is a log line nobody reads, while this is the moment
        // the person who knows the answer is actually looking at the screen.
        var needsCustomer = BillingType is CouponBillingType.AllocatedPrepaymentByCustomer
                                        or CouponBillingType.ClaimableAdHocPaymentByCustomer;
        if (needsCustomer && ErpCustomerNumber is not > 0)
        {
            Error = $"'{name}' is set to a billing type that invoices someone, so it needs an "
                  + "e-conomic customer number.";
            return RedirectWithFlash(Message, Error);
        }

        // ⚠️ NoInvoicing must not keep a stale customer number — it would read as "we bill them"
        // to the next person who looks, and NoInvoicing means the opposite.
        var customer = needsCustomer ? ErpCustomerNumber : null;

        var existing = SettingId > 0
            ? await _db.CouponInvoicingSettings
                .FirstOrDefaultAsync(c => c.Id == SettingId && c.EventId == me.EventId, ct)
            : await _db.CouponInvoicingSettings.FirstOrDefaultAsync(
                c => c.EventId == me.EventId && c.CouponName == name, ct);

        // §798.1 — the requester must belong to THIS coupon's customer. A contact number from
        // another customer is one e-conomic rejects, or worse accepts against the wrong account, so
        // it is dropped rather than saved: changing the customer clears the requester.
        int? requester = null;
        string? requesterName = null;
        if (customer is { } cust && RequesterContactNumber is > 0)
        {
            await LoadContactsAsync(new[] { cust }, ct);
            ContactsByCustomer.TryGetValue(cust, out var contacts);
            var match = contacts?.FirstOrDefault(c => c.ContactNumber == RequesterContactNumber);

            if (match is not null)
            {
                requester = match.ContactNumber;
                requesterName = match.Name;
            }
            else if (contacts is null || contacts.Count == 0)
            {
                // 🔒 e-conomic unreachable: keep what the number says rather than silently wiping a
                // requester because a third party is down. The stored name is left as it was — it is
                // the copy that goes stale, and inventing one here would be worse than an old one.
                requester = RequesterContactNumber;
                requesterName = existing?.RequesterName;
            }
        }

        var now = _clock.GetUtcNow();
        if (existing is null)
        {
            _db.CouponInvoicingSettings.Add(new CouponInvoicingSetting
            {
                EventId = me.EventId,
                CouponName = name,
                BillingType = BillingType,
                ErpCustomerNumber = customer,
                RequesterContactNumber = requester,
                RequesterName = requesterName,
                Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim(),
                CreatedAt = now,
                UpdatedAt = now,
                LastUpdatedByEmail = me.Email,
            });
            Message = $"'{name}' mapped.";
            DraftCouponName = null;   // it saved — nothing to type again
        }
        else
        {
            existing.CouponName = name;
            existing.BillingType = BillingType;
            existing.ErpCustomerNumber = customer;
            existing.RequesterContactNumber = requester;
            existing.RequesterName = requesterName;
            existing.Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim();
            existing.UpdatedAt = now;
            existing.LastUpdatedByEmail = me.Email;
            Message = $"'{name}' updated.";
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The (EventId, CouponName) unique index — a second row for the same coupon would mean
            // two contradictory rules and an invoice that depends on which one is read first.
            Error = $"'{name}' already has a rule in this edition — edit that one instead.";
        }

        return RedirectWithFlash(Message, Error);
    }

    // ⚰️ §787.13 — the BULK CSV IMPORT IS DELETED. Operator 2026-08-04: *"csv customer name
    // mapper. are we not using api integr and a db table now. no files should be needed"*.
    //
    // 🔒 He is right, and the reason is worth keeping: §787.2 moved this mapping OFF a file on a VM
    // and into a table with a page precisely so the alert and the fix could live together. A paste
    // box in the retired file's syntax quietly dragged the file back in — and it invited the exact
    // defect §787.13 found, a customer NUMBER (111111111) copied from a spreadsheet that does not
    // exist in e-conomic at all. Customers are picked BY NAME from the live API instead.
    //
    // ⚠️ Do not reintroduce it "just for the initial load". The initial load is the one moment the
    // numbers are least trustworthy.
    [BindProperty] public string? PoolTicketClassId { get; set; }
    [BindProperty] public int PoolQuantity { get; set; }
    [BindProperty] public int? PoolThreshold { get; set; }

    /// <summary>
    /// §794/§798.4 — buy tickets into a pool: the first purchase, or a TOP-UP of an existing one.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-04: *"lets say arrow dk buys 50 prepaid coupons"* … *"I must be able to
    /// extend a prepaid pool (increase amount) if customer decides t buy more, but dont want a new
    /// coupon code. Then i will invoice him for fx 25 more as invoice #."*</para>
    ///
    /// <para>🔒 <b>Adding tickets ADDS A PURCHASE ROW; it never edits a total.</b> That is what keeps
    /// "which invoice covers which tickets" answerable, and it is why the balance still cannot drift
    /// (§794.4): purchased is the SUM of these rows, claimed is derived from the order mirror, and
    /// neither is ever written down as a running figure.</para>
    /// </remarks>
    public async Task<IActionResult> OnPostAddTicketsAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var classId = (PoolTicketClassId ?? string.Empty).Trim();
        if (SettingId <= 0 || classId.Length == 0)
        {
            Error = "Pick a coupon and a ticket class for the allocation.";
            return RedirectWithFlash(Message, Error);
        }

        // ⚠️ A purchase of 0 or fewer is not an agreement. Removing a pool is its own button, so
        // "quantity 0" no longer has to double as delete — which it did before §798.4 and which
        // would now be ambiguous with "add nothing".
        if (PoolQuantity <= 0)
        {
            Error = "How many tickets did the partner buy? Use 'Remove allocation' to delete a pool.";
            return RedirectWithFlash(Message, Error);
        }

        var rule = await _db.CouponInvoicingSettings
            .FirstOrDefaultAsync(c => c.Id == SettingId && c.EventId == me.EventId, ct);
        if (rule is null)
        {
            Error = "That coupon no longer exists.";
            return RedirectWithFlash(Message, Error);
        }

        var pool = await _db.CouponPrepaidAllocations
            .Include(a => a.Purchases)
            .FirstOrDefaultAsync(
                a => a.CouponInvoicingSettingId == rule.Id && a.TicketClassId == classId, ct);

        var now = _clock.GetUtcNow();
        var isTopUp = pool is not null;

        if (pool is null)
        {
            pool = new CouponPrepaidAllocation
            {
                EventId = me.EventId,
                CouponInvoicingSettingId = rule.Id,
                TicketClassId = classId,
                TicketClassLabel = TicketClassLabels.TryGetValue(classId, out var lbl) ? lbl : null,
                LowBalanceThreshold = PoolThreshold,
                CreatedAt = now,
                UpdatedAt = now,
                LastUpdatedByEmail = me.Email,
            };
            _db.CouponPrepaidAllocations.Add(pool);
        }
        else
        {
            if (PoolThreshold is not null) pool.LowBalanceThreshold = PoolThreshold;
            if (TicketClassLabels.TryGetValue(classId, out var lbl2)) pool.TicketClassLabel = lbl2;
            pool.UpdatedAt = now;
            pool.LastUpdatedByEmail = me.Email;
        }

        pool.Purchases.Add(new CouponPrepaidPurchase
        {
            Quantity = PoolQuantity,
            // 🔒 The invoice number is OPTIONAL here on purpose: he agrees the tickets first and
            // raises the invoice in e-conomic afterwards. Until it is filled in, §795.2 chases THIS
            // purchase — not the pool, which may well have been invoiced for its earlier tickets.
            ErpInvoiceNumber = string.IsNullOrWhiteSpace(PoolErpInvoiceNumber)
                ? null : PoolErpInvoiceNumber.Trim(),
            ErpInvoiceConfirmedAt = string.IsNullOrWhiteSpace(PoolErpInvoiceNumber) ? null : now,
            ErpInvoiceConfirmedByEmail = string.IsNullOrWhiteSpace(PoolErpInvoiceNumber)
                ? null : me.Email,
            Notes = string.IsNullOrWhiteSpace(PoolPurchaseNotes) ? null : PoolPurchaseNotes.Trim(),
            CreatedAt = now,
            CreatedByEmail = me.Email,
        });

        await _db.SaveChangesAsync(ct);

        Message = isTopUp
            ? $"'{rule.CouponName}': {PoolQuantity} more ticket(s) added to the pool."
            : $"'{rule.CouponName}': pool created with {PoolQuantity} ticket(s).";

        return RedirectWithFlash(Message, Error);
    }

    /// <summary>§798.4 — change the warning level on a pool without buying anything.</summary>
    public async Task<IActionResult> OnPostSetPoolThresholdAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var pool = await _db.CouponPrepaidAllocations
            .Include(a => a.CouponInvoicingSetting)
            .FirstOrDefaultAsync(a => a.Id == PoolAllocationId && a.EventId == me.EventId, ct);

        if (pool is null)
        {
            Error = "That allocation no longer exists.";
            return RedirectWithFlash(Message, Error);
        }

        pool.LowBalanceThreshold = PoolThreshold is > 0 ? PoolThreshold : null;
        pool.UpdatedAt = _clock.GetUtcNow();
        pool.LastUpdatedByEmail = me.Email;
        await _db.SaveChangesAsync(ct);

        Message = $"'{pool.CouponInvoicingSetting?.CouponName}': warning level set to "
                + $"{pool.LowBalanceThreshold?.ToString() ?? CouponPrepaidBalance.DefaultLowBalanceThreshold + " (default)"}.";

        return RedirectWithFlash(Message, Error);
    }

    /// <summary>
    /// §798.4 — remove a pool entirely (its purchases go with it).
    /// </summary>
    /// <remarks>
    /// ⚠️ Its own button rather than "set the quantity to 0": with top-ups, a zero quantity means
    /// "buy nothing", and deleting an agreement on that reading would be a very expensive typo.
    /// </remarks>
    public async Task<IActionResult> OnPostRemovePoolAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var pool = await _db.CouponPrepaidAllocations
            .Include(a => a.CouponInvoicingSetting)
            .FirstOrDefaultAsync(a => a.Id == PoolAllocationId && a.EventId == me.EventId, ct);

        if (pool is not null)
        {
            var coupon = pool.CouponInvoicingSetting?.CouponName;
            _db.CouponPrepaidAllocations.Remove(pool);
            await _db.SaveChangesAsync(ct);
            Message = $"'{coupon}': allocation removed, including its purchase history.";
        }

        return RedirectWithFlash(Message, Error);
    }

    [BindProperty] public int PoolAllocationId { get; set; }
    [BindProperty] public int PoolPurchaseId { get; set; }
    [BindProperty] public string? PoolPurchaseNotes { get; set; }
    [BindProperty] public string? PoolClosedReason { get; set; }
    [BindProperty] public string? PoolErpInvoiceNumber { get; set; }

    /// <summary>
    /// §795.1 — close a pool EARLY, or re-open one that was closed by hand.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-04: *"we need a state of pool (open, closed when used in full)"*.</para>
    ///
    /// <para>🔴 <b>Only the human close is stored.</b> "Used in full" is derived on every read
    /// (<c>remaining &lt;= 0</c>), so it re-opens by itself the moment a ticket is cancelled and the
    /// allocation genuinely comes back. An agreement somebody ENDED must not re-open that way — this
    /// handler is the only thing that can undo it.</para>
    /// </remarks>
    public async Task<IActionResult> OnPostClosePoolAsync(bool reopen, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var pool = await _db.CouponPrepaidAllocations
            .Include(a => a.CouponInvoicingSetting)
            .FirstOrDefaultAsync(a => a.Id == PoolAllocationId && a.EventId == me.EventId, ct);

        if (pool is null)
        {
            Error = "That allocation no longer exists.";
            return RedirectWithFlash(Message, Error);
        }

        var now = _clock.GetUtcNow();
        if (reopen)
        {
            pool.ClosedAt = null;
            pool.ClosedByEmail = null;
            pool.ClosedReason = null;
            Message = $"'{pool.CouponInvoicingSetting?.CouponName}': allocation re-opened.";
        }
        else
        {
            pool.ClosedAt = now;
            pool.ClosedByEmail = me.Email;
            pool.ClosedReason = string.IsNullOrWhiteSpace(PoolClosedReason) ? null : PoolClosedReason.Trim();
            // ⚠️ Said plainly, because closing does NOT stop Zoho accepting the code. Backstage has
            // no coupon API (§787.14), so a closed pool still lets a claim through — it just stops
            // reading as an open agreement here, and any claim after this shows as oversubscribed.
            Message = $"'{pool.CouponInvoicingSetting?.CouponName}': allocation closed. The promo "
                    + "code is still live in Backstage — close it there too if nobody should claim it.";
        }

        pool.UpdatedAt = now;
        pool.LastUpdatedByEmail = me.Email;
        await _db.SaveChangesAsync(ct);

        return RedirectWithFlash(Message, Error);
    }

    /// <summary>
    /// §795.2 — record the e-conomic invoice number that confirms the PREPAYMENT was billed.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-04: *"we need to link an invoice number manually from erp for prepaid
    /// as confirmation it was billed. otherwise reminder mails"*.</para>
    ///
    /// <para>🔒 A confirmation, not a link CEH validates: CEH never raises this invoice (§794), so
    /// nothing here can look it up. Entering it stops the reminder; CLEARING it starts the chase
    /// again on the next pass, which is why the reminder stamp is reset below — a wrong number
    /// removed must not buy another week of silence.</para>
    /// </remarks>
    public async Task<IActionResult> OnPostSetPoolInvoiceAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        // §798.4 — against ONE PURCHASE, not the pool. "50 in January on 20147, 25 more in March on
        // 20233" is the case his sentence describes, and a pool-level field could only hold one.
        var purchase = await _db.CouponPrepaidPurchases
            .Include(p => p.Allocation!).ThenInclude(a => a.CouponInvoicingSetting)
            .FirstOrDefaultAsync(
                p => p.Id == PoolPurchaseId && p.Allocation!.EventId == me.EventId, ct);

        if (purchase is null)
        {
            Error = "That purchase no longer exists.";
            return RedirectWithFlash(Message, Error);
        }

        var number = (PoolErpInvoiceNumber ?? string.Empty).Trim();
        var now = _clock.GetUtcNow();
        var coupon = purchase.Allocation?.CouponInvoicingSetting?.CouponName;

        if (number.Length == 0)
        {
            purchase.ErpInvoiceNumber = null;
            purchase.ErpInvoiceConfirmedAt = null;
            purchase.ErpInvoiceConfirmedByEmail = null;
            // 🔒 Reset the quiet period too: an unbilled purchase must be chased on the next run,
            // not a week after somebody removed the number.
            purchase.LastBillingReminderAt = null;
            Message = $"'{coupon}': invoice number cleared on the {purchase.Quantity}-ticket "
                    + "purchase — it counts as not billed again, and will be chased.";
        }
        else
        {
            purchase.ErpInvoiceNumber = number;
            purchase.ErpInvoiceConfirmedAt = now;
            purchase.ErpInvoiceConfirmedByEmail = me.Email;
            Message = $"'{coupon}': {purchase.Quantity} ticket(s) billed on e-conomic invoice {number}.";
        }

        await _db.SaveChangesAsync(ct);

        return RedirectWithFlash(Message, Error);
    }

    public async Task<IActionResult> OnPostDeleteAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var row = await _db.CouponInvoicingSettings
            .FirstOrDefaultAsync(c => c.Id == SettingId && c.EventId == me.EventId, ct);
        if (row is not null)
        {
            _db.CouponInvoicingSettings.Remove(row);
            await _db.SaveChangesAsync(ct);
            // ⚠️ Deleting a rule does NOT stop the coupon being claimed — it reverts to Unmapped and
            // comes back to the top of this page. Said plainly so it is not mistaken for "ignore it".
            Message = $"Rule for '{row.CouponName}' deleted — it counts as UNMAPPED again, so any "
                    + "claim on it will reappear above as needing attention.";
        }

        return RedirectWithFlash(Message, Error);
    }

    private async Task LoadAsync(int eventId, CancellationToken ct)
    {
        // §787.13 — the customer list by NAME, from the API. Fail-soft: e-conomic being unreachable
        // must not take down a page whose main job (the mapping) lives in our own database.
        if (_economic is not null)
        {
            try
            {
                Customers = (await _economic.ListCustomersAsync(search: null, ct: ct))
                    .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            catch
            {
                Customers = Array.Empty<EconomicCustomerRow>();
            }
        }

        var settings = await _db.CouponInvoicingSettings
            .Where(c => c.EventId == eventId)
            .ToListAsync(ct);

        // 🔑 The live claims, read from CEH's OWN mirror of the Backstage /orders feed — no Zoho
        // call (§525: an hourly job minting its own token is how the refresh-grant limit was tripped
        // and every sync went dark against a valid credential).
        var raw = await _db.Orders
            .Where(o => o.EventId == eventId)
            .Select(o => o.RawJson)
            .ToListAsync(ct);

        var claims = raw
            .SelectMany(j => CouponClaimExtractor.FromOrderJson(j))
            .ToList();
        TotalClaims = claims.Count;

        // §794 — the classes actually seen, so the pool editor offers them by name.
        TicketClassLabels = claims
            .Where(c => c.TicketClassId.Length > 0)
            .GroupBy(c => c.TicketClassId, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.TicketClassName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
                     ?? g.Key,
                StringComparer.Ordinal);

        var byCoupon = claims
            .GroupBy(c => c.CouponName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (Count: g.Count(), Value: g.Sum(x => x.UnitPriceDkk)),
                StringComparer.OrdinalIgnoreCase);

        // §794/§798.4 — the prepaid allocations WITH their purchases (the pool is the sum of them),
        // and the LIVE claims each pool is spent by. Claims are grouped by coupon so a pool is only
        // ever balanced against its own partner's usage.
        var allocations = (await _db.CouponPrepaidAllocations
                .Where(a => a.EventId == eventId)
                .Include(a => a.Purchases)
                .ToListAsync(ct))
            .GroupBy(a => a.CouponInvoicingSettingId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<CouponPrepaidAllocation>)g.ToList());

        var claimsByCoupon = claims
            .GroupBy(c => c.CouponName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<CouponClaim>)g.ToList(),
                StringComparer.OrdinalIgnoreCase);

        // 🔴 §799 — a pool only counts while the coupon is ACTUALLY prepaid. Operator 2026-08-04:
        // *"i chaned type of coupon from claimed to prepaid and back to claimed. then count was
        // still showing even after page refresh"*. The allocation survives the type change on
        // purpose (it records money — see DormantPoolsFor), but a coupon billed ad hoc has no pool,
        // so showing one made the page disagree with the sweep and both alert services, all three of
        // which have always filtered on IsPrepaid.
        IReadOnlyList<PoolView> PoolsFor(CouponInvoicingSetting s) =>
            s.IsPrepaid ? BuildPools(s) : Array.Empty<PoolView>();

        /// <summary>The same rows, when the coupon is NOT prepaid — shown, but counted nowhere.</summary>
        IReadOnlyList<PoolView> DormantPoolsFor(CouponInvoicingSetting s) =>
            s.IsPrepaid ? Array.Empty<PoolView>() : BuildPools(s);

        IReadOnlyList<PoolView> BuildPools(CouponInvoicingSetting s)
        {
            if (!allocations.TryGetValue(s.Id, out var mine)) return Array.Empty<PoolView>();
            claimsByCoupon.TryGetValue(s.CouponName, out var theirs);
            var claims = theirs ?? Array.Empty<CouponClaim>();

            return CouponPrepaidBalance.ForCoupon(mine, claims)
                .Select(b => new PoolView(
                    b,
                    // §798.4 — oldest purchase first: it reads as a history of what was agreed, and
                    // the top-up he is chasing an invoice for is the one at the bottom.
                    mine.First(a => a.Id == b.AllocationId).Purchases
                        .OrderBy(p => p.CreatedAt).ThenBy(p => p.Id).ToList()))
                .ToList();
        }

        // §795.4 — the invoice NUMBERS, read back from e-conomic. 🔑 No new integration and no extra
        // traffic: this is the scan the sweep already runs for its idempotency check, which used to
        // throw the numbers away.
        //
        // ⚠️ Fail-soft, like the customer list above: e-conomic being unreachable must not take down
        // a page whose main job lives in CEH's own database. Empty then reads as "no number known",
        // which is the honest answer — never as "not invoiced".
        var invoiceRefs = new Dictionary<string, List<EconomicInvoiceReference>>(StringComparer.Ordinal);
        if (_invoices is not null)
        {
            try
            {
                foreach (var inv in await _invoices.ListInvoiceReferencesAsync(ct))
                {
                    if (!invoiceRefs.TryGetValue(inv.Reference, out var list))
                    {
                        invoiceRefs[inv.Reference] = list = new List<EconomicInvoiceReference>();
                    }
                    list.Add(inv);
                }
                EconomicReachable = true;
            }
            catch
            {
                invoiceRefs.Clear();
                EconomicReachable = false;
            }
        }

        IReadOnlyList<EconomicInvoiceReference> InvoicesFor(string couponName)
        {
            if (invoiceRefs.Count == 0) return Array.Empty<EconomicInvoiceReference>();
            if (!claimsByCoupon.TryGetValue(couponName, out var mine)) return Array.Empty<EconomicInvoiceReference>();

            return mine
                .Select(c => c.Reference)
                .Distinct(StringComparer.Ordinal)
                .SelectMany(r => invoiceRefs.TryGetValue(r, out var found)
                    ? found : Enumerable.Empty<EconomicInvoiceReference>())
                // Booked first — a booked number is the one a partner recognises; a draft is still work.
                .OrderByDescending(i => i.IsBooked)
                .ThenBy(i => i.Number)
                .ToList();
        }

        var rows = settings
            .Select(s => new Row(
                s, s.CouponName,
                byCoupon.TryGetValue(s.CouponName, out var m) ? m.Count : 0,
                byCoupon.TryGetValue(s.CouponName, out var v) ? v.Value : 0m,
                PoolsFor(s),
                InvoicesFor(s.CouponName),
                DormantPoolsFor(s)))
            .ToList();

        // Claimed coupons with NO rule at all. These are the ones the retired script would have
        // e-mailed about, and they must appear here whether or not anyone has created a row yet —
        // otherwise the page can only show what someone already knew about.
        var known = settings.Select(s => s.CouponName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        rows.AddRange(byCoupon
            .Where(kv => !known.Contains(kv.Key))
            .Select(kv => new Row(
                null, kv.Key, kv.Value.Count, kv.Value.Value, Array.Empty<PoolView>(),
                InvoicesFor(kv.Key), Array.Empty<PoolView>())));

        // Needs-attention first, then the busiest coupons, then alphabetically — the order a person
        // triaging this would put them in.
        Rows = rows
            .OrderByDescending(r => r.NeedsAttention)
            .ThenByDescending(r => r.ClaimCount)
            .ThenBy(r => r.CouponName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // §798.1 — the requester dropdowns, one fetch per DISTINCT customer on the page.
        await LoadContactsAsync(
            Rows.Select(r => r.ErpCustomerNumber).Where(n => n is > 0).Select(n => n!.Value), ct);
    }

    /// <summary>
    /// §798.1 — load the e-conomic contacts for the given customers, skipping any already loaded.
    /// </summary>
    /// <remarks>
    /// 🔒 Fail-soft per customer: one unreadable customer must not empty every other dropdown on the
    /// page, and e-conomic being down must not stop a coupon being mapped at all.
    /// </remarks>
    private async Task LoadContactsAsync(IEnumerable<int> customerNumbers, CancellationToken ct)
    {
        if (_economic is null) return;

        var map = ContactsByCustomer.ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var number in customerNumbers.Distinct().Where(n => n > 0))
        {
            if (map.ContainsKey(number)) continue;
            try
            {
                map[number] = (await _economic.ListContactsAsync(number, ct))
                    .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            catch
            {
                map[number] = Array.Empty<EconomicContactRow>();
            }
        }

        ContactsByCustomer = map;
    }

    /// <summary>
    /// §798.3 — the operator's own words for a billing type, for the dropdown.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-04: *"can you rename the display name of the Billing type to 'Prepaid
    /// tickets with ticket-pool' and 'Ad-hoc invoicing when claimed'"*.</para>
    ///
    /// <para>🔒 <b>DISPLAY ONLY — the enum values are untouched.</b> They are persisted as integers
    /// and read by the sweep, the pool and the alerts; renaming the stored contract to relabel a
    /// dropdown would be a silent data change. ⚠️ The dropdown used to render the raw enum NAME,
    /// which is exactly why it read like code.</para>
    /// </remarks>
    public static string DisplayName(CouponBillingType t) => t switch
    {
        CouponBillingType.AllocatedPrepaymentByCustomer => "Prepaid tickets with ticket-pool",
        CouponBillingType.ClaimableAdHocPaymentByCustomer => "Ad-hoc invoicing when claimed",
        CouponBillingType.NoInvoicing => "No invoicing — free or included",
        _ => "Not mapped yet",
    };

    public static string Describe(CouponBillingType t) => t switch
    {
        CouponBillingType.AllocatedPrepaymentByCustomer =>
            "Prepaid tickets with ticket-pool — the partner pays up front for an allocation, and every claim draws on it",
        CouponBillingType.ClaimableAdHocPaymentByCustomer =>
            "Ad-hoc invoicing when claimed — anyone with the code can claim it, first come first served; the partner is invoiced for what is claimed",
        CouponBillingType.NoInvoicing =>
            "No invoicing — free or already included, deliberately never billed",
        _ => "Not mapped yet — nobody has said who pays",
    };
}
