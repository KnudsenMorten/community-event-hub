using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using CommunityHub.Core.Data;
using CommunityHub.Core.Domain;
using CommunityHub.Core.Reminders;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

// ===========================================================================
//  REQUIREMENTS §221 / §223 — concurrent master-class seat-allocation simulation
//  against a REAL dev/prod Azure SQL database (the ONLY supported target).
//
//  Proves the §218/§219 OPTIMISTIC atomic seat-claim (MasterClassSignupService.
//  TryClaimSeatAsync — a guarded conditional UPDATE on the Session row, with the
//  capacity COUNT read under WITH (UPDLOCK, HOLDLOCK) so it is RCSI-immune) is
//  correct under GENUINE wall-clock concurrency against a REAL Azure SQL engine:
//    * N concurrent signups race for a few LOW-capacity master classes;
//    * confirmed == capacity EXACTLY, zero oversell of the last seat;
//    * the rest waitlist, no duplicate signups, counts conserved;
//    * concurrent cancellations free seats that waitlisted users are promoted
//      into — exactly once each, no double-promote, never above capacity;
//    * no deadlock / exception storm.
//
//  Each simulated user gets its OWN DbContext + SQL connection, so they
//  genuinely race on real row locks (unlike the §220 shared-connection SQLite
//  test, which serializes).
//
//  PROD/DEV-ONLY POLICY (§223)
//  ---------------------------
//  Per operator ("delete sql express. i don't accept tests outside of either
//  dev or prod"): this harness runs ONLY against an EXISTING dev/prod Azure SQL
//  database (no local SQL Express, no throwaway CREATE/DROP DATABASE, no SQLite
//  fallback). RATIONALE: SQL Express has READ_COMMITTED_SNAPSHOT (RCSI) OFF,
//  which MASKED the §222 oversell bug — only the real Azure SQL engine (RCSI ON)
//  exercises the seat guard faithfully. It connects to the existing DB with an
//  AAD access token (no CREATE/DROP DATABASE rights needed — the deploy SPN is
//  only a read/write DB user). All work happens in an ISOLATED, clearly-synthetic
//  test event (never EventId = 1); every seeded row is tagged to that event id and
//  a FK-safe cleanup deletes + VERIFIES zero rows remain.
//      env CEH_LOADSIM_AZURE_CS  (required) -> the existing dev/prod-DB connection string
//      env CEH_LOADSIM_TOKEN     (required) -> an AAD access token for database.windows.net
//      env CEH_LOADSIM_EVENTID              -> preferred synthetic test event id (default 900001)
//      env CEH_LOADSIM_MAXPOOL              -> connection-pool cap (default 100)
//      env CEH_LOADSIM_CAP_A/_B, _USERS_A/_B-> class caps / racer counts
//  If CEH_LOADSIM_AZURE_CS / CEH_LOADSIM_TOKEN are not both set, the harness
//  prints a clear message and exits 0 (it never falls back to any local engine).
// ===========================================================================

// --- knobs (env-overridable) -----------------------------------------------
int classACapacity = EnvInt("CEH_LOADSIM_CAP_A", 5);
int classBCapacity = EnvInt("CEH_LOADSIM_CAP_B", 10);
int contendersA = EnvInt("CEH_LOADSIM_USERS_A", 200);
int contendersB = EnvInt("CEH_LOADSIM_USERS_B", 200);
int totalUsers = contendersA + contendersB;

Console.WriteLine("============================================================");
Console.WriteLine(" §221/§223 Master-Class concurrency simulation (dev/prod Azure SQL only)");
Console.WriteLine("============================================================");

var azureCs = Environment.GetEnvironmentVariable("CEH_LOADSIM_AZURE_CS");
var azureToken = Environment.GetEnvironmentVariable("CEH_LOADSIM_TOKEN");

