using CommunityHub.Core.Auth;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Participants;

/// <summary>
/// §422 — the ONE definition of "my other email address".
///
/// <para><b>Why this exists.</b> The operator entered an alternate address in the speaker
/// Get Started wizard, then found it nowhere (2026-07-27: <i>"i just added an alternative email
/// … but i dont see it in in my hub profile"</i>, and <i>"it appears as the alternative email
/// didn't pick up here as well, as it still shows primary email"</i>). Nothing had failed to
/// save. The platform had grown THREE separate columns for what a person experiences as one
/// fact, and the three self-service screens each wrote or read a different one:</para>
/// <list type="bullet">
///   <item><description><see cref="SpeakerProfile.CalendarEmail"/> — set by the speaker wizard's
///     "Extra email" step; only ever moved the To-address of calendar invites.</description></item>
///   <item><description><see cref="Participant.AlternateEmail"/> — set by My Hub Profile;
///     a sign-in identity, read by <b>no</b> mail code at all.</description></item>
///   <item><description><see cref="Participant.SecondaryEmail"/> — the actual reminder CC, and
///     the one the tasks banner reads — yet settable ONLY by an organizer.</description></item>
/// </list>
///
/// <para>So the banner offered "add an alternative email →", the link landed on the profile, the
/// only box there wrote a login identity, and the banner still reported one inbox afterwards.
/// Every screen was individually behaving as written; the concept was split three ways.</para>
///
/// <para><b>The rule now:</b> <see cref="Participant.AlternateEmail"/> is the single
/// PARTICIPANT-OWNED alternate address. Whichever self-service screen sets it, all of them show
/// it, and mail is copied there. <see cref="Participant.SecondaryEmail"/> stays as the
/// ORGANIZER-set CC and keeps precedence — organizers may have entered a shared/PA inbox that a
/// participant edit must not silently overwrite — which is why this resolves rather than merges.</para>
/// </summary>
public static class AlternateEmailPolicy
{
    /// <summary>
    /// The address participant mail is CC'd to: the organizer-set
    /// <paramref name="secondaryEmail"/> when present, otherwise the participant's own
    /// <paramref name="alternateEmail"/>. Null when neither is set.
    ///
    /// <para>Resolution, not union: two CC addresses for one person is a duplicate in their
    /// inbox, and the organizer-set value is the more deliberate of the two.</para>
    /// </summary>
    public static string? CcFor(string? secondaryEmail, string? alternateEmail)
    {
        if (!string.IsNullOrWhiteSpace(secondaryEmail)) return secondaryEmail.Trim();
        if (!string.IsNullOrWhiteSpace(alternateEmail)) return alternateEmail.Trim();
        return null;
    }

    /// <summary>The same resolution as <see cref="CcFor(string?,string?)"/>, shaped as the
    /// CC collection the senders take (empty rather than null).</summary>
    public static IReadOnlyCollection<string> CcListFor(string? secondaryEmail, string? alternateEmail)
    {
        var cc = CcFor(secondaryEmail, alternateEmail);
        return cc is null ? Array.Empty<string>() : new[] { cc };
    }

    /// <summary>Blank => null (no alternate); otherwise lower-cased + trimmed, the same
    /// normalisation sign-in uses, so what is stored is what a login lookup will match.</summary>
    public static string? Normalize(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? null : PinLoginService.NormalizeEmail(raw);

    /// <summary>
    /// Validate a proposed alternate address for one participant. Returns the user-facing error,
    /// or null when it is acceptable. <paramref name="normalized"/> must already have been
    /// through <see cref="Normalize"/>; null (blank) is always acceptable — clearing the field is
    /// how you remove the alternate.
    ///
    /// <para>The clash check is what makes it safe for this address to be a sign-in identity: it
    /// cannot be an address that already resolves to somebody else in the edition, so the field
    /// can never be used to reach another person's account.</para>
    /// </summary>
    public static async Task<string?> ValidateAsync(
        CommunityHubDbContext db, int eventId, int participantId, string primaryEmail,
        string? normalized, CancellationToken ct)
    {
        if (normalized is null) return null;

        var at = normalized.IndexOf('@');
        if (normalized.Length > 320 || at <= 0 || at >= normalized.Length - 1 || normalized.Contains(' '))
            return "Please enter a valid alternate email (or leave it blank).";

        if (normalized == PinLoginService.NormalizeEmail(primaryEmail))
            return "Your alternate email can't be the same as your sign-in email.";

        var clash = await db.Participants.AnyAsync(
            x => x.EventId == eventId && x.Id != participantId
                 && (x.Email == normalized || x.AlternateEmail == normalized), ct);

        return clash
            ? "That alternate email is already used by another participant in this event."
            : null;
    }
}
