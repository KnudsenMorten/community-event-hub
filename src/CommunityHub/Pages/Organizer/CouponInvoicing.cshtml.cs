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
    private readonly CouponCustomerMonitorProvisioner? _monitorProvisioner;

    /// <summary>
    /// §1093 — the customer's real name for the monitor label, or null to fall back to the number.
    /// </summary>
    /// <remarks>
    /// ⚠️ Returns null when the customer list has not been loaded on this request (the save path
    /// does not always load it). That is deliberate: a monitor named "customer 1234" that EXISTS is
    /// worth more than a prettier one that does not, and the organizer can rename it.
    /// </remarks>
    private string? CustomerNameOrNull(int customerNumber) =>
        Customers.FirstOrDefault(c => c.CustomerNumber == customerNumber)?.Name;

    /// <summary>
    /// §1116 — the customer as a PERSON reads them: the name, with the number in brackets.
    /// </summary>
    /// <remarks>
    /// Operator 2026-08-21: *"it was the same with customer id - noboddy knows a 13 digit customer
    /// id - use names"*. The number stays because it is what he SEARCHES on in e-conomic; it is just
    /// not the identity. Falls back to the bare number only when the page's customer list has not
    /// been loaded — a number is still better than nothing at all.
    /// </remarks>
    public string CustomerLabel(int customerNumber)
    {
        var name = CustomerNameOrNull(customerNumber);
        var no = customerNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(name) ? no : $"{name} ({no})";
    }

    /// <summary>
    /// §1109 — the customer's name for a mail, falling back to e-conomic when the page's list has
    /// not been loaded on this request.
    /// </summary>
    /// <remarks>
    /// 🔑 <c>Customers</c> is filled by <c>LoadAsync</c>, and the POST handlers that send these mails
    /// do not all call it — so relying on the list alone would put the NAME in the mail only when he
    /// happened to arrive by a path that had loaded it. The same asymmetry that nearly shipped in
    /// §1093b: a value read from request-scoped state is only as good as the handler that filled it.
    /// <para>⚠️ Fail-soft: a name is a nicety, and the mail is still useful with the number alone.</para>
    /// </remarks>
    private async Task<string?> CustomerNameForMailAsync(int? customerNumber, CancellationToken ct)
    {
        if (customerNumber is not > 0) return null;

        var known = CustomerNameOrNull(customerNumber.Value);
        if (!string.IsNullOrWhiteSpace(known)) return known;

        if (_invoices is null) return null;
        try { return (await _invoices.GetCustomerAsync(customerNumber.Value, ct))?.Name; }
        catch { return null; }
    }

    /// <summary>
    /// §1093 — the live monitor URL for a billing customer, or null when there is none.
    /// </summary>
    /// <remarks>
    /// 🔒 Queries the database rather than reading <see cref="MonitorLinkByCustomer"/>, so it gives
    /// the same answer in EVERY handler. The dictionary is only populated by <c>LoadAsync</c>, and
    /// the send handler does not call it — a preview showing a link that the sent mail omitted would
    /// mean approving a body that is not what went out.
    /// <para>⚠️ Revoked monitors excluded: a withdrawn link must not be mailed to a partner.</para>
    /// </remarks>
    private async Task<string?> MonitorUrlForAsync(
        int eventId, int? customerNumber, CancellationToken ct)
    {
        if (customerNumber is not > 0) return null;

        var value = customerNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var token = await _db.AttendeeMonitors
            .AsNoTracking()
            .Where(m => m.EventId == eventId
                        && m.Kind == AttendeeMonitorKind.ErpCustomer
                        && m.Value == value
                        && m.RevokedAt == null)
            .Select(m => m.Token)
            .FirstOrDefaultAsync(ct);

        return string.IsNullOrEmpty(token)
            ? null
            : $"{Request.Scheme}://{Request.Host}/monitor/{token}";
    }

    public CouponInvoicingModel(
        ICurrentParticipantAccessor participant, CommunityHubDbContext db, TimeProvider clock,
        IEconomicContactAdminClient? economic = null,
        IEconomicInvoiceClient? invoices = null,
        // §990 — the prepaid "Create Invoice" path. Optional so the page still constructs (and every
        // existing test still builds it) when e-conomic is not wired up at all.
        CouponPrepaidInvoiceService? prepaidInvoices = null,
        CouponPoolZohoActionNotifier? zohoAction = null,
        ILogger<CouponInvoicingModel>? log = null,
        // §1013a — keeps a stored draft number current once he books it. Optional for the same
        // reason as the rest: the page must still construct with no e-conomic wiring at all.
        CouponPrepaidInvoiceNumberRefresher? invoiceNumbers = null,
        // §1013c — the standard prepaid unit price, so he stops retyping it. Optional: the page
        // must still construct in the tests that build it with no invoicing wiring.
        InvoicingOptions? invoicing = null,
        // §1016a — the "a new draft invoice exists" ops mail. It was registered in this host and
        // resolved by nobody, which is exactly why the prepaid button announced nothing.
        DraftInvoiceCreatedNotifier? draftNotices = null,
        // §1016c — the partner-facing claim invite: the sender, the ambient mail context, and the
        // edition config that supplies the ticket base URL.
        CommunityHub.Core.Email.IEmailSender? emailSender = null,
        CommunityHub.Core.Email.IEmailContextAccessor? emailContext = null,
        // 🔴 §1110 — the LOADER and its path, not a bare EventEditionConfig.
        //
        // This used to take `EventEditionConfig? editionConfig = null`, a type NOTHING registers —
        // only the loader is in DI. So the optional parameter silently defaulted to null on every
        // request, `ticketBaseUrl` fell to "", and the claim invite mailed partners the bare
        // fragment `#/buyTickets?promoCode=…`, which resolves against whatever page they are on.
        CommunityHub.Core.Config.EventEditionConfigLoader? editionConfigLoader = null,
        CommunityHub.Core.Config.EventConfigOptions? eventConfigOptions = null,
        // The DB override layer, so a TicketUrl changed in /Organizer/Settings/Config reaches the
        // mail too rather than only the shipped default.
        CommunityHub.Core.Config.ConfigOverrideStore? configOverrides = null,
        // 🔴 §1019 — Backstage's own ticket-class list, so a class can be NAMED before anybody has
        // bought one. Optional: with no Zoho wiring the page falls back to the attendee/claim
        // mirrors exactly as before.
        CommunityHub.Core.Integrations.ZohoClient? zoho = null,
        // §1093 — one monitor link per billing customer. Optional like the rest, so every existing
        // test that constructs this page keeps compiling.
        CouponCustomerMonitorProvisioner? monitorProvisioner = null)
    {
        _monitorProvisioner = monitorProvisioner;
        _participant = participant;
        _db = db;
        _clock = clock;
        _economic = economic;
        _invoices = invoices;
        _prepaidInvoices = prepaidInvoices;
        _zohoAction = zohoAction;
        _log = log;
        _invoiceNumbers = invoiceNumbers;
        _invoicing = invoicing;
        _draftNotices = draftNotices;
        _emailSender = emailSender;
        _emailContext = emailContext;
        _editionConfigLoader = editionConfigLoader;
        _eventConfigOptions = eventConfigOptions;
        _configOverrides = configOverrides;
        _zoho = zoho;
    }

    private readonly CommunityHub.Core.Integrations.ZohoClient? _zoho;

    private readonly CommunityHub.Core.Email.IEmailSender? _emailSender;
    private readonly CommunityHub.Core.Email.IEmailContextAccessor? _emailContext;
    private readonly CommunityHub.Core.Config.EventEditionConfigLoader? _editionConfigLoader;
    private readonly CommunityHub.Core.Config.EventConfigOptions? _eventConfigOptions;
    private readonly CommunityHub.Core.Config.ConfigOverrideStore? _configOverrides;

    /// <summary>
    /// 🔴 §1110 — the PUBLIC ticket site the claim link is built on (operator 2026-08-21: *"url looks
    /// wrong here … correct url format -&gt; https://eldk27.expertslive.dk#/buyTickets?promoCode=…"*).
    /// </summary>
    /// <remarks>
    /// <para>🔑 <b>Resolved through the loader, per request.</b> The bare <c>EventEditionConfig</c>
    /// this page used to inject is not in DI at all, so it was always null and the base was always
    /// blank — a mail already sent to two real partners with a link that goes nowhere.</para>
    ///
    /// <para>⚠️ Fails soft to <see cref="string.Empty"/> exactly as before; the difference is that
    /// <see cref="BuildInviteAsync"/> now REFUSES to send on a blank base rather than shipping a
    /// fragment. A missing config must not 500 the coupon page.</para>
    /// </remarks>
    private async Task<string> TicketBaseUrlAsync(int eventId, CancellationToken ct)
    {
        if (_editionConfigLoader is null || _eventConfigOptions is null) return string.Empty;
        try
        {
            var overrideJson = _configOverrides is null
                ? null
                : await _configOverrides.GetOverrideJsonAsync(
                    eventId, CommunityHub.Core.Domain.ConfigSection.Event, ct);
            return _editionConfigLoader
                .Load(_eventConfigOptions.EventConfigPath, overrideJson)
                .TicketSale?.TicketUrl ?? string.Empty;
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "§1110: could not read the ticket base URL from {Path}.",
                _eventConfigOptions.EventConfigPath);
            return string.Empty;
        }
    }
    private readonly DraftInvoiceCreatedNotifier? _draftNotices;
    private readonly InvoicingOptions? _invoicing;

    /// <summary>
    /// §1013c — the unit price the pool forms PREFILL (DKK, ex VAT). Operator 2026-08-09:
    /// *"unit price is DKK 3000"*. Empty string when the prefill is switched off (0) or invoicing
    /// options are not wired, so the box simply opens blank as it did before.
    /// </summary>
    /// <remarks>
    /// 🔒 A prefill only — §992 keeps the price TYPED, because a prepaid pool exists before any
    /// claim and deriving a price from another partner's claims would bill this one at that one's
    /// negotiated rate. This removes the retyping, not the decision.
    /// </remarks>
    public string DefaultUnitPriceDkk =>
        _invoicing is { DefaultPrepaidUnitPriceDkk: > 0m } o
            ? o.DefaultPrepaidUnitPriceDkk.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty;

    private readonly CouponPrepaidInvoiceNumberRefresher? _invoiceNumbers;
    private readonly CouponPrepaidInvoiceService? _prepaidInvoices;
    private readonly CouponPoolZohoActionNotifier? _zohoAction;
    private readonly ILogger<CouponInvoicingModel>? _log;

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
    /// <summary>
    /// §1093 — the partner's monitor link, by e-conomic customer number.
    /// </summary>
    /// <remarks>
    /// 🔑 Shown on the COUPON row rather than only on /Organizer/AttendeeMonitors, because this is
    /// the page an organizer is on when a partner asks *"can I see who has used it?"*. A link that
    /// exists but lives one page away is a link nobody sends.
    /// <para>⚠️ Revoked monitors are excluded: the page must not offer a URL that will 404.</para>
    /// </remarks>
    public IReadOnlyDictionary<int, string> MonitorLinkByCustomer { get; private set; }
        = new Dictionary<int, string>();

    /// <summary>
    /// §1094b — an outstanding "I need more tickets" request from a partner, by customer number.
    /// </summary>
    /// <remarks>
    /// 🔑 Shown on the COUPON row because that is where he can act on it — the ad-hoc cap and the
    /// prepaid increase box are both right there. A request that only exists in an e-mail is a
    /// request that gets actioned once and then forgotten about.
    /// </remarks>
    public IReadOnlyDictionary<string, (int Requested, DateTimeOffset At)> CapRequestByCoupon
    { get; private set; } = new Dictionary<string, (int, DateTimeOffset)>(StringComparer.OrdinalIgnoreCase);

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

    // ===================== §1016c: tell the requester =========================
    //
    // Operator 2026-08-09: *"can you also make a button which will notify the requester, that he can
    // now use the coupon code"*. Every other coupon mail goes to info@/mok@ and tells an ORGANIZER
    // to act; nothing ever reached the person actually waiting to claim.

    [BindProperty] public int InviteSettingId { get; set; }

    /// <summary>§1016c — the composed mail, shown for approval before anything is sent.</summary>
    /// <param name="Blocker">Why it cannot be sent, or null when it can.</param>
    public sealed record InvitePreview(
        int SettingId, string CouponName, string? ToEmail, string? ToName,
        string Subject, string Html, bool IsPrepaid, string? Blocker);

    /// <summary>The preview currently on screen (null unless he just asked for one).</summary>
    public InvitePreview? Invite { get; private set; }

    /// <summary>
    /// §1016c — compose the claim invite and show it. Sends NOTHING.
    /// </summary>
    /// <remarks>
    /// 🔒 <b>Preview-then-confirm IS the safety on this mail</b> (operator 2026-08-09: *"preview is
    /// fine and then a send mail. then it is safe"*), and it has to be, because the ring gate
    /// cannot help here — see <see cref="OnPostSendInviteAsync"/>.
    /// </remarks>
    public async Task<IActionResult> OnPostPreviewInviteAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        await LoadAsync(me.EventId, ct);
        Invite = await BuildInviteAsync(me.EventId, InviteSettingId, ct);
        // 🔴 §1026 — do NOT also raise the page-level Error. The blocker is rendered inside the
        // preview card, beside the button he pressed; setting Error printed the identical sentence
        // a second time at the top of the page (operator 2026-08-10: *"mentioned 2 times"*).
        return Page();
    }

    /// <summary>
    /// §1016c — SEND the previewed claim invite to the partner.
    /// </summary>
    /// <remarks>
    /// <para>🔴 <b>RING-EXEMPT, and that is a decision, not an oversight.</b> Operator 2026-08-09:
    /// *"i accept that we turn off the ring gate for this"* · *"no ring-gate"*.</para>
    ///
    /// <para>🔑 <b>The ring gate could not have gated this mail — only killed it.</b> Rings resolve
    /// an address to a PARTICIPANT of the edition; this recipient is an e-conomic CONTACT at a
    /// partner company and is not a participant, so <c>BrevoEmailSender</c>'s unknown-recipient rule
    /// FAILS CLOSED and would have dropped every send silently. "Ring-gated" would have meant "a
    /// button that never works".</para>
    ///
    /// <para>🔒 <b>What protects it instead is stricter than a ring, not weaker.</b> A ring is a
    /// rollout control for BULK, AUTOMATED sends. This is organizer-only, one coupon at a time,
    /// composed and shown in full — recipient, subject and body — and sent only by a second,
    /// deliberate click on that exact text. Nothing here can fire on a timer.</para>
    /// </remarks>
    public async Task<IActionResult> OnPostSendInviteAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var preview = await BuildInviteAsync(me.EventId, InviteSettingId, ct);
        if (preview is null) return RedirectWithFlash(null, "That coupon no longer exists.");
        if (preview.Blocker is { } why) return RedirectWithFlash(null, why);
        if (_emailSender is null)
            return RedirectWithFlash(null, "No email sender is configured on this host.");

        try
        {
            // §707.2 — the mail carries its own identity. RingExempt per the operator's decision
            // above; the category keeps it in the ledger like every other send.
            using (_emailContext?.Set(new CommunityHub.Core.Email.EmailContext(
                       "coupon-claim-invite", me.EventId, null, preview.ToName ?? preview.ToEmail,
                       RingExempt: true)))
            {
                // §1118 — the organizer team is copied on everything a paying customer receives.
                await _emailSender.SendAsync(
                    preview.ToEmail!, preview.Subject, preview.Html,
                    CommunityHub.Core.Integrations.Erp.CouponMailAudience.CcFor(preview.ToEmail), ct);
            }

            var rule = await _db.CouponInvoicingSettings
                .FirstOrDefaultAsync(c => c.Id == InviteSettingId && c.EventId == me.EventId, ct);
            if (rule is not null)
            {
                rule.ClaimInviteSentAt = _clock.GetUtcNow();
                rule.ClaimInviteSentToEmail = preview.ToEmail;
                await _db.SaveChangesAsync(ct);
            }

            return RedirectWithFlash(
                $"Claim invite sent to {preview.ToEmail}.", null);
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "§1016c: could not send the claim invite for setting {Id}.", InviteSettingId);
            return RedirectWithFlash(null, $"The mail could not be sent: {ex.Message}");
        }
    }

    /// <summary>
    /// §1016c — compose the invite for one coupon, or explain why it cannot be sent. Shared by the
    /// preview and the send so the mail he approves is byte-for-byte the mail that goes.
    /// </summary>
    private async Task<InvitePreview?> BuildInviteAsync(int eventId, int settingId, CancellationToken ct)
    {
        var rule = await _db.CouponInvoicingSettings
            .FirstOrDefaultAsync(c => c.Id == settingId && c.EventId == eventId, ct);
        if (rule is null) return null;

        var ev = await _db.Events.AsNoTracking()
            .Where(e => e.Id == eventId)
            .Select(e => new { e.DisplayName, e.Code })
            .FirstOrDefaultAsync(ct);

        // The prepaid pool (if any) supplies the quantity and the invoice number this mail quotes.
        var pool = await _db.CouponPrepaidAllocations.AsNoTracking()
            .Where(a => a.EventId == eventId && a.CouponInvoicingSettingId == rule.Id)
            .Select(a => new
            {
                a.TicketClassId,
                a.TicketClassLabel,
                Quantity = a.Purchases.Sum(p => (int?)p.Quantity) ?? 0,
                // 🔒 §1013a — the number is refreshed on page load, so by here it is the BOOKED
                // number when one exists rather than the dead draft number.
                Invoice = a.Purchases
                    .Where(p => p.ErpInvoiceNumber != null)
                    .OrderByDescending(p => p.Id)
                    .Select(p => new { p.ErpInvoiceNumber, p.ErpInvoiceIsBooked })
                    .FirstOrDefault(),
            })
            .FirstOrDefaultAsync(ct);

        var classLabel = pool is null
            ? "tickets"
            : TicketClassDisplay(pool.TicketClassId, pool.TicketClassLabel);

        var ticketBaseUrl = await TicketBaseUrlAsync(eventId, ct);

        var invite = CouponClaimInviteComposer.From(
            rule,
            eventDisplayName: ev?.DisplayName ?? "the event",
            ticketClassLabel: classLabel,
            ticketBaseUrl: ticketBaseUrl,
            defaultIntervalDays: DefaultInvoiceIntervalDays,
            extendDkk: _invoicing?.ExtendTermsPriceDkk ?? 3000m,
            extendEur: _invoicing?.ExtendTermsPriceEur ?? 390m,
            prepaidQuantity: rule.IsPrepaid && pool is { Quantity: > 0 } ? pool.Quantity : null,
            invoiceNumber: rule.IsPrepaid ? pool?.Invoice?.ErpInvoiceNumber : null,
            // 🔴 §1093 — the partner's own usage page, resolved HERE rather than read from
            // MonitorLinkByCustomer.
            //
            // That dictionary is filled by LoadAsync, and OnPostSendInvite does NOT call LoadAsync —
            // only the preview handler does. Reading it here would have put the link in the PREVIEW
            // and left it out of the mail actually sent, which is the worst of both: he approves a
            // body that is not what goes out. Querying directly makes this independent of which
            // handler is running.
            monitorUrl: await MonitorUrlForAsync(eventId, rule.ErpCustomerNumber, ct));

        var (subject, html) = CouponClaimInviteComposer.Build(invite);

        // --- who it goes to, and every reason it cannot ----------------------------------
        string? email = null, name = rule.RequesterName;
        string? blocker = null;
        if (rule.ErpCustomerNumber is { } cust)
        {
            // 🔴 §1026 — "— customer default —" IS A REQUESTER. Operator 2026-08-10: *"it has a
            // contact - the default one"*.
            //
            // 🔑 A null `RequesterContactNumber` does NOT mean "nobody". It means "use whoever
            // e-conomic has as this customer's attention contact" — which is exactly what the
            // INVOICE does (`CouponPrepaidInvoiceService` passes the same null and e-conomic fills
            // it in). Refusing to mail in that case told him he had picked nothing when he had
            // picked the default, and it is the commonest setting on the page.
            var contact = rule.RequesterContactNumber;
            if (contact is null)
            {
                var detail = _invoices is null ? null : await _invoices.GetCustomerAsync(cust, ct);
                contact = detail?.AttentionContactNumber;
            }

            if (contact is { } contactNo)
            {
                await LoadContactsAsync(new[] { cust }, ct);
                ContactsByCustomer.TryGetValue(cust, out var contacts);
                var match = contacts?.FirstOrDefault(c => c.ContactNumber == contactNo);
                email = match?.Email;
                name = match?.Name ?? name;
                if (string.IsNullOrWhiteSpace(email))
                {
                    blocker = contacts is null || contacts.Count == 0
                        ? "e-conomic could not be reached, so the requester's email address is unknown."
                        : $"The requester ({name ?? "contact " + contactNo}) has no email address in e-conomic.";
                }
            }
            else
            {
                blocker = "This customer has no default contact in e-conomic, so there is nobody to "
                        + "notify. Pick a specific requester above, or set a contact on the customer "
                        + "in e-conomic.";
            }
        }
        else
        {
            blocker = "This coupon has no e-conomic customer, so there is nobody to notify. "
                    + "Pick the customer above first.";
        }

        // 🔴 §1110 — REFUSE rather than mail a link that goes nowhere.
        //
        // The claim URL is the entire point of this mail; with no ticket base it degrades to the bare
        // fragment "#/buyTickets?promoCode=…", which resolves against whatever page the reader happens
        // to be on. That shipped, and two real partners got it. A blocker is the same treatment the
        // draft-invoice number gets below, for the same reason: a wrong value sent to a paying
        // customer costs more than a send he has to retry.
        if (blocker is null && string.IsNullOrWhiteSpace(ticketBaseUrl))
        {
            blocker = "The edition has no public ticket URL configured (ticketSale.ticketUrl), so the "
                    + "claim link would have no site to point at. Set it in Settings → Config first.";
        }

        // 🔒 A provisional DRAFT number must never be quoted to a partner — they would look for an
        // invoice that does not exist under it (§1013a: the draft was 182, the real one 170).
        if (blocker is null && rule.IsPrepaid && pool?.Invoice is { ErpInvoiceIsBooked: false, ErpInvoiceNumber: not null })
        {
            blocker = $"Invoice {pool.Invoice.ErpInvoiceNumber} is still a DRAFT in e-conomic. "
                    + "Book it first — a draft is renumbered when booked, so the partner would be "
                    + "given a number that will not exist.";
        }

        return new InvitePreview(
            rule.Id, rule.CouponName, email, name, subject, html, rule.IsPrepaid, blocker);
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
    /// <summary>
    /// 🔴 §1013b — the ticket class's DISPLAY NAME ("2-day ticket"), never its 17-digit Backstage
    /// id. Live label first, then whatever the pool stored, then — only if nothing knows it — the
    /// id itself.
    /// </summary>
    /// <remarks>
    /// 🔒 The <paramref name="stored"/> value is IGNORED when it is just the id. Pools created
    /// before their class had ever been claimed stored the id AS the label, so accepting it would
    /// keep printing that id on the invoice and on the page for ever — which is the complaint.
    /// </remarks>
    public string TicketClassDisplay(string classId, string? stored = null)
    {
        if (TicketClassLabels.TryGetValue(classId, out var live)
            && !string.IsNullOrWhiteSpace(live)
            && !string.Equals(live, classId, StringComparison.Ordinal))
        {
            return live;
        }
        return !string.IsNullOrWhiteSpace(stored)
               && !string.Equals(stored, classId, StringComparison.Ordinal)
            ? stored!
            : classId;
    }

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

    /// <summary>
    /// §991 — the customers whose contact fetch FAILED, as opposed to returning nothing.
    /// </summary>
    /// <remarks>
    /// 🔴 <b>The page used to assert "no contacts on this customer" for both.</b> The per-customer
    /// catch in <see cref="LoadContactsAsync"/> turns a failure into an empty list, and the message
    /// then decided which of the two it was from <see cref="EconomicReachable"/> — a PAGE-WIDE flag
    /// set by the CUSTOMER-LIST call. So whenever the customer list loaded but one customer's
    /// contacts did not, the page stated as fact that a customer has no contacts. Operator
    /// 2026-08-09 reported exactly that line on a customer.
    ///
    /// <para>⚠️ It is the §21.5 rule the invoice-number lookup already follows — *"distinguishes
    /// 'no invoice carries this reference' from 'the finance system could not be read'"* — and it
    /// matters more here, because "no contacts" reads as a data-entry job in e-conomic and sends him
    /// off to fix something that may not be broken.</para>
    /// </remarks>
    public IReadOnlySet<int> ContactLoadFailedFor { get; private set; } = new HashSet<int>();

    [BindProperty] public int SettingId { get; set; }
    [BindProperty] public string? CouponName { get; set; }
    [BindProperty] public CouponBillingType BillingType { get; set; }
    [BindProperty] public int? ErpCustomerNumber { get; set; }
    [BindProperty] public int? RequesterContactNumber { get; set; }
    [BindProperty] public string? Notes { get; set; }

    /// <summary>
    /// §1016d — THIS coupon's billing period in days, overriding the edition default (14).
    /// Blank ⇒ use the default; <c>0</c> ⇒ invoice every pass (no batching).
    /// </summary>
    /// <remarks>
    /// Operator 2026-08-09: *"maybe the internal days could be a field that could be adjusted pr
    /// coupon"*. A billing period is negotiated with a partner, so one global number would force
    /// the strictest partner's terms onto everybody.
    /// </remarks>
    [BindProperty] public int? InvoiceIntervalDays { get; set; }

    /// <summary>
    /// §1091 — the agreed price per ticket for this coupon, in DKK. Blank ⇒ the price the ticket
    /// actually sold for in Zoho.
    /// </summary>
    /// <remarks>
    /// Operator 2026-08-19: *"prio 1: custom (agreed) price and prio 2 if prio 1 is not filled out:
    /// use zoho price"*.
    /// </remarks>
    [BindProperty] public decimal? AgreedUnitPriceDkk { get; set; }

    /// <summary>
    /// §1091 — the percentage of that price the <b>LINKED COMPANY</b> is invoiced. Blank ⇒ 100.
    /// </summary>
    /// <remarks>
    /// 🔑 The company's share, NOT the attendee's discount — the operator asked for that to be said
    /// plainly, because at 50/50 the two readings produce the same number and only diverge once
    /// somebody types 30.
    /// </remarks>
    [BindProperty] public int? InvoicedSharePercent { get; set; }

    /// <summary>
    /// §1094 — the agreed ticket ceiling for a capped ad-hoc coupon. Blank ⇒ uncapped.
    /// </summary>
    /// <remarks>
    /// 🔴 Reported and invoiced against, never enforced — the limit that stops a claim is Zoho's
    /// (§787.14). Raising it re-sends the Backstage instruction so the two numbers stay in step.
    /// </remarks>
    [BindProperty] public int? ClaimCapTickets { get; set; }

    /// <summary>
    /// §1098 — the partner-contact switches, OFF by default and set by hand.
    /// </summary>
    /// <remarks>
    /// <para>🔒 Off is the safe direction: the failure mode of ON is "a customer was contacted who
    /// should not have been", which cannot be taken back. ⚠️ A checkbox posts nothing when unticked,
    /// so an absent value binds as <c>false</c> — which is what makes unticking work at all.</para>
    ///
    /// <para>⚰️ §1111 — there were THREE; the claim-invite one is gone. These two gate things that
    /// happen without him (a link a provisioner issues, a mail on a timer), so the tick is the only
    /// consent that exists. The claim invite has a button, and that button is the consent.</para>
    /// </remarks>
    [BindProperty] public bool IssueUsageLink { get; set; }

    /// <inheritdoc cref="SendClaimInviteMail"/>
    [BindProperty] public bool SendUsageStatusMail { get; set; }

    /// <summary>§1016d — the edition default, shown as the placeholder so a blank box is not blank
    /// in meaning: it says which number is in force when nothing is typed.</summary>
    public int DefaultInvoiceIntervalDays => _invoicing?.CouponInvoiceIntervalDays ?? 14;

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
        // §1091 — remember the split BEFORE it is overwritten, so the Backstage instruction mail can
        // fire on a real change only. Read here rather than after the assignments below, where
        // `existing` already holds the new values and every comparison would say "unchanged".
        var isNewRule = existing is null;
        var priorAgreedPrice = existing?.AgreedUnitPriceDkk;
        var priorSharePercent = existing?.InvoicedSharePercent;
        // §1094 — a cap change is a Backstage change too: the promo code's own limit has to move
        // with it, or the hub reports a ceiling Zoho will not honour.
        var priorCap = existing?.ClaimCapTickets;

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
                // §1016d — a NEGATIVE typed value is treated as unset, not as an error: this is a
                // number in a form, and a typo must fall back to the edition default rather than
                // silently change a partner's billing terms.
                InvoiceIntervalDays = InvoiceIntervalDays is >= 0 ? InvoiceIntervalDays : null,
                // §1091 — same defensive read as the interval above. A price of 0 or less and a
                // percentage outside 1–100 are typos, and both are stored as NULL rather than
                // honoured: null means "bill the whole ticket at what it cost", which is the old
                // behaviour and the only wrong answer nobody loses money on.
                AgreedUnitPriceDkk = AgreedUnitPriceDkk is > 0m ? AgreedUnitPriceDkk : null,
                InvoicedSharePercent = InvoicedSharePercent is > 0 and <= 100 ? InvoicedSharePercent : null,
                // §1094 — same defensive read: 0 or negative is a typo, and null means "uncapped"
                // rather than "a cap of nothing", which would report every claim as over the limit.
                ClaimCapTickets = ClaimCapTickets is > 0 ? ClaimCapTickets : null,
                // §1098 — the contact switches, exactly as ticked (§1111: the claim-invite one is
                // retired — the "Send the claim link now" button is that decision).
                IssueUsageLink = IssueUsageLink,
                SendUsageStatusMail = SendUsageStatusMail,
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
            existing.InvoiceIntervalDays = InvoiceIntervalDays is >= 0 ? InvoiceIntervalDays : null;
            // §1091 — see the create branch: a bad price or percentage is stored as null, which
            // means "the whole ticket at what it cost", never 0.
            existing.AgreedUnitPriceDkk = AgreedUnitPriceDkk is > 0m ? AgreedUnitPriceDkk : null;
            existing.InvoicedSharePercent = InvoicedSharePercent is > 0 and <= 100 ? InvoicedSharePercent : null;
            existing.ClaimCapTickets = ClaimCapTickets is > 0 ? ClaimCapTickets : null;
            existing.IssueUsageLink = IssueUsageLink;
            existing.SendUsageStatusMail = SendUsageStatusMail;
            existing.UpdatedAt = now;
            existing.LastUpdatedByEmail = me.Email;
            Message = $"'{name}' updated.";
        }

        try
        {
            await _db.SaveChangesAsync(ct);

            // 🔴 §1091 (operator 2026-08-19: *"rgr 2, create that"*) — TELL HIM TO SET THE PROMO
            // CODE UP. Only the prepaid path did this before, so an ad-hoc coupon could carry a
            // 50/50 in CEH while Backstage still charged the attendee full price: the hub would
            // invoice the company its half and nobody would be told the other half never happened.
            //
            // 🔒 On CREATE, or when the split actually CHANGED — never on every Save. Mailing him
            // because somebody fixed a typo in the notes field is the §302 "70-mail night" rebuilt,
            // and an instruction mail that arrives when there is nothing to do gets filtered.
            //
            // ⚠️ AFTER the save, and inside the try: a mail telling him to configure a split that
            // failed to persist is worse than no mail. The notifier never throws.
            // §1093 — give this customer its monitor link NOW rather than at the next 5-minute
            // tick. The job is still the authority (it catches coupons mapped by any other route);
            // this only removes the window where a freshly-mapped customer shows no link and the
            // page looks broken. Idempotent, so the two callers cannot double up.
            if (_monitorProvisioner is not null && customer is > 0)
            {
                try
                {
                    await _monitorProvisioner.EnsureAsync(
                        me.EventId,
                        // The page HAS the customer names loaded already, so unlike the job it can
                        // afford a real name — "Coupon usage — Arrow ECS" rather than "customer 1234"
                        // on a page a partner opens.
                        nameFor: (n, _) => Task.FromResult(CustomerNameOrNull(n)),
                        ct);
                }
                catch (Exception ex)
                {
                    _log?.LogWarning(ex, "§1093: monitor provisioning after coupon save threw.");
                }
            }

            if (_zohoAction is not null
                && BillingType == CouponBillingType.ClaimableAdHocPaymentByCustomer)
            {
                var newAgreedPrice = AgreedUnitPriceDkk is > 0m ? AgreedUnitPriceDkk : null;
                var newSharePercent = InvoicedSharePercent is > 0 and <= 100 ? InvoicedSharePercent : null;
                var newCap = ClaimCapTickets is > 0 ? ClaimCapTickets : null;
                var splitChanged = newAgreedPrice != priorAgreedPrice
                                   || newSharePercent != priorSharePercent
                                   // §1094 — the cap is the one field on this row that Backstage
                                   // ALSO holds, so a change here is the strongest reason of all to
                                   // send the instruction: leave it and the partner is stopped at
                                   // the old number while the hub reports the new one.
                                   || newCap != priorCap;

                if (isNewRule || splitChanged)
                {
                    await _zohoAction.NotifyAdHocAsync(
                        name, newAgreedPrice, newSharePercent, customer, isNewRule, ct, newCap,
                        // §1109 — the NAME as well as the id; the page already has the customer list.
                        customerName: await CustomerNameForMailAsync(customer, ct));
                }
            }
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
    /// §990 — the agreed unit price, in DKK, for the tickets being bought.
    /// </summary>
    /// <remarks>
    /// 🔑 <b>Typed, because the hub genuinely cannot know it.</b> A claim invoice prices each ticket
    /// from Zoho's <c>base_price</c> on the claim; a prepaid pool exists BEFORE anybody claims, so
    /// there is no claim to read. Deriving it from other claims of the class would bill one partner
    /// at another's negotiated rate. Operator chose this (2026-08-09).
    /// </remarks>
    [BindProperty] public decimal? PoolUnitPriceDkk { get; set; }

    /// <summary>
    /// §992 — the NEW TOTAL the coupon should allow, for the "Increase to" form. When set, the
    /// quantity bought is derived as <c>target − already purchased</c>.
    /// </summary>
    /// <remarks>
    /// 🔑 He types the same number he then sets as the code's max in Backstage, so the two systems
    /// cannot disagree through arithmetic done twice. Null on the "new pool" form, where the quantity
    /// IS the total.
    /// </remarks>
    [BindProperty] public int? PoolTargetTotal { get; set; }

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
    /// <summary>
    /// §1094b — apply a partner's requested cap to an AD-HOC coupon, in one click.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-19: *"the extend should trigger email to me, so i will manually extend
    /// zoho cap. and secodnly the cap increase will be extended inside ceh. if it is a prepaid, it
    /// would trigger a new invoice of the difference (extend amount). if it is an ad-hoc billing,
    /// invoices will come automatically with the cadence"*.</para>
    ///
    /// <para>🔴 <b>AD-HOC ONLY, and it raises no money.</b> That is his rule, not a shortcut: an
    /// ad-hoc cap is a ceiling, and the tickets under it are already invoiced as they are claimed on
    /// the coupon's own cadence. Raising the ceiling bills nothing today. **A PREPAID extension is a
    /// different act** — it is a purchase, and it goes through the §992 "increase to a new total"
    /// box, which derives the delta and invoices exactly that. Routing prepaid through here would
    /// raise a limit and never bill for it.</para>
    ///
    /// <para>🔒 <b>He presses this AFTER raising the code's limit in Backstage</b>, which is why it
    /// is a click and not automatic. CEH's number must never promise a ceiling Zoho will refuse —
    /// the same invariant the partner-facing request was built around.</para>
    ///
    /// <para>⚠️ Clears the PENDING ask (<c>LastCapRequestedTickets</c>) but keeps
    /// <c>LastCapRequestAt</c>: the throttle and the audit of when they asked both survive.</para>
    /// </remarks>
    /// <summary>
    /// §1095 — "the code is live in Backstage": confirm it, and the partner is notified at once.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-08-19: *"customer will get email with secure link to monitor usage (i
    /// need to click send mail). actually i prefer to have you send this automatically"*.</para>
    ///
    /// <para>🔴 <b>Automatic, but NOT unattended — and the ordering is why.</b> The invite says
    /// *"you can now use the below registration URL to start claiming"*. Backstage has no coupon API
    /// (§787.14), so CEH cannot know the code exists; a send triggered by the coupon row appearing
    /// would usually beat him to creating it, and the partner would click a dead code in a mail from
    /// us. This button records the one fact only he has, and sending is then immediate.</para>
    ///
    /// <para>🔑 <b>It replaces the ritual, not the judgement.</b> He no longer composes, previews and
    /// sends; he states that the code is live and the mail goes with the monitor link in it. The
    /// preview button stays for the times he wants to read it first.</para>
    ///
    /// <para>🔒 <b>Every §1016c blocker still applies</b>, because this reuses
    /// <c>BuildInviteAsync</c> verbatim: no contact e-mail, no customer, and above all an UNBOOKED
    /// draft invoice (§1013a — quoting a draft number sends the partner looking for an invoice that
    /// will be renumbered). A blocker refuses the send and says why; the confirmation still stands,
    /// so fixing the blocker and pressing again is all that is needed.</para>
    /// </remarks>
    public async Task<IActionResult> OnPostConfirmCodeLiveAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var rule = await _db.CouponInvoicingSettings
            .FirstOrDefaultAsync(c => c.Id == InviteSettingId && c.EventId == me.EventId, ct);
        if (rule is null) return RedirectWithFlash(null, "That coupon no longer exists.");

        var now = _clock.GetUtcNow();
        if (rule.BackstageCodeConfirmedAt is null)
        {
            rule.BackstageCodeConfirmedAt = now;
            rule.BackstageCodeConfirmedByEmail = me.Email;
            await _db.SaveChangesAsync(ct);
        }

        // ⚰️ §1111 — the `SendClaimInviteMail` tick used to veto this handler, so pressing a button
        // labelled "Send the claim link now" could answer "no mail was sent". Operator 2026-08-21:
        // *"i dont get the point of having this tick … if there is also a button"*.
        //
        // 🔑 The two switches that remain (IssueUsageLink, SendUsageStatusMail) gate things that
        // happen WITHOUT him — a link issued by a provisioner, a mail on a timer — so an explicit
        // opt-in is the only consent there is. This one has a button, and pressing the button IS the
        // consent. A second, invisible one only made the visible one lie.
        //
        // The one-off-sale case (§1098: set up, invoiced, never written to) is still served exactly
        // as well: don't press the button.

        // Already told them? Confirming again must not re-mail a partner.
        if (rule.ClaimInviteSentAt is not null)
        {
            return RedirectWithFlash(
                $"'{rule.CouponName}' is marked live. The partner was already notified on "
                + $"{rule.ClaimInviteSentAt:d MMM yyyy} — use \"Notify again…\" to re-send.", null);
        }

        await LoadAsync(me.EventId, ct);
        var preview = await BuildInviteAsync(me.EventId, rule.Id, ct);

        if (preview is null) return RedirectWithFlash(null, "That coupon no longer exists.");
        if (preview.Blocker is { } why)
        {
            return RedirectWithFlash(
                $"'{rule.CouponName}' is marked live, but the partner was NOT notified: {why}",
                null);
        }
        if (_emailSender is null)
            return RedirectWithFlash(null, "No email sender is configured on this host.");

        try
        {
            // §707.2 / §1016c — same identity and the same ring exemption as the manual send: the
            // recipient is an e-conomic contact, not a participant, so the ring gate cannot resolve
            // them and would fail closed.
            using (_emailContext?.Set(new CommunityHub.Core.Email.EmailContext(
                       "coupon-claim-invite", me.EventId, null, preview.ToName ?? preview.ToEmail,
                       RingExempt: true)))
            {
                // §1118 — the organizer team is copied on everything a paying customer receives.
                await _emailSender.SendAsync(
                    preview.ToEmail!, preview.Subject, preview.Html,
                    CommunityHub.Core.Integrations.Erp.CouponMailAudience.CcFor(preview.ToEmail), ct);
            }

            rule.ClaimInviteSentAt = _clock.GetUtcNow();
            rule.ClaimInviteSentToEmail = preview.ToEmail;
            await _db.SaveChangesAsync(ct);

            return RedirectWithFlash(
                $"'{rule.CouponName}' marked live and the partner notified at {preview.ToEmail}"
                + " — the mail includes their usage link.", null);
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "§1095: auto-notify after confirming {Coupon} failed.", rule.CouponName);
            return RedirectWithFlash(
                null,
                $"'{rule.CouponName}' is marked live, but the notification could not be sent. "
                + "Use \"Notify requester…\" to try again.");
        }
    }

    public async Task<IActionResult> OnPostApplyCapRequestAsync(CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) { AccessDenied = true; return Page(); }

        var rule = await _db.CouponInvoicingSettings
            .FirstOrDefaultAsync(c => c.Id == SettingId && c.EventId == me.EventId, ct);
        if (rule is null) return RedirectWithFlash(null, "That coupon no longer exists.");

        if (rule.BillingType != CouponBillingType.ClaimableAdHocPaymentByCustomer)
        {
            return RedirectWithFlash(
                null,
                "This is a prepaid coupon — use \"Increase to new total\" on its pool instead, so "
                + "the extra tickets are invoiced.");
        }

        if (rule.ErpCustomerNumber is not > 0)
            return RedirectWithFlash(null, "That coupon has no billing customer.");

        // §1096 — the ask lives on the COUPON, so applying one leaves any other coupon's request
        // untouched. Under the old per-customer field, applying Arrow's ad-hoc cap would have
        // silently cleared their prepaid request too.
        if (rule.RequestedCapTickets is not { } requested || requested <= 0)
            return RedirectWithFlash(null, "There is no outstanding request for that coupon.");

        var priorCap = rule.ClaimCapTickets;
        rule.ClaimCapTickets = requested;
        rule.RequestedCapTickets = null;   // applied — stop showing it as outstanding
        rule.UpdatedAt = _clock.GetUtcNow();
        rule.LastUpdatedByEmail = me.Email;
        await _db.SaveChangesAsync(ct);

        // The Backstage instruction goes again, because the limit that actually stops a claim is
        // still the one on the promo code and it has to match what CEH now reports.
        if (_zohoAction is not null)
        {
            try
            {
                await _zohoAction.NotifyAdHocAsync(
                    rule.CouponName, rule.AgreedUnitPriceDkk, rule.InvoicedSharePercent,
                    rule.ErpCustomerNumber, isNew: false, ct, requested,
                    customerName: await CustomerNameForMailAsync(rule.ErpCustomerNumber, ct));
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "§1094b: Backstage instruction after a cap apply threw.");
            }
        }

        return RedirectWithFlash(
            $"'{rule.CouponName}' cap set to {requested}"
            + (priorCap is { } p ? $" (was {p})" : string.Empty)
            + ". Tickets stay billed as they are claimed — no invoice raised.",
            null);
    }

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

        // §992 — INCREASE TO A NEW TOTAL, rather than "add N". Operator 2026-08-09: *"lets say that
        // the customer comes back and says, lets extend the 20 to 30 … now we need a increase button
        // in ceh, so we can increase from 20 to 30. i need to then invoice him for 10 extra. the
        // amount must now reflect a max of 30"*.
        //
        // 🔑 The number he types is the SAME NUMBER HE SETS IN BACKSTAGE — the code's max. That is
        // the whole point: with an "add N" box he has to do the arithmetic twice and the two systems
        // disagree the moment he gets it wrong. CEH derives the delta and invoices only that.
        //
        // 🔒 It still ADDS A PURCHASE ROW; it does not edit a total. "50 in January on 20147, 25 more
        // in March on 20233" stays answerable, and the balance keeps the §794.4 property of being
        // derived (purchased = SUM(purchases)) rather than accumulated.
        var alreadyBought = pool?.Purchases.Sum(p => p.Quantity) ?? 0;
        if (PoolTargetTotal is { } target)
        {
            if (target <= alreadyBought)
            {
                // ⚠️ Refused, never silently reduced. Purchases are a money record; "decreasing" one
                // would mean deleting an agreement that was invoiced. Removing is its own button.
                Error = alreadyBought == 0
                    ? "Enter the new total number of tickets the code should allow."
                    : $"This pool already covers {alreadyBought} ticket(s). Enter a HIGHER new total "
                      + $"to increase it — a pool cannot be reduced, because each purchase is an "
                      + $"invoiced agreement. Use 'Remove allocation' to delete the pool entirely.";
                return RedirectWithFlash(Message, Error);
            }

            PoolQuantity = target - alreadyBought;
        }

        // ⚠️ A purchase of 0 or fewer is not an agreement. Removing a pool is its own button, so
        // "quantity 0" no longer has to double as delete — which it did before §798.4 and which
        // would now be ambiguous with "add nothing".
        if (PoolQuantity <= 0)
        {
            Error = "How many tickets did the partner buy? Use 'Remove allocation' to delete a pool.";
            return RedirectWithFlash(Message, Error);
        }

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

        var purchase = new CouponPrepaidPurchase
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
            // §992 — kept so the page can total what the pool is WORTH, not just how many tickets it
            // holds. Nothing else records it: the price is agreed before any claim exists.
            UnitPriceDkk = PoolUnitPriceDkk is > 0m ? PoolUnitPriceDkk : null,
            CreatedAt = now,
            CreatedByEmail = me.Email,
        };
        pool.Purchases.Add(purchase);

        // 🔒 SAVED FIRST, AND NEVER ROLLED BACK BY WHAT FOLLOWS. The partner agreed to buy the
        // tickets; whether e-conomic answered a second later is a different fact. The purchase id
        // is also the invoice's reference (§990), so it has to exist before the invoice does.
        await _db.SaveChangesAsync(ct);

        // 🔴 §1013b — the LIVE label wins over the stored one, and the raw id is the last resort.
        // A pool created before the class had ever been claimed stored the 17-digit id AS its
        // label, so trusting the stored value would keep printing that id on the invoice for ever.
        // Storing the improvement back means the pool heals itself the first time it is touched.
        var classLabel = TicketClassDisplay(classId, pool.TicketClassLabel);
        if (!string.Equals(pool.TicketClassLabel, classLabel, StringComparison.Ordinal))
            pool.TicketClassLabel = classLabel;
        var bought = pool.Purchases.Sum(p => p.Quantity);

        Message = isTopUp
            ? $"'{rule.CouponName}': {PoolQuantity} more ticket(s) added to the pool."
            : $"'{rule.CouponName}': pool created with {PoolQuantity} ticket(s).";

        // §990 — CREATE THE INVOICE. Only when the organizer did not already type a number: a
        // hand-entered number means the invoice exists in e-conomic already, and raising a second
        // one would bill the partner twice for the same tickets.
        string? invoiceNote = null;
        if (purchase.ErpInvoiceNumber is { Length: > 0 } typed)
        {
            invoiceNote = $"Invoice {typed} was entered by hand — no draft was created.";
        }
        else if (_prepaidInvoices is null)
        {
            invoiceNote = "No invoice was created (e-conomic invoicing is not wired up on this host).";
            Error = "The tickets are recorded, but no invoice could be created — e-conomic invoicing "
                  + "is not configured here. Raise it by hand and enter the number.";
        }
        else
        {
            var result = await _prepaidInvoices.CreateForPurchaseAsync(
                rule, classLabel, purchase.Id, PoolQuantity, PoolUnitPriceDkk ?? 0m, ct);

            if (result.DraftNumber is { } draft)
            {
                // 🔴 §1013a/d — the BARE number, plus a flag saying it is not booked yet.
                //
                // Operator 2026-08-09: *"dont store the DRAFT 182 - just the invoice ID"*. The word
                // was doing real work (§795.4: a draft number is provisional), so dropping it is
                // only safe because ErpInvoiceIsBooked now carries that meaning as data AND
                // CouponPrepaidInvoiceNumberRefresher replaces the number with the BOOKED one the
                // moment he books it. Without those two, "182" would sit there looking
                // authoritative long after e-conomic had renumbered it to 170.
                purchase.ErpInvoiceNumber = draft.ToString(System.Globalization.CultureInfo.InvariantCulture);
                purchase.ErpInvoiceIsBooked = false;
                purchase.ErpInvoiceConfirmedAt = now;
                purchase.ErpInvoiceConfirmedByEmail = me.Email;
                await _db.SaveChangesAsync(ct);

                invoiceNote = $"e-conomic draft {draft} was created for these tickets.";
                Message += $" e-conomic draft {draft} created.";

                // 🔴 §1016a — ANNOUNCE IT. Operator 2026-08-09: *"i did not get any emails about a
                // new invoice was created"*. DraftInvoiceCreatedNotifier existed and had exactly
                // two callers — CouponInvoiceJob and WebshopInvoiceJob — so the one invoice path a
                // HUMAN triggers, by clicking a button labelled "Create Invoice", was the only one
                // that announced nothing. It was even registered in this host's DI and resolved by
                // nobody.
                //
                // 🔑 The tell was already here: this handler composes `invoiceNote` and posts it
                // inside the NEIGHBOURING coupon mail. Somebody saw that the invoice fact needed to
                // travel and attached it to the mail next door instead of sending the one built for
                // it — so the fact arrived, in the wrong envelope, and the real mail never fired.
                if (_draftNotices is not null && result.Created_ is { } createdDraft)
                {
                    try
                    {
                        await _draftNotices.NotifyAsync(
                            "Prepaid coupon tickets", new[] { createdDraft }, ct);
                    }
                    catch (Exception ex)
                    {
                        // Same rule as the coupon mail below: a mail failure must never look like
                        // the purchase or the invoice failed. Both really happened.
                        _log?.LogError(ex,
                            "§1016a: could not mail the draft-invoice notice for purchase {Purchase}.",
                            purchase.Id);
                    }
                }
            }
            else
            {
                // 🔒 The tickets stay. The §795.2 chase keeps asking for a number, which is exactly
                // the right outcome for an invoice that still has to be raised by hand.
                invoiceNote = result.WouldCreate
                    ? "No invoice was created — the hub is in dry-run mode."
                    : "No invoice was created — raise it by hand and enter the number.";
                Error = result.Problem;
            }
        }

        // §990 — Backstage has NO coupon API (§787.14), so the promo code is a manual step. Mailed
        // whatever happened with the invoice: the partner cannot claim a ticket without the code,
        // and that is true even on a run where the invoicing failed.
        if (_zohoAction is not null)
        {
            try
            {
                await _zohoAction.NotifyAsync(
                    rule.CouponName, classLabel, PoolQuantity, bought, isTopUp, invoiceNote, ct);
            }
            catch (Exception ex)
            {
                // A mail failure must not look like the purchase failed.
                _log?.LogError(ex,
                    "§990: could not mail the Backstage promo-code action for coupon {Coupon}.",
                    rule.CouponName);
            }
        }

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
        // 🔴 §1013a — REFRESH THE STORED INVOICE NUMBERS FIRST, so the page can never show him a
        // draft number that e-conomic has already replaced (he saw 182 for an invoice booked as
        // 170). Runs here rather than on a timer because this page IS where he reads them, and a
        // number that is correct only after the next job run is a number he will quote wrongly in
        // between. Fail-soft, like every other e-conomic call on this page.
        if (_invoiceNumbers is not null)
        {
            try
            {
                var refreshed = await _invoiceNumbers.RefreshAsync(eventId, ct);
                if (refreshed.Updated > 0)
                {
                    _log?.LogInformation(
                        "§1013a: refreshed {Count} prepaid invoice number(s) from e-conomic.",
                        refreshed.Updated);
                }
            }
            catch (Exception ex)
            {
                // A stale number is a nuisance; a page that will not open is worse.
                _log?.LogWarning(ex, "§1013a: prepaid invoice-number refresh failed.");
            }
        }

        // §1093 — the monitor link per billing customer, so it sits beside the coupon it belongs to.
        // Live links only: offering a revoked one would hand the organizer a URL that 404s.
        var monitorLinks = await _db.AttendeeMonitors
            .AsNoTracking()
            .Where(m => m.EventId == eventId
                        && m.Kind == AttendeeMonitorKind.ErpCustomer
                        && m.RevokedAt == null)
            .Select(m => new { m.Value, m.Token })
            .ToListAsync(ct);

        var linkBase = $"{Request.Scheme}://{Request.Host}";
        var byCustomer = new Dictionary<int, string>();
        foreach (var m in monitorLinks)
        {
            if (int.TryParse(
                    m.Value, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var n))
            {
                byCustomer[n] = $"{linkBase}/monitor/{m.Token}";
            }
        }
        MonitorLinkByCustomer = byCustomer;

        // §1096 — outstanding partner requests, PER COUPON. `RequestedCapTickets` is the pending
        // ask; it is cleared when he applies it, so a coupon carrying one is still waiting on him.
        // ⚰️ This used to read the monitor row, which is per CUSTOMER — Arrow Denmark, holding a
        // prepaid pool AND a capped ad-hoc coupon, is exactly why that could not work: one field
        // could not say which of the two the partner meant.
        var pending = await _db.CouponInvoicingSettings
            .AsNoTracking()
            .Where(c => c.EventId == eventId
                        && c.RequestedCapTickets != null
                        && c.RequestedCapAt != null)
            .Select(c => new { c.CouponName, c.RequestedCapTickets, c.RequestedCapAt })
            .ToListAsync(ct);

        CapRequestByCoupon = pending.ToDictionary(
            p => p.CouponName,
            p => (p.RequestedCapTickets!.Value, p.RequestedCapAt!.Value),
            StringComparer.OrdinalIgnoreCase);

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
        //
        // 🔴 §1013b — THE ATTENDEE MIRROR IS THE BROADER SOURCE, AND IT GOES FIRST.
        //
        // Operator 2026-08-09: *"dont show the ticket class id, but the actual ticket class
        // displayname like 2-day ticket"* — on the page AND on the invoice.
        //
        // 🔑 The cause: this dictionary was built from CLAIMS only, and a prepaid pool exists
        // precisely BEFORE anybody claims (that is what prepaid means). So for exactly the pools
        // this page is about, the class had no label and everything downstream fell back to
        // `classId` — a 17-digit Backstage number that lands on a customer's invoice line.
        // `Attendee` carries TicketClassId + TicketClassName for every ticket ever sold, claimed or
        // not, so it answers for classes the claim list has never seen.
        var attendeeClasses = await _db.Attendees
            .Where(a => a.EventId == eventId
                        && a.TicketClassId != null && a.TicketClassName != null)
            .Select(a => new { a.TicketClassId, a.TicketClassName })
            .Distinct()
            .ToListAsync(ct);

        var labels = new Dictionary<string, string>(StringComparer.Ordinal);

        // 🔴 §1019 — BACKSTAGE'S OWN TICKET-CLASS LIST FIRST. Operator 2026-08-09, fourth time:
        // *"no-one knows a id - it must be the ticket class name"*.
        //
        // 🔑 Why the previous two fixes were not enough: claims, then claims + attendees, are both
        // "somebody must already have BOUGHT this class". A PREPAID POOL IS CREATED BEFORE ANYBODY
        // BUYS — that is what prepaid means — so in exactly the case he kept hitting there was
        // nothing to learn the name from, and the id showed through. This endpoint knows every
        // class from the moment it is defined, so it is the one source that cannot be empty when a
        // pool is created. Fail-soft: unreadable ⇒ fall through to the mirrors below.
        if (_zoho is not null)
        {
            try
            {
                var token = await _zoho.GetAccessTokenAsync(ct);
                if (!string.IsNullOrWhiteSpace(token))
                {
                    foreach (var kv in await _zoho.GetTicketClassNamesAsync(token!, ct))
                        labels[kv.Key] = kv.Value;
                }
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "§1019: could not read Backstage ticket classes.");
            }
        }

        // The attendee mirror FILLS GAPS ONLY — it must never overwrite the Backstage name above.
        // A ticket_name copied onto an attendee record is a snapshot taken at purchase; the class
        // list is the current truth, and a renamed class would otherwise keep its old name here.
        foreach (var a in attendeeClasses)
        {
            if (string.IsNullOrWhiteSpace(a.TicketClassId) || string.IsNullOrWhiteSpace(a.TicketClassName))
                continue;
            if (labels.ContainsKey(a.TicketClassId!)) continue;
            labels[a.TicketClassId!] = a.TicketClassName!.Trim();
        }
        // Claims fill any gap the attendee mirror does not cover (and never overwrite it).
        foreach (var g in claims.Where(c => c.TicketClassId.Length > 0)
                     .GroupBy(c => c.TicketClassId, StringComparer.Ordinal))
        {
            if (labels.ContainsKey(g.Key)) continue;
            var name = g.Select(x => x.TicketClassName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
            labels[g.Key] = string.IsNullOrWhiteSpace(name) ? g.Key : name!.Trim();
        }
        TicketClassLabels = labels;

        // 🔴 §1019 — HEAL THE STORED LABELS, so the id cannot reach a human anywhere.
        //
        // Operator 2026-08-09: *"so mails, erp integration, etc must use the ticket class name; not
        // the id"* — and the page alone could not deliver that. `CouponPrepaidBillingReminderService`
        // and `CouponPrepaidLowBalanceAlertService` are BACKGROUND jobs with no page context: both
        // print `TicketClassLabel ?? TicketClassId`, so they show the id whenever the stored label
        // is missing. The same stored label is what reaches the e-conomic invoice line.
        //
        // ⇒ Writing the real Backstage name onto the allocation once fixes every consumer at the
        // source, instead of teaching three services to call Zoho separately.
        var stale = await _db.CouponPrepaidAllocations
            .Where(a => a.EventId == eventId)
            .ToListAsync(ct);
        var healed = 0;
        foreach (var a in stale)
        {
            if (!labels.TryGetValue(a.TicketClassId, out var real) || string.IsNullOrWhiteSpace(real))
                continue;
            // Replace a MISSING label, and one that is merely the id wearing a label's clothes.
            if (string.Equals(a.TicketClassLabel, real, StringComparison.Ordinal)) continue;
            if (!string.IsNullOrWhiteSpace(a.TicketClassLabel)
                && !string.Equals(a.TicketClassLabel, a.TicketClassId, StringComparison.Ordinal))
            {
                continue;   // a real, different name — somebody meant it; leave it alone
            }
            a.TicketClassLabel = real;
            healed++;
        }
        if (healed > 0)
        {
            await _db.SaveChangesAsync(ct);
            _log?.LogInformation("§1019: named {Count} ticket class(es) from Backstage.", healed);
        }

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

        // §992 — every PURCHASE id per coupon, so `InvoicesFor` can also find the prepaid invoices
        // (`CouponPrepaid-{purchaseId}`). Built from ALL allocations, dormant included: a dormant
        // pool still records money that was billed, and hiding its invoice would misstate the total.
        var purchaseIdsByCoupon = settings
            .Where(s => allocations.ContainsKey(s.Id))
            .ToDictionary(
                s => s.CouponName,
                s => (IReadOnlyList<int>)allocations[s.Id]
                    .SelectMany(a => a.Purchases).Select(p => p.Id).ToList(),
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

            // The CLAIM invoices (§795.4) — one reference per claimed ticket on this coupon.
            var references = claimsByCoupon.TryGetValue(couponName, out var mine)
                ? mine.Select(c => c.Reference)
                : Enumerable.Empty<string>();

            // 🔴 §992 — and the PREPAID invoices, which were invisible here. `InvoicesFor` matched
            // only claim references, so a coupon's prepayments — the very invoices §990 now creates —
            // never appeared in "e-conomic invoices" and the operator could not reconcile what he had
            // billed against the code's max. Their reference is per PURCHASE (`CouponPrepaid-{id}`).
            if (purchaseIdsByCoupon.TryGetValue(couponName, out var purchaseIds))
            {
                references = references.Concat(
                    purchaseIds.Select(CouponPrepaidInvoiceService.ReferenceFor));
            }

            return references
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
        var failed = new HashSet<int>(ContactLoadFailedFor);
        foreach (var number in customerNumbers.Distinct().Where(n => n > 0))
        {
            if (map.ContainsKey(number)) continue;
            try
            {
                map[number] = (await _economic.ListContactsAsync(number, ct))
                    .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                failed.Remove(number);
            }
            catch (Exception ex)
            {
                // §991 — still fail-soft (one bad customer must not empty every other dropdown),
                // but REMEMBER that it failed. An empty list is no longer allowed to be read as
                // "this customer has no contacts", which is what the page said before.
                map[number] = Array.Empty<EconomicContactRow>();
                failed.Add(number);
                _log?.LogWarning(ex,
                    "§991: could not read e-conomic contacts for customer {Customer}; the picker "
                    + "will say so rather than claim the customer has none.", number);
            }
        }

        ContactsByCustomer = map;
        ContactLoadFailedFor = failed;
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
