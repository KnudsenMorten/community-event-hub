using CommunityHub.Auth;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CommunityHub.Pages.Organizer;

/// <summary>
/// §327 — every background job in one place: what it does, how often it runs, when it last
/// ran, whether its feature switch is on, and an operator-editable "run at most every N
/// minutes" limit.
///
/// <para>The job list comes from <see cref="JobCatalog"/>, which a reflection test holds
/// EQUAL to the real <c>[TimerTrigger]</c> functions — so this page cannot quietly omit a job
/// that is running, nor invent one that is not.</para>
/// </summary>
[Authorize]
public class JobsModel : PageModel
{
    private readonly ICurrentParticipantAccessor _participant;
    private readonly JobScheduleService _jobs;
    private readonly JobTriggerService _trigger;

    // §635 — optional so existing wiring/tests construct unchanged; null ⇒ no banner.
    private readonly CommunityHub.Core.Diagnostics.EmailTransportHealth? _mail;

    public JobsModel(
        ICurrentParticipantAccessor participant, JobScheduleService jobs, JobTriggerService trigger,
        CommunityHub.Core.Diagnostics.EmailTransportHealth? mail = null)
    {
        _participant = participant;
        _jobs = jobs;
        _trigger = trigger;
        _mail = mail;
    }

    public bool AccessDenied { get; private set; }
    public string? Message { get; private set; }
    public IReadOnlyList<JobStatusRow> Rows { get; private set; } = Array.Empty<JobStatusRow>();

    /// <summary>
    /// §635 — is outbound e-mail itself down? Shown as a BANNER because, when it is, no alert mail
    /// can reach him: the alerter would have to send through the very relay that is failing.
    /// </summary>
    public CommunityHub.Core.Diagnostics.EmailTransportHealth.Status MailStatus { get; private set; }
        = CommunityHub.Core.Diagnostics.EmailTransportHealth.Status.Healthy;

    /// <summary>§543 — false when this environment has no Functions admin key wired.</summary>
    public bool CanTrigger => _trigger.IsConfigured;

    /// <summary>
    /// §543 — RUN THIS JOB NOW (operator 2026-07-28: "i need a trigger now option for each of the
    /// functions/jobs … i cannot trigger now, i cannot change frequency"). Starts the real timer
    /// function through the Functions admin endpoint, so it is the production routine running
    /// early — never a re-implementation of it.
    /// </summary>
    public async Task<IActionResult> OnPostTriggerAsync(string functionName, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        var result = await _trigger.TriggerAsync(functionName, ct);
        return RedirectToPage(new { msg = result.Message });
    }

    /// <summary>
    /// §707.13 — show jobs whose FEATURE SWITCH IS OFF. Default FALSE (operator 2026-07-30:
    /// *"make a default filter to not show feature off settings and allow me to tick that filter on
    /// if i want to see it. it disturbs my eye that the first one is off"*).
    /// </summary>
    /// <remarks>
    /// A switched-off job does nothing, so on the default view it is noise sitting at the top of a
    /// group. Hiding it is safe ONLY because the count of what was hidden is stated on the page —
    /// a filter that silently removes rows would make this page lie by omission, which is the
    /// defect this codebase keeps removing.
    /// </remarks>
    [BindProperty(SupportsGet = true, Name = "showOff")]
    public bool ShowSwitchedOff { get; set; }

    /// <summary>How many rows the default filter is hiding, so the page can say so.</summary>
    public int HiddenByFilter { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? msg, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (me.Role != ParticipantRole.Organizer) { AccessDenied = true; return Page(); }

        Message = msg;
        var all = await _jobs.BuildAsync(me.EventId, ct);

        // §707.13 — hide the switched-off jobs unless asked for, and always COUNT them.
        HiddenByFilter = all.Count(r => r.BlockedByFeature);
        Rows = ShowSwitchedOff
            ? all
            : all.Where(r => !r.BlockedByFeature).ToList();

        // §635 — never let a diagnostics read break the page it is diagnosing.
        if (_mail is not null)
        {
            try { MailStatus = await _mail.CheckAsync(ct); }
            catch { /* the banner is a nicety; the jobs table is the page */ }
        }

        return Page();
    }

