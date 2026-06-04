// =============================================================================
// capture-snapshot.csx  (v9 — port discovery for local PBIP ADOMD mode)
// =============================================================================
// TEMPLATE — model-agnostic, READ-ONLY. Do not edit in place. The
// regression-testing skill copies this template to output/{label}.csx and
// fills in ONLY these four sections; everything else is verbatim and tested:
//   1. PURPOSE comment + test-count line (just below)
//   2. modelName config var               (Configuration section)
//   3. testLines block                    (TEST CASE DEFINITIONS section)
//   4. groupByColumns dictionary          (DIMENSION MAP section)
//
// PURPOSE:  <Set per session — describes which model and refactor this
//            snapshot validates.>
//           <N test cases — set per session.>
//           Results streamed to disk per test case to avoid OutOfMemoryException.
//
// OUTPUTS:  {label}.json           — full snapshot with results + timing
//           {label}-testplan.json  — planned test order (written pre-flight)
//           {label}-timing.csv     — per-test-case timing for performance comparison
//           {label}-errors.log     — error details (only if errors occur)
//           {label}-timeouts.log   — timeouts + smoke-test failures, with Type/Reason
//                                    (written if timeouts OR smoke failures occur)
//
// USAGE:    GUI (Tabular Editor 3):
//           1. Connect TE3 to the target model
//           2. Set snapshotLabel, modelName, and diagnosticMode below
//           3. Run script — output saved to Desktop\PBI-Regression\
//
//           CLI (Claude Code / automation):
//           Environment variables override the hardcoded defaults below.
//           Set any combination before invoking the TE CLI:
//             SNAPSHOT_LABEL        → snapshotLabel       (e.g. "baseline", "refactored")
//             MODEL_NAME            → modelName            (e.g. "Occupancy")
//             DIAGNOSTIC_MODE       → diagnosticMode       ("true" / "false")
//             OUTPUT_DIR            → outputDir            (full path)
//             TEAMS_WEBHOOK_URL     → teamsWebhookUrl      (full URL)
//             QUERY_TIMEOUT_MS      → queryTimeoutMs       (default: 60000, in milliseconds)
//             SMOKE_TEST_TIMEOUT_MS → smokeTestTimeoutMs   (default: 10000)
//             MEMORY_THRESHOLD_PCT  → memoryThresholdPct   (default: 80, percent)
//             USE_DIRECT_ADOMD      → useDirectAdomd       ("true" / "false")
//             SKIP_ON_SMOKE_FAILURE → skipOnSmokeTestFailure ("true" / "false")
//             CONNECTION_STRING     → discoveredConnStr      (full MSOLAP connection string;
//                                    skips port discovery — use for XMLA or non-standard installs)
//           Example:
//             set SNAPSHOT_LABEL=baseline
//             set MODEL_NAME=Occupancy
//             TabularEditor.exe model.bim -S capture-snapshot.csx
//
// ROLLBACK: Read-only — no model changes made
// =============================================================================

// ── Configuration ────────────────────────────────────────────────────────────
// Env vars override these defaults when running via CLI. When running in TE3
// GUI, edit the defaults directly — env vars won't be set.

var snapshotLabel = System.Environment.GetEnvironmentVariable("SNAPSHOT_LABEL")
    ?? "refactor";

// Model name — written to the snapshot JSON header for downstream comparison
// reports. Replaced per session by the regression-testing skill (or override
// via the MODEL_NAME env var when running from the CLI).
var modelName = System.Environment.GetEnvironmentVariable("MODEL_NAME")
    ?? "<MODEL NAME — replaced per session by regression-testing skill>";

var _envDiag = System.Environment.GetEnvironmentVariable("DIAGNOSTIC_MODE");
var diagnosticMode = _envDiag != null
    ? _envDiag.Equals("true", StringComparison.OrdinalIgnoreCase)
    : false;   // ← flip to true for GUI debugging

// ── Global Filters ──────────────────────────────────────────────────────────
// Optional: KEEPFILTERS expressions applied to EVERY measure evaluation via
// a CALCULATE wrapper. Filters flow through actual model relationships — no
// TREATAS, no virtual relationships — so they validate real filter propagation.
//
// Leave the list empty to evaluate measures with no additional filter context.
// Each entry must be a valid DAX boolean filter expression for KEEPFILTERS().
//
// Examples:
//   "'Calendar'[Start of Year] = DATE(2025, 1, 1)"
//   "'Properties'[Property Current Same Home Reporting] = \"Y\""
//
var globalFilters = new List<string>
{
    // "'Calendar'[Start of Year] = DATE(2025, 1, 1)",
};

// ── Row Cap ─────────────────────────────────────────────────────────────────
// Optional: Maximum rows returned per grouped context query. Wraps the
// SUMMARIZECOLUMNS in TOPN to limit result set size. Set to 0 to return all
// rows (full evaluation).
//
// For regression testing, 3–5 rows per context is typically sufficient to
// validate filter propagation and calculation correctness without evaluating
// every distinct dimension value. Reduces capture time significantly on
// high-cardinality dimensions like Vendor Name.
//
var maxRowsPerContext = 0;   // 0 = no limit; e.g. 5 = cap at 5 rows per test

// Teams notification — paste your Power Automate incoming webhook URL below.
// Leave blank to skip notification. To create one:
//   Teams channel → "+" → Workflows → "Post to a channel when a webhook request
//   is received" → copy the URL it generates
var teamsWebhookUrl = System.Environment.GetEnvironmentVariable("TEAMS_WEBHOOK_URL")
    ?? "https://your-webhook-url";

var outputDir = System.Environment.GetEnvironmentVariable("OUTPUT_DIR")
    ?? System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop),
        "PBI-Regression"
    );

if (!System.IO.Directory.Exists(outputDir))
    System.IO.Directory.CreateDirectory(outputDir);

// ── Safety Limits ───────────────────────────────────────────────────────────
// Declared here — before port discovery — because useDirectAdomd gates the
// discovery block. The remaining limits apply during query execution below.
//
// queryTimeoutMs: per-query hard cap enforced server-side via AdomdCommand.
//   60 seconds is generous for well-formed DAX (typical slow queries: 5–8s).
//   Set to 0 to disable (not recommended for large test suites).
var _envTimeout = System.Environment.GetEnvironmentVariable("QUERY_TIMEOUT_MS");
var queryTimeoutMs = _envTimeout != null ? int.Parse(_envTimeout) : 60000;

