using System.Security.Cryptography;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain.Evaluation;
using Microsoft.EntityFrameworkCore;

namespace CommunityHub.Core.Evaluation;

/// <summary>
/// §1259 — the WEB KIOSK option: an iPad in a stand doing the job of a built evaluation box.
///
/// <para>🔒 <b>The built-unit design is untouched.</b> A kiosk is an <see cref="EvaluationDevice"/>
/// like any other — same opaque identifier, same per-device key, same room link, same
/// <c>/evaluation/v1/ingest</c> contract, same time attribution, same score, same reports. This
/// service adds exactly two things a browser needs and a box does not: a way to receive its key
/// without anyone typing a 43-character secret on a tablet (PAIRING), and a way to show the
/// attendee what they are rating (CURRENT SESSION).</para>
///
/// <para>🔑 Reusing the device pipeline is not laziness, it is the brief: <i>"the two channels are
/// one instrument … do not build a second scoring model"</i>. A kiosk press and a box press are the
/// same measurement, so they must travel the same path — otherwise the day a report looks wrong
/// there are two ingest paths to reason about instead of one.</para>
/// </summary>
public sealed class EvaluationKioskService
{
    /// <summary>How long an issued pairing code stays usable.</summary>
    /// <remarks>
    /// Long enough to walk 20 iPads round a venue, short enough that a code glimpsed on a screen
    /// is worthless by the next session. Re-issuing is one click, which is what makes a strict
    /// window affordable — a generous expiry would be protecting nobody from anything.
    /// </remarks>
    public static readonly TimeSpan PairingWindow = TimeSpan.FromMinutes(60);

    /// <summary>
    /// The alphabet for a pairing code: no <c>0/O</c>, no <c>1/I/L</c>, no <c>5/S</c>, no <c>8/B</c>.
    /// A code is read off one screen and typed on another, by someone standing up, in a corridor.
    /// </summary>
    private const string CodeAlphabet = "ACDEFGHJKMNPQRTUVWXYZ2346799";

    private readonly CommunityHubDbContext _db;
    private readonly TimeProvider _clock;

