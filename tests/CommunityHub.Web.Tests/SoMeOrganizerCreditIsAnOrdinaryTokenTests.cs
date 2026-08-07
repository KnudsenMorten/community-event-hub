using Xunit;

namespace CommunityHub.Web.Tests;

/// <summary>
/// §932 — THE ORGANIZER CREDIT IS AN ORDINARY TOKEN. THE EDITOR MUST NOT TOUCH IT.
/// </summary>
/// <remarks>
/// <para>🔴 <b>Post 6345 published to the company page with no organizer credit</b> at 07:00 on
/// 2026-08-07, and post 496 was approved and due at 11:00 the same morning. The operator sent the
/// preview he had approved: the credit was on it, fully mention-resolved.</para>
///
/// <para><b>The mechanism.</b> The editor stripped the credit on load, stripped it again on SAVE,
/// and re-appended it for display. That was coherent under §861, when the PUBLISHER stapled it on
/// afterwards. §888.3 reversed that on 2026-08-06 07:59 — <i>"make it as a variable like others"</i>
/// — and moved the token into the body. The data was migrated; the editor was not. From that moment
/// every save deleted the credit from the stored body while the preview went on drawing it, so
/// <b>what he approved and what published were different documents</b>.</para>
///
/// <para>🔑 Operator 2026-08-07: <i>"we have 10+ variables and each of them must be handled the same
/// way"</i> and <i>"no difference between {organizers} and {speakers}"</i>. The one thing that looked
/// like a reason to special-case it — that the credit @-mentions people — is the SAME pipeline
/// <c>{Speakers}</c> uses.</para>
///
/// <para>⚠️ <b>822 Web tests passed both before and after the fix.</b> Nothing covered strip-on-save,
/// which is precisely why this reached LinkedIn. These are the tests that were owed.</para>
/// </remarks>
public sealed class SoMeOrganizerCreditIsAnOrdinaryTokenTests
{
    private static string EditorCode() =>
        ReadSource(Path.Combine("Pages", "Organizer", "SoMePostEditor.cshtml.cs"));

    /// <summary>
    /// 🔴 THE REGRESSION ITSELF: the editor must never strip the credit out of the body it stores.
    /// </summary>
    [Fact]
    public void The_editor_never_strips_the_organizer_credit_from_the_stored_body()
    {
        var code = EditorCode();

        Assert.DoesNotContain("SoMePostCredit.TryStrip", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// 🔒 …and never ADDS it either. A preview that appends is a preview of a different document.
    /// </summary>
    /// <remarks>
    /// This is the half that made the defect invisible: the page promises "Preview — exactly what
    /// publishes", and it was drawing a credit the stored body did not contain.
    /// </remarks>
    [Fact]
    public void The_editor_never_appends_a_credit_block_for_display()
    {
        var code = EditorCode();

        Assert.DoesNotContain("Organizers:", code, StringComparison.Ordinal);
        Assert.DoesNotContain("SoMePostCredit.Suffix", code, StringComparison.Ordinal);
        Assert.DoesNotContain("SoMePostCredit.Compose", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// 🔑 The credit gets no bespoke UI of its own — his "10+ variables, handled the same way".
    /// </summary>
    /// <remarks>
    /// A missing credit is reported by the approval gate, in the same place and the same words as
    /// every other missing value (§850), not by a notice hand-written for one token.
    /// </remarks>
    [Fact]
    public void The_credit_has_no_special_case_properties_left()
    {
        var code = EditorCode();

        Assert.DoesNotContain("BodyPlacesCredit", code, StringComparison.Ordinal);
        Assert.DoesNotContain("CreditPreview", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// 🔒 The preview renders the SAME text the publisher will, so the two cannot drift apart again.
    /// </summary>
    [Fact]
    public void The_preview_is_built_from_the_edited_body_itself()
    {
        var code = EditorCode();

        Assert.Contains("Segments(EditText, values)", code, StringComparison.Ordinal);
    }

    private static string ReadSource(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "CommunityHub", relative);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"Could not find {relative} walking up from the test binaries.");
    }
}
