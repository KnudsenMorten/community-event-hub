using CommunityHub.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Webshop;
using CommunityHub.Core.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Pages;

/// <summary>
/// §1305 item 1 — public sponsor registration: prove the e-mail (captcha + one-time code), then the get-started
/// wizard (company → signer → event coordinator), then straight into the shop, signed in.
/// </summary>
/// <remarks>
/// <para>Replaces the public form on the old site and its registration webhooks. Operator: <i>"a secure login with
/// pin code so we have an authenticated registration, also with capcha and then a get started wizard, that asks
/// all the questions in the form."</i></para>
/// <para>🔒 Anonymous by design (§27 public page, public layout) — and GATED by <c>sponsor-webshop</c>: while the
/// shop is off only a signed-in organizer can open it, so a PROD deploy cannot expose registration.</para>
/// </remarks>
[AllowAnonymous]
public class SponsorRegistrationModel : PageModel
{
    private readonly CommunityHubDbContext _db;
    private readonly ShopRegistrationService _registration;
    private readonly ShopCaptcha _captcha;
    private readonly FeatureGateService _gate;
    private readonly ICurrentParticipantAccessor _participant;

    public SponsorRegistrationModel(
        CommunityHubDbContext db, ShopRegistrationService registration, ShopCaptcha captcha,
        FeatureGateService gate, ICurrentParticipantAccessor participant, ShopCompanyDirectoryService directory,
        CommunityHub.Sponsors.SponsorActingCompanyService acting)
    {
        _directory = directory;
        _acting = acting;
        _db = db;
        _registration = registration;
        _captcha = captcha;
        _gate = gate;
        _participant = participant;
    }

    private readonly ShopCompanyDirectoryService _directory;
    private readonly CommunityHub.Sponsors.SponsorActingCompanyService _acting;

    /// <summary>§1309 A — the verified e-mail already belongs to a sponsor contact: offer "go to the shop".</summary>
    public bool IsExistingContact { get; private set; }

    // §1309 — Join: the verified e-mail's domain matches a company CEH already knows → join it (read-only, no edits).
    // Pending: matched only by CVR → a request waits for an organizer.
    public enum Step { Email, Code, Wizard, Join, Pending }

    /// <summary>§1309 — companies whose domain matches the verified e-mail (name + city only).</summary>
    public IReadOnlyList<ShopCompanyMatch> Matches { get; private set; } = Array.Empty<ShopCompanyMatch>();
    /// <summary>§1309 — the company being joined, and its people for the dropdowns (masked e-mails).</summary>
    public ShopCompanyMatch? Joining { get; private set; }
    public IReadOnlyList<ShopPersonOption> People { get; private set; } = Array.Empty<ShopPersonOption>();
    /// <summary>§1309 — only when nobody of the company is in the hub yet may the joiner choose its signer / coordinator.</summary>
    public bool CanPickRoles { get; private set; }
    [BindProperty] public string? JoinCompanyId { get; set; }
    [BindProperty] public string? JoinFirst { get; set; }
    [BindProperty] public string? JoinLast { get; set; }
    [BindProperty] public string? JoinPhone { get; set; }
    [BindProperty] public string? SignerKey { get; set; }
    [BindProperty] public string? CoordKey { get; set; }
    /// <summary>§1312 — "I sign the contract" / "I coordinate the event" when joining a company that already has people.</summary>
    [BindProperty] public bool JoinAsSigner { get; set; }
    [BindProperty] public bool JoinAsCoordinator { get; set; }

    public Step Current { get; private set; } = Step.Email;
    public bool Closed { get; private set; }
    public bool IsPreview { get; private set; }
    public string? EventName { get; private set; }
    public string? Message { get; private set; }
    public bool MessageIsError { get; private set; }
    public IReadOnlyList<string> Problems { get; private set; } = Array.Empty<string>();
    public ShopCaptchaChallenge? Challenge { get; private set; }
    public bool UsesTurnstile => _captcha.UsesTurnstile;
    public string TurnstileSiteKey => _captcha.TurnstileSiteKey;

    [BindProperty] public string? Email { get; set; }
    [BindProperty] public string? Code { get; set; }
    [BindProperty] public string? Session { get; set; }

