using CommunityHub.Core.Email;
using Microsoft.Extensions.Logging;

namespace CommunityHub.OneShot;

/// <summary>
/// The <c>send-sample-emails</c> OneShot command (REQUIREMENTS §7c). Renders
/// EVERY shipped email template (everything under the template directory except
/// the <c>_layout</c> partial) through the REAL <see cref="EmailTemplateProvider"/>
/// /<see cref="EmailTemplateRenderer"/> — the exact renderer production uses, so
/// the operator reviews faithful output — with realistic sample data for the
/// 2LINKIT test sponsor (coordinator <c>mok@2linkit.net</c>). Each subject is
/// prefixed with <c>[SAMPLE]</c> and each email is sent via the configured
/// <see cref="IEmailSender"/> (so the DEV redirect + the PROD/allowlist gate in
/// <see cref="BrevoEmailSender"/> still apply).
///
/// This NEVER runs during build or test — it is only invoked from the CLI:
///   dotnet run --project tools/CommunityHub.OneShot -- send-sample-emails --to mok@2linkit.net
/// Optional <c>--sponsor &lt;name&gt;</c> overrides the sample sponsor company name.
/// </summary>
public static class SendSampleEmailsCommand
{
    /// <summary>Templates that are NOT standalone emails and must be skipped.</summary>
    private static readonly HashSet<string> SkipTemplates =
        new(StringComparer.OrdinalIgnoreCase) { "_layout" };

    public static async Task<int> RunAsync(
        EmailTemplateProvider templates,
        IEmailSender emailSender,
        EmailTemplateOptions templateOptions,
        string toAddress,
        string sponsorName,
        ILogger logger,
        CancellationToken ct = default,
        string? onlyTemplate = null)
    {
        if (string.IsNullOrWhiteSpace(toAddress))
        {
            logger.LogError("send-sample-emails: --to <address> is required.");
            return 1;
        }

        // Use the SAME directory the EmailTemplateProvider reads from, so the file
        // list here matches what Render() will actually load.
        var dir = templateOptions.TemplateDirectory;
        if (!Directory.Exists(dir))
        {
            logger.LogError(
                "send-sample-emails: template directory '{Dir}' not found (run from the repo root so 'templates/emails' resolves, or set EmailTemplates:TemplateDirectory).",
                Path.GetFullPath(dir));
            return 2;
        }

        var files = Directory.GetFiles(dir, "*.html")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name is not null && !SkipTemplates.Contains(name!))
            .Where(name => string.IsNullOrWhiteSpace(onlyTemplate)
                || string.Equals(name, onlyTemplate, StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!string.IsNullOrWhiteSpace(onlyTemplate) && files.Count == 0)
        {
            logger.LogError("send-sample-emails: --only '{Only}' matched no template in {Dir}.", onlyTemplate, dir);
            return 2;
        }

        if (files.Count == 0)
        {
            logger.LogWarning("send-sample-emails: no email templates found in {Dir}.", dir);
            return 0;
        }

        logger.LogInformation(
            "send-sample-emails: rendering {Count} template(s) for sponsor '{Sponsor}' -> {To} (subjects prefixed [SAMPLE]).",
            files.Count, sponsorName, toAddress);

        int sent = 0, failed = 0;
        foreach (var name in files)
        {
            try
            {
                var tokens = SampleTokens(templates, sponsorName);
                var rendered = templates.Render(name!, tokens);
                var subject = $"[SAMPLE] {rendered.Subject}";

                // Send via the configured sender so the allowlist/redirect apply.
                await emailSender.SendAsync(toAddress, subject, rendered.HtmlBody, ct);
                logger.LogInformation("  sent sample '{Template}' — subject: {Subject}", name, subject);
                sent++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "  FAILED to render/send sample '{Template}'.", name);
                failed++;
            }
        }