    public EvaluationKioskService(CommunityHubDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>A freshly issued code, shown to the organiser ONCE (only its hash is stored).</summary>
    public sealed record IssuedCode(string Code, DateTimeOffset ExpiresAt);

    /// <summary>What a kiosk receives when it redeems a code.</summary>
    public sealed record PairedDevice(
        string SerialNumber, string DeviceKey, string? Label, string? RoomName);

    /// <summary>The session a kiosk should name on screen, or null when nothing is running.</summary>
    /// <param name="ClosesAt">
    /// §1270 — when feedback for this session stops being accepted: the scheduled end plus the
    /// 30-minute grace (<see cref="EvaluationAttribution.GraceMinutes"/>). Shown on the screen so the
    /// close is never a surprise (operator 2026-09-21: *"it should stop 12:30, but at 12:00 it
    /// stopped"*).
    /// </param>
    public sealed record CurrentSession(
        int Id, string Title, string? Speakers, DateTimeOffset? EndsAt, DateTimeOffset? ClosesAt = null);

    /// <summary>The next session in the room, with the window its feedback is collected in.</summary>
    public sealed record NextSession(int Id, string Title, DateTimeOffset OpensAt, DateTimeOffset ClosesAt);

    // ---- pairing -----------------------------------------------------------------------------

    /// <summary>
    /// Issue a one-time pairing code for a device. Any previous outstanding code is replaced —
    /// there is only ever one live code per device, so a re-issue cannot leave an older one usable.
    /// </summary>
    public async Task<IssuedCode?> IssuePairingCodeAsync(int deviceId, CancellationToken ct = default)
    {
        var device = await _db.EvaluationDevices.FirstOrDefaultAsync(d => d.Id == deviceId, ct);
        if (device is null) return null;

        var code = NewCode();
        var now = _clock.GetUtcNow();
        device.IsKiosk = true;
        // 🔒 Hash the NORMALISED form — the same transform redemption applies — or a code issued
        // with its readable dash could never be verified against a typed one without it.
        device.PairingCodeHash = EvaluationIngestService.HashKey(Normalise(code));
        device.PairingCodeExpiresAt = now.Add(PairingWindow);
        device.PairingCodeUsedAt = null;
        device.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        return new IssuedCode(code, device.PairingCodeExpiresAt.Value);
    }

    /// <summary>
    /// Redeem a code: returns the device's identity and a FRESH key, or null when the code is
    /// unknown, expired or already used.
    /// </summary>
    /// <remarks>
    /// 🔒 <b>The key is rotated on every pairing</b>, and the previous one is kept as
    /// <see cref="EvaluationDevice.PreviousKeyHash"/> exactly as a hardware rotation does — so an
    /// iPad that is re-paired (lost, wiped, replaced) invalidates whatever the old browser held,
    /// while a device mid-flush of its offline queue is not cut off in the same instant.
    /// <para>⚠️ Scanned rather than looked up by index: the stored value is a salted hash, so
    /// there is nothing to match on. At 20-100 devices per event that is a trivial scan, and the
    /// candidate set is narrowed to codes that are live and unused first.</para>
    /// </remarks>
    public async Task<PairedDevice?> RedeemAsync(string? code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var normalised = Normalise(code);
        var now = _clock.GetUtcNow();

        var candidates = await _db.EvaluationDevices
            .Where(d => d.PairingCodeHash != null
                        && d.PairingCodeUsedAt == null
                        && d.PairingCodeExpiresAt != null
                        && d.PairingCodeExpiresAt > now)
            .ToListAsync(ct);

        var device = candidates.FirstOrDefault(
            d => EvaluationIngestService.VerifyKey(normalised, d.PairingCodeHash));
        if (device is null) return null;

        var key = EvaluationIngestService.NewKey();
        device.PreviousKeyHash = device.KeyHash;
        device.KeyHash = EvaluationIngestService.HashKey(key);
        device.PairingCodeUsedAt = now;
        device.PairingCodeHash = null;
        device.PairingCodeExpiresAt = null;
        device.IsKiosk = true;
        device.IsActive = true;
        device.KioskLastSeenAt = now;
        device.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        return new PairedDevice(device.SerialNumber, key, device.Label, await RoomNameAsync(device, ct));
    }

    // ---- enrolment link (self-healing, no daily login) ---------------------------------------

    /// <summary>
    /// §1259c — issue (or re-issue) this device's LONG-LIVED enrolment token. The organiser hands
    /// the iPad a URL carrying it; the browser exchanges it for a device key whenever it has none.
    /// </summary>
    /// <remarks>
    /// 🔒 Re-issuing REVOKES the previous link — there is one live token per device, so a lost
    /// iPad is cut off by enrolling its replacement. Returned in clear once; only the hash is kept.
    /// </remarks>
    public async Task<string?> IssueEnrolmentTokenAsync(int deviceId, CancellationToken ct = default)
    {
        var device = await _db.EvaluationDevices.FirstOrDefaultAsync(d => d.Id == deviceId, ct);
        if (device is null) return null;

        var token = EvaluationIngestService.NewKey();
        var now = _clock.GetUtcNow();
        device.IsKiosk = true;
        device.KioskEnrolmentTokenHash = EvaluationIngestService.HashKey(token);
        device.KioskEnrolmentIssuedAt = now;
        device.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        return token;
    }

    /// <summary>§1259c — stop this device re-enrolling at all (the link stops working immediately).</summary>
    public async Task<bool> RevokeEnrolmentTokenAsync(int deviceId, CancellationToken ct = default)
    {
        var device = await _db.EvaluationDevices.FirstOrDefaultAsync(d => d.Id == deviceId, ct);
        if (device is null) return false;
        device.KioskEnrolmentTokenHash = null;
        device.KioskEnrolmentIssuedAt = null;
        device.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// §1259c — exchange an enrolment token for a device key. Unlike a pairing code this is
    /// REPEATABLE: the same bookmark works again after a storage eviction, a wipe, or on a
    /// replacement iPad — which is precisely what stops a kiosk needing a human mid-event.
    /// </summary>
    /// <remarks>
    /// 🔑 <b>Each exchange MINTS a key</b> — it cannot hand back the existing one, because only a
    /// hash of it is stored. The previous key is kept, exactly as every other rotation here does,
    /// and that two-deep history is what makes a swap survivable: an iPad still flushing its
    /// offline queue keeps working while its replacement enrols.
    /// <para>⚠️ Known limit, stated rather than discovered: with THREE devices sharing one link the
    /// oldest falls out of the history and stops flushing. That is a re-enrol away (the bookmark
    /// still works), and sharing one kiosk link across three tablets is not a thing we do.</para>
    /// </remarks>
    public async Task<PairedDevice?> EnrolAsync(string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var candidates = await _db.EvaluationDevices
            .Where(d => d.KioskEnrolmentTokenHash != null && d.IsActive)
            .ToListAsync(ct);

        var device = candidates.FirstOrDefault(
            d => EvaluationIngestService.VerifyKey(token, d.KioskEnrolmentTokenHash));
        if (device is null) return null;

        // 🔑 The device key is stored only as a hash, so it cannot be read back — the enrolment
        // exchange therefore MINTS a key and records it as the current one. The previous key is
        // kept (as everywhere else here) so an iPad still flushing an offline queue is not cut off
        // the instant its replacement enrols.
        var key = EvaluationIngestService.NewKey();
        var now = _clock.GetUtcNow();
        device.PreviousKeyHash = device.KeyHash;
        device.KeyHash = EvaluationIngestService.HashKey(key);
        device.KioskLastSeenAt = now;
        device.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        return new PairedDevice(device.SerialNumber, key, device.Label, await RoomNameAsync(device, ct));
    }

    // ---- self-enrolment: join the queue a human already watches ------------------------------

    /// <summary>
    /// §1259d — how many NEW devices may sit unapproved at once. A queue is a person's inbox; an
    /// unbounded one is a denial of attention, which is the realistic abuse of an endpoint that
    /// grants nothing.
    /// </summary>
    public const int MaxPendingJoins = 60;

    /// <summary>The edition a kiosk belongs to. Resolved server-side — never sent by the tablet.</summary>
    public async Task<int?> ActiveEventIdAsync(CancellationToken ct = default) =>
        await _db.Events.Where(e => e.IsActive).Select(e => (int?)e.Id).FirstOrDefaultAsync(ct);

    /// <summary>
    /// May this identifier join? Always yes for one ALREADY in the queue — that is a device polling
    /// for its answer, and locking it out because other people are also enrolling would strand the
    /// very tablets an organiser is working through.
    /// </summary>
    public async Task<bool> MayJoinAsync(int eventId, string serialNumber, CancellationToken ct = default)
    {
        var known = await _db.EvaluationDeviceProvisionRequests
            .AnyAsync(r => r.EventId == eventId && r.SerialNumber == serialNumber, ct);
        if (known) return true;

        var pending = await _db.EvaluationDeviceProvisionRequests
            .CountAsync(r => r.EventId == eventId && r.Status == EvaluationProvisionStatus.Pending, ct);
        return pending < MaxPendingJoins;
    }

    /// <summary>Flag a just-approved device as a kiosk, so its responses record the right source.</summary>
    /// <summary>
    /// §1303 — one-tap enrolment of an Intune-managed tablet, ALREADY APPROVED (the caller has checked
    /// the organizer PIN and that the name is an Intune device). The Intune name IS the identifier —
    /// no browser-made <c>KIOSK-…</c> id (operator: <i>"i dont see a need to also have a kiosk-&lt;id&gt;"</i>).
    /// Enrolling a name that already exists re-connects that device with a fresh key (the tablet was
    /// wiped or replaced) and keeps its room; the previous key is kept so a queue still flushing is
    /// not cut off.
    /// </summary>
    public async Task<PairedDevice> EnrolManagedAsync(int eventId, string name, CancellationToken ct = default)
    {
        var id = name.Trim();
        var now = _clock.GetUtcNow();
        var key = EvaluationIngestService.NewKey();

        var device = await _db.EvaluationDevices
            .FirstOrDefaultAsync(d => d.EventId == eventId && d.SerialNumber == id, ct);
        if (device is null)
        {
            device = new EvaluationDevice
            {
                EventId = eventId,
                SerialNumber = id,
                Label = id.Length > 200 ? id[..200] : id,
                FirmwareVersion = "web-kiosk/1",
                IsActive = true,
                IsKiosk = true,
                HealthTelemetryEnabled = true,
                KioskLastSeenAt = now,
                CreatedAt = now,
                UpdatedAt = now,
                KeyHash = EvaluationIngestService.HashKey(key),
            };
            _db.EvaluationDevices.Add(device);
        }
        else
        {
            device.PreviousKeyHash = device.KeyHash;
            device.KeyHash = EvaluationIngestService.HashKey(key);
            device.IsKiosk = true;
            device.IsActive = true;
            device.KioskLastSeenAt = now;
            device.UpdatedAt = now;
        }
        await _db.SaveChangesAsync(ct);

        return new PairedDevice(device.SerialNumber, key, device.Label, await RoomNameAsync(device, ct));
    }

    /// <summary>§1303 — which of these names are already enrolled, and in which room.</summary>
    public async Task<Dictionary<string, string?>> EnrolledRoomsAsync(
        int eventId, IReadOnlyCollection<string> names, CancellationToken ct = default)
    {
        var devices = await _db.EvaluationDevices
            .Where(d => d.EventId == eventId && d.IsActive && names.Contains(d.SerialNumber))
            .ToListAsync(ct);
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in devices)
            result[d.SerialNumber] = await EvaluationRoomMap.RoomNameAsync(_db, d, ct);   // §1350 — a handful of tablets
        return result;
    }

    public async Task MarkAsKioskAsync(int deviceId, CancellationToken ct = default)
    {
        var device = await _db.EvaluationDevices.FirstOrDefaultAsync(d => d.Id == deviceId, ct);
        if (device is null || device.IsKiosk) return;
        device.IsKiosk = true;
        device.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>The device's room name, for the kiosk to show after it is approved.</summary>
    /// <summary>§1354 — what this kiosk asks now (its own policy, else the edition's kiosk default).</summary>
    public Task<FeedbackCapturePolicy> CapturePolicyAsync(EvaluationDevice device, CancellationToken ct = default) =>
        FeedbackCapturePolicies.ForDeviceAsync(_db, device, ct);

    /// <summary>§1355 — the symbols this kiosk's rating buttons show (its own, else the kiosk default, else faces).</summary>
    public Task<RatingSymbols> RatingSymbolsAsync(EvaluationDevice device, CancellationToken ct = default) =>
        RatingSymbolChoices.ForDeviceAsync(_db, device, ct);

    /// <remarks>§1350 — the physical room; else the §1349 main-day room, else its pre-day room.</remarks>
    public Task<string?> RoomNameAsync(EvaluationDevice device, CancellationToken ct = default) =>
        EvaluationRoomMap.RoomNameAsync(_db, device, ct);

    /// <summary>
    /// §1259d — a QR of any kiosk URL, as PNG bytes.
    /// </summary>
    /// <remarks>
    /// 🔑 The tablets have no mail and no comfortable keyboard (operator: *"i dont have email etc on
    /// these devices so i cannot move a hard to remember url to the device"*), so the way onto the
    /// page is the iPad's own camera. Same renderer as the session QR codes —
    /// <c>PngByteQRCode</c>, pure managed, no imaging backend.
    /// </remarks>
    public static byte[] RenderQrPng(string url, int pixelsPerModule = 10)
    {
        using var generator = new QRCoder.QRCodeGenerator();
        var data = generator.CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.Q);
        return new QRCoder.PngByteQRCode(data).GetGraphic(pixelsPerModule);
    }

    // ---- what is running now -----------------------------------------------------------------

    /// <summary>
    /// The session running in this device's room right now — the SAME attribution a press gets,
    /// so the screen can never name one session while the vote lands on another.
    /// </summary>
    public async Task<CurrentSession?> CurrentSessionAsync(
        EvaluationDevice device, CancellationToken ct = default)
    {
        var linkedRooms = await EvaluationRoomMap.RoomIdsAsync(_db, device, ct);
        if (linkedRooms.Count == 0) return null;

        var now = _clock.GetUtcNow();
        device.KioskLastSeenAt = now;
        await _db.SaveChangesAsync(ct);

        // §1350 — every schedule room of the unit's physical room, exactly as ingest attributes a press.
        var roomSessions = await _db.EvaluationSessions
            .Where(s => s.EventId == device.EventId && s.RoomId != null && linkedRooms.Contains(s.RoomId.Value))
            .ToListAsync(ct);

        var session = EvaluationAttribution.Resolve(roomSessions, now);
        if (session is null) return null;

        // The display name when we have one, else the address — the screen names a human, and an
        // address is better than a blank where a speaker record is thin.
        var speakers = await _db.EvaluationSessionSpeakers
            .Where(s => s.EvaluationSessionId == session.Id)
            .Select(s => s.DisplayName ?? s.SpeakerEmail)
            .ToListAsync(ct);

        return new CurrentSession(
            session.Id,
            session.Title,
            speakers.Count == 0 ? null : string.Join(", ", speakers),
            session.ScheduledEnd,
            session.CollectionWindowClosesAt);
    }

    /// <summary>
    /// §1262 — the next session in this device's room whose feedback window has not opened yet, so
    /// an idle screen can say when it will next count (operator: *"show timer for when next session
    /// evaluation starts and stops"*). The COLLECTION window, not the scheduled slot: that is when a
    /// press starts landing on the session, which is what the countdown promises.
    /// </summary>
    public async Task<NextSession?> NextSessionAsync(
        EvaluationDevice device, CancellationToken ct = default)
    {
        var linkedRooms = await EvaluationRoomMap.RoomIdsAsync(_db, device, ct);
        if (linkedRooms.Count == 0) return null;

        var now = _clock.GetUtcNow();
        // One room's sessions are a handful — filtered in memory, as CurrentSessionAsync does.
        var next = (await _db.EvaluationSessions
                .Where(s => s.EventId == device.EventId && s.RoomId != null && linkedRooms.Contains(s.RoomId.Value))
                .Select(s => new { s.Id, s.Title, s.CollectionWindowOpensAt, s.CollectionWindowClosesAt })
                .ToListAsync(ct))
            .Where(s => s.CollectionWindowOpensAt > now)
            .OrderBy(s => s.CollectionWindowOpensAt)
            .FirstOrDefault();

        return next is null
            ? null
            : new NextSession(next.Id, next.Title, next.CollectionWindowOpensAt, next.CollectionWindowClosesAt);
    }

    // ---- helpers -----------------------------------------------------------------------------

    /// <summary>A 8-character code in two readable groups, e.g. <c>KDHR-29TQ</c>.</summary>
    private static string NewCode()
    {
        Span<char> chars = stackalloc char[8];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        return $"{new string(chars[..4])}-{new string(chars[4..])}";
    }

    /// <summary>
    /// Upper-cased, with spaces and dashes dropped, so <c>kdhr 29tq</c> and <c>KDHR-29TQ</c> are
    /// the same code. A pairing screen must not fail on punctuation somebody could not see.
    /// </summary>
    public static string Normalise(string code) =>
        new(code.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