// smokeTestTimeoutMs: pre-flight check cap per unique measure.
//   Smoke test runs EVALUATE ROW("r", [Measure]) — grand-total only.
//   Broken measures that fail even this minimal query are skipped in the main run.
var _envSmokeTimeout = System.Environment.GetEnvironmentVariable("SMOKE_TEST_TIMEOUT_MS");
var smokeTestTimeoutMs = _envSmokeTimeout != null ? int.Parse(_envSmokeTimeout) : 10000;

// memoryThresholdPct: watchdog aborts the run if (TE3 + msmdsrv) combined
//   WorkingSet64 exceeds this % of total physical RAM. Set to 0 to disable.
//   NOTE: Uses a hard-coded 16 GB denominator (see IsMemoryCritical below).
//   Meaningful only in local PBIP workspace mode — in XMLA mode the model
//   memory lives on the Fabric capacity and is invisible to this check.
var _envMemPct = System.Environment.GetEnvironmentVariable("MEMORY_THRESHOLD_PCT");
var memoryThresholdPct = _envMemPct != null ? double.Parse(_envMemPct) : 80.0;

// useDirectAdomd: when true, bypasses EvaluateDax() and uses AdomdCommand
//   directly with CommandTimeout — the ONLY mechanism that actually cancels a
//   hung query server-side and releases msmdsrv memory/CPU.
//   Set to false to fall back to EvaluateDax() (no timeout enforcement).
var _envDirectAdomd = System.Environment.GetEnvironmentVariable("USE_DIRECT_ADOMD");
var useDirectAdomd = _envDirectAdomd != null
    ? _envDirectAdomd.Equals("true", StringComparison.OrdinalIgnoreCase)
    : true;

// skipOnSmokeTestFailure: when true, measures that fail the smoke test are
//   skipped in the main run (status:"skipped"). When false, they still run
//   but will likely timeout and be caught by queryTimeoutMs.
var _envSkipOnSmoke = System.Environment.GetEnvironmentVariable("SKIP_ON_SMOKE_FAILURE");
var skipOnSmokeTestFailure = _envSkipOnSmoke != null
    ? _envSkipOnSmoke.Equals("true", StringComparison.OrdinalIgnoreCase)
    : true;

