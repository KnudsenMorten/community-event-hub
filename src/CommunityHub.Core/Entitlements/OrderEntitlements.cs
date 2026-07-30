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
    /// <para>Speaker hat (the person HAS a <paramref name="speaker"/> profile),
    /// by <see cref="SpeakerProfile.Category"/> (§299 6.2) with the DERIVED
    /// presenting days (<paramref name="days"/>, from
    /// <see cref="SpeakerDayScope"/> — the stored flags are retired, C5):</para>
    /// <list type="bullet">
    ///   <item><see cref="SpeakerCategory.Community"/>: Polo, Swag, Award, Hotel,
    ///   TravelReimbursement, AppreciationDinner, LunchPreDay (§294 — every
    ///   speaker may join the pre-day); + LunchMainDay when they present on the
    ///   main day (<see cref="SpeakerDays.PresentsMainDay"/>).</item>
    ///   <item><see cref="SpeakerCategory.Guest"/>: IDENTICAL to Community except
    ///   NO TravelReimbursement (§299 6.2 — the travel option/tasks are never
    ///   presented to a Guest; their travel runs on individual terms).</item>
    ///   <item><see cref="SpeakerCategory.Sponsor"/>: AppreciationDinner,
    ///   LunchPreDay, LunchMainDay only (sponsor-paid otherwise).</item>
    ///   <item>null (NOT YET CATEGORIZED): NOTHING from the speaker hat — an
    ///   uncategorized speaker is excluded from every count (§299 6.1; they also
    ///   cannot be activated until categorized).</item>
    /// </list>
    /// <para>Primary role hat:</para>
    /// <list type="bullet">
    ///   <item>Organizer: Polo, Swag, Hotel, AppreciationDinner, LunchPreDay, LunchMainDay.</item>
    ///   <item>Sponsor + <see cref="Participant.IsBoothMember"/>: Polo,
    ///   LunchMainDay (NO appreciation dinner — booth members are not invited;
    ///   one who also speaks gets the dinner via the speaker hat).</item>
    ///   <item>Sponsor (not a booth member): nothing from the sponsor hat.</item>
    ///   <item>Volunteer: Polo, Swag, Hotel, AppreciationDinner, LunchMainDay.</item>
    ///   <item>Media: Polo, Hotel, AppreciationDinner, LunchPreDay, LunchMainDay.</item>
    ///   <item>EventPartner: Polo, Hotel, AppreciationDinner, LunchPreDay, LunchMainDay.</item>
    ///   <item>Attendee: nothing (their ticket covers food).</item>
    /// </list>
    /// </summary>
    /// <param name="p">The participant (role hat).</param>
    /// <param name="speaker">Their speaker profile, or null when they have no speaker hat.</param>
    /// <param name="days">
    /// The speaker's DERIVED presenting days (<see cref="SpeakerDayScope.DaysBySpeakerAsync"/> /
    /// <see cref="SpeakerDayScope.DaysForSpeakerAsync"/>); null when there is no speaker hat (or
    /// sessions are irrelevant to the caller — treated as <see cref="SpeakerDays.None"/>).
    /// </param>
    public static IReadOnlySet<OrderItem> Base(Participant p, SpeakerProfile? speaker, SpeakerDays? days)
    {
        ArgumentNullException.ThrowIfNull(p);

        var set = new HashSet<OrderItem>();

        // --- Speaker hat (only when a speaker profile is in hand) ------------
        if (speaker is not null)
        {
            switch (speaker.Category)
            {
                case SpeakerCategory.Community:
                case SpeakerCategory.Guest:
                    set.Add(OrderItem.Polo);
                    set.Add(OrderItem.Swag);
                    set.Add(OrderItem.Award);
                    set.Add(OrderItem.Hotel);
                    // §299 6.2: a Guest is Community MINUS the travel-reimbursement
                    // option — ELDK hires them on individual terms, so the travel
                    // form/task must never be presented to them.
                    if (speaker.Category == SpeakerCategory.Community)
                        set.Add(OrderItem.TravelReimbursement);
                    set.Add(OrderItem.AppreciationDinner);
                    // §294 (operator 2026-07-11: "any speaker can participate in preday"):
                    // pre-day lunch is open to EVERY speaker, regardless of which days
                    // they present. Main-day lunch follows the DERIVED schedule (C5).
                    set.Add(OrderItem.LunchPreDay);
                    if (days?.PresentsMainDay == true) set.Add(OrderItem.LunchMainDay);
                    break;

                case SpeakerCategory.Sponsor:
                    set.Add(OrderItem.AppreciationDinner);
                    set.Add(OrderItem.LunchMainDay);
                    set.Add(OrderItem.LunchPreDay);   // §294: any speaker can participate pre-day
                    break;

                case null:
                    // §299 6.1: an UNCATEGORIZED speaker contributes NOTHING from
                    // the speaker hat — they are excluded from every count until an
                    // organizer sets the category (which also gates activation).
                    break;
            }
        }

        // --- Primary role hat ------------------------------------------------
        switch (p.Role)
        {
            case ParticipantRole.Organizer:
                set.Add(OrderItem.Polo);
                set.Add(OrderItem.Swag);
                set.Add(OrderItem.Hotel);
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
                set.Add(OrderItem.Hotel);
                set.Add(OrderItem.AppreciationDinner);
                set.Add(OrderItem.LunchMainDay);
                break;

            // §299 7.2/7.3: Media and EventPartner are DELIBERATELY two separate blocks even
            // though the sets are identical today — they are different populations whose
            // entitlements may diverge independently. A change to one must NEVER be
            // auto-applied to the other; tests assert each role on its own.
            case ParticipantRole.Media:
                set.Add(OrderItem.Polo);
                set.Add(OrderItem.Swag);   // §299 OPEN-27 (operator 2026-07-23): polo + swag, same as volunteers
                set.Add(OrderItem.Hotel);
                set.Add(OrderItem.AppreciationDinner);
                set.Add(OrderItem.LunchPreDay);
                set.Add(OrderItem.LunchMainDay);
                break;

            case ParticipantRole.EventPartner:
                set.Add(OrderItem.Polo);
                set.Add(OrderItem.Swag);   // §299 OPEN-27: polo + swag, same as volunteers
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
    /// <param name="p">The participant (role hat).</param>
    /// <param name="speaker">Their speaker profile, or null when they have no speaker hat.</param>
    /// <param name="days">The speaker's derived presenting days — see <see cref="Base"/>.</param>
    /// <param name="overrides">The per-person per-item overrides (other people's rows are ignored).</param>
    public static IReadOnlySet<OrderItem> Effective(
        Participant p,
        SpeakerProfile? speaker,
        SpeakerDays? days,
        IEnumerable<ParticipantOrderOverride> overrides)
    {
        ArgumentNullException.ThrowIfNull(p);

        var set = new HashSet<OrderItem>(Base(p, speaker, days));

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
