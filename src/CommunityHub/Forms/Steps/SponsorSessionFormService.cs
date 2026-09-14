using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Integrations.Graphics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Forms.Steps;

/// <summary>
/// §292 — render + edit model for the sponsor "Speaking session" wizard step. Title + abstract +
/// up to three speakers (name + email). Bound with an EMPTY prefix by the wizard host.
/// </summary>
public sealed class SponsorSessionModel
{
    public string? Title { get; set; }
    public string? Abstract { get; set; }

    /// <summary>§356 — the chosen agenda track NAME (see <see cref="SponsorSession.Track"/>).</summary>
    public string? Track { get; set; }

    // Up to three speakers (fixed rows keep the wizard's single-form binding simple; "add more" is
    // a later enhancement). At least one name+email is required.
    public string? Speaker1Name { get; set; }
    public string? Speaker1Email { get; set; }
    public string? Speaker2Name { get; set; }
    public string? Speaker2Email { get; set; }
    public string? Speaker3Name { get; set; }
    public string? Speaker3Email { get; set; }

    // §356 (operator 2026-07-26: "form must include link to linkedin for speaker(s) … ability to
    // include a picture upload per speaker"). Neither needs new storage: a sponsor-session speaker
    // IS a real Speaker Participant with a SpeakerProfile (§292), and LinkedIn + PhotoUrl are
    // already profile fields — LinkedIn is even already pushed to Zoho by CreateSpeakerAsync. So
    // this is the sponsor filling in their speaker's own profile on that speaker's behalf, not a
    // parallel copy of it, which is what keeps the two from disagreeing later.
    public string? Speaker1LinkedIn { get; set; }
    public string? Speaker2LinkedIn { get; set; }
    public string? Speaker3LinkedIn { get; set; }

    public IFormFile? Speaker1Photo { get; set; }
    public IFormFile? Speaker2Photo { get; set; }
    public IFormFile? Speaker3Photo { get; set; }

    /// <summary>Display-only: the speakers already saved for this session.</summary>
    public List<SavedSpeaker> CurrentSpeakers { get; set; } = new();
    public sealed record SavedSpeaker(string Name, string Email);

    /// <summary>
    /// §356 — display-only: the photo already stored per speaker ROW (1-based index → URL). A file
    /// input cannot be pre-filled, so without this the form looks like no photo was ever uploaded
    /// and the sponsor re-uploads it on every visit.
    /// </summary>
    public Dictionary<int, string> CurrentPhotoUrls { get; set; } = new();

    /// <summary>
    /// §356 — the track options offered. Sourced from the tracks ALREADY IN USE on this edition's
    /// sessions, deliberately: that is exactly the vocabulary
    /// <c>SessionBackstagePushService.ResolveTrackId</c> matches against (EXACT, case-insensitive,
    /// no fuzzy matching), so a sponsor can only pick a track that will actually resolve on push.
    /// Fetching the list live from Zoho on every form load would be slower and would break the form
    /// whenever Zoho is unreachable — a bad trade for a dropdown.
    /// </summary>
    public IReadOnlyList<string> AvailableTracks { get; set; } = Array.Empty<string>();

    /// <summary>False when no track vocabulary exists yet (nothing imported) — the view then offers
    /// free text rather than an empty dropdown, so the form is never blocked by a missing list.</summary>
    public bool HasTrackOptions => AvailableTracks.Count > 0;
}

/// <summary>
/// §292 — shared submit-service for the sponsor Speaking-session step. Stores the session (title +
/// abstract) IN CEH (not Sessionize) for later Zoho Backstage sync, and turns each entered speaker
/// into a real Speaker <see cref="Participant"/> (funding <see cref="SpeakerFunding.SponsorSelfFunded"/>
/// — dinner + lunch only, NOT ELDK-funded) so they get CEH access, welcome mail, dinner sign-up and
/// the normal speaker details/photo. Self-registers via <see cref="IWizardFormService"/>.
/// </summary>
public sealed class SponsorSessionFormService : IWizardFormService
{
    /// <summary>
    /// §648 — the wizard step key for this form, and the value a sponsor task carries in
    /// <see cref="CommunityHub.Core.Domain.ParticipantTask.FormStepKey"/> to point at it.
    /// </summary>
    /// <remarks>
    /// Named once here so the handler's <c>Key</c>, the config's <c>"form": "session"</c> and the
    /// task-completion query cannot drift apart into three copies of the same string — the drift
    /// that §637 showed is invisible until something quietly stops working.
    /// </remarks>
    public const string SessionFormStepKey = "session";