    /// <summary>
    /// §1312 — where the offer's button sends them after registering: "request" (the cart's Request a reservation) or
    /// "order" (the cart, to sign). Operator 2026-09-27: "in the offer can you make 2 buttons: reservation request … and
    /// sign/order". Only these two values mean anything; anything else is the normal landing.
    /// </summary>
    [BindProperty(SupportsGet = true)] public string? Next { get; set; }

    private string LandingUrl => Next switch
    {
        "request" => "/Sponsor/Shop/Cart#request",
        "order" => "/Sponsor/Shop/Cart",
        _ => "/Sponsor/Shop?welcome=1",
    };
    [BindProperty] public string? CaptchaToken { get; set; }
    [BindProperty] public string? CaptchaAnswer { get; set; }
    /// <summary>Honeypot — hidden off-screen; any value ⇒ silently ignored (bot).</summary>
    [BindProperty] public string? Website2 { get; set; }

    // Wizard fields (the old public form's questions, minus passwords).
    [BindProperty] public string? CompanyName { get; set; }
    [BindProperty] public string? Cvr { get; set; }
    [BindProperty] public string? CompanyEmail { get; set; }
    [BindProperty] public string? Phone { get; set; }
    [BindProperty] public string? WebsiteUrl { get; set; }
    [BindProperty] public string? LinkedIn { get; set; }
    [BindProperty] public string? Twitter { get; set; }
    [BindProperty] public string? Address { get; set; }
    [BindProperty] public string? Postcode { get; set; }
    [BindProperty] public string? City { get; set; }
    [BindProperty] public string Country { get; set; } = "DK";
    [BindProperty] public string Currency { get; set; } = "DKK";
    [BindProperty] public string? SignerFirst { get; set; }
    [BindProperty] public string? SignerLast { get; set; }
    [BindProperty] public string? SignerEmail { get; set; }
    [BindProperty] public string? SignerPhone { get; set; }
    [BindProperty] public bool SeparateCoordinator { get; set; }
    [BindProperty] public string? CoordFirst { get; set; }
    [BindProperty] public string? CoordLast { get; set; }
    [BindProperty] public string? CoordEmail { get; set; }
    [BindProperty] public string? CoordPhone { get; set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        if (await ContextAsync(ct) is null) return;
        Challenge = _captcha.NewChallenge();
    }

    public async Task<IActionResult> OnPostEmailAsync(CancellationToken ct)
    {
        var eventId = await ContextAsync(ct);
        if (eventId is null) return Page();
        if (!string.IsNullOrWhiteSpace(Website2))
        {
            // Bot: behave as if it worked, send nothing.
            Current = Step.Code;
            return Page();
        }
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        if (!await _captcha.VerifyAsync(CaptchaToken, UsesTurnstile ? Request.Form["cf-turnstile-response"].ToString() : CaptchaAnswer, ip, ct))
        {
            Fail("The check did not pass. Please answer the question again.");
            Challenge = _captcha.NewChallenge();
            return Page();
        }
        var r = await _registration.StartAsync(eventId.Value, Email ?? string.Empty, ip, ct);
        if (!r.Ok) { Fail(r.Message); Challenge = _captcha.NewChallenge(); return Page(); }
        Message = r.Message;
        Current = Step.Code;
        return Page();
    }

