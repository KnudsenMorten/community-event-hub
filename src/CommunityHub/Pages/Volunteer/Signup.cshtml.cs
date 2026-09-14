using System.Text.RegularExpressions;
using CommunityHub.Core.Config;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using CommunityHub.Core.Integrations;
using CommunityHub.Core.Volunteers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Pages.Volunteer;

/// <summary>
/// PUBLIC (anonymous) volunteer sign-up — a FOUR-step wizard (operator 2026-06-23):
///   1. About you: name, email, mobile.
///   2. My availability: per-day Full / Half / Blocked / Not-able-to-help (incl. any
///      configured extra days such as the packing day).
///   3. Public profile: LinkedIn + photo, and the consent to be featured on the public
///      volunteer page. §1148 — ticking that consent makes the photo REQUIRED.
///   4. Collaboration agreement: the volunteer must accept it to submit.
///
/// ⚰️ This said "3-step" and listed the agreement as step 3, which stopped being true when the
/// public-profile step was added — the same drift that left the agreement error message pointing at
/// step 3 (§1150). A wizard's step numbers appear in the UI and in error text, so a stale count here
/// is a stale instruction there.
/// The submission creates a pending <see cref="Participant"/>
/// (<c>Role = Volunteer, IsActive = false, QueueSource = VolunteerInterestForm</c>)
/// plus the per-day availability + a <see cref="VolunteerAvailability"/> row holding
/// LinkedIn / photo URL / agreement timestamp, and emails the volunteer lead.
///
/// URL: /volunteer/signup — anonymous so it can be linked from social / the site.
/// </summary>
[AllowAnonymous]
public class SignupModel : PageModel
{
    /// <summary>The volunteer lead notified of every new / updated application.</summary>
    public const string NotifyEmail = "mlh@expertslive.dk";
    private const long MaxPhotoBytes = 8 * 1024 * 1024; // 8 MB

    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;
    private readonly EventEditionConfigLoader _cfg;
    private readonly EventConfigOptions _cfgOptions;
    private readonly SharePointUploadClient _sp;
    private readonly IEmailSender _email;
    private readonly ILogger<SignupModel> _log;

    public SignupModel(
        CommunityHubDbContext db,
        TimeProvider clock,
        EventEditionConfigLoader cfg,
        EventConfigOptions cfgOptions,
        SharePointUploadClient sp,
        IEmailSender email,
        CommunityHub.Core.Integrations.DocLibrary.IDocLibraryPathResolver paths,
        ILogger<SignupModel> log)
    {
        _db = db;
        _clock = clock;
        _cfg = cfg;
        _cfgOptions = cfgOptions;
        _sp = sp;
        _email = email;
        _paths = paths;
        _log = log;
    }

    private readonly CommunityHub.Core.Integrations.DocLibrary.IDocLibraryPathResolver _paths;

    // --- Step 1: about you --------------------------------------------------
    [BindProperty] public string FullName    { get; set; } = string.Empty;
    [BindProperty] public string Email       { get; set; } = string.Empty;
    [BindProperty] public string? Phone      { get; set; }
    [BindProperty] public string? LinkedInUrl { get; set; }
    [BindProperty] public IFormFile? Photo   { get; set; }

    // --- Step 2: availability ----------------------------------------------
    [BindProperty] public List<DayInput> Availability { get; set; } = new();

    public class DayInput
    {
        public DateOnly Day { get; set; }
        /// <summary>The chosen option's stable slot id (see <see cref="VolunteerDayOptions"/>).</summary>
        public string? Slot { get; set; }
        public string? Note { get; set; }
    }

    /// <summary>
    /// RawNote is the full stored Note (incl. any "[slot]" tag — used to re-select
    /// the right option after a validation error); UserNote is the free text only.
    /// </summary>
    public record DayRow(DateOnly Day, string Label, VolunteerAvailabilityLevel Level, string? RawNote)
    {
        public string? UserNote => VolunteerDayOptions.StripSlot(RawNote);
    }
    public IReadOnlyList<DayRow> Days { get; private set; } = Array.Empty<DayRow>();

