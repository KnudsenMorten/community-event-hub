using System.Linq.Expressions;
using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Sponsors;

/// <summary>
/// §1081 — THE ONE ANSWER to <i>"what company content has this sponsor delivered?"</i>.
/// </summary>
/// <remarks>
/// <para>🔴 <b>Why this type exists.</b> Before it, three surfaces asked that question and gave three
/// different answers: <see cref="Forms.SponsorWizardService"/> accepted <c>WebsiteUrl</c> <b>OR</b>
/// <c>CompanyDescription</c>; <c>SponsorOrderPullService</c> looked only at <c>CompanyDescription</c>;
/// and <c>SoMeApprovalGate</c> looked only at <c>SocialMediaIntro</c>. So a sponsor could be "done" in
/// Get Started, "onboarded" to the task reconciler, and still be the reason a SoMe post could not be
/// approved — with nothing chasing them for the field that was actually blocking it.</para>
///
/// <para>🔑 <b>Operator 2026-08-13:</b> <i>"we need all fields like company description, short
/// description, some branding description to be filled out … fields must contain text at minimum,
/// otherwise it is not used. these fields are important as other service like some post service are
/// dependent on them. so the OR here is wrong."</i> ("some" = <b>SoMe</b>, social media.)</para>
///
/// <para>⚠️ <b>The short description is EXHIBITOR-ONLY, and that is load-bearing.</b>
/// <c>SponsorCompanyFormService</c> writes it inside <c>if (info.HasBooth)</c>, mirroring the page. So
/// requiring it from a non-exhibitor would park them below 100% for ever on a field their own form
/// refuses to save — the §732 defect, rebuilt deliberately. The conditionality lives HERE, once, so no
/// caller can forget it.</para>
///
/// <para>🔒 <b>WebsiteUrl is deliberately NOT part of this.</b> It is one of the three URLs the WEBSHOP
/// owns (<c>ReconcileWithWebshopAsync</c>, §41b, fills it into CEH automatically and never overwrites a
/// non-blank value). Operator: <i>"website url comes from webshop so it is filled out automatically …
/// if field is missing in ceh, then sponsor should fill it into webshop, as that one is authoritative
/// for that field."</i> ⇒ Chasing a sponsor in the hub for it would send them to a form that cannot fix
/// it. The three fields below are exactly the three CEH owns outright.</para>
///
/// <para>🔒 <b>The LOGO is deliberately NOT part of this either</b>, and the reason is subtle: the two
/// sources measure DIFFERENT AXES. <c>SponsorUploadAudits.Kind</c> distinguishes <c>some</c> (web/SoMe)
/// from <c>print</c> — the kinds the wizard's logo step requires both of — while
/// <c>SponsorInfo.Logo{Vector,Raster}Path</c> distinguishes vector from raster, a FILE FORMAT. A vector
/// file is not evidence of a print logo. Folding them together would silently downgrade "both kinds" to
/// "some logo exists". Each caller keeps its own logo rule.</para>
/// </remarks>
public sealed record SponsorCompanyContentStatus(
    bool HasBooth,
    bool HasCompanyDescription,
    bool HasShortDescription,
    bool HasSocialMediaIntro)
{
    /// <summary>Exhibitors only — see the remarks on <see cref="SponsorCompanyContent"/>.</summary>
    public bool ShortDescriptionRequired => HasBooth;

    /// <summary>Required for this company AND not delivered.</summary>
    public bool ShortDescriptionMissing => ShortDescriptionRequired && !HasShortDescription;

    /// <summary>
    /// Every required field carries text. This is the sponsor Get Started "company" step, and the
    /// answer <c>SoMeApprovalGate</c> needs for the text half of its check.
    /// </summary>
    public bool AllDelivered =>
        HasCompanyDescription && HasSocialMediaIntro && !ShortDescriptionMissing;

    /// <summary>
    /// The field keys still missing, in form order — resolved to labels via
    /// <c>SponsorContent.Field.*</c> in SharedResource.resx. 🔑 Empty when
    /// <see cref="AllDelivered"/>. §854's rule: name WHAT is missing, because somebody has to chase
    /// it — "your company details are incomplete" is not chaseable.
    /// </summary>
    public IReadOnlyList<string> MissingFieldKeys
    {
        get
        {
            var missing = new List<string>(3);
            if (!HasCompanyDescription) missing.Add(SponsorCompanyContent.CompanyDescriptionKey);
            if (ShortDescriptionMissing) missing.Add(SponsorCompanyContent.ShortDescriptionKey);
            if (!HasSocialMediaIntro) missing.Add(SponsorCompanyContent.SocialMediaIntroKey);
            return missing;
        }
    }
}