    private const long MaxPhotoBytes = 5 * 1024 * 1024;
    private static readonly string[] PhotoExts = [".jpg", ".jpeg", ".png", ".webp"];

    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    // §356 — optional + last so the existing unit tests keep constructing this with (db, clock);
    // DI always supplies them in production. With them absent the form still saves everything
    // except the photo FILE, and says so rather than pretending it stored one.
    private readonly Core.Config.EventEditionConfigLoader? _cfg;
    private readonly Core.Config.EventConfigOptions? _cfgOptions;
    private readonly Core.Integrations.SharePointUploadClient? _sp;

    // §734 — the ops notice for a removed speaker. Optional + last so existing constructions keep
    // compiling; null simply means no mail (the removal itself still happens).
    private readonly Core.Email.ZohoChangeNotifier? _zohoChanges;

    public SponsorSessionFormService(
        CommunityHubDbContext db,
        TimeProvider clock,
        Core.Config.EventEditionConfigLoader? cfg = null,
        Core.Config.EventConfigOptions? cfgOptions = null,
        Core.Integrations.SharePointUploadClient? sp = null,
        Core.Email.ZohoChangeNotifier? zohoChanges = null,
        Core.Integrations.DocLibrary.IDocLibraryPathResolver? paths = null)
    {
        _db = db;
        _clock = clock;
        _cfg = cfg;
        _cfgOptions = cfgOptions;
        _sp = sp;
        _zohoChanges = zohoChanges;
        _paths = paths;
    }

    private readonly Core.Integrations.DocLibrary.IDocLibraryPathResolver? _paths;

    /// <summary>
    /// §768.14 — the speaker-photo folder, from the registry. 🔒 It MUST be the same key
    /// <c>SpeakerPhotoService</c> reads and <c>SpeakerPhotoArchiveService</c> writes: this is §764's
    /// "speaker photos are in 1 place only" expressed as ONE key rather than three config lookups
    /// that happen to hold the same string.
    /// </summary>
    private string? SpeakerPhotoFolder() =>
        _paths is not null
        && _paths.TryResolve(Core.Integrations.DocLibrary.DocLibraryPaths.SpeakerPhotos, out var f)
            ? f
            : null;

    private Task<string?> CompanyIdAsync(int participantId, CancellationToken ct) =>
        _db.Participants.Where(p => p.Id == participantId)
            .Select(p => p.SponsorCompanyId).FirstOrDefaultAsync(ct);