    // --- Public profile step ------------------------------------------------
    /// <summary>Consent to be featured on the public volunteer page (name/photo/LinkedIn).</summary>
    [BindProperty] public bool ProfileConsent { get; set; }

    // --- Final step: agreement ----------------------------------------------
    [BindProperty] public bool AgreementAccepted { get; set; }

    /// <summary>Honeypot — hidden off-screen; any value ⇒ silent 200 (bot).</summary>
    [BindProperty] public string? Website { get; set; }

    // --- View-side state ----------------------------------------------------
    public string? EventDisplayName { get; private set; }
    public bool NoActiveEvent { get; private set; }
    public bool SubmittedOk { get; private set; }
    public string? SubmittedName { get; private set; }
    public string? SubmittedEmail { get; private set; }
    public string? ErrorMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var active = await GetActiveEventAsync(ct);
        if (active is null) { NoActiveEvent = true; return Page(); }
        EventDisplayName = active.DisplayName;
        Days = (await EventDaysAsync(active.Id, ct))
            .Select(d => new DayRow(d.Day, d.Label, VolunteerAvailabilityLevel.Full, null))
            .ToList();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        // Honeypot — pretend success, write nothing.
        if (!string.IsNullOrWhiteSpace(Website))
        {
            _log.LogInformation("Volunteer signup honeypot tripped from {Ip}", HttpContext.Connection.RemoteIpAddress);
            SubmittedOk = true; SubmittedName = "there"; SubmittedEmail = "(no email recorded)";
            EventDisplayName = "the event";
            return Page();
        }

        var active = await GetActiveEventAsync(ct);
        if (active is null) { NoActiveEvent = true; return Page(); }
        EventDisplayName = active.DisplayName;

        // Re-build day rows (so a validation error re-renders step 2 with the
        // user's posted choices preserved).
        var dayList = await EventDaysAsync(active.Id, ct);
        var validDays = dayList.Select(d => d.Day).ToHashSet();
        var labelByDay = dayList.ToDictionary(d => d.Day, d => d.Label);
        var postedByDay = Availability
            .Where(i => validDays.Contains(i.Day))
            .GroupBy(i => i.Day).ToDictionary(g => g.Key, g => g.Last());
        Days = dayList.Select(d =>
        {
            postedByDay.TryGetValue(d.Day, out var p);
            // Re-derive the row from the posted slot so a validation error re-renders
            // step 2 with the user's choice preserved.
            var chosen = p?.Slot is null
                ? null
                : VolunteerDayOptions.For(d.Day).FirstOrDefault(o =>
                    string.Equals(o.Slot, p.Slot, StringComparison.OrdinalIgnoreCase));
            var level = chosen?.Level ?? VolunteerAvailabilityLevel.Full;
            var rawNote = chosen is null
                ? null
                : VolunteerDayOptions.ComposeNote(chosen.Slot, p?.Note);
            return new DayRow(d.Day, d.Label, level, rawNote);
        }).ToList();

        // --- Validate ------------------------------------------------------
        var fullName = (FullName ?? string.Empty).Trim();
        var email    = (Email    ?? string.Empty).Trim().ToLowerInvariant();
        var phone    = string.IsNullOrWhiteSpace(Phone) ? null : Phone.Trim();
        var linkedIn = string.IsNullOrWhiteSpace(LinkedInUrl) ? null : LinkedInUrl.Trim();