if (string.IsNullOrWhiteSpace(azureCs) || string.IsNullOrWhiteSpace(azureToken))
{
    Console.WriteLine();
    Console.WriteLine("This harness runs ONLY against an existing dev/prod Azure SQL database (§223 prod-only");
    Console.WriteLine("policy — the SQL Express path was removed because its RCSI-OFF engine masked a real bug).");
    Console.WriteLine("It does NOT fall back to any local SQL Server or SQLite engine.");
    Console.WriteLine();
    Console.WriteLine("To run it, set BOTH:");
    Console.WriteLine("  CEH_LOADSIM_AZURE_CS = the dev/prod Azure SQL connection string (no auth in the string)");
    Console.WriteLine("  CEH_LOADSIM_TOKEN    = an AAD access token for https://database.windows.net/");
    Console.WriteLine("e.g.  $env:CEH_LOADSIM_TOKEN = (az account get-access-token --resource https://database.windows.net/ --query accessToken -o tsv)");
    Console.WriteLine();
    Console.WriteLine(string.IsNullOrWhiteSpace(azureCs)
        ? "  (CEH_LOADSIM_AZURE_CS is not set.)"
        : "  (CEH_LOADSIM_TOKEN is not set.)");
    return 0;   // not a failure — nothing to run without a dev/prod target
}

return await RunAzureAsync(azureCs, azureToken);