    public async Task<SponsorSessionModel> LoadAsync(int eventId, int participantId, CancellationToken ct)
    {
        var model = new SponsorSessionModel();
        var companyId = await CompanyIdAsync(participantId, ct);
        if (companyId is null) return model;

        // §356: offered regardless of whether a session exists yet — a first-time sponsor needs the
        // dropdown on their FIRST visit, which is the only visit where no session row exists.
        model.AvailableTracks = await AvailableTracksAsync(eventId, ct);

        var session = await _db.SponsorSessions.AsNoTracking()
            .Include(s => s.Speakers)
            .FirstOrDefaultAsync(s => s.EventId == eventId && s.SponsorCompanyId == companyId, ct);
        if (session is null) return model;

        model.Title = session.Title;
        model.Abstract = session.Abstract;
        model.Track = session.Track;
        var speakers = session.Speakers.OrderBy(s => s.Id).ToList();
        model.CurrentSpeakers = speakers.Select(s => new SponsorSessionModel.SavedSpeaker(s.Name, s.Email)).ToList();
        // Pre-fill the editable rows from the saved speakers (up to 3).
        if (speakers.Count > 0) { model.Speaker1Name = speakers[0].Name; model.Speaker1Email = speakers[0].Email; }
        if (speakers.Count > 1) { model.Speaker2Name = speakers[1].Name; model.Speaker2Email = speakers[1].Email; }
        if (speakers.Count > 2) { model.Speaker3Name = speakers[2].Name; model.Speaker3Email = speakers[2].Email; }

        // §356: LinkedIn + the stored photo come from each speaker's OWN SpeakerProfile, so if that
        // speaker later edits their own details the sponsor form shows the speaker's version rather
        // than a stale copy the sponsor typed months ago.
        var profileIds = speakers.Where(s => s.ParticipantId is not null)
            .Select(s => s.ParticipantId!.Value).ToList();
        if (profileIds.Count > 0)
        {
            var profiles = await _db.SpeakerProfiles.AsNoTracking()
                .Where(p => p.EventId == eventId && profileIds.Contains(p.ParticipantId))
                .Select(p => new { p.ParticipantId, p.LinkedIn, p.PhotoUrl, p.PhotoSharePointPath })
                .ToDictionaryAsync(p => p.ParticipantId, ct);

            for (var i = 0; i < speakers.Count && i < 3; i++)
            {
                var pid = speakers[i].ParticipantId;
                if (pid is null || !profiles.TryGetValue(pid.Value, out var pr)) continue;

                switch (i)
                {
                    case 0: model.Speaker1LinkedIn = pr.LinkedIn; break;
                    case 1: model.Speaker2LinkedIn = pr.LinkedIn; break;
                    case 2: model.Speaker3LinkedIn = pr.LinkedIn; break;
                }
                // §665 — resolve through the shared rule. Profiles saved BEFORE the fix hold a
                // broken sharepoint.com URL; because they also hold PhotoSharePointPath they now
                // resolve to the proxy, so existing rows repair themselves with no migration.
                var shownPhoto = SpeakerPhotoUrl.Resolve(pr.PhotoUrl, pr.PhotoSharePointPath);
                if (!string.IsNullOrWhiteSpace(shownPhoto)) model.CurrentPhotoUrls[i + 1] = shownPhoto!;
            }
        }
        return model;
    }

    /// <summary>
    /// §356 — the track vocabulary for the dropdown: the DISTINCT non-blank tracks already on this
    /// edition's sessions, alphabetically. See <see cref="SponsorSessionModel.AvailableTracks"/> for
    /// why this rather than a live Zoho call.
    /// </summary>
    private async Task<IReadOnlyList<string>> AvailableTracksAsync(int eventId, CancellationToken ct) =>
        await _db.Sessions.AsNoTracking()
            .Where(s => s.EventId == eventId && s.Track != null && s.Track != "")
            .Select(s => s.Track!)
            .Distinct()
            .OrderBy(t => t)
            .ToListAsync(ct);