        if (fullName.Length < 2) { ErrorMessage = "Please enter your full name."; return Page(); }
        if (!IsPlausibleEmail(email)) { ErrorMessage = "Please enter a valid email address."; return Page(); }
        if (phone is null || phone.Length < 4) { ErrorMessage = "Please enter your mobile number."; return Page(); }
        if (!AgreementAccepted)
        {
            // ⚰️ §1150 — this said "(step 3)". The agreement is STEP 4; it moved when the public-
            // profile step was added and the message was left behind. A wrong step number on the one
            // error that blocks submission sends people to the wrong page to fix it.
            ErrorMessage = "Please accept the volunteer collaboration agreement (step 4) to submit.";
            return Page();
        }
        // 🔴 §1148 — CONSENTING TO BE SHOWN WITH A PHOTO REQUIRES A PHOTO.
        //
        // Operator 2026-08-28: *"people have completed the form but not uploaded the picture"* — with
        // a queue showing rows consented to the public wall and "none" under Photo.
        //
        // 🔑 The checkbox promises exactly this: *"feature me on the public volunteer page (name,
        // photo & LinkedIn)"*. Accepting that with no photo creates a promise the wall cannot keep,
        // and the gap only surfaces weeks later when somebody builds the page.
        //
        // 🔒 CONDITIONAL, not blanket (his decision, 2026-08-28). A volunteer who does NOT opt in is
        // never blocked for want of a headshot — this is a public recruitment form, and turning
        // somebody away over a picture they did not offer to give costs a volunteer.
        //
        // ⚠️ Enforced HERE as well as in the browser: the client-side gate is a courtesy, and a
        // form that can be posted without JavaScript would otherwise sail straight past it.
        if (ProfileConsent)
        {
            // §1151 — BOTH fields, because the consent names both: "feature me on the public
            // volunteer page (name, photo & LinkedIn)". Requiring only the photo let somebody
            // opt into a listing we then could not build — the same half-kept promise, one field
            // over.
            var missingPhoto = Photo is null || Photo.Length == 0;
            var missingLinkedIn = string.IsNullOrWhiteSpace(linkedIn);

            if (missingPhoto || missingLinkedIn)
            {
                // 🔑 Name WHICH one is missing. "Fill in the required fields" on a step with two
                // of them is a puzzle, and this is a public form where a puzzle costs a volunteer.
                var what = missingPhoto && missingLinkedIn ? "a photo and your LinkedIn URL"
                    : missingPhoto ? "a photo"
                    : "your LinkedIn URL";
                ErrorMessage =
                    $"You ticked \"feature me on the public volunteer page\", which shows your name, "
                    + $"photo and LinkedIn — please add {what}, or untick that box if you would "
                    + "rather not be featured.";
                return Page();
            }

            // ⚠️ The form is `novalidate`, so the browser does NOT check `type="url"`. Without this
            // a typo (or a `javascript:` string) reaches the organizer queue as the applicant's
            // profile link — §1149 refuses to make such a value clickable, but the honest place to
            // catch it is here, where the person who typed it can still fix it.
            //
            // §1152 — a MISSING "https://" is not a typo. "www.linkedin.com/in/…" is how people
            // write a web address and every browser accepts it, so it is normalised rather than
            // rejected; only a value carrying a non-http(s) scheme is refused. Stored normalised so
            // the row is right at the source, not repaired at every render.
            var normalized = CommunityHub.Core.Volunteers.ExternalProfileUrl.TryNormalize(linkedIn);
            if (normalized is null)
            {
                ErrorMessage =
                    "That LinkedIn address does not look like a web address — please use something "
                    + "like https://www.linkedin.com/in/you.";
                return Page();
            }
            linkedIn = normalized;
        }

        if (Photo is not null)
        {
            if (Photo.Length > MaxPhotoBytes)
            {
                ErrorMessage = "Your photo is too large (max 8 MB). Please choose a smaller image.";
                return Page();
            }
            if (!(Photo.ContentType ?? string.Empty).StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                ErrorMessage = "The photo must be an image file (JPG or PNG).";
                return Page();
            }
        }

