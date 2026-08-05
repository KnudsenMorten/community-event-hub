using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CommunityHub.Core.Integrations.DocLibrary;

/// <summary>What one logistics run did — the shape the Jobs page and a test both read.</summary>
/// <param name="Unplaced">
/// §774 — people who need a room and are in nobody's rooming list. NOT a skip and NOT a failure:
/// every file the run was asked for was produced correctly. It is the gap those correct files
/// cannot show, because a person placed in no hotel appears in no hotel's file.
/// </param>
public sealed record LogisticsRunResult(
    bool Ran,
    string? InactiveReason,
    int Published,
    int Unchanged,
    int Mailed,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> Unplaced)
{
    public static LogisticsRunResult Inactive(string reason) =>
        new(false, reason, 0, 0, 0, [], [], []);
}

/// <summary>§6.3 — what one "Generate now" did.</summary>
/// <param name="Headline">The regenerated file's headline figure, so the page can show the new
/// number immediately rather than telling the organizer to reload and hope.</param>
public sealed record LogisticsRegenerateResult(bool Ok, string? Error, string? Headline);

/// <summary>
/// §6.4 — THE daily logistics run: build every §3.5 file, publish what changed, mail on the
/// schedule.
/// </summary>
/// <remarks>
/// <para>The producers are pure (data in, bytes + a content key out) and the publisher is stateless
/// (it compares the key it is given). This service is the only place that holds the two together and
/// owns the memory — <see cref="LogisticsFileState"/>.</para>
///
/// <para><b>The schedule, from §6.4:</b> files rebuild DAILY; the food and expo files mail WEEKLY;
/// each hotel's file mails ON CHANGE, and only <b>from three weeks before the event</b>.</para>
///
/// <para>🔒 <b>Every send goes through <see cref="LogisticsRecipients.Resolve"/></b>, which routes
/// everything to the operator's review mailbox until he approves the reports (§770.4). A sender that
/// read an address directly could bypass that, and what it would bypass is "an unreviewed
/// spreadsheet reaches a hotel".</para>
///
/// <para>⚠️ <b>One file's failure never stops the run.</b> Ten other files still have to reach the
/// venue, so failures are collected and reported, not thrown.</para>
/// </remarks>
public sealed class LogisticsRunService
{
    /// <summary>How often the weekly files are mailed. Anything older ⇒ send again.</summary>
    public static readonly TimeSpan WeeklyCadence = TimeSpan.FromDays(7);

    /// <summary>§6.4 — hotel files start mailing three weeks before the event.</summary>
    public const int HotelMailWindowDays = 21;

    private readonly CommunityHubDbContext _db;
    private readonly DocLibraryFilePublisher _publisher;
    private readonly SwagLogisticsProducer _swag;
    private readonly FoodLogisticsProducer _food;
    private readonly LunchLogisticsProducer _lunch;
    private readonly ExpoLogisticsProducer _expo;
    private readonly HotelLogisticsProducer _hotels;
    private readonly LogisticsRecipients _recipients;
    private readonly IEmailSender _email;
    private readonly TimeProvider _clock;
    private readonly ILogger<LogisticsRunService>? _log;

    public LogisticsRunService(
        CommunityHubDbContext db,
        DocLibraryFilePublisher publisher,
        SwagLogisticsProducer swag,
        FoodLogisticsProducer food,
        LunchLogisticsProducer lunch,
        ExpoLogisticsProducer expo,
        HotelLogisticsProducer hotels,
        LogisticsRecipients recipients,
        IEmailSender email,
        TimeProvider? clock = null,
        ILogger<LogisticsRunService>? log = null)
    {
        _db = db;
        _publisher = publisher;
        _swag = swag;
        _food = food;
        _lunch = lunch;
        _expo = expo;
        _hotels = hotels;
        _recipients = recipients;
        _email = email;
        _clock = clock ?? TimeProvider.System;
        _log = log;
    }