// ── Port Discovery ───────────────────────────────────────────────────────────
// TE3's Model.Database wrapper hides .Server, so we cannot extract the
// connection string from the model directly. For local PBIP workspace mode,
// scan PBI Desktop's AnalysisServicesWorkspaces folder for running msmdsrv
// instances and match by database name (which TE3 DOES expose).
// Runs ONCE at script start; captured via closure into ExecuteDaxWithTimeout.
//
// Override: set CONNECTION_STRING env var to skip discovery (for XMLA
// endpoints or non-standard installs).
//
// Works for both the standard PBI Desktop installer and the Microsoft Store
// build; checks three workspace root paths to cover known install locations.
// ─────────────────────────────────────────────────────────────────────────────
string discoveredConnStr = null;
if (useDirectAdomd)
{
    discoveredConnStr = System.Environment.GetEnvironmentVariable("CONNECTION_STRING");
    if (string.IsNullOrEmpty(discoveredConnStr))
    {
        var localAppData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        var userProfile  = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        var workspaceRoots = new[] {
            // Standard PBI Desktop installer
            System.IO.Path.Combine(localAppData, "Microsoft", "Power BI Desktop", "AnalysisServicesWorkspaces"),
            // Microsoft Store install (sandboxed Packages path)
            System.IO.Path.Combine(localAppData, "Packages", "Microsoft.MicrosoftPowerBIDesktop_8wekyb3d8bbwe",
                "LocalCache", "Local", "Microsoft", "Power BI Desktop Store App", "AnalysisServicesWorkspaces"),
            // Store App provisioned directly under user profile (seen on some machines)
            System.IO.Path.Combine(userProfile, "Microsoft", "Power BI Desktop Store App", "AnalysisServicesWorkspaces"),
        };

        var portCandidates = new System.Collections.Generic.List<Tuple<string, string>>();
        var rootsChecked   = new System.Collections.Generic.List<string>();
        foreach (var root in workspaceRoots)
        {
            rootsChecked.Add(root);
            if (!System.IO.Directory.Exists(root)) continue;
            foreach (var ws in System.IO.Directory.GetDirectories(root, "AnalysisServicesWorkspace*"))
            {
                // Port file lives at workspace root in current builds; older builds put it under Data\.
                var portFile = System.IO.Path.Combine(ws, "Data", "msmdsrv.port.txt");
                if (!System.IO.File.Exists(portFile))
                    portFile = System.IO.Path.Combine(ws, "msmdsrv.port.txt");
                if (!System.IO.File.Exists(portFile)) continue;

                // msmdsrv.port.txt is UTF-16 LE with BOM. Read bytes, keep only ASCII digits —
                // encoding-agnostic and immune to BOM/whitespace noise.
                var portBytes = System.IO.File.ReadAllBytes(portFile);
                var portSb    = new System.Text.StringBuilder();
                foreach (var b in portBytes)
                    if (b >= (byte)'0' && b <= (byte)'9') portSb.Append((char)b);
                var port = portSb.ToString();
                if (!string.IsNullOrEmpty(port))
                    portCandidates.Add(Tuple.Create(port, ws));
            }
        }

        if (portCandidates.Count == 0)
            throw new Exception(
                "No running msmdsrv instances found. Open the .pbip in Power BI Desktop first. " +
                "Workspace roots checked: " + string.Join(" | ", rootsChecked));

        // Match by Model.Database.Name — a GUID in local workspace mode that
        // uniquely identifies which msmdsrv hosts the model TE3 is connected to.
        var targetDbName = Model.Database.Name;
        var probeLog     = new System.Collections.Generic.List<string>();
        foreach (var pc in portCandidates)
        {
            var port  = pc.Item1;
            var probe = "Provider=MSOLAP;Data Source=localhost:" + port + ";";
            try
            {
                using (var c = new Microsoft.AnalysisServices.AdomdClient.AdomdConnection(probe))
                {
                    c.Open();
                    var ds = c.GetSchemaDataSet("DBSCHEMA_CATALOGS", null);
                    foreach (System.Data.DataRow row in ds.Tables[0].Rows)
                    {
                        var name = row["CATALOG_NAME"].ToString();
                        probeLog.Add("localhost:" + port + " -> " + name);
                        if (string.Equals(name, targetDbName, StringComparison.OrdinalIgnoreCase))
                        {
                            discoveredConnStr = probe + "Catalog=" + name + ";";
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                probeLog.Add("localhost:" + port + " -> [probe failed: " + ex.Message + "]");
            }
            if (discoveredConnStr != null) break;
        }

        if (discoveredConnStr == null)
            throw new Exception(
                "Could not match TE3's Model.Database.Name='" + targetDbName +
                "' to any open PBI Desktop workspace. Probed: " + string.Join(" | ", probeLog) +
                ". Either reopen the correct .pbip, or set CONNECTION_STRING env var to override.");
    }
}

var outputPath = System.IO.Path.Combine(outputDir, snapshotLabel + ".json");
var errorLogPath = System.IO.Path.Combine(outputDir, snapshotLabel + "-errors.log");
var timeoutLogPath = System.IO.Path.Combine(outputDir, snapshotLabel + "-timeouts.log");

// ═══════════════════════════════════════════════════════════════════════════════
// HELPERS
// ═══════════════════════════════════════════════════════════════════════════════
Func<string, string> JsonEscape = (string s) =>
{
    if (s == null) return "";
    return s.Replace("\\", "\\\\").Replace("\"", "\\\"")
            .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
};

Func<object, string> SerializeValue = (object val) =>
{
    if (val == null || val == DBNull.Value)
        return "\"__BLANK__\"";
    if (val is double d)
    {
        if (double.IsNaN(d)) return "\"__NaN__\"";
        if (double.IsInfinity(d)) return "\"__INF__\"";
        return Math.Round(d, 4).ToString("G");
    }
    if (val is decimal dec) return dec.ToString();
    if (val is long l) return l.ToString();
    if (val is int intVal) return intVal.ToString();
    return "\"" + JsonEscape(val.ToString()) + "\"";
};

// ═══════════════════════════════════════════════════════════════════════════════
// BUILD GLOBAL FILTER FRAGMENT
// ═══════════════════════════════════════════════════════════════════════════════
// When globalFilters is non-empty, the measure reference [MeasureName] is
// wrapped in: CALCULATE([MeasureName], KEEPFILTERS(filter1), KEEPFILTERS(filter2), ...)
//
// KEEPFILTERS intersects with (rather than overrides) any filter context the
// measure applies internally, so it behaves like a report-level slicer.
//
// When globalFilters is empty, the measure is referenced directly — no wrapper.

Func<string, string> BuildMeasureRef = (string measureName) =>
{
    if (globalFilters.Count == 0)
        return "[" + measureName + "]";

    var keepFilterArgs = string.Join(", ",
        globalFilters.Select(f => "KEEPFILTERS(" + f + ")"));

    return "CALCULATE([" + measureName + "], " + keepFilterArgs + ")";
};

// ─────────────────────────────────────────────────────────────────────────────
// ExecuteDaxWithTimeout — returns Tuple<DataTable, status, errorMessage>
//   status ∈ { "ok", "timeout", "error" }
//
// useDirectAdomd=true (recommended path):
//   Runs ExecuteReader on a thread-pool task so the script thread stays free.
//   The script thread polls every 500ms for (a) wall-clock timeout, (b) memory
//   pressure via IsMemoryCritical. On either trigger it calls cmd.Cancel() —
//   which IS thread-safe by design and IS the only mechanism that reliably
//   interrupts a Storage-Engine-bound Tabular query. After Cancel we wait up
//   to 10s for the background task to unwind (so we don't dispose mid-stream),
//   then return "timeout" with a descriptive error message.
//
//   AdomdCommand.CommandTimeout is also set as a backstop, but is unreliable
//   for SE-bound Tabular queries (the engine only checks at "checkpoints"
//   which a single big SE scan may never hit). Cancel() is the real safeguard.
//
//   Memory watchdog uses a 3-poll debounce (1.5s sustained pressure required)
//   to filter transient working-set spikes during normal msmdsrv evaluation
//   while still catching genuine runaway memory.
//
// useDirectAdomd=false: falls back to EvaluateDax() with NO enforced timeout
//   and NO cancellability (pre-v7 behavior). A hung query freezes TE3 and
//   only Task Manager + msmdsrv kill recovers. Avoid.
//
// XMLA note: Power BI gateway caps queries at ~225s regardless. Setting
//   queryTimeoutMs > 225000 has no effect against the Power BI service.
// ─────────────────────────────────────────────────────────────────────────────
Func<string, int, Tuple<System.Data.DataTable, string, string>> ExecuteDaxWithTimeout = (dax, timeoutMs) =>
{
    if (!useDirectAdomd)
    {
        try
        {
            var dt = EvaluateDax(dax) as System.Data.DataTable;
            return Tuple.Create(dt, "ok", (string)null);
        }
        catch (Exception ex)
        {
            return Tuple.Create((System.Data.DataTable)null, "error", ex.Message);
        }
    }

    // Use the connection string discovered once at script startup (see port-discovery
    // block above). Catalog is already appended by the discovery code.
    var connStr = discoveredConnStr;
    if (connStr.IndexOf("catalog=", StringComparison.OrdinalIgnoreCase) < 0)
        connStr += ";Catalog=" + Model.Database.Name;

    // Manual lifetime management (not `using`) because we must NOT dispose the
    // connection/command until the background task finishes — disposing while
    // ExecuteReader is still in flight would throw ObjectDisposedException on
    // the background thread or hang Dispose itself.
    var conn = new Microsoft.AnalysisServices.AdomdClient.AdomdConnection(connStr);
    var cmd = new Microsoft.AnalysisServices.AdomdClient.AdomdCommand(dax, conn);
    bool safeToDispose = true;
    try
    {
        conn.Open();
        // Backstop only; Cancel() below is the real enforcement.
        cmd.CommandTimeout = timeoutMs <= 0 ? 0 : Math.Max(1, timeoutMs / 1000);

        System.Data.DataTable resultTable = null;
        Exception bgException = null;

        // ExecuteReader on a thread-pool thread → script thread stays free to
        // monitor wall-clock + memory and call Cancel() when needed.
        var queryTask = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                using (var reader = cmd.ExecuteReader())
                {
                    // Build the DataTable manually rather than calling dt.Load(reader).
                    // Load() auto-promotes ADOMD-reported IsKey columns to a PrimaryKey
                    // constraint, then throws "Failed to enable constraints. One or more
                    // rows contain values violating non-null, unique, or foreign-key
                    // constraints." whenever a result row has NULL/blank in the key
                    // column. Manual iteration skips the schema's IsKey/AllowDBNull
                    // metadata and simply collects every row as-is.
                    var dt = new System.Data.DataTable();
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        var col = new System.Data.DataColumn(reader.GetName(i), reader.GetFieldType(i));
                        col.AllowDBNull = true;
                        dt.Columns.Add(col);
                    }
                    dt.BeginLoadData();
                    var rowBuf = new object[reader.FieldCount];
                    while (reader.Read())
                    {
                        reader.GetValues(rowBuf);
                        dt.Rows.Add(rowBuf);
                    }
                    dt.EndLoadData();
                    resultTable = dt;
                }
            }
            catch (Exception ex)
            {
                bgException = ex;
            }
        });

        // Poll at 500ms granularity for completion / wall-clock / memory pressure.
        const int pollMs = 500;
        // Memory watchdog debounce: require this many consecutive critical polls
        // before cancelling. Filters transient working-set spikes during normal
        // msmdsrv evaluation while still catching sustained runaway memory.
        // 3 × 500ms = 1.5s sustained pressure required.
        const int memCriticalPollsRequired = 3;
        var deadlineUtc = timeoutMs <= 0
            ? DateTime.MaxValue
            : DateTime.UtcNow.AddMilliseconds(timeoutMs);
        string abortReason = null;
        int memCriticalCount = 0;

        while (true)
        {
            if (queryTask.Wait(pollMs)) break;  // task completed (success or thrown)
            if (DateTime.UtcNow >= deadlineUtc)
            {
                abortReason = "wall-clock timeout after " + timeoutMs + "ms (cancelled by watchdog)";
                break;
            }
            if (IsMemoryCritical(memoryThresholdPct))
            {
                memCriticalCount++;
                if (memCriticalCount >= memCriticalPollsRequired)
                {
                    abortReason = "memory threshold " + memoryThresholdPct
                        + "% sustained for " + (memCriticalPollsRequired * pollMs)
                        + "ms mid-query (cancelled by watchdog)";
                    break;
                }
            }
            else
            {
                memCriticalCount = 0;
            }
        }

        if (abortReason != null)
        {
            try { cmd.Cancel(); } catch { /* best effort — Cancel may itself fail if engine is unresponsive */ }
            // Wait up to 10s for the background task to observe the cancellation
            // and exit. If it doesn't, leak conn/cmd rather than risk hanging
            // the script on Dispose() against an unresponsive engine.
            bool unwound = false;
            try { unwound = queryTask.Wait(10000); } catch { unwound = queryTask.IsCompleted; }
            if (!unwound) safeToDispose = false;
            return Tuple.Create((System.Data.DataTable)null, "timeout", abortReason);
        }

        if (bgException != null)
        {
            var msg = bgException.Message ?? "";
            var isTimeout = msg.IndexOf("cancel", StringComparison.OrdinalIgnoreCase) >= 0
                         || msg.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0;
            return Tuple.Create((System.Data.DataTable)null, isTimeout ? "timeout" : "error", msg);
        }

        return Tuple.Create(resultTable, "ok", (string)null);
    }
    finally
    {
        if (safeToDispose)
        {
            try { cmd.Dispose(); } catch { }
            try { conn.Dispose(); } catch { }
        }
        // else: leak. GC + msmdsrv-side cleanup will eventually reclaim. Better
        // than freezing the script on Dispose against a hung engine.
    }
};