    /// <summary>
    /// §734 — remove ONE speaker from the sponsor's session.
    /// </summary>
    /// <remarks>
    /// <para>Operator 2026-07-31: <i>"need option to remove a speaker here - it must set person as
    /// inactive and also send email to info@expertslive.dk so we remember to remove speaker from
    /// zoho manually"</i>, then, asked whether a dual-role person should lose their login:
    /// <i>"remove from sesison only"</i>.</para>
    ///
    /// <para>🔒 <b>The link removal already existed</b> — clearing a speaker's name/email row and
    /// saving drops the <see cref="SponsorSessionSpeaker"/> link (<i>"the Speaker participant row is
    /// left intact"</i>). What was missing is that nobody could FIND it: it is discoverable only if
    /// you guess that blanking three fields means "remove". So this is the same removal with a
    /// button on it, plus the two things he asked for and the old path never did.</para>
    ///
    /// <para>⚠️ <b>DEACTIVATION IS CONDITIONAL, and that is his decision.</b> A sponsor-brought
    /// speaker is very often ALSO that company's sponsor contact; deactivating them to tidy a
    /// session line-up would take away their booth, logo and leads access as a side effect. So the
    /// participant is deactivated ONLY when this session was their sole reason to be in the hub —
    /// no other sponsor session, and not a Sponsor-role contact themselves. Everyone else simply
    /// stops being on the session, which is what <i>"remove from session only"</i> asks for.</para>
    ///
    /// <para>The ops notice rides <see cref="Core.Email.ZohoChangeNotifier"/> rather than a new mail
    /// identity: this IS a "CEH changed something that needs a manual Zoho action" notice, which is
    /// exactly what that notifier is for — so it inherits §736's <c>info@</c> recipient, its
    /// ring-exemption and its never-throws guarantee for free.</para>
    /// </remarks>
    public async Task<string?> RemoveSpeakerAsync(
        int eventId, int actorParticipantId, string speakerEmail, CancellationToken ct)
    {
        var companyId = await CompanyIdAsync(actorParticipantId, ct);
        if (string.IsNullOrWhiteSpace(companyId)) return null;

        var wanted = (speakerEmail ?? string.Empty).Trim();
        if (wanted.Length == 0) return null;

        var session = await _db.SponsorSessions
            .Include(s => s.Speakers)
            .FirstOrDefaultAsync(s => s.EventId == eventId && s.SponsorCompanyId == companyId, ct);
        if (session is null) return null;

        var link = session.Speakers.FirstOrDefault(
            s => string.Equals(s.Email, wanted, StringComparison.OrdinalIgnoreCase));
        if (link is null) return null;

        var name = link.Name;
        var speakerPid = link.ParticipantId;
        _db.SponsorSessionSpeakers.Remove(link);
        await _db.SaveChangesAsync(ct);

        var deactivated = false;
        if (speakerPid is int pid)
        {
            // "Sole reason to be here": no OTHER sponsor-session link, and not a sponsor contact.
            var onAnotherSession = await _db.SponsorSessionSpeakers
                .AnyAsync(s => s.ParticipantId == pid, ct);
            var person = await _db.Participants.FirstOrDefaultAsync(
                p => p.Id == pid && p.EventId == eventId, ct);

            if (person is not null
                && !onAnotherSession
                && person.Role != ParticipantRole.Sponsor
                && person.IsActive)
            {
                person.IsActive = false;
                await _db.SaveChangesAsync(ct);
                deactivated = true;
            }
        }

        if (_zohoChanges is not null)
        {
            var line = $"Removed speaker {name} ({wanted}) from sponsor session '{session.Title}'"
                + (deactivated
                    ? " — their hub login was deactivated (this session was their only role)."
                    : " — their hub login was KEPT (they hold another role or session).")
                + " Delete them in Zoho Backstage if they should no longer appear.";
            // §763 — manualOnly: CEH removed the link on ITS side only. The Backstage speakers API
            // has no delete endpoint either, so nothing changed over there and the mail must not say
            // the hub "wrote" a change and ask him to publish it.
            await _zohoChanges.NotifyAsync("Speakers", new[] { line }, ct, manualOnly: true);
        }

        return name;
    }

