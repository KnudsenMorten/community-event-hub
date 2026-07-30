using CommunityHub.Core.Domain;
using CommunityHub.Core.Organizer;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §603 / §602.5 — "Mark complete" is retired where an artefact exists (operator 2026-07-28).
///
/// <para>Today a sponsor can press the green <b>Mark complete</b> on the sponsor-wall task with no
/// file uploaded, and the reminder engine believes them. §598 is the mirror image: he deleted the
/// files in SharePoint and CEH still reported them uploaded. Completion has to follow the artefact.</para>
///
/// <para>The classification is deliberately NARROW, and these tests pin that: a false positive
/// HIDES the completion control on a task with no other way to be finished — stranding a sponsor —
/// whereas a false negative just leaves today's behaviour. It must fail toward "manual still
/// allowed".</para>
/// </summary>
public class TaskArtefactRulesTests
{
    private static ParticipantTask Task(string? sourceKey, string title = "T") =>
        new() { SourceKey = sourceKey, Title = title };

    [Theory]
    [InlineData("sponsor:12:upload-sponsor-wall-design-in-vector-format")]
    [InlineData("sponsor:88:sponsor-wall-artwork")]
    [InlineData("sponsor:4:upload-wall-design")]
    public void The_sponsor_wall_task_is_artefact_backed(string key)
    {
        var t = Task(key);
        Assert.True(TaskArtefactRules.IsArtefactBacked(t));
        Assert.Equal("wall", TaskArtefactRules.UploadKindFor(t));
        // …and therefore offers NO self-declared completion.
        Assert.False(TaskArtefactRules.AllowsManualCompletion(t));
    }

    [Theory]
    [InlineData(null)]                                            // no source key at all
    [InlineData("")]
    [InlineData("accept:13")]                                     // Code of Conduct — an acknowledgement
    [InlineData("dinner-form:5")]                                 // a form, not an upload
    [InlineData("availability:13")]
    // §687.8 — "sponsor:12:choose-your-booth-layout" USED to sit here as "a CHOICE, artefact comes
    // later". It has moved to its own test below, on the operator's instruction: "technically a
    // furniture order should auto-close that task". Its own body warns that placing the order is
    // what RESERVES the furniture, so a self-declared tick let a sponsor arrive to an empty booth
    // believing they were done.
    [InlineData("sponsor:12:register-booth-members")]
    [InlineData("speaker:9:submit-session-description")]          // not a sponsor key
    public void Everything_else_keeps_its_manual_completion(string? key)
    {
        var t = Task(key);
        Assert.False(TaskArtefactRules.IsArtefactBacked(t));
        Assert.Null(TaskArtefactRules.UploadKindFor(t));
        // THE SAFE DIRECTION: unknown ⇒ the human can still say "done". Removing the control from a
        // task with no artefact would leave it permanently un-completable.
        Assert.True(TaskArtefactRules.AllowsManualCompletion(t));
    }

    /// <summary>
    /// §687.8 — a task with a REAL dependency offers no self-declared tick.
    /// </summary>
    /// <remarks>
    /// Operator 2026-07-29: <i>"in case there is a real dependency to this, then dont show the mark
    /// completed state and use this validation as the real tasks is completed or not"</i>. This is
    /// §600.3 generalised — and it became buildable only once the render-time webshop resolver
    /// existed, which is why the booth-layout task carried a manual tick until now.
    /// </remarks>
    [Theory]
    [InlineData("sponsor:12:choose-your-booth-layout")]
    [InlineData("sponsor:7:choose-your-booth-layout-table-chairs")]
    public void A_purchase_backed_task_offers_NO_manual_complete(string key)
    {
        var t = Task(key);

        Assert.True(TaskArtefactRules.IsPurchaseBacked(t));
        Assert.False(TaskArtefactRules.AllowsManualCompletion(t));
        // It is not an ARTEFACT task — the evidence is an order, not a file. Keeping the two
        // distinct matters: the upload flow must not appear on a task with nothing to upload.
        Assert.False(TaskArtefactRules.IsArtefactBacked(t));
    }

    [Fact]
    public void Completion_follows_the_uploaded_artefact()
    {
        var wall = Task("sponsor:12:upload-sponsor-wall-design-in-vector-format");

        var nothing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.False(TaskArtefactRules.HasArtefact(wall, nothing));

        // §598: only the RIGHT artefact counts — a logo upload must not complete the wall task.
        var logosOnly = new HashSet<string>(new[] { "some", "print", "zoho" }, StringComparer.OrdinalIgnoreCase);
        Assert.False(TaskArtefactRules.HasArtefact(wall, logosOnly));

        var withWall = new HashSet<string>(new[] { "print", "wall" }, StringComparer.OrdinalIgnoreCase);
        Assert.True(TaskArtefactRules.HasArtefact(wall, withWall));
    }

    [Fact]
    public void A_task_with_no_artefact_never_reports_one_however_much_was_uploaded()
    {
        // Guards the obvious mistake of "this company uploaded something, call the task done".
        var accept = Task("accept:13");
        var everything = new HashSet<string>(
            new[] { "some", "print", "zoho", "wall", "booth" }, StringComparer.OrdinalIgnoreCase);

        Assert.False(TaskArtefactRules.HasArtefact(accept, everything));
    }

    [Fact]
    public void Matching_is_case_insensitive_on_both_the_key_and_the_kind()
    {
        var t = Task("SPONSOR:12:Upload-Sponsor-Wall-Design");
        Assert.Equal("wall", TaskArtefactRules.UploadKindFor(t));
        Assert.True(TaskArtefactRules.HasArtefact(
            t, new HashSet<string>(new[] { "WALL" }, StringComparer.OrdinalIgnoreCase)));
    }
}