// ─────────────────────────────────────────────────────────────────────────────
// IsMemoryCritical — returns true when combined TE3 + msmdsrv WorkingSet64
//   exceeds memoryThresholdPct % of total physical RAM.
//
// Sums msmdsrv processes because in local PBIP workspace mode the Analysis
// Services engine holds most of the model memory outside the TE3 process.
// Returns false immediately when memoryThresholdPct == 0 (disabled).
//
// NOTE: Without P/Invoke or WMI (not guaranteed in TE3 scripting context),
// we approximate total RAM as 16 GB. If your machine has more RAM, lower
// memoryThresholdPct proportionally (e.g., on 32GB use 40% ≈ same as 80% of 16GB).
// ─────────────────────────────────────────────────────────────────────────────
Func<double, bool> IsMemoryCritical = (thresholdPct) =>
{
    if (thresholdPct <= 0) return false;
    try
    {
        long used = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64;
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("msmdsrv"))
        {
            try { used += p.WorkingSet64; } catch { }
        }
        // Without P/Invoke or WMI (not guaranteed in TE3 scripting context),
        // we approximate total RAM as 16 GB. If your machine has more RAM,
        // lower memoryThresholdPct proportionally (e.g., on 32GB use 40% ≈ same as 80% of 16GB).
        const long assumedTotalRamBytes = 16L * 1024 * 1024 * 1024;
        return (used / (double)assumedTotalRamBytes) * 100.0 >= thresholdPct;
    }
    catch
    {
        return false;
    }
};

// ═══════════════════════════════════════════════════════════════════════════════
// TEST CASE DEFINITIONS — id|measure|context
// ═══════════════════════════════════════════════════════════════════════════════
var testLines = new List<string>
{
    // ── Replaced per session by the regression-testing skill ──
    // Format: testId|measureName|contextLabel
    //   contextLabel must be "grand_total" or a key from groupByColumns below.
    //   Cross-product context labels conventionally use "_x_" (e.g. by_dim1_x_year).
    "t0001|Sample Measure A|grand_total",
    "t0002|Sample Measure A|by_dim1",
    "t0003|Sample Measure A|by_year",
    "t0004|Sample Measure A|by_dim1_x_year",
    "t0005|Sample Measure B|grand_total",
};

// ═══════════════════════════════════════════════════════════════════════════════
// DIMENSION MAP — context label → DAX column reference
// ═══════════════════════════════════════════════════════════════════════════════
var groupByColumns = new Dictionary<string, string>
{
    // ── Replaced per session by the regression-testing skill ──
    // Single-dimension entries: one DAX column reference per context label.
    { "by_dim1",   "'YourDimTable'[YourSlicerColumn]" },
    { "by_year",   "'Calendar'[YourYearColumn]" },
    { "by_status", "'YourStatusTable'[YourStatusColumn]" },

    // Cross-product entries: pipe-separated, FIRST column = TOPN partition.
    // Place the lower-cardinality column first (see skill Section 2.1).
    { "by_dim1_x_year", "'YourDimTable'[YourSlicerColumn]|'Calendar'[YourYearColumn]" },
};