// ===========================================================================
//  AZURE EXISTING-DB MODE — run inside an existing dev/prod Azure SQL DB,
//  isolated synthetic test event (the ONLY supported target, §223)
// ===========================================================================
async Task<int> RunAzureAsync(string cs, string token)
{
    int preferredEventId = EnvInt("CEH_LOADSIM_EVENTID", 900001);
    int maxPool = EnvInt("CEH_LOADSIM_MAXPOOL", 100);

    // Per-user connection string: a capped pool (the prod tier can't take 400 raw
    // connections at once) + a generous connect timeout so the 400 racers QUEUE
    // briefly on the pool rather than erroring. Each signup is a few ms, so the
    // pool drains continuously and contention on the few low-cap rows is still
    // genuine wall-clock concurrency. NOTE: no auth in the string — the token is
    // attached to every physical connection by the interceptor below.
    var userCs = new SqlConnectionStringBuilder(cs)
    {
        MaxPoolSize = Math.Max(20, maxPool),
        MinPoolSize = 10,
        ConnectTimeout = 30,
        Encrypt = true,
    }.ConnectionString;

    var interceptor = new AadTokenInterceptor(token);

    // Mirror PROD (src/CommunityHub/Program.cs): EnableRetryOnFailure with the SAME
    // params so InTxAsync's CreateExecutionStrategy() returns the RETRYING strategy
    // §218 relies on — concurrent seat-frees can deadlock at the DB and prod absorbs
    // that via this retry policy.
    DbContextOptions<CommunityHubDbContext> Options() =>
        new DbContextOptionsBuilder<CommunityHubDbContext>()
            .UseSqlServer(userCs, o =>
            {
                o.CommandTimeout(120);
                o.EnableRetryOnFailure(maxRetryCount: 6, maxRetryDelay: TimeSpan.FromSeconds(30), errorNumbersToAdd: null);
            })
            .AddInterceptors(interceptor)
            .Options;

    CommunityHubDbContext NewDb() => new(Options());

    // Resolve real table/schema names from the EF model (no hard-coded names).
    string evTable, evSchema, sTable, aTable, mcsTable, mcsetTable;
    using (var probe = NewDb())
    {
        string Tbl<T>() => probe.Model.FindEntityType(typeof(T))!.GetTableName()!;
        string Sch<T>() => probe.Model.FindEntityType(typeof(T))!.GetSchema() ?? "dbo";
        evTable = Tbl<Event>(); evSchema = Sch<Event>();
        sTable = Tbl<Session>(); aTable = Tbl<Attendee>();
        mcsTable = Tbl<MasterClassSignup>(); mcsetTable = Tbl<MasterClassSettings>();
    }
    string Q(string schema, string table) => $"[{schema}].[{table}]";
    var events = Q(evSchema, evTable);
    var sessions = Q(evSchema, sTable);
    var attendees = Q(evSchema, aTable);
    var signups = Q(evSchema, mcsTable);
    var settings = Q(evSchema, mcsetTable);

    int testEventId = preferredEventId;
    bool eventSeeded = false;
    int? originalMaxId = null;
    bool forcedIdReseededIdentity = false;

    var overallPass = true;
    var findings = new List<string>();

    try
    {
        // --- 0. diagnostics + connectivity ---------------------------------
        await using (var c = await OpenAsync(userCs, token))
        {
            Console.WriteLine("MODE      : AZURE existing-DB (no CREATE/DROP DATABASE; isolated test event)");
            Console.WriteLine("DB target : " + new SqlConnectionStringBuilder(cs).DataSource
                              + " / " + new SqlConnectionStringBuilder(cs).InitialCatalog);
            Console.WriteLine("Auth      : AAD access token (SqlConnection.AccessToken)");
            Console.WriteLine("Identity  : login=" + await ScalarStrAsync(c, "SELECT SUSER_SNAME()")
                              + "  dbuser=" + await ScalarStrAsync(c, "SELECT USER_NAME()"));
            Console.WriteLine("Edition   : " + await ScalarStrAsync(c, "SELECT CAST(DATABASEPROPERTYEX(DB_NAME(),'Edition') AS nvarchar(128))")
                              + " / " + await ScalarStrAsync(c, "SELECT CAST(DATABASEPROPERTYEX(DB_NAME(),'ServiceObjective') AS nvarchar(128))"));
            Console.WriteLine("Roles     : db_owner=" + await ScalarStrAsync(c, "SELECT IS_ROLEMEMBER('db_owner')")
                              + " db_ddladmin=" + await ScalarStrAsync(c, "SELECT IS_ROLEMEMBER('db_ddladmin')")
                              + " db_datawriter=" + await ScalarStrAsync(c, "SELECT IS_ROLEMEMBER('db_datawriter')"));
            // RCSI matters for §218: under READ_COMMITTED_SNAPSHOT (Azure SQL default,
            // OFF on SQL Express) a READ COMMITTED count-subquery reads a pre-statement
            // SNAPSHOT, so the optimistic seat-count guard can pass on stale rows.
            Console.WriteLine("Isolation : read_committed_snapshot_on="
                              + await ScalarStrAsync(c, "SELECT CAST(is_read_committed_snapshot_on AS int) FROM sys.databases WHERE name = DB_NAME()")
                              + " snapshot_isolation_state="
                              + await ScalarStrAsync(c, "SELECT snapshot_isolation_state_desc FROM sys.databases WHERE name = DB_NAME()"));

            // --- 1. choose an ISOLATED, clearly-synthetic test event id -----
            var maxId = await ScalarIntAsync(c, $"SELECT ISNULL(MAX(Id),0) FROM {events}");
            originalMaxId = maxId;
            Console.WriteLine($"Events    : MAX(Id) currently = {maxId}");
            var exists = await ScalarIntAsync(c, $"SELECT COUNT(*) FROM {events} WHERE Id = {testEventId}");
            if (exists != 0)
            {
                Console.WriteLine($"ABORT: preferred synthetic test event id {testEventId} already EXISTS — refusing to reuse.");
                return 2;
            }
            Console.WriteLine($"Test event: Id = {testEventId} (synthetic, does NOT exist — safe to seed)");
        }

        // --- 2. seed the isolated Event row --------------------------------
        // Prefer a FORCED synthetic high id (IDENTITY_INSERT) for traceability;
        // fall back to a natural identity id if the SPN lacks ALTER permission.
        try
        {
            await SeedEventForcedIdAsync(userCs, token, events, testEventId);
            eventSeeded = true;
            forcedIdReseededIdentity = testEventId > (originalMaxId ?? 0);
            Console.WriteLine($"Seed event: forced Id={testEventId} via IDENTITY_INSERT (traceable synthetic id)");
        }
        catch (Exception ex)
        {
            Console.WriteLine("Seed event: IDENTITY_INSERT not permitted (" + ex.GetType().Name + ": "
                              + ex.Message.Split('\n')[0].Trim() + ")");
            // Fallback: let EF assign the next natural identity id. Still brand-new
            // and fully isolated (verified > prior MAX), just not the 900001 literal.
            using var db = NewDb();
            var ev = NewLoadSimEvent();
            db.Events.Add(ev);
            await db.SaveChangesAsync();
            testEventId = ev.Id;
            eventSeeded = true;
            if (testEventId <= (originalMaxId ?? 0))
            {
                Console.WriteLine($"ABORT: fallback event id {testEventId} is not greater than prior MAX {originalMaxId} — unexpected, refusing to continue.");
                return 2;
            }
            Console.WriteLine($"Seed event: fell back to natural identity Id={testEventId} (still isolated, > prior MAX {originalMaxId})");
        }

        // --- 3. seed 2 low-cap master classes + attendee pools -------------
        int classA, classB;
        using (var db = NewDb())
        {
            var a = new Session { EventId = testEventId, Title = "MC Alpha", SessionizeId = "loadsim-a-" + Guid.NewGuid().ToString("N")[..8], Type = SessionType.MasterClass, MasterClassCapacity = classACapacity };
            var bClass = new Session { EventId = testEventId, Title = "MC Bravo", SessionizeId = "loadsim-b-" + Guid.NewGuid().ToString("N")[..8], Type = SessionType.MasterClass, MasterClassCapacity = classBCapacity };
            db.Sessions.Add(a); db.Sessions.Add(bClass);
            await db.SaveChangesAsync();
            classA = a.Id; classB = bClass.Id;
        }
        var attendeesA = await AddAttendeesAsync(NewDb, testEventId, "a", contendersA);
        var attendeesB = await AddAttendeesAsync(NewDb, testEventId, "b", contendersB);
        Console.WriteLine($"Seeded    : 1 event, 2 master classes (cap {classACapacity} + {classBCapacity}), {totalUsers} attendees");
        Console.WriteLine();

        // --- 4. run the shared concurrency phases --------------------------
        (overallPass, findings) = await RunPhasesAsync(
            NewDb, testEventId, classA, classB, attendeesA, attendeesB,
            classACapacity, classBCapacity, contendersA, contendersB);
    }
    finally
    {
        // --- 5. CLEANUP — FK-safe row delete + VERIFY zero rows remain -----
        if (eventSeeded)
        {
            Console.WriteLine();
            Console.WriteLine("CLEANUP (Azure existing-DB) — deleting every seeded row for test event " + testEventId);
            try
            {
                SqlConnection.ClearAllPools();
                await using var c = await OpenAsync(userCs, token);
                // FK-safe order: signups (NoAction FKs to Session/Attendee) first,
                // then Sessions, Attendees, settings, finally the Event root.
                var dSig = await ExecAsync(c, $"DELETE FROM {signups}  WHERE EventId = {testEventId}");
                var dSet = await ExecAsync(c, $"DELETE FROM {settings} WHERE EventId = {testEventId}");
                var dSes = await ExecAsync(c, $"DELETE FROM {sessions} WHERE EventId = {testEventId}");
                var dAtt = await ExecAsync(c, $"DELETE FROM {attendees} WHERE EventId = {testEventId}");
                var dEv = await ExecAsync(c, $"DELETE FROM {events}   WHERE Id = {testEventId}");
                Console.WriteLine($"  deleted: signups={dSig} settings={dSet} sessions={dSes} attendees={dAtt} event={dEv}");

                // Restore the identity counter if a forced high id reseeded it, so the
                // table's next natural id is exactly what it would have been (no prod
                // side-effect). Best-effort; a benign identity gap is harmless if it fails.
                if (forcedIdReseededIdentity)
                {
                    try
                    {
                        var remainingMax = await ScalarIntAsync(c, $"SELECT ISNULL(MAX(Id),0) FROM {events}");
                        var reseedTo = Math.Min(originalMaxId ?? 0, remainingMax);
                        await ExecAsync(c, $"DBCC CHECKIDENT('{evSchema}.{evTable}', RESEED, {reseedTo})");
                        Console.WriteLine($"  identity: reseeded {evTable} back to {reseedTo} (pre-run state restored)");
                    }
                    catch (Exception rex)
                    {
                        Console.WriteLine("  identity: reseed skipped (" + rex.GetType().Name + ": "
                                          + rex.Message.Split('\n')[0].Trim() + ") — benign identity gap only");
                    }
                }

                // VERIFY: re-query every touched table; ALL must be zero.
                var vSig = await ScalarIntAsync(c, $"SELECT COUNT(*) FROM {signups}  WHERE EventId = {testEventId}");
                var vSet = await ScalarIntAsync(c, $"SELECT COUNT(*) FROM {settings} WHERE EventId = {testEventId}");
                var vSes = await ScalarIntAsync(c, $"SELECT COUNT(*) FROM {sessions} WHERE EventId = {testEventId}");
                var vAtt = await ScalarIntAsync(c, $"SELECT COUNT(*) FROM {attendees} WHERE EventId = {testEventId}");
                var vEv = await ScalarIntAsync(c, $"SELECT COUNT(*) FROM {events}   WHERE Id = {testEventId}");
                var leftover = vSig + vSet + vSes + vAtt + vEv;
                Console.WriteLine($"  VERIFY remaining for event {testEventId}: signups={vSig} settings={vSet} sessions={vSes} attendees={vAtt} event={vEv}");
                Console.WriteLine($"  CLEANUP {(leftover == 0 ? "VERIFIED — 0 rows remain" : "FAILED — " + leftover + " ROWS LEFT")}");
                if (leftover != 0) { overallPass = false; findings.Add($"cleanup left {leftover} rows for event {testEventId}"); }
            }
            catch (Exception ex)
            {
                Console.WriteLine("CLEANUP ERROR: " + ex.GetType().Name + ": " + ex.Message.Split('\n')[0].Trim());
                overallPass = false; findings.Add("cleanup threw: " + ex.Message.Split('\n')[0].Trim());
            }
        }
    }

    Console.WriteLine();
    Console.WriteLine("============================================================");
    Console.WriteLine(" VERDICT: " + (overallPass ? "PASS — §218 optimistic allocation holds on REAL Azure SQL under 400-way concurrency" : "FAIL"));
    if (!overallPass) { Console.WriteLine(" Findings:"); foreach (var f in findings) Console.WriteLine("   - " + f); }
    Console.WriteLine("============================================================");
    return overallPass ? 0 : 1;
}

