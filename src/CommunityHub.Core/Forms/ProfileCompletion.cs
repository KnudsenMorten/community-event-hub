using System.Linq.Expressions;
using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Forms;

/// <summary>
/// §945 — WHEN IS "Your Profile" DONE, asked in ONE place. Operator 2026-08-07: *"the state of Your
/// Profile (step in get organized) should be counted as completed, if full name, email, phone is set.
/// Phone is mandatory to fill out. if these fields are filled out, then it is completed"*.
/// </summary>
/// <remarks>
/// <para>🔴 <b>§945a — PHONE IS REQUIRED OF VOLUNTEERS ONLY (operator 2026-08-07, correcting §945).</b>
/// <i>"i enforced only for volunteers the phone otherwise disable so it is not mandatory and a shared
/// form"</i>. §945 read <i>"Phone is mandatory"</i> as applying to every role; he meant the volunteer
/// rule (§262) he had already set.</para>
///
/// <para>🔑 <b>§945's actual lesson is KEPT, and it is not the scope — it is that the two rules must
/// MOVE TOGETHER.</b> The dead end §945 fixed was completion testing phone for all roles while
/// validation demanded it only from volunteers: a speaker saved, was told it saved, and watched the
/// step stay incomplete for ever with nothing explaining why. Narrowing completion alone would
/// recreate that in mirror image. So both sides narrow here, off
/// <see cref="PhoneRequiredFor"/> — one predicate, both callers.</para>
///
/// <para>📊 <b>Measured before changing it.</b> 51 real active people had no phone: 20 speakers, 24
/// sponsors, 3 organizers, 3 other — and <b>exactly ONE volunteer</b>. Under §945 all 51 had an open
/// profile step and would have been chased by the get-started digest the day the e-mail rings opened.
/// Under this rule, one person is.</para>
/// </remarks>
/// <remarks>
/// <para>🔴 <b>There were TWO copies of this rule</b> — <c>RoleWizardService</c> (the Get Started
/// progress) and <c>ProfileFormService.IsDoneAsync</c> (the step/task view) each carried their own
/// <c>Phone != null &amp;&amp; Phone != ""</c>. They agreed by coincidence, and a change to one would
/// have produced two answers to "am I finished?" — the §939 failure exactly, where a volunteer who had
/// completed everything was told 90%. The wrong answer is the one that sends somebody looking for work
/// that does not exist. Both now call this.</para>
///
/// <para>🔑 <b>Name and email are not decoration.</b> The old rule tested phone ALONE, on the reasoning
/// that name arrives from the import and phone is the field people actually add. That holds for a
/// synced participant and fails for a PRE-STAGED one (§941), who can exist with an email and nothing
/// else — and would score complete the moment a phone was typed, with no name on file.</para>
///
/// <para>🔒 <b>Trimmed, not just non-null.</b> A single space satisfies <c>!= ""</c> and is not a phone
/// number. <c>Trim()</c> translates to SQL (<c>LTRIM(RTRIM(...))</c>), so the predicate stays a
/// database query rather than becoming a client-side evaluation over every participant.</para>
/// </remarks>
public static class ProfileCompletion
{
    /// <summary>
    /// The rule, as a SQL-translatable predicate — compose it onto an already-scoped query
    /// (<c>.Where(p =&gt; p.Id == id &amp;&amp; p.EventId == eventId).AnyAsync(ProfileCompletion.IsComplete, ct)</c>).
    /// </summary>
    public static readonly Expression<Func<Participant, bool>> IsComplete =
        p => p.FullName != null && p.FullName.Trim() != ""
          && p.Email != null && p.Email.Trim() != ""
          && (p.Role != ParticipantRole.Volunteer
              || (p.Phone != null && p.Phone.Trim() != ""));

    /// <summary>
    /// 🔒 §945a — WHO ACTUALLY HAS TO GIVE A PHONE NUMBER. One place, so validation and completion
    /// cannot drift apart again.
    /// </summary>
    public static bool PhoneRequiredFor(ParticipantRole role) => role == ParticipantRole.Volunteer;

    /// <summary>
    /// The same rule in memory, for an already-loaded row (validation, tests, a projection that
    /// already has the three fields). Kept beside the expression deliberately: two spellings of one
    /// rule is what §945 was cleaning up, so they live in one file and are asserted equivalent.
    /// </summary>
    public static bool IsCompleteFor(string? fullName, string? email, string? phone, ParticipantRole role) =>
        !string.IsNullOrWhiteSpace(fullName)
        && !string.IsNullOrWhiteSpace(email)
        && (!PhoneRequiredFor(role) || !string.IsNullOrWhiteSpace(phone));
}