// ═══════════════════════════════════════════════════════════════════════════════
// DAX QUERY CONSTRUCTION — single, cross-product, and TOPN-per-group
// ═══════════════════════════════════════════════════════════════════════════════
// Context types:
//   "grand_total"          → SUMMARIZECOLUMNS with no grouping columns
//   single-dimension       → groupByColumns value has no "|" separator
//   cross-product          → groupByColumns value has "|" separating multiple columns
//
// TOPN behavior (when maxRowsPerContext > 0):
//   grand_total            → skipped (always 1 row)
//   single-dimension       → TOPN wraps entire SUMMARIZECOLUMNS (top N overall)
//   cross-product          → GENERATE over first column (partition), TOPN within each
//                            partition value. First column in "|"-list = partition column.
//                            Ensures top N detail rows per partition, not top N overall.
//
// Cross-product convention:
//   groupByColumns entry:  { "by_same_home_x_year", "'Properties'[...] | 'Calendar'[...]" }
//   The "|" is the delimiter. First column = partition for TOPN-per-group.
//   Place the lower-cardinality column first.
// ═══════════════════════════════════════════════════════════════════════════════

var testCases = new List<Tuple<string, string, string, string>>();

foreach (var line in testLines)
{
    var parts = line.Split('|');
    var testId = parts[0];
    var measureName = parts[1];
    var context = parts[2];

    // Build measure reference — bare [Measure] or CALCULATE([Measure], KEEPFILTERS(...))
    var measureRef = BuildMeasureRef(measureName);

    string dax;
    if (context == "grand_total")
    {
        // No grouping — single-row result
        dax = "SUMMARIZECOLUMNS(\"Result\", " + measureRef + ")";
    }
    else
    {
        var groupCol = groupByColumns[context];

        if (groupCol.Contains("|"))
        {
            // ── Cross-product: multiple grouping columns ──
            var cols = groupCol.Split('|');
            var allCols = string.Join(", ", cols);
            var innerDax = "SUMMARIZECOLUMNS(" + allCols + ", \"Result\", " + measureRef + ")";

            if (maxRowsPerContext > 0)
            {
                // TOPN per group: first column = partition, rest = detail
                // DAX: GENERATE(VALUES(partition), TOPN(N, SUMMARIZECOLUMNS(detail, "Result", measure)))
                var partitionCol = cols[0];
                var detailCols = string.Join(", ", cols.Skip(1));
                dax = "GENERATE(VALUES(" + partitionCol + "), "
                    + "TOPN(" + maxRowsPerContext + ", "
                    + "SUMMARIZECOLUMNS(" + detailCols + ", \"Result\", " + measureRef + ")))";
            }
            else
            {
                dax = innerDax;
            }
        }
        else
        {
            // ── Single dimension ──
            var innerDax = "SUMMARIZECOLUMNS(" + groupCol + ", \"Result\", " + measureRef + ")";

            if (maxRowsPerContext > 0)
            {
                dax = "TOPN(" + maxRowsPerContext + ", " + innerDax + ")";
            }
            else
            {
                dax = innerDax;
            }
        }
    }

    testCases.Add(Tuple.Create(testId, measureName, context, dax));
}

// ═══════════════════════════════════════════════════════════════════════════════
// PRE-FLIGHT SMOKE TEST — validates each unique measure before the main run
// ═══════════════════════════════════════════════════════════════════════════════
// Runs EVALUATE ROW("r", [Measure]) per unique measure with a tight timeout.
// Broken measures (syntax errors, missing columns, hung grand-totals) are
// added to skippedMeasures and skipped in the main run.
//
// Caveat: a measure that passes the grand-total smoke test can still hang in a
// cross-product context. queryTimeoutMs (Layer 1) catches those.
// ═══════════════════════════════════════════════════════════════════════════════
var skippedMeasures = new HashSet<string>();
var smokeResults = new Dictionary<string, string>(); // measure → failure reason

// timeoutLog is declared early (before the smoke loop) so smoke-test failures
// can be appended alongside main-loop timeouts. Each entry carries a Type tag —
// memory_watchdog | query_timeout | smoketest_timeout | smoketest_error |
// query_error — and a Reason line with the full error message.
var timeoutLog = new System.Text.StringBuilder();

