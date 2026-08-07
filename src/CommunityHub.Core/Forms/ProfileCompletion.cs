using System.Linq.Expressions;
using CommunityHub.Core.Domain;

namespace CommunityHub.Core.Forms;

/// <summary>
/// §945 — WHEN IS "Your Profile" DONE, asked in ONE place. Operator 2026-08-07: *"the state of Your
/// Profile (step in get organized) should be counted as completed, if full name, email, phone is set.
/// Phone is mandatory to fill out. if these fields are filled out, then it is completed"*.
/// </summary>
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
          && p.Phone != null && p.Phone.Trim() != "";

    /// <summary>
    /// The same rule in memory, for an already-loaded row (validation, tests, a projection that
    /// already has the three fields). Kept beside the expression deliberately: two spellings of one
    /// rule is what §945 was cleaning up, so they live in one file and are asserted equivalent.
    /// </summary>
    public static bool IsCompleteFor(string? fullName, string? email, string? phone) =>
        !string.IsNullOrWhiteSpace(fullName)
        && !string.IsNullOrWhiteSpace(email)
        && !string.IsNullOrWhiteSpace(phone);
}