        logger.LogInformation(
            "send-sample-emails done: {Sent} sent, {Failed} failed. "
            + "(If nothing arrived in DEV, set Email__OnlySendTo to the redirected inbox — "
            + "BrevoEmailSender fails closed on an empty allowlist.)",
            sent, failed);
        return failed > 0 ? 2 : 0;
    }

    /// <summary>
    /// A comprehensive, realistic 2LINKIT sample token set. The branding tokens
    /// come from the provider (brandColor / logoUrl / supportEmail / hubUrl);
    /// every content token any shipped template uses is filled with plausible
    /// data so the rendered email reads like a real one. Tokens a given template
    /// does not reference are simply ignored by the renderer. The <c>*Html</c> /
    /// <c>*Block</c> tokens are intentional raw-HTML fragments (the renderer
    /// inserts them verbatim — see <see cref="EmailTemplateRenderer.RawHtmlTokens"/>).
    /// </summary>
    private static Dictionary<string, string> SampleTokens(
        EmailTemplateProvider templates, string sponsorName)
    {
        var t = templates.NewTokenSet();

        // People + event identity.
        t["firstName"] = "Morten";
        t["contactName"] = "Morten Knudsen";
        t["volunteerName"] = "Morten Knudsen";
        t["communityName"] = "Experts Live Denmark";
        t["eventDisplayName"] = "Experts Live Denmark 2027";
        t["eventCode"] = "ELDK27";
        t["roleName"] = "sponsor contact";
        t["sponsorRole"] = "event coordinator and booth member"; // sample multi-role label
        t["roleGuidance"] =
            "Your sponsor onboarding tasks and deadlines are in the hub. New tasks appear as your order is processed.";
        t["roleLine"] =
            "As a sponsor contact you manage your company's booth, logo and onboarding tasks here.";

        // Sponsor company (the 2LINKIT test sponsor).
        t["sponsorCompany"] = sponsorName;
        t["companyName"] = sponsorName;

        // Tasks + deadlines.
        t["taskTitle"] = "Upload your company logo (vector + raster)";
        t["dueDate"] = "30/06/2027";
        t["state"] = "due today"; // §81: deadline reminders fire only on the due day
        t["taskLink"] = "Open the hub to see and update this task.";
        t["formName"] = "Booth logistics form";
        t["formDeadline"] = "30/06/2027";
        t["stepLabel"] = "Bio";

        // Leads digest.
        t["leadCount"] = "4";

        // App game + media.
        t["giftDescription"] = "A signed copy of our latest cloud-ops handbook";
        t["location"] = "Main stage, by the welcome desk";
        t["slotTime"] = "Day 1, 12:30";

        // Volunteer help-raised.
        t["categoryName"] = "Registration desk";
        t["helpMessage"] = "We need one more pair of hands at the desk during the morning rush.";

        // Session evaluation results (§87) — sessionTitle + the raw resultsHtml
        // blockquote, so the sample doesn't render an empty evaluation.
        t["sessionTitle"] = "Securing Azure at scale";
        t["resultsHtml"] =
            "<strong>Audience feedback:</strong><br>"
            + "😀 Very good: 24<br>🙂 Good: 11<br>😐 Neutral: 3<br>🙁 Poor: 1";

        // §83: session-time-location-changed renders a When/Where table from these
        // tokens — without sample values both cells render BLANK in the preview. Fill
        // them with a realistic before→after move (and the room change) so the sample
        // email shows the actual schedule change.
        t["oldTime"] = "Wed 09 Feb 2027, 10:00–10:50";
        t["newTime"] = "Wed 09 Feb 2027, 13:00–13:50";
        t["oldRoom"] = "Hall A";
        t["newRoom"] = "Hall C";
        t["timeChanged"] = "yes";
        t["roomChanged"] = "yes";

        // Misc.
        t["amount"] = "1.250,00 DKK";
        t["openCount"] = "3";
        t["openCountNoun"] = "questions";
        t["sessionCount"] = "2";
        t["sessionCountNoun"] = "sessions";

        // --- Raw-HTML fragment tokens (inserted verbatim by the renderer) ------
        t["taskListHtml"] =
            "<ul style=\"margin:0;padding-left:18px;\">"
            + "<li>Upload your company logo (vector + raster)</li>"
            + "<li>Submit your booth logistics form</li>"
            + "<li>Confirm your attendee list</li></ul>";
        t["leadListHtml"] =
            "<ul style=\"margin:0;padding-left:18px;\">"
            + "<li>Alex Jensen — interested in the platform demo</li>"
            + "<li>Sam Larsen — follow up on pricing</li></ul>";
        t["messageHtml"] =
            "<p style=\"margin:0 0 16px;\">This is a sample broadcast body so you can review the layout.</p>";
        t["descriptionBlock"] =
            "<p style=\"margin:0 0 16px;\">Please complete this before the deadline shown above.</p>";
        t["notesBlock"] =
            "<p style=\"margin:0 0 16px;\">Reimbursement is based on the travel details you submitted.</p>";
        t["dueText"] = "<strong>due in 3 days</strong>";
        t["masterClassList"] =
            "<ul style=\"margin:0;padding-left:18px;\"><li>Master Class: Securing Azure at scale</li></ul>";
        // §92: the masterclass-* templates (waitlisted/confirmed/cancelled/offer/
        // promoted/month-reminder) render {{masterClassTitle}} — without a sample
        // value the waitlist email reads "You're on the waitlist for ." (blank).
        t["masterClassTitle"] = "Securing Azure at scale";
        t["magicLink"] = "https://example.invalid/Login/Magic?token=SAMPLE";
        t["loginUrl"] = "https://example.invalid/Login/Magic?token=SAMPLE";

        return t;
    }
}