    public async Task<WizardStepOutcome> SaveAsync(
        SponsorSessionModel model, int eventId, int participantId, string email,
        ModelStateDictionary modelState, CancellationToken ct)
    {
        var companyId = await CompanyIdAsync(participantId, ct);
        if (companyId is null) return WizardStepOutcome.NotRelevant;

        var title = Trim(model.Title);
        var abstractText = Trim(model.Abstract);
        if (title is null) modelState.AddModelError(nameof(model.Title), "Please enter the session title.");
        if (abstractText is null) modelState.AddModelError(nameof(model.Abstract), "Please enter the session abstract.");

        // Collect the entered speaker rows (name + email), validated.
        var rows = new[]
        {
            (Name: Trim(model.Speaker1Name), Email: NormEmail(model.Speaker1Email)),
            (Name: Trim(model.Speaker2Name), Email: NormEmail(model.Speaker2Email)),
            (Name: Trim(model.Speaker3Name), Email: NormEmail(model.Speaker3Email)),
        };
        var speakers = new List<(string Name, string Email)>();
        for (var i = 0; i < rows.Length; i++)
        {
            var (n, e) = rows[i];
            if (n is null && e is null) continue;          // empty row — skip
            if (n is null || e is null || !LooksLikeEmail(e))
            {
                modelState.AddModelError(string.Empty, $"Speaker {i + 1}: enter both a name and a valid email (or leave both blank).");
                continue;
            }
            if (speakers.Any(s => s.Email == e))
                modelState.AddModelError(string.Empty, $"Speaker {i + 1}: {e} is listed twice.");
            else
                speakers.Add((n, e));
        }
        if (speakers.Count == 0)
            modelState.AddModelError(string.Empty, "Please add at least one speaker (name + email).");

        if (!modelState.IsValid) { model.CurrentSpeakers = speakers.Select(s => new SponsorSessionModel.SavedSpeaker(s.Name, s.Email)).ToList(); return WizardStepOutcome.Invalid; }

        var now = _clock.GetUtcNow();
        var session = await _db.SponsorSessions
            .Include(s => s.Speakers)
            .FirstOrDefaultAsync(s => s.EventId == eventId && s.SponsorCompanyId == companyId, ct);
        if (session is null)
        {
            session = new SponsorSession { EventId = eventId, SponsorCompanyId = companyId, CreatedAt = now };
            _db.SponsorSessions.Add(session);
        }
        session.Title = title;
        session.Abstract = abstractText;
        session.Track = Trim(model.Track);   // §356 — optional; blank simply means "no track yet"
        session.UpdatedAt = now;
        session.LastUpdatedByEmail = email;
        session.SyncedToZohoAt = null;   // content changed → needs a re-sync to Backstage
        await _db.SaveChangesAsync(ct);

        // Reconcile the session's speakers to exactly the entered set: create/find a real Speaker
        // participant (SponsorSelfFunded) for each, and drop any removed.
        var keepIds = new List<int>();
        foreach (var (name, spEmail) in speakers)
        {
            var pid = await EnsureSpeakerParticipantAsync(eventId, companyId, name, spEmail, now, ct);
            var link = session.Speakers.FirstOrDefault(s => s.Email == spEmail);
            if (link is null)
            {
                link = new SponsorSessionSpeaker { SponsorSessionId = session.Id, Name = name, Email = spEmail, ParticipantId = pid, CreatedAt = now };
                _db.SponsorSessionSpeakers.Add(link);
            }
            else { link.Name = name; link.ParticipantId = pid; }
            keepIds.Add(link.Id);
        }
        // Remove links no longer in the entered set (the Speaker participant row is left intact).
        foreach (var stale in session.Speakers.Where(s => !speakers.Any(x => x.Email == s.Email)).ToList())
            _db.SponsorSessionSpeakers.Remove(stale);

        await _db.SaveChangesAsync(ct);

        // §356 — LinkedIn + photo, written onto each speaker's OWN SpeakerProfile. Done AFTER the
        // speaker participants are reconciled above, because the profile only exists once
        // EnsureSpeakerParticipantAsync has created it.
        var linkedIns = new[] { model.Speaker1LinkedIn, model.Speaker2LinkedIn, model.Speaker3LinkedIn };
        var photos = new[] { model.Speaker1Photo, model.Speaker2Photo, model.Speaker3Photo };
        for (var i = 0; i < speakers.Count && i < 3; i++)
        {
            var link = session.Speakers.FirstOrDefault(s => s.Email == speakers[i].Email);
            if (link?.ParticipantId is not { } pid) continue;

            var profile = await _db.SpeakerProfiles
                .FirstOrDefaultAsync(p => p.EventId == eventId && p.ParticipantId == pid, ct);
            if (profile is null) continue;

            // Blank LEAVES the existing value rather than clearing it. The speaker may have filled
            // their own LinkedIn in on their Details page; a sponsor tabbing past an empty box must
            // not wipe it. Clearing stays possible from the speaker's own form, where it is
            // unambiguous that the owner meant it.
            var li = Trim(linkedIns[i]);
            if (li is not null) profile.LinkedIn = li;

            await TryUploadPhotoAsync(photos[i], i + 1, profile, modelState, ct);
        }
        await _db.SaveChangesAsync(ct);

        model.CurrentSpeakers = speakers.Select(s => new SponsorSessionModel.SavedSpeaker(s.Name, s.Email)).ToList();

        // §648 — CLOSE THE TASK THAT SENT THEM HERE. The sponsor "Submit session description" task
        // now links to this step (ParticipantTask.FormStepKey = "session") and deliberately offers
        // no manual tick — so if the form does not complete it, nothing can.
        //
        // 🔒 Only on a CLEAN save. A session saved with a REJECTED PHOTO is not "submitted", and
        // completing it anyway would recreate exactly the lie §603 removed from the upload tasks:
        // a task that reads done while the thing it asked for never arrived.
        if (modelState.IsValid)
        {
            var openTasks = await _db.Tasks
                .Where(t => t.EventId == eventId
                            && t.SponsorCompanyId == companyId
                            && t.FormStepKey == SessionFormStepKey
                            && t.State != TaskState.Done)
                .ToListAsync(ct);

            if (openTasks.Count > 0)
            {
                var completedAt = _clock.GetUtcNow();
                foreach (var t in openTasks)
                {
                    t.State = TaskState.Done;
                    t.CompletedAt = completedAt;
                }
                await _db.SaveChangesAsync(ct);
            }
        }

        // A failed PHOTO does not lose the session: everything above is already saved, so the step
        // re-renders with the error and the sponsor retries just the file. Losing a typed abstract
        // because a JPEG was 6 MB would be the worse outcome by far.
        return modelState.IsValid ? WizardStepOutcome.Advance : WizardStepOutcome.Invalid;
    }