        // --- Dedup on (EventId, Email) -------------------------------------
        var existing = await _db.Participants.FirstOrDefaultAsync(
            p => p.EventId == active.Id && p.Email == email, ct);
        Participant applicant;
        if (existing is not null)
        {
            if (existing.IsActive)
            {
                ErrorMessage = $"Looks like {email} is already registered for this event. If you forgot how to sign in, request a PIN at the Sign in page.";
                return Page();
            }
            if (existing.Role != ParticipantRole.Volunteer)
            {
                ErrorMessage = $"{email} is already on file for this event. Please contact the organizer team.";
                return Page();
            }
            // Pending applicant re-submitting: refresh their details.
            existing.FullName = fullName;
            existing.Phone = phone;
            applicant = existing;
        }
        else
        {
            applicant = new Participant
            {
                EventId   = active.Id,
                Email     = email,
                FullName  = fullName,
                Phone     = phone,
                Role      = ParticipantRole.Volunteer,
                IsActive  = false,
                LifecycleState = ParticipantLifecycleState.Inactive,
                QueueSource    = ParticipantQueueSource.VolunteerInterestForm,
                CreatedAt = _clock.GetUtcNow(),
            };
            _db.Participants.Add(applicant);
        }

        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex)
        {
            _log.LogWarning(ex, "Volunteer signup DB write failed for {Email}", email);
            ErrorMessage = "We hit a problem saving your application. Please try again in a moment.";
            return Page();
        }

        // --- Per-day availability ------------------------------------------
        await SaveDayAvailabilityAsync(active.Id, applicant.Id, validDays, ct);

        // --- Photo upload (fail-soft) --------------------------------------
        // §769.9 — keyed on the participant id now, not the name (operator: "volunteer pictures
        // switch to id to make consistent"). The row is saved above, so the id exists here.
        string? photoUrl = await TryUploadPhotoAsync(applicant.Id, ct);

        // --- Volunteer metadata (LinkedIn / photo / agreement) -------------
        await SaveVolunteerMetaAsync(active.Id, applicant.Id, linkedIn, photoUrl, ct);

        // --- Notify the volunteer lead -------------------------------------
        await NotifyLeadAsync(applicant, fullName, email, phone, linkedIn, photoUrl, validDays, labelByDay, ct);

        _log.LogInformation(
            "Volunteer application: id={Id} email={Email} name={Name} event={EventId} photo={HasPhoto}",
            applicant.Id, email, fullName, active.Id, photoUrl is not null);

        SubmittedOk = true;
        SubmittedName = fullName.Split(' ').First();
        SubmittedEmail = email;
        return Page();
    }

    // ------- Helpers --------------------------------------------------------

    private async Task SaveDayAvailabilityAsync(
        int eventId, int participantId, HashSet<DateOnly> validDays, CancellationToken ct)
    {
        var existing = await _db.VolunteerDayAvailabilities
            .Where(x => x.EventId == eventId && x.ParticipantId == participantId)
            .ToListAsync(ct);

        foreach (var input in Availability)
        {
            if (!validDays.Contains(input.Day)) continue;

            // The posted slot drives the capacity Level and (with the free note) the
            // stored Note. Ignore an unknown/absent slot for this day.
            var chosen = VolunteerDayOptions.For(input.Day).FirstOrDefault(o =>
                string.Equals(o.Slot, input.Slot, StringComparison.OrdinalIgnoreCase));
            if (chosen is null) continue;

            var userNote = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim();
            var note = VolunteerDayOptions.ComposeNote(chosen.Slot, userNote);
            if (note is { Length: > 500 }) note = note[..500];

            var row = existing.FirstOrDefault(x => x.Day == input.Day);
            if (row is null)
            {
                _db.VolunteerDayAvailabilities.Add(new VolunteerDayAvailability
                {
                    EventId = eventId, ParticipantId = participantId,
                    Day = input.Day, Level = chosen.Level, Note = note,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
            }
            else { row.Level = chosen.Level; row.Note = note; row.UpdatedAt = DateTimeOffset.UtcNow; }
        }
        await _db.SaveChangesAsync(ct);
    }

    private async Task SaveVolunteerMetaAsync(
        int eventId, int participantId, string? linkedIn, string? photoUrl, CancellationToken ct)
    {
        var meta = await _db.VolunteerAvailabilities.FirstOrDefaultAsync(
            v => v.EventId == eventId && v.ParticipantId == participantId, ct);
        if (meta is null)
        {
            meta = new VolunteerAvailability
            {
                EventId = eventId, ParticipantId = participantId, CreatedAt = _clock.GetUtcNow(),
            };
            _db.VolunteerAvailabilities.Add(meta);
        }
        else { meta.UpdatedAt = _clock.GetUtcNow(); }

        meta.LinkedInUrl = linkedIn;
        if (photoUrl is not null) meta.PhotoUrl = photoUrl; // keep an earlier photo if none uploaded now
        meta.ProfileConsent = ProfileConsent;
        meta.AgreementAcceptedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
    }

    private async Task<string?> TryUploadPhotoAsync(int participantId, CancellationToken ct)
    {
        if (Photo is null || Photo.Length == 0) return null;
        try
        {
            var sp = _cfg.Load(_cfgOptions.EventConfigPath).SharePoint;
            // §768.14 — the folder is a registry key now (VolunteerPhotos), resolved under the one
            // configured root rather than carried whole in the edition config.
            if (sp is null || string.IsNullOrWhiteSpace(sp.SiteUrl) || !_sp.IsConfigured
                || !_paths.TryResolve(
                       CommunityHub.Core.Integrations.DocLibrary.DocLibraryPaths.VolunteerPhotos,
                       out var photoFolder))
            {
                _log.LogInformation("Volunteer photo upload skipped — SharePoint not configured.");
                return null;
            }

            using var ms = new MemoryStream();
            await Photo.CopyToAsync(ms, ct);

            // §769.9 — volunteer-photo-{id}.{ext}, composed by the ONE owner of this convention.
            // It was {Full Name}.ext and lived here as a private helper — which was fine until a
            // READER appeared that DELETES: a name cannot identify a person, so two volunteers with
            // the same name shared one file and the departing one's cleanup would have taken the
            // active one's photo.
            var name = CommunityHub.Core.Integrations.Graphics.VolunteerPhotoFileName.Build(
                participantId, Path.GetExtension(Photo.FileName));

            var contentType = string.IsNullOrWhiteSpace(Photo.ContentType)
                ? "application/octet-stream"
                : Photo.ContentType;
            var bytes = ms.ToArray();

            var (_, webUrl, _) = await _sp.UploadFileAsync(
                sp.SiteUrl, sp.DriveName, photoFolder, name, bytes, contentType, ct);

            // §1132 (operator 2026-08-25: *"same for volunteers that upload pics, could be geat to
            // have 2 files - one with id and another with name"*) — a SECOND copy named after the
            // person, so the folder can be searched by name instead of by id.
            //
            // 🔒 The id file above stays authoritative and is what `webUrl` points at. The alias is
            // written afterwards and best-effort: a failure here must never cost the volunteer their
            // photo, which the outer catch would otherwise do by returning null.
            // 🔒 It carries the trailing id, so two volunteers with the same name cannot overwrite
            // each other — the exact defect §769.9 removed when it dropped `{Full Name}.ext` — and
            // VolunteerPhotoFileName.TryParse recognises it, so deactivation cleanup deletes it too.
            var aliasName = CommunityHub.Core.Integrations.Graphics.VolunteerPhotoFileName.BuildAlias(
                participantId, FullName, Path.GetExtension(Photo.FileName));
            if (aliasName is not null)
            {
                try
                {
                    await _sp.UploadFileAsync(
                        sp.SiteUrl, sp.DriveName, photoFolder, aliasName, bytes, contentType, ct);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex,
                        "§1132: the volunteer name alias '{Alias}' could not be written for "
                        + "participant {ParticipantId}; the id-named photo is stored.",
                        aliasName, participantId);
                }
            }

            return string.IsNullOrWhiteSpace(webUrl) ? null : webUrl;
        }
        catch (Exception ex)
        {
            // Never fail the application because the photo couldn't be stored.
            _log.LogError(ex,
                "Volunteer photo upload failed for participant {ParticipantId}; application kept.",
                participantId);
            return null;
        }
    }

    private async Task NotifyLeadAsync(
        Participant applicant, string fullName, string email, string? phone, string? linkedIn,
        string? photoUrl, HashSet<DateOnly> validDays,
        IReadOnlyDictionary<DateOnly, string> labelByDay, CancellationToken ct)
    {
        try
        {
            var rows = (await _db.VolunteerDayAvailabilities
                    .Where(x => x.EventId == applicant.EventId && x.ParticipantId == applicant.Id)
                    .ToListAsync(ct))
                .Where(x => validDays.Contains(x.Day)).OrderBy(x => x.Day).ToList();

            string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);
            var availRows = string.Concat(rows.Select(r =>
                "<tr><td style=\"padding:3px 10px 3px 0;\">"
                + Enc(labelByDay.TryGetValue(r.Day, out var l) ? l : r.Day.ToString("yyyy-MM-dd"))
                + "</td><td style=\"padding:3px 10px;\"><b>"
                + Enc(VolunteerDayOptions.DisplayLabel(r.Day, r.Level, r.Note))
                + "</b></td><td style=\"padding:3px 0;color:#555;\">"
                + Enc(VolunteerDayOptions.StripSlot(r.Note)) + "</td></tr>"));

            var html =
                $"<p>New volunteer application from <b>{Enc(fullName)}</b>.</p><ul>"
                + $"<li><b>Email:</b> <a href=\"mailto:{Enc(email)}\">{Enc(email)}</a></li>"
                + $"<li><b>Mobile:</b> {Enc(phone ?? "—")}</li>"
                + $"<li><b>LinkedIn:</b> {(string.IsNullOrWhiteSpace(linkedIn) ? "—" : $"<a href=\"{Enc(linkedIn)}\">{Enc(linkedIn)}</a>")}</li>"
                + $"<li><b>Photo:</b> {(string.IsNullOrWhiteSpace(photoUrl) ? "—" : $"<a href=\"{Enc(photoUrl)}\">view</a>")}</li>"
                + "<li><b>Agreement:</b> accepted</li></ul>"
                + "<p><b>Availability:</b></p><table style=\"border-collapse:collapse;font-size:14px;\">"
                + "<tr><th align=\"left\" style=\"padding:3px 10px 3px 0;\">Day</th>"
                + "<th align=\"left\" style=\"padding:3px 10px;\">Availability</th>"
                + "<th align=\"left\" style=\"padding:3px 0;\">Note</th></tr>"
                + availRows + "</table>";

            await _email.SendAsync(
                NotifyEmail, $"[Volunteer application] {fullName}", html, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Volunteer application notification to {Lead} failed for {Email}.", NotifyEmail, email);
        }
    }

    private static string LevelLabel(VolunteerAvailabilityLevel level) => level switch
    {
        VolunteerAvailabilityLevel.Full => "Full day",
        VolunteerAvailabilityLevel.Half => "Half day",
        VolunteerAvailabilityLevel.Blocked => "Blocked (attending only)",
        VolunteerAvailabilityLevel.Unavailable => "Not able to help",
        _ => level.ToString(),
    };

    // §6.9 — the volunteer photo naming rule moved to VolunteerPhotoFileName and this copy is
    // DELETED, not left behind. A second implementation is what lets writer and reader drift, and
    // the reader here deletes files.

    private async Task<IReadOnlyList<(DateOnly Day, string Label)>> EventDaysAsync(int eventId, CancellationToken ct)
    {
        var ev = await _db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Id == eventId, ct);
        if (ev is null) return Array.Empty<(DateOnly, string)>();

        var set = new SortedSet<DateOnly>();
        if (ev.PreDayDate is { } pre) set.Add(pre);
        for (var d = ev.StartDate; d <= ev.EndDate; d = d.AddDays(1)) set.Add(d);

        var labels = set.ToDictionary(d => d, d =>
        {
            var isPre = ev.PreDayDate == d || d < ev.StartDate;
            return d.ToString("dddd dd MMM") + (isPre ? " — pre-day" : string.Empty);
        });

        var cfg = _cfg.Load(_cfgOptions.EventConfigPath);
        foreach (var x in cfg.Volunteer?.ExtraAvailabilityDays ?? new List<VolunteerExtraDay>())
        {
            if (!DateOnly.TryParse(x.Date, out var day)) continue;
            set.Add(day);
            labels[day] = string.IsNullOrWhiteSpace(x.Label)
                ? day.ToString("dddd dd MMM")
                : $"{day:dddd dd MMM} — {x.Label}";
        }

        return set.Select(d => (d, labels[d])).ToList();
    }

    private async Task<Event?> GetActiveEventAsync(CancellationToken ct) =>
        await _db.Events.Where(e => e.IsActive).OrderByDescending(e => e.Id).FirstOrDefaultAsync(ct);

    private static readonly Regex EmailShape = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

    private static bool IsPlausibleEmail(string s) =>
        !string.IsNullOrWhiteSpace(s) && s.Length <= 320 && EmailShape.IsMatch(s);
}