if (skipOnSmokeTestFailure)
{
    var uniqueMeasures = testCases.Select(tc => tc.Item2).Distinct().ToList();
    int smokeIdx = 0;

    foreach (var mName in uniqueMeasures)
    {
        smokeIdx++;
        // IGNORE() is a SUMMARIZECOLUMNS-only modifier and is invalid inside ROW().
        // ROW() always emits one row, so IGNORE adds no semantic value here.
        // Filters (if any) are folded into the measure ref via BuildMeasureRef.
        var smokeDax = "EVALUATE ROW(\"r\", " + BuildMeasureRef(mName) + ")";
        var smokeSw = System.Diagnostics.Stopwatch.StartNew();
        var smokeResult = ExecuteDaxWithTimeout(smokeDax, smokeTestTimeoutMs);
        smokeSw.Stop();
        if (smokeResult.Item2 != "ok")
        {
            skippedMeasures.Add(mName);
            var smokeReason = smokeResult.Item3 ?? "unknown error";
            smokeResults[mName] = smokeResult.Item2 + ": " + smokeReason;

            string smokeType;
            if (smokeReason.IndexOf("memory threshold", StringComparison.OrdinalIgnoreCase) >= 0)
                smokeType = "memory_watchdog";
            else if (smokeResult.Item2 == "timeout")
                smokeType = "smoketest_timeout";
            else
                smokeType = "smoketest_error";

            var smokeId = "s" + smokeIdx.ToString("D4");
            timeoutLog.AppendLine($"{smokeId} | {mName} | smoke_test | {smokeSw.ElapsedMilliseconds}ms");
            timeoutLog.AppendLine($"  Type: {smokeType}");
            timeoutLog.AppendLine($"  Reason: {smokeReason}");
            timeoutLog.AppendLine($"  DAX: {smokeDax}");
            timeoutLog.AppendLine();
        }
        if (smokeResult.Item1 != null) smokeResult.Item1.Dispose();
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// PRE-FLIGHT: write testplan manifest
// ═══════════════════════════════════════════════════════════════════════════════
// Writes the planned test order (test_id, measure, context, dax) to a sibling
// JSON file BEFORE the main writer starts. Never truncated; safe to parse even
// after a force-kill. Useful for identifying which test was in flight during a
// killed run.
// ═══════════════════════════════════════════════════════════════════════════════

var testplanPath = System.IO.Path.Combine(outputDir, snapshotLabel + "-testplan.json");
using (var planWriter = new System.IO.StreamWriter(testplanPath, false, System.Text.Encoding.UTF8))
{
    planWriter.WriteLine("{");
    planWriter.WriteLine("  \"label\": \"" + snapshotLabel + "\",");
    planWriter.WriteLine("  \"captured_at\": \"" + DateTime.UtcNow.ToString("o") + "\",");
    planWriter.WriteLine("  \"tests\": [");
    for (int i = 0; i < testCases.Count; i++)
    {
        var tc = testCases[i];
        planWriter.Write("    {\"test_id\": \"" + tc.Item1 + "\", ");
        planWriter.Write("\"measure\": \"" + JsonEscape(tc.Item2) + "\", ");
        planWriter.Write("\"context\": \"" + tc.Item3 + "\", ");
        planWriter.Write("\"dax\": \"" + JsonEscape(tc.Item4) + "\"}");
        planWriter.WriteLine(i < testCases.Count - 1 ? "," : "");
    }
    planWriter.WriteLine("  ]");
    planWriter.WriteLine("}");
}

// ═══════════════════════════════════════════════════════════════════════════════
// EXECUTION ENGINE — streams results to disk per test case
// ═══════════════════════════════════════════════════════════════════════════════
var sw = System.Diagnostics.Stopwatch.StartNew();
var errorLog = new System.Text.StringBuilder();
// timeoutLog is declared earlier (above the smoke loop) so smoke-test failures
// can be appended before main execution begins.

// Timing accumulator — written to CSV after JSON is complete
var timingRows = new List<Tuple<string, string, string, string, int, long>>();
// Fields: testId, measureName, context, status, rowCount, durationMs

int total = diagnosticMode ? Math.Min(8, testCases.Count) : testCases.Count;
int okCount = 0;
int errCount = 0;
int timeoutCount = 0;
int skipCount = 0;
int abortedMemoryCount = 0;
bool memoryAborted = false;

using (var writer = new System.IO.StreamWriter(outputPath, false, System.Text.Encoding.UTF8))
{
    writer.WriteLine("{");
    writer.WriteLine("  \"snapshot_version\": \"1.0\",");
    writer.WriteLine("  \"model_name\": \"" + JsonEscape(modelName) + "\",");
    writer.WriteLine("  \"captured_at\": \"" + DateTime.UtcNow.ToString("o") + "\",");
    writer.WriteLine("  \"label\": \"" + snapshotLabel + "\",");
    writer.WriteLine("  \"global_filters\": [" + string.Join(", ", globalFilters.Select(f => "\"" + JsonEscape(f) + "\"")) + "],");
    writer.WriteLine("  \"max_rows_per_context\": " + maxRowsPerContext + ",");
    writer.WriteLine("  \"query_timeout_ms\": " + queryTimeoutMs + ",");
    writer.WriteLine("  \"results\": {");

    for (int i = 0; i < total; i++)
    {
        var tc = testCases[i];
        var testId = tc.Item1;
        var measureName = tc.Item2;
        var context = tc.Item3;
        var dax = tc.Item4;
        var isLast = (i == total - 1);

        // ── Memory watchdog — check before starting next test ──────────────
        if (!memoryAborted && IsMemoryCritical(memoryThresholdPct))
        {
            // Settle window: give msmdsrv ~1s to release post-query working set.
            // If pressure clears within that window it was cleanup noise, not
            // sustained strain — don't abort the run.
            System.Threading.Thread.Sleep(1000);
            if (IsMemoryCritical(memoryThresholdPct))
            {
                memoryAborted = true;

                // Write aborted_memory for this and all remaining tests
                for (int j = i; j < total; j++)
                {
                    var atc = testCases[j];
                    var ajLast = (j == total - 1);
                    writer.Write("    \"" + atc.Item1 + "\": {");
                    writer.Write("\"status\": \"aborted_memory\", ");
                    writer.Write("\"measure\": \"" + JsonEscape(atc.Item2) + "\", ");
                    writer.Write("\"context\": \"" + atc.Item3 + "\", ");
                    writer.Write("\"duration_ms\": 0");
                    writer.WriteLine("}" + (ajLast ? "" : ","));
                    abortedMemoryCount++;
                    timingRows.Add(Tuple.Create(atc.Item1, atc.Item2, atc.Item3, "aborted_memory", 0, 0L));
                }
                writer.Flush();
                break;
            }
        }

        // ── Skip measures that failed smoke test ───────────────────────────
        if (skippedMeasures.Contains(measureName))
        {
            skipCount++;
            writer.Write("    \"" + testId + "\": {");
            writer.Write("\"status\": \"skipped\", ");
            writer.Write("\"measure\": \"" + JsonEscape(measureName) + "\", ");
            writer.Write("\"context\": \"" + context + "\", ");
            writer.Write("\"skip_reason\": \"" + JsonEscape(smokeResults[measureName]) + "\", ");
            writer.Write("\"duration_ms\": 0");
            writer.WriteLine("}" + (isLast ? "" : ","));
            writer.Flush();
            timingRows.Add(Tuple.Create(testId, measureName, context, "skipped", 0, 0L));
            continue;
        }

        // ── Execute query with timeout ─────────────────────────────────────
        var tcSw = System.Diagnostics.Stopwatch.StartNew();
        var result = ExecuteDaxWithTimeout("EVALUATE " + dax, queryTimeoutMs);
        tcSw.Stop();

        var resultStatus = result.Item2;
        var resultDt = result.Item1;
        var resultError = result.Item3;

        if (resultStatus == "ok")
        {
            okCount++;

            writer.Write("    \"" + testId + "\": {");
            writer.Write("\"status\": \"ok\", ");
            writer.Write("\"measure\": \"" + JsonEscape(measureName) + "\", ");
            writer.Write("\"context\": \"" + context + "\", ");

            if (resultDt == null || resultDt.Rows.Count == 0)
            {
                writer.Write("\"row_count\": 0, \"columns\": [], \"rows\": [], ");
                writer.Write("\"duration_ms\": " + tcSw.ElapsedMilliseconds);
                writer.WriteLine("}" + (isLast ? "" : ","));
                writer.Flush();
                timingRows.Add(Tuple.Create(testId, measureName, context, "ok", 0, tcSw.ElapsedMilliseconds));
                if (diagnosticMode)
                    Info($"[DIAG] {testId} OK — {measureName} [{context}] — 0 rows, {tcSw.ElapsedMilliseconds}ms\nDAX: {dax}");
                continue;
            }

            var colNames = new List<string>();
            foreach (System.Data.DataColumn col in resultDt.Columns)
                colNames.Add(col.ColumnName);

            writer.Write("\"row_count\": " + resultDt.Rows.Count + ", ");
            writer.Write("\"columns\": [" + string.Join(", ", colNames.Select(c => "\"" + JsonEscape(c) + "\"")) + "], ");
            writer.Write("\"rows\": [");

            for (int r = 0; r < resultDt.Rows.Count; r++)
            {
                var row = resultDt.Rows[r];
                var cells = new List<string>();
                foreach (var cname in colNames)
                    cells.Add("\"" + JsonEscape(cname) + "\": " + SerializeValue(row[cname]));
                writer.Write("{" + string.Join(", ", cells) + "}" + (r < resultDt.Rows.Count - 1 ? ", " : ""));
            }

            writer.Write("], ");
            writer.Write("\"duration_ms\": " + tcSw.ElapsedMilliseconds);
            writer.WriteLine("}" + (isLast ? "" : ","));

            // Flush after each test case — keeps memory flat
            writer.Flush();

            var rowCount = resultDt.Rows.Count;
            resultDt.Dispose();

            timingRows.Add(Tuple.Create(testId, measureName, context, "ok", rowCount, tcSw.ElapsedMilliseconds));

            if (diagnosticMode)
                Info($"[DIAG] {testId} OK — {measureName} [{context}] — {rowCount} rows, {tcSw.ElapsedMilliseconds}ms\nDAX: {dax}");
        }
        else if (resultStatus == "timeout")
        {
            timeoutCount++;

            errorLog.AppendLine($"{testId} | {measureName} | {context}");
            errorLog.AppendLine($"  TIMEOUT after {tcSw.ElapsedMilliseconds}ms (limit: {queryTimeoutMs}ms)");
            errorLog.AppendLine($"  DAX: {dax}");
            errorLog.AppendLine();

            string timeoutType;
            var errMsg = resultError ?? "";
            if (errMsg.IndexOf("memory threshold", StringComparison.OrdinalIgnoreCase) >= 0)
                timeoutType = "memory_watchdog";
            else if (errMsg.IndexOf("wall-clock timeout", StringComparison.OrdinalIgnoreCase) >= 0)
                timeoutType = "query_timeout";
            else
                timeoutType = "query_error"; // defensive fallback — shouldn't normally hit

            timeoutLog.AppendLine($"{testId} | {measureName} | {context} | {tcSw.ElapsedMilliseconds}ms");
            timeoutLog.AppendLine($"  Type: {timeoutType}");
            timeoutLog.AppendLine($"  Reason: {errMsg}");
            timeoutLog.AppendLine($"  DAX: {dax}");
            timeoutLog.AppendLine();

            writer.Write("    \"" + testId + "\": {");
            writer.Write("\"status\": \"timeout\", ");
            writer.Write("\"measure\": \"" + JsonEscape(measureName) + "\", ");
            writer.Write("\"context\": \"" + context + "\", ");
            writer.Write("\"timeout_limit_ms\": " + queryTimeoutMs + ", ");
            writer.Write("\"duration_ms\": " + tcSw.ElapsedMilliseconds + ", ");
            writer.Write("\"dax\": \"" + JsonEscape(dax) + "\", ");
            writer.Write("\"error\": \"" + JsonEscape(resultError ?? "Query timeout") + "\"");
            writer.WriteLine("}" + (isLast ? "" : ","));
            writer.Flush();

            timingRows.Add(Tuple.Create(testId, measureName, context, "timeout", 0, tcSw.ElapsedMilliseconds));

            if (diagnosticMode)
                Info($"[DIAG] {testId} TIMEOUT — {measureName} [{context}] — {tcSw.ElapsedMilliseconds}ms\nDAX: {dax}");
        }
        else // "error"
        {
            errCount++;

            errorLog.AppendLine($"{testId} | {measureName} | {context}");
            errorLog.AppendLine($"  DAX: {dax}");
            errorLog.AppendLine($"  Error: {resultError}");
            errorLog.AppendLine();

            writer.Write("    \"" + testId + "\": {");
            writer.Write("\"status\": \"error\", ");
            writer.Write("\"measure\": \"" + JsonEscape(measureName) + "\", ");
            writer.Write("\"context\": \"" + context + "\", ");
            writer.Write("\"error\": \"" + JsonEscape(resultError ?? "") + "\", ");
            writer.Write("\"duration_ms\": " + tcSw.ElapsedMilliseconds);
            writer.WriteLine("}" + (isLast ? "" : ","));
            writer.Flush();

            timingRows.Add(Tuple.Create(testId, measureName, context, "error", 0, tcSw.ElapsedMilliseconds));

            if (diagnosticMode)
                Info($"[DIAG] {testId} ERROR — {measureName} [{context}]\nDAX: {dax}\nError: {resultError}");
        }
    }

    sw.Stop();

    // Smoke test summary for JSON
    int smokeTestedCount = skipOnSmokeTestFailure ? testCases.Select(tc => tc.Item2).Distinct().Count() : 0;
    int smokeSkippedCount = skippedMeasures.Count;

    writer.WriteLine("  },");
    writer.WriteLine("  \"summary\": {");
    writer.WriteLine("    \"total\": " + total + ",");
    writer.WriteLine("    \"ok\": " + okCount + ",");
    writer.WriteLine("    \"error\": " + errCount + ",");
    writer.WriteLine("    \"timeout\": " + timeoutCount + ",");
    writer.WriteLine("    \"skipped\": " + skipCount + ",");
    writer.WriteLine("    \"aborted_memory\": " + abortedMemoryCount + ",");
    writer.WriteLine("    \"smoke_test\": {");
    writer.WriteLine("      \"measures_tested\": " + smokeTestedCount + ",");
    writer.WriteLine("      \"measures_skipped\": " + smokeSkippedCount);
    writer.WriteLine("    },");
    writer.WriteLine("    \"total_duration_ms\": " + sw.ElapsedMilliseconds);
    writer.WriteLine("  }");
    writer.WriteLine("}");
}

if (errCount > 0)
    System.IO.File.WriteAllText(errorLogPath, errorLog.ToString());

if (timeoutCount > 0 || skippedMeasures.Count > 0)
    System.IO.File.WriteAllText(timeoutLogPath, timeoutLog.ToString());

// ═══════════════════════════════════════════════════════════════════════════════
// TIMING CSV — lightweight per-test-case timing for performance comparison
// ═══════════════════════════════════════════════════════════════════════════════
var timingCsvPath = System.IO.Path.Combine(outputDir, snapshotLabel + "-timing.csv");
using (var csvWriter = new System.IO.StreamWriter(timingCsvPath, false, System.Text.Encoding.UTF8))
{
    csvWriter.WriteLine("test_id,measure,context,status,row_count,duration_ms");
    foreach (var t in timingRows)
    {
        csvWriter.WriteLine(
            "\"" + JsonEscape(t.Item1) + "\","
            + "\"" + JsonEscape(t.Item2) + "\","
            + "\"" + JsonEscape(t.Item3) + "\","
            + t.Item4 + ","
            + t.Item5 + ","
            + t.Item6
        );
    }
}

var report = new System.Text.StringBuilder();
report.AppendLine("════════════════════════════════════════════════════════════");
report.AppendLine("  Regression Test Capture Complete");
report.AppendLine("════════════════════════════════════════════════════════════");
report.AppendLine($"  Label:    {snapshotLabel}");
report.AppendLine($"  Tests:    {total}");
report.AppendLine($"  OK:       {okCount}");
report.AppendLine($"  Errors:   {errCount}");
report.AppendLine($"  Timeouts: {timeoutCount}");
report.AppendLine($"  Skipped:  {skipCount}");
if (abortedMemoryCount > 0)
    report.AppendLine($"  Aborted (memory): {abortedMemoryCount}");
report.AppendLine($"  Duration: {sw.Elapsed.TotalMinutes:F1} minutes");
report.AppendLine($"  Output:   {outputPath}");
report.AppendLine($"  Timing:   {timingCsvPath}");
if (globalFilters.Count > 0)
    report.AppendLine($"  Global filters: {globalFilters.Count} applied");
if (maxRowsPerContext > 0)
    report.AppendLine($"  Row cap: TOPN({maxRowsPerContext}) per grouped context");
if (useDirectAdomd)
    report.AppendLine($"  Timeout: {queryTimeoutMs}ms per query (ADOMD direct)");
else
    report.AppendLine($"  Timeout: disabled (useDirectAdomd=false, EvaluateDax fallback)");
if (skipOnSmokeTestFailure && skippedMeasures.Count > 0)
{
    report.AppendLine($"  Smoke test: {skippedMeasures.Count} measure(s) skipped");
    foreach (var kvp in smokeResults)
        report.AppendLine($"    - {kvp.Key}: {kvp.Value}");
}
if (errCount > 0)
{
    report.AppendLine($"  Error log: {errorLogPath}");
    report.AppendLine();
    report.AppendLine("  First 3 errors:");
    var errorLines = errorLog.ToString().Split('\n');
    int shown = 0;
    for (int e = 0; e < errorLines.Length && shown < 9; e++)
    {
        report.AppendLine("    " + errorLines[e]);
        shown++;
    }
}
if (timeoutCount > 0)
{
    report.AppendLine($"  Timeout log: {timeoutLogPath}");
    report.AppendLine();
    report.AppendLine("  First 3 timeouts:");
    var timeoutLines = timeoutLog.ToString().Split('\n');
    int shownT = 0;
    for (int e = 0; e < timeoutLines.Length && shownT < 9; e++)
    {
        report.AppendLine("    " + timeoutLines[e]);
        shownT++;
    }
}
if (memoryAborted)
    report.AppendLine($"  ⚠ Run ABORTED due to memory threshold ({memoryThresholdPct}%)");
report.AppendLine("════════════════════════════════════════════════════════════");

// ═══════════════════════════════════════════════════════════════════════════════
// TEAMS NOTIFICATION
// ═══════════════════════════════════════════════════════════════════════════════
if (!string.IsNullOrWhiteSpace(teamsWebhookUrl))
{
    try
    {
        var isClean = errCount == 0 && timeoutCount == 0 && skipCount == 0 && abortedMemoryCount == 0;
        var status = isClean ? "✅ All Passed" :
            $"⚠️ {errCount} errors / {timeoutCount} timeouts / {skipCount} skipped" +
            (abortedMemoryCount > 0 ? $" / {abortedMemoryCount} aborted" : "");
        var cardJson = "{"
            + "\"type\": \"message\","
            + "\"attachments\": [{"
            + "\"contentType\": \"application/vnd.microsoft.card.adaptive\","
            + "\"content\": {"
            + "\"$schema\": \"http://adaptivecards.io/schemas/adaptive-card.json\","
            + "\"type\": \"AdaptiveCard\","
            + "\"version\": \"1.4\","
            + "\"body\": ["
            + "{\"type\": \"TextBlock\", \"text\": \"PBI Regression Test Complete\", \"weight\": \"Bolder\", \"size\": \"Medium\"},"
            + "{\"type\": \"FactSet\", \"facts\": ["
            + "{\"title\": \"Label\", \"value\": \"" + snapshotLabel + "\"},"
            + "{\"title\": \"Status\", \"value\": \"" + status + "\"},"
            + "{\"title\": \"Tests\", \"value\": \"" + okCount + " OK / " + errCount + " errors / " + timeoutCount + " timeouts / " + total + " total\"},"
            + "{\"title\": \"Duration\", \"value\": \"" + sw.Elapsed.TotalMinutes.ToString("F1") + " min\"}"
            + (skipCount > 0 ? ",{\"title\": \"Skipped\", \"value\": \"" + skipCount + " (smoke test)\"}" : "")
            + (abortedMemoryCount > 0 ? ",{\"title\": \"Aborted (memory)\", \"value\": \"" + abortedMemoryCount + "\"}" : "")
            + "]}"
            + "]"
            + "}"
            + "}]"
            + "}";

        using (var client = new System.Net.WebClient())
        {
            client.Headers[System.Net.HttpRequestHeader.ContentType] = "application/json";
            client.UploadString(teamsWebhookUrl, cardJson);
        }
    }
    catch (Exception webhookEx)
    {
        report.AppendLine($"  ⚠ Teams notification failed: {webhookEx.Message}");
    }
}

Info(report.ToString());