    public async Task<IActionResult> OnPostCodeAsync(CancellationToken ct)
    {
        var eventId = await ContextAsync(ct);
        if (eventId is null) return Page();
        var (r, token) = await _registration.VerifyAsync(eventId.Value, Email ?? string.Empty, Code ?? string.Empty, ct);
        if (!r.Ok) { Fail(r.Message); Current = Step.Code; return Page(); }
        Session = token;
        SignerEmail = Email?.Trim().ToLowerInvariant();
        // §1309 — a company CEH already knows for this e-mail's domain is JOINED, never registered a second time.
        Matches = await _directory.FindByEmailDomainAsync(eventId.Value, SignerEmail ?? string.Empty, ct);

        // §1309 A — the code proves the address just as the login PIN does: an existing sponsor contact is signed in and
        // goes to the shop (operator 2026-09-27: "can we redirect the login if a sponsor uses the sponsor registration page
        // to login by mistake"). Only when their domain also matches a company they are NOT in yet (a group's shared
        // coordinator) do they see the Join step — with a "go to the shop" button.
        var existing = await ExistingContactAsync(eventId.Value, SignerEmail, ct);
        if (existing is not null)
        {
            var mine = await _db.SponsorCompanyContacts.AsNoTracking().Where(l => l.EventId == eventId.Value && l.ParticipantId == existing.Id)
                .Select(l => l.SponsorCompanyId).ToListAsync(ct);
            Matches = Matches.Where(m => !mine.Contains(m.SponsorCompanyId)).ToList();
            if (Matches.Count == 0) return await SignInAndGoAsync(existing, null, ct);
            IsExistingContact = true;
        }
        if (Matches.Count > 0)
        {
            await LoadJoinAsync(eventId.Value, Matches.Count == 1 ? Matches[0].SponsorCompanyId : null, ct);
            Current = Step.Join;
            return Page();
        }
        Current = Step.Wizard;
        return Page();
    }

    /// <summary>§1309 A — "Go to the shop" for an existing contact who was shown the Join step.</summary>
    public async Task<IActionResult> OnPostShopAsync(CancellationToken ct)
    {
        var eventId = await ContextAsync(ct);
        if (eventId is null) return Page();
        var verified = await _registration.VerifiedEmailAsync(eventId.Value, Session, ct);
        if (verified is null) return Expired();
        var existing = await ExistingContactAsync(eventId.Value, verified, ct);
        if (existing is null) return Expired();
        return await SignInAndGoAsync(existing, null, ct);
    }