    public async Task<LogisticsRunResult> RunAsync(int eventId, CancellationToken ct = default)
    {
        var evt = await _db.Events
            .Where(e => e.Id == eventId)
            .Select(e => new { e.Code, e.StartDate, e.PreDayDate })
            .FirstOrDefaultAsync(ct);

        if (evt is null) return LogisticsRunResult.Inactive("No such edition.");
        if (!_publisher.CanPublish)
            return LogisticsRunResult.Inactive("The document library is not writable on this host.");

        var shortName = evt.Code;
        var now = _clock.GetUtcNow();
        var published = 0;
        var unchanged = 0;
        var mailed = 0;
        var skipped = new List<string>();
        var failures = new List<string>();

        var states = await _db.LogisticsFileStates
            .Where(s => s.EventId == eventId)
            .ToListAsync(ct);
        var byName = states.ToDictionary(s => s.FileName, StringComparer.OrdinalIgnoreCase);

        var plan = await BuildPlanAsync(eventId, shortName, evt.StartDate, evt.PreDayDate, now, ct);
        var batch = plan.Batch;
        var unplaced = plan.Unplaced;
        skipped.AddRange(plan.Skipped);

        // ---- publish, then decide the mail --------------------------------------------------
        foreach (var (pathKey, file, mailKind, intendedTo) in batch)
        {
            byName.TryGetValue(file.FileName, out var state);

            var result = await _publisher.PublishAsync(pathKey, file, state?.ContentKey, ct);
            if (!result.Ok)
            {
                failures.Add($"{file.FileName}: {result.Error}");
                continue;
            }

            if (result.Written) published++; else unchanged++;

            state = Upsert(state, eventId, pathKey, file, now, result.WebUrl);

            var due = ShouldMail(mailKind, result.Changed, state, now);
            if (!due) continue;

            var to = _recipients.Resolve(intendedTo);
            try
            {
                await SendAsync(to, file, intendedTo, ct);
                state.LastMailedAt = now;
                state.LastMailedTo = to;
                mailed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A file that published but could not be mailed is NOT a failed publish. Reported,
                // and the state keeps its old LastMailedAt so the next run tries again.
                failures.Add($"{file.FileName}: published, but the mail failed — {ex.Message}");
            }
        }

        await RecordRunAsync(eventId, now, published, unchanged, mailed, skipped, failures, unplaced, ct);

        await _db.SaveChangesAsync(ct);

        _log?.LogInformation(
            "§6.4 logistics run: {Published} published, {Unchanged} unchanged, {Mailed} mailed, "
            + "{Skipped} skipped, {Failed} failed, {Unplaced} awaiting a hotel.",
            published, unchanged, mailed, skipped.Count, failures.Count, unplaced.Count);

        return new LogisticsRunResult(
            true, null, published, unchanged, mailed, skipped, failures, unplaced);
    }

    /// <summary>Everything one run intends to do, before anything is written.</summary>
    private sealed record LogisticsPlan(
        List<(string PathKey, GeneratedFile File, LogisticsMailKind Mail, string? To)> Batch,
        IReadOnlyList<string> Skipped,
        IReadOnlyList<string> Unplaced);

    /// <summary>
    /// Build every §3.5 file and decide which schedule each is on. No writes, no mail.
    /// </summary>
    /// <remarks>
    /// 🔒 <b>The daily run and the §6.3 "Generate now" button share THIS.</b> A button that built
    /// its files a second way would be a second answer to "how many polos?" — the §767 failure with
    /// a vendor invoice attached. The button re-uses the plan and publishes one entry of it.
    /// </remarks>
    private async Task<LogisticsPlan> BuildPlanAsync(
        int eventId, string shortName, DateOnly startDate, DateOnly? preDayDate,
        DateTimeOffset now, CancellationToken ct)
    {
        var batch = new List<(string PathKey, GeneratedFile File, LogisticsMailKind Mail, string? To)>();
        var skipped = new List<string>();

        foreach (var f in await _swag.BuildAllAsync(eventId, shortName, ct))
        {
            // Swag has no mail schedule in §3.5 — the files exist for the organizer to order from.
            batch.Add((SwagPathKeyFor(f.FileName), f, LogisticsMailKind.None, null));
        }

        foreach (var f in await _food.BuildAllAsync(eventId, shortName, ct))
            batch.Add((DocLibraryPaths.BcFood, f, LogisticsMailKind.Weekly, _recipients.VenueOperations));

        foreach (var f in await _lunch.BuildAllAsync(eventId, shortName, ct))
            batch.Add((DocLibraryPaths.BcFood, f, LogisticsMailKind.Weekly, _recipients.VenueOperations));

        var expo = await _expo.BuildAllAsync(shortName, ct);
        skipped.AddRange(expo.Skipped);
        foreach (var f in expo.Files)
        {
            // §3.5: TV weekly to the venue, furniture weekly to the logistics organizer.
            var to = f.FileName.Contains("-tv", StringComparison.OrdinalIgnoreCase)
                ? _recipients.VenueOperations
                : _recipients.LogisticsOrganizer;
            batch.Add((DocLibraryPaths.BcExpo, f, LogisticsMailKind.Weekly, to));
        }

        // ---- the hotels: mailed ON CHANGE, and only inside the window ----------------------
        var daysToEvent = preDayDate is { } pre
            ? pre.DayNumber - DateOnly.FromDateTime(now.UtcDateTime).DayNumber
            : startDate.DayNumber - DateOnly.FromDateTime(now.UtcDateTime).DayNumber;
        var hotelWindowOpen = daysToEvent <= HotelMailWindowDays;

        foreach (var h in await _hotels.BuildAllAsync(eventId, shortName, ct))
        {
            batch.Add((
                DocLibraryPaths.Hotel, h.File,
                hotelWindowOpen ? LogisticsMailKind.OnChange : LogisticsMailKind.None,
                h.ContactEmail));
        }

        // 🔒 §774 — WHO ASKED FOR A ROOM AND IS IN NOBODY'S LIST.
        // The rooming lists are built from placements, so a person who needs a bed but has been
        // placed nowhere is, by construction, in no file — and an empty hotel folder then reads
        // exactly like "no hotel work to do". This report is the only thing that tells the two
        // apart, and until §774 it was written, tested, and called by nothing.
        var unplaced = await _hotels.UnplacedAsync(eventId, ct);

        return new LogisticsPlan(batch, skipped, unplaced);
    }

