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
    ?? "mc-baseline";

// Model name — written to the snapshot JSON header for downstream comparison
// reports. Replaced per session by the regression-testing skill (or override
// via the MODEL_NAME env var when running from the CLI).
var modelName = System.Environment.GetEnvironmentVariable("MODEL_NAME")
    ?? "Maintenance and Construction";

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
    "'Calendar'[Year] = 2025",
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
var maxRowsPerContext = 5;   // 0 = no limit; e.g. 5 = cap at 5 rows per test

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
var memoryThresholdPct = _envMemPct != null ? double.Parse(_envMemPct) : 95.0;

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
    "t0001|% of Homes with Open Violations|grand_total",
    "t0002|% of Homes with Open Violations|by_month",
    "t0003|% of Homes with Open Violations|by_prop_toggle",
    "t0004|% of Homes with Open Violations|by_market",
    "t0005|% of Homes with Open Violations|by_scope",
    "t0006|% of Homes with Open Violations|by_wo_status",
    "t0007|% of Homes with Open Violations|by_vendor",
    "t0008|% of Homes with Open Violations|by_repair_type",
    "t0009|% of Homes with Open Violations|by_market_x_month",
    "t0010|% of Homes with Open Violations|by_wo_status_x_month",
    "t0011|% of Properties with In House Move In Work Orders|grand_total",
    "t0012|% of Properties with In House Move In Work Orders|by_month",
    "t0013|% of Properties with In House Move In Work Orders|by_prop_toggle",
    "t0014|% of Properties with In House Move In Work Orders|by_market",
    "t0015|% of Properties with In House Move In Work Orders|by_scope",
    "t0016|% of Properties with In House Move In Work Orders|by_wo_status",
    "t0017|% of Properties with In House Move In Work Orders|by_vendor",
    "t0018|% of Properties with In House Move In Work Orders|by_repair_type",
    "t0019|% of Properties with In House Move In Work Orders|by_market_x_month",
    "t0020|% of Properties with In House Move In Work Orders|by_wo_status_x_month",
    "t0021|% of Properties with Move In Work Orders|grand_total",
    "t0022|% of Properties with Move In Work Orders|by_month",
    "t0023|% of Properties with Move In Work Orders|by_prop_toggle",
    "t0024|% of Properties with Move In Work Orders|by_market",
    "t0025|% of Properties with Move In Work Orders|by_scope",
    "t0026|% of Properties with Move In Work Orders|by_wo_status",
    "t0027|% of Properties with Move In Work Orders|by_vendor",
    "t0028|% of Properties with Move In Work Orders|by_repair_type",
    "t0029|% of Properties with Move In Work Orders|by_market_x_month",
    "t0030|% of Properties with Move In Work Orders|by_wo_status_x_month",
    "t0031|% of Properties with Vendor Move In Work Orders|grand_total",
    "t0032|% of Properties with Vendor Move In Work Orders|by_month",
    "t0033|% of Properties with Vendor Move In Work Orders|by_prop_toggle",
    "t0034|% of Properties with Vendor Move In Work Orders|by_market",
    "t0035|% of Properties with Vendor Move In Work Orders|by_scope",
    "t0036|% of Properties with Vendor Move In Work Orders|by_wo_status",
    "t0037|% of Properties with Vendor Move In Work Orders|by_vendor",
    "t0038|% of Properties with Vendor Move In Work Orders|by_repair_type",
    "t0039|% of Properties with Vendor Move In Work Orders|by_market_x_month",
    "t0040|% of Properties with Vendor Move In Work Orders|by_wo_status_x_month",
    "t0041|% of Total Work Orders|grand_total",
    "t0042|% of Total Work Orders|by_month",
    "t0043|% of Total Work Orders|by_prop_toggle",
    "t0044|% of Total Work Orders|by_market",
    "t0045|% of Total Work Orders|by_scope",
    "t0046|% of Total Work Orders|by_wo_status",
    "t0047|% of Total Work Orders|by_vendor",
    "t0048|% of Total Work Orders|by_repair_type",
    "t0049|% of Total Work Orders|by_market_x_month",
    "t0050|% of Total Work Orders|by_wo_status_x_month",
    "t0051|1st & 2nd Warning Violations|grand_total",
    "t0052|1st & 2nd Warning Violations|by_month",
    "t0053|1st & 2nd Warning Violations|by_prop_toggle",
    "t0054|1st & 2nd Warning Violations|by_market",
    "t0055|1st & 2nd Warning Violations|by_scope",
    "t0056|1st & 2nd Warning Violations|by_wo_status",
    "t0057|1st & 2nd Warning Violations|by_vendor",
    "t0058|1st & 2nd Warning Violations|by_repair_type",
    "t0059|1st & 2nd Warning Violations|by_market_x_month",
    "t0060|1st & 2nd Warning Violations|by_wo_status_x_month",
    "t0061|Active CIP-RR Exceptions Project Avg Days Aging|grand_total",
    "t0062|Active CIP-RR Exceptions Project Avg Days Aging|by_month",
    "t0063|Active CIP-RR Exceptions Project Avg Days Aging|by_prop_toggle",
    "t0064|Active CIP-RR Exceptions Project Avg Days Aging|by_market",
    "t0065|Active CIP-RR Exceptions Project Avg Days Aging|by_scope",
    "t0066|Active CIP-RR Exceptions Project Avg Days Aging|by_wo_status",
    "t0067|Active CIP-RR Exceptions Project Avg Days Aging|by_vendor",
    "t0068|Active CIP-RR Exceptions Project Avg Days Aging|by_repair_type",
    "t0069|Active CIP-RR Exceptions Project Avg Days Aging|by_market_x_month",
    "t0070|Active CIP-RR Exceptions Project Avg Days Aging|by_wo_status_x_month",
    "t0071|Active CIP-RR Exceptions Project Avg Days CIP|grand_total",
    "t0072|Active CIP-RR Exceptions Project Avg Days CIP|by_month",
    "t0073|Active CIP-RR Exceptions Project Avg Days CIP|by_prop_toggle",
    "t0074|Active CIP-RR Exceptions Project Avg Days CIP|by_market",
    "t0075|Active CIP-RR Exceptions Project Avg Days CIP|by_scope",
    "t0076|Active CIP-RR Exceptions Project Avg Days CIP|by_wo_status",
    "t0077|Active CIP-RR Exceptions Project Avg Days CIP|by_vendor",
    "t0078|Active CIP-RR Exceptions Project Avg Days CIP|by_repair_type",
    "t0079|Active CIP-RR Exceptions Project Avg Days CIP|by_market_x_month",
    "t0080|Active CIP-RR Exceptions Project Avg Days CIP|by_wo_status_x_month",
    "t0081|Active CIP-RR Pass Rate|grand_total",
    "t0082|Active CIP-RR Pass Rate|by_month",
    "t0083|Active CIP-RR Pass Rate|by_prop_toggle",
    "t0084|Active CIP-RR Pass Rate|by_market",
    "t0085|Active CIP-RR Pass Rate|by_scope",
    "t0086|Active CIP-RR Pass Rate|by_wo_status",
    "t0087|Active CIP-RR Pass Rate|by_vendor",
    "t0088|Active CIP-RR Pass Rate|by_repair_type",
    "t0089|Active CIP-RR Pass Rate|by_market_x_month",
    "t0090|Active CIP-RR Pass Rate|by_wo_status_x_month",
    "t0091|Active MO-RR Pass Rate|grand_total",
    "t0092|Active MO-RR Pass Rate|by_month",
    "t0093|Active MO-RR Pass Rate|by_prop_toggle",
    "t0094|Active MO-RR Pass Rate|by_market",
    "t0095|Active MO-RR Pass Rate|by_scope",
    "t0096|Active MO-RR Pass Rate|by_wo_status",
    "t0097|Active MO-RR Pass Rate|by_vendor",
    "t0098|Active MO-RR Pass Rate|by_repair_type",
    "t0099|Active MO-RR Pass Rate|by_market_x_month",
    "t0100|Active MO-RR Pass Rate|by_wo_status_x_month",
    "t0101|Active Pre-Construction Exceptions Project Avg Days Aging|grand_total",
    "t0102|Active Pre-Construction Exceptions Project Avg Days Aging|by_month",
    "t0103|Active Pre-Construction Exceptions Project Avg Days Aging|by_prop_toggle",
    "t0104|Active Pre-Construction Exceptions Project Avg Days Aging|by_market",
    "t0105|Active Pre-Construction Exceptions Project Avg Days Aging|by_scope",
    "t0106|Active Pre-Construction Exceptions Project Avg Days Aging|by_wo_status",
    "t0107|Active Pre-Construction Exceptions Project Avg Days Aging|by_vendor",
    "t0108|Active Pre-Construction Exceptions Project Avg Days Aging|by_repair_type",
    "t0109|Active Pre-Construction Exceptions Project Avg Days Aging|by_market_x_month",
    "t0110|Active Pre-Construction Exceptions Project Avg Days Aging|by_wo_status_x_month",
    "t0111|Active Pre-Construction Pass Rate|grand_total",
    "t0112|Active Pre-Construction Pass Rate|by_month",
    "t0113|Active Pre-Construction Pass Rate|by_prop_toggle",
    "t0114|Active Pre-Construction Pass Rate|by_market",
    "t0115|Active Pre-Construction Pass Rate|by_scope",
    "t0116|Active Pre-Construction Pass Rate|by_wo_status",
    "t0117|Active Pre-Construction Pass Rate|by_vendor",
    "t0118|Active Pre-Construction Pass Rate|by_repair_type",
    "t0119|Active Pre-Construction Pass Rate|by_market_x_month",
    "t0120|Active Pre-Construction Pass Rate|by_wo_status_x_month",
    "t0121|Annualized Avg Project Cost per Property by Project Complete Date|grand_total",
    "t0122|Annualized Avg Project Cost per Property by Project Complete Date|by_month",
    "t0123|Annualized Avg Project Cost per Property by Project Complete Date|by_prop_toggle",
    "t0124|Annualized Avg Project Cost per Property by Project Complete Date|by_market",
    "t0125|Annualized Avg Project Cost per Property by Project Complete Date|by_scope",
    "t0126|Annualized Avg Project Cost per Property by Project Complete Date|by_wo_status",
    "t0127|Annualized Avg Project Cost per Property by Project Complete Date|by_vendor",
    "t0128|Annualized Avg Project Cost per Property by Project Complete Date|by_repair_type",
    "t0129|Annualized Avg Project Cost per Property by Project Complete Date|by_market_x_month",
    "t0130|Annualized Avg Project Cost per Property by Project Complete Date|by_wo_status_x_month",
    "t0131|Annualized Total Cost Per Property|grand_total",
    "t0132|Annualized Total Cost Per Property|by_month",
    "t0133|Annualized Total Cost Per Property|by_prop_toggle",
    "t0134|Annualized Total Cost Per Property|by_market",
    "t0135|Annualized Total Cost Per Property|by_scope",
    "t0136|Annualized Total Cost Per Property|by_wo_status",
    "t0137|Annualized Total Cost Per Property|by_vendor",
    "t0138|Annualized Total Cost Per Property|by_repair_type",
    "t0139|Annualized Total Cost Per Property|by_market_x_month",
    "t0140|Annualized Total Cost Per Property|by_wo_status_x_month",
    "t0141|Appian fraction count|grand_total",
    "t0142|Appian fraction count|by_month",
    "t0143|Appian fraction count|by_prop_toggle",
    "t0144|Appian fraction count|by_market",
    "t0145|Appian fraction count|by_scope",
    "t0146|Appian fraction count|by_wo_status",
    "t0147|Appian fraction count|by_vendor",
    "t0148|Appian fraction count|by_repair_type",
    "t0149|Appian fraction count|by_market_x_month",
    "t0150|Appian fraction count|by_wo_status_x_month",
    "t0151|Appian total  count|grand_total",
    "t0152|Appian total  count|by_month",
    "t0153|Appian total  count|by_prop_toggle",
    "t0154|Appian total  count|by_market",
    "t0155|Appian total  count|by_scope",
    "t0156|Appian total  count|by_wo_status",
    "t0157|Appian total  count|by_vendor",
    "t0158|Appian total  count|by_repair_type",
    "t0159|Appian total  count|by_market_x_month",
    "t0160|Appian total  count|by_wo_status_x_month",
    "t0161|Appian total available|grand_total",
    "t0162|Appian total available|by_month",
    "t0163|Appian total available|by_prop_toggle",
    "t0164|Appian total available|by_market",
    "t0165|Appian total available|by_scope",
    "t0166|Appian total available|by_wo_status",
    "t0167|Appian total available|by_vendor",
    "t0168|Appian total available|by_repair_type",
    "t0169|Appian total available|by_market_x_month",
    "t0170|Appian total available|by_wo_status_x_month",
    "t0171|Approved Cost Variance|grand_total",
    "t0172|Approved Cost Variance|by_month",
    "t0173|Approved Cost Variance|by_prop_toggle",
    "t0174|Approved Cost Variance|by_market",
    "t0175|Approved Cost Variance|by_scope",
    "t0176|Approved Cost Variance|by_wo_status",
    "t0177|Approved Cost Variance|by_vendor",
    "t0178|Approved Cost Variance|by_repair_type",
    "t0179|Approved Cost Variance|by_market_x_month",
    "t0180|Approved Cost Variance|by_wo_status_x_month",
    "t0181|Approved Cost Variance %|grand_total",
    "t0182|Approved Cost Variance %|by_month",
    "t0183|Approved Cost Variance %|by_prop_toggle",
    "t0184|Approved Cost Variance %|by_market",
    "t0185|Approved Cost Variance %|by_scope",
    "t0186|Approved Cost Variance %|by_wo_status",
    "t0187|Approved Cost Variance %|by_vendor",
    "t0188|Approved Cost Variance %|by_repair_type",
    "t0189|Approved Cost Variance %|by_market_x_month",
    "t0190|Approved Cost Variance %|by_wo_status_x_month",
    "t0191|Available Work order as % of total|grand_total",
    "t0192|Available Work order as % of total|by_month",
    "t0193|Available Work order as % of total|by_prop_toggle",
    "t0194|Available Work order as % of total|by_market",
    "t0195|Available Work order as % of total|by_scope",
    "t0196|Available Work order as % of total|by_wo_status",
    "t0197|Available Work order as % of total|by_vendor",
    "t0198|Available Work order as % of total|by_repair_type",
    "t0199|Available Work order as % of total|by_market_x_month",
    "t0200|Available Work order as % of total|by_wo_status_x_month",
    "t0201|Average Casualty Work Order Costs Complete Date|grand_total",
    "t0202|Average Casualty Work Order Costs Complete Date|by_month",
    "t0203|Average Casualty Work Order Costs Complete Date|by_prop_toggle",
    "t0204|Average Casualty Work Order Costs Complete Date|by_market",
    "t0205|Average Casualty Work Order Costs Complete Date|by_scope",
    "t0206|Average Casualty Work Order Costs Complete Date|by_wo_status",
    "t0207|Average Casualty Work Order Costs Complete Date|by_vendor",
    "t0208|Average Casualty Work Order Costs Complete Date|by_repair_type",
    "t0209|Average Casualty Work Order Costs Complete Date|by_market_x_month",
    "t0210|Average Casualty Work Order Costs Complete Date|by_wo_status_x_month",
    "t0211|Average Cost per Homes Serviced (Budget)|grand_total",
    "t0212|Average Cost per Homes Serviced (Budget)|by_month",
    "t0213|Average Cost per Homes Serviced (Budget)|by_prop_toggle",
    "t0214|Average Cost per Homes Serviced (Budget)|by_market",
    "t0215|Average Cost per Homes Serviced (Budget)|by_scope",
    "t0216|Average Cost per Homes Serviced (Budget)|by_wo_status",
    "t0217|Average Cost per Homes Serviced (Budget)|by_vendor",
    "t0218|Average Cost per Homes Serviced (Budget)|by_repair_type",
    "t0219|Average Cost per Homes Serviced (Budget)|by_market_x_month",
    "t0220|Average Cost per Homes Serviced (Budget)|by_wo_status_x_month",
    "t0221|Average Cost per Property by Work Order Complete Date|grand_total",
    "t0222|Average Cost per Property by Work Order Complete Date|by_month",
    "t0223|Average Cost per Property by Work Order Complete Date|by_prop_toggle",
    "t0224|Average Cost per Property by Work Order Complete Date|by_market",
    "t0225|Average Cost per Property by Work Order Complete Date|by_scope",
    "t0226|Average Cost per Property by Work Order Complete Date|by_wo_status",
    "t0227|Average Cost per Property by Work Order Complete Date|by_vendor",
    "t0228|Average Cost per Property by Work Order Complete Date|by_repair_type",
    "t0229|Average Cost per Property by Work Order Complete Date|by_market_x_month",
    "t0230|Average Cost per Property by Work Order Complete Date|by_wo_status_x_month",
    "t0231|Average Cost per Vendor|grand_total",
    "t0232|Average Cost per Vendor|by_month",
    "t0233|Average Cost per Vendor|by_prop_toggle",
    "t0234|Average Cost per Vendor|by_market",
    "t0235|Average Cost per Vendor|by_scope",
    "t0236|Average Cost per Vendor|by_wo_status",
    "t0237|Average Cost per Vendor|by_vendor",
    "t0238|Average Cost per Vendor|by_repair_type",
    "t0239|Average Cost per Vendor|by_market_x_month",
    "t0240|Average Cost per Vendor|by_wo_status_x_month",
    "t0241|Average Deferred Rehab Project Cost by Completed Date (Budget)|grand_total",
    "t0242|Average Deferred Rehab Project Cost by Completed Date (Budget)|by_month",
    "t0243|Average Deferred Rehab Project Cost by Completed Date (Budget)|by_prop_toggle",
    "t0244|Average Deferred Rehab Project Cost by Completed Date (Budget)|by_market",
    "t0245|Average Deferred Rehab Project Cost by Completed Date (Budget)|by_scope",
    "t0246|Average Deferred Rehab Project Cost by Completed Date (Budget)|by_wo_status",
    "t0247|Average Deferred Rehab Project Cost by Completed Date (Budget)|by_vendor",
    "t0248|Average Deferred Rehab Project Cost by Completed Date (Budget)|by_repair_type",
    "t0249|Average Deferred Rehab Project Cost by Completed Date (Budget)|by_market_x_month",
    "t0250|Average Deferred Rehab Project Cost by Completed Date (Budget)|by_wo_status_x_month",
    "t0251|Average HOA Work Order Costs Complete Date|grand_total",
    "t0252|Average HOA Work Order Costs Complete Date|by_month",
    "t0253|Average HOA Work Order Costs Complete Date|by_prop_toggle",
    "t0254|Average HOA Work Order Costs Complete Date|by_market",
    "t0255|Average HOA Work Order Costs Complete Date|by_scope",
    "t0256|Average HOA Work Order Costs Complete Date|by_wo_status",
    "t0257|Average HOA Work Order Costs Complete Date|by_vendor",
    "t0258|Average HOA Work Order Costs Complete Date|by_repair_type",
    "t0259|Average HOA Work Order Costs Complete Date|by_market_x_month",
    "t0260|Average HOA Work Order Costs Complete Date|by_wo_status_x_month",
    "t0261|Average Home Count|grand_total",
    "t0262|Average Home Count|by_month",
    "t0263|Average Home Count|by_prop_toggle",
    "t0264|Average Home Count|by_market",
    "t0265|Average Home Count|by_scope",
    "t0266|Average Home Count|by_wo_status",
    "t0267|Average Home Count|by_vendor",
    "t0268|Average Home Count|by_repair_type",
    "t0269|Average Home Count|by_market_x_month",
    "t0270|Average Home Count|by_wo_status_x_month",
    "t0271|Average In-House Maintenance Work Order Costs Complete Date|grand_total",
    "t0272|Average In-House Maintenance Work Order Costs Complete Date|by_month",
    "t0273|Average In-House Maintenance Work Order Costs Complete Date|by_prop_toggle",
    "t0274|Average In-House Maintenance Work Order Costs Complete Date|by_market",
    "t0275|Average In-House Maintenance Work Order Costs Complete Date|by_scope",
    "t0276|Average In-House Maintenance Work Order Costs Complete Date|by_wo_status",
    "t0277|Average In-House Maintenance Work Order Costs Complete Date|by_vendor",
    "t0278|Average In-House Maintenance Work Order Costs Complete Date|by_repair_type",
    "t0279|Average In-House Maintenance Work Order Costs Complete Date|by_market_x_month",
    "t0280|Average In-House Maintenance Work Order Costs Complete Date|by_wo_status_x_month",
    "t0281|Average Line Item Amt|grand_total",
    "t0282|Average Line Item Amt|by_month",
    "t0283|Average Line Item Amt|by_prop_toggle",
    "t0284|Average Line Item Amt|by_market",
    "t0285|Average Line Item Amt|by_scope",
    "t0286|Average Line Item Amt|by_wo_status",
    "t0287|Average Line Item Amt|by_vendor",
    "t0288|Average Line Item Amt|by_repair_type",
    "t0289|Average Line Item Amt|by_market_x_month",
    "t0290|Average Line Item Amt|by_wo_status_x_month",
    "t0291|Average Number of Work Orders per Property by Work Order Created Date|grand_total",
    "t0292|Average Number of Work Orders per Property by Work Order Created Date|by_month",
    "t0293|Average Number of Work Orders per Property by Work Order Created Date|by_prop_toggle",
    "t0294|Average Number of Work Orders per Property by Work Order Created Date|by_market",
    "t0295|Average Number of Work Orders per Property by Work Order Created Date|by_scope",
    "t0296|Average Number of Work Orders per Property by Work Order Created Date|by_wo_status",
    "t0297|Average Number of Work Orders per Property by Work Order Created Date|by_vendor",
    "t0298|Average Number of Work Orders per Property by Work Order Created Date|by_repair_type",
    "t0299|Average Number of Work Orders per Property by Work Order Created Date|by_market_x_month",
    "t0300|Average Number of Work Orders per Property by Work Order Created Date|by_wo_status_x_month",
    "t0301|Average Number of Work Orders per Vendor by Work Order Created Date|grand_total",
    "t0302|Average Number of Work Orders per Vendor by Work Order Created Date|by_month",
    "t0303|Average Number of Work Orders per Vendor by Work Order Created Date|by_prop_toggle",
    "t0304|Average Number of Work Orders per Vendor by Work Order Created Date|by_market",
    "t0305|Average Number of Work Orders per Vendor by Work Order Created Date|by_scope",
    "t0306|Average Number of Work Orders per Vendor by Work Order Created Date|by_wo_status",
    "t0307|Average Number of Work Orders per Vendor by Work Order Created Date|by_vendor",
    "t0308|Average Number of Work Orders per Vendor by Work Order Created Date|by_repair_type",
    "t0309|Average Number of Work Orders per Vendor by Work Order Created Date|by_market_x_month",
    "t0310|Average Number of Work Orders per Vendor by Work Order Created Date|by_wo_status_x_month",
    "t0311|Average Project Cost by Completed Date|grand_total",
    "t0312|Average Project Cost by Completed Date|by_month",
    "t0313|Average Project Cost by Completed Date|by_prop_toggle",
    "t0314|Average Project Cost by Completed Date|by_market",
    "t0315|Average Project Cost by Completed Date|by_scope",
    "t0316|Average Project Cost by Completed Date|by_wo_status",
    "t0317|Average Project Cost by Completed Date|by_vendor",
    "t0318|Average Project Cost by Completed Date|by_repair_type",
    "t0319|Average Project Cost by Completed Date|by_market_x_month",
    "t0320|Average Project Cost by Completed Date|by_wo_status_x_month",
    "t0321|Average Project Cost by Created Date|grand_total",
    "t0322|Average Project Cost by Created Date|by_month",
    "t0323|Average Project Cost by Created Date|by_prop_toggle",
    "t0324|Average Project Cost by Created Date|by_market",
    "t0325|Average Project Cost by Created Date|by_scope",
    "t0326|Average Project Cost by Created Date|by_wo_status",
    "t0327|Average Project Cost by Created Date|by_vendor",
    "t0328|Average Project Cost by Created Date|by_repair_type",
    "t0329|Average Project Cost by Created Date|by_market_x_month",
    "t0330|Average Project Cost by Created Date|by_wo_status_x_month",
    "t0331|Average Project Cost by Estimated Completion Date|grand_total",
    "t0332|Average Project Cost by Estimated Completion Date|by_month",
    "t0333|Average Project Cost by Estimated Completion Date|by_prop_toggle",
    "t0334|Average Project Cost by Estimated Completion Date|by_market",
    "t0335|Average Project Cost by Estimated Completion Date|by_scope",
    "t0336|Average Project Cost by Estimated Completion Date|by_wo_status",
    "t0337|Average Project Cost by Estimated Completion Date|by_vendor",
    "t0338|Average Project Cost by Estimated Completion Date|by_repair_type",
    "t0339|Average Project Cost by Estimated Completion Date|by_market_x_month",
    "t0340|Average Project Cost by Estimated Completion Date|by_wo_status_x_month",
    "t0341|Average Project Cost(Occupancy)|grand_total",
    "t0342|Average Project Cost(Occupancy)|by_month",
    "t0343|Average Project Cost(Occupancy)|by_prop_toggle",
    "t0344|Average Project Cost(Occupancy)|by_market",
    "t0345|Average Project Cost(Occupancy)|by_scope",
    "t0346|Average Project Cost(Occupancy)|by_wo_status",
    "t0347|Average Project Cost(Occupancy)|by_vendor",
    "t0348|Average Project Cost(Occupancy)|by_repair_type",
    "t0349|Average Project Cost(Occupancy)|by_market_x_month",
    "t0350|Average Project Cost(Occupancy)|by_wo_status_x_month",
    "t0351|Average Resident Tenure by Project Complete Date|grand_total",
    "t0352|Average Resident Tenure by Project Complete Date|by_month",
    "t0353|Average Resident Tenure by Project Complete Date|by_prop_toggle",
    "t0354|Average Resident Tenure by Project Complete Date|by_market",
    "t0355|Average Resident Tenure by Project Complete Date|by_scope",
    "t0356|Average Resident Tenure by Project Complete Date|by_wo_status",
    "t0357|Average Resident Tenure by Project Complete Date|by_vendor",
    "t0358|Average Resident Tenure by Project Complete Date|by_repair_type",
    "t0359|Average Resident Tenure by Project Complete Date|by_market_x_month",
    "t0360|Average Resident Tenure by Project Complete Date|by_wo_status_x_month",
    "t0361|Average Response time|grand_total",
    "t0362|Average Response time|by_month",
    "t0363|Average Response time|by_prop_toggle",
    "t0364|Average Response time|by_market",
    "t0365|Average Response time|by_scope",
    "t0366|Average Response time|by_wo_status",
    "t0367|Average Response time|by_vendor",
    "t0368|Average Response time|by_repair_type",
    "t0369|Average Response time|by_market_x_month",
    "t0370|Average Response time|by_wo_status_x_month",
    "t0371|Average Tenure of Turn|grand_total",
    "t0372|Average Tenure of Turn|by_month",
    "t0373|Average Tenure of Turn|by_prop_toggle",
    "t0374|Average Tenure of Turn|by_market",
    "t0375|Average Tenure of Turn|by_scope",
    "t0376|Average Tenure of Turn|by_wo_status",
    "t0377|Average Tenure of Turn|by_vendor",
    "t0378|Average Tenure of Turn|by_repair_type",
    "t0379|Average Tenure of Turn|by_market_x_month",
    "t0380|Average Tenure of Turn|by_wo_status_x_month",
    "t0381|Average Turn Project Cost by Completed Date (Budget)|grand_total",
    "t0382|Average Turn Project Cost by Completed Date (Budget)|by_month",
    "t0383|Average Turn Project Cost by Completed Date (Budget)|by_prop_toggle",
    "t0384|Average Turn Project Cost by Completed Date (Budget)|by_market",
    "t0385|Average Turn Project Cost by Completed Date (Budget)|by_scope",
    "t0386|Average Turn Project Cost by Completed Date (Budget)|by_wo_status",
    "t0387|Average Turn Project Cost by Completed Date (Budget)|by_vendor",
    "t0388|Average Turn Project Cost by Completed Date (Budget)|by_repair_type",
    "t0389|Average Turn Project Cost by Completed Date (Budget)|by_market_x_month",
    "t0390|Average Turn Project Cost by Completed Date (Budget)|by_wo_status_x_month",
    "t0391|Average Work Order Cost by Work Order Completed Date|grand_total",
    "t0392|Average Work Order Cost by Work Order Completed Date|by_month",
    "t0393|Average Work Order Cost by Work Order Completed Date|by_prop_toggle",
    "t0394|Average Work Order Cost by Work Order Completed Date|by_market",
    "t0395|Average Work Order Cost by Work Order Completed Date|by_scope",
    "t0396|Average Work Order Cost by Work Order Completed Date|by_wo_status",
    "t0397|Average Work Order Cost by Work Order Completed Date|by_vendor",
    "t0398|Average Work Order Cost by Work Order Completed Date|by_repair_type",
    "t0399|Average Work Order Cost by Work Order Completed Date|by_market_x_month",
    "t0400|Average Work Order Cost by Work Order Completed Date|by_wo_status_x_month",
    "t0401|Average Work Order Cost by Work Order Created Date|grand_total",
    "t0402|Average Work Order Cost by Work Order Created Date|by_month",
    "t0403|Average Work Order Cost by Work Order Created Date|by_prop_toggle",
    "t0404|Average Work Order Cost by Work Order Created Date|by_market",
    "t0405|Average Work Order Cost by Work Order Created Date|by_scope",
    "t0406|Average Work Order Cost by Work Order Created Date|by_wo_status",
    "t0407|Average Work Order Cost by Work Order Created Date|by_vendor",
    "t0408|Average Work Order Cost by Work Order Created Date|by_repair_type",
    "t0409|Average Work Order Cost by Work Order Created Date|by_market_x_month",
    "t0410|Average Work Order Cost by Work Order Created Date|by_wo_status_x_month",
    "t0411|Average Work Order Days To Complete by Completed Date|grand_total",
    "t0412|Average Work Order Days To Complete by Completed Date|by_month",
    "t0413|Average Work Order Days To Complete by Completed Date|by_prop_toggle",
    "t0414|Average Work Order Days To Complete by Completed Date|by_market",
    "t0415|Average Work Order Days To Complete by Completed Date|by_scope",
    "t0416|Average Work Order Days To Complete by Completed Date|by_wo_status",
    "t0417|Average Work Order Days To Complete by Completed Date|by_vendor",
    "t0418|Average Work Order Days To Complete by Completed Date|by_repair_type",
    "t0419|Average Work Order Days To Complete by Completed Date|by_market_x_month",
    "t0420|Average Work Order Days To Complete by Completed Date|by_wo_status_x_month",
    "t0421|Average Work Order Days To Complete by Created Date|grand_total",
    "t0422|Average Work Order Days To Complete by Created Date|by_month",
    "t0423|Average Work Order Days To Complete by Created Date|by_prop_toggle",
    "t0424|Average Work Order Days To Complete by Created Date|by_market",
    "t0425|Average Work Order Days To Complete by Created Date|by_scope",
    "t0426|Average Work Order Days To Complete by Created Date|by_wo_status",
    "t0427|Average Work Order Days To Complete by Created Date|by_vendor",
    "t0428|Average Work Order Days To Complete by Created Date|by_repair_type",
    "t0429|Average Work Order Days To Complete by Created Date|by_market_x_month",
    "t0430|Average Work Order Days To Complete by Created Date|by_wo_status_x_month",
    "t0431|Average Year Built by Project completed date|grand_total",
    "t0432|Average Year Built by Project completed date|by_month",
    "t0433|Average Year Built by Project completed date|by_prop_toggle",
    "t0434|Average Year Built by Project completed date|by_market",
    "t0435|Average Year Built by Project completed date|by_scope",
    "t0436|Average Year Built by Project completed date|by_wo_status",
    "t0437|Average Year Built by Project completed date|by_vendor",
    "t0438|Average Year Built by Project completed date|by_repair_type",
    "t0439|Average Year Built by Project completed date|by_market_x_month",
    "t0440|Average Year Built by Project completed date|by_wo_status_x_month",
    "t0441|Avg Completed Occupied Maintenance Project Cost|grand_total",
    "t0442|Avg Completed Occupied Maintenance Project Cost|by_month",
    "t0443|Avg Completed Occupied Maintenance Project Cost|by_prop_toggle",
    "t0444|Avg Completed Occupied Maintenance Project Cost|by_market",
    "t0445|Avg Completed Occupied Maintenance Project Cost|by_scope",
    "t0446|Avg Completed Occupied Maintenance Project Cost|by_wo_status",
    "t0447|Avg Completed Occupied Maintenance Project Cost|by_vendor",
    "t0448|Avg Completed Occupied Maintenance Project Cost|by_repair_type",
    "t0449|Avg Completed Occupied Maintenance Project Cost|by_market_x_month",
    "t0450|Avg Completed Occupied Maintenance Project Cost|by_wo_status_x_month",
    "t0451|Avg Daily Open Properties %|grand_total",
    "t0452|Avg Daily Open Properties %|by_month",
    "t0453|Avg Daily Open Properties %|by_prop_toggle",
    "t0454|Avg Daily Open Properties %|by_market",
    "t0455|Avg Daily Open Properties %|by_scope",
    "t0456|Avg Daily Open Properties %|by_wo_status",
    "t0457|Avg Daily Open Properties %|by_vendor",
    "t0458|Avg Daily Open Properties %|by_repair_type",
    "t0459|Avg Daily Open Properties %|by_market_x_month",
    "t0460|Avg Daily Open Properties %|by_wo_status_x_month",
    "t0461|Avg Daily Open Properties Count|grand_total",
    "t0462|Avg Daily Open Properties Count|by_month",
    "t0463|Avg Daily Open Properties Count|by_prop_toggle",
    "t0464|Avg Daily Open Properties Count|by_market",
    "t0465|Avg Daily Open Properties Count|by_scope",
    "t0466|Avg Daily Open Properties Count|by_wo_status",
    "t0467|Avg Daily Open Properties Count|by_vendor",
    "t0468|Avg Daily Open Properties Count|by_repair_type",
    "t0469|Avg Daily Open Properties Count|by_market_x_month",
    "t0470|Avg Daily Open Properties Count|by_wo_status_x_month",
    "t0471|Avg Daily Open Vendors Count|grand_total",
    "t0472|Avg Daily Open Vendors Count|by_month",
    "t0473|Avg Daily Open Vendors Count|by_prop_toggle",
    "t0474|Avg Daily Open Vendors Count|by_market",
    "t0475|Avg Daily Open Vendors Count|by_scope",
    "t0476|Avg Daily Open Vendors Count|by_wo_status",
    "t0477|Avg Daily Open Vendors Count|by_vendor",
    "t0478|Avg Daily Open Vendors Count|by_repair_type",
    "t0479|Avg Daily Open Vendors Count|by_market_x_month",
    "t0480|Avg Daily Open Vendors Count|by_wo_status_x_month",
    "t0481|Avg Move In Work Order Cost|grand_total",
    "t0482|Avg Move In Work Order Cost|by_month",
    "t0483|Avg Move In Work Order Cost|by_prop_toggle",
    "t0484|Avg Move In Work Order Cost|by_market",
    "t0485|Avg Move In Work Order Cost|by_scope",
    "t0486|Avg Move In Work Order Cost|by_wo_status",
    "t0487|Avg Move In Work Order Cost|by_vendor",
    "t0488|Avg Move In Work Order Cost|by_repair_type",
    "t0489|Avg Move In Work Order Cost|by_market_x_month",
    "t0490|Avg Move In Work Order Cost|by_wo_status_x_month",
    "t0491|Avg Property Underwritten Budget $|grand_total",
    "t0492|Avg Property Underwritten Budget $|by_month",
    "t0493|Avg Property Underwritten Budget $|by_prop_toggle",
    "t0494|Avg Property Underwritten Budget $|by_market",
    "t0495|Avg Property Underwritten Budget $|by_scope",
    "t0496|Avg Property Underwritten Budget $|by_wo_status",
    "t0497|Avg Property Underwritten Budget $|by_vendor",
    "t0498|Avg Property Underwritten Budget $|by_repair_type",
    "t0499|Avg Property Underwritten Budget $|by_market_x_month",
    "t0500|Avg Property Underwritten Budget $|by_wo_status_x_month",
    "t0501|Avg Work Order Age|grand_total",
    "t0502|Avg Work Order Age|by_month",
    "t0503|Avg Work Order Age|by_prop_toggle",
    "t0504|Avg Work Order Age|by_market",
    "t0505|Avg Work Order Age|by_scope",
    "t0506|Avg Work Order Age|by_wo_status",
    "t0507|Avg Work Order Age|by_vendor",
    "t0508|Avg Work Order Age|by_repair_type",
    "t0509|Avg Work Order Age|by_market_x_month",
    "t0510|Avg Work Order Age|by_wo_status_x_month",
    "t0511|Canceled Violations|grand_total",
    "t0512|Canceled Violations|by_month",
    "t0513|Canceled Violations|by_prop_toggle",
    "t0514|Canceled Violations|by_market",
    "t0515|Canceled Violations|by_scope",
    "t0516|Canceled Violations|by_wo_status",
    "t0517|Canceled Violations|by_vendor",
    "t0518|Canceled Violations|by_repair_type",
    "t0519|Canceled Violations|by_market_x_month",
    "t0520|Canceled Violations|by_wo_status_x_month",
    "t0521|Closed  Violations|grand_total",
    "t0522|Closed  Violations|by_month",
    "t0523|Closed  Violations|by_prop_toggle",
    "t0524|Closed  Violations|by_market",
    "t0525|Closed  Violations|by_scope",
    "t0526|Closed  Violations|by_wo_status",
    "t0527|Closed  Violations|by_vendor",
    "t0528|Closed  Violations|by_repair_type",
    "t0529|Closed  Violations|by_market_x_month",
    "t0530|Closed  Violations|by_wo_status_x_month",
    "t0531|Column Space|grand_total",
    "t0532|Column Space|by_month",
    "t0533|Column Space|by_prop_toggle",
    "t0534|Column Space|by_market",
    "t0535|Column Space|by_scope",
    "t0536|Column Space|by_wo_status",
    "t0537|Column Space|by_vendor",
    "t0538|Column Space|by_repair_type",
    "t0539|Column Space|by_market_x_month",
    "t0540|Column Space|by_wo_status_x_month",
    "t0541|Completed CIP-RR Pass Rate|grand_total",
    "t0542|Completed CIP-RR Pass Rate|by_month",
    "t0543|Completed CIP-RR Pass Rate|by_prop_toggle",
    "t0544|Completed CIP-RR Pass Rate|by_market",
    "t0545|Completed CIP-RR Pass Rate|by_scope",
    "t0546|Completed CIP-RR Pass Rate|by_wo_status",
    "t0547|Completed CIP-RR Pass Rate|by_vendor",
    "t0548|Completed CIP-RR Pass Rate|by_repair_type",
    "t0549|Completed CIP-RR Pass Rate|by_market_x_month",
    "t0550|Completed CIP-RR Pass Rate|by_wo_status_x_month",
    "t0551|Completed MO-RR Pass Rate|grand_total",
    "t0552|Completed MO-RR Pass Rate|by_month",
    "t0553|Completed MO-RR Pass Rate|by_prop_toggle",
    "t0554|Completed MO-RR Pass Rate|by_market",
    "t0555|Completed MO-RR Pass Rate|by_scope",
    "t0556|Completed MO-RR Pass Rate|by_wo_status",
    "t0557|Completed MO-RR Pass Rate|by_vendor",
    "t0558|Completed MO-RR Pass Rate|by_repair_type",
    "t0559|Completed MO-RR Pass Rate|by_market_x_month",
    "t0560|Completed MO-RR Pass Rate|by_wo_status_x_month",
    "t0561|Completed Occupied Maintenance Project Costs|grand_total",
    "t0562|Completed Occupied Maintenance Project Costs|by_month",
    "t0563|Completed Occupied Maintenance Project Costs|by_prop_toggle",
    "t0564|Completed Occupied Maintenance Project Costs|by_market",
    "t0565|Completed Occupied Maintenance Project Costs|by_scope",
    "t0566|Completed Occupied Maintenance Project Costs|by_wo_status",
    "t0567|Completed Occupied Maintenance Project Costs|by_vendor",
    "t0568|Completed Occupied Maintenance Project Costs|by_repair_type",
    "t0569|Completed Occupied Maintenance Project Costs|by_market_x_month",
    "t0570|Completed Occupied Maintenance Project Costs|by_wo_status_x_month",
    "t0571|Completed Pre-Construction Pass Rate|grand_total",
    "t0572|Completed Pre-Construction Pass Rate|by_month",
    "t0573|Completed Pre-Construction Pass Rate|by_prop_toggle",
    "t0574|Completed Pre-Construction Pass Rate|by_market",
    "t0575|Completed Pre-Construction Pass Rate|by_scope",
    "t0576|Completed Pre-Construction Pass Rate|by_wo_status",
    "t0577|Completed Pre-Construction Pass Rate|by_vendor",
    "t0578|Completed Pre-Construction Pass Rate|by_repair_type",
    "t0579|Completed Pre-Construction Pass Rate|by_market_x_month",
    "t0580|Completed Pre-Construction Pass Rate|by_wo_status_x_month",
    "t0581|Count of Move Ins By Resident Move In Date|grand_total",
    "t0582|Count of Move Ins By Resident Move In Date|by_month",
    "t0583|Count of Move Ins By Resident Move In Date|by_prop_toggle",
    "t0584|Count of Move Ins By Resident Move In Date|by_market",
    "t0585|Count of Move Ins By Resident Move In Date|by_scope",
    "t0586|Count of Move Ins By Resident Move In Date|by_wo_status",
    "t0587|Count of Move Ins By Resident Move In Date|by_vendor",
    "t0588|Count of Move Ins By Resident Move In Date|by_repair_type",
    "t0589|Count of Move Ins By Resident Move In Date|by_market_x_month",
    "t0590|Count of Move Ins By Resident Move In Date|by_wo_status_x_month",
    "t0591|Count of Move Out Exception Properties|grand_total",
    "t0592|Count of Move Out Exception Properties|by_month",
    "t0593|Count of Move Out Exception Properties|by_prop_toggle",
    "t0594|Count of Move Out Exception Properties|by_market",
    "t0595|Count of Move Out Exception Properties|by_scope",
    "t0596|Count of Move Out Exception Properties|by_wo_status",
    "t0597|Count of Move Out Exception Properties|by_vendor",
    "t0598|Count of Move Out Exception Properties|by_repair_type",
    "t0599|Count of Move Out Exception Properties|by_market_x_month",
    "t0600|Count of Move Out Exception Properties|by_wo_status_x_month",
    "t0601|Count of Properties Do Not Publish List|grand_total",
    "t0602|Count of Properties Do Not Publish List|by_month",
    "t0603|Count of Properties Do Not Publish List|by_prop_toggle",
    "t0604|Count of Properties Do Not Publish List|by_market",
    "t0605|Count of Properties Do Not Publish List|by_scope",
    "t0606|Count of Properties Do Not Publish List|by_wo_status",
    "t0607|Count of Properties Do Not Publish List|by_vendor",
    "t0608|Count of Properties Do Not Publish List|by_repair_type",
    "t0609|Count of Properties Do Not Publish List|by_market_x_month",
    "t0610|Count of Properties Do Not Publish List|by_wo_status_x_month",
    "t0611|Count of Properties with > 1 Move In Work Orders by Resident Move In Date|grand_total",
    "t0612|Count of Properties with > 1 Move In Work Orders by Resident Move In Date|by_month",
    "t0613|Count of Properties with > 1 Move In Work Orders by Resident Move In Date|by_prop_toggle",
    "t0614|Count of Properties with > 1 Move In Work Orders by Resident Move In Date|by_market",
    "t0615|Count of Properties with > 1 Move In Work Orders by Resident Move In Date|by_scope",
    "t0616|Count of Properties with > 1 Move In Work Orders by Resident Move In Date|by_wo_status",
    "t0617|Count of Properties with > 1 Move In Work Orders by Resident Move In Date|by_vendor",
    "t0618|Count of Properties with > 1 Move In Work Orders by Resident Move In Date|by_repair_type",
    "t0619|Count of Properties with > 1 Move In Work Orders by Resident Move In Date|by_market_x_month",
    "t0620|Count of Properties with > 1 Move In Work Orders by Resident Move In Date|by_wo_status_x_month",
    "t0621|Count of Properties with Move In Work Orders by Resident Move In Date|grand_total",
    "t0622|Count of Properties with Move In Work Orders by Resident Move In Date|by_month",
    "t0623|Count of Properties with Move In Work Orders by Resident Move In Date|by_prop_toggle",
    "t0624|Count of Properties with Move In Work Orders by Resident Move In Date|by_market",
    "t0625|Count of Properties with Move In Work Orders by Resident Move In Date|by_scope",
    "t0626|Count of Properties with Move In Work Orders by Resident Move In Date|by_wo_status",
    "t0627|Count of Properties with Move In Work Orders by Resident Move In Date|by_vendor",
    "t0628|Count of Properties with Move In Work Orders by Resident Move In Date|by_repair_type",
    "t0629|Count of Properties with Move In Work Orders by Resident Move In Date|by_market_x_month",
    "t0630|Count of Properties with Move In Work Orders by Resident Move In Date|by_wo_status_x_month",
    "t0631|Count of Properties with No Self-Tour Smart Home|grand_total",
    "t0632|Count of Properties with No Self-Tour Smart Home|by_month",
    "t0633|Count of Properties with No Self-Tour Smart Home|by_prop_toggle",
    "t0634|Count of Properties with No Self-Tour Smart Home|by_market",
    "t0635|Count of Properties with No Self-Tour Smart Home|by_scope",
    "t0636|Count of Properties with No Self-Tour Smart Home|by_wo_status",
    "t0637|Count of Properties with No Self-Tour Smart Home|by_vendor",
    "t0638|Count of Properties with No Self-Tour Smart Home|by_repair_type",
    "t0639|Count of Properties with No Self-Tour Smart Home|by_market_x_month",
    "t0640|Count of Properties with No Self-Tour Smart Home|by_wo_status_x_month",
    "t0641|Count of Properties with No Virtual Tour/Inside Map|grand_total",
    "t0642|Count of Properties with No Virtual Tour/Inside Map|by_month",
    "t0643|Count of Properties with No Virtual Tour/Inside Map|by_prop_toggle",
    "t0644|Count of Properties with No Virtual Tour/Inside Map|by_market",
    "t0645|Count of Properties with No Virtual Tour/Inside Map|by_scope",
    "t0646|Count of Properties with No Virtual Tour/Inside Map|by_wo_status",
    "t0647|Count of Properties with No Virtual Tour/Inside Map|by_vendor",
    "t0648|Count of Properties with No Virtual Tour/Inside Map|by_repair_type",
    "t0649|Count of Properties with No Virtual Tour/Inside Map|by_market_x_month",
    "t0650|Count of Properties with No Virtual Tour/Inside Map|by_wo_status_x_month",
    "t0651|Count of Properties with Projects by Completed Date|grand_total",
    "t0652|Count of Properties with Projects by Completed Date|by_month",
    "t0653|Count of Properties with Projects by Completed Date|by_prop_toggle",
    "t0654|Count of Properties with Projects by Completed Date|by_market",
    "t0655|Count of Properties with Projects by Completed Date|by_scope",
    "t0656|Count of Properties with Projects by Completed Date|by_wo_status",
    "t0657|Count of Properties with Projects by Completed Date|by_vendor",
    "t0658|Count of Properties with Projects by Completed Date|by_repair_type",
    "t0659|Count of Properties with Projects by Completed Date|by_market_x_month",
    "t0660|Count of Properties with Projects by Completed Date|by_wo_status_x_month",
    "t0661|Count of Properties with Projects by Completed Date (Occupancy) DK THIS IS WRONG DAX|grand_total",
    "t0662|Count of Properties with Projects by Completed Date (Occupancy) DK THIS IS WRONG DAX|by_month",
    "t0663|Count of Properties with Projects by Completed Date (Occupancy) DK THIS IS WRONG DAX|by_prop_toggle",
    "t0664|Count of Properties with Projects by Completed Date (Occupancy) DK THIS IS WRONG DAX|by_market",
    "t0665|Count of Properties with Projects by Completed Date (Occupancy) DK THIS IS WRONG DAX|by_scope",
    "t0666|Count of Properties with Projects by Completed Date (Occupancy) DK THIS IS WRONG DAX|by_wo_status",
    "t0667|Count of Properties with Projects by Completed Date (Occupancy) DK THIS IS WRONG DAX|by_vendor",
    "t0668|Count of Properties with Projects by Completed Date (Occupancy) DK THIS IS WRONG DAX|by_repair_type",
    "t0669|Count of Properties with Projects by Completed Date (Occupancy) DK THIS IS WRONG DAX|by_market_x_month",
    "t0670|Count of Properties with Projects by Completed Date (Occupancy) DK THIS IS WRONG DAX|by_wo_status_x_month",
    "t0671|Count of Properties with Projects by Created Date|grand_total",
    "t0672|Count of Properties with Projects by Created Date|by_month",
    "t0673|Count of Properties with Projects by Created Date|by_prop_toggle",
    "t0674|Count of Properties with Projects by Created Date|by_market",
    "t0675|Count of Properties with Projects by Created Date|by_scope",
    "t0676|Count of Properties with Projects by Created Date|by_wo_status",
    "t0677|Count of Properties with Projects by Created Date|by_vendor",
    "t0678|Count of Properties with Projects by Created Date|by_repair_type",
    "t0679|Count of Properties with Projects by Created Date|by_market_x_month",
    "t0680|Count of Properties with Projects by Created Date|by_wo_status_x_month",
    "t0681|Count of Properties with Smart Homes by Gateway Assignment Date|grand_total",
    "t0682|Count of Properties with Smart Homes by Gateway Assignment Date|by_month",
    "t0683|Count of Properties with Smart Homes by Gateway Assignment Date|by_prop_toggle",
    "t0684|Count of Properties with Smart Homes by Gateway Assignment Date|by_market",
    "t0685|Count of Properties with Smart Homes by Gateway Assignment Date|by_scope",
    "t0686|Count of Properties with Smart Homes by Gateway Assignment Date|by_wo_status",
    "t0687|Count of Properties with Smart Homes by Gateway Assignment Date|by_vendor",
    "t0688|Count of Properties with Smart Homes by Gateway Assignment Date|by_repair_type",
    "t0689|Count of Properties with Smart Homes by Gateway Assignment Date|by_market_x_month",
    "t0690|Count of Properties with Smart Homes by Gateway Assignment Date|by_wo_status_x_month",
    "t0691|Count of Properties(Acquisitions)|grand_total",
    "t0692|Count of Properties(Acquisitions)|by_month",
    "t0693|Count of Properties(Acquisitions)|by_prop_toggle",
    "t0694|Count of Properties(Acquisitions)|by_market",
    "t0695|Count of Properties(Acquisitions)|by_scope",
    "t0696|Count of Properties(Acquisitions)|by_wo_status",
    "t0697|Count of Properties(Acquisitions)|by_vendor",
    "t0698|Count of Properties(Acquisitions)|by_repair_type",
    "t0699|Count of Properties(Acquisitions)|by_market_x_month",
    "t0700|Count of Properties(Acquisitions)|by_wo_status_x_month",
    "t0701|Count of Rent Ready Exception Projects|grand_total",
    "t0702|Count of Rent Ready Exception Projects|by_month",
    "t0703|Count of Rent Ready Exception Projects|by_prop_toggle",
    "t0704|Count of Rent Ready Exception Projects|by_market",
    "t0705|Count of Rent Ready Exception Projects|by_scope",
    "t0706|Count of Rent Ready Exception Projects|by_wo_status",
    "t0707|Count of Rent Ready Exception Projects|by_vendor",
    "t0708|Count of Rent Ready Exception Projects|by_repair_type",
    "t0709|Count of Rent Ready Exception Projects|by_market_x_month",
    "t0710|Count of Rent Ready Exception Projects|by_wo_status_x_month",
    "t0711|Count of Rent Ready Exception Properties|grand_total",
    "t0712|Count of Rent Ready Exception Properties|by_month",
    "t0713|Count of Rent Ready Exception Properties|by_prop_toggle",
    "t0714|Count of Rent Ready Exception Properties|by_market",
    "t0715|Count of Rent Ready Exception Properties|by_scope",
    "t0716|Count of Rent Ready Exception Properties|by_wo_status",
    "t0717|Count of Rent Ready Exception Properties|by_vendor",
    "t0718|Count of Rent Ready Exception Properties|by_repair_type",
    "t0719|Count of Rent Ready Exception Properties|by_market_x_month",
    "t0720|Count of Rent Ready Exception Properties|by_wo_status_x_month",
    "t0721|Count of Vacant Properties Missing Scope|grand_total",
    "t0722|Count of Vacant Properties Missing Scope|by_month",
    "t0723|Count of Vacant Properties Missing Scope|by_prop_toggle",
    "t0724|Count of Vacant Properties Missing Scope|by_market",
    "t0725|Count of Vacant Properties Missing Scope|by_scope",
    "t0726|Count of Vacant Properties Missing Scope|by_wo_status",
    "t0727|Count of Vacant Properties Missing Scope|by_vendor",
    "t0728|Count of Vacant Properties Missing Scope|by_repair_type",
    "t0729|Count of Vacant Properties Missing Scope|by_market_x_month",
    "t0730|Count of Vacant Properties Missing Scope|by_wo_status_x_month",
    "t0731|Count of Work Orders|grand_total",
    "t0732|Count of Work Orders|by_month",
    "t0733|Count of Work Orders|by_prop_toggle",
    "t0734|Count of Work Orders|by_market",
    "t0735|Count of Work Orders|by_scope",
    "t0736|Count of Work Orders|by_wo_status",
    "t0737|Count of Work Orders|by_vendor",
    "t0738|Count of Work Orders|by_repair_type",
    "t0739|Count of Work Orders|by_market_x_month",
    "t0740|Count of Work Orders|by_wo_status_x_month",
    "t0741|Count of occupied projects with Workorders by Complete Date DK THIS IS WRONG DAX|grand_total",
    "t0742|Count of occupied projects with Workorders by Complete Date DK THIS IS WRONG DAX|by_month",
    "t0743|Count of occupied projects with Workorders by Complete Date DK THIS IS WRONG DAX|by_prop_toggle",
    "t0744|Count of occupied projects with Workorders by Complete Date DK THIS IS WRONG DAX|by_market",
    "t0745|Count of occupied projects with Workorders by Complete Date DK THIS IS WRONG DAX|by_scope",
    "t0746|Count of occupied projects with Workorders by Complete Date DK THIS IS WRONG DAX|by_wo_status",
    "t0747|Count of occupied projects with Workorders by Complete Date DK THIS IS WRONG DAX|by_vendor",
    "t0748|Count of occupied projects with Workorders by Complete Date DK THIS IS WRONG DAX|by_repair_type",
    "t0749|Count of occupied projects with Workorders by Complete Date DK THIS IS WRONG DAX|by_market_x_month",
    "t0750|Count of occupied projects with Workorders by Complete Date DK THIS IS WRONG DAX|by_wo_status_x_month",
    "t0751|CountOfpCard|grand_total",
    "t0752|CountOfpCard|by_month",
    "t0753|CountOfpCard|by_prop_toggle",
    "t0754|CountOfpCard|by_market",
    "t0755|CountOfpCard|by_scope",
    "t0756|CountOfpCard|by_wo_status",
    "t0757|CountOfpCard|by_vendor",
    "t0758|CountOfpCard|by_repair_type",
    "t0759|CountOfpCard|by_market_x_month",
    "t0760|CountOfpCard|by_wo_status_x_month",
    "t0761|Courtesy Violations|grand_total",
    "t0762|Courtesy Violations|by_month",
    "t0763|Courtesy Violations|by_prop_toggle",
    "t0764|Courtesy Violations|by_market",
    "t0765|Courtesy Violations|by_scope",
    "t0766|Courtesy Violations|by_wo_status",
    "t0767|Courtesy Violations|by_vendor",
    "t0768|Courtesy Violations|by_repair_type",
    "t0769|Courtesy Violations|by_market_x_month",
    "t0770|Courtesy Violations|by_wo_status_x_month",
    "t0771|Cumulative % Properties with Smart Homes by Gateway Assignment Date|grand_total",
    "t0772|Cumulative % Properties with Smart Homes by Gateway Assignment Date|by_month",
    "t0773|Cumulative % Properties with Smart Homes by Gateway Assignment Date|by_prop_toggle",
    "t0774|Cumulative % Properties with Smart Homes by Gateway Assignment Date|by_market",
    "t0775|Cumulative % Properties with Smart Homes by Gateway Assignment Date|by_scope",
    "t0776|Cumulative % Properties with Smart Homes by Gateway Assignment Date|by_wo_status",
    "t0777|Cumulative % Properties with Smart Homes by Gateway Assignment Date|by_vendor",
    "t0778|Cumulative % Properties with Smart Homes by Gateway Assignment Date|by_repair_type",
    "t0779|Cumulative % Properties with Smart Homes by Gateway Assignment Date|by_market_x_month",
    "t0780|Cumulative % Properties with Smart Homes by Gateway Assignment Date|by_wo_status_x_month",
    "t0781|Cumulative Count of Properties with Smart Homes by Gateway Assignment Date|grand_total",
    "t0782|Cumulative Count of Properties with Smart Homes by Gateway Assignment Date|by_month",
    "t0783|Cumulative Count of Properties with Smart Homes by Gateway Assignment Date|by_prop_toggle",
    "t0784|Cumulative Count of Properties with Smart Homes by Gateway Assignment Date|by_market",
    "t0785|Cumulative Count of Properties with Smart Homes by Gateway Assignment Date|by_scope",
    "t0786|Cumulative Count of Properties with Smart Homes by Gateway Assignment Date|by_wo_status",
    "t0787|Cumulative Count of Properties with Smart Homes by Gateway Assignment Date|by_vendor",
    "t0788|Cumulative Count of Properties with Smart Homes by Gateway Assignment Date|by_repair_type",
    "t0789|Cumulative Count of Properties with Smart Homes by Gateway Assignment Date|by_market_x_month",
    "t0790|Cumulative Count of Properties with Smart Homes by Gateway Assignment Date|by_wo_status_x_month",
    "t0791|Current Backlog Ratio|grand_total",
    "t0792|Current Backlog Ratio|by_month",
    "t0793|Current Backlog Ratio|by_prop_toggle",
    "t0794|Current Backlog Ratio|by_market",
    "t0795|Current Backlog Ratio|by_scope",
    "t0796|Current Backlog Ratio|by_wo_status",
    "t0797|Current Backlog Ratio|by_vendor",
    "t0798|Current Backlog Ratio|by_repair_type",
    "t0799|Current Backlog Ratio|by_market_x_month",
    "t0800|Current Backlog Ratio|by_wo_status_x_month",
    "t0801|Cut Off Review Threshold Costs|grand_total",
    "t0802|Cut Off Review Threshold Costs|by_month",
    "t0803|Cut Off Review Threshold Costs|by_prop_toggle",
    "t0804|Cut Off Review Threshold Costs|by_market",
    "t0805|Cut Off Review Threshold Costs|by_scope",
    "t0806|Cut Off Review Threshold Costs|by_wo_status",
    "t0807|Cut Off Review Threshold Costs|by_vendor",
    "t0808|Cut Off Review Threshold Costs|by_repair_type",
    "t0809|Cut Off Review Threshold Costs|by_market_x_month",
    "t0810|Cut Off Review Threshold Costs|by_wo_status_x_month",
    "t0811|Deferred Rehab Labor Cost|grand_total",
    "t0812|Deferred Rehab Labor Cost|by_month",
    "t0813|Deferred Rehab Labor Cost|by_prop_toggle",
    "t0814|Deferred Rehab Labor Cost|by_market",
    "t0815|Deferred Rehab Labor Cost|by_scope",
    "t0816|Deferred Rehab Labor Cost|by_wo_status",
    "t0817|Deferred Rehab Labor Cost|by_vendor",
    "t0818|Deferred Rehab Labor Cost|by_repair_type",
    "t0819|Deferred Rehab Labor Cost|by_market_x_month",
    "t0820|Deferred Rehab Labor Cost|by_wo_status_x_month",
    "t0821|Deferred Rehab Project Count by Completed Date (Budget)|grand_total",
    "t0822|Deferred Rehab Project Count by Completed Date (Budget)|by_month",
    "t0823|Deferred Rehab Project Count by Completed Date (Budget)|by_prop_toggle",
    "t0824|Deferred Rehab Project Count by Completed Date (Budget)|by_market",
    "t0825|Deferred Rehab Project Count by Completed Date (Budget)|by_scope",
    "t0826|Deferred Rehab Project Count by Completed Date (Budget)|by_wo_status",
    "t0827|Deferred Rehab Project Count by Completed Date (Budget)|by_vendor",
    "t0828|Deferred Rehab Project Count by Completed Date (Budget)|by_repair_type",
    "t0829|Deferred Rehab Project Count by Completed Date (Budget)|by_market_x_month",
    "t0830|Deferred Rehab Project Count by Completed Date (Budget)|by_wo_status_x_month",
    "t0831|Delinquent Violations|grand_total",
    "t0832|Delinquent Violations|by_month",
    "t0833|Delinquent Violations|by_prop_toggle",
    "t0834|Delinquent Violations|by_market",
    "t0835|Delinquent Violations|by_scope",
    "t0836|Delinquent Violations|by_wo_status",
    "t0837|Delinquent Violations|by_vendor",
    "t0838|Delinquent Violations|by_repair_type",
    "t0839|Delinquent Violations|by_market_x_month",
    "t0840|Delinquent Violations|by_wo_status_x_month",
    "t0841|Dispo Prep Labor Cost|grand_total",
    "t0842|Dispo Prep Labor Cost|by_month",
    "t0843|Dispo Prep Labor Cost|by_prop_toggle",
    "t0844|Dispo Prep Labor Cost|by_market",
    "t0845|Dispo Prep Labor Cost|by_scope",
    "t0846|Dispo Prep Labor Cost|by_wo_status",
    "t0847|Dispo Prep Labor Cost|by_vendor",
    "t0848|Dispo Prep Labor Cost|by_repair_type",
    "t0849|Dispo Prep Labor Cost|by_market_x_month",
    "t0850|Dispo Prep Labor Cost|by_wo_status_x_month",
    "t0851|FieldServices Data as of TS|grand_total",
    "t0852|FieldServices Data as of TS|by_month",
    "t0853|FieldServices Data as of TS|by_prop_toggle",
    "t0854|FieldServices Data as of TS|by_market",
    "t0855|FieldServices Data as of TS|by_scope",
    "t0856|FieldServices Data as of TS|by_wo_status",
    "t0857|FieldServices Data as of TS|by_vendor",
    "t0858|FieldServices Data as of TS|by_repair_type",
    "t0859|FieldServices Data as of TS|by_market_x_month",
    "t0860|FieldServices Data as of TS|by_wo_status_x_month",
    "t0861|Final Warning Violations|grand_total",
    "t0862|Final Warning Violations|by_month",
    "t0863|Final Warning Violations|by_prop_toggle",
    "t0864|Final Warning Violations|by_market",
    "t0865|Final Warning Violations|by_scope",
    "t0866|Final Warning Violations|by_wo_status",
    "t0867|Final Warning Violations|by_vendor",
    "t0868|Final Warning Violations|by_repair_type",
    "t0869|Final Warning Violations|by_market_x_month",
    "t0870|Final Warning Violations|by_wo_status_x_month",
    "t0871|First Time Fix Rate %|grand_total",
    "t0872|First Time Fix Rate %|by_month",
    "t0873|First Time Fix Rate %|by_prop_toggle",
    "t0874|First Time Fix Rate %|by_market",
    "t0875|First Time Fix Rate %|by_scope",
    "t0876|First Time Fix Rate %|by_wo_status",
    "t0877|First Time Fix Rate %|by_vendor",
    "t0878|First Time Fix Rate %|by_repair_type",
    "t0879|First Time Fix Rate %|by_market_x_month",
    "t0880|First Time Fix Rate %|by_wo_status_x_month",
    "t0881|Homes Serviced (Budget)|grand_total",
    "t0882|Homes Serviced (Budget)|by_month",
    "t0883|Homes Serviced (Budget)|by_prop_toggle",
    "t0884|Homes Serviced (Budget)|by_market",
    "t0885|Homes Serviced (Budget)|by_scope",
    "t0886|Homes Serviced (Budget)|by_wo_status",
    "t0887|Homes Serviced (Budget)|by_vendor",
    "t0888|Homes Serviced (Budget)|by_repair_type",
    "t0889|Homes Serviced (Budget)|by_market_x_month",
    "t0890|Homes Serviced (Budget)|by_wo_status_x_month",
    "t0891|Homes Serviced By Vendor & Technician|grand_total",
    "t0892|Homes Serviced By Vendor & Technician|by_month",
    "t0893|Homes Serviced By Vendor & Technician|by_prop_toggle",
    "t0894|Homes Serviced By Vendor & Technician|by_market",
    "t0895|Homes Serviced By Vendor & Technician|by_scope",
    "t0896|Homes Serviced By Vendor & Technician|by_wo_status",
    "t0897|Homes Serviced By Vendor & Technician|by_vendor",
    "t0898|Homes Serviced By Vendor & Technician|by_repair_type",
    "t0899|Homes Serviced By Vendor & Technician|by_market_x_month",
    "t0900|Homes Serviced By Vendor & Technician|by_wo_status_x_month",
    "t0901|Homes Serviced Internally (Budget)|grand_total",
    "t0902|Homes Serviced Internally (Budget)|by_month",
    "t0903|Homes Serviced Internally (Budget)|by_prop_toggle",
    "t0904|Homes Serviced Internally (Budget)|by_market",
    "t0905|Homes Serviced Internally (Budget)|by_scope",
    "t0906|Homes Serviced Internally (Budget)|by_wo_status",
    "t0907|Homes Serviced Internally (Budget)|by_vendor",
    "t0908|Homes Serviced Internally (Budget)|by_repair_type",
    "t0909|Homes Serviced Internally (Budget)|by_market_x_month",
    "t0910|Homes Serviced Internally (Budget)|by_wo_status_x_month",
    "t0911|Homes Serviced Internally Work Order Costs by Completed Date|grand_total",
    "t0912|Homes Serviced Internally Work Order Costs by Completed Date|by_month",
    "t0913|Homes Serviced Internally Work Order Costs by Completed Date|by_prop_toggle",
    "t0914|Homes Serviced Internally Work Order Costs by Completed Date|by_market",
    "t0915|Homes Serviced Internally Work Order Costs by Completed Date|by_scope",
    "t0916|Homes Serviced Internally Work Order Costs by Completed Date|by_wo_status",
    "t0917|Homes Serviced Internally Work Order Costs by Completed Date|by_vendor",
    "t0918|Homes Serviced Internally Work Order Costs by Completed Date|by_repair_type",
    "t0919|Homes Serviced Internally Work Order Costs by Completed Date|by_market_x_month",
    "t0920|Homes Serviced Internally Work Order Costs by Completed Date|by_wo_status_x_month",
    "t0921|Homes Serviced Internally base|grand_total",
    "t0922|Homes Serviced Internally base|by_month",
    "t0923|Homes Serviced Internally base|by_prop_toggle",
    "t0924|Homes Serviced Internally base|by_market",
    "t0925|Homes Serviced Internally base|by_scope",
    "t0926|Homes Serviced Internally base|by_wo_status",
    "t0927|Homes Serviced Internally base|by_vendor",
    "t0928|Homes Serviced Internally base|by_repair_type",
    "t0929|Homes Serviced Internally base|by_market_x_month",
    "t0930|Homes Serviced Internally base|by_wo_status_x_month",
    "t0931|Homes Serviced by Vendor (Budget)|grand_total",
    "t0932|Homes Serviced by Vendor (Budget)|by_month",
    "t0933|Homes Serviced by Vendor (Budget)|by_prop_toggle",
    "t0934|Homes Serviced by Vendor (Budget)|by_market",
    "t0935|Homes Serviced by Vendor (Budget)|by_scope",
    "t0936|Homes Serviced by Vendor (Budget)|by_wo_status",
    "t0937|Homes Serviced by Vendor (Budget)|by_vendor",
    "t0938|Homes Serviced by Vendor (Budget)|by_repair_type",
    "t0939|Homes Serviced by Vendor (Budget)|by_market_x_month",
    "t0940|Homes Serviced by Vendor (Budget)|by_wo_status_x_month",
    "t0941|Homes Serviced by Vendor base|grand_total",
    "t0942|Homes Serviced by Vendor base|by_month",
    "t0943|Homes Serviced by Vendor base|by_prop_toggle",
    "t0944|Homes Serviced by Vendor base|by_market",
    "t0945|Homes Serviced by Vendor base|by_scope",
    "t0946|Homes Serviced by Vendor base|by_wo_status",
    "t0947|Homes Serviced by Vendor base|by_vendor",
    "t0948|Homes Serviced by Vendor base|by_repair_type",
    "t0949|Homes Serviced by Vendor base|by_market_x_month",
    "t0950|Homes Serviced by Vendor base|by_wo_status_x_month",
    "t0951|Homes with Open  Violations|grand_total",
    "t0952|Homes with Open  Violations|by_month",
    "t0953|Homes with Open  Violations|by_prop_toggle",
    "t0954|Homes with Open  Violations|by_market",
    "t0955|Homes with Open  Violations|by_scope",
    "t0956|Homes with Open  Violations|by_wo_status",
    "t0957|Homes with Open  Violations|by_vendor",
    "t0958|Homes with Open  Violations|by_repair_type",
    "t0959|Homes with Open  Violations|by_market_x_month",
    "t0960|Homes with Open  Violations|by_wo_status_x_month",
    "t0961|IH Labor Cost|grand_total",
    "t0962|IH Labor Cost|by_month",
    "t0963|IH Labor Cost|by_prop_toggle",
    "t0964|IH Labor Cost|by_market",
    "t0965|IH Labor Cost|by_scope",
    "t0966|IH Labor Cost|by_wo_status",
    "t0967|IH Labor Cost|by_vendor",
    "t0968|IH Labor Cost|by_repair_type",
    "t0969|IH Labor Cost|by_market_x_month",
    "t0970|IH Labor Cost|by_wo_status_x_month",
    "t0971|IHM Completed Work Order|grand_total",
    "t0972|IHM Completed Work Order|by_month",
    "t0973|IHM Completed Work Order|by_prop_toggle",
    "t0974|IHM Completed Work Order|by_market",
    "t0975|IHM Completed Work Order|by_scope",
    "t0976|IHM Completed Work Order|by_wo_status",
    "t0977|IHM Completed Work Order|by_vendor",
    "t0978|IHM Completed Work Order|by_repair_type",
    "t0979|IHM Completed Work Order|by_market_x_month",
    "t0980|IHM Completed Work Order|by_wo_status_x_month",
    "t0981|IHM Completed Work Order Count (by Created Date)|grand_total",
    "t0982|IHM Completed Work Order Count (by Created Date)|by_month",
    "t0983|IHM Completed Work Order Count (by Created Date)|by_prop_toggle",
    "t0984|IHM Completed Work Order Count (by Created Date)|by_market",
    "t0985|IHM Completed Work Order Count (by Created Date)|by_scope",
    "t0986|IHM Completed Work Order Count (by Created Date)|by_wo_status",
    "t0987|IHM Completed Work Order Count (by Created Date)|by_vendor",
    "t0988|IHM Completed Work Order Count (by Created Date)|by_repair_type",
    "t0989|IHM Completed Work Order Count (by Created Date)|by_market_x_month",
    "t0990|IHM Completed Work Order Count (by Created Date)|by_wo_status_x_month",
    "t0991|IHM Completion %|grand_total",
    "t0992|IHM Completion %|by_month",
    "t0993|IHM Completion %|by_prop_toggle",
    "t0994|IHM Completion %|by_market",
    "t0995|IHM Completion %|by_scope",
    "t0996|IHM Completion %|by_wo_status",
    "t0997|IHM Completion %|by_vendor",
    "t0998|IHM Completion %|by_repair_type",
    "t0999|IHM Completion %|by_market_x_month",
    "t1000|IHM Completion %|by_wo_status_x_month",
    "t1001|IHM Completion % (by Completed Date)|grand_total",
    "t1002|IHM Completion % (by Completed Date)|by_month",
    "t1003|IHM Completion % (by Completed Date)|by_prop_toggle",
    "t1004|IHM Completion % (by Completed Date)|by_market",
    "t1005|IHM Completion % (by Completed Date)|by_scope",
    "t1006|IHM Completion % (by Completed Date)|by_wo_status",
    "t1007|IHM Completion % (by Completed Date)|by_vendor",
    "t1008|IHM Completion % (by Completed Date)|by_repair_type",
    "t1009|IHM Completion % (by Completed Date)|by_market_x_month",
    "t1010|IHM Completion % (by Completed Date)|by_wo_status_x_month",
    "t1011|IHM WO's Completed|grand_total",
    "t1012|IHM WO's Completed|by_month",
    "t1013|IHM WO's Completed|by_prop_toggle",
    "t1014|IHM WO's Completed|by_market",
    "t1015|IHM WO's Completed|by_scope",
    "t1016|IHM WO's Completed|by_wo_status",
    "t1017|IHM WO's Completed|by_vendor",
    "t1018|IHM WO's Completed|by_repair_type",
    "t1019|IHM WO's Completed|by_market_x_month",
    "t1020|IHM WO's Completed|by_wo_status_x_month",
    "t1021|In House Work order Created|grand_total",
    "t1022|In House Work order Created|by_month",
    "t1023|In House Work order Created|by_prop_toggle",
    "t1024|In House Work order Created|by_market",
    "t1025|In House Work order Created|by_scope",
    "t1026|In House Work order Created|by_wo_status",
    "t1027|In House Work order Created|by_vendor",
    "t1028|In House Work order Created|by_repair_type",
    "t1029|In House Work order Created|by_market_x_month",
    "t1030|In House Work order Created|by_wo_status_x_month",
    "t1031|In House Work order completed|grand_total",
    "t1032|In House Work order completed|by_month",
    "t1033|In House Work order completed|by_prop_toggle",
    "t1034|In House Work order completed|by_market",
    "t1035|In House Work order completed|by_scope",
    "t1036|In House Work order completed|by_wo_status",
    "t1037|In House Work order completed|by_vendor",
    "t1038|In House Work order completed|by_repair_type",
    "t1039|In House Work order completed|by_market_x_month",
    "t1040|In House Work order completed|by_wo_status_x_month",
    "t1041|In-House Maintenance Work Order Costs Complete Date|grand_total",
    "t1042|In-House Maintenance Work Order Costs Complete Date|by_month",
    "t1043|In-House Maintenance Work Order Costs Complete Date|by_prop_toggle",
    "t1044|In-House Maintenance Work Order Costs Complete Date|by_market",
    "t1045|In-House Maintenance Work Order Costs Complete Date|by_scope",
    "t1046|In-House Maintenance Work Order Costs Complete Date|by_wo_status",
    "t1047|In-House Maintenance Work Order Costs Complete Date|by_vendor",
    "t1048|In-House Maintenance Work Order Costs Complete Date|by_repair_type",
    "t1049|In-House Maintenance Work Order Costs Complete Date|by_market_x_month",
    "t1050|In-House Maintenance Work Order Costs Complete Date|by_wo_status_x_month",
    "t1051|In-House Utilization Rate (excl Appliances) Project Cost by Completed Date|grand_total",
    "t1052|In-House Utilization Rate (excl Appliances) Project Cost by Completed Date|by_month",
    "t1053|In-House Utilization Rate (excl Appliances) Project Cost by Completed Date|by_prop_toggle",
    "t1054|In-House Utilization Rate (excl Appliances) Project Cost by Completed Date|by_market",
    "t1055|In-House Utilization Rate (excl Appliances) Project Cost by Completed Date|by_scope",
    "t1056|In-House Utilization Rate (excl Appliances) Project Cost by Completed Date|by_wo_status",
    "t1057|In-House Utilization Rate (excl Appliances) Project Cost by Completed Date|by_vendor",
    "t1058|In-House Utilization Rate (excl Appliances) Project Cost by Completed Date|by_repair_type",
    "t1059|In-House Utilization Rate (excl Appliances) Project Cost by Completed Date|by_market_x_month",
    "t1060|In-House Utilization Rate (excl Appliances) Project Cost by Completed Date|by_wo_status_x_month",
    "t1061|Initial Rehab Labor Cost|grand_total",
    "t1062|Initial Rehab Labor Cost|by_month",
    "t1063|Initial Rehab Labor Cost|by_prop_toggle",
    "t1064|Initial Rehab Labor Cost|by_market",
    "t1065|Initial Rehab Labor Cost|by_scope",
    "t1066|Initial Rehab Labor Cost|by_wo_status",
    "t1067|Initial Rehab Labor Cost|by_vendor",
    "t1068|Initial Rehab Labor Cost|by_repair_type",
    "t1069|Initial Rehab Labor Cost|by_market_x_month",
    "t1070|Initial Rehab Labor Cost|by_wo_status_x_month",
    "t1071|Legal Violations|grand_total",
    "t1072|Legal Violations|by_month",
    "t1073|Legal Violations|by_prop_toggle",
    "t1074|Legal Violations|by_market",
    "t1075|Legal Violations|by_scope",
    "t1076|Legal Violations|by_wo_status",
    "t1077|Legal Violations|by_vendor",
    "t1078|Legal Violations|by_repair_type",
    "t1079|Legal Violations|by_market_x_month",
    "t1080|Legal Violations|by_wo_status_x_month",
    "t1081|Maintenance Labor Cost|grand_total",
    "t1082|Maintenance Labor Cost|by_month",
    "t1083|Maintenance Labor Cost|by_prop_toggle",
    "t1084|Maintenance Labor Cost|by_market",
    "t1085|Maintenance Labor Cost|by_scope",
    "t1086|Maintenance Labor Cost|by_wo_status",
    "t1087|Maintenance Labor Cost|by_vendor",
    "t1088|Maintenance Labor Cost|by_repair_type",
    "t1089|Maintenance Labor Cost|by_market_x_month",
    "t1090|Maintenance Labor Cost|by_wo_status_x_month",
    "t1091|Max|grand_total",
    "t1092|Max|by_month",
    "t1093|Max|by_prop_toggle",
    "t1094|Max|by_market",
    "t1095|Max|by_scope",
    "t1096|Max|by_wo_status",
    "t1097|Max|by_vendor",
    "t1098|Max|by_repair_type",
    "t1099|Max|by_market_x_month",
    "t1100|Max|by_wo_status_x_month",
    "t1101|Median Work Order Costs by Created Date|grand_total",
    "t1102|Median Work Order Costs by Created Date|by_month",
    "t1103|Median Work Order Costs by Created Date|by_prop_toggle",
    "t1104|Median Work Order Costs by Created Date|by_market",
    "t1105|Median Work Order Costs by Created Date|by_scope",
    "t1106|Median Work Order Costs by Created Date|by_wo_status",
    "t1107|Median Work Order Costs by Created Date|by_vendor",
    "t1108|Median Work Order Costs by Created Date|by_repair_type",
    "t1109|Median Work Order Costs by Created Date|by_market_x_month",
    "t1110|Median Work Order Costs by Created Date|by_wo_status_x_month",
    "t1111|Min|grand_total",
    "t1112|Min|by_month",
    "t1113|Min|by_prop_toggle",
    "t1114|Min|by_market",
    "t1115|Min|by_scope",
    "t1116|Min|by_wo_status",
    "t1117|Min|by_vendor",
    "t1118|Min|by_repair_type",
    "t1119|Min|by_market_x_month",
    "t1120|Min|by_wo_status_x_month",
    "t1121|Move In Work Order Costs by Resident Move In Date|grand_total",
    "t1122|Move In Work Order Costs by Resident Move In Date|by_month",
    "t1123|Move In Work Order Costs by Resident Move In Date|by_prop_toggle",
    "t1124|Move In Work Order Costs by Resident Move In Date|by_market",
    "t1125|Move In Work Order Costs by Resident Move In Date|by_scope",
    "t1126|Move In Work Order Costs by Resident Move In Date|by_wo_status",
    "t1127|Move In Work Order Costs by Resident Move In Date|by_vendor",
    "t1128|Move In Work Order Costs by Resident Move In Date|by_repair_type",
    "t1129|Move In Work Order Costs by Resident Move In Date|by_market_x_month",
    "t1130|Move In Work Order Costs by Resident Move In Date|by_wo_status_x_month",
    "t1131|Move In Work Order Count by Resident Move In Date|grand_total",
    "t1132|Move In Work Order Count by Resident Move In Date|by_month",
    "t1133|Move In Work Order Count by Resident Move In Date|by_prop_toggle",
    "t1134|Move In Work Order Count by Resident Move In Date|by_market",
    "t1135|Move In Work Order Count by Resident Move In Date|by_scope",
    "t1136|Move In Work Order Count by Resident Move In Date|by_wo_status",
    "t1137|Move In Work Order Count by Resident Move In Date|by_vendor",
    "t1138|Move In Work Order Count by Resident Move In Date|by_repair_type",
    "t1139|Move In Work Order Count by Resident Move In Date|by_market_x_month",
    "t1140|Move In Work Order Count by Resident Move In Date|by_wo_status_x_month",
    "t1141|Number of technicians|grand_total",
    "t1142|Number of technicians|by_month",
    "t1143|Number of technicians|by_prop_toggle",
    "t1144|Number of technicians|by_market",
    "t1145|Number of technicians|by_scope",
    "t1146|Number of technicians|by_wo_status",
    "t1147|Number of technicians|by_vendor",
    "t1148|Number of technicians|by_repair_type",
    "t1149|Number of technicians|by_market_x_month",
    "t1150|Number of technicians|by_wo_status_x_month",
    "t1151|Occupied Count|grand_total",
    "t1152|Occupied Count|by_month",
    "t1153|Occupied Count|by_prop_toggle",
    "t1154|Occupied Count|by_market",
    "t1155|Occupied Count|by_scope",
    "t1156|Occupied Count|by_wo_status",
    "t1157|Occupied Count|by_vendor",
    "t1158|Occupied Count|by_repair_type",
    "t1159|Occupied Count|by_market_x_month",
    "t1160|Occupied Count|by_wo_status_x_month",
    "t1161|Open  Violations|grand_total",
    "t1162|Open  Violations|by_month",
    "t1163|Open  Violations|by_prop_toggle",
    "t1164|Open  Violations|by_market",
    "t1165|Open  Violations|by_scope",
    "t1166|Open  Violations|by_wo_status",
    "t1167|Open  Violations|by_vendor",
    "t1168|Open  Violations|by_repair_type",
    "t1169|Open  Violations|by_market_x_month",
    "t1170|Open  Violations|by_wo_status_x_month",
    "t1171|P-Cards Cost|grand_total",
    "t1172|P-Cards Cost|by_month",
    "t1173|P-Cards Cost|by_prop_toggle",
    "t1174|P-Cards Cost|by_market",
    "t1175|P-Cards Cost|by_scope",
    "t1176|P-Cards Cost|by_wo_status",
    "t1177|P-Cards Cost|by_vendor",
    "t1178|P-Cards Cost|by_repair_type",
    "t1179|P-Cards Cost|by_market_x_month",
    "t1180|P-Cards Cost|by_wo_status_x_month",
    "t1181|PO Costs|grand_total",
    "t1182|PO Costs|by_month",
    "t1183|PO Costs|by_prop_toggle",
    "t1184|PO Costs|by_market",
    "t1185|PO Costs|by_scope",
    "t1186|PO Costs|by_wo_status",
    "t1187|PO Costs|by_vendor",
    "t1188|PO Costs|by_repair_type",
    "t1189|PO Costs|by_market_x_month",
    "t1190|PO Costs|by_wo_status_x_month",
    "t1191|PO Line Item Count|grand_total",
    "t1192|PO Line Item Count|by_month",
    "t1193|PO Line Item Count|by_prop_toggle",
    "t1194|PO Line Item Count|by_market",
    "t1195|PO Line Item Count|by_scope",
    "t1196|PO Line Item Count|by_wo_status",
    "t1197|PO Line Item Count|by_vendor",
    "t1198|PO Line Item Count|by_repair_type",
    "t1199|PO Line Item Count|by_market_x_month",
    "t1200|PO Line Item Count|by_wo_status_x_month",
    "t1201|PO Work Order Count|grand_total",
    "t1202|PO Work Order Count|by_month",
    "t1203|PO Work Order Count|by_prop_toggle",
    "t1204|PO Work Order Count|by_market",
    "t1205|PO Work Order Count|by_scope",
    "t1206|PO Work Order Count|by_wo_status",
    "t1207|PO Work Order Count|by_vendor",
    "t1208|PO Work Order Count|by_repair_type",
    "t1209|PO Work Order Count|by_market_x_month",
    "t1210|PO Work Order Count|by_wo_status_x_month",
    "t1211|Percentage Total|grand_total",
    "t1212|Percentage Total|by_month",
    "t1213|Percentage Total|by_prop_toggle",
    "t1214|Percentage Total|by_market",
    "t1215|Percentage Total|by_scope",
    "t1216|Percentage Total|by_wo_status",
    "t1217|Percentage Total|by_vendor",
    "t1218|Percentage Total|by_repair_type",
    "t1219|Percentage Total|by_market_x_month",
    "t1220|Percentage Total|by_wo_status_x_month",
    "t1221|Physical Occupancy|grand_total",
    "t1222|Physical Occupancy|by_month",
    "t1223|Physical Occupancy|by_prop_toggle",
    "t1224|Physical Occupancy|by_market",
    "t1225|Physical Occupancy|by_scope",
    "t1226|Physical Occupancy|by_wo_status",
    "t1227|Physical Occupancy|by_vendor",
    "t1228|Physical Occupancy|by_repair_type",
    "t1229|Physical Occupancy|by_market_x_month",
    "t1230|Physical Occupancy|by_wo_status_x_month",
    "t1231|Project Approved Cost|grand_total",
    "t1232|Project Approved Cost|by_month",
    "t1233|Project Approved Cost|by_prop_toggle",
    "t1234|Project Approved Cost|by_market",
    "t1235|Project Approved Cost|by_scope",
    "t1236|Project Approved Cost|by_wo_status",
    "t1237|Project Approved Cost|by_vendor",
    "t1238|Project Approved Cost|by_repair_type",
    "t1239|Project Approved Cost|by_market_x_month",
    "t1240|Project Approved Cost|by_wo_status_x_month",
    "t1241|Project Average Days to Complete|grand_total",
    "t1242|Project Average Days to Complete|by_month",
    "t1243|Project Average Days to Complete|by_prop_toggle",
    "t1244|Project Average Days to Complete|by_market",
    "t1245|Project Average Days to Complete|by_scope",
    "t1246|Project Average Days to Complete|by_wo_status",
    "t1247|Project Average Days to Complete|by_vendor",
    "t1248|Project Average Days to Complete|by_repair_type",
    "t1249|Project Average Days to Complete|by_market_x_month",
    "t1250|Project Average Days to Complete|by_wo_status_x_month",
    "t1251|Project Avg CIP-RR Timeline|grand_total",
    "t1252|Project Avg CIP-RR Timeline|by_month",
    "t1253|Project Avg CIP-RR Timeline|by_prop_toggle",
    "t1254|Project Avg CIP-RR Timeline|by_market",
    "t1255|Project Avg CIP-RR Timeline|by_scope",
    "t1256|Project Avg CIP-RR Timeline|by_wo_status",
    "t1257|Project Avg CIP-RR Timeline|by_vendor",
    "t1258|Project Avg CIP-RR Timeline|by_repair_type",
    "t1259|Project Avg CIP-RR Timeline|by_market_x_month",
    "t1260|Project Avg CIP-RR Timeline|by_wo_status_x_month",
    "t1261|Project Avg Days Aging|grand_total",
    "t1262|Project Avg Days Aging|by_month",
    "t1263|Project Avg Days Aging|by_prop_toggle",
    "t1264|Project Avg Days Aging|by_market",
    "t1265|Project Avg Days Aging|by_scope",
    "t1266|Project Avg Days Aging|by_wo_status",
    "t1267|Project Avg Days Aging|by_vendor",
    "t1268|Project Avg Days Aging|by_repair_type",
    "t1269|Project Avg Days Aging|by_market_x_month",
    "t1270|Project Avg Days Aging|by_wo_status_x_month",
    "t1271|Project Avg Days CIP|grand_total",
    "t1272|Project Avg Days CIP|by_month",
    "t1273|Project Avg Days CIP|by_prop_toggle",
    "t1274|Project Avg Days CIP|by_market",
    "t1275|Project Avg Days CIP|by_scope",
    "t1276|Project Avg Days CIP|by_wo_status",
    "t1277|Project Avg Days CIP|by_vendor",
    "t1278|Project Avg Days CIP|by_repair_type",
    "t1279|Project Avg Days CIP|by_market_x_month",
    "t1280|Project Avg Days CIP|by_wo_status_x_month",
    "t1281|Project Avg Days Past ECD|grand_total",
    "t1282|Project Avg Days Past ECD|by_month",
    "t1283|Project Avg Days Past ECD|by_prop_toggle",
    "t1284|Project Avg Days Past ECD|by_market",
    "t1285|Project Avg Days Past ECD|by_scope",
    "t1286|Project Avg Days Past ECD|by_wo_status",
    "t1287|Project Avg Days Past ECD|by_vendor",
    "t1288|Project Avg Days Past ECD|by_repair_type",
    "t1289|Project Avg Days Past ECD|by_market_x_month",
    "t1290|Project Avg Days Past ECD|by_wo_status_x_month",
    "t1291|Project Avg MO-RR Timeline|grand_total",
    "t1292|Project Avg MO-RR Timeline|by_month",
    "t1293|Project Avg MO-RR Timeline|by_prop_toggle",
    "t1294|Project Avg MO-RR Timeline|by_market",
    "t1295|Project Avg MO-RR Timeline|by_scope",
    "t1296|Project Avg MO-RR Timeline|by_wo_status",
    "t1297|Project Avg MO-RR Timeline|by_vendor",
    "t1298|Project Avg MO-RR Timeline|by_repair_type",
    "t1299|Project Avg MO-RR Timeline|by_market_x_month",
    "t1300|Project Avg MO-RR Timeline|by_wo_status_x_month",
    "t1301|Project Avg Pre-Construction Timeline|grand_total",
    "t1302|Project Avg Pre-Construction Timeline|by_month",
    "t1303|Project Avg Pre-Construction Timeline|by_prop_toggle",
    "t1304|Project Avg Pre-Construction Timeline|by_market",
    "t1305|Project Avg Pre-Construction Timeline|by_scope",
    "t1306|Project Avg Pre-Construction Timeline|by_wo_status",
    "t1307|Project Avg Pre-Construction Timeline|by_vendor",
    "t1308|Project Avg Pre-Construction Timeline|by_repair_type",
    "t1309|Project Avg Pre-Construction Timeline|by_market_x_month",
    "t1310|Project Avg Pre-Construction Timeline|by_wo_status_x_month",
    "t1311|Project Cost by Completed Date|grand_total",
    "t1312|Project Cost by Completed Date|by_month",
    "t1313|Project Cost by Completed Date|by_prop_toggle",
    "t1314|Project Cost by Completed Date|by_market",
    "t1315|Project Cost by Completed Date|by_scope",
    "t1316|Project Cost by Completed Date|by_wo_status",
    "t1317|Project Cost by Completed Date|by_vendor",
    "t1318|Project Cost by Completed Date|by_repair_type",
    "t1319|Project Cost by Completed Date|by_market_x_month",
    "t1320|Project Cost by Completed Date|by_wo_status_x_month",
    "t1321|Project Cost by Created Date|grand_total",
    "t1322|Project Cost by Created Date|by_month",
    "t1323|Project Cost by Created Date|by_prop_toggle",
    "t1324|Project Cost by Created Date|by_market",
    "t1325|Project Cost by Created Date|by_scope",
    "t1326|Project Cost by Created Date|by_wo_status",
    "t1327|Project Cost by Created Date|by_vendor",
    "t1328|Project Cost by Created Date|by_repair_type",
    "t1329|Project Cost by Created Date|by_market_x_month",
    "t1330|Project Cost by Created Date|by_wo_status_x_month",
    "t1331|Project Cost by Estimated Completion Date|grand_total",
    "t1332|Project Cost by Estimated Completion Date|by_month",
    "t1333|Project Cost by Estimated Completion Date|by_prop_toggle",
    "t1334|Project Cost by Estimated Completion Date|by_market",
    "t1335|Project Cost by Estimated Completion Date|by_scope",
    "t1336|Project Cost by Estimated Completion Date|by_wo_status",
    "t1337|Project Cost by Estimated Completion Date|by_vendor",
    "t1338|Project Cost by Estimated Completion Date|by_repair_type",
    "t1339|Project Cost by Estimated Completion Date|by_market_x_month",
    "t1340|Project Cost by Estimated Completion Date|by_wo_status_x_month",
    "t1341|Project Count by Created Date (Ignore 0 and Nulls)|grand_total",
    "t1342|Project Count by Created Date (Ignore 0 and Nulls)|by_month",
    "t1343|Project Count by Created Date (Ignore 0 and Nulls)|by_prop_toggle",
    "t1344|Project Count by Created Date (Ignore 0 and Nulls)|by_market",
    "t1345|Project Count by Created Date (Ignore 0 and Nulls)|by_scope",
    "t1346|Project Count by Created Date (Ignore 0 and Nulls)|by_wo_status",
    "t1347|Project Count by Created Date (Ignore 0 and Nulls)|by_vendor",
    "t1348|Project Count by Created Date (Ignore 0 and Nulls)|by_repair_type",
    "t1349|Project Count by Created Date (Ignore 0 and Nulls)|by_market_x_month",
    "t1350|Project Count by Created Date (Ignore 0 and Nulls)|by_wo_status_x_month",
    "t1351|Project Count by Estimated Completion Date|grand_total",
    "t1352|Project Count by Estimated Completion Date|by_month",
    "t1353|Project Count by Estimated Completion Date|by_prop_toggle",
    "t1354|Project Count by Estimated Completion Date|by_market",
    "t1355|Project Count by Estimated Completion Date|by_scope",
    "t1356|Project Count by Estimated Completion Date|by_wo_status",
    "t1357|Project Count by Estimated Completion Date|by_vendor",
    "t1358|Project Count by Estimated Completion Date|by_repair_type",
    "t1359|Project Count by Estimated Completion Date|by_market_x_month",
    "t1360|Project Count by Estimated Completion Date|by_wo_status_x_month",
    "t1361|Project Count by Estimated Completion Date (Ignore 0 and Nulls)|grand_total",
    "t1362|Project Count by Estimated Completion Date (Ignore 0 and Nulls)|by_month",
    "t1363|Project Count by Estimated Completion Date (Ignore 0 and Nulls)|by_prop_toggle",
    "t1364|Project Count by Estimated Completion Date (Ignore 0 and Nulls)|by_market",
    "t1365|Project Count by Estimated Completion Date (Ignore 0 and Nulls)|by_scope",
    "t1366|Project Count by Estimated Completion Date (Ignore 0 and Nulls)|by_wo_status",
    "t1367|Project Count by Estimated Completion Date (Ignore 0 and Nulls)|by_vendor",
    "t1368|Project Count by Estimated Completion Date (Ignore 0 and Nulls)|by_repair_type",
    "t1369|Project Count by Estimated Completion Date (Ignore 0 and Nulls)|by_market_x_month",
    "t1370|Project Count by Estimated Completion Date (Ignore 0 and Nulls)|by_wo_status_x_month",
    "t1371|Project Count from Properties with Multiple Scopes (Exceptions)|grand_total",
    "t1372|Project Count from Properties with Multiple Scopes (Exceptions)|by_month",
    "t1373|Project Count from Properties with Multiple Scopes (Exceptions)|by_prop_toggle",
    "t1374|Project Count from Properties with Multiple Scopes (Exceptions)|by_market",
    "t1375|Project Count from Properties with Multiple Scopes (Exceptions)|by_scope",
    "t1376|Project Count from Properties with Multiple Scopes (Exceptions)|by_wo_status",
    "t1377|Project Count from Properties with Multiple Scopes (Exceptions)|by_vendor",
    "t1378|Project Count from Properties with Multiple Scopes (Exceptions)|by_repair_type",
    "t1379|Project Count from Properties with Multiple Scopes (Exceptions)|by_market_x_month",
    "t1380|Project Count from Properties with Multiple Scopes (Exceptions)|by_wo_status_x_month",
    "t1381|Projected Avg Project Cost (Actual + ECD)|grand_total",
    "t1382|Projected Avg Project Cost (Actual + ECD)|by_month",
    "t1383|Projected Avg Project Cost (Actual + ECD)|by_prop_toggle",
    "t1384|Projected Avg Project Cost (Actual + ECD)|by_market",
    "t1385|Projected Avg Project Cost (Actual + ECD)|by_scope",
    "t1386|Projected Avg Project Cost (Actual + ECD)|by_wo_status",
    "t1387|Projected Avg Project Cost (Actual + ECD)|by_vendor",
    "t1388|Projected Avg Project Cost (Actual + ECD)|by_repair_type",
    "t1389|Projected Avg Project Cost (Actual + ECD)|by_market_x_month",
    "t1390|Projected Avg Project Cost (Actual + ECD)|by_wo_status_x_month",
    "t1391|Projected Project Cost (Actual + ECD)|grand_total",
    "t1392|Projected Project Cost (Actual + ECD)|by_month",
    "t1393|Projected Project Cost (Actual + ECD)|by_prop_toggle",
    "t1394|Projected Project Cost (Actual + ECD)|by_market",
    "t1395|Projected Project Cost (Actual + ECD)|by_scope",
    "t1396|Projected Project Cost (Actual + ECD)|by_wo_status",
    "t1397|Projected Project Cost (Actual + ECD)|by_vendor",
    "t1398|Projected Project Cost (Actual + ECD)|by_repair_type",
    "t1399|Projected Project Cost (Actual + ECD)|by_market_x_month",
    "t1400|Projected Project Cost (Actual + ECD)|by_wo_status_x_month",
    "t1401|Projected Project Count (Actual + ECD)|grand_total",
    "t1402|Projected Project Count (Actual + ECD)|by_month",
    "t1403|Projected Project Count (Actual + ECD)|by_prop_toggle",
    "t1404|Projected Project Count (Actual + ECD)|by_market",
    "t1405|Projected Project Count (Actual + ECD)|by_scope",
    "t1406|Projected Project Count (Actual + ECD)|by_wo_status",
    "t1407|Projected Project Count (Actual + ECD)|by_vendor",
    "t1408|Projected Project Count (Actual + ECD)|by_repair_type",
    "t1409|Projected Project Count (Actual + ECD)|by_market_x_month",
    "t1410|Projected Project Count (Actual + ECD)|by_wo_status_x_month",
    "t1411|Projects Completed by Occupied Homes|grand_total",
    "t1412|Projects Completed by Occupied Homes|by_month",
    "t1413|Projects Completed by Occupied Homes|by_prop_toggle",
    "t1414|Projects Completed by Occupied Homes|by_market",
    "t1415|Projects Completed by Occupied Homes|by_scope",
    "t1416|Projects Completed by Occupied Homes|by_wo_status",
    "t1417|Projects Completed by Occupied Homes|by_vendor",
    "t1418|Projects Completed by Occupied Homes|by_repair_type",
    "t1419|Projects Completed by Occupied Homes|by_market_x_month",
    "t1420|Projects Completed by Occupied Homes|by_wo_status_x_month",
    "t1421|Properties Serviced Internally %|grand_total",
    "t1422|Properties Serviced Internally %|by_month",
    "t1423|Properties Serviced Internally %|by_prop_toggle",
    "t1424|Properties Serviced Internally %|by_market",
    "t1425|Properties Serviced Internally %|by_scope",
    "t1426|Properties Serviced Internally %|by_wo_status",
    "t1427|Properties Serviced Internally %|by_vendor",
    "t1428|Properties Serviced Internally %|by_repair_type",
    "t1429|Properties Serviced Internally %|by_market_x_month",
    "t1430|Properties Serviced Internally %|by_wo_status_x_month",
    "t1431|Properties Serviced Internally % (Budget)|grand_total",
    "t1432|Properties Serviced Internally % (Budget)|by_month",
    "t1433|Properties Serviced Internally % (Budget)|by_prop_toggle",
    "t1434|Properties Serviced Internally % (Budget)|by_market",
    "t1435|Properties Serviced Internally % (Budget)|by_scope",
    "t1436|Properties Serviced Internally % (Budget)|by_wo_status",
    "t1437|Properties Serviced Internally % (Budget)|by_vendor",
    "t1438|Properties Serviced Internally % (Budget)|by_repair_type",
    "t1439|Properties Serviced Internally % (Budget)|by_market_x_month",
    "t1440|Properties Serviced Internally % (Budget)|by_wo_status_x_month",
    "t1441|Property Count by Acquisition Date (Cumulative)|grand_total",
    "t1442|Property Count by Acquisition Date (Cumulative)|by_month",
    "t1443|Property Count by Acquisition Date (Cumulative)|by_prop_toggle",
    "t1444|Property Count by Acquisition Date (Cumulative)|by_market",
    "t1445|Property Count by Acquisition Date (Cumulative)|by_scope",
    "t1446|Property Count by Acquisition Date (Cumulative)|by_wo_status",
    "t1447|Property Count by Acquisition Date (Cumulative)|by_vendor",
    "t1448|Property Count by Acquisition Date (Cumulative)|by_repair_type",
    "t1449|Property Count by Acquisition Date (Cumulative)|by_market_x_month",
    "t1450|Property Count by Acquisition Date (Cumulative)|by_wo_status_x_month",
    "t1451|Property Count with Multiple Scopes (Exceptions)|grand_total",
    "t1452|Property Count with Multiple Scopes (Exceptions)|by_month",
    "t1453|Property Count with Multiple Scopes (Exceptions)|by_prop_toggle",
    "t1454|Property Count with Multiple Scopes (Exceptions)|by_market",
    "t1455|Property Count with Multiple Scopes (Exceptions)|by_scope",
    "t1456|Property Count with Multiple Scopes (Exceptions)|by_wo_status",
    "t1457|Property Count with Multiple Scopes (Exceptions)|by_vendor",
    "t1458|Property Count with Multiple Scopes (Exceptions)|by_repair_type",
    "t1459|Property Count with Multiple Scopes (Exceptions)|by_market_x_month",
    "t1460|Property Count with Multiple Scopes (Exceptions)|by_wo_status_x_month",
    "t1461|Property Rental Home Count Average|grand_total",
    "t1462|Property Rental Home Count Average|by_month",
    "t1463|Property Rental Home Count Average|by_prop_toggle",
    "t1464|Property Rental Home Count Average|by_market",
    "t1465|Property Rental Home Count Average|by_scope",
    "t1466|Property Rental Home Count Average|by_wo_status",
    "t1467|Property Rental Home Count Average|by_vendor",
    "t1468|Property Rental Home Count Average|by_repair_type",
    "t1469|Property Rental Home Count Average|by_market_x_month",
    "t1470|Property Rental Home Count Average|by_wo_status_x_month",
    "t1471|Property Underwritten Budget $|grand_total",
    "t1472|Property Underwritten Budget $|by_month",
    "t1473|Property Underwritten Budget $|by_prop_toggle",
    "t1474|Property Underwritten Budget $|by_market",
    "t1475|Property Underwritten Budget $|by_scope",
    "t1476|Property Underwritten Budget $|by_wo_status",
    "t1477|Property Underwritten Budget $|by_vendor",
    "t1478|Property Underwritten Budget $|by_repair_type",
    "t1479|Property Underwritten Budget $|by_market_x_month",
    "t1480|Property Underwritten Budget $|by_wo_status_x_month",
    "t1481|Property Unit Count|grand_total",
    "t1482|Property Unit Count|by_month",
    "t1483|Property Unit Count|by_prop_toggle",
    "t1484|Property Unit Count|by_market",
    "t1485|Property Unit Count|by_scope",
    "t1486|Property Unit Count|by_wo_status",
    "t1487|Property Unit Count|by_vendor",
    "t1488|Property Unit Count|by_repair_type",
    "t1489|Property Unit Count|by_market_x_month",
    "t1490|Property Unit Count|by_wo_status_x_month",
    "t1491|Rate of Properties Do Not Publish List|grand_total",
    "t1492|Rate of Properties Do Not Publish List|by_month",
    "t1493|Rate of Properties Do Not Publish List|by_prop_toggle",
    "t1494|Rate of Properties Do Not Publish List|by_market",
    "t1495|Rate of Properties Do Not Publish List|by_scope",
    "t1496|Rate of Properties Do Not Publish List|by_wo_status",
    "t1497|Rate of Properties Do Not Publish List|by_vendor",
    "t1498|Rate of Properties Do Not Publish List|by_repair_type",
    "t1499|Rate of Properties Do Not Publish List|by_market_x_month",
    "t1500|Rate of Properties Do Not Publish List|by_wo_status_x_month",
    "t1501|Rate of Properties with No Self-Tour Smart Home|grand_total",
    "t1502|Rate of Properties with No Self-Tour Smart Home|by_month",
    "t1503|Rate of Properties with No Self-Tour Smart Home|by_prop_toggle",
    "t1504|Rate of Properties with No Self-Tour Smart Home|by_market",
    "t1505|Rate of Properties with No Self-Tour Smart Home|by_scope",
    "t1506|Rate of Properties with No Self-Tour Smart Home|by_wo_status",
    "t1507|Rate of Properties with No Self-Tour Smart Home|by_vendor",
    "t1508|Rate of Properties with No Self-Tour Smart Home|by_repair_type",
    "t1509|Rate of Properties with No Self-Tour Smart Home|by_market_x_month",
    "t1510|Rate of Properties with No Self-Tour Smart Home|by_wo_status_x_month",
    "t1511|Rate of Properties with No Virtual Tour/Inside Map|grand_total",
    "t1512|Rate of Properties with No Virtual Tour/Inside Map|by_month",
    "t1513|Rate of Properties with No Virtual Tour/Inside Map|by_prop_toggle",
    "t1514|Rate of Properties with No Virtual Tour/Inside Map|by_market",
    "t1515|Rate of Properties with No Virtual Tour/Inside Map|by_scope",
    "t1516|Rate of Properties with No Virtual Tour/Inside Map|by_wo_status",
    "t1517|Rate of Properties with No Virtual Tour/Inside Map|by_vendor",
    "t1518|Rate of Properties with No Virtual Tour/Inside Map|by_repair_type",
    "t1519|Rate of Properties with No Virtual Tour/Inside Map|by_market_x_month",
    "t1520|Rate of Properties with No Virtual Tour/Inside Map|by_wo_status_x_month",
    "t1521|Rate of Replacement|grand_total",
    "t1522|Rate of Replacement|by_month",
    "t1523|Rate of Replacement|by_prop_toggle",
    "t1524|Rate of Replacement|by_market",
    "t1525|Rate of Replacement|by_scope",
    "t1526|Rate of Replacement|by_wo_status",
    "t1527|Rate of Replacement|by_vendor",
    "t1528|Rate of Replacement|by_repair_type",
    "t1529|Rate of Replacement|by_market_x_month",
    "t1530|Rate of Replacement|by_wo_status_x_month",
    "t1531|Rental Home Count|grand_total",
    "t1532|Rental Home Count|by_month",
    "t1533|Rental Home Count|by_prop_toggle",
    "t1534|Rental Home Count|by_market",
    "t1535|Rental Home Count|by_scope",
    "t1536|Rental Home Count|by_wo_status",
    "t1537|Rental Home Count|by_vendor",
    "t1538|Rental Home Count|by_repair_type",
    "t1539|Rental Home Count|by_market_x_month",
    "t1540|Rental Home Count|by_wo_status_x_month",
    "t1541|Repair Type Cost Cumulative %|grand_total",
    "t1542|Repair Type Cost Cumulative %|by_month",
    "t1543|Repair Type Cost Cumulative %|by_prop_toggle",
    "t1544|Repair Type Cost Cumulative %|by_market",
    "t1545|Repair Type Cost Cumulative %|by_scope",
    "t1546|Repair Type Cost Cumulative %|by_wo_status",
    "t1547|Repair Type Cost Cumulative %|by_vendor",
    "t1548|Repair Type Cost Cumulative %|by_repair_type",
    "t1549|Repair Type Cost Cumulative %|by_market_x_month",
    "t1550|Repair Type Cost Cumulative %|by_wo_status_x_month",
    "t1551|Repair Type Line Item Count Cumulative %|grand_total",
    "t1552|Repair Type Line Item Count Cumulative %|by_month",
    "t1553|Repair Type Line Item Count Cumulative %|by_prop_toggle",
    "t1554|Repair Type Line Item Count Cumulative %|by_market",
    "t1555|Repair Type Line Item Count Cumulative %|by_scope",
    "t1556|Repair Type Line Item Count Cumulative %|by_wo_status",
    "t1557|Repair Type Line Item Count Cumulative %|by_vendor",
    "t1558|Repair Type Line Item Count Cumulative %|by_repair_type",
    "t1559|Repair Type Line Item Count Cumulative %|by_market_x_month",
    "t1560|Repair Type Line Item Count Cumulative %|by_wo_status_x_month",
    "t1561|Request Market Vendor|grand_total",
    "t1562|Request Market Vendor|by_month",
    "t1563|Request Market Vendor|by_prop_toggle",
    "t1564|Request Market Vendor|by_market",
    "t1565|Request Market Vendor|by_scope",
    "t1566|Request Market Vendor|by_wo_status",
    "t1567|Request Market Vendor|by_vendor",
    "t1568|Request Market Vendor|by_repair_type",
    "t1569|Request Market Vendor|by_market_x_month",
    "t1570|Request Market Vendor|by_wo_status_x_month",
    "t1571|Request Market Vendor %|grand_total",
    "t1572|Request Market Vendor %|by_month",
    "t1573|Request Market Vendor %|by_prop_toggle",
    "t1574|Request Market Vendor %|by_market",
    "t1575|Request Market Vendor %|by_scope",
    "t1576|Request Market Vendor %|by_wo_status",
    "t1577|Request Market Vendor %|by_vendor",
    "t1578|Request Market Vendor %|by_repair_type",
    "t1579|Request Market Vendor %|by_market_x_month",
    "t1580|Request Market Vendor %|by_wo_status_x_month",
    "t1581|Return trip needed|grand_total",
    "t1582|Return trip needed|by_month",
    "t1583|Return trip needed|by_prop_toggle",
    "t1584|Return trip needed|by_market",
    "t1585|Return trip needed|by_scope",
    "t1586|Return trip needed|by_wo_status",
    "t1587|Return trip needed|by_vendor",
    "t1588|Return trip needed|by_repair_type",
    "t1589|Return trip needed|by_market_x_month",
    "t1590|Return trip needed|by_wo_status_x_month",
    "t1591|Return trip needed %|grand_total",
    "t1592|Return trip needed %|by_month",
    "t1593|Return trip needed %|by_prop_toggle",
    "t1594|Return trip needed %|by_market",
    "t1595|Return trip needed %|by_scope",
    "t1596|Return trip needed %|by_wo_status",
    "t1597|Return trip needed %|by_vendor",
    "t1598|Return trip needed %|by_repair_type",
    "t1599|Return trip needed %|by_market_x_month",
    "t1600|Return trip needed %|by_wo_status_x_month",
    "t1601|Scope Line Item Usage|grand_total",
    "t1602|Scope Line Item Usage|by_month",
    "t1603|Scope Line Item Usage|by_prop_toggle",
    "t1604|Scope Line Item Usage|by_market",
    "t1605|Scope Line Item Usage|by_scope",
    "t1606|Scope Line Item Usage|by_wo_status",
    "t1607|Scope Line Item Usage|by_vendor",
    "t1608|Scope Line Item Usage|by_repair_type",
    "t1609|Scope Line Item Usage|by_market_x_month",
    "t1610|Scope Line Item Usage|by_wo_status_x_month",
    "t1611|Smart Home Installation Amount|grand_total",
    "t1612|Smart Home Installation Amount|by_month",
    "t1613|Smart Home Installation Amount|by_prop_toggle",
    "t1614|Smart Home Installation Amount|by_market",
    "t1615|Smart Home Installation Amount|by_scope",
    "t1616|Smart Home Installation Amount|by_wo_status",
    "t1617|Smart Home Installation Amount|by_vendor",
    "t1618|Smart Home Installation Amount|by_repair_type",
    "t1619|Smart Home Installation Amount|by_market_x_month",
    "t1620|Smart Home Installation Amount|by_wo_status_x_month",
    "t1621|Smart Home Installed Properties % by Gateway Assignment Date|grand_total",
    "t1622|Smart Home Installed Properties % by Gateway Assignment Date|by_month",
    "t1623|Smart Home Installed Properties % by Gateway Assignment Date|by_prop_toggle",
    "t1624|Smart Home Installed Properties % by Gateway Assignment Date|by_market",
    "t1625|Smart Home Installed Properties % by Gateway Assignment Date|by_scope",
    "t1626|Smart Home Installed Properties % by Gateway Assignment Date|by_wo_status",
    "t1627|Smart Home Installed Properties % by Gateway Assignment Date|by_vendor",
    "t1628|Smart Home Installed Properties % by Gateway Assignment Date|by_repair_type",
    "t1629|Smart Home Installed Properties % by Gateway Assignment Date|by_market_x_month",
    "t1630|Smart Home Installed Properties % by Gateway Assignment Date|by_wo_status_x_month",
    "t1631|Square Footage|grand_total",
    "t1632|Square Footage|by_month",
    "t1633|Square Footage|by_prop_toggle",
    "t1634|Square Footage|by_market",
    "t1635|Square Footage|by_scope",
    "t1636|Square Footage|by_wo_status",
    "t1637|Square Footage|by_vendor",
    "t1638|Square Footage|by_repair_type",
    "t1639|Square Footage|by_market_x_month",
    "t1640|Square Footage|by_wo_status_x_month",
    "t1641|Superintendent % of Move Ins with Work Orders by Resident Move In Date|grand_total",
    "t1642|Superintendent % of Move Ins with Work Orders by Resident Move In Date|by_month",
    "t1643|Superintendent % of Move Ins with Work Orders by Resident Move In Date|by_prop_toggle",
    "t1644|Superintendent % of Move Ins with Work Orders by Resident Move In Date|by_market",
    "t1645|Superintendent % of Move Ins with Work Orders by Resident Move In Date|by_scope",
    "t1646|Superintendent % of Move Ins with Work Orders by Resident Move In Date|by_wo_status",
    "t1647|Superintendent % of Move Ins with Work Orders by Resident Move In Date|by_vendor",
    "t1648|Superintendent % of Move Ins with Work Orders by Resident Move In Date|by_repair_type",
    "t1649|Superintendent % of Move Ins with Work Orders by Resident Move In Date|by_market_x_month",
    "t1650|Superintendent % of Move Ins with Work Orders by Resident Move In Date|by_wo_status_x_month",
    "t1651|T-3 P-Cards Cost|grand_total",
    "t1652|T-3 P-Cards Cost|by_month",
    "t1653|T-3 P-Cards Cost|by_prop_toggle",
    "t1654|T-3 P-Cards Cost|by_market",
    "t1655|T-3 P-Cards Cost|by_scope",
    "t1656|T-3 P-Cards Cost|by_wo_status",
    "t1657|T-3 P-Cards Cost|by_vendor",
    "t1658|T-3 P-Cards Cost|by_repair_type",
    "t1659|T-3 P-Cards Cost|by_market_x_month",
    "t1660|T-3 P-Cards Cost|by_wo_status_x_month",
    "t1661|Tech count|grand_total",
    "t1662|Tech count|by_month",
    "t1663|Tech count|by_prop_toggle",
    "t1664|Tech count|by_market",
    "t1665|Tech count|by_scope",
    "t1666|Tech count|by_wo_status",
    "t1667|Tech count|by_vendor",
    "t1668|Tech count|by_repair_type",
    "t1669|Tech count|by_market_x_month",
    "t1670|Tech count|by_wo_status_x_month",
    "t1671|Technician Assigned Work Orders|grand_total",
    "t1672|Technician Assigned Work Orders|by_month",
    "t1673|Technician Assigned Work Orders|by_prop_toggle",
    "t1674|Technician Assigned Work Orders|by_market",
    "t1675|Technician Assigned Work Orders|by_scope",
    "t1676|Technician Assigned Work Orders|by_wo_status",
    "t1677|Technician Assigned Work Orders|by_vendor",
    "t1678|Technician Assigned Work Orders|by_repair_type",
    "t1679|Technician Assigned Work Orders|by_market_x_month",
    "t1680|Technician Assigned Work Orders|by_wo_status_x_month",
    "t1681|Total Available Work orders|grand_total",
    "t1682|Total Available Work orders|by_month",
    "t1683|Total Available Work orders|by_prop_toggle",
    "t1684|Total Available Work orders|by_market",
    "t1685|Total Available Work orders|by_scope",
    "t1686|Total Available Work orders|by_wo_status",
    "t1687|Total Available Work orders|by_vendor",
    "t1688|Total Available Work orders|by_repair_type",
    "t1689|Total Available Work orders|by_market_x_month",
    "t1690|Total Available Work orders|by_wo_status_x_month",
    "t1691|Total HOA Homes|grand_total",
    "t1692|Total HOA Homes|by_month",
    "t1693|Total HOA Homes|by_prop_toggle",
    "t1694|Total HOA Homes|by_market",
    "t1695|Total HOA Homes|by_scope",
    "t1696|Total HOA Homes|by_wo_status",
    "t1697|Total HOA Homes|by_vendor",
    "t1698|Total HOA Homes|by_repair_type",
    "t1699|Total HOA Homes|by_market_x_month",
    "t1700|Total HOA Homes|by_wo_status_x_month",
    "t1701|Total Project Cost|grand_total",
    "t1702|Total Project Cost|by_month",
    "t1703|Total Project Cost|by_prop_toggle",
    "t1704|Total Project Cost|by_market",
    "t1705|Total Project Cost|by_scope",
    "t1706|Total Project Cost|by_wo_status",
    "t1707|Total Project Cost|by_vendor",
    "t1708|Total Project Cost|by_repair_type",
    "t1709|Total Project Cost|by_market_x_month",
    "t1710|Total Project Cost|by_wo_status_x_month",
    "t1711|Total Project Count|grand_total",
    "t1712|Total Project Count|by_month",
    "t1713|Total Project Count|by_prop_toggle",
    "t1714|Total Project Count|by_market",
    "t1715|Total Project Count|by_scope",
    "t1716|Total Project Count|by_wo_status",
    "t1717|Total Project Count|by_vendor",
    "t1718|Total Project Count|by_repair_type",
    "t1719|Total Project Count|by_market_x_month",
    "t1720|Total Project Count|by_wo_status_x_month",
    "t1721|Total Property Count with Open In Progress Dates|grand_total",
    "t1722|Total Property Count with Open In Progress Dates|by_month",
    "t1723|Total Property Count with Open In Progress Dates|by_prop_toggle",
    "t1724|Total Property Count with Open In Progress Dates|by_market",
    "t1725|Total Property Count with Open In Progress Dates|by_scope",
    "t1726|Total Property Count with Open In Progress Dates|by_wo_status",
    "t1727|Total Property Count with Open In Progress Dates|by_vendor",
    "t1728|Total Property Count with Open In Progress Dates|by_repair_type",
    "t1729|Total Property Count with Open In Progress Dates|by_market_x_month",
    "t1730|Total Property Count with Open In Progress Dates|by_wo_status_x_month",
    "t1731|Total Violation Amt|grand_total",
    "t1732|Total Violation Amt|by_month",
    "t1733|Total Violation Amt|by_prop_toggle",
    "t1734|Total Violation Amt|by_market",
    "t1735|Total Violation Amt|by_scope",
    "t1736|Total Violation Amt|by_wo_status",
    "t1737|Total Violation Amt|by_vendor",
    "t1738|Total Violation Amt|by_repair_type",
    "t1739|Total Violation Amt|by_market_x_month",
    "t1740|Total Violation Amt|by_wo_status_x_month",
    "t1741|Total Violations|grand_total",
    "t1742|Total Violations|by_month",
    "t1743|Total Violations|by_prop_toggle",
    "t1744|Total Violations|by_market",
    "t1745|Total Violations|by_scope",
    "t1746|Total Violations|by_wo_status",
    "t1747|Total Violations|by_vendor",
    "t1748|Total Violations|by_repair_type",
    "t1749|Total Violations|by_market_x_month",
    "t1750|Total Violations|by_wo_status_x_month",
    "t1751|Total Wo's Completed|grand_total",
    "t1752|Total Wo's Completed|by_month",
    "t1753|Total Wo's Completed|by_prop_toggle",
    "t1754|Total Wo's Completed|by_market",
    "t1755|Total Wo's Completed|by_scope",
    "t1756|Total Wo's Completed|by_wo_status",
    "t1757|Total Wo's Completed|by_vendor",
    "t1758|Total Wo's Completed|by_repair_type",
    "t1759|Total Wo's Completed|by_market_x_month",
    "t1760|Total Wo's Completed|by_wo_status_x_month",
    "t1761|Total Work Order Violation Amt|grand_total",
    "t1762|Total Work Order Violation Amt|by_month",
    "t1763|Total Work Order Violation Amt|by_prop_toggle",
    "t1764|Total Work Order Violation Amt|by_market",
    "t1765|Total Work Order Violation Amt|by_scope",
    "t1766|Total Work Order Violation Amt|by_wo_status",
    "t1767|Total Work Order Violation Amt|by_vendor",
    "t1768|Total Work Order Violation Amt|by_repair_type",
    "t1769|Total Work Order Violation Amt|by_market_x_month",
    "t1770|Total Work Order Violation Amt|by_wo_status_x_month",
    "t1771|Total property count|grand_total",
    "t1772|Total property count|by_month",
    "t1773|Total property count|by_prop_toggle",
    "t1774|Total property count|by_market",
    "t1775|Total property count|by_scope",
    "t1776|Total property count|by_wo_status",
    "t1777|Total property count|by_vendor",
    "t1778|Total property count|by_repair_type",
    "t1779|Total property count|by_market_x_month",
    "t1780|Total property count|by_wo_status_x_month",
    "t1781|TotalRank|grand_total",
    "t1782|TotalRank|by_month",
    "t1783|TotalRank|by_prop_toggle",
    "t1784|TotalRank|by_market",
    "t1785|TotalRank|by_scope",
    "t1786|TotalRank|by_wo_status",
    "t1787|TotalRank|by_vendor",
    "t1788|TotalRank|by_repair_type",
    "t1789|TotalRank|by_market_x_month",
    "t1790|TotalRank|by_wo_status_x_month",
    "t1791|TotalRank FT|grand_total",
    "t1792|TotalRank FT|by_month",
    "t1793|TotalRank FT|by_prop_toggle",
    "t1794|TotalRank FT|by_market",
    "t1795|TotalRank FT|by_scope",
    "t1796|TotalRank FT|by_wo_status",
    "t1797|TotalRank FT|by_vendor",
    "t1798|TotalRank FT|by_repair_type",
    "t1799|TotalRank FT|by_market_x_month",
    "t1800|TotalRank FT|by_wo_status_x_month",
    "t1801|TotalRank HPD|grand_total",
    "t1802|TotalRank HPD|by_month",
    "t1803|TotalRank HPD|by_prop_toggle",
    "t1804|TotalRank HPD|by_market",
    "t1805|TotalRank HPD|by_scope",
    "t1806|TotalRank HPD|by_wo_status",
    "t1807|TotalRank HPD|by_vendor",
    "t1808|TotalRank HPD|by_repair_type",
    "t1809|TotalRank HPD|by_market_x_month",
    "t1810|TotalRank HPD|by_wo_status_x_month",
    "t1811|TotalRankWO|grand_total",
    "t1812|TotalRankWO|by_month",
    "t1813|TotalRankWO|by_prop_toggle",
    "t1814|TotalRankWO|by_market",
    "t1815|TotalRankWO|by_scope",
    "t1816|TotalRankWO|by_wo_status",
    "t1817|TotalRankWO|by_vendor",
    "t1818|TotalRankWO|by_repair_type",
    "t1819|TotalRankWO|by_market_x_month",
    "t1820|TotalRankWO|by_wo_status_x_month",
    "t1821|Turn Project Count by Completed Date (Budget)|grand_total",
    "t1822|Turn Project Count by Completed Date (Budget)|by_month",
    "t1823|Turn Project Count by Completed Date (Budget)|by_prop_toggle",
    "t1824|Turn Project Count by Completed Date (Budget)|by_market",
    "t1825|Turn Project Count by Completed Date (Budget)|by_scope",
    "t1826|Turn Project Count by Completed Date (Budget)|by_wo_status",
    "t1827|Turn Project Count by Completed Date (Budget)|by_vendor",
    "t1828|Turn Project Count by Completed Date (Budget)|by_repair_type",
    "t1829|Turn Project Count by Completed Date (Budget)|by_market_x_month",
    "t1830|Turn Project Count by Completed Date (Budget)|by_wo_status_x_month",
    "t1831|Unique Scope ID|grand_total",
    "t1832|Unique Scope ID|by_month",
    "t1833|Unique Scope ID|by_prop_toggle",
    "t1834|Unique Scope ID|by_market",
    "t1835|Unique Scope ID|by_scope",
    "t1836|Unique Scope ID|by_wo_status",
    "t1837|Unique Scope ID|by_vendor",
    "t1838|Unique Scope ID|by_repair_type",
    "t1839|Unique Scope ID|by_market_x_month",
    "t1840|Unique Scope ID|by_wo_status_x_month",
    "t1841|Vendor Count|grand_total",
    "t1842|Vendor Count|by_month",
    "t1843|Vendor Count|by_prop_toggle",
    "t1844|Vendor Count|by_market",
    "t1845|Vendor Count|by_scope",
    "t1846|Vendor Count|by_wo_status",
    "t1847|Vendor Count|by_vendor",
    "t1848|Vendor Count|by_repair_type",
    "t1849|Vendor Count|by_market_x_month",
    "t1850|Vendor Count|by_wo_status_x_month",
    "t1851|Vendor Move In Work Order Count by Resident Move In Date|grand_total",
    "t1852|Vendor Move In Work Order Count by Resident Move In Date|by_month",
    "t1853|Vendor Move In Work Order Count by Resident Move In Date|by_prop_toggle",
    "t1854|Vendor Move In Work Order Count by Resident Move In Date|by_market",
    "t1855|Vendor Move In Work Order Count by Resident Move In Date|by_scope",
    "t1856|Vendor Move In Work Order Count by Resident Move In Date|by_wo_status",
    "t1857|Vendor Move In Work Order Count by Resident Move In Date|by_vendor",
    "t1858|Vendor Move In Work Order Count by Resident Move In Date|by_repair_type",
    "t1859|Vendor Move In Work Order Count by Resident Move In Date|by_market_x_month",
    "t1860|Vendor Move In Work Order Count by Resident Move In Date|by_wo_status_x_month",
    "t1861|Work Order Costs by Completed Date 80th Percentile|grand_total",
    "t1862|Work Order Costs by Completed Date 80th Percentile|by_month",
    "t1863|Work Order Costs by Completed Date 80th Percentile|by_prop_toggle",
    "t1864|Work Order Costs by Completed Date 80th Percentile|by_market",
    "t1865|Work Order Costs by Completed Date 80th Percentile|by_scope",
    "t1866|Work Order Costs by Completed Date 80th Percentile|by_wo_status",
    "t1867|Work Order Costs by Completed Date 80th Percentile|by_vendor",
    "t1868|Work Order Costs by Completed Date 80th Percentile|by_repair_type",
    "t1869|Work Order Costs by Completed Date 80th Percentile|by_market_x_month",
    "t1870|Work Order Costs by Completed Date 80th Percentile|by_wo_status_x_month",
    "t1871|Work Order Costs by Completed Date 80th Percentile Trailing 12 Month|grand_total",
    "t1872|Work Order Costs by Completed Date 80th Percentile Trailing 12 Month|by_month",
    "t1873|Work Order Costs by Completed Date 80th Percentile Trailing 12 Month|by_prop_toggle",
    "t1874|Work Order Costs by Completed Date 80th Percentile Trailing 12 Month|by_market",
    "t1875|Work Order Costs by Completed Date 80th Percentile Trailing 12 Month|by_scope",
    "t1876|Work Order Costs by Completed Date 80th Percentile Trailing 12 Month|by_wo_status",
    "t1877|Work Order Costs by Completed Date 80th Percentile Trailing 12 Month|by_vendor",
    "t1878|Work Order Costs by Completed Date 80th Percentile Trailing 12 Month|by_repair_type",
    "t1879|Work Order Costs by Completed Date 80th Percentile Trailing 12 Month|by_market_x_month",
    "t1880|Work Order Costs by Completed Date 80th Percentile Trailing 12 Month|by_wo_status_x_month",
    "t1881|Work Order Count by Completed Date|grand_total",
    "t1882|Work Order Count by Completed Date|by_month",
    "t1883|Work Order Count by Completed Date|by_prop_toggle",
    "t1884|Work Order Count by Completed Date|by_market",
    "t1885|Work Order Count by Completed Date|by_scope",
    "t1886|Work Order Count by Completed Date|by_wo_status",
    "t1887|Work Order Count by Completed Date|by_vendor",
    "t1888|Work Order Count by Completed Date|by_repair_type",
    "t1889|Work Order Count by Completed Date|by_market_x_month",
    "t1890|Work Order Count by Completed Date|by_wo_status_x_month",
    "t1891|Work Order Count by Created Date|grand_total",
    "t1892|Work Order Count by Created Date|by_month",
    "t1893|Work Order Count by Created Date|by_prop_toggle",
    "t1894|Work Order Count by Created Date|by_market",
    "t1895|Work Order Count by Created Date|by_scope",
    "t1896|Work Order Count by Created Date|by_wo_status",
    "t1897|Work Order Count by Created Date|by_vendor",
    "t1898|Work Order Count by Created Date|by_repair_type",
    "t1899|Work Order Count by Created Date|by_market_x_month",
    "t1900|Work Order Count by Created Date|by_wo_status_x_month",
    "t1901|Work Order Days to Complete|grand_total",
    "t1902|Work Order Days to Complete|by_month",
    "t1903|Work Order Days to Complete|by_prop_toggle",
    "t1904|Work Order Days to Complete|by_market",
    "t1905|Work Order Days to Complete|by_scope",
    "t1906|Work Order Days to Complete|by_wo_status",
    "t1907|Work Order Days to Complete|by_vendor",
    "t1908|Work Order Days to Complete|by_repair_type",
    "t1909|Work Order Days to Complete|by_market_x_month",
    "t1910|Work Order Days to Complete|by_wo_status_x_month",
    "t1911|Work Order Property Count|grand_total",
    "t1912|Work Order Property Count|by_month",
    "t1913|Work Order Property Count|by_prop_toggle",
    "t1914|Work Order Property Count|by_market",
    "t1915|Work Order Property Count|by_scope",
    "t1916|Work Order Property Count|by_wo_status",
    "t1917|Work Order Property Count|by_vendor",
    "t1918|Work Order Property Count|by_repair_type",
    "t1919|Work Order Property Count|by_market_x_month",
    "t1920|Work Order Property Count|by_wo_status_x_month",
    "t1921|Work Order Vendor Count|grand_total",
    "t1922|Work Order Vendor Count|by_month",
    "t1923|Work Order Vendor Count|by_prop_toggle",
    "t1924|Work Order Vendor Count|by_market",
    "t1925|Work Order Vendor Count|by_scope",
    "t1926|Work Order Vendor Count|by_wo_status",
    "t1927|Work Order Vendor Count|by_vendor",
    "t1928|Work Order Vendor Count|by_repair_type",
    "t1929|Work Order Vendor Count|by_market_x_month",
    "t1930|Work Order Vendor Count|by_wo_status_x_month",
    "t1931|Work Orders Completed Trailing 30 Days|grand_total",
    "t1932|Work Orders Completed Trailing 30 Days|by_month",
    "t1933|Work Orders Completed Trailing 30 Days|by_prop_toggle",
    "t1934|Work Orders Completed Trailing 30 Days|by_market",
    "t1935|Work Orders Completed Trailing 30 Days|by_scope",
    "t1936|Work Orders Completed Trailing 30 Days|by_wo_status",
    "t1937|Work Orders Completed Trailing 30 Days|by_vendor",
    "t1938|Work Orders Completed Trailing 30 Days|by_repair_type",
    "t1939|Work Orders Completed Trailing 30 Days|by_market_x_month",
    "t1940|Work Orders Completed Trailing 30 Days|by_wo_status_x_month",
    "t1941|count of tech|grand_total",
    "t1942|count of tech|by_month",
    "t1943|count of tech|by_prop_toggle",
    "t1944|count of tech|by_market",
    "t1945|count of tech|by_scope",
    "t1946|count of tech|by_wo_status",
    "t1947|count of tech|by_vendor",
    "t1948|count of tech|by_repair_type",
    "t1949|count of tech|by_market_x_month",
    "t1950|count of tech|by_wo_status_x_month",
};

// ═══════════════════════════════════════════════════════════════════════════════
// DIMENSION MAP — context label → DAX column reference
// ═══════════════════════════════════════════════════════════════════════════════
var groupByColumns = new Dictionary<string, string>
{
    { "by_month", "'Calendar'[Start of Month]" },
    { "by_prop_toggle", "'Proportionate Ownership Toggle'[Proportionate Values]" },
    { "by_market", "'Properties'[Property Market Reporting]" },
    { "by_scope", "'Projects'[Project Scope Type Desc]" },
    { "by_wo_status", "'Work Orders'[Work Order Status Desc]" },
    { "by_vendor", "'Vendors'[Vendor Name]" },
    { "by_repair_type", "'Repair Type'[Repair Type]" },
    { "by_market_x_month", "'Properties'[Property Market Reporting]|'Calendar'[Start of Month]" },
    { "by_wo_status_x_month", "'Work Orders'[Work Order Status Desc]|'Calendar'[Start of Month]" },
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