// ===========================================================================
//  CONCURRENCY PHASES — signup race (Phase 1) + cancellation/promotion race (Phase 2)
// ===========================================================================
async Task<(bool pass, List<string> findings)> RunPhasesAsync(
    Func<CommunityHubDbContext> NewDb, int eventId, int classA, int classB,
    IReadOnlyList<int> attendeesA, IReadOnlyList<int> attendeesB,
    int capA, int capB, int usersA, int usersB)
{
    var overallPass = true;
    var findings = new List<string>();
    var total = usersA + usersB;

    // --- 4. fire ALL signups at once (true wall-clock race) ----------------
    Console.WriteLine($"PHASE 1 — {total} CONCURRENT signups (A:{usersA}->cap{capA}, B:{usersB}->cap{capB})");

    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var statuses = new ConcurrentBag<(int cls, MasterClassSignupStatus st)>();
    var errors = new ConcurrentBag<string>();
    var tasks = new List<Task>(total);

    void Enqueue(int sessionId, IReadOnlyList<int> pool)
    {
        foreach (var att in pool)
        {
            var attId = att; var sid = sessionId;
            tasks.Add(Task.Run(async () =>
            {
                await gate.Task;   // everyone blocks here, then is released together
                try
                {
                    using var db = NewDb();
                    var svc = new MasterClassSignupService(db);
                    var r = await svc.SignUpAsync(eventId, attId, sid);
                    if (!r.Ok || r.Signup is null) { errors.Add($"signup not ok: {r.Error}"); return; }
                    statuses.Add((sid, r.Signup.Status));
                }
                catch (Exception ex) { errors.Add(ex.GetType().Name + ": " + ex.Message.Split('\n')[0]); }
            }));
        }
    }
    Enqueue(classA, attendeesA);
    Enqueue(classB, attendeesB);

    var sw = Stopwatch.StartNew();
    gate.SetResult();                 // RELEASE — all ~400 race now
    await Task.WhenAll(tasks);
    sw.Stop();
    var signupMs = sw.ElapsedMilliseconds;

    // --- 5. read the truth straight from the db ----------------------------
    var (confA, waitA, offA) = await CountsAsync(NewDb, eventId, classA);
    var (confB, waitB, offB) = await CountsAsync(NewDb, eventId, classB);
    var dupA = await DuplicateCountAsync(NewDb, eventId, classA);
    var dupB = await DuplicateCountAsync(NewDb, eventId, classB);

    int oversellA = Math.Max(0, confA - capA);
    int oversellB = Math.Max(0, confB - capB);
    int oversell = oversellA + oversellB;

    Console.WriteLine($"  wall-clock : {signupMs} ms for {total} concurrent signups ({(total * 1000.0 / Math.Max(1, signupMs)):F0}/s)");
    Console.WriteLine($"  exceptions : {errors.Count}");
    Console.WriteLine($"  Class A (cap {capA}): confirmed={confA} offered={offA} waitlisted={waitA} duplicates={dupA}");
    Console.WriteLine($"  Class B (cap {capB}): confirmed={confB} offered={offB} waitlisted={waitB} duplicates={dupB}");
    Console.WriteLine($"  OVERSELL   : {oversell}   (must be 0)");

    void Check(bool ok, string label)
    {
        Console.WriteLine($"    [{(ok ? "PASS" : "FAIL")}] {label}");
        if (!ok) { overallPass = false; findings.Add(label); }
    }
    Check(confA == capA, $"Class A confirmed ({confA}) == capacity ({capA})");
    Check(confB == capB, $"Class B confirmed ({confB}) == capacity ({capB})");
    Check(oversell == 0, $"zero oversell of the last seat (got {oversell})");
    Check(confA + waitA == usersA, $"Class A counts conserved ({confA}+{waitA} == {usersA})");
    Check(confB + waitB == usersB, $"Class B counts conserved ({confB}+{waitB} == {usersB})");
    Check(dupA == 0 && dupB == 0, $"no duplicate (attendee,session) signups (A={dupA} B={dupB})");
    Check(errors.Count == 0, $"no exceptions / deadlock storm ({errors.Count} errors)");
    foreach (var e in errors.Distinct().Take(5)) Console.WriteLine("      ! " + e);
    Console.WriteLine();

    // --- 6. concurrent cancellations -> race-safe promotion ----------------
    Console.WriteLine($"PHASE 2 — {capA} CONCURRENT cancellations of Class A confirmed seats (promotion race)");

    List<int> confirmedSeatHolders;
    using (var db = NewDb())
        confirmedSeatHolders = await db.MasterClassSignups.AsNoTracking()
            .Where(x => x.EventId == eventId && x.SessionId == classA && x.Status == MasterClassSignupStatus.Confirmed)
            .Select(x => x.AttendeeId).ToListAsync();

    var preWaitlist = (await CountsAsync(NewDb, eventId, classA)).waitlisted;
    var promotedIds = new ConcurrentBag<int>();
    var cancelErrors = new ConcurrentBag<string>();
    var cgate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var ctasks = confirmedSeatHolders.Select(att => Task.Run(async () =>
    {
        await cgate.Task;
        try
        {
            using var db = NewDb();
            var svc = new MasterClassSignupService(db);
            var promo = await svc.RemoveAsync(eventId, att, classA);
            if (promo?.PromotedAttendeeId is int pid) promotedIds.Add(pid);
        }
        catch (Exception ex) { cancelErrors.Add(ex.GetType().Name + ": " + ex.Message.Split('\n')[0]); }
    })).ToList();

    var sw2 = Stopwatch.StartNew();
    cgate.SetResult();
    await Task.WhenAll(ctasks);
    sw2.Stop();

    var (confA2, waitA2, offA2) = await CountsAsync(NewDb, eventId, classA);
    var distinctPromoted = promotedIds.Distinct().Count();
    var promotedWereWaiting = promotedIds.All(p => !confirmedSeatHolders.Contains(p));

    Console.WriteLine($"  wall-clock      : {sw2.ElapsedMilliseconds} ms for {confirmedSeatHolders.Count} concurrent cancellations");
    Console.WriteLine($"  cancel errors   : {cancelErrors.Count}");
    Console.WriteLine($"  promoted (total): {promotedIds.Count}   distinct: {distinctPromoted}");
    Console.WriteLine($"  Class A after   : confirmed={confA2} offered={offA2} waitlisted={waitA2} (was waitlist {preWaitlist})");

    Check(confA2 == capA, $"Class A still exactly capacity after cancellations ({confA2} == {capA})");
    Check(confA2 <= capA, $"never above capacity ({confA2} <= {capA})");
    Check(promotedIds.Count == confirmedSeatHolders.Count, $"every freed seat promoted one waiter ({promotedIds.Count} == {confirmedSeatHolders.Count})");
    Check(distinctPromoted == promotedIds.Count, $"no double-promote (distinct {distinctPromoted} == {promotedIds.Count})");
    Check(promotedWereWaiting, "promoted attendees were genuinely from the waitlist");
    Check(waitA2 == preWaitlist - confirmedSeatHolders.Count, $"waitlist shrank by exactly the seats freed ({waitA2} == {preWaitlist - confirmedSeatHolders.Count})");
    Check(cancelErrors.Count == 0, $"no exceptions during cancellation race ({cancelErrors.Count})");
    foreach (var e in cancelErrors.Distinct().Take(5)) Console.WriteLine("      ! " + e);
    Console.WriteLine();

    // Machine-readable line the doc/runner can scrape.
    Console.WriteLine($"RESULT users={total} A_cap={capA} A_conf={confA} A_wait={waitA} B_cap={capB} B_conf={confB} B_wait={waitB} oversell={oversell} dup={dupA + dupB} signup_ms={signupMs} cancel_ms={sw2.ElapsedMilliseconds} promoted={distinctPromoted} verdict={(overallPass ? "PASS" : "FAIL")}");

    return (overallPass, findings);
}