    /// <summary>An active SPONSOR contact with this e-mail in the edition (any other role is never signed in from here).</summary>
    private async Task<Participant?> ExistingContactAsync(int eventId, string? email, CancellationToken ct)
    {
        var e = email?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(e)) return null;
        var p = await _db.Participants.AsNoTracking().FirstOrDefaultAsync(x => x.EventId == eventId && x.Email == e, ct);
        return p is { Role: ParticipantRole.Sponsor, IsActive: true }
               && await _db.SponsorCompanyContacts.AnyAsync(l => l.EventId == eventId && l.ParticipantId == p.Id, ct)
            ? p : null;
    }

    /// <summary>
    /// Sign in and open the shop. §1309 B — when a company is given (just registered / joined) the shop opens ON it, so a
    /// person with several companies does not land on their old one.
    /// </summary>
    private async Task<IActionResult> SignInAndGoAsync(Participant person, string? companyId, CancellationToken ct)
    {
        await ParticipantSessionSignIn.SignInAsync(HttpContext, person.Id, person.Email, person.FullName,
            person.Role, person.EventId, persistent: true);
        if (!string.IsNullOrWhiteSpace(companyId))
            await _acting.TrySelectAsync(person.Id, person.EventId, companyId, ct);
        return Redirect(LandingUrl);
    }

    /// <summary>§1309 — with several matching companies, the registrant picks one.</summary>
    public async Task<IActionResult> OnPostPickAsync(CancellationToken ct)
    {
        var eventId = await ContextAsync(ct);
        if (eventId is null) return Page();
        var verified = await _registration.VerifiedEmailAsync(eventId.Value, Session, ct);
        if (verified is null) return Expired();
        Matches = await _directory.FindByEmailDomainAsync(eventId.Value, verified, ct);
        await LoadJoinAsync(eventId.Value, Matches.Any(m => m.SponsorCompanyId == JoinCompanyId) ? JoinCompanyId : null, ct);
        Current = Step.Join;
        return Page();
    }

    /// <summary>§1309 — join the matched company. The company id is re-checked against the VERIFIED e-mail's domain.</summary>
    public async Task<IActionResult> OnPostJoinAsync(CancellationToken ct)
    {
        var eventId = await ContextAsync(ct);
        if (eventId is null) return Page();
        var verified = await _registration.VerifiedEmailAsync(eventId.Value, Session, ct);
        if (verified is null) return Expired();
        Matches = await _directory.FindByEmailDomainAsync(eventId.Value, verified, ct);
        if (!Matches.Any(m => m.SponsorCompanyId == JoinCompanyId))
        {
            Fail("That company does not match your e-mail address.");
            Current = Step.Join;
            await LoadJoinAsync(eventId.Value, null, ct);
            return Page();
        }
        var name = $"{JoinFirst?.Trim()} {JoinLast?.Trim()}".Trim();
        if (name.Length == 0)
        {
            Fail("Please enter your name.");
            Current = Step.Join;
            await LoadJoinAsync(eventId.Value, JoinCompanyId, ct);
            return Page();
        }
        var (r, pid) = await _directory.JoinAsync(eventId.Value, JoinCompanyId!, verified, name, JoinPhone, SignerKey, CoordKey, ct,
            claimSigner: JoinAsSigner, claimCoordinator: JoinAsCoordinator);
        if (!r.Ok || pid is null)
        {
            Fail(r.Message);
            Current = Step.Join;
            await LoadJoinAsync(eventId.Value, JoinCompanyId, ct);
            return Page();
        }
        var person = await _db.Participants.AsNoTracking().FirstAsync(p => p.Id == pid, ct);
        return await SignInAndGoAsync(person, JoinCompanyId, ct);
    }

    /// <summary>§1309 — "this is not my company": the full registration for a new company.</summary>
    public async Task<IActionResult> OnPostNotMineAsync(CancellationToken ct)
    {
        var eventId = await ContextAsync(ct);
        if (eventId is null) return Page();
        var verified = await _registration.VerifiedEmailAsync(eventId.Value, Session, ct);
        if (verified is null) return Expired();
        SignerEmail = verified;
        Current = Step.Wizard;
        return Page();
    }

    private async Task LoadJoinAsync(int eventId, string? companyId, CancellationToken ct)
    {
        Joining = companyId is null ? null : Matches.FirstOrDefault(m => m.SponsorCompanyId == companyId);
        if (Joining is null) return;
        JoinCompanyId = Joining.SponsorCompanyId;
        People = await _directory.PeopleForAsync(eventId, Joining.SponsorCompanyId, ct);
        CanPickRoles = await _directory.HasNoHubContactsAsync(eventId, Joining.SponsorCompanyId, ct);
    }

    private IActionResult Expired()
    {
        Fail("Your registration session has expired. Please start again.");
        Current = Step.Email;
        Challenge = _captcha.NewChallenge();
        return Page();
    }

    public async Task<IActionResult> OnPostCompleteAsync(CancellationToken ct)
    {
        var eventId = await ContextAsync(ct);
        if (eventId is null) return Page();
        Current = Step.Wizard;
        var verified = await _registration.VerifiedEmailAsync(eventId.Value, Session, ct);
        if (verified is null)
        {
            Fail("Your registration session has expired. Please start again.");
            Current = Step.Email;
            Challenge = _captcha.NewChallenge();
            return Page();
        }

        var input = Input();

        // §1309 — a CVR CEH already knows is never registered twice. The registrant's e-mail does not prove that company
        // (no domain match got them here), so they ask to JOIN and an organizer decides; nothing they typed is stored on it.
        var byCvr = await _directory.FindByCvrAsync(eventId.Value, Cvr, ct);
        if (byCvr is not null)
        {
            // §1309 C — the confirmed address is already one of THAT company's people (known from e-conomic / the old
            // webshop, or already a contact): it proves the link, so they join straight away instead of waiting.
            if (await _directory.IsKnownPersonOfAsync(eventId.Value, byCvr.SponsorCompanyId, verified, ct))
            {
                var (joined, joinedId) = await _directory.JoinAsync(eventId.Value, byCvr.SponsorCompanyId, verified,
                    $"{SignerFirst?.Trim()} {SignerLast?.Trim()}".Trim(), SignerPhone, "me", "me", ct);
                if (joined.Ok && joinedId is int jid)
                {
                    var joinedPerson = await _db.Participants.AsNoTracking().FirstAsync(p => p.Id == jid, ct);
                    return await SignInAndGoAsync(joinedPerson, byCvr.SponsorCompanyId, ct);
                }
            }
            var req = await _directory.RequestJoinAsync(eventId.Value, byCvr.SponsorCompanyId, verified,
                $"{SignerFirst?.Trim()} {SignerLast?.Trim()}".Trim(), SignerPhone, "cvr", ct);
            Message = req.Message;
            MessageIsError = !req.Ok;
            Current = Step.Pending;
            return Page();
        }

        var problems = await _registration.ValidateAsync(input, verified, ct);
        if (problems.Count > 0) { Problems = problems; return Page(); }

        var outcome = await _registration.CompleteAsync(eventId.Value, Session!, input, ct);
        if (!outcome.Ok) { Fail(outcome.Message); return Page(); }

        var person = await _db.Participants.AsNoTracking().FirstAsync(p => p.Id == outcome.SignInParticipantId, ct);
        return await SignInAndGoAsync(person, outcome.SponsorCompanyId, ct);
    }

    private ShopRegistrationInput Input() => new(
        CompanyName ?? string.Empty, Cvr ?? string.Empty, CompanyEmail, Phone, WebsiteUrl, LinkedIn, Twitter,
        Address ?? string.Empty, Postcode ?? string.Empty, City ?? string.Empty, Country, Currency,
        new ShopRegistrationPerson(SignerFirst ?? string.Empty, SignerLast ?? string.Empty, SignerEmail ?? string.Empty, SignerPhone),
        SeparateCoordinator
            ? new ShopRegistrationPerson(CoordFirst ?? string.Empty, CoordLast ?? string.Empty, CoordEmail ?? string.Empty, CoordPhone)
            : null);

    /// <summary>
    /// §1312 — the "Verify VAT number" button (operator 2026-09-27: "once the sponsor has entered details they click verify
    /// and if green then they can submit the request. this is not relevant for fx usa companies"). JSON for the wizard:
    /// green / red / unavailable / notneeded, the note, the registered name, and the number with any country code stripped
    /// ("strip if sponsor adds country code in the field"). 🔒 Only for a verified registration session, so the page is not
    /// an open VIES proxy; the final Register repeats the check server-side, so it cannot be skipped.
    /// </summary>
    public async Task<IActionResult> OnPostVerifyVatAsync(CancellationToken ct)
    {
        var eventId = await ContextAsync(ct);
        if (eventId is null) return new JsonResult(new { state = "red", note = "Registration is not open." });
        var verified = await _registration.VerifiedEmailAsync(eventId.Value, Session, ct);
        if (verified is null) return new JsonResult(new { state = "red", note = "Your registration session has expired. Please start again." });
        var v = await _registration.VerifyVatAsync(Input(), verified, ct);
        var state = v.Valid switch
        {
            true => "green",
            false => "red",
            null => Core.Integrations.Webshop.ShopVatChecker.NeedsVies(Country, Cvr) ? "unavailable" : "notneeded",
        };
        var number = Core.Integrations.Erp.LiveEconomicErpClient.NormaliseCvr(Cvr);
        // §1312 — a Northern Irish number keeps its XI prefix: it is what makes the final check go to VIES.
        if (Core.Integrations.Webshop.ShopVatChecker.IsNorthernIrish(Country, Cvr) && number is not null) number = "XI" + number;
        return new JsonResult(new { state, note = v.Note, name = v.RegisteredName, number });
    }

    /// <summary>The active edition, or null (with <see cref="Closed"/> set) when registration is not open.</summary>
    private async Task<int?> ContextAsync(CancellationToken ct)
    {
        var ev = await _db.Events.AsNoTracking().Where(e => e.IsActive)
            .Select(e => new { e.Id, Name = e.DisplayName }).FirstOrDefaultAsync(ct);
        if (ev is null) { Closed = true; return null; }
        EventName = ev.Name;
        var open = await _gate.IsFeatureEnabledAsync(ShopMailer.FeatureKey, ev.Id, ct);
        var me = _participant.Current;
        IsPreview = !open && me is not null && OrganizerAuth.IsRealOrganizer(me);
        if (!open && !IsPreview) { Closed = true; return null; }
        return ev.Id;
    }

    private void Fail(string message)
    {
        Message = message;
        MessageIsError = true;
    }

    public static IReadOnlyList<(string Code, string Name)> Countries => Core.Domain.Countries.All;
}
