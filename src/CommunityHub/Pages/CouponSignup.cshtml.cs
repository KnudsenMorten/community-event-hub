using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Erp;
using CommunityHub.Core.Integrations.Webshop;
using CommunityHub.Core.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages;

/// <summary>
/// §1382 — the PUBLIC coupon sign-up form: a company asks for a ticket coupon; CEH stores the request, creates the coupon,
/// finds or creates the e-conomic customer (group 2) and mails the event mailbox (<see cref="CouponSignupService"/>).
/// </summary>
/// <remarks>
/// <para>🔒 Anonymous by design (public layout) and protected like /sponsor-registration: the same captcha
/// (<see cref="ShopCaptcha"/> — Turnstile when configured, else the sealed arithmetic question with a minimum fill time),
/// a honeypot field, and a per-IP / per-e-mail hourly limit in the service. GATED by <c>coupon-signup</c>: while it is off
/// only a signed-in organizer sees the page (as a preview), so a PROD deploy cannot open it by itself.</para>
/// </remarks>
[AllowAnonymous]
public class CouponSignupModel : PageModel
{
    public const string FeatureKey = "coupon-signup";

    private readonly CommunityHubDbContext _db;
    private readonly CouponSignupService _signup;
    private readonly ShopCaptcha _captcha;
    private readonly FeatureGateService _gate;
    private readonly ICurrentParticipantAccessor _participant;

    public CouponSignupModel(
        CommunityHubDbContext db, CouponSignupService signup, ShopCaptcha captcha,
        FeatureGateService gate, ICurrentParticipantAccessor participant)
    {
        _db = db;
        _signup = signup;
        _captcha = captcha;
        _gate = gate;
        _participant = participant;
    }

    public bool Closed { get; private set; }
    public bool IsPreview { get; private set; }
    public bool Done { get; private set; }
    public string? EventName { get; private set; }
    public string? Message { get; private set; }
    public IReadOnlyList<string> Problems { get; private set; } = Array.Empty<string>();
    public ShopCaptchaChallenge? Challenge { get; private set; }
    public bool UsesTurnstile => _captcha.UsesTurnstile;
    public string TurnstileSiteKey => _captcha.TurnstileSiteKey;

    [BindProperty] public CouponSignupOrderType? OrderType { get; set; }
    [BindProperty] public CouponSignupTicketType? TicketType { get; set; }
    [BindProperty] public int? TicketCount { get; set; }
    [BindProperty] public string? VatNumber { get; set; }
    [BindProperty] public string? CompanyName { get; set; }
    [BindProperty] public string? Address { get; set; }
    [BindProperty] public string? PostalCode { get; set; }
    [BindProperty] public string? City { get; set; }
    [BindProperty] public string Country { get; set; } = "DK";
    [BindProperty] public string? RequesterName { get; set; }
    [BindProperty] public string? RequesterEmail { get; set; }
    [BindProperty] public string? RequesterMobile { get; set; }
    /// <summary>The invoice-details toggle: "email" or "ean". Only the chosen field is passed on.</summary>
    [BindProperty] public string InvoiceBy { get; set; } = "email";
    [BindProperty] public string? InvoiceEmail { get; set; }
    [BindProperty] public string? EanNumber { get; set; }
    [BindProperty] public int? SplitPercent { get; set; }
    [BindProperty] public string? PurchaseOrder { get; set; }

    [BindProperty] public string? CaptchaToken { get; set; }
    [BindProperty] public string? CaptchaAnswer { get; set; }
    /// <summary>Honeypot — hidden off-screen; any value ⇒ silently ignored (bot).</summary>
    [BindProperty] public string? Website2 { get; set; }

    public static IReadOnlyList<(string Code, string Name)> Countries => Core.Domain.Countries.All;

    public async Task OnGetAsync(CancellationToken ct)
    {
        if (await ContextAsync(ct) is null) return;
        Challenge = _captcha.NewChallenge();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var eventId = await ContextAsync(ct);
        if (eventId is null) return Page();

        // Bot: behave as if it worked, store and send nothing.
        if (!string.IsNullOrWhiteSpace(Website2)) { Done = true; return Page(); }

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var answer = UsesTurnstile ? Request.Form["cf-turnstile-response"].ToString() : CaptchaAnswer;
        if (!await _captcha.VerifyAsync(CaptchaToken, answer, ip, ct))
        {
            Problems = new[] { "The check did not pass. Please answer the question again." };
            Challenge = _captcha.NewChallenge();
            return Page();
        }

        var byEan = string.Equals(InvoiceBy, "ean", StringComparison.OrdinalIgnoreCase);
        var input = new CouponSignupInput(
            OrderType, TicketType, TicketCount, VatNumber, CompanyName, Address, PostalCode, City, Country,
            RequesterName, RequesterEmail, RequesterMobile,
            InvoiceEmail: byEan ? null : InvoiceEmail,
            EanNumber: byEan ? EanNumber : null,
            SplitPercent, PurchaseOrder);

        var outcome = await _signup.SubmitAsync(eventId.Value, input, ip, ct);
        if (!outcome.Ok)
        {
            Problems = outcome.Problems;
            Challenge = _captcha.NewChallenge();
            return Page();
        }
        Done = true;
        Message = $"Thank you — we have received your request (no. {outcome.RequestId}). We will set up your coupon and "
                + $"send the details to {RequesterEmail?.Trim()}.";
        return Page();
    }

    /// <summary>The active edition, or null (with <see cref="Closed"/> set) when the form is not open.</summary>
    private async Task<int?> ContextAsync(CancellationToken ct)
    {
        var ev = await _db.Events.AsNoTracking().Where(e => e.IsActive)
            .Select(e => new { e.Id, Name = e.DisplayName }).FirstOrDefaultAsync(ct);
        if (ev is null) { Closed = true; return null; }
        EventName = ev.Name;
        var open = await _gate.IsFeatureEnabledAsync(FeatureKey, ev.Id, ct);
        var me = _participant.Current;
        IsPreview = !open && me is not null && OrganizerAuth.IsRealOrganizer(me);
        if (!open && !IsPreview) { Closed = true; return null; }
        return ev.Id;
    }
}
