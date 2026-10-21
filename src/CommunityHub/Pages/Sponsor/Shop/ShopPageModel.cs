using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Webshop;
using CommunityHub.Core.Settings;
using CommunityHub.Sponsors;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Sponsor.Shop;

/// <summary>
/// §1305 — what every sponsor-shop page needs first: who is here, whether the shop is open to them, and
/// which company they are buying for.
/// </summary>
/// <remarks>
/// <para>🔑 <b>The company, not the person, owns the cart</b> (operator: <i>"it must be stored under the company
/// so both signer and event coordinator can see the things in cart"</i>). The company is the one this contact
/// is ACTING for (§1236 switcher), so a person serving two companies shops for the one they chose.</para>
/// <para>🔑 <b>An organizer pre-stages a cart by acting as a contact</b> (operator: <i>"organizer must be able
/// to do switch user so i can prestage things in the cart to them"</i>). While acting, the current
/// participant IS that contact, so everything below resolves to their company unchanged.</para>
/// <para>🔒 <c>sponsor-webshop</c> OFF ⇒ sponsors are told it is not available; an organizer (not acting) still
/// sees the pages with a preview banner, because he cannot approve what he cannot open.</para>
/// </remarks>
public abstract class ShopPageModel : PageModel
{
    public const string FeatureKey = ShopMailer.FeatureKey;

    protected readonly ICurrentParticipantAccessor Participant;
    protected readonly FeatureGateService Gate;
    protected readonly SponsorActingCompanyService Acting;
    protected readonly ShopCompanyService Companies;

    protected ShopPageModel(
        ICurrentParticipantAccessor participant, FeatureGateService gate,
        SponsorActingCompanyService acting, ShopCompanyService companies)
    {
        Participant = participant;
        Gate = gate;
        Acting = acting;
        Companies = companies;
    }

    public bool NotAvailableYet { get; private set; }
    public bool IsPreview { get; private set; }
    public bool NoCompany { get; private set; }
    public bool IsOrganizer { get; private set; }
    public int EventId { get; private set; }
    public int ParticipantId { get; private set; }
    public string? CompanyId { get; private set; }
    public ShopCompany? Company { get; private set; }

    /// <summary>
    /// §1309 — every company this person is a contact of (sponsor OR potential sponsor), for the "Buying for … Switch"
    /// control. Operator 2026-09-27: "i need option to switch company in the cart and signing, as the same person can be
    /// linked to multiple companies … arrow have same event coordinator in all 4 countries".
    /// </summary>
    public IReadOnlyList<CommunityHub.Sponsors.SponsorCompanyOption> CompanyOptions { get; private set; } =
        Array.Empty<CommunityHub.Sponsors.SponsorCompanyOption>();

    /// <summary>Resolve the context. False ⇒ render the notice the flags above describe and stop.</summary>
    protected async Task<bool> ResolveAsync(CancellationToken ct, bool needCompany = true)
    {
        var me = Participant.Current;
        if (me is null) { NotAvailableYet = true; return false; }
        EventId = me.EventId;
        ParticipantId = me.ParticipantId;
        IsOrganizer = OrganizerAuth.IsRealOrganizer(me);

        var released = await Gate.IsFeatureEnabledAsync(FeatureKey, me.EventId, ct);
        if (!released && !IsOrganizer && !(me.IsActingAs && me.ActingAs is not null))
        {
            NotAvailableYet = true;
            return false;
        }
        IsPreview = !released;

        if (!needCompany) return true;
        CompanyOptions = await Acting.OptionsAsync(me.ParticipantId, me.EventId, ct);
        CompanyId = await Acting.CurrentAsync(me.ParticipantId, me.EventId, ct);
        if (string.IsNullOrWhiteSpace(CompanyId)) { NoCompany = true; return false; }
        Company = await Companies.GetOrCreateAsync(me.EventId, CompanyId, ct);
        if (Company is null) { NoCompany = true; return false; }
        return true;
    }

    /// <summary>
    /// §1312 — a link to ONE order (the signer's mail) opens on that order's company, when this person is linked to it.
    /// A signer for several companies (operator 2026-09-27: "he can be cfo for multiple companies") otherwise landed on
    /// "That order was not found for your company" whenever the switcher stood on another one. 🔒 The switch goes through
    /// <see cref="SponsorActingCompanyService.TrySelectAsync"/> — the same link check as the switcher — so an order of a
    /// company the person does not serve changes nothing.
    /// </summary>
    protected async Task FollowOrderCompanyAsync(string? orderCompanyId, CancellationToken ct)
    {
        var me = Participant.Current;
        if (me is null || string.IsNullOrWhiteSpace(orderCompanyId)
            || string.Equals(orderCompanyId, CompanyId, StringComparison.OrdinalIgnoreCase)) return;
        if (!await Acting.TrySelectAsync(me.ParticipantId, me.EventId, orderCompanyId, ct)) return;
        var company = await Companies.GetOrCreateAsync(me.EventId, orderCompanyId, ct);
        if (company is null) return;
        CompanyId = company.SponsorCompanyId;
        Company = company;
    }

    /// <summary>Who is acting, for audit and cancellation records.</summary>
    protected string ActorEmail => Participant.Current?.Email ?? "unknown";

    /// <summary>"€1,234" / "€11.50" — invariant, like the contract.</summary>
    public static string Eur(decimal v) =>
        "€" + ShopPricing.Amount(v);

    public static string GroupLabel(ShopGroup g) => g switch
    {
        ShopGroup.Booths => "Booths",
        ShopGroup.Packages => "Digital only sponsor",
        ShopGroup.Sessions => "Sessions",
        ShopGroup.Branding => "Branding",
        ShopGroup.Furniture => "Furniture",
        ShopGroup.BoothOptions => "Booth options",
        ShopGroup.PackageHandling => "Package handling",
        ShopGroup.Swag => "Swag",
        ShopGroup.Tickets => "Tickets",
        _ => "Other",
    };

    /// <summary>The groups in the order a sponsor walks through them.</summary>
    /// <summary>
    /// The order for a company that is ALREADY a sponsor: branding and sessions first, the booth last — they have their
    /// booth (operator 2026-09-26: "booth must not be first for existing sponsor, branding must be #1, sessions, booth is
    /// last. For new sponsor this order is fine").
    /// </summary>
    public static readonly ShopGroup[] ExistingSponsorGroups =
    {
        ShopGroup.Branding, ShopGroup.Sessions, ShopGroup.Furniture, ShopGroup.BoothOptions, ShopGroup.PackageHandling,
        ShopGroup.Swag, ShopGroup.Tickets, ShopGroup.Packages, ShopGroup.Booths,
    };

    /// <summary>The order for everyone else — a new or candidate sponsor starts with the booth.</summary>
    public IReadOnlyList<ShopGroup> GroupOrder =>
        Company?.State == CommunityHub.Core.Domain.ShopCompanyState.Sponsor ? ExistingSponsorGroups : SponsorGroups;

    public static readonly ShopGroup[] SponsorGroups =
    {
        ShopGroup.Booths, ShopGroup.Packages, ShopGroup.Sessions, ShopGroup.Branding,
        ShopGroup.Furniture, ShopGroup.BoothOptions, ShopGroup.PackageHandling, ShopGroup.Swag, ShopGroup.Tickets,
    };

    public static string AvailabilityText(ShopAvailability? a)
    {
        if (a is null || !a.Available) return "Sold out";
        var s = a.Unlimited || a.Remaining is null ? "Available" : $"{a.Remaining} left";
        return a.Live ? s : s + " (not confirmed)";
    }
}