    public async Task<IActionResult> OnPostSetIntervalAsync(
        string functionName, int minutes, CancellationToken ct)
    {
        var me = _participant.Current;
        if (me is null) return RedirectToPage("/Login");
        if (!OrganizerAuth.IsRealOrganizer(me)) return Forbid();

        // §510 — enforce the floor SERVER-SIDE. The input carries min/step, but a browser is not a
        // validator, and a value under the base tick would be stored and then silently do nothing —
        // exactly the class of control §509 was raised about.
        var job = CommunityHub.Core.Settings.JobCatalog.All
            .FirstOrDefault(d => d.FunctionName == functionName);

        if (job is not null && job.IsIntervalDriven && minutes > 0
            && minutes < CommunityHub.Core.Settings.JobDescriptor.BaseTickMinutes)
        {
            return RedirectToPage(new { msg =
                $"{Title(functionName)}: {minutes} minute(s) is below the " +
                $"{CommunityHub.Core.Settings.JobDescriptor.BaseTickMinutes}-minute minimum, so nothing changed — " +
                "the host only offers each job a run that often." });
        }

        var ok = await _jobs.SetMinIntervalAsync(functionName, minutes, me.Email, ct);
        if (!ok)
        {
            return RedirectToPage(new { msg = $"'{functionName}' is not a known job — nothing changed." });
        }

        if (job is not null && job.IsIntervalDriven)
        {
            return RedirectToPage(new { msg = minutes <= 0
                ? $"{Title(functionName)}: reset to its default of every {job.DefaultIntervalMinutes} minute(s)."
                : $"{Title(functionName)}: now runs every {minutes} minute(s)." });
        }

        return RedirectToPage(new { msg = minutes <= 0
            ? $"{Title(functionName)}: limit removed — it now runs on its own schedule."
            : $"{Title(functionName)}: will run at most every {minutes} minute(s)." });
    }

    private static string Title(string functionName) =>
        JobCatalog.Find(functionName)?.Title ?? functionName;

    /// <summary>
    /// §440 — a raw NCRONTAB expression in plain English. The operator's words were "this is
    /// impossible to understand - how can i change the schedule": the Schedule column printed
    /// something like <c>0 */5 * * * *</c> with nothing saying what it meant, nor that it is
    /// code-owned. Falls back to the RAW expression when the shape is unfamiliar, so an unusual
    /// schedule degrades to today's behaviour rather than to a confidently WRONG description.
    /// </summary>
    public static string DescribeCron(string? cron)
    {
        if (string.IsNullOrWhiteSpace(cron)) return "";
        var p = cron.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length != 6) return cron;   // 6-field NCRONTAB (sec min hour day month dow) only

        var (sec, min, hour, dom, mon, dow) = (p[0], p[1], p[2], p[3], p[4], p[5]);
        var everyDay = dom == "*" && mon == "*" && dow == "*";

        // "0 */5 * * * *" — every N minutes.
        if (sec == "0" && min.StartsWith("*/") && hour == "*" && everyDay
            && int.TryParse(min[2..], out var everyMin))
        {
            return everyMin == 1 ? "Every minute" : $"Every {everyMin} minutes";
        }
        // "0 0 */2 * * *" — every N hours.
        if (sec == "0" && min == "0" && hour.StartsWith("*/") && everyDay
            && int.TryParse(hour[2..], out var everyHour))
        {
            return everyHour == 1 ? "Every hour" : $"Every {everyHour} hours";
        }
        // "0 15 * * * *" — hourly at a fixed minute.
        if (sec == "0" && hour == "*" && everyDay && int.TryParse(min, out var atMin))
        {
            return $"Every hour at :{atMin:00}";
        }
        // "0 30 6 * * *" — once a day at a fixed time.
        if (sec == "0" && everyDay
            && int.TryParse(min, out var dMin) && int.TryParse(hour, out var dHour))
        {
            return $"Every day at {dHour:00}:{dMin:00} UTC";
        }
        return cron;
    }

    /// <summary>"3 minutes ago" / "never" — a timestamp an organizer can read at a glance.</summary>
    public static string Ago(DateTimeOffset? when)
    {
        if (when is null) return "never";
        var d = DateTimeOffset.UtcNow - when.Value;
        if (d < TimeSpan.Zero) return "just now";
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} h ago";
        return $"{(int)d.TotalDays} d ago";
    }
}