// ===========================================================================
// helpers
// ===========================================================================
static int EnvInt(string name, int dflt) =>
    int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : dflt;

static Event NewLoadSimEvent() => new()
{
    CommunityName = "LoadSim", DisplayName = "LoadSim (concurrency harness)",
    Code = "LOADSIM-" + Guid.NewGuid().ToString("N")[..8], IsActive = false,
    StartDate = new DateOnly(2027, 2, 9), EndDate = new DateOnly(2027, 2, 11),
};

// --- Azure-mode raw-SQL helpers (token-authenticated) ----------------------
static async Task<SqlConnection> OpenAsync(string cs, string token)
{
    var c = new SqlConnection(cs) { AccessToken = token };
    await c.OpenAsync();
    return c;
}

static async Task<int> ExecAsync(SqlConnection c, string sql)
{
    await using var cmd = c.CreateCommand();
    cmd.CommandText = sql;
    cmd.CommandTimeout = 120;
    return await cmd.ExecuteNonQueryAsync();
}

static async Task<int> ScalarIntAsync(SqlConnection c, string sql)
{
    await using var cmd = c.CreateCommand();
    cmd.CommandText = sql;
    cmd.CommandTimeout = 120;
    var o = await cmd.ExecuteScalarAsync();
    return o is null or DBNull ? 0 : Convert.ToInt32(o);
}

