using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §1041 — EVERY CLIENT THAT WRITES TO A THIRD PARTY MUST CONSULT THE WRITE GUARD.
/// </summary>
/// <remarks>
/// <para>Operator 2026-08-10, on a DEV run that updated 53 records on the LIVE webshop:
/// <i>"this is a service that runs in dev that sync from erp to webshop = write permissions. it is
/// not allowed to do that"</i> → <i>"do we have others doing similar"</i>. There were: two more.</para>
///
/// <para>🔴 <b>Why the existing test could not have caught it.</b> `ExternalWritePerSystemPolicyTests`
/// greps for system names PASSED TO the guard and checks they are known. `CompanyManagerClient` never
/// called the guard at all, so it contributed no name and the test was perfectly green while the
/// webshop sat wide open. <b>A test of the present cases cannot see the absent one.</b></para>
///
/// <para>🔑 So this test inverts the question: it starts from the code that makes outbound HTTP
/// WRITES and asserts each such file consults the guard — a list nobody has to remember to update,
/// because it is derived from the writes themselves.</para>
/// </remarks>
public sealed class ExternalWriteCoverageTests
{
    /// <summary>
    /// Files that issue outbound writes but legitimately do not gate them, each with the reason.
    /// ⚠️ Adding to this list is a DECISION — it should be as hard to do quietly as it is visible.
    ///
    /// <para>🔑 §1044 — <b>A REASON MUST SAY WHETHER THE EXEMPTION IS FINAL OR PROVISIONAL.</b> The
    /// reason text is the ONLY thing a future session sees. One entry here used to read "exempt for
    /// now, but it should still route through the guard" — that is not a decision, it is a to-do
    /// with a rationale attached, and a session picking up the handover duly actioned it on the eve
    /// of ticket sale for a change that altered no behaviour in either environment. Mark a final
    /// exemption 🛑 FINAL; a provisional one must say what would make it final.</para>
    /// </summary>
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GraphSharePointFileStore.cs"] =
            "SharePoint file store — DEV writes to its OWN folder path, exempted deliberately and "
            + "documented in the class; §1037 made the intent explicit as ExternalWrites:SharePoint.",
        ["ISharePointFileStore.cs"] =
            "Interface only — the implementations carry the gate.",
        ["SharePointUploadClient.cs"] =
            "🛑 FINAL (§1044). SharePoint uploads — same separate-path reasoning as the file store "
            + "(§1037: DEV may write to SharePoint). Every host is PERMITTED this write, so there is "
            + "nothing for a ceiling to express. Do not gate it.",
        ["SessionEvalsQrService.cs"] =
            "Publishes QR codes into the SharePoint doc library via the gated store.",
        ["LinkedInTokenStore.cs"] =
            "OAuth token exchange with LinkedIn's auth endpoint — obtaining a credential, not "
            + "publishing content. The content path is LiveLinkedInPostPublisher, which IS gated.",
        ["EmailTemplateOverrideStore.cs"] =
            "Local persistence, not a third-party call.",
        ["EconomicContactAdminService.cs"] =
            "Delegates to LiveEconomicContactAdminClient; the client is where the gate belongs.",
        ["LiveEconomicContactAdminClient.cs"] =
            "🛑 FINAL (§1044) — operator 2026-08-10: \"dev is allowed to update contacts in erp, no "
            + "guards for that\". DEV is PERMITTED this write, so there is no environment in which "
            + "we want it stopped and nothing for a ceiling to express. DO NOT GATE IT. ⚠️ This "
            + "entry used to end \"...but it should still route through the guard so the policy is "
            + "enforceable rather than assumed\" — a to-do with a reason attached, which a later "
            + "session duly actioned on the eve of ticket sale for zero behaviour change.",

        // ── LLM inference: outbound, but nothing is MUTATED at the far end ──────────────────────
        // 🔑 A different category, and worth stating rather than lumping in. These POST a prompt to
        // Azure OpenAI and read a completion back. Nothing persists there, no shared business record
        // changes, and DEV needs them working — the SoMe teaser generation runs in the jobs host and
        // is part of "test it like real life". Gating them would break DEV testing to protect
        // nothing. ⚠️ They DO send hub content to a third party, so if that ever becomes the concern
        // it is a data-processing question, not an external-WRITE one.
        ["AiHelperAssistant.cs"] = "Azure OpenAI inference — sends a prompt, mutates nothing.",
        ["LlmTaskGuidanceGenerator.cs"] = "Azure OpenAI inference — sends a prompt, mutates nothing.",
        ["SoMeIntroGenerator.cs"] = "Azure OpenAI inference — sends a prompt, mutates nothing.",
        ["SoMeTextEligibilityJudge.cs"] =
            "Azure OpenAI inference (§1060(l)) — sends a session title + abstract and reads back a "
            + "0/1 verdict. Mutates nothing outside CEH, exactly as SoMeIntroGenerator above; the "
            + "guard governs WRITES, and its own doc states reads are never gated. 🛑 FINAL. "
            + "⚠️ Note what it sends is session copy the event intends to PUBLISH, not personal data "
            + "— if that ever changes, this exemption is void and the call needs its own decision.",

        ["JobTriggerService.cs"] =
            "Calls the hub's OWN job endpoints (an organizer pressing 'run now'). The work each job "
            + "then does is gated where it reaches a third party, which is the right place — gating "
            + "the trigger would block DEV from exercising its own jobs.",
    };

    [Fact]
    public void Every_outbound_write_client_consults_the_guard()
    {
        var root = FindRepoRoot();
        var files = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"))
            .ToList();

        // A real outbound WRITE: an HttpRequestMessage with a mutating verb, or the HttpClient
        // convenience methods. Deliberately NOT a bare "PostAsync" grep — that matches domain
        // methods like CreateSpeakerPostAsync and produced three false positives in the manual audit.
        var writeCall = new Regex(
            @"HttpMethod\.(Post|Put|Patch|Delete)\b|_http\.(PostAsync|PutAsync|PatchAsync|DeleteAsync)\(",
            RegexOptions.Compiled);

        var unguarded = new List<string>();

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            if (!writeCall.IsMatch(text)) continue;

            var name = Path.GetFileName(file);
            if (Exempt.ContainsKey(name)) continue;

            var consultsGuard = text.Contains("IExternalWriteGuard", StringComparison.Ordinal)
                                || text.Contains("AllowAsync(", StringComparison.Ordinal);
            if (!consultsGuard) unguarded.Add(name);
        }

        Assert.True(unguarded.Count == 0,
            "These files make outbound HTTP WRITES but never consult IExternalWriteGuard, so no "
            + "environment policy can stop them — this is how DEV wrote 53 records to the live "
            + "webshop (§1041).\n\nGate them, or add them to ExternalWriteCoverageTests.Exempt WITH "
            + "the reason:\n  " + string.Join("\n  ", unguarded.OrderBy(x => x)));
    }

    /// <summary>
    /// 🔒 The exemption list must not outlive the files it excuses — a stale entry silently exempts
    /// whatever later takes that filename. Same rule as IntroCurrencyTests' excuse list.
    /// </summary>
    [Fact]
    public void No_exemption_names_a_file_that_no_longer_exists()
    {
        var root = FindRepoRoot();
        var present = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var stale = Exempt.Keys.Where(k => !present.Contains(k)).ToList();

        Assert.True(stale.Count == 0,
            "Exemptions for files that no longer exist: " + string.Join(", ", stale));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