/// <summary>
/// §1081 — builds a <see cref="SponsorCompanyContentStatus"/>, and exposes the SAME rule as an EF
/// expression so a database-side filter cannot drift from the in-memory answer.
/// </summary>
/// <remarks>
/// 🔒 <b>The two forms live in one file on purpose.</b> §867.1 was itself a fix for two halves of one
/// gate disagreeing — the list filter and the reason string asking different questions, so the walk hid
/// a post whose reason said it was fine. Keeping the predicate and its expression side by side is what
/// makes that class of bug visible in review.
/// </remarks>
public static class SponsorCompanyContent
{
    /// <summary>Resx suffix + the wizard/mail key for the long company description.</summary>
    public const string CompanyDescriptionKey = "company-description";

    /// <summary>Resx suffix + the wizard/mail key for the 80-char listing one-liner.</summary>
    public const string ShortDescriptionKey = "short-description";

    /// <summary>Resx suffix + the wizard/mail key for the SoMe branding text.</summary>
    public const string SocialMediaIntroKey = "social-media-intro";

    /// <summary>The resx prefix these keys resolve under.</summary>
    public const string ResourcePrefix = "SponsorContent.Field.";

    /// <summary>
    /// <i>"fields must contain text at minimum"</i> — whitespace-only is not text. The form already
    /// normalises blank input to null (<c>NormaliseOrNull</c>), so this agrees with what is stored.
    /// </summary>
    public static bool HasText(string? value) => !string.IsNullOrWhiteSpace(value);

    /// <summary>
    /// What this company has delivered. A missing <see cref="SponsorInfo"/> row means nothing is on
    /// file — every field reads as not delivered, and <c>HasBooth</c> as false (so the exhibitor-only
    /// short description is not demanded of a company we hold no record for).
    /// </summary>
    public static SponsorCompanyContentStatus StatusOf(SponsorInfo? info) =>
        info is null
            ? new SponsorCompanyContentStatus(false, false, false, false)
            : new SponsorCompanyContentStatus(
                HasBooth: info.HasBooth,
                HasCompanyDescription: HasText(info.CompanyDescription),
                HasShortDescription: HasText(info.CompanyDescriptionShort),
                HasSocialMediaIntro: HasText(info.SocialMediaIntro));

    /// <summary>
    /// The same rule as <see cref="SponsorCompanyContentStatus.AllDelivered"/>, negated, as an EF
    /// expression: <i>"this company still owes us company content"</i>.
    /// </summary>
    /// <remarks>
    /// <para>⚠️ Written as explicit <c>== null || .Trim() == ""</c> rather than
    /// <c>string.IsNullOrWhiteSpace</c> — the shape the existing SoMe gate query already used, so the
    /// generated SQL does not change with this refactor.</para>
    ///
    /// <para>🔴 <b><see cref="SponsorInfo.HasBooth"/> CANNOT APPEAR HERE.</b> It is a DERIVED
    /// property (<c>IsExhibitor || SponsorPackage >= Gold</c>) with no column behind it, so EF cannot
    /// translate it and the query would throw at runtime. The two mapped columns are spelled out
    /// instead — and they must keep saying the same thing as <c>HasBooth</c>, which is what
    /// <c>The_EF_expression_and_the_in_memory_predicate_agree</c> exists to hold.</para>
    /// </remarks>
    public static Expression<Func<SponsorInfo, bool>> IsMissingContent =>
        s => (s.CompanyDescription == null || s.CompanyDescription.Trim() == "")
             || (s.SocialMediaIntro == null || s.SocialMediaIntro.Trim() == "")
             || ((s.IsExhibitor || s.SponsorPackage >= SponsorPackage.Gold)
                 && (s.CompanyDescriptionShort == null || s.CompanyDescriptionShort.Trim() == ""));

    /// <summary>
    /// The SoMe branding text alone, as an EF expression — the ONE field
    /// <see cref="Integrations.SoMeApprovalGate"/> blocks a sponsor post on. Kept separate from
    /// <see cref="IsMissingContent"/> because a post is held up by the text it prints, not by a
    /// listing one-liner it never renders.
    /// </summary>
    public static Expression<Func<SponsorInfo, bool>> IsMissingSocialMediaText =>
        s => s.SocialMediaIntro == null || s.SocialMediaIntro.Trim() == "";
}