static async Task<string> ScalarStrAsync(SqlConnection c, string sql)
{
    await using var cmd = c.CreateCommand();
    cmd.CommandText = sql;
    cmd.CommandTimeout = 120;
    var o = await cmd.ExecuteScalarAsync();
    return o is null or DBNull ? "" : o.ToString() ?? "";
}

// Insert the isolated Event row at a FORCED synthetic id via IDENTITY_INSERT.
// Builds the column list dynamically from sys.columns (robust to schema additions):
// every NOT NULL column without a default gets a type-appropriate literal; nullable
// columns and columns with defaults are left out. Throws if the SPN lacks ALTER
// (IDENTITY_INSERT permission) — the caller then falls back to a natural identity id.
static async Task SeedEventForcedIdAsync(string cs, string token, string eventsQualified, int eventId)
{
    await using var c = await OpenAsync(cs, token);

    // Discover the NOT NULL, no-default, non-identity columns of the Events table.
    var cols = new List<(string name, string type)>();
    await using (var cmd = c.CreateCommand())
    {
        cmd.CommandText = $@"
SELECT col.name, t.name AS typ
FROM sys.columns col
JOIN sys.types t ON col.user_type_id = t.user_type_id
WHERE col.object_id = OBJECT_ID('{eventsQualified}')
  AND col.is_identity = 0
  AND col.is_nullable = 0
  AND col.default_object_id = 0
ORDER BY col.column_id;";
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
            cols.Add((rd.GetString(0), rd.GetString(1)));
    }

    var colNames = new StringBuilder("[Id]");
    var colVals = new StringBuilder(eventId.ToString());
    foreach (var (name, type) in cols)
    {
        colNames.Append(", [").Append(name).Append(']');
        colVals.Append(", ").Append(LiteralFor(name, type));
    }

    var sql = $@"
SET IDENTITY_INSERT {eventsQualified} ON;
INSERT INTO {eventsQualified} ({colNames}) VALUES ({colVals});
SET IDENTITY_INSERT {eventsQualified} OFF;";
    await ExecAsync(c, sql);

    static string LiteralFor(string name, string type) => type.ToLowerInvariant() switch
    {
        "bit" => "0",
        "tinyint" or "smallint" or "int" or "bigint" => "0",
        "decimal" or "numeric" or "money" or "smallmoney" or "float" or "real" => "0",
        "date" => "'2027-02-09'",
        "datetime" or "datetime2" or "smalldatetime" => "SYSUTCDATETIME()",
        "datetimeoffset" => "SYSDATETIMEOFFSET()",
        "time" => "'00:00:00'",
        "uniqueidentifier" => "'00000000-0000-0000-0000-000000000000'",
        "varbinary" or "binary" or "image" => "0x",
        // text-ish: keep Code unique (it has a unique index), generic elsewhere.
        _ => name.Equals("Code", StringComparison.OrdinalIgnoreCase)
                ? $"N'LS-{Guid.NewGuid().ToString("N")[..8]}'"
                : "N'LOADSIM'",
    };
}

