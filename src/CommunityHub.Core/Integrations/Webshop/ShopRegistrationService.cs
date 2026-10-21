using System.Security.Cryptography;
using System.Text;
using CommunityHub.Core.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Erp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.Webshop;

/// <summary>One person entered in the registration wizard.</summary>
public sealed record ShopRegistrationPerson(string FirstName, string LastName, string Email, string? Phone)
{
    public string FullName => $"{FirstName.Trim()} {LastName.Trim()}".Trim();
}

/// <summary>Everything the registration wizard asks — the questions of the old public sponsor form, minus passwords.</summary>
public sealed record ShopRegistrationInput(
    string CompanyName,
    string Cvr,
    string? CompanyEmail,
    string? Phone,
    string? Website,
    string? LinkedIn,
    string? Twitter,
    string Address,
    string Postcode,
    string City,
    string CountryIso2,
    string Currency,
    ShopRegistrationPerson Signer,
    /// <summary>Null ⇒ the signer is also the event coordinator (the old form's unticked checkbox).</summary>
    ShopRegistrationPerson? Coordinator);

/// <summary>The outcome of completing a registration.</summary>
public sealed record ShopRegistrationOutcome(bool Ok, string Message, string? SponsorCompanyId, int? SignInParticipantId);

/// <summary>
/// §1305 item 1 — sponsor registration inside CEH: e-mail proof → the get-started wizard → the company, its
/// people and its e-conomic customer — replacing the registration webhooks.
/// </summary>
/// <remarks>
/// <para><b>What the retired webhooks did, and where each step lives now</b> (DESIGN §1305):</para>
/// <list type="number">
///   <item>Validate the VAT number → <see cref="ICvrValidator"/> for Danish companies (checksum). Foreign VAT
///   numbers are stored as given and flagged for the organizer; there is no live VIES lookup in CEH yet.</item>
///   <item>Store the CVR without its country prefix → <see cref="LiveEconomicErpClient.NormaliseCvr"/>.</item>
///   <item>Derive the VAT zone from the COUNTRY, never from the form → <see cref="VatZoneMapper"/>.</item>
///   <item>Create or find the e-conomic customer — matched by CVR first, then name (the webhook matched on name
///   only) — with the address block.</item>
///   <item>Create the signer + coordinator as e-conomic contacts with their roles (Role:1 / Role:2).</item>
///   <item>Mail: the organizers get a notice; the registrant is signed in straight away.</item>
/// </list>
/// <para>🔑 The company starts as a <see cref="ShopCompanyState.Candidate"/>: its <see cref="SponsorInfo"/> is
/// created with <c>IsSponsor = false</c>, whose query filter keeps it out of every sponsor list, mail and job
/// until it signs. Operator: <i>"This is not equal to an order or where they are considered a sponsor"</i>.</para>
/// <para>🔒 An e-conomic failure does NOT fail the registration: the company can still shop; the error is
/// stored on it and the organizers are told, so the customer can be created by hand before invoicing.</para>
/// </remarks>
public sealed class ShopRegistrationService
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(15);
    public const int MaxCodeAttempts = 5;
    public const int MaxCodesPerEmailPerHour = 5;
    public const int MaxCodesPerIpPerHour = 20;
    public static readonly string[] Currencies = { "DKK", "EUR", "USD" };

    private readonly CommunityHubDbContext _db;
    private readonly PinService _pins;
    private readonly ShopMailer _mail;
    private readonly ICvrValidator _cvr;
    private readonly IEconomicErpClient? _erp;
    private readonly EconomicContactAdminService? _erpContacts;
    private readonly ShopVatChecker? _vat;
    private readonly SponsorCompanyMembershipService _membership;
    private readonly TimeProvider _clock;
    private readonly ILogger<ShopRegistrationService>? _log;

    public ShopRegistrationService(
        CommunityHubDbContext db, PinService pins, ShopMailer mail, ICvrValidator cvr,
        SponsorCompanyMembershipService membership, TimeProvider clock,
        IEconomicErpClient? erp = null, EconomicContactAdminService? erpContacts = null,
        ILogger<ShopRegistrationService>? log = null, ShopVatChecker? vat = null)
    {
        _vat = vat;
        _db = db;
        _pins = pins;
        _mail = mail;
        _cvr = cvr;
        _membership = membership;
        _clock = clock;
        _erp = erp;
        _erpContacts = erpContacts;
        _log = log;
    }

    // ------------------------------------------------------------------------------------------
    // Step 1 + 2: prove the e-mail address
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Send a one-time code. Always answers the same way, so the form cannot be used to probe addresses.
    /// </summary>
    public async Task<ShopResult> StartAsync(int eventId, string email, string? ip, CancellationToken ct = default)
    {
        var normalized = (email ?? string.Empty).Trim().ToLowerInvariant();
        if (!IsPlausibleEmail(normalized)) return ShopResult.Fail("Please enter a valid e-mail address.");

        var now = _clock.GetUtcNow();
        var hourAgo = now.AddHours(-1);
        var byEmail = await _db.ShopRegistrations.CountAsync(r => r.EventId == eventId && r.Email == normalized && r.CreatedAt > hourAgo, ct);
        var byIp = ip is null ? 0 : await _db.ShopRegistrations.CountAsync(r => r.EventId == eventId && r.RequestIp == ip && r.CreatedAt > hourAgo, ct);
        if (byEmail >= MaxCodesPerEmailPerHour || byIp >= MaxCodesPerIpPerHour)
            return ShopResult.Fail("Too many codes were requested. Please wait an hour and try again.");

        var code = _pins.GeneratePin();
        _db.ShopRegistrations.Add(new ShopRegistration
        {
            EventId = eventId,
            Email = normalized,
            CodeHash = _pins.HashPin(code),
            CreatedAt = now,
            ExpiresAt = now + CodeLifetime,
            RequestIp = ip,
        });
        await _db.SaveChangesAsync(ct);

        try { await _mail.SendRegistrationCodeAsync(eventId, normalized, code, (int)CodeLifetime.TotalMinutes, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.LogWarning(ex, "§1305 registration code mail failed.");
            return ShopResult.Fail("The code could not be sent right now. Please try again in a minute.");
        }
        return ShopResult.Success($"We sent a code to {normalized}. It is valid for {(int)CodeLifetime.TotalMinutes} minutes.");
    }

    /// <summary>
    /// Check the code. On success returns a one-time SESSION token the wizard posts back (its hash is stored).
    /// </summary>
    public async Task<(ShopResult Result, string? SessionToken)> VerifyAsync(
        int eventId, string email, string code, CancellationToken ct = default)
    {
        var normalized = (email ?? string.Empty).Trim().ToLowerInvariant();
        var now = _clock.GetUtcNow();
        var reg = await _db.ShopRegistrations
            .Where(r => r.EventId == eventId && r.Email == normalized && r.VerifiedAt == null && r.ExpiresAt > now)
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync(ct);
        if (reg is null) return (ShopResult.Fail("The code has expired. Please ask for a new one."), null);
        if (reg.FailedAttempts >= MaxCodeAttempts) return (ShopResult.Fail("Too many wrong attempts. Please ask for a new code."), null);

        if (!_pins.VerifyPin((code ?? string.Empty).Trim(), reg.CodeHash))
        {
            reg.FailedAttempts++;
            await _db.SaveChangesAsync(ct);
            return (ShopResult.Fail("That code is not right."), null);
        }

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        reg.VerifiedAt = now;
        reg.SessionTokenHash = Hash(token);
        await _db.SaveChangesAsync(ct);
        return (ShopResult.Success(), token);
    }

    /// <summary>The verified e-mail for a session token, or null when it is unknown, used or older than 2 hours.</summary>
    public async Task<string?> VerifiedEmailAsync(int eventId, string? sessionToken, CancellationToken ct = default)
    {
        var reg = await SessionAsync(eventId, sessionToken, ct);
        return reg?.Email;
    }

    private async Task<ShopRegistration?> SessionAsync(int eventId, string? sessionToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sessionToken)) return null;
        var hash = Hash(sessionToken);
        var cutoff = _clock.GetUtcNow().AddHours(-2);
        return await _db.ShopRegistrations.FirstOrDefaultAsync(r => r.EventId == eventId && r.SessionTokenHash == hash
            && r.CompletedAt == null && r.VerifiedAt != null && r.VerifiedAt > cutoff, ct);
    }

    // ------------------------------------------------------------------------------------------
    // Step 3: the wizard is submitted
    // ------------------------------------------------------------------------------------------

    /// <summary>Validate the wizard input. Returns the problems a registrant must fix (empty ⇒ fine).</summary>
    public async Task<IReadOnlyList<string>> ValidateAsync(ShopRegistrationInput i, string verifiedEmail, CancellationToken ct = default)
    {
        var errors = new List<string>();
        void Need(string? v, string label) { if (string.IsNullOrWhiteSpace(v)) errors.Add($"{label} is required."); }
        Need(i.CompanyName, "Company name");
        Need(i.Cvr, "CVR / VAT number");
        Need(i.Address, "Address");
        Need(i.Postcode, "Postcode");
        Need(i.City, "City");
        Need(i.CountryIso2, "Country");
        if (!Currencies.Contains((i.Currency ?? string.Empty).ToUpperInvariant())) errors.Add("Choose a currency (DKK, EUR or USD).");
        // §1312 — website and LinkedIn are required, and every address must be secure (operator 2026-09-27: "linkedin +
        // website is mandatory" · "it must be clear if people must add https:// or not in urls. it should block if http://
        // is used, as zoho does not support non-secure url").
        Need(i.Website, "Website");
        Need(i.LinkedIn, "LinkedIn URL");
        // Operator 2026-09-27: the company / invoice e-mail is mandatory.
        Need(i.CompanyEmail, "Company / invoice e-mail");
        if (!string.IsNullOrWhiteSpace(i.CompanyEmail) && !IsPlausibleEmail(i.CompanyEmail.Trim().ToLowerInvariant()))
            errors.Add("The company / invoice e-mail is not valid.");
        foreach (var (raw, label, linkedIn) in new[] { (i.Website, "Website", false), (i.LinkedIn, "LinkedIn URL", true), (i.Twitter, "X / Twitter URL", false) })
        {
            var u = SecureUrl(raw);
            if (u.Error is not null) errors.Add($"{label} \"{raw?.Trim()}\": {u.Error}");
            // Any LinkedIn host (www / country subdomains, the lnkd.in short link). Operator 2026-09-27, after a stricter check
            // refused a LinkedIn address typed without https:// — "i did not provide https as you told me not to do".
            else if (linkedIn && u.Url is not null && new Uri(u.Url).Host is var h
                     && !h.Contains("linkedin.", StringComparison.OrdinalIgnoreCase) && !h.Equals("lnkd.in", StringComparison.OrdinalIgnoreCase))
                errors.Add($"{label} \"{raw?.Trim()}\" is not a LinkedIn address (e.g. https://www.linkedin.com/company/your-company).");
        }
        foreach (var (p, role) in new[] { (i.Signer, "signer"), (i.Coordinator, "event coordinator") })
        {
            if (p is null) continue;
            Need(p.FirstName, $"The {role}'s first name");
            Need(p.LastName, $"The {role}'s last name");
            if (!IsPlausibleEmail(p.Email?.Trim().ToLowerInvariant() ?? string.Empty)) errors.Add($"The {role}'s e-mail is not valid.");
        }

        // The verified address must belong to one of the people being registered: that is what makes the
        // registration authenticated.
        var emails = new[] { i.Signer.Email, i.Coordinator?.Email }.Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e!.Trim().ToLowerInvariant()).ToList();
        if (!emails.Contains(verifiedEmail)) errors.Add($"Your verified address ({verifiedEmail}) must be the signer's or the event coordinator's e-mail.");

        if (string.Equals(i.CountryIso2, "DK", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(i.Cvr))
        {
            var r = await _cvr.ValidateAsync(i.Cvr, ct);
            if (!r.IsValid) errors.Add($"The CVR number is not valid ({r.Reason}).");

        }
        else if (_vat is not null && !string.IsNullOrWhiteSpace(i.Cvr) && !string.IsNullOrWhiteSpace(i.CountryIso2))
        {
            // An EU VAT number the EU register says is NOT valid stops the registration, as the webhook did.
            // Unreachable / non-EU is recorded on the company instead (fail open, visibly).
            var v = await _vat.CheckAsync(i.CountryIso2, i.Cvr, ct);
            if (v.Valid == false)
            {
                errors.Add($"The VAT number is not valid in the EU VIES register. {v.Note}");
                await AlertVatAsync(i, verifiedEmail, "was refused: the EU VIES register says the VAT number is NOT valid", v.Note, ct);
            }
            else if (v.Valid is null && ShopVatChecker.NeedsVies(i.CountryIso2, i.Cvr))
            {
                // §1312 — an EU company needs a GREEN check before registration completes (operator 2026-09-27: "a prereq of a
                // green verification before registration can complete for european companies covered by the service").
                errors.Add("Your VAT number could not be verified right now — the EU VAT service (VIES) is not answering for your "
                           + "country. Please press Verify again in a little while. We have been told and can help.");
                await AlertVatAsync(i, verifiedEmail, "could not complete: the EU VIES register is not answering for this country, so the VAT number could not be verified", v.Note, ct);
            }
        }
        return errors;
    }

    /// <summary>
    /// §1312 — a web address as it is stored: "https://" is added when the registrant typed none ("www.company.com"); an
    /// insecure "http://" address is REFUSED, because the event platform (Zoho Backstage) does not accept one. Blank ⇒ (null,
    /// null); the required check is separate.
    /// </summary>
    public static (string? Url, string? Error) SecureUrl(string? raw)
    {
        var s = raw?.Trim();
        if (string.IsNullOrEmpty(s)) return (null, null);
        // §1312 — http:// is UPGRADED, not refused (operator 2026-09-30: "change all … to https:// … noone uses http:// today").
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) s = "https://" + s[7..];
        if (!s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) s = "https://" + s;
        return Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.Host.Contains('.')
               && !u.Host.Contains(' ')
            ? (u.AbsoluteUri, null)
            : (null, "that is not a valid web address.");
    }

    /// <summary>
    /// §1312 — the registration page's "Verify VAT number" button (operator 2026-09-27: "once the sponsor has entered details
    /// they click verify and if green then they can submit the request. this is not relevant for fx usa companies").
    /// Denmark: CVR checksum. EU (VIES): the register's answer; a refusal or an unanswered check tells the organizers.
    /// Outside the EU: nothing to verify (<see cref="VatCheckResult.Valid"/> null, and <see cref="ShopVatChecker.NeedsVies"/> false).
    /// </summary>
    public async Task<VatCheckResult> VerifyVatAsync(ShopRegistrationInput i, string verifiedEmail, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(i.Cvr)) return new(false, "Enter the CVR / VAT number first.");
        if (string.Equals(i.CountryIso2, "DK", StringComparison.OrdinalIgnoreCase))
        {
            var r = await _cvr.ValidateAsync(i.Cvr, ct);
            return r.IsValid ? new(true, "Danish CVR number — valid.") : new(false, $"The CVR number is not valid ({r.Reason}).");
        }
        if (!ShopVatChecker.NeedsVies(i.CountryIso2, i.Cvr)) return new(null, "Outside the EU — no VAT check is needed.");
        if (_vat is null) return new(null, "The VAT check is not available right now.");
        var v = await _vat.CheckAsync(i.CountryIso2!, i.Cvr, ct);
        if (v.Valid == false)
            await AlertVatAsync(i, verifiedEmail, "was refused at Verify: the EU VIES register says the VAT number is NOT valid", v.Note, ct);
        else if (v.Valid is null)
            await AlertVatAsync(i, verifiedEmail, "could not be verified: the EU VIES register is not answering for this country", v.Note, ct);
        return v;
    }

    /// <summary>
    /// Tell the organizers when a registrant's EU VAT number fails, so they can reach out (operator 2026-09-27: "i must get
    /// alerts if someone tries but it fails, so i can reach out to them and help. send mail if the vat checker fails for a
    /// potential sponsor, so i dont loose a customer" — a wrong EU VAT id means the VAT cannot be refunded). One alert per
    /// e-mail + VAT number per day, so retries do not flood the mailbox. Organizers only — the registrant is not mailed.
    /// </summary>
    private async Task AlertVatAsync(ShopRegistrationInput i, string verifiedEmail, string what, string note, CancellationToken ct)
    {
        try
        {
            var eventId = await _db.Events.Where(e => e.IsActive).Select(e => e.Id).FirstOrDefaultAsync(ct);
            var key = $"vat-alert:{verifiedEmail}:{(i.CountryIso2 ?? "").ToUpperInvariant()}{i.Cvr?.Trim()}:{_clock.GetUtcNow():yyyy-MM-dd}";
            if (await _db.SentReminders.AnyAsync(s => s.EventId == eventId && s.ReminderType == "shop-vat-alert" && s.OccasionKey == key, ct)) return;
            static string E(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
            var person = i.Signer.Email?.Trim().ToLowerInvariant() == verifiedEmail ? i.Signer : i.Coordinator ?? i.Signer;
            await _mail.NotifyOrganizersAsync(eventId, $"Sponsor registration VAT problem: {i.CompanyName} ({i.CountryIso2} {i.Cvr})",
                $"<p>A sponsor registration {E(what)}.</p><ul>"
                + $"<li>Company: <b>{E(i.CompanyName)}</b>, {E(i.City)}, {E(i.CountryIso2)}</li>"
                + $"<li>VAT number typed: <b>{E(i.Cvr)}</b> — {E(note)}</li>"
                + $"<li>Person: {E(person.FullName)} — <a href=\"mailto:{E(verifiedEmail)}\">{E(verifiedEmail)}</a>{(string.IsNullOrWhiteSpace(person.Phone) ? "" : $", {E(person.Phone)}")}</li>"
                + "</ul><p>Reach out to help them with the right VAT number.</p>", ct);
            _db.SentReminders.Add(new SentReminder { EventId = eventId, RecipientEmail = "organizers", ReminderType = "shop-vat-alert", OccasionKey = key, SentAt = _clock.GetUtcNow() });
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.LogWarning(ex, "§1309 VAT alert to the organizers could not be sent.");
        }
    }

    /// <summary>
    /// Create the company, its people and its e-conomic customer. Returns the participant to sign in (the
    /// registrant).
    /// </summary>
    public async Task<ShopRegistrationOutcome> CompleteAsync(
        int eventId, string sessionToken, ShopRegistrationInput i, CancellationToken ct = default)
    {
        var session = await SessionAsync(eventId, sessionToken, ct);
        if (session is null) return new(false, "Your registration session has expired. Please start again.", null, null);

        var problems = await ValidateAsync(i, session.Email, ct);
        if (problems.Count > 0) return new(false, string.Join(" ", problems), null, null);

        var now = _clock.GetUtcNow();
        var country = i.CountryIso2.Trim().ToUpperInvariant();
        var cvr = LiveEconomicErpClient.NormaliseCvr(i.Cvr);

        // 🔒 De-duplicate on the CVR: a company registers once per edition. A colleague who registers the same
        // company again is sent to the existing one (sign in) instead of creating a second.
        if (cvr is not null && await _db.ShopCompanies.AnyAsync(c => c.EventId == eventId && c.Cvr == cvr, ct))
        {
            await _mail.NotifyOrganizersAsync(eventId, $"Duplicate registration attempt: {i.CompanyName}",
                $"<p>{System.Net.WebUtility.HtmlEncode(session.Email)} tried to register CVR {cvr}, which is already registered. "
                + "If they should be a contact of that company, add them.</p>", ct);
            return new(false, "This company is already registered. Ask your colleague to add you, or contact the organizers.", null, null);
        }

        var zone = VatZoneMapper.EconomicZoneNumber(VatZoneMapper.Resolve(country)) ?? 0;
        var companyId = "ceh-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        var company = new ShopCompany
        {
            EventId = eventId,
            SponsorCompanyId = companyId,
            Source = ShopCompanySource.Registered,
            State = ShopCompanyState.Candidate,
            LegalName = i.CompanyName.Trim(),
            // 🔴 §1312 — the PUBLIC name is the legal name without its legal form (§1158's rule, the one the ERP → CM sync
            // uses). Operator 2026-09-29: "it is using the billing company name in the creation on master packager, that is
            // wrong. it must be the public name" — "Master Packager SIA" reached Zoho and the event site.
            PublicName = CompanyLegalForm.Strip(i.CompanyName),
            Cvr = cvr,
            Address = i.Address.Trim(),
            Postcode = i.Postcode.Trim(),
            City = i.City.Trim(),
            CountryIso2 = country,
            CompanyEmail = Blank(i.CompanyEmail),
            InvoiceEmail = Blank(i.CompanyEmail),
            Phone = Blank(i.Phone),
            Website = SecureUrl(i.Website).Url,
            LinkedInUrl = SecureUrl(i.LinkedIn).Url,
            TwitterUrl = SecureUrl(i.Twitter).Url,
            Currency = i.Currency.Trim().ToUpperInvariant(),
            VatZone = zone,
            VatCheck = country == "DK" ? "Danish CVR — checksum valid."
                : _vat is null ? "Not checked." : (await _vat.CheckAsync(country, i.Cvr, ct)).Note,
            CreatedAt = now,
            RegisteredByEmail = session.Email,
        };
        _db.ShopCompanies.Add(company);

        // The candidate's sponsor record — hidden (IsSponsor=false) until it signs.
        _db.SponsorInfos.Add(new SponsorInfo
        {
            EventId = eventId,
            SponsorCompanyId = companyId,
            CompanyName = company.PublicName,
            WebsiteUrl = company.Website,
            LinkedInUrl = company.LinkedInUrl,
            TwitterUrl = company.TwitterUrl,
            IsSponsor = false,
            CreatedAt = now,
        });

        var coordinator = i.Coordinator ?? i.Signer;
        var sameperson = string.Equals(coordinator.Email.Trim(), i.Signer.Email.Trim(), StringComparison.OrdinalIgnoreCase);
        var signer = await PersonAsync(eventId, companyId, i.Signer, now, ct);
        var coord = sameperson ? signer : await PersonAsync(eventId, companyId, coordinator, now, ct);
        await _db.SaveChangesAsync(ct);

        await _membership.LinkAsync(eventId, signer.Id, companyId, isSigner: true,
            isEventCoordinator: sameperson, isPrimary: signer.SponsorCompanyId == companyId, ct: ct);
        if (!sameperson)
            await _membership.LinkAsync(eventId, coord.Id, companyId, isSigner: false,
                isEventCoordinator: true, isPrimary: coord.SponsorCompanyId == companyId, ct: ct);

        session.CompletedAt = now;
        session.SponsorCompanyId = companyId;
        await _db.SaveChangesAsync(ct);

        await CreateInErpAsync(company, i, sameperson, ct);

        await _mail.NotifyOrganizersAsync(eventId, $"New sponsor registration: {company.LegalName}",
            $"<p><b>{Enc(company.LegalName)}</b> ({Enc(country)}, CVR {Enc(cvr ?? "-")}) registered as a sponsor candidate.</p>"
            + $"<p>Signer: {Enc(signer.FullName)} &lt;{Enc(signer.Email)}&gt;<br>Event coordinator: {Enc(coord.FullName)} &lt;{Enc(coord.Email)}&gt;</p>"
            + (company.ErpError is null
                ? $"<p>VAT: {Enc(company.VatCheck ?? "-")}</p><p>e-conomic customer: {Enc(company.ErpCustomerNumber ?? "(not created — writes off here)")}</p>"
                : $"<p style=\"color:#b91c1c\"><b>e-conomic customer NOT created:</b> {Enc(company.ErpError)}</p>"), ct);

        var registrantId = string.Equals(session.Email, signer.Email, StringComparison.OrdinalIgnoreCase) ? signer.Id : coord.Id;
        return new(true, "Welcome — your company is registered.", companyId, registrantId);
    }

    /// <summary>
    /// The participant for a registered person: an existing one (same e-mail, this edition) is LINKED to the new
    /// company — never moved — because one person may serve several companies (§1235).
    /// </summary>
    private async Task<Participant> PersonAsync(int eventId, string companyId, ShopRegistrationPerson p, DateTimeOffset now, CancellationToken ct)
    {
        var email = p.Email.Trim().ToLowerInvariant();
        var existing = await _db.Participants.FirstOrDefaultAsync(x => x.EventId == eventId && x.Email == email, ct);
        if (existing is not null)
        {
            if (string.IsNullOrWhiteSpace(existing.Phone) && !string.IsNullOrWhiteSpace(p.Phone)) existing.Phone = p.Phone.Trim();
            return existing;
        }
        var person = new Participant
        {
            EventId = eventId,
            Email = email,
            FullName = p.FullName,
            Phone = Blank(p.Phone),
            Role = ParticipantRole.Sponsor,
            SponsorCompanyId = companyId,
            IsActive = true,
            LifecycleState = ParticipantLifecycleState.Active,
            CreatedAt = now,
        };
        _db.Participants.Add(person);
        return person;
    }

    /// <summary>
    /// §1312 — create (or link, by CVR) the e-conomic customer of a CEH-REGISTERED company that has none yet, with its signer
    /// and coordinator as contacts — the step registration does, run again from what CEH holds. For a registration that
    /// could not reach e-conomic (BalanceMe, 2026-09-27). Never for a company that already has a customer number.
    /// </summary>
    public async Task<ShopResult> EnsureInErpAsync(int eventId, string companyId, CancellationToken ct = default)
    {
        var company = await _db.ShopCompanies.FirstOrDefaultAsync(c => c.EventId == eventId && c.SponsorCompanyId == companyId, ct);
        if (company is null) return ShopResult.Fail("That company is not known.");
        if (!string.IsNullOrWhiteSpace(company.ErpCustomerNumber))
            return ShopResult.Success($"It is already e-conomic customer {company.ErpCustomerNumber}.");
        if (company.Source != ShopCompanySource.Registered)
            return ShopResult.Fail("Only a company registered in CEH is created in e-conomic from here.");

        var links = await _db.SponsorCompanyContacts.AsNoTracking().Include(l => l.Participant)
            .Where(l => l.EventId == eventId && l.SponsorCompanyId == companyId && l.Participant.IsActive).ToListAsync(ct);
        var signer = links.FirstOrDefault(l => l.IsSigner)?.Participant ?? links.FirstOrDefault()?.Participant;
        if (signer is null) return ShopResult.Fail("The company has no signer in the hub to create as its contact.");
        var coord = links.FirstOrDefault(l => l.IsEventCoordinator)?.Participant ?? signer;
        static ShopRegistrationPerson Person(Participant p)
        {
            var parts = (p.FullName ?? p.Email).Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            return new(parts.ElementAtOrDefault(0) ?? p.Email, parts.ElementAtOrDefault(1) ?? string.Empty, p.Email, p.Phone);
        }
        var same = coord.Id == signer.Id;
        var input = new ShopRegistrationInput(company.LegalName, company.Cvr ?? string.Empty, company.CompanyEmail, company.Phone,
            company.Website, company.LinkedInUrl, company.TwitterUrl, company.Address ?? string.Empty, company.Postcode ?? string.Empty,
            company.City ?? string.Empty, company.CountryIso2, company.Currency, Person(signer), same ? null : Person(coord));

        await CreateInErpAsync(company, input, same, ct);
        return company.ErpCustomerNumber is not null
            ? ShopResult.Success($"Created in e-conomic as customer {company.ErpCustomerNumber}, with its contacts.")
            : ShopResult.Fail("e-conomic customer NOT created: " + (company.ErpError ?? "unknown reason"));
    }

    private async Task CreateInErpAsync(ShopCompany company, ShopRegistrationInput i, bool samePerson, CancellationToken ct)
    {
        // §1312 — never SILENT: e-conomic is the master of every registration, so a host that cannot reach it records why,
        // and the organizers' registration notice shows it in red (it used to return quietly — BalanceMe, 2026-09-27).
        if (_erp is null || !_erp.CanWrite)
        {
            company.ErpError = _erp is null
                ? "e-conomic is not connected on this server — the customer was NOT created. Use \"Create in e-conomic now\" on the company page."
                : "e-conomic is not configured (tokens / base URL) — the customer was NOT created.";
            _log?.LogError("§1312 e-conomic customer for {Company} NOT created: {Why}", company.LegalName, company.ErpError);
            await _db.SaveChangesAsync(ct);
            return;
        }
        try
        {
            var customer = new ErpCustomer(
                company.SponsorCompanyId, company.LegalName, company.Cvr, company.Currency,
                company.VatZone > 0 ? company.VatZone.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
                company.CompanyEmail, null,
                Address: company.Address, Zip: company.Postcode, City: company.City,
                Country: CountryName(company.CountryIso2), Phone: company.Phone, Website: company.Website);

            // CVR ONLY — never adopt another legal entity by NAME (§1309, 2026-09-27). CEH creates the customer through the
            // API and stamps the number right here, so no sync is needed to learn it.
            var number = await _erp.FindCustomerNumberByCvrAsync(company.Cvr, ct) ?? await _erp.CreateCustomerAsync(customer, ct);
            company.ErpCustomerNumber = number;
            company.ErpError = null;

            if (_erpContacts is not null && int.TryParse(number, out var customerNumber))
            {
                // De-duplicated by e-mail within THIS customer, as the retired webhook did. The same person in another company
                // is another customer's contact — CEH keeps ONE person with one link per company (switch company), and each
                // link carries that company's e-conomic contact number (operator 2026-09-27: "in ceh, we only have 1 contact
                // and use switch company" · "add it to the bundle").
                var signerContact = await _erpContacts.CreateOrUpdateByEmailAsync(customerNumber, i.Signer.FullName, i.Signer.Email, i.Signer.Phone,
                    signer: true, eventCoordinator: samePerson, ct);
                await StampContactNumberAsync(company, i.Signer.Email, signerContact, ct);
                if (!samePerson && i.Coordinator is { } c)
                {
                    var coordContact = await _erpContacts.CreateOrUpdateByEmailAsync(customerNumber, c.FullName, c.Email, c.Phone,
                        signer: false, eventCoordinator: true, ct);
                    await StampContactNumberAsync(company, c.Email, coordContact, ct);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            company.ErpError = ex.Message.Length > 480 ? ex.Message[..480] : ex.Message;
            _log?.LogWarning(ex, "§1305 e-conomic customer for {Company} could not be created.", company.LegalName);
        }
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Store the e-conomic contact number on this person's link to THIS company (one link per company).</summary>
    private async Task StampContactNumberAsync(ShopCompany company, string? email, int contactNumber, CancellationToken ct)
    {
        var e = email?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(e) || contactNumber <= 0) return;
        var link = await _db.SponsorCompanyContacts
            .FirstOrDefaultAsync(l => l.EventId == company.EventId && l.SponsorCompanyId == company.SponsorCompanyId && l.Participant.Email == e, ct);
        if (link is not null) link.ErpContactNumber = contactNumber;
    }

    private static bool IsPlausibleEmail(string e) =>
        e.Length is > 3 and < 256 && e.Contains('@') && e.IndexOf('@') > 0 && e.LastIndexOf('.') > e.IndexOf('@') + 1 && !e.Contains(' ');

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static string Enc(string s) => System.Net.WebUtility.HtmlEncode(s);
    private static string CountryName(string iso2) =>
        Countries.All.FirstOrDefault(c => string.Equals(c.Code, iso2, StringComparison.OrdinalIgnoreCase)).Name ?? iso2;
}