    /// <summary>
    /// §6.3 "Generate now" — rebuild ONE artifact and overwrite it in the library.
    /// </summary>
    /// <remarks>
    /// <para>🔒 <b>It never mails.</b> The work order puts this button "alongside the daily automatic
    /// run", and the daily run owns the schedule. A button that also sent would let anybody mail an
    /// external venue contact by clicking twice, outside the weekly cadence and outside the review
    /// gate's intent.</para>
    ///
    /// <para>⚠️ <b>It forces the write even when nothing changed</b> — that is the whole point: the
    /// button exists to repair a file somebody edited or deleted by hand. But the result is still
    /// not reported as a CHANGE, so it cannot trigger the on-change hotel mail on the next run.</para>
    /// </remarks>
    public async Task<LogisticsRegenerateResult> RegenerateOneAsync(
        int eventId, string fileName, CancellationToken ct = default)
    {
        var evt = await _db.Events
            .Where(e => e.Id == eventId)
            .Select(e => new { e.Code, e.StartDate, e.PreDayDate })
            .FirstOrDefaultAsync(ct);

        if (evt is null) return new LogisticsRegenerateResult(false, "No such edition.", null);
        if (!_publisher.CanPublish)
            return new LogisticsRegenerateResult(
                false, "The document library is not writable on this host.", null);

        var now = _clock.GetUtcNow();
        var plan = await BuildPlanAsync(eventId, evt.Code, evt.StartDate, evt.PreDayDate, now, ct);

        var entry = plan.Batch.FirstOrDefault(
            b => string.Equals(b.File.FileName, fileName, StringComparison.OrdinalIgnoreCase));

        if (entry.File is null)
        {
            // ⚠️ NOT an error state to hide. A file the producers no longer build is a real answer —
            // e.g. the last person wanting a Credly badge was deactivated — and saying "regenerated"
            // would be a lie about a file that is not coming back.
            return new LogisticsRegenerateResult(
                false, $"'{fileName}' is not one of the files this edition currently produces.", null);
        }

        var state = await _db.LogisticsFileStates
            .FirstOrDefaultAsync(s => s.EventId == eventId && s.FileName == entry.File.FileName, ct);

        var result = await _publisher.PublishAsync(
            entry.PathKey, entry.File, state?.ContentKey, ct, force: true);

        if (!result.Ok)
            return new LogisticsRegenerateResult(false, result.Error, null);

        Upsert(state, eventId, entry.PathKey, entry.File, now, result.WebUrl);
        await _db.SaveChangesAsync(ct);

        _log?.LogInformation(
            "§6.3 logistics: '{File}' regenerated on request ({Headline}).",
            entry.File.FileName, entry.File.Headline);

        return new LogisticsRegenerateResult(true, null, entry.File.Headline);
    }

