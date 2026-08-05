using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Email;

/// <summary>
/// Maps a participant's PRIMARY <see cref="ParticipantRole"/> to the per-role
/// welcome content-template key (e.g. <c>welcome-speaker</c>). Both the welcome
/// EMAIL (<see cref="Reminders.WelcomeWithLoginEmailService"/>) and the
/// first-login PORTAL welcome view render the variant this router selects, so a
/// recipient sees one consistent, role-tailored welcome in both places.
///
/// <para>Every key has a generic, publish-safe default under
/// <c>templates/emails/</c>; an edition's exact copy lives only in the
/// private <c>config/email-templates/</c> layer (which wins via
/// <see cref="EmailTemplateProvider"/> resolution).</para>
/// </summary>
public static class WelcomeVariants
{
    /// <summary>
    /// The per-role welcome template key for <paramref name="role"/>, or
    /// <c>null</c> for roles that get NO welcome. Operator decision 2026-06-22:
    /// ORGANIZERS get no welcome, and ATTENDEES are not sent a platform welcome
    /// (they are covered by the Master Class confirmed-seat mail) — both return
    /// null so neither the welcome email nor the first-login portal welcome fires.
    /// </summary>
    public static string? TemplateKeyFor(ParticipantRole role) => role switch
    {
        ParticipantRole.Speaker      => "welcome-speaker",
        ParticipantRole.Volunteer    => "welcome-volunteer",
        ParticipantRole.Sponsor      => "welcome-sponsor",
        ParticipantRole.Media        => "welcome-media",
        ParticipantRole.EventPartner => "welcome-eventpartner",
        // Organizer + Attendee (and any future role) get no welcome.
        _                            => null,
    };

    /// <summary>
    /// §726 — the welcome key for a SPEAKER, split by <see cref="SpeakerCategory"/>.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-07-31: *"the welcome mail for speakers must be extended to 3 types:
    /// welcome-speaker-community, welcome-speaker-guest and welcome-speaker-sponsor - the header is
    /// different"*, then asked *"how can we implement this 2 level of ring-gating?"*.</para>
    ///
    /// <para>🔑 <b>There is no second level.</b> A ring is per (MAIL × ROLE), so three KEYS give
    /// three independent rings for free — the §707.11 precedent exactly (task-deadline-reminder was
    /// split into the party and Master Class chasers because *"one key meant one ring and one
    /// cadence for all three, so control it per mail was impossible"*). Each key gets its own
    /// Settings row and its own ring; nothing new is needed in the gate.</para>
    ///
    /// <para>🔒 <b>These three keys have NO template file of their own</b> — they all render
    /// <c>welcome-speaker.html</c>, and only the opening clause differs (see
    /// <see cref="SpeakerIntroHtml"/>). Three copies of a long body is how the two drift (§660/§719,
    /// hit twice in one day). The transport picks the ring from
    /// <c>EmailContext.TemplateName</c>, not from which file was rendered, which is what makes one
    /// body with three identities work.</para>
    ///
    /// <para>A speaker with NO category falls back to plain <c>welcome-speaker</c>. In practice a
    /// new speaker sits in the pending queue until the organizer sets ring + category and approves
    /// (operator: *"they will newer get the welcome mail before that is done anyway"*), so this is a
    /// safety net rather than a live path — and it fails to the mail that already exists rather than
    /// inventing a category for someone.</para>
    /// </remarks>
    public static string? TemplateKeyFor(ParticipantRole role, SpeakerCategory? category)
    {
        if (role != ParticipantRole.Speaker) return TemplateKeyFor(role);

        return category switch
        {
            SpeakerCategory.Community => "welcome-speaker-community",
            SpeakerCategory.Guest     => "welcome-speaker-guest",
            SpeakerCategory.Sponsor   => "welcome-speaker-sponsor",
            _                         => "welcome-speaker",   // category not set yet
        };
    }

    /// <summary>
    /// §726 — the opening clause of the speaker welcome, which is the ONLY part that differs by
    /// category. Raw HTML by design: the name is <c>…Html</c>, which
    /// <c>EmailTemplateRenderer.RawHtmlTokens</c> treats as a sender-built fragment.
    /// </summary>
    /// <remarks>
    /// ⚠️ The event name is interpolated HERE, not left as a nested <c>{{eventDisplayName}}</c>:
    /// the renderer substitutes in ONE pass, so a token inside a token value would reach the reader
    /// as literal braces. The DATE and VENUE deliberately stay in the per-edition template file —
    /// putting "9–10 February 2027 at Bella Center" in Core would break the evergreen rule
    /// (CLAUDE.md: code is CommunityHub, never eldk27).
    /// </remarks>
    public static string SpeakerIntroHtml(SpeakerCategory? category, string eventName)
    {
        var ev = $"<strong>{System.Net.WebUtility.HtmlEncode(eventName)}</strong>";

        // Community speakers were SELECTED — the congratulation is the point, and it would read as
        // a mistake to a sponsor-brought or hired guest speaker, who was not selected at all.
        return category == SpeakerCategory.Community
            ? $"Once again, congratulations on being selected to speak at {ev} &mdash; we&#8217;re "
              + "thrilled to have you at the conference!"
            : $"We&#8217;re thrilled to have you at the {ev} conference!";
    }

    /// <summary>
    /// The recipient's sponsor role label for the welcome's "you are receiving this
    /// in your role as …" line — built from the participant's actual sponsor flags
    /// (event coordinator / signer / booth member), so it is never a static value.
    /// Multiple roles are joined naturally ("event coordinator and signer",
    /// "event coordinator, signer, and booth member"). When no flag is set it falls
    /// back to the generic "sponsor contact".
    /// </summary>
    public static string SponsorRoleLabel(
        bool isEventCoordinator, bool isSigner, bool isBoothMember)
    {
        var roles = new List<string>(3);
        if (isEventCoordinator) roles.Add("event coordinator");
        if (isSigner) roles.Add("signer");
        if (isBoothMember) roles.Add("booth member");

        return roles.Count switch
        {
            0 => "sponsor contact",
            1 => roles[0],
            2 => $"{roles[0]} and {roles[1]}",
            _ => $"{string.Join(", ", roles.Take(roles.Count - 1))}, and {roles[^1]}",
        };
    }

    /// <summary>The full set of welcome template keys (one per welcomed role) — for catalog/registration.</summary>
    public static readonly IReadOnlyList<string> AllTemplateKeys = new[]
    {
        "welcome-speaker",
        // §726 — the three category variants. File-less: they render welcome-speaker.html.
        "welcome-speaker-community",
        "welcome-speaker-guest",
        "welcome-speaker-sponsor",
        "welcome-volunteer",
        "welcome-sponsor",
        "welcome-media",
        "welcome-eventpartner",
    };

    /// <summary>
    /// §726 — the three category variants have no file; this is the body they render.
    /// </summary>
    public static string TemplateFileKeyFor(string mailKey) =>
        mailKey.StartsWith("welcome-speaker", StringComparison.Ordinal) ? "welcome-speaker" : mailKey;
}
