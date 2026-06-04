// =============================================================================
// capture-snapshot.csx  (v7 — query timeout + smoke test + memory watchdog)
// =============================================================================
// TEMPLATE — model-agnostic. Filled in per session by the regression-testing
// skill (skills/skill-regression-testing.md). The skill replaces ONLY these
// four sections; everything else is verbatim and tested:
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
// OUTPUTS:  {label}.json          — full snapshot with results + timing
//           {label}-timing.csv    — per-test-case timing for performance comparison
//           {label}-errors.log    — error details (only if errors occur)
//           {label}-timeouts.log  — timed-out tests for manual investigation (only if timeouts occur)
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
    ?? "mc-baseline-smoketest";

// Model name — written to the snapshot JSON header for downstream comparison
// reports. Replaced per session by the regression-testing skill (or override
// via the MODEL_NAME env var when running from the CLI).
var modelName = System.Environment.GetEnvironmentVariable("MODEL_NAME")
    ?? "Maintenance and Construction";

var _envDiag = System.Environment.GetEnvironmentVariable("DIAGNOSTIC_MODE");
var diagnosticMode = _envDiag != null
    ? _envDiag.Equals("true", StringComparison.OrdinalIgnoreCase)
    : true;   // ← first 8 test cases show Info() popups for pre-run validation

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
    "'Calendar'[Year] = 2026",
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
// queryTimeoutMs: per-query hard cap enforced server-side via AdomdCommand.
//   60 seconds is generous for well-formed DAX (typical slow queries: 5–8s).
//   Set to 0 to disable (not recommended for large test suites).
var _envTimeout = System.Environment.GetEnvironmentVariable("QUERY_TIMEOUT_MS");
var queryTimeoutMs = _envTimeout != null ? int.Parse(_envTimeout) : 20000;

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
var memoryThresholdPct = _envMemPct != null ? double.Parse(_envMemPct) : 70.0;

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
// Port discovery — TE3's Model.Database wrapper hides .Server, so we cannot
// extract the connection string from the model directly. For local PBIP
// workspace mode, scan PBI Desktop's AnalysisServicesWorkspaces folder for
// running msmdsrv instances and match by database name (which TE3 DOES expose).
// Runs ONCE at script start, then captured via closure into ExecuteDaxWithTimeout.
//
// Override: set CONNECTION_STRING env var to skip discovery (for XMLA endpoints
// or non-standard installs).
// ─────────────────────────────────────────────────────────────────────────────
string discoveredConnStr = null;
if (useDirectAdomd)
{
    discoveredConnStr = System.Environment.GetEnvironmentVariable("CONNECTION_STRING");
    if (string.IsNullOrEmpty(discoveredConnStr))
    {
        var localAppData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        var userProfile = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        var workspaceRoots = new[] {
            // Standard PBI Desktop install
            System.IO.Path.Combine(localAppData, "Microsoft", "Power BI Desktop", "AnalysisServicesWorkspaces"),
            // Microsoft Store install (sandboxed Packages path)
            System.IO.Path.Combine(localAppData, "Packages", "Microsoft.MicrosoftPowerBIDesktop_8wekyb3d8bbwe",
                "LocalCache", "Local", "Microsoft", "Power BI Desktop Store App", "AnalysisServicesWorkspaces"),
            // Store App provisioned directly under user profile (observed on dkay's machine)
            System.IO.Path.Combine(userProfile, "Microsoft", "Power BI Desktop Store App", "AnalysisServicesWorkspaces"),
        };

        // Collect (port, sourceFolder) pairs from every workspace under either install path.
        var portCandidates = new System.Collections.Generic.List<Tuple<string, string>>();
        var rootsChecked = new System.Collections.Generic.List<string>();
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

                // msmdsrv.port.txt is UTF-16 LE (with BOM). Read bytes and keep only
                // ASCII digits — encoding-agnostic and immune to BOM/whitespace noise.
                var portBytes = System.IO.File.ReadAllBytes(portFile);
                var portSb = new System.Text.StringBuilder();
                foreach (var b in portBytes)
                    if (b >= (byte)'0' && b <= (byte)'9') portSb.Append((char)b);
                var port = portSb.ToString();
                if (!string.IsNullOrEmpty(port)) portCandidates.Add(Tuple.Create(port, ws));
            }
        }

        if (portCandidates.Count == 0)
            throw new Exception(
                "No running msmdsrv instances found. Open the .pbip in Power BI Desktop first. " +
                "Workspace roots checked: " + string.Join(" | ", rootsChecked));

        // Match by Model.Database.Name. In local workspace mode this is a GUID that
        // uniquely identifies which msmdsrv hosts the model TE3 is currently connected to.
        var targetDbName = Model.Database.Name;
        var probeLog = new System.Collections.Generic.List<string>();
        foreach (var pc in portCandidates)
        {
            var port = pc.Item1;
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
                    // column (common for dimension columns like [Property Market Reporting]).
                    // Manual iteration skips the schema's IsKey/AllowDBNull metadata and
                    // simply collects every row as-is.
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
    "t0001|Avg Move In Work Order Cost|grand_total",
    "t0002|Avg Move In Work Order Cost|by_month",
    "t0003|Avg Move In Work Order Cost|by_prop_toggle",
    "t0004|Avg Move In Work Order Cost|by_market",
    "t0005|Avg Move In Work Order Cost|by_scope",
    "t0006|Avg Move In Work Order Cost|by_wo_status",
    "t0007|Avg Move In Work Order Cost|by_vendor",
    "t0008|Avg Move In Work Order Cost|by_repair_type",
    "t0009|Avg Move In Work Order Cost|by_market_x_month",
    "t0010|Count of Properties with Open Work Orders Age by Group|grand_total",
    "t0011|Count of Properties with Open Work Orders Age by Group|by_month",
    "t0012|Count of Properties with Open Work Orders Age by Group|by_prop_toggle",
    "t0013|Count of Properties with Open Work Orders Age by Group|by_market",
    "t0014|Count of Properties with Open Work Orders Age by Group|by_scope",
    "t0015|Count of Properties with Open Work Orders Age by Group|by_wo_status",
    "t0016|Count of Properties with Open Work Orders Age by Group|by_vendor",
    "t0017|Count of Properties with Open Work Orders Age by Group|by_repair_type",
    "t0018|Count of Properties with Open Work Orders Age by Group|by_market_x_month",
    "t0019|Open Work Orders per Property|grand_total",
    "t0020|Open Work Orders per Property|by_month",
    "t0021|Open Work Orders per Property|by_prop_toggle",
    "t0022|Open Work Orders per Property|by_market",
    "t0023|Open Work Orders per Property|by_scope",
    "t0024|Open Work Orders per Property|by_wo_status",
    "t0025|Open Work Orders per Property|by_vendor",
    "t0026|Open Work Orders per Property|by_repair_type",
    "t0027|Open Work Orders per Property|by_market_x_month",
    "t0028|Open Work Order Age|grand_total",
    "t0029|Open Work Order Age|by_month",
    "t0030|Open Work Order Age|by_prop_toggle",
    "t0031|Open Work Order Age|by_market",
    "t0032|Open Work Order Age|by_scope",
    "t0033|Open Work Order Age|by_wo_status",
    "t0034|Open Work Order Age|by_vendor",
    "t0035|Open Work Order Age|by_repair_type",
    "t0036|Open Work Order Age|by_market_x_month",
    "t0037|Avg Cost per Open Work Order|grand_total",
    "t0038|Avg Cost per Open Work Order|by_month",
    "t0039|Avg Cost per Open Work Order|by_prop_toggle",
    "t0040|Avg Cost per Open Work Order|by_market",
    "t0041|Avg Cost per Open Work Order|by_scope",
    "t0042|Avg Cost per Open Work Order|by_wo_status",
    "t0043|Avg Cost per Open Work Order|by_vendor",
    "t0044|Avg Cost per Open Work Order|by_repair_type",
    "t0045|Avg Cost per Open Work Order|by_market_x_month",
    "t0046|Open Work Orders per Vendor|grand_total",
    "t0047|Open Work Orders per Vendor|by_month",
    "t0048|Open Work Orders per Vendor|by_prop_toggle",
    "t0049|Open Work Orders per Vendor|by_market",
    "t0050|Open Work Orders per Vendor|by_scope",
    "t0051|Open Work Orders per Vendor|by_wo_status",
    "t0052|Open Work Orders per Vendor|by_vendor",
    "t0053|Open Work Orders per Vendor|by_repair_type",
    "t0054|Open Work Orders per Vendor|by_market_x_month",
    "t0055|Project Work Order Count|grand_total",
    "t0056|Project Work Order Count|by_month",
    "t0057|Project Work Order Count|by_prop_toggle",
    "t0058|Project Work Order Count|by_market",
    "t0059|Project Work Order Count|by_scope",
    "t0060|Project Work Order Count|by_wo_status",
    "t0061|Project Work Order Count|by_vendor",
    "t0062|Project Work Order Count|by_repair_type",
    "t0063|Project Work Order Count|by_market_x_month",
    "t0064|Avg Daily Open Work Orders Count|grand_total",
    "t0065|Avg Daily Open Work Orders Count|by_month",
    "t0066|Avg Daily Open Work Orders Count|by_prop_toggle",
    "t0067|Avg Daily Open Work Orders Count|by_market",
    "t0068|Avg Daily Open Work Orders Count|by_scope",
    "t0069|Avg Daily Open Work Orders Count|by_wo_status",
    "t0070|Avg Daily Open Work Orders Count|by_vendor",
    "t0071|Avg Daily Open Work Orders Count|by_repair_type",
    "t0072|Avg Daily Open Work Orders Count|by_market_x_month",
    "t0073|Avg Open Work Orders Age|grand_total",
    "t0074|Avg Open Work Orders Age|by_month",
    "t0075|Avg Open Work Orders Age|by_prop_toggle",
    "t0076|Avg Open Work Orders Age|by_market",
    "t0077|Avg Open Work Orders Age|by_scope",
    "t0078|Avg Open Work Orders Age|by_wo_status",
    "t0079|Avg Open Work Orders Age|by_vendor",
    "t0080|Avg Open Work Orders Age|by_repair_type",
    "t0081|Avg Open Work Orders Age|by_market_x_month",
    "t0082|% of Properties with Open Work Orders|grand_total",
    "t0083|% of Properties with Open Work Orders|by_month",
    "t0084|% of Properties with Open Work Orders|by_prop_toggle",
    "t0085|% of Properties with Open Work Orders|by_market",
    "t0086|% of Properties with Open Work Orders|by_scope",
    "t0087|% of Properties with Open Work Orders|by_wo_status",
    "t0088|% of Properties with Open Work Orders|by_vendor",
    "t0089|% of Properties with Open Work Orders|by_repair_type",
    "t0090|% of Properties with Open Work Orders|by_market_x_month",
    "t0091|Open Work Order Costs|grand_total",
    "t0092|Open Work Order Costs|by_month",
    "t0093|Open Work Order Costs|by_prop_toggle",
    "t0094|Open Work Order Costs|by_market",
    "t0095|Open Work Order Costs|by_scope",
    "t0096|Open Work Order Costs|by_wo_status",
    "t0097|Open Work Order Costs|by_vendor",
    "t0098|Open Work Order Costs|by_repair_type",
    "t0099|Open Work Order Costs|by_market_x_month",
    "t0100|Open Work Orders Count (Final Age)|grand_total",
    "t0101|Open Work Orders Count (Final Age)|by_month",
    "t0102|Open Work Orders Count (Final Age)|by_prop_toggle",
    "t0103|Open Work Orders Count (Final Age)|by_market",
    "t0104|Open Work Orders Count (Final Age)|by_scope",
    "t0105|Open Work Orders Count (Final Age)|by_wo_status",
    "t0106|Open Work Orders Count (Final Age)|by_vendor",
    "t0107|Open Work Orders Count (Final Age)|by_repair_type",
    "t0108|Open Work Orders Count (Final Age)|by_market_x_month",
    "t0109|Open Work Orders Count|grand_total",
    "t0110|Open Work Orders Count|by_month",
    "t0111|Open Work Orders Count|by_prop_toggle",
    "t0112|Open Work Orders Count|by_market",
    "t0113|Open Work Orders Count|by_scope",
    "t0114|Open Work Orders Count|by_wo_status",
    "t0115|Open Work Orders Count|by_vendor",
    "t0116|Open Work Orders Count|by_repair_type",
    "t0117|Open Work Orders Count|by_market_x_month",
    "t0118|Open Work Order Properties Count|grand_total",
    "t0119|Open Work Order Properties Count|by_month",
    "t0120|Open Work Order Properties Count|by_prop_toggle",
    "t0121|Open Work Order Properties Count|by_market",
    "t0122|Open Work Order Properties Count|by_scope",
    "t0123|Open Work Order Properties Count|by_wo_status",
    "t0124|Open Work Order Properties Count|by_vendor",
    "t0125|Open Work Order Properties Count|by_repair_type",
    "t0126|Open Work Order Properties Count|by_market_x_month",
    "t0127|Open Work Order Vendors Count|grand_total",
    "t0128|Open Work Order Vendors Count|by_month",
    "t0129|Open Work Order Vendors Count|by_prop_toggle",
    "t0130|Open Work Order Vendors Count|by_market",
    "t0131|Open Work Order Vendors Count|by_scope",
    "t0132|Open Work Order Vendors Count|by_wo_status",
    "t0133|Open Work Order Vendors Count|by_vendor",
    "t0134|Open Work Order Vendors Count|by_repair_type",
    "t0135|Open Work Order Vendors Count|by_market_x_month",
    "t0136|% of Vendors with Open Work Orders|grand_total",
    "t0137|% of Vendors with Open Work Orders|by_month",
    "t0138|% of Vendors with Open Work Orders|by_prop_toggle",
    "t0139|% of Vendors with Open Work Orders|by_market",
    "t0140|% of Vendors with Open Work Orders|by_scope",
    "t0141|% of Vendors with Open Work Orders|by_wo_status",
    "t0142|% of Vendors with Open Work Orders|by_vendor",
    "t0143|% of Vendors with Open Work Orders|by_repair_type",
    "t0144|% of Vendors with Open Work Orders|by_market_x_month",
    "t0145|Avg Completed Occupied Maintenance Project Cost|grand_total",
    "t0146|Avg Completed Occupied Maintenance Project Cost|by_month",
    "t0147|Avg Completed Occupied Maintenance Project Cost|by_prop_toggle",
    "t0148|Avg Completed Occupied Maintenance Project Cost|by_market",
    "t0149|Avg Completed Occupied Maintenance Project Cost|by_scope",
    "t0150|Avg Completed Occupied Maintenance Project Cost|by_wo_status",
    "t0151|Avg Completed Occupied Maintenance Project Cost|by_vendor",
    "t0152|Avg Completed Occupied Maintenance Project Cost|by_repair_type",
    "t0153|Avg Completed Occupied Maintenance Project Cost|by_market_x_month",
    "t0154|Average Work Order Cost by Work Order Created Date|grand_total",
    "t0155|Average Work Order Cost by Work Order Created Date|by_month",
    "t0156|Average Work Order Cost by Work Order Created Date|by_prop_toggle",
    "t0157|Average Work Order Cost by Work Order Created Date|by_market",
    "t0158|Average Work Order Cost by Work Order Created Date|by_scope",
    "t0159|Average Work Order Cost by Work Order Created Date|by_wo_status",
    "t0160|Average Work Order Cost by Work Order Created Date|by_vendor",
    "t0161|Average Work Order Cost by Work Order Created Date|by_repair_type",
    "t0162|Average Work Order Cost by Work Order Created Date|by_market_x_month",
    "t0163|Median Work Order Costs by Created Date|grand_total",
    "t0164|Median Work Order Costs by Created Date|by_month",
    "t0165|Median Work Order Costs by Created Date|by_prop_toggle",
    "t0166|Median Work Order Costs by Created Date|by_market",
    "t0167|Median Work Order Costs by Created Date|by_scope",
    "t0168|Median Work Order Costs by Created Date|by_wo_status",
    "t0169|Median Work Order Costs by Created Date|by_vendor",
    "t0170|Median Work Order Costs by Created Date|by_repair_type",
    "t0171|Median Work Order Costs by Created Date|by_market_x_month",
    "t0172|Average Vendor Maintenance Work Order Costs Complete Date|grand_total",
    "t0173|Average Vendor Maintenance Work Order Costs Complete Date|by_month",
    "t0174|Average Vendor Maintenance Work Order Costs Complete Date|by_prop_toggle",
    "t0175|Average Vendor Maintenance Work Order Costs Complete Date|by_market",
    "t0176|Average Vendor Maintenance Work Order Costs Complete Date|by_scope",
    "t0177|Average Vendor Maintenance Work Order Costs Complete Date|by_wo_status",
    "t0178|Average Vendor Maintenance Work Order Costs Complete Date|by_vendor",
    "t0179|Average Vendor Maintenance Work Order Costs Complete Date|by_repair_type",
    "t0180|Average Vendor Maintenance Work Order Costs Complete Date|by_market_x_month",
    "t0181|Average Work Order Cost by Work Order Completed Date|grand_total",
    "t0182|Average Work Order Cost by Work Order Completed Date|by_month",
    "t0183|Average Work Order Cost by Work Order Completed Date|by_prop_toggle",
    "t0184|Average Work Order Cost by Work Order Completed Date|by_market",
    "t0185|Average Work Order Cost by Work Order Completed Date|by_scope",
    "t0186|Average Work Order Cost by Work Order Completed Date|by_wo_status",
    "t0187|Average Work Order Cost by Work Order Completed Date|by_vendor",
    "t0188|Average Work Order Cost by Work Order Completed Date|by_repair_type",
    "t0189|Average Work Order Cost by Work Order Completed Date|by_market_x_month",
    "t0190|IHM Completed Work Order Count (by Created Date)|grand_total",
    "t0191|IHM Completed Work Order Count (by Created Date)|by_month",
    "t0192|IHM Completed Work Order Count (by Created Date)|by_prop_toggle",
    "t0193|IHM Completed Work Order Count (by Created Date)|by_market",
    "t0194|IHM Completed Work Order Count (by Created Date)|by_scope",
    "t0195|IHM Completed Work Order Count (by Created Date)|by_wo_status",
    "t0196|IHM Completed Work Order Count (by Created Date)|by_vendor",
    "t0197|IHM Completed Work Order Count (by Created Date)|by_repair_type",
    "t0198|IHM Completed Work Order Count (by Created Date)|by_market_x_month",
    "t0199|Work Order Count by Created Date|grand_total",
    "t0200|Work Order Count by Created Date|by_month",
    "t0201|Work Order Count by Created Date|by_prop_toggle",
    "t0202|Work Order Count by Created Date|by_market",
    "t0203|Work Order Count by Created Date|by_scope",
    "t0204|Work Order Count by Created Date|by_wo_status",
    "t0205|Work Order Count by Created Date|by_vendor",
    "t0206|Work Order Count by Created Date|by_repair_type",
    "t0207|Work Order Count by Created Date|by_market_x_month",
    "t0208|Work Order Count by Completed Date|grand_total",
    "t0209|Work Order Count by Completed Date|by_month",
    "t0210|Work Order Count by Completed Date|by_prop_toggle",
    "t0211|Work Order Count by Completed Date|by_market",
    "t0212|Work Order Count by Completed Date|by_scope",
    "t0213|Work Order Count by Completed Date|by_wo_status",
    "t0214|Work Order Count by Completed Date|by_vendor",
    "t0215|Work Order Count by Completed Date|by_repair_type",
    "t0216|Work Order Count by Completed Date|by_market_x_month",
    "t0217|Work Order Costs by Created Date|grand_total",
    "t0218|Work Order Costs by Created Date|by_month",
    "t0219|Work Order Costs by Created Date|by_prop_toggle",
    "t0220|Work Order Costs by Created Date|by_market",
    "t0221|Work Order Costs by Created Date|by_scope",
    "t0222|Work Order Costs by Created Date|by_wo_status",
    "t0223|Work Order Costs by Created Date|by_vendor",
    "t0224|Work Order Costs by Created Date|by_repair_type",
    "t0225|Work Order Costs by Created Date|by_market_x_month",
    "t0226|Work Order Costs by Completed Date|grand_total",
    "t0227|Work Order Costs by Completed Date|by_month",
    "t0228|Work Order Costs by Completed Date|by_prop_toggle",
    "t0229|Work Order Costs by Completed Date|by_market",
    "t0230|Work Order Costs by Completed Date|by_scope",
    "t0231|Work Order Costs by Completed Date|by_wo_status",
    "t0232|Work Order Costs by Completed Date|by_vendor",
    "t0233|Work Order Costs by Completed Date|by_repair_type",
    "t0234|Work Order Costs by Completed Date|by_market_x_month",
    "t0235|IHM Completed Work Order Count (by Completed Date)|grand_total",
    "t0236|IHM Completed Work Order Count (by Completed Date)|by_month",
    "t0237|IHM Completed Work Order Count (by Completed Date)|by_prop_toggle",
    "t0238|IHM Completed Work Order Count (by Completed Date)|by_market",
    "t0239|IHM Completed Work Order Count (by Completed Date)|by_scope",
    "t0240|IHM Completed Work Order Count (by Completed Date)|by_wo_status",
    "t0241|IHM Completed Work Order Count (by Completed Date)|by_vendor",
    "t0242|IHM Completed Work Order Count (by Completed Date)|by_repair_type",
    "t0243|IHM Completed Work Order Count (by Completed Date)|by_market_x_month",
    "t0244|Vendor Maintenance Work Order Costs Complete Date|grand_total",
    "t0245|Vendor Maintenance Work Order Costs Complete Date|by_month",
    "t0246|Vendor Maintenance Work Order Costs Complete Date|by_prop_toggle",
    "t0247|Vendor Maintenance Work Order Costs Complete Date|by_market",
    "t0248|Vendor Maintenance Work Order Costs Complete Date|by_scope",
    "t0249|Vendor Maintenance Work Order Costs Complete Date|by_wo_status",
    "t0250|Vendor Maintenance Work Order Costs Complete Date|by_vendor",
    "t0251|Vendor Maintenance Work Order Costs Complete Date|by_repair_type",
    "t0252|Vendor Maintenance Work Order Costs Complete Date|by_market_x_month",
    "t0253|Request Market Vendor %|grand_total",
    "t0254|Request Market Vendor %|by_month",
    "t0255|Request Market Vendor %|by_prop_toggle",
    "t0256|Request Market Vendor %|by_market",
    "t0257|Request Market Vendor %|by_scope",
    "t0258|Request Market Vendor %|by_wo_status",
    "t0259|Request Market Vendor %|by_vendor",
    "t0260|Request Market Vendor %|by_repair_type",
    "t0261|Request Market Vendor %|by_market_x_month",
    "t0262|Homes Serviced Internally Work Order Costs by Completed Date|grand_total",
    "t0263|Homes Serviced Internally Work Order Costs by Completed Date|by_month",
    "t0264|Homes Serviced Internally Work Order Costs by Completed Date|by_prop_toggle",
    "t0265|Homes Serviced Internally Work Order Costs by Completed Date|by_market",
    "t0266|Homes Serviced Internally Work Order Costs by Completed Date|by_scope",
    "t0267|Homes Serviced Internally Work Order Costs by Completed Date|by_wo_status",
    "t0268|Homes Serviced Internally Work Order Costs by Completed Date|by_vendor",
    "t0269|Homes Serviced Internally Work Order Costs by Completed Date|by_repair_type",
    "t0270|Homes Serviced Internally Work Order Costs by Completed Date|by_market_x_month",
    "t0271|Average In-House Maintenance Work Order Costs Complete Date|grand_total",
    "t0272|Average In-House Maintenance Work Order Costs Complete Date|by_month",
    "t0273|Average In-House Maintenance Work Order Costs Complete Date|by_prop_toggle",
    "t0274|Average In-House Maintenance Work Order Costs Complete Date|by_market",
    "t0275|Average In-House Maintenance Work Order Costs Complete Date|by_scope",
    "t0276|Average In-House Maintenance Work Order Costs Complete Date|by_wo_status",
    "t0277|Average In-House Maintenance Work Order Costs Complete Date|by_vendor",
    "t0278|Average In-House Maintenance Work Order Costs Complete Date|by_repair_type",
    "t0279|Average In-House Maintenance Work Order Costs Complete Date|by_market_x_month",
    "t0280|Vendor Maintenance Work Order Count Complete Date|grand_total",
    "t0281|Vendor Maintenance Work Order Count Complete Date|by_month",
    "t0282|Vendor Maintenance Work Order Count Complete Date|by_prop_toggle",
    "t0283|Vendor Maintenance Work Order Count Complete Date|by_market",
    "t0284|Vendor Maintenance Work Order Count Complete Date|by_scope",
    "t0285|Vendor Maintenance Work Order Count Complete Date|by_wo_status",
    "t0286|Vendor Maintenance Work Order Count Complete Date|by_vendor",
    "t0287|Vendor Maintenance Work Order Count Complete Date|by_repair_type",
    "t0288|Vendor Maintenance Work Order Count Complete Date|by_market_x_month",
    "t0289|Work Order Counts|grand_total",
    "t0290|Work Order Counts|by_month",
    "t0291|Work Order Counts|by_prop_toggle",
    "t0292|Work Order Counts|by_market",
    "t0293|Work Order Counts|by_scope",
    "t0294|Work Order Counts|by_wo_status",
    "t0295|Work Order Counts|by_vendor",
    "t0296|Work Order Counts|by_repair_type",
    "t0297|Work Order Counts|by_market_x_month",
    "t0298|Completed Occupied Maintenance Project Costs|grand_total",
    "t0299|Completed Occupied Maintenance Project Costs|by_month",
    "t0300|Completed Occupied Maintenance Project Costs|by_prop_toggle",
    "t0301|Completed Occupied Maintenance Project Costs|by_market",
    "t0302|Completed Occupied Maintenance Project Costs|by_scope",
    "t0303|Completed Occupied Maintenance Project Costs|by_wo_status",
    "t0304|Completed Occupied Maintenance Project Costs|by_vendor",
    "t0305|Completed Occupied Maintenance Project Costs|by_repair_type",
    "t0306|Completed Occupied Maintenance Project Costs|by_market_x_month",
    "t0307|First Time Fix Rate %|grand_total",
    "t0308|First Time Fix Rate %|by_month",
    "t0309|First Time Fix Rate %|by_prop_toggle",
    "t0310|First Time Fix Rate %|by_market",
    "t0311|First Time Fix Rate %|by_scope",
    "t0312|First Time Fix Rate %|by_wo_status",
    "t0313|First Time Fix Rate %|by_vendor",
    "t0314|First Time Fix Rate %|by_repair_type",
    "t0315|First Time Fix Rate %|by_market_x_month",
    "t0316|Request Market Vendor|grand_total",
    "t0317|Request Market Vendor|by_month",
    "t0318|Request Market Vendor|by_prop_toggle",
    "t0319|Request Market Vendor|by_market",
    "t0320|Request Market Vendor|by_scope",
    "t0321|Request Market Vendor|by_wo_status",
    "t0322|Request Market Vendor|by_vendor",
    "t0323|Request Market Vendor|by_repair_type",
    "t0324|Request Market Vendor|by_market_x_month",
    "t0325|Projected Avg Project Cost (Actual + ECD)|grand_total",
    "t0326|Projected Avg Project Cost (Actual + ECD)|by_month",
    "t0327|Projected Avg Project Cost (Actual + ECD)|by_prop_toggle",
    "t0328|Projected Avg Project Cost (Actual + ECD)|by_market",
    "t0329|Projected Avg Project Cost (Actual + ECD)|by_scope",
    "t0330|Projected Avg Project Cost (Actual + ECD)|by_wo_status",
    "t0331|Projected Avg Project Cost (Actual + ECD)|by_vendor",
    "t0332|Projected Avg Project Cost (Actual + ECD)|by_repair_type",
    "t0333|Projected Avg Project Cost (Actual + ECD)|by_market_x_month",
    "t0334|Project Count by Completed Date (Ignores 0 and Nulls)|grand_total",
    "t0335|Project Count by Completed Date (Ignores 0 and Nulls)|by_month",
    "t0336|Project Count by Completed Date (Ignores 0 and Nulls)|by_prop_toggle",
    "t0337|Project Count by Completed Date (Ignores 0 and Nulls)|by_market",
    "t0338|Project Count by Completed Date (Ignores 0 and Nulls)|by_scope",
    "t0339|Project Count by Completed Date (Ignores 0 and Nulls)|by_wo_status",
    "t0340|Project Count by Completed Date (Ignores 0 and Nulls)|by_vendor",
    "t0341|Project Count by Completed Date (Ignores 0 and Nulls)|by_repair_type",
    "t0342|Project Count by Completed Date (Ignores 0 and Nulls)|by_market_x_month",
    "t0343|Return trip needed|grand_total",
    "t0344|Return trip needed|by_month",
    "t0345|Return trip needed|by_prop_toggle",
    "t0346|Return trip needed|by_market",
    "t0347|Return trip needed|by_scope",
    "t0348|Return trip needed|by_wo_status",
    "t0349|Return trip needed|by_vendor",
    "t0350|Return trip needed|by_repair_type",
    "t0351|Return trip needed|by_market_x_month",
    "t0352|Homes Serviced Cost Internally|grand_total",
    "t0353|Homes Serviced Cost Internally|by_month",
    "t0354|Homes Serviced Cost Internally|by_prop_toggle",
    "t0355|Homes Serviced Cost Internally|by_market",
    "t0356|Homes Serviced Cost Internally|by_scope",
    "t0357|Homes Serviced Cost Internally|by_wo_status",
    "t0358|Homes Serviced Cost Internally|by_vendor",
    "t0359|Homes Serviced Cost Internally|by_repair_type",
    "t0360|Homes Serviced Cost Internally|by_market_x_month",
    "t0361|Project Count by Estimated Completion Date (Ignore 0 and Nulls)|grand_total",
    "t0362|Project Count by Estimated Completion Date (Ignore 0 and Nulls)|by_month",
    "t0363|Project Count by Estimated Completion Date (Ignore 0 and Nulls)|by_prop_toggle",
    "t0364|Project Count by Estimated Completion Date (Ignore 0 and Nulls)|by_market",
    "t0365|Project Count by Estimated Completion Date (Ignore 0 and Nulls)|by_scope",
    "t0366|Project Count by Estimated Completion Date (Ignore 0 and Nulls)|by_wo_status",
    "t0367|Project Count by Estimated Completion Date (Ignore 0 and Nulls)|by_vendor",
    "t0368|Project Count by Estimated Completion Date (Ignore 0 and Nulls)|by_repair_type",
    "t0369|Project Count by Estimated Completion Date (Ignore 0 and Nulls)|by_market_x_month",
    "t0370|Average Casualty Work Order Costs Complete Date|grand_total",
    "t0371|Average Casualty Work Order Costs Complete Date|by_month",
    "t0372|Average Casualty Work Order Costs Complete Date|by_prop_toggle",
    "t0373|Average Casualty Work Order Costs Complete Date|by_market",
    "t0374|Average Casualty Work Order Costs Complete Date|by_scope",
    "t0375|Average Casualty Work Order Costs Complete Date|by_wo_status",
    "t0376|Average Casualty Work Order Costs Complete Date|by_vendor",
    "t0377|Average Casualty Work Order Costs Complete Date|by_repair_type",
    "t0378|Average Casualty Work Order Costs Complete Date|by_market_x_month",
    "t0379|Average HOA Work Order Costs Complete Date|grand_total",
    "t0380|Average HOA Work Order Costs Complete Date|by_month",
    "t0381|Average HOA Work Order Costs Complete Date|by_prop_toggle",
    "t0382|Average HOA Work Order Costs Complete Date|by_market",
    "t0383|Average HOA Work Order Costs Complete Date|by_scope",
    "t0384|Average HOA Work Order Costs Complete Date|by_wo_status",
    "t0385|Average HOA Work Order Costs Complete Date|by_vendor",
    "t0386|Average HOA Work Order Costs Complete Date|by_repair_type",
    "t0387|Average HOA Work Order Costs Complete Date|by_market_x_month",
    "t0388|Return trip needed %|grand_total",
    "t0389|Return trip needed %|by_month",
    "t0390|Return trip needed %|by_prop_toggle",
    "t0391|Return trip needed %|by_market",
    "t0392|Return trip needed %|by_scope",
    "t0393|Return trip needed %|by_wo_status",
    "t0394|Return trip needed %|by_vendor",
    "t0395|Return trip needed %|by_repair_type",
    "t0396|Return trip needed %|by_market_x_month",
    "t0397|In-House Utilization Rate (excl Appliances) Project Cost by Completed Date|grand_total",
    "t0398|In-House Utilization Rate (excl Appliances) Project Cost by Completed Date|by_month",
    "t0399|In-House Utilization Rate (excl Appliances) Project Cost by Completed Date|by_prop_toggle",
    "t0400|In-House Utilization Rate (excl Appliances) Project Cost by Completed Date|by_market",
    "t0401|In-House Utilization Rate (excl Appliances) Project Cost by Completed Date|by_scope",
    "t0402|In-House Utilization Rate (excl Appliances) Project Cost by Completed Date|by_wo_status",
    "t0403|In-House Utilization Rate (excl Appliances) Project Cost by Completed Date|by_vendor",
    "t0404|In-House Utilization Rate (excl Appliances) Project Cost by Completed Date|by_repair_type",
    "t0405|In-House Utilization Rate (excl Appliances) Project Cost by Completed Date|by_market_x_month",
    "t0406|Homes Serviced Project Cost by Completed Date|grand_total",
    "t0407|Homes Serviced Project Cost by Completed Date|by_month",
    "t0408|Homes Serviced Project Cost by Completed Date|by_prop_toggle",
    "t0409|Homes Serviced Project Cost by Completed Date|by_market",
    "t0410|Homes Serviced Project Cost by Completed Date|by_scope",
    "t0411|Homes Serviced Project Cost by Completed Date|by_wo_status",
    "t0412|Homes Serviced Project Cost by Completed Date|by_vendor",
    "t0413|Homes Serviced Project Cost by Completed Date|by_repair_type",
    "t0414|Homes Serviced Project Cost by Completed Date|by_market_x_month",
    "t0415|In-House Maintenance Work Order Count Complete Date|grand_total",
    "t0416|In-House Maintenance Work Order Count Complete Date|by_month",
    "t0417|In-House Maintenance Work Order Count Complete Date|by_prop_toggle",
    "t0418|In-House Maintenance Work Order Count Complete Date|by_market",
    "t0419|In-House Maintenance Work Order Count Complete Date|by_scope",
    "t0420|In-House Maintenance Work Order Count Complete Date|by_wo_status",
    "t0421|In-House Maintenance Work Order Count Complete Date|by_vendor",
    "t0422|In-House Maintenance Work Order Count Complete Date|by_repair_type",
    "t0423|In-House Maintenance Work Order Count Complete Date|by_market_x_month",
    "t0424|Vendor Move In Work Order Count by Resident Move In Date|grand_total",
    "t0425|Vendor Move In Work Order Count by Resident Move In Date|by_month",
    "t0426|Vendor Move In Work Order Count by Resident Move In Date|by_prop_toggle",
    "t0427|Vendor Move In Work Order Count by Resident Move In Date|by_market",
    "t0428|Vendor Move In Work Order Count by Resident Move In Date|by_scope",
    "t0429|Vendor Move In Work Order Count by Resident Move In Date|by_wo_status",
    "t0430|Vendor Move In Work Order Count by Resident Move In Date|by_vendor",
    "t0431|Vendor Move In Work Order Count by Resident Move In Date|by_repair_type",
    "t0432|Vendor Move In Work Order Count by Resident Move In Date|by_market_x_month",
    "t0433|Total Violations|grand_total",
    "t0434|Total Violations|by_month",
    "t0435|Total Violations|by_prop_toggle",
    "t0436|Total Violations|by_market",
    "t0437|Total Violations|by_scope",
    "t0438|Total Violations|by_wo_status",
    "t0439|Total Violations|by_vendor",
    "t0440|Total Violations|by_repair_type",
    "t0441|Total Violations|by_market_x_month",
    "t0442|Project Count by Created Date (Ignore 0 and Nulls)|grand_total",
    "t0443|Project Count by Created Date (Ignore 0 and Nulls)|by_month",
    "t0444|Project Count by Created Date (Ignore 0 and Nulls)|by_prop_toggle",
    "t0445|Project Count by Created Date (Ignore 0 and Nulls)|by_market",
    "t0446|Project Count by Created Date (Ignore 0 and Nulls)|by_scope",
    "t0447|Project Count by Created Date (Ignore 0 and Nulls)|by_wo_status",
    "t0448|Project Count by Created Date (Ignore 0 and Nulls)|by_vendor",
    "t0449|Project Count by Created Date (Ignore 0 and Nulls)|by_repair_type",
    "t0450|Project Count by Created Date (Ignore 0 and Nulls)|by_market_x_month",
    "t0451|Average Cost per Homes Serviced|grand_total",
    "t0452|Average Cost per Homes Serviced|by_month",
    "t0453|Average Cost per Homes Serviced|by_prop_toggle",
    "t0454|Average Cost per Homes Serviced|by_market",
    "t0455|Average Cost per Homes Serviced|by_scope",
    "t0456|Average Cost per Homes Serviced|by_wo_status",
    "t0457|Average Cost per Homes Serviced|by_vendor",
    "t0458|Average Cost per Homes Serviced|by_repair_type",
    "t0459|Average Cost per Homes Serviced|by_market_x_month",
    "t0460|Average Cost per Homes Serviced Externally|grand_total",
    "t0461|Average Cost per Homes Serviced Externally|by_month",
    "t0462|Average Cost per Homes Serviced Externally|by_prop_toggle",
    "t0463|Average Cost per Homes Serviced Externally|by_market",
    "t0464|Average Cost per Homes Serviced Externally|by_scope",
    "t0465|Average Cost per Homes Serviced Externally|by_wo_status",
    "t0466|Average Cost per Homes Serviced Externally|by_vendor",
    "t0467|Average Cost per Homes Serviced Externally|by_repair_type",
    "t0468|Average Cost per Homes Serviced Externally|by_market_x_month",
    "t0469|Homes Serviced By Vendor & Technician|grand_total",
    "t0470|Homes Serviced By Vendor & Technician|by_month",
    "t0471|Homes Serviced By Vendor & Technician|by_prop_toggle",
    "t0472|Homes Serviced By Vendor & Technician|by_market",
    "t0473|Homes Serviced By Vendor & Technician|by_scope",
    "t0474|Homes Serviced By Vendor & Technician|by_wo_status",
    "t0475|Homes Serviced By Vendor & Technician|by_vendor",
    "t0476|Homes Serviced By Vendor & Technician|by_repair_type",
    "t0477|Homes Serviced By Vendor & Technician|by_market_x_month",
    "t0478|Move In Work Order Count by Resident Move In Date|grand_total",
    "t0479|Move In Work Order Count by Resident Move In Date|by_month",
    "t0480|Move In Work Order Count by Resident Move In Date|by_prop_toggle",
    "t0481|Move In Work Order Count by Resident Move In Date|by_market",
    "t0482|Move In Work Order Count by Resident Move In Date|by_scope",
    "t0483|Move In Work Order Count by Resident Move In Date|by_wo_status",
    "t0484|Move In Work Order Count by Resident Move In Date|by_vendor",
    "t0485|Move In Work Order Count by Resident Move In Date|by_repair_type",
    "t0486|Move In Work Order Count by Resident Move In Date|by_market_x_month",
    "t0487|Annualized Avg Project Cost per Property by Project Complete Date|grand_total",
    "t0488|Annualized Avg Project Cost per Property by Project Complete Date|by_month",
    "t0489|Annualized Avg Project Cost per Property by Project Complete Date|by_prop_toggle",
    "t0490|Annualized Avg Project Cost per Property by Project Complete Date|by_market",
    "t0491|Annualized Avg Project Cost per Property by Project Complete Date|by_scope",
    "t0492|Annualized Avg Project Cost per Property by Project Complete Date|by_wo_status",
    "t0493|Annualized Avg Project Cost per Property by Project Complete Date|by_vendor",
    "t0494|Annualized Avg Project Cost per Property by Project Complete Date|by_repair_type",
    "t0495|Annualized Avg Project Cost per Property by Project Complete Date|by_market_x_month",
    "t0496|Average Project Cost by Estimated Completion Date|grand_total",
    "t0497|Average Project Cost by Estimated Completion Date|by_month",
    "t0498|Average Project Cost by Estimated Completion Date|by_prop_toggle",
    "t0499|Average Project Cost by Estimated Completion Date|by_market",
    "t0500|Average Project Cost by Estimated Completion Date|by_scope",
    "t0501|Average Project Cost by Estimated Completion Date|by_wo_status",
    "t0502|Average Project Cost by Estimated Completion Date|by_vendor",
    "t0503|Average Project Cost by Estimated Completion Date|by_repair_type",
    "t0504|Average Project Cost by Estimated Completion Date|by_market_x_month",
    "t0505|Total Property Count with Open In Progress Dates|grand_total",
    "t0506|Total Property Count with Open In Progress Dates|by_month",
    "t0507|Total Property Count with Open In Progress Dates|by_prop_toggle",
    "t0508|Total Property Count with Open In Progress Dates|by_market",
    "t0509|Total Property Count with Open In Progress Dates|by_scope",
    "t0510|Total Property Count with Open In Progress Dates|by_wo_status",
    "t0511|Total Property Count with Open In Progress Dates|by_vendor",
    "t0512|Total Property Count with Open In Progress Dates|by_repair_type",
    "t0513|Total Property Count with Open In Progress Dates|by_market_x_month",
    "t0514|Courtesy Violations|grand_total",
    "t0515|Courtesy Violations|by_month",
    "t0516|Courtesy Violations|by_prop_toggle",
    "t0517|Courtesy Violations|by_market",
    "t0518|Courtesy Violations|by_scope",
    "t0519|Courtesy Violations|by_wo_status",
    "t0520|Courtesy Violations|by_vendor",
    "t0521|Courtesy Violations|by_repair_type",
    "t0522|Courtesy Violations|by_market_x_month",
    "t0523|Average Project Cost by Created Date|grand_total",
    "t0524|Average Project Cost by Created Date|by_month",
    "t0525|Average Project Cost by Created Date|by_prop_toggle",
    "t0526|Average Project Cost by Created Date|by_market",
    "t0527|Average Project Cost by Created Date|by_scope",
    "t0528|Average Project Cost by Created Date|by_wo_status",
    "t0529|Average Project Cost by Created Date|by_vendor",
    "t0530|Average Project Cost by Created Date|by_repair_type",
    "t0531|Average Project Cost by Created Date|by_market_x_month",
    "t0532|Closed  Violations|grand_total",
    "t0533|Closed  Violations|by_month",
    "t0534|Closed  Violations|by_prop_toggle",
    "t0535|Closed  Violations|by_market",
    "t0536|Closed  Violations|by_scope",
    "t0537|Closed  Violations|by_wo_status",
    "t0538|Closed  Violations|by_vendor",
    "t0539|Closed  Violations|by_repair_type",
    "t0540|Closed  Violations|by_market_x_month",
    "t0541|Homes Serviced Cost Combined|grand_total",
    "t0542|Homes Serviced Cost Combined|by_month",
    "t0543|Homes Serviced Cost Combined|by_prop_toggle",
    "t0544|Homes Serviced Cost Combined|by_market",
    "t0545|Homes Serviced Cost Combined|by_scope",
    "t0546|Homes Serviced Cost Combined|by_wo_status",
    "t0547|Homes Serviced Cost Combined|by_vendor",
    "t0548|Homes Serviced Cost Combined|by_repair_type",
    "t0549|Homes Serviced Cost Combined|by_market_x_month",
    "t0550|Total Violation Amt|grand_total",
    "t0551|Total Violation Amt|by_month",
    "t0552|Total Violation Amt|by_prop_toggle",
    "t0553|Total Violation Amt|by_market",
    "t0554|Total Violation Amt|by_scope",
    "t0555|Total Violation Amt|by_wo_status",
    "t0556|Total Violation Amt|by_vendor",
    "t0557|Total Violation Amt|by_repair_type",
    "t0558|Total Violation Amt|by_market_x_month",
    "t0559|Total property count|grand_total",
    "t0560|Total property count|by_month",
    "t0561|Total property count|by_prop_toggle",
    "t0562|Total property count|by_market",
    "t0563|Total property count|by_scope",
    "t0564|Total property count|by_wo_status",
    "t0565|Total property count|by_vendor",
    "t0566|Total property count|by_repair_type",
    "t0567|Total property count|by_market_x_month",
    "t0568|Number of technicians|grand_total",
    "t0569|Number of technicians|by_month",
    "t0570|Number of technicians|by_prop_toggle",
    "t0571|Number of technicians|by_market",
    "t0572|Number of technicians|by_scope",
    "t0573|Number of technicians|by_wo_status",
    "t0574|Number of technicians|by_vendor",
    "t0575|Number of technicians|by_repair_type",
    "t0576|Number of technicians|by_market_x_month",
    "t0577|Total Work Order Violation Amt|grand_total",
    "t0578|Total Work Order Violation Amt|by_month",
    "t0579|Total Work Order Violation Amt|by_prop_toggle",
    "t0580|Total Work Order Violation Amt|by_market",
    "t0581|Total Work Order Violation Amt|by_scope",
    "t0582|Total Work Order Violation Amt|by_wo_status",
    "t0583|Total Work Order Violation Amt|by_vendor",
    "t0584|Total Work Order Violation Amt|by_repair_type",
    "t0585|Total Work Order Violation Amt|by_market_x_month",
    "t0586|Average Project Cost by Completed Date|grand_total",
    "t0587|Average Project Cost by Completed Date|by_month",
    "t0588|Average Project Cost by Completed Date|by_prop_toggle",
    "t0589|Average Project Cost by Completed Date|by_market",
    "t0590|Average Project Cost by Completed Date|by_scope",
    "t0591|Average Project Cost by Completed Date|by_wo_status",
    "t0592|Average Project Cost by Completed Date|by_vendor",
    "t0593|Average Project Cost by Completed Date|by_repair_type",
    "t0594|Average Project Cost by Completed Date|by_market_x_month",
    "t0595|Project Count by Completed Date|grand_total",
    "t0596|Project Count by Completed Date|by_month",
    "t0597|Project Count by Completed Date|by_prop_toggle",
    "t0598|Project Count by Completed Date|by_market",
    "t0599|Project Count by Completed Date|by_scope",
    "t0600|Project Count by Completed Date|by_wo_status",
    "t0601|Project Count by Completed Date|by_vendor",
    "t0602|Project Count by Completed Date|by_repair_type",
    "t0603|Project Count by Completed Date|by_market_x_month",
    "t0604|Final Warning Violations|grand_total",
    "t0605|Final Warning Violations|by_month",
    "t0606|Final Warning Violations|by_prop_toggle",
    "t0607|Final Warning Violations|by_market",
    "t0608|Final Warning Violations|by_scope",
    "t0609|Final Warning Violations|by_wo_status",
    "t0610|Final Warning Violations|by_vendor",
    "t0611|Final Warning Violations|by_repair_type",
    "t0612|Final Warning Violations|by_market_x_month",
    "t0613|Project Count by Created Date|grand_total",
    "t0614|Project Count by Created Date|by_month",
    "t0615|Project Count by Created Date|by_prop_toggle",
    "t0616|Project Count by Created Date|by_market",
    "t0617|Project Count by Created Date|by_scope",
    "t0618|Project Count by Created Date|by_wo_status",
    "t0619|Project Count by Created Date|by_vendor",
    "t0620|Project Count by Created Date|by_repair_type",
    "t0621|Project Count by Created Date|by_market_x_month",
    "t0622|Projected Project Count (Actual + ECD)|grand_total",
    "t0623|Projected Project Count (Actual + ECD)|by_month",
    "t0624|Projected Project Count (Actual + ECD)|by_prop_toggle",
    "t0625|Projected Project Count (Actual + ECD)|by_market",
    "t0626|Projected Project Count (Actual + ECD)|by_scope",
    "t0627|Projected Project Count (Actual + ECD)|by_wo_status",
    "t0628|Projected Project Count (Actual + ECD)|by_vendor",
    "t0629|Projected Project Count (Actual + ECD)|by_repair_type",
    "t0630|Projected Project Count (Actual + ECD)|by_market_x_month",
    "t0631|Total HOA Homes|grand_total",
    "t0632|Total HOA Homes|by_month",
    "t0633|Total HOA Homes|by_prop_toggle",
    "t0634|Total HOA Homes|by_market",
    "t0635|Total HOA Homes|by_scope",
    "t0636|Total HOA Homes|by_wo_status",
    "t0637|Total HOA Homes|by_vendor",
    "t0638|Total HOA Homes|by_repair_type",
    "t0639|Total HOA Homes|by_market_x_month",
    "t0640|Average Cost per Homes Serviced Internally|grand_total",
    "t0641|Average Cost per Homes Serviced Internally|by_month",
    "t0642|Average Cost per Homes Serviced Internally|by_prop_toggle",
    "t0643|Average Cost per Homes Serviced Internally|by_market",
    "t0644|Average Cost per Homes Serviced Internally|by_scope",
    "t0645|Average Cost per Homes Serviced Internally|by_wo_status",
    "t0646|Average Cost per Homes Serviced Internally|by_vendor",
    "t0647|Average Cost per Homes Serviced Internally|by_repair_type",
    "t0648|Average Cost per Homes Serviced Internally|by_market_x_month",
    "t0649|Project Cost by Completed Date Excluding In-house Project Team Cost|grand_total",
    "t0650|Project Cost by Completed Date Excluding In-house Project Team Cost|by_month",
    "t0651|Project Cost by Completed Date Excluding In-house Project Team Cost|by_prop_toggle",
    "t0652|Project Cost by Completed Date Excluding In-house Project Team Cost|by_market",
    "t0653|Project Cost by Completed Date Excluding In-house Project Team Cost|by_scope",
    "t0654|Project Cost by Completed Date Excluding In-house Project Team Cost|by_wo_status",
    "t0655|Project Cost by Completed Date Excluding In-house Project Team Cost|by_vendor",
    "t0656|Project Cost by Completed Date Excluding In-house Project Team Cost|by_repair_type",
    "t0657|Project Cost by Completed Date Excluding In-house Project Team Cost|by_market_x_month",
    "t0658|Project Cost by Completed Date|grand_total",
    "t0659|Project Cost by Completed Date|by_month",
    "t0660|Project Cost by Completed Date|by_prop_toggle",
    "t0661|Project Cost by Completed Date|by_market",
    "t0662|Project Cost by Completed Date|by_scope",
    "t0663|Project Cost by Completed Date|by_wo_status",
    "t0664|Project Cost by Completed Date|by_vendor",
    "t0665|Project Cost by Completed Date|by_repair_type",
    "t0666|Project Cost by Completed Date|by_market_x_month",
    "t0667|Delinquent Violations|grand_total",
    "t0668|Delinquent Violations|by_month",
    "t0669|Delinquent Violations|by_prop_toggle",
    "t0670|Delinquent Violations|by_market",
    "t0671|Delinquent Violations|by_scope",
    "t0672|Delinquent Violations|by_wo_status",
    "t0673|Delinquent Violations|by_vendor",
    "t0674|Delinquent Violations|by_repair_type",
    "t0675|Delinquent Violations|by_market_x_month",
    "t0676|Projected Project Cost (Actual + ECD)|grand_total",
    "t0677|Projected Project Cost (Actual + ECD)|by_month",
    "t0678|Projected Project Cost (Actual + ECD)|by_prop_toggle",
    "t0679|Projected Project Cost (Actual + ECD)|by_market",
    "t0680|Projected Project Cost (Actual + ECD)|by_scope",
    "t0681|Projected Project Cost (Actual + ECD)|by_wo_status",
    "t0682|Projected Project Cost (Actual + ECD)|by_vendor",
    "t0683|Projected Project Cost (Actual + ECD)|by_repair_type",
    "t0684|Projected Project Cost (Actual + ECD)|by_market_x_month",
    "t0685|Move In Work Order Costs by Resident Move In Date|grand_total",
    "t0686|Move In Work Order Costs by Resident Move In Date|by_month",
    "t0687|Move In Work Order Costs by Resident Move In Date|by_prop_toggle",
    "t0688|Move In Work Order Costs by Resident Move In Date|by_market",
    "t0689|Move In Work Order Costs by Resident Move In Date|by_scope",
    "t0690|Move In Work Order Costs by Resident Move In Date|by_wo_status",
    "t0691|Move In Work Order Costs by Resident Move In Date|by_vendor",
    "t0692|Move In Work Order Costs by Resident Move In Date|by_repair_type",
    "t0693|Move In Work Order Costs by Resident Move In Date|by_market_x_month",
    "t0694|Project Count by Estimated Completion Date|grand_total",
    "t0695|Project Count by Estimated Completion Date|by_month",
    "t0696|Project Count by Estimated Completion Date|by_prop_toggle",
    "t0697|Project Count by Estimated Completion Date|by_market",
    "t0698|Project Count by Estimated Completion Date|by_scope",
    "t0699|Project Count by Estimated Completion Date|by_wo_status",
    "t0700|Project Count by Estimated Completion Date|by_vendor",
    "t0701|Project Count by Estimated Completion Date|by_repair_type",
    "t0702|Project Count by Estimated Completion Date|by_market_x_month",
    "t0703|Total Project Count|grand_total",
    "t0704|Total Project Count|by_month",
    "t0705|Total Project Count|by_prop_toggle",
    "t0706|Total Project Count|by_market",
    "t0707|Total Project Count|by_scope",
    "t0708|Total Project Count|by_wo_status",
    "t0709|Total Project Count|by_vendor",
    "t0710|Total Project Count|by_repair_type",
    "t0711|Total Project Count|by_market_x_month",
    "t0712|Project Cost by Created Date|grand_total",
    "t0713|Project Cost by Created Date|by_month",
    "t0714|Project Cost by Created Date|by_prop_toggle",
    "t0715|Project Cost by Created Date|by_market",
    "t0716|Project Cost by Created Date|by_scope",
    "t0717|Project Cost by Created Date|by_wo_status",
    "t0718|Project Cost by Created Date|by_vendor",
    "t0719|Project Cost by Created Date|by_repair_type",
    "t0720|Project Cost by Created Date|by_market_x_month",
    "t0721|Project Cost by Estimated Completion Date|grand_total",
    "t0722|Project Cost by Estimated Completion Date|by_month",
    "t0723|Project Cost by Estimated Completion Date|by_prop_toggle",
    "t0724|Project Cost by Estimated Completion Date|by_market",
    "t0725|Project Cost by Estimated Completion Date|by_scope",
    "t0726|Project Cost by Estimated Completion Date|by_wo_status",
    "t0727|Project Cost by Estimated Completion Date|by_vendor",
    "t0728|Project Cost by Estimated Completion Date|by_repair_type",
    "t0729|Project Cost by Estimated Completion Date|by_market_x_month",
    "t0730|Total Project Cost|grand_total",
    "t0731|Total Project Cost|by_month",
    "t0732|Total Project Cost|by_prop_toggle",
    "t0733|Total Project Cost|by_market",
    "t0734|Total Project Cost|by_scope",
    "t0735|Total Project Cost|by_wo_status",
    "t0736|Total Project Cost|by_vendor",
    "t0737|Total Project Cost|by_repair_type",
    "t0738|Total Project Cost|by_market_x_month",
    "t0739|HOA Work Order Count by HOA Completed Date|grand_total",
    "t0740|HOA Work Order Count by HOA Completed Date|by_month",
    "t0741|HOA Work Order Count by HOA Completed Date|by_prop_toggle",
    "t0742|HOA Work Order Count by HOA Completed Date|by_market",
    "t0743|HOA Work Order Count by HOA Completed Date|by_scope",
    "t0744|HOA Work Order Count by HOA Completed Date|by_wo_status",
    "t0745|HOA Work Order Count by HOA Completed Date|by_vendor",
    "t0746|HOA Work Order Count by HOA Completed Date|by_repair_type",
    "t0747|HOA Work Order Count by HOA Completed Date|by_market_x_month",
    "t0748|Casualty Work Order Count by HOA Completed Date|grand_total",
    "t0749|Casualty Work Order Count by HOA Completed Date|by_month",
    "t0750|Casualty Work Order Count by HOA Completed Date|by_prop_toggle",
    "t0751|Casualty Work Order Count by HOA Completed Date|by_market",
    "t0752|Casualty Work Order Count by HOA Completed Date|by_scope",
    "t0753|Casualty Work Order Count by HOA Completed Date|by_wo_status",
    "t0754|Casualty Work Order Count by HOA Completed Date|by_vendor",
    "t0755|Casualty Work Order Count by HOA Completed Date|by_repair_type",
    "t0756|Casualty Work Order Count by HOA Completed Date|by_market_x_month",
    "t0757|% of Homes with Open Violations|grand_total",
    "t0758|% of Homes with Open Violations|by_month",
    "t0759|% of Homes with Open Violations|by_prop_toggle",
    "t0760|% of Homes with Open Violations|by_market",
    "t0761|% of Homes with Open Violations|by_scope",
    "t0762|% of Homes with Open Violations|by_wo_status",
    "t0763|% of Homes with Open Violations|by_vendor",
    "t0764|% of Homes with Open Violations|by_repair_type",
    "t0765|% of Homes with Open Violations|by_market_x_month",
    "t0766|Scope with Project Cost Greater Than Approved|grand_total",
    "t0767|Scope with Project Cost Greater Than Approved|by_month",
    "t0768|Scope with Project Cost Greater Than Approved|by_prop_toggle",
    "t0769|Scope with Project Cost Greater Than Approved|by_market",
    "t0770|Scope with Project Cost Greater Than Approved|by_scope",
    "t0771|Scope with Project Cost Greater Than Approved|by_wo_status",
    "t0772|Scope with Project Cost Greater Than Approved|by_vendor",
    "t0773|Scope with Project Cost Greater Than Approved|by_repair_type",
    "t0774|Scope with Project Cost Greater Than Approved|by_market_x_month",
    "t0775|Homes with Open  Violations|grand_total",
    "t0776|Homes with Open  Violations|by_month",
    "t0777|Homes with Open  Violations|by_prop_toggle",
    "t0778|Homes with Open  Violations|by_market",
    "t0779|Homes with Open  Violations|by_scope",
    "t0780|Homes with Open  Violations|by_wo_status",
    "t0781|Homes with Open  Violations|by_vendor",
    "t0782|Homes with Open  Violations|by_repair_type",
    "t0783|Homes with Open  Violations|by_market_x_month",
    "t0784|Scope with Project Cost Less Than or Equal To Approved|grand_total",
    "t0785|Scope with Project Cost Less Than or Equal To Approved|by_month",
    "t0786|Scope with Project Cost Less Than or Equal To Approved|by_prop_toggle",
    "t0787|Scope with Project Cost Less Than or Equal To Approved|by_market",
    "t0788|Scope with Project Cost Less Than or Equal To Approved|by_scope",
    "t0789|Scope with Project Cost Less Than or Equal To Approved|by_wo_status",
    "t0790|Scope with Project Cost Less Than or Equal To Approved|by_vendor",
    "t0791|Scope with Project Cost Less Than or Equal To Approved|by_repair_type",
    "t0792|Scope with Project Cost Less Than or Equal To Approved|by_market_x_month",
    "t0793|Legal Violations|grand_total",
    "t0794|Legal Violations|by_month",
    "t0795|Legal Violations|by_prop_toggle",
    "t0796|Legal Violations|by_market",
    "t0797|Legal Violations|by_scope",
    "t0798|Legal Violations|by_wo_status",
    "t0799|Legal Violations|by_vendor",
    "t0800|Legal Violations|by_repair_type",
    "t0801|Legal Violations|by_market_x_month",
    "t0802|Active CIP-RR Exceptions Project Count|grand_total",
    "t0803|Active CIP-RR Exceptions Project Count|by_month",
    "t0804|Active CIP-RR Exceptions Project Count|by_prop_toggle",
    "t0805|Active CIP-RR Exceptions Project Count|by_market",
    "t0806|Active CIP-RR Exceptions Project Count|by_scope",
    "t0807|Active CIP-RR Exceptions Project Count|by_wo_status",
    "t0808|Active CIP-RR Exceptions Project Count|by_vendor",
    "t0809|Active CIP-RR Exceptions Project Count|by_repair_type",
    "t0810|Active CIP-RR Exceptions Project Count|by_market_x_month",
    "t0811|Active Pre-Construction Exceptions Project Count|grand_total",
    "t0812|Active Pre-Construction Exceptions Project Count|by_month",
    "t0813|Active Pre-Construction Exceptions Project Count|by_prop_toggle",
    "t0814|Active Pre-Construction Exceptions Project Count|by_market",
    "t0815|Active Pre-Construction Exceptions Project Count|by_scope",
    "t0816|Active Pre-Construction Exceptions Project Count|by_wo_status",
    "t0817|Active Pre-Construction Exceptions Project Count|by_vendor",
    "t0818|Active Pre-Construction Exceptions Project Count|by_repair_type",
    "t0819|Active Pre-Construction Exceptions Project Count|by_market_x_month",
    "t0820|1st & 2nd Warning Violations|grand_total",
    "t0821|1st & 2nd Warning Violations|by_month",
    "t0822|1st & 2nd Warning Violations|by_prop_toggle",
    "t0823|1st & 2nd Warning Violations|by_market",
    "t0824|1st & 2nd Warning Violations|by_scope",
    "t0825|1st & 2nd Warning Violations|by_wo_status",
    "t0826|1st & 2nd Warning Violations|by_vendor",
    "t0827|1st & 2nd Warning Violations|by_repair_type",
    "t0828|1st & 2nd Warning Violations|by_market_x_month",
    "t0829|Project Count from Properties with Multiple Scopes (Exceptions)|grand_total",
    "t0830|Project Count from Properties with Multiple Scopes (Exceptions)|by_month",
    "t0831|Project Count from Properties with Multiple Scopes (Exceptions)|by_prop_toggle",
    "t0832|Project Count from Properties with Multiple Scopes (Exceptions)|by_market",
    "t0833|Project Count from Properties with Multiple Scopes (Exceptions)|by_scope",
    "t0834|Project Count from Properties with Multiple Scopes (Exceptions)|by_wo_status",
    "t0835|Project Count from Properties with Multiple Scopes (Exceptions)|by_vendor",
    "t0836|Project Count from Properties with Multiple Scopes (Exceptions)|by_repair_type",
    "t0837|Project Count from Properties with Multiple Scopes (Exceptions)|by_market_x_month",
    "t0838|Canceled Violations|grand_total",
    "t0839|Canceled Violations|by_month",
    "t0840|Canceled Violations|by_prop_toggle",
    "t0841|Canceled Violations|by_market",
    "t0842|Canceled Violations|by_scope",
    "t0843|Canceled Violations|by_wo_status",
    "t0844|Canceled Violations|by_vendor",
    "t0845|Canceled Violations|by_repair_type",
    "t0846|Canceled Violations|by_market_x_month",
    "t0847|Open  Violations|grand_total",
    "t0848|Open  Violations|by_month",
    "t0849|Open  Violations|by_prop_toggle",
    "t0850|Open  Violations|by_market",
    "t0851|Open  Violations|by_scope",
    "t0852|Open  Violations|by_wo_status",
    "t0853|Open  Violations|by_vendor",
    "t0854|Open  Violations|by_repair_type",
    "t0855|Open  Violations|by_market_x_month",
    "t0856|Avg Daily Open Properties Count|grand_total",
    "t0857|Avg Daily Open Properties Count|by_month",
    "t0858|Avg Daily Open Properties Count|by_prop_toggle",
    "t0859|Avg Daily Open Properties Count|by_market",
    "t0860|Avg Daily Open Properties Count|by_scope",
    "t0861|Avg Daily Open Properties Count|by_wo_status",
    "t0862|Avg Daily Open Properties Count|by_vendor",
    "t0863|Avg Daily Open Properties Count|by_repair_type",
    "t0864|Avg Daily Open Properties Count|by_market_x_month",
    "t0865|Avg Daily Open Properties % (Current MTD)|grand_total",
    "t0866|Avg Daily Open Properties % (Current MTD)|by_month",
    "t0867|Avg Daily Open Properties % (Current MTD)|by_prop_toggle",
    "t0868|Avg Daily Open Properties % (Current MTD)|by_market",
    "t0869|Avg Daily Open Properties % (Current MTD)|by_scope",
    "t0870|Avg Daily Open Properties % (Current MTD)|by_wo_status",
    "t0871|Avg Daily Open Properties % (Current MTD)|by_vendor",
    "t0872|Avg Daily Open Properties % (Current MTD)|by_repair_type",
    "t0873|Avg Daily Open Properties % (Current MTD)|by_market_x_month",
    "t0874|Avg Daily Open Properties %|grand_total",
    "t0875|Avg Daily Open Properties %|by_month",
    "t0876|Avg Daily Open Properties %|by_prop_toggle",
    "t0877|Avg Daily Open Properties %|by_market",
    "t0878|Avg Daily Open Properties %|by_scope",
    "t0879|Avg Daily Open Properties %|by_wo_status",
    "t0880|Avg Daily Open Properties %|by_vendor",
    "t0881|Avg Daily Open Properties %|by_repair_type",
    "t0882|Avg Daily Open Properties %|by_market_x_month",
    "t0883|Work Orders Completed Trailing 30 Days|grand_total",
    "t0884|Work Orders Completed Trailing 30 Days|by_month",
    "t0885|Work Orders Completed Trailing 30 Days|by_prop_toggle",
    "t0886|Work Orders Completed Trailing 30 Days|by_market",
    "t0887|Work Orders Completed Trailing 30 Days|by_scope",
    "t0888|Work Orders Completed Trailing 30 Days|by_wo_status",
    "t0889|Work Orders Completed Trailing 30 Days|by_vendor",
    "t0890|Work Orders Completed Trailing 30 Days|by_repair_type",
    "t0891|Work Orders Completed Trailing 30 Days|by_market_x_month",
    "t0892|Project Avg Days Aging to ECD|grand_total",
    "t0893|Project Avg Days Aging to ECD|by_month",
    "t0894|Project Avg Days Aging to ECD|by_prop_toggle",
    "t0895|Project Avg Days Aging to ECD|by_market",
    "t0896|Project Avg Days Aging to ECD|by_scope",
    "t0897|Project Avg Days Aging to ECD|by_wo_status",
    "t0898|Project Avg Days Aging to ECD|by_vendor",
    "t0899|Project Avg Days Aging to ECD|by_repair_type",
    "t0900|Project Avg Days Aging to ECD|by_market_x_month",
    "t0901|Project Avg Days Aging to RR|grand_total",
    "t0902|Project Avg Days Aging to RR|by_month",
    "t0903|Project Avg Days Aging to RR|by_prop_toggle",
    "t0904|Project Avg Days Aging to RR|by_market",
    "t0905|Project Avg Days Aging to RR|by_scope",
    "t0906|Project Avg Days Aging to RR|by_wo_status",
    "t0907|Project Avg Days Aging to RR|by_vendor",
    "t0908|Project Avg Days Aging to RR|by_repair_type",
    "t0909|Project Avg Days Aging to RR|by_market_x_month",
    "t0910|Project Avg Days Aging to Today|grand_total",
    "t0911|Project Avg Days Aging to Today|by_month",
    "t0912|Project Avg Days Aging to Today|by_prop_toggle",
    "t0913|Project Avg Days Aging to Today|by_market",
    "t0914|Project Avg Days Aging to Today|by_scope",
    "t0915|Project Avg Days Aging to Today|by_wo_status",
    "t0916|Project Avg Days Aging to Today|by_vendor",
    "t0917|Project Avg Days Aging to Today|by_repair_type",
    "t0918|Project Avg Days Aging to Today|by_market_x_month",
    "t0919|Project Average Days to Complete|grand_total",
    "t0920|Project Average Days to Complete|by_month",
    "t0921|Project Average Days to Complete|by_prop_toggle",
    "t0922|Project Average Days to Complete|by_market",
    "t0923|Project Average Days to Complete|by_scope",
    "t0924|Project Average Days to Complete|by_wo_status",
    "t0925|Project Average Days to Complete|by_vendor",
    "t0926|Project Average Days to Complete|by_repair_type",
    "t0927|Project Average Days to Complete|by_market_x_month",
    "t0928|Average Work Order Days To Complete by Created Date|grand_total",
    "t0929|Average Work Order Days To Complete by Created Date|by_month",
    "t0930|Average Work Order Days To Complete by Created Date|by_prop_toggle",
    "t0931|Average Work Order Days To Complete by Created Date|by_market",
    "t0932|Average Work Order Days To Complete by Created Date|by_scope",
    "t0933|Average Work Order Days To Complete by Created Date|by_wo_status",
    "t0934|Average Work Order Days To Complete by Created Date|by_vendor",
    "t0935|Average Work Order Days To Complete by Created Date|by_repair_type",
    "t0936|Average Work Order Days To Complete by Created Date|by_market_x_month",
    "t0937|Average Tenure of Turn|grand_total",
    "t0938|Average Tenure of Turn|by_month",
    "t0939|Average Tenure of Turn|by_prop_toggle",
    "t0940|Average Tenure of Turn|by_market",
    "t0941|Average Tenure of Turn|by_scope",
    "t0942|Average Tenure of Turn|by_wo_status",
    "t0943|Average Tenure of Turn|by_vendor",
    "t0944|Average Tenure of Turn|by_repair_type",
    "t0945|Average Tenure of Turn|by_market_x_month",
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
        // NOTE: original template wrapped the measure in IGNORE(), but IGNORE() is a
        // SUMMARIZECOLUMNS-only modifier and is invalid inside ROW(). Dropping it —
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
// after a force-kill. Consumed ad-hoc by scripts/find-hung-query.py when the
// user wants to identify which test was in flight during a killed run.
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