    /// <summary>
    /// §6.3 — record what this run did, so the organizer page can show the last automatic run's
    /// status and any error without anybody reading a log.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Written on EVERY run, including the ones that changed nothing.</b> A page that only
    /// records eventful runs cannot tell "the job has not run since Friday" from "the job ran and
    /// had nothing to do", and those two need opposite responses.
    /// </remarks>
    private async Task RecordRunAsync(
        int eventId, DateTimeOffset now, int published, int unchanged, int mailed,
        IReadOnlyList<string> skipped, IReadOnlyList<string> failures,
        IReadOnlyList<string> unplaced, CancellationToken ct)
    {
        var summary = await _db.LogisticsRunSummaries
            .FirstOrDefaultAsync(s => s.EventId == eventId, ct);

        if (summary is null)
        {
            summary = new LogisticsRunSummary { EventId = eventId };
            _db.LogisticsRunSummaries.Add(summary);
        }

        var problems = new List<string>();
        problems.AddRange(failures.Select(f => $"Failed — {f}"));
        problems.AddRange(skipped.Select(s => $"Skipped — {s}"));
        if (unplaced.Count > 0)
        {
            problems.Add(
                $"Awaiting a hotel — {unplaced.Count} person(s) need a room and are placed nowhere: "
                + string.Join(", ", unplaced));
        }

        summary.RanAt = now;
        summary.Published = published;
        summary.Unchanged = unchanged;
        summary.Mailed = mailed;
        summary.Problems = problems.Count == 0 ? null : string.Join("\n", problems);

        // 🔒 Only a FAILURE makes a run not-Ok. A skip and an unplaced guest are things somebody
        // should see, but they are not the job breaking — colouring them red would train the
        // organizer to ignore the colour.
        summary.Ok = failures.Count == 0;
    }

    /// <summary>Which schedule a file is on.</summary>
    private enum LogisticsMailKind
    {
        /// <summary>Published, never mailed — the organizer opens it in the library.</summary>
        None = 0,

        /// <summary>Mailed once a week whether or not it changed.</summary>
        Weekly = 1,

        /// <summary>Mailed only when its content changed (the hotels).</summary>
        OnChange = 2,
    }

    /// <remarks>
    /// 🔒 <b>The two schedules answer different questions.</b> WEEKLY is "here is the current
    /// picture" and must go out even when nothing moved — silence would read as "no update yet"
    /// rather than "no change". ON CHANGE is the opposite: a hotel hears from us only when their
    /// rooming list actually moved, because a weekly identical list teaches them to ignore us.
    /// </remarks>
    private static bool ShouldMail(
        LogisticsMailKind kind, bool changed, LogisticsFileState state, DateTimeOffset now) =>
        kind switch
        {
            LogisticsMailKind.Weekly =>
                state.LastMailedAt is not { } last || now - last >= WeeklyCadence,
            LogisticsMailKind.OnChange => changed,
            _ => false,
        };

    private LogisticsFileState Upsert(
        LogisticsFileState? state, int eventId, string pathKey, GeneratedFile file,
        DateTimeOffset now, string? webUrl)
    {
        if (state is null)
        {
            state = new LogisticsFileState
            {
                EventId = eventId, PathKey = pathKey, FileName = file.FileName,
            };
            _db.LogisticsFileStates.Add(state);
        }

        state.ContentKey = file.ContentKey;
        state.PublishedAt = now;
        state.Headline = file.Headline;

        // ⚠️ Only overwrite the link when we actually LEARNED one. A run on a host that cannot read
        // the library would otherwise erase a good link with null, and the §6.3 page would lose the
        // ability to open a file that is still perfectly there.
        if (!string.IsNullOrWhiteSpace(webUrl)) state.WebUrl = webUrl;

        return state;
    }

    private async Task SendAsync(
        string to, GeneratedFile file, string? intendedFor, CancellationToken ct)
    {
        var body =
            $"<p>The attached <strong>{System.Net.WebUtility.HtmlEncode(file.FileName)}</strong> "
            + "was generated by the Community Event Hub.</p>"
            // 🔑 The redirect explains itself IN the mail. A file that arrives in his inbox instead
            // of the venue's must say why, or it reads as a misdelivery.
            + $"<p style=\"color:#6b7280;font-size:13px;\">{System.Net.WebUtility.HtmlEncode(_recipients.ExplainFor(intendedFor))}</p>";

        await _email.SendWithAttachmentsAsync(
            to,
            $"{file.FileName}",
            body,
            [new EmailAttachment(file.FileName, file.Content, file.ContentType)],
            ct);
    }

    /// <summary>Which swag folder a generated file belongs in — the name carries it.</summary>
    private static string SwagPathKeyFor(string fileName) =>
        fileName.Contains("-credly", StringComparison.OrdinalIgnoreCase) ? DocLibraryPaths.SwagCredly
        : fileName.Contains("-polo", StringComparison.OrdinalIgnoreCase) ? DocLibraryPaths.SwagPolo
        : DocLibraryPaths.SwagAward;
}