static async Task<List<int>> AddAttendeesAsync(Func<CommunityHubDbContext> newDb, int eventId, string tag, int n)
{
    using var db = newDb();
    var list = new List<Attendee>(n);
    for (var i = 0; i < n; i++)
        list.Add(new Attendee
        {
            EventId = eventId, Email = $"{tag}{i}@loadsim.test", FirstName = "F", LastName = $"{tag}{i}",
            FullName = $"F {tag}{i}",
            TicketStatus = TicketStatus.TwoDay, MirrorState = MirrorState.Active,
        });
    db.Attendees.AddRange(list);
    await db.SaveChangesAsync();
    return list.Select(a => a.Id).ToList();
}

static async Task<(int confirmed, int waitlisted, int offered)> CountsAsync(
    Func<CommunityHubDbContext> newDb, int eventId, int sessionId)
{
    using var db = newDb();
    var rows = await db.MasterClassSignups.AsNoTracking()
        .Where(x => x.EventId == eventId && x.SessionId == sessionId)
        .GroupBy(x => x.Status)
        .Select(g => new { g.Key, Count = g.Count() })
        .ToListAsync();
    int C(MasterClassSignupStatus s) => rows.FirstOrDefault(r => r.Key == s)?.Count ?? 0;
    return (C(MasterClassSignupStatus.Confirmed), C(MasterClassSignupStatus.Waitlisted), C(MasterClassSignupStatus.Offered));
}