    /// <summary>
    /// §356 — store one posted speaker photo and point the profile at it.
    ///
    /// <para>The operator's proposal was <i>"let sponsor upload picture per speaker and then we
    /// expose the picture as url"</i>, and that is exactly this: the file goes to the SAME
    /// SharePoint drive the sponsor logos use, and the resulting web URL is written to
    /// <c>SpeakerProfile.PhotoUrl</c> — the field the speaker's own Details form sets, so every
    /// existing consumer (programme, graphics, the public catalogue) picks it up with no change.</para>
    ///
    /// <para><b>NOT synced to Zoho, deliberately.</b> §356 established that
    /// <c>ZohoClient.CreateSpeakerAsync</c> has no photo field and that the v3 API rejects unknown
    /// keys with HTTP 400 <i>"Extra param found"</i> — so guessing a key would fail the whole
    /// speaker create, not degrade quietly. The photo is useful inside CEH regardless; the Zoho leg
    /// waits on the API question, which is a fact to look up, not a thing to try.</para>
    /// </summary>
    private async Task TryUploadPhotoAsync(
        IFormFile? file, int rowNumber, SpeakerProfile profile,
        ModelStateDictionary modelState, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return;   // not provided this save

        var field = $"Speaker{rowNumber}Photo";

        if (file.Length > MaxPhotoBytes)
        {
            modelState.AddModelError(field, $"Photo is too large (max {MaxPhotoBytes / (1024 * 1024)} MB).");
            return;
        }
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!PhotoExts.Contains(ext))
        {
            modelState.AddModelError(field, $"Unsupported image type. Allowed: {string.Join(", ", PhotoExts)}.");
            return;
        }

        var sp = _cfg is null || _cfgOptions is null
            ? null
            : _cfg.Load(_cfgOptions.EventConfigPath).SharePoint;
        var photoFolder = SpeakerPhotoFolder();
        if (sp is null || _sp is null || string.IsNullOrWhiteSpace(photoFolder))
        {
            // Honest failure. Silently dropping the file would leave the sponsor believing a photo
            // is on file for their speaker, and nobody would find out until the programme was laid
            // out without it.
            modelState.AddModelError(field,
                "Photo uploads aren't available right now — everything else was saved. "
                + "Please try the photo again later or send it to the organizers.");
            return;
        }

