using System.Reflection;
using CommunityHub.Core.Diagnostics;
using CommunityHub.Core.Settings;
using Microsoft.Azure.Functions.Worker;
using Xunit;

namespace CommunityHub.Core.Tests;

/// <summary>
/// §545(c) — <i>"Coverage must be a LIST, not an accident. Enumerate every job and every integration
/// and assert each has an alert path — the same mechanical guarantee `JobCatalogCompletenessTests`
/// gives the Jobs page, so a new integration cannot ship unmonitored."</i>
/// </summary>
/// <remarks>
/// <para><b>Why a hand-written list would be worse than nothing.</b> §327's catalog is checked by
/// reflection precisely because a list nobody verifies drifts and then quietly lies. Every alerting
/// gap in this session was of that shape: something looked covered and was not.</para>
///
/// <para>So these tests reflect over the REAL <c>CommunityHub.Jobs</c> assembly and fail the build
/// when coverage regresses — in either direction. The declared lists below are not documentation;
/// they are the assertion.</para>
/// </remarks>
public sealed class AlertCoverageTests
{
    /// <summary>Every deployed timer function, by name.</summary>
    private static IReadOnlyList<string> RealTimerJobs() =>
        typeof(CommunityHub.Jobs.ReminderJob).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Where(m => m.GetCustomAttribute<FunctionAttribute>() is not null
                        && m.GetParameters().Any(p => p.GetCustomAttribute<TimerTriggerAttribute>() is not null))
            .Select(m => m.GetCustomAttribute<FunctionAttribute>()!.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Job classes that take a <see cref="JobActivityReporter"/>, i.e. can report INACTIVE (§627).
    /// Found by reflection, never by hand.
    /// </summary>
    private static IReadOnlySet<string> InstrumentedJobs() =>
        typeof(CommunityHub.Jobs.ReminderJob).Assembly.GetTypes()
            .Where(t => t.GetConstructors()
                .Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(JobActivityReporter))))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Select(m => m.GetCustomAttribute<FunctionAttribute>()?.Name)
                .Where(n => n is not null)!)
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);

    // -------------------------------------------------------------------------
    // FAILURE coverage — universal by construction, and it must stay that way.
    // -------------------------------------------------------------------------

    /// <summary>
    /// 🔒 `EngineErrorAlertMiddleware` wraps EVERY function, so failure alerting is universal by
    /// construction rather than per-job — the reason §138 could be fixed once instead of 26 times.
    /// This pins the property that makes that true: nothing may be excluded from the middleware.
    /// </summary>
    [Fact]
    public void The_failure_alerter_wraps_every_function_with_no_exclusion_list()
    {
        var mw = typeof(CommunityHub.Jobs.EngineErrorAlertMiddleware);
        var source = mw.GetMethods()
            .Where(m => m.Name == "Invoke")
            .ToList();

        Assert.Single(source);
        // One entry point, no per-function switch: if someone adds an opt-out, the shape below
        // changes and this test is where they will have to justify it.
        Assert.Equal(2, source[0].GetParameters().Length);
    }

    // -------------------------------------------------------------------------
    // SILENT coverage (§624)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Jobs whose cadence is too rare for the silence detector to judge. Every entry needs a REASON
    /// here, because an unlisted rare job is indistinguishable from a job that silently stopped.
    /// </summary>
    private static readonly Dictionary<string, string> RareCadenceByDesign = new(StringComparer.Ordinal)
    {
        ["SessionPushPilotJob"] = "Annual cron '0 0 0 1 1 *' — a deliberately inert pilot hook.",

        // Found by THIS TEST on its first run, which is the point of writing it. The annual cron is
        // parking, not a schedule: §252 F1 hard-guarded Run() on EnableEmailFeatures:Allowed so the
        // Jan-1 tick cannot mass-enable e-mail features unattended. It exists only for the admin
        // POST /admin/functions/... path, so "it has not succeeded lately" is the correct state.
    };

    [Fact]
    public void Every_job_the_silence_detector_CANNOT_judge_is_declared_with_a_reason()
    {
        var unjudgeable = JobCatalog.All
            .Where(j => JobSilenceDetector.ExpectedGap(j) is null)
            .Select(j => j.FunctionName)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var undeclared = unjudgeable.Where(f => !RareCadenceByDesign.ContainsKey(f)).ToList();

        Assert.True(undeclared.Count == 0,
            "These jobs run on a cadence the §624 silence detector cannot judge, so if they stopped "
            + "NOTHING would say so. Either give them a regular cadence or declare them in "
            + $"{nameof(RareCadenceByDesign)} with a reason: " + string.Join(", ", undeclared));
    }

    [Fact]
    public void The_rare_cadence_list_never_keeps_a_job_that_is_no_longer_rare()
    {
        // The reverse drift: a job given a real cadence must LEAVE the list, or it stays unwatched
        // forever behind an excuse that stopped being true.
        var stale = RareCadenceByDesign.Keys
            .Where(f => JobCatalog.Find(f) is not null
                        && JobSilenceDetector.ExpectedGap(JobCatalog.Find(f)!) is not null)
            .ToList();

        Assert.True(stale.Count == 0,
            "These jobs now run on a judgeable cadence and must be REMOVED from "
            + $"{nameof(RareCadenceByDesign)} so the watchdog starts covering them: "
            + string.Join(", ", stale));
    }

    // -------------------------------------------------------------------------
    // INACTIVE coverage (§627) — the honest, incomplete part
    // -------------------------------------------------------------------------

    /// <summary>
    /// The jobs instrumented to report INACTIVE. 🔒 <b>This list is the "no silent caps" rule made
    /// mechanical</b> (§624): partial coverage is fine — silence is UNKNOWN and never flagged — but
    /// it must be VISIBLE rather than something he discovers when an alert he expected never comes.
    /// </summary>
    private static readonly string[] DeclaredInstrumented =
    {
        // §627 — the four engines where this session's incidents actually happened.
        "AttendeeBackstageSyncJob",     // attendee-reconcile off ⇒ ticket purchases silently stop
        "SessionBackstagePushJob",      // §544 itself — the line that was invisible for weeks
        "SessionChangeDetectionJob",    // §576 — demanded a sync stage that had been DELETED
        "SpeakerChangeDetectionJob",    // §621 — read "unavailable" without ever calling Zoho
        // §636 — the remaining five, closing §630.2's documented gap. Each has a gate that can
        // silently stop something the operator would only notice by its absence.
        // (BackstageSyncJob was here until §641 RETIRED it — a job with no [Function] attribute
        //  cannot run, so it has nothing to report and must not be declared.)
        "ErpSyncCustomerContactJob",    // feature off / not configured ⇒ ERP and webshop drift apart
        "SponsorProvisioningStallJob",  // no active edition ⇒ stalled provisioning goes undetected
        "SponsorWelcomeReconcileJob",   // welcome-email off ⇒ new sponsor contacts never welcomed
        "WelcomeReconcileJob",          // welcome-email off ⇒ sign-ups accumulate unwelcomed
        // §640 — the new scheduled sponsor reconcile. Instrumented from birth, so the job created
        // to fix an invisible gap can never itself become one.
        "SponsorZohoReconcileJob",
        // §655 — the failed-mail retry. Instrumented from birth for the same reason.
        "FailedMailRetryJob",
    };

    [Fact]
    public void The_declared_INACTIVE_coverage_matches_what_is_actually_wired()
    {
        var actual = InstrumentedJobs().OrderBy(f => f, StringComparer.Ordinal).ToList();
        var declared = DeclaredInstrumented.OrderBy(f => f, StringComparer.Ordinal).ToList();

        Assert.Equal(declared, actual);
    }

    [Fact]
    public void Every_instrumented_job_is_a_real_deployed_job()
    {
        var real = RealTimerJobs().ToHashSet(StringComparer.Ordinal);

        foreach (var fn in DeclaredInstrumented)
        {
            Assert.True(real.Contains(fn), $"'{fn}' is declared instrumented but is not a timer job.");
            Assert.NotNull(JobCatalog.Find(fn));
        }
    }

    // -------------------------------------------------------------------------
    // CREDENTIAL coverage (§545(b)) — one registration line per integration
    // -------------------------------------------------------------------------

    /// <summary>
    /// The integrations that must have <c>.AddCredentialFailureAlert(...)</c> beside their
    /// <c>AddHttpClient</c>, in BOTH hosts.
    /// </summary>
    /// <remarks>
    /// §545(b) named these six alongside Zoho, which has had one since §524. Tonight proved the
    /// need twice: a dead SharePoint client secret in BOTH environments (§598) and a never-set
    /// Zoho scope flag (§621), each hidden for an unknown length of time.
    /// </remarks>
    private static readonly string[] IntegrationsNeedingCredentialAlerts =
    {
        "SharePoint", "Company Manager", "WooCommerce", "Sessionize", "LinkedIn", "e-conomic",
    };

    /// <summary>
    /// 🔒 §635 — <b>Brevo is on §545's list and is deliberately NOT in the array above.</b> It is
    /// reached over SMTP, so an <c>HttpClient</c> handler structurally cannot see it; covering it
    /// needed a different mechanism (<see cref="EmailTransportHealth"/>, surfaced as a banner
    /// because a dead relay cannot mail you about itself).
    ///
    /// <para>This test exists so that omission stays DELIBERATE. Without it, "Brevo is missing from
    /// the credential-alert list" reads as an oversight to the next person — and the honest record
    /// of why is the thing that keeps a documented gap from becoming a forgotten one.</para>
    /// </summary>
    [Fact]
    public void Brevo_is_covered_by_the_SMTP_route_not_the_HttpClient_one()
    {
        Assert.DoesNotContain("Brevo", IntegrationsNeedingCredentialAlerts);

        // The alternative mechanism must actually exist and classify a dead SMTP credential.
        Assert.True(EmailTransportHealth.LooksLikeAuthFailure("535 5.7.8 Authentication failed"));
        Assert.True(EmailTransportHealth.Evaluate(
            new[] { "535 auth", "535 auth", "535 auth" }).IsDown);
    }

    [Theory]
    [InlineData("src/CommunityHub.Jobs/Program.cs")]
    [InlineData("src/CommunityHub/Program.cs")]
    public void Every_named_integration_has_a_credential_alert_registered_in_BOTH_hosts(string relative)
    {
        var root = RepoRoot();
        var source = File.ReadAllText(Path.Combine(root, relative));

        var missing = IntegrationsNeedingCredentialAlerts
            .Where(i => !source.Contains($"AddCredentialFailureAlert(\"{i}\")", StringComparison.Ordinal))
            .ToList();

        Assert.True(missing.Count == 0,
            $"{relative} registers these integrations without credential alerting, so a dead secret "
            + "would stop them silently — exactly the §598 SharePoint failure: "
            + string.Join(", ", missing));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CommunityHub.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>
    /// 🔒 §645 — every job must declare which SYSTEM it belongs to, so the grouped Jobs page cannot
    /// quietly fill a "Platform &amp; housekeeping" bucket with Zoho and webshop jobs that merely
    /// forgot to say. <see cref="JobSystem.Platform"/> is the parameter default, so an omission is
    /// invisible without this check — which is exactly the §637 shape.
    /// </summary>
    [Fact]
    public void Every_job_declares_which_system_it_belongs_to()
    {
        // Only genuinely hub-internal work belongs in Platform: watchdogs and housekeeping.
        var allowedInPlatform = new[]
        {
            "JobSilenceAlertJob", "WelcomeGrantPruneJob", "AuditPurgeJob",
        };

        var undeclared = JobCatalog.All
            .Where(j => j.System == JobSystem.Platform && !allowedInPlatform.Contains(j.FunctionName))
            .Select(j => j.FunctionName)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        Assert.True(undeclared.Count == 0,
            "These jobs fell into the default 'Platform' group, which almost always means the "
            + "System: argument was forgotten — they will appear under the wrong heading on the "
            + "Jobs page: " + string.Join(", ", undeclared));
    }

    /// <summary>
    /// The four engines where this session's incidents ACTUALLY happened must never lose their
    /// instrumentation. Named individually so a refactor that drops one fails loudly.
    /// </summary>
    [Theory]
    [InlineData("SessionBackstagePushJob")]
    [InlineData("SessionChangeDetectionJob")]
    [InlineData("SpeakerChangeDetectionJob")]
    public void The_engines_that_actually_went_dark_stay_instrumented(string fn)
    {
        Assert.Contains(fn, InstrumentedJobs());
    }
}
