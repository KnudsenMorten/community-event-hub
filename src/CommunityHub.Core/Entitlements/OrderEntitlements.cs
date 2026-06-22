using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Entitlements;

/// <summary>
/// The SINGLE SOURCE OF TRUTH for which <see cref="OrderItem"/>s a participant
/// is entitled to. A person can wear several hats (their primary
/// <see cref="ParticipantRole"/> plus, if they have a
/// <see cref="SpeakerProfile"/>, a speaker hat); capabilities COMPOSE, so the
/// entitlement set is the UNION across their hats.
///
/// <para>
/// The rules below are DEFAULTS — they are deliberately overridable per person,
/// per item via <see cref="ParticipantOrderOverride"/> (apply with
/// <see cref="Effective"/>). The defaults are pure (no DB / no I/O) so they are
/// trivially testable.
/// </para>
/// </summary>
public static class OrderEntitlements
{
    /// <summary>
    /// The BASE entitlement set for a participant — the union of their speaker
    /// hat (if they have a <paramref name="speaker"/> profile) and their primary
    /// <see cref="ParticipantRole"/> hat — BEFORE any
    /// <see cref="ParticipantOrderOverride"/> is applied.
    ///
    /// <para>Default rules (overridable):</para>
    /// <para>Speaker hat (the person HAS a <paramref name="speaker"/> profile):</para>
    /// <list type="bullet">
    ///   <item><see cref="SpeakerFunding.Supported"/>: Polo, Swag, Award, Hotel,
    ///   TravelReimbursement, AppreciationDinner; + LunchPreDay if
    ///   <see cref="SpeakerProfile.SpeakingPreDay"/>; + LunchMainDay if
    ///   <see cref="SpeakerProfile.SpeakingMainDay"/>.</item>
    ///   <item><see cref="SpeakerFunding.SponsorSelfFunded"/>: AppreciationDinner,
    ///   LunchMainDay only.</item>
    ///   <item><see cref="SpeakerFunding.Organizer"/>: NOTHING from the speaker hat
    ///   (their Organizer role entitlements still apply).</item>
    /// </list>
    /// <para>Primary role hat:</para>
    /// <list type="bullet">
    ///   <item>Organizer: Polo, Swag, AppreciationDinner, LunchPreDay, LunchMainDay.</item>
    ///   <item>Sponsor + <see cref="Participant.IsBoothMember"/>: Polo,
    ///   LunchMainDay (NO appreciation dinner — booth members are not invited;
    ///   one who also speaks gets the dinner via the speaker hat).</item>
    ///   <item>Sponsor (not a booth member): nothing from the sponsor hat.</item>
    ///   <item>Volunteer: Polo, Swag, AppreciationDinner, LunchMainDay.</item>
    ///   <item>Media: Polo, Hotel, AppreciationDinner, LunchPreDay, LunchMainDay.</item>
    ///   <item>EventPartner: Polo, Hotel, AppreciationDinner, LunchPreDay, LunchMainDay.</item>
    ///   <item>Attendee: nothing (their ticket covers food).</item>
    /// </list>
    /// </summary>
    public static IReadOnlySet<OrderItem> Base(Participant p, SpeakerProfile? speaker)
    {
        ArgumentNullException.ThrowIfNull(p);

        var set = new HashSet<OrderItem>();

        // --- Speaker hat (only when a speaker profile is in hand) ------------
        if (speaker is not null)
        {
            switch (speaker.SpeakerFunding)
            {
                case SpeakerFunding.Supported:
                    set.Add(OrderItem.Polo);
                    set.Add(OrderItem.Swag);
                    set.Add(OrderItem.Award);
                    set.Add(OrderItem.Hotel);
                    set.Add(OrderItem.TravelReimbursement);
                    set.Add(OrderItem.AppreciationDinner);
                    if (speaker.SpeakingPreDay) set.Add(OrderItem.LunchPreDay);
                    if (speaker.SpeakingMainDay) set.Add(OrderItem.LunchMainDay);
                    break;

                case SpeakerFunding.SponsorSelfFunded:
                    set.Add(OrderItem.AppreciationDinner);
                    set.Add(OrderItem.LunchMainDay);
                    break;

                case SpeakerFunding.Organizer:
                    // Contributes nothing from the speaker hat; the Organizer
                    // role hat below supplies their entitlements.
                    break;
            }
        }

        // --- Primary role hat ------------------------------------------------
        switch (p.Role)
        {
            case ParticipantRole.Organizer:
                set.Add(OrderItem.Polo);
                set.Add(OrderItem.Swag);
                set.Add(OrderItem.AppreciationDinner);
                set.Add(OrderItem.LunchPreDay);
                set.Add(OrderItem.LunchMainDay);
                break;

            case ParticipantRole.Sponsor:
                if (p.IsBoothMember)
                {
                    set.Add(OrderItem.Polo);
                    set.Add(OrderItem.LunchMainDay);
                    // NOTE: booth members are NOT invited to the appreciation
                    // dinner (operator 2026-06-22). A booth member who ALSO
                    // speaks still gets the dinner via the speaker hat above.
                }
                // A non-booth (digital) sponsor gets nothing from the sponsor
                // hat — their dinner/lunch only ever come via a speaker hat.
                break;

            case ParticipantRole.Volunteer:
                set.Add(OrderItem.Polo);
                set.Add(OrderItem.Swag);
                set.Add(OrderItem.AppreciationDinner);
                set.Add(OrderItem.LunchMainDay);
                break;

            case ParticipantRole.Media:
            case ParticipantRole.EventPartner:
                set.Add(OrderItem.Polo);
                set.Add(OrderItem.Hotel);
                set.Add(OrderItem.AppreciationDinner);
                set.Add(OrderItem.LunchPreDay);
                set.Add(OrderItem.LunchMainDay);
                break;

            case ParticipantRole.Speaker:
                // The speaker entitlements are supplied entirely by the speaker
                // hat above; the bare Speaker role adds nothing on its own.
                break;

            case ParticipantRole.Attendee:
                // Nothing — their ticket covers food.
                break;
        }

        return set;
    }

    /// <summary>
    /// The EFFECTIVE entitlement set: <see cref="Base"/> with every applicable
    /// <see cref="ParticipantOrderOverride"/> applied — an
    /// <see cref="ParticipantOrderOverride.Include"/>=true override ADDS its item,
    /// an Include=false override REMOVES it. Overrides for other participants are
    /// ignored, so a caller may pass the whole edition's override list.
    /// </summary>
    public static IReadOnlySet<OrderItem> Effective(
        Participant p,
        SpeakerProfile? speaker,
        IEnumerable<ParticipantOrderOverride> overrides)
    {
        ArgumentNullException.ThrowIfNull(p);

        var set = new HashSet<OrderItem>(Base(p, speaker));

        if (overrides is not null)
        {
            foreach (var o in overrides)
            {
                if (o.ParticipantId != p.Id) continue;
                if (o.Include) set.Add(o.Item);
                else set.Remove(o.Item);
            }
        }

        return set;
    }
}