        try
        {
            // Deterministic name per speaker, so re-uploading REPLACES rather than accumulating —
            // the same overwrite contract the SharePoint store documents, and it keeps PhotoUrl
            // stable so anything already pointing at it keeps working. §768.16: composed by the ONE
            // naming function, keyed on the participant id alone.
            var fileName = SpeakerPhotoFileName.Build(profile.ParticipantId, ext);
            // §455 — stream rather than buffer (was MemoryStream + ToArray, i.e. the file in RAM
            // twice before anything reached SharePoint).
            await using var upload = file.OpenReadStream();
            var (_, webUrl, _) = await _sp.UploadFileStreamAsync(
                sp.SiteUrl, sp.DriveName, photoFolder, fileName, upload, file.Length,
                string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
                ct);

            // §665 — store the HUB proxy URL, not the SharePoint one. `webUrl` is a SharePoint
            // document URL: a browser fetching it sends no SharePoint credentials, gets a sign-in
            // page, and renders a broken image. That is the bug the operator reported, and why
            // re-uploading never fixed it — each upload just wrote another unfetchable URL.
            profile.PhotoSharePointPath = fileName;
            profile.PhotoUrl = SpeakerPhotoUrl.Resolve(webUrl, fileName);

            // §1132 — the NAME ALIAS, so the folder is searchable by person and not only by id.
            //
            // 🔒 A SECOND streamed upload, not a buffered copy. `IFormFile.OpenReadStream()` hands
            // back a fresh stream, so §455's rule holds: the file is never held in RAM twice.
            // 🔒 Best-effort and AFTER the real file is stored — the id file is what PhotoUrl points
            // at, and a failure here must not cost the sponsor their upload.
            var aliasName = SpeakerPhotoFileName.BuildAlias(
                profile.ParticipantId, $"{profile.FirstName} {profile.LastName}", ext);
            if (aliasName is not null)
            {
                try
                {
                    await using var aliasUpload = file.OpenReadStream();
                    await _sp.UploadFileStreamAsync(
                        sp.SiteUrl, sp.DriveName, photoFolder, aliasName, aliasUpload, file.Length,
                        string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
                        ct);
                }
                catch (Exception)
                {
                    // Swallowed on purpose: the photo IS stored under its id and everything that
                    // reads it works. The alias is a convenience for a human browsing SharePoint.
                }
            }
        }
        catch (Exception)
        {
            modelState.AddModelError(field,
                "That photo could not be uploaded — everything else was saved. Please try again.");
        }
    }

    // §768.16 — the name sanitiser is DELETED, not left unused: a speaker's name is no longer part
    // of any file name, and a spare sanitiser is how a second convention comes back.

    /// <summary>Find an existing participant by email in the edition, or create a new Speaker
    /// participant (SponsorSelfFunded) linked to the sponsor company. Returns the participant id.</summary>
    private async Task<int> EnsureSpeakerParticipantAsync(
        int eventId, string companyId, string name, string normEmail, DateTimeOffset now, CancellationToken ct)
    {
        var existing = await _db.Participants
            .FirstOrDefaultAsync(p => p.EventId == eventId && p.Email == normEmail, ct);
        if (existing is not null)
        {
            // Don't hijack an existing person's role; just ensure a speaker profile exists so they
            // get the speaker details form. (A sponsor speaker who is already a participant is rare.)
            await EnsureSpeakerProfileAsync(eventId, existing.Id, name, now, ct);
            return existing.Id;
        }

        var (first, last) = SplitName(name);
        var p = new Participant
        {
            EventId = eventId,
            Email = normEmail,
            FullName = name,
            Role = ParticipantRole.Speaker,
            IsActive = true,
            SponsorCompanyId = companyId,
            CreatedAt = now,
        };
        _db.Participants.Add(p);
        await _db.SaveChangesAsync(ct);   // need the id for the speaker profile

        _db.SpeakerProfiles.Add(new SpeakerProfile
        {
            EventId = eventId,
            ParticipantId = p.Id,
            FirstName = first,
            LastName = last,
            SpeakerFunding = SpeakerFunding.SponsorSelfFunded,   // LEGACY audit value (§299 C3)
            // §299 6.1: sponsor-session speakers are AUTO-categorized as Sponsor
            // (§292: NOT ELDK-funded) — the activation hard gate is satisfied, so
            // they stay immediately active exactly as before.
            Category = SpeakerCategory.Sponsor,
            CreatedAt = now,
        });
        await _db.SaveChangesAsync(ct);
        return p.Id;
    }

    private async Task EnsureSpeakerProfileAsync(int eventId, int participantId, string name, DateTimeOffset now, CancellationToken ct)
    {
        var has = await _db.SpeakerProfiles.AnyAsync(s => s.EventId == eventId && s.ParticipantId == participantId, ct);
        if (has) return;
        var (first, last) = SplitName(name);
        _db.SpeakerProfiles.Add(new SpeakerProfile
        {
            EventId = eventId, ParticipantId = participantId,
            FirstName = first, LastName = last,
            SpeakerFunding = SpeakerFunding.SponsorSelfFunded,   // LEGACY audit value (§299 C3)
            Category = SpeakerCategory.Sponsor,                  // §299 6.1: auto-categorized
            CreatedAt = now,
        });
        await _db.SaveChangesAsync(ct);
    }

    private static string? Trim(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    private static string? NormEmail(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim().ToLowerInvariant();
    private static bool LooksLikeEmail(string e)
    {
        var at = e.IndexOf('@');
        return at > 0 && at < e.Length - 1 && !e.Contains(' ');
    }
    private static (string First, string? Last) SplitName(string name)
    {
        var parts = name.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch { 0 => (name, null), 1 => (parts[0], null), _ => (parts[0], parts[1]) };
    }
}