static async Task<int> DuplicateCountAsync(Func<CommunityHubDbContext> newDb, int eventId, int sessionId)
{
    using var db = newDb();
    var groups = await db.MasterClassSignups.AsNoTracking()
        .Where(x => x.EventId == eventId && x.SessionId == sessionId)
        .GroupBy(x => x.AttendeeId)
        .Select(g => g.Count())
        .ToListAsync();
    return groups.Count(c => c > 1);
}

// ===========================================================================
//  AAD token interceptor — attaches the access token to every physical
//  SqlConnection EF opens (the connection string itself carries no auth).
// ===========================================================================
sealed class AadTokenInterceptor : DbConnectionInterceptor
{
    private readonly string _token;
    public AadTokenInterceptor(string token) => _token = token;

    public override InterceptionResult ConnectionOpening(
        System.Data.Common.DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        if (connection is SqlConnection sql && string.IsNullOrEmpty(sql.AccessToken))
            sql.AccessToken = _token;
        return base.ConnectionOpening(connection, eventData, result);
    }

    public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
        System.Data.Common.DbConnection connection, ConnectionEventData eventData, InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        if (connection is SqlConnection sql && string.IsNullOrEmpty(sql.AccessToken))
            sql.AccessToken = _token;
        return base.ConnectionOpeningAsync(connection, eventData, result, cancellationToken);
    }
}
