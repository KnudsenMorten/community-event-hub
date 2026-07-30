using CommunityHub.Core.Settings;

namespace CommunityHub.Core.Domain;

/// <summary>
/// §515 — a release ring for ONE email template, per edition.
///
/// <para><b>Why this exists.</b> A ring used to live only on the FEATURE key, and template→feature
/// is many-to-one: <c>welcome-speaker</c>, <c>welcome-sponsor</c>, <c>welcome-volunteer</c>,
/// <c>welcome-media</c>, <c>welcome-eventpartner</c> and the entire Master Class funnel all map to
/// the single key <c>welcome-email</c>. One ring therefore governed every persona's welcome at
/// once. Operator 2026-07-28, going live with speakers first: <i>"i would like to configure
/// indiidual ring per mail, like ring 2 for speakers welome and ring 1 for sponsors"</i>.</para>
///
/// <para><b>It REPLACES the feature ring rather than tightening it.</b> Every other clamp in the
/// ring model only narrows (§514), but that rule cannot express what he needs: under a Ring-1
/// feature, a template set to Ring 2 would clamp straight back to Ring 1 and change nothing. So
/// the resolution is <c>MIN(transport, templateRing ?? featureRing)</c> — the template ring stands
/// in for the feature ring, and the outbound-email master remains the single ceiling above it.</para>
///
/// <para>🔒 <b>§705.2 UPDATE — the feature fallback is GONE.</b> Resolution is now
/// <c>(template, recipient role) ?? (template, all-roles) ?? Ring0</c>. A registered mail with no row
/// at all FAILS CLOSED and reaches nobody, because the old fallback for an unresolvable key was
/// <b>Broad</b> — a mail that slipped through unregistered would have gone to everyone.</para>
/// </summary>
public class EmailTemplateRing
{
    public int Id { get; set; }

    /// <summary>The edition this override belongs to.</summary>
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    /// <summary>
    /// The template key — the on-disk file name without ".html" (e.g. <c>welcome-speaker</c>),
    /// matching a <c>EmailTemplateCatalog.Map</c> entry. A stale key is simply ignored on read.
    /// </summary>
    public string TemplateKey { get; set; } = string.Empty;

    /// <summary>
    /// §705.3b — the RECIPIENT ROLE this ring applies to, or <c>null</c> for "every role".
    /// </summary>
    /// <remarks>
    /// 🔑 <b>The unit of a ring is (mail type × role), not the template.</b> Operator 2026-07-29:
    /// <i>"1 mail type to a role = 1 ring gate. that is it. so reminder to speaker is NOT the same as
    /// reminder to organizer or reminder to sponsor."</i> One template genuinely serves several roles —
    /// <c>task-deadline-reminder</c> reaches all seven — so a single ring per template could never mean
    /// "hold the sponsors but release the speakers", which is the control he kept asking for.
    ///
    /// <para><b>Nullable ON PURPOSE, and that is what makes this migration safe.</b> <c>null</c> = the
    /// ring for every role, which is exactly what the 32 existing rows already meant. So the column can
    /// be added with **zero behaviour change**: every current row keeps governing every role, and a
    /// per-role ring is opt-in — added only where he wants two roles to differ.</para>
    ///
    /// <para><b>Specific beats general.</b> A <c>(template, Speaker)</c> row wins over
    /// <c>(template, null)</c> for a speaker recipient, and leaves every other role on the general row.
    /// That is the §515 trap solved properly: setting one role cannot move the others.</para>
    /// </remarks>
    public ParticipantRole? Role { get; set; }

    /// <summary>The ring THIS mail is released to for <see cref="Role"/> (or for all roles when null).</summary>
    public Ring ReleasedToRing { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Who last changed it — this decides who receives real mail, so it is audited.</summary>
    public string? UpdatedByEmail { get; set; }
}
