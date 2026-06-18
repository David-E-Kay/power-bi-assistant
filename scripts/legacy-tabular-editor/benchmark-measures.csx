// =============================================================================
// benchmark-measures.csx  (v5 — threaded cancel + memory debounce + smoke test)
// =============================================================================
// PURPOSE:  Profile query timing for a list of measures across multiple
//           dimension slices and an optional cross-product context.
//           Auto-generates the full test matrix from simple lists — no manual
//           test case enumeration needed.
//
// USE CASE: Identify the slowest measures in a model to prioritize DAX
//           optimization work. Complements the regression testing framework
//           (capture-snapshot.csx) which compares pre/post values.
//           This script captures timing only — no result values.
//
// OUTPUTS:  {label}-timing.csv     — per-test-case timing (main deliverable)
//           {label}-config.csv     — filter context reference for validation
//           {label}-testplan.json  — planned test order (written pre-flight)
//           {label}-errors.log     — error details (only if errors occur)
//           {label}-timeouts.log   — timeouts + smoke-test failures, with
//                                     Type/Reason tags (written if timeouts
//                                     OR smoke failures occur)
//
// CSV COLUMNS:
//   test_id, measure, context, status, row_count, duration_ms, distinct_values
//
//   distinct_values = count of unique values in the Result column.
//   False-fast detection: if distinct_values == 1 AND row_count > 1,
//   the dimension isn't filtering — the measure returned the same value
//   for every row (likely no relationship path to that dimension).
//
//   status values: "ok", "error", "timeout", "skipped", "aborted_memory"
//     "timeout"        → wall-clock OR memory-watchdog cancel (duration_ms ≈ queryTimeoutMs
//                         or ~1.5s if memory-cancelled). The exact cause is in {label}-timeouts.log
//                         under the Type: tag (memory_watchdog | query_timeout).
//     "skipped"        → measure failed pre-flight smoke test; all permutations get this status
//                         and duration_ms = 0.
//     "aborted_memory" → between-test memory check tripped; aborts the rest of the run.
//
// USAGE:    GUI (Tabular Editor 3):
//           1. Connect TE3 to the target model
//           2. Populate the measures, dimensions, and cross-product lists below
//           3. Set benchmarkLabel and diagnosticMode
//           4. Run script — output saved to Desktop\PBI-Benchmark\
//
//           CLI (Claude Code / automation):
//           Environment variables override the hardcoded defaults below.
//             BENCHMARK_LABEL       → benchmarkLabel
//             DIAGNOSTIC_MODE       → diagnosticMode  ("true" / "false")
//             OUTPUT_DIR            → outputDir
//             QUERY_TIMEOUT_MS      → queryTimeoutMs       (default: 60000)
//             SMOKE_TEST_TIMEOUT_MS → smokeTestTimeoutMs   (default: 10000)
//             MEMORY_THRESHOLD_PCT  → memoryThresholdPct   (default: 80, percent)
//             USE_DIRECT_ADOMD      → useDirectAdomd       ("true" / "false")
//             SKIP_ON_SMOKE_FAILURE → skipOnSmokeTestFailure ("true" / "false")
//           Example:
//             set BENCHMARK_LABEL=baseline
//             TabularEditor.exe model.bim -S benchmark-measures.csx
//
// ROLLBACK: Read-only — no model changes made
//
// CHANGELOG:
//   v5 — Mid-query memory watchdog (3-poll debounce, 1.5s sustained pressure)
//         using a thread-pool task + cmd.Cancel(); matches the v8 regression
//         script. Pre-flight smoke test (default ON) skips broken measures
//         before the main run so a single runaway can't take down the whole
//         benchmark. Failed measures emit one "skipped" record per permutation
//         and one Type:smoketest_* entry in the timeouts log. timeouts.log
//         entries now carry Type: (memory_watchdog | query_timeout |
//         smoketest_timeout | smoketest_error | query_error) and Reason: lines.
//         {label}-testplan.json is written pre-flight so a force-killed run
//         can still be inspected.
//   v4 — Per-query timeout via AdomdCommand.CommandTimeout (server-side
//         cancellation). Memory watchdog aborts run when TE3 + msmdsrv
//         combined RAM exceeds threshold. Dedicated {label}-timeouts.log
//         for manual investigation. New status values: "timeout",
//         "aborted_memory".
//   v3 — Global filters as TREATAS filter arguments to SUMMARIZECOLUMNS
//         (mirrors real PBI visual queries). Grand total uses ROW/
//         CALCULATETABLE instead of SUMMARIZECOLUMNS (fixes 0-row bug
//         with filter args + no grouping columns). IGNORE removed from
//         measure references (fixes blank-row inflation). distinct_values
//         column uses last-column index instead of name match. False-fast
//         warnings in summary report.
//   v2 — Added distinct_values column.
//   v1 — Initial release.
// =============================================================================


// ═══════════════════════════════════════════════════════════════════════════════
// CONFIGURATION — edit these sections for each benchmark run
// ═══════════════════════════════════════════════════════════════════════════════

var benchmarkLabel = System.Environment.GetEnvironmentVariable("BENCHMARK_LABEL")
    ?? "benchmark";

var _envDiag = System.Environment.GetEnvironmentVariable("DIAGNOSTIC_MODE");
var diagnosticMode = _envDiag != null
    ? _envDiag.Equals("true", StringComparison.OrdinalIgnoreCase)
    : false;   // ← flip to true for GUI debugging

// ── Measures ────────────────────────────────────────────────────────────────
// Paste your measure names here, one per line. These must match the measure
// names in the model exactly (case-sensitive).

var measures = new List<string>
{
    // "[Measure A]",
    // "[Measure B]",
    // "[Measure C] (Base)",
    // "[Measure D]",
};


// ── Single-Slice Dimensions ────────────────────────────────────────────────
// Each dimension generates an independent query per measure.
// All distinct values are returned (subject to maxRowsPerContext).
// Format: label → DAX column reference

var singleSliceDimensions = new Dictionary<string, string>
{
    // { "by_col_a", "'Table A'[Column A]" },
    // { "by_toggle", "'Table B'[Column B]" },
    // { "by_month", "'Date'[Month]" },
    // { "by_col_c", "'Table C'[Column C]" },
    // { "by_col_d", "'Table D'[Column D]" },
};


// ── Cross-Product Context ──────────────────────────────────────────────────
// All columns listed here go into a SINGLE SUMMARIZECOLUMNS together,
// producing one combined result set per measure. Leave empty to skip.
//
// This mirrors a report matrix visual: e.g., Column A on rows, Month on
// columns, with slicers for Column C and Column D.
//
// Use crossProductValueFilters (below) to constrain specific columns to
// selected values — just like a report slicer.

var crossProductColumns = new List<string>
{
    // "'Table A'[Column A]",
    // "'Table B'[Column B]",
    // "'Date'[Month]",
    // "'Table C'[Column C]",
    // "'Table D'[Column D]",
};


// ── Cross-Product Value Filters (Slicer Simulation) ────────────────────────
// Constrain specific cross-product columns to selected values using TREATAS,
// exactly as a Power BI slicer works. The column stays in the grouping (so
// it appears in output) while TREATAS restricts it to the listed values.
//
// Any cross-product column NOT listed here returns all distinct values.
// Date/numeric values: use the DAX literal format the column expects.
//
// Examples:
//   { "'Table C'[Column C]", new List<string> { "Value 1", "Value 2" } }
//   { "'Date'[Start of Year]", new List<string> { "DATE(2025,1,1)" } }

var crossProductValueFilters = new Dictionary<string, List<string>>
{
    // { "'Table C'[Column C]", new List<string> { "Value 1" } },
    // { "'Table D'[Column D]", new List<string> { "Value 2", "Value 3" } },
};


// ── Global Filters ─────────────────────────────────────────────────────────
// Applied to EVERY query as TREATAS filter arguments to SUMMARIZECOLUMNS
// (or CALCULATETABLE for grand_total). This mirrors report-level slicer
// selections — the filter restricts the iteration space, not just the
// measure evaluation.
//
// Format: column reference → list of allowed values.
// String values are auto-quoted. DAX expressions (DATE(...), numbers)
// are passed through raw.
//
// Examples:
//   { "'Date'[Year]", new List<string> { "2026" } }
//   { "'Date'[Start of Year]", new List<string> { "DATE(2025,1,1)" } }
//   { "'Table A'[Column A]", new List<string> { "Value 1" } }

var globalFilters = new Dictionary<string, List<string>>
{
    // { "'Date'[Year]", new List<string> { "2026" } },
};


// ── Row Cap ────────────────────────────────────────────────────────────────
// Maximum rows returned per query. Wraps SUMMARIZECOLUMNS in TOPN.
// Set to 0 for no limit (full evaluation).
//
// For benchmarking, you typically want 0 (full eval) to measure real query
// cost, or a moderate cap (50-100) if high-cardinality explosions cause
// timeouts. For single-slice dimensions this caps overall rows. For the
// cross-product it caps the total cross-product result.

var maxRowsPerContext = 0;


// ── Output Directory ───────────────────────────────────────────────────────
var outputDir = System.Environment.GetEnvironmentVariable("OUTPUT_DIR")
    ?? System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop),
        "PBI-Benchmark"
    );

if (!System.IO.Directory.Exists(outputDir))
    System.IO.Directory.CreateDirectory(outputDir);

var timingCsvPath = System.IO.Path.Combine(outputDir, benchmarkLabel + "-timing.csv");
var errorLogPath = System.IO.Path.Combine(outputDir, benchmarkLabel + "-errors.log");
var timeoutLogPath = System.IO.Path.Combine(outputDir, benchmarkLabel + "-timeouts.log");

// ── Safety Limits ─────────────────────────────────────────────────────────
// queryTimeoutMs: per-query hard cap enforced server-side via AdomdCommand.
//   60 seconds is generous for well-formed DAX (typical slow queries: 5–8s).
//   Set to 0 to disable (not recommended for large benchmarks).
//   Note: benchmarks do NOT skip measures that timeout — the timeout IS the
//   timing data. Triage via {label}-timeouts.log.
var _envTimeout = System.Environment.GetEnvironmentVariable("QUERY_TIMEOUT_MS");
var queryTimeoutMs = _envTimeout != null ? int.Parse(_envTimeout) : 60000;

// memoryThresholdPct: watchdog aborts the run if (TE3 + msmdsrv) combined
//   WorkingSet64 exceeds this % of total physical RAM. Set to 0 to disable.
//   NOTE: Uses a hard-coded 16 GB denominator. On 32 GB use ~40%, on 64 GB use ~20%.
//   Meaningful only in local PBIP workspace mode — in XMLA mode the model
//   memory lives on the Fabric capacity and is invisible to this check.
var _envMemPct = System.Environment.GetEnvironmentVariable("MEMORY_THRESHOLD_PCT");
var memoryThresholdPct = _envMemPct != null ? double.Parse(_envMemPct) : 80.0;

// useDirectAdomd: when true, runs queries on a thread-pool task so the script
//   thread can poll for wall-clock + memory pressure and call cmd.Cancel() —
//   the ONLY mechanism that reliably interrupts a SE-bound Tabular query.
//   AdomdCommand.CommandTimeout is set as a backstop only.
//   Set to false to fall back to EvaluateDax() (no timeout, no cancellability).
var _envDirectAdomd = System.Environment.GetEnvironmentVariable("USE_DIRECT_ADOMD");
var useDirectAdomd = _envDirectAdomd != null
    ? _envDirectAdomd.Equals("true", StringComparison.OrdinalIgnoreCase)
    : true;

// smokeTestTimeoutMs: pre-flight check cap per unique measure. Smoke test runs
//   EVALUATE ROW("r", [Measure]) — grand-total only. Broken measures (syntax
//   errors, missing columns, hung grand-totals) are skipped in the main run
//   so a single runaway cannot take down the whole benchmark sweep.
var _envSmokeTimeout = System.Environment.GetEnvironmentVariable("SMOKE_TEST_TIMEOUT_MS");
var smokeTestTimeoutMs = _envSmokeTimeout != null ? int.Parse(_envSmokeTimeout) : 10000;

// skipOnSmokeTestFailure: when true (default), measures failing the smoke test
//   are skipped in the main run with status:"skipped". When false, they still
//   attempt to run (caught by queryTimeoutMs / memory watchdog).
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


// ═══════════════════════════════════════════════════════════════════════════════
// VALIDATION
// ═══════════════════════════════════════════════════════════════════════════════

if (measures.Count == 0)
{
    Info("ERROR: No measures defined. Add measure names to the 'measures' list and re-run.");
    return;
}

if (singleSliceDimensions.Count == 0 && crossProductColumns.Count == 0)
{
    Info("WARNING: No dimensions defined. Only grand_total context will be tested.\n"
       + "Add entries to singleSliceDimensions and/or crossProductColumns for richer profiling.");
}

// Validate cross-product value filter columns are actually in the cross-product list
foreach (var kvp in crossProductValueFilters)
{
    if (!crossProductColumns.Contains(kvp.Key))
    {
        Info("ERROR: crossProductValueFilters references column '" + kvp.Key
           + "' which is not in crossProductColumns. Fix the configuration and re-run.");
        return;
    }
}


// ═══════════════════════════════════════════════════════════════════════════════
// HELPERS
// ═══════════════════════════════════════════════════════════════════════════════

Func<string, string> JsonEscape = (string s) =>
{
    if (s == null) return "";
    return s.Replace("\\", "\\\\").Replace("\"", "\\\"")
            .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
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
//   and NO cancellability (pre-v5 behavior). A hung query freezes TE3 and
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
                    // constraint, then throws "Failed to enable constraints" whenever a
                    // result row has NULL/blank in the key column. Manual iteration
                    // skips the schema's IsKey/AllowDBNull metadata and collects every
                    // row as-is.
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
        const long assumedTotalRamBytes = 16L * 1024 * 1024 * 1024;
        return (used / (double)assumedTotalRamBytes) * 100.0 >= thresholdPct;
    }
    catch
    {
        return false;
    }
};


// ═══════════════════════════════════════════════════════════════════════════════
// BUILD TREATAS FILTER ARGUMENTS
// ═══════════════════════════════════════════════════════════════════════════════
// Shared helper: converts a Dictionary<column, List<values>> into TREATAS
// filter argument strings for SUMMARIZECOLUMNS / CALCULATETABLE.
// Each entry becomes: TREATAS({"val1", "val2"}, 'Table'[Column])
// String values are auto-quoted. DAX expressions (DATE(...), numbers) are
// passed through raw.

Func<Dictionary<string, List<string>>, List<string>> BuildTreatasArgs =
    (Dictionary<string, List<string>> filterDict) =>
{
    var filters = new List<string>();
    foreach (var kvp in filterDict)
    {
        var col = kvp.Key;
        var values = kvp.Value;

        var formattedValues = new List<string>();
        foreach (var v in values)
        {
            var trimmed = v.Trim();
            // If it looks like a DAX expression (starts with letter+paren, or is numeric),
            // pass it through raw. Otherwise, wrap in quotes as a string literal.
            if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^[A-Za-z]+\(")
                || double.TryParse(trimmed, out _))
            {
                formattedValues.Add(trimmed);
            }
            else
            {
                formattedValues.Add("\"" + trimmed + "\"");
            }
        }

        var valueList = string.Join(", ", formattedValues);
        filters.Add("TREATAS({" + valueList + "}, " + col + ")");
    }
    return filters;
};

// Pre-build global filter fragment (same for every query)
var globalFilterArgs = BuildTreatasArgs(globalFilters);
var globalFilterFragment = globalFilterArgs.Count > 0
    ? ", " + string.Join(", ", globalFilterArgs)
    : "";


// ═══════════════════════════════════════════════════════════════════════════════
// TEST MATRIX GENERATION — auto cross-product of measures × contexts
// ═══════════════════════════════════════════════════════════════════════════════
// Contexts generated per measure:
//   1. grand_total                        (always — uses ROW/CALCULATETABLE)
//   2. One per singleSliceDimension       (if any defined)
//   3. One cross-product context          (if crossProductColumns non-empty)
//
// Global filters are applied as TREATAS filter arguments to every query,
// restricting the iteration space — matching how Power BI passes
// report-level slicer selections.
//
// Grand total uses ROW (not SUMMARIZECOLUMNS) because SUMMARIZECOLUMNS
// with filter arguments but no grouping columns returns 0 rows.
// CALCULATETABLE wraps ROW to apply global filters.

var testCases = new List<Tuple<string, string, string, string>>();
// Fields: testId, measureName, contextLabel, daxQuery

int testNum = 0;

foreach (var measureName in measures)
{
    var measureRef = "[" + measureName + "]";

    // ── Grand Total ──
    testNum++;
    var testId = "b" + testNum.ToString("D4");
    string dax;
    if (globalFilterArgs.Count > 0)
    {
        dax = "CALCULATETABLE(ROW(\"Result\", " + measureRef + "), "
            + string.Join(", ", globalFilterArgs) + ")";
    }
    else
    {
        dax = "ROW(\"Result\", " + measureRef + ")";
    }
    testCases.Add(Tuple.Create(testId, measureName, "grand_total", dax));

    // ── Single-Slice Dimensions ──
    foreach (var dim in singleSliceDimensions)
    {
        testNum++;
        testId = "b" + testNum.ToString("D4");
        var contextLabel = dim.Key;
        var groupCol = dim.Value;

        var innerDax = "SUMMARIZECOLUMNS(" + groupCol
                     + globalFilterFragment
                     + ", \"Result\", " + measureRef + ")";

        if (maxRowsPerContext > 0)
        {
            dax = "TOPN(" + maxRowsPerContext + ", " + innerDax + ")";
        }
        else
        {
            dax = innerDax;
        }

        testCases.Add(Tuple.Create(testId, measureName, contextLabel, dax));
    }

    // ── Cross-Product Context ──
    if (crossProductColumns.Count > 0)
    {
        testNum++;
        testId = "b" + testNum.ToString("D4");
        var contextLabel = string.Join("_x_", crossProductColumns.Select(c => {
            var start = c.IndexOf('[') + 1;
            var end = c.IndexOf(']');
            return (start > 0 && end > start)
                ? c.Substring(start, end - start).Replace(" ", "_")
                : c.Replace(" ", "_");
        }));

        var allCols = string.Join(", ", crossProductColumns);
        var crossFilterArgs = BuildTreatasArgs(crossProductValueFilters);
        var crossFilterFragment = crossFilterArgs.Count > 0
            ? ", " + string.Join(", ", crossFilterArgs)
            : "";

        var innerDax = "SUMMARIZECOLUMNS(" + allCols
                     + globalFilterFragment
                     + crossFilterFragment
                     + ", \"Result\", " + measureRef + ")";

        if (maxRowsPerContext > 0)
        {
            dax = "TOPN(" + maxRowsPerContext + ", " + innerDax + ")";
        }
        else
        {
            dax = innerDax;
        }

        testCases.Add(Tuple.Create(testId, measureName, contextLabel, dax));
    }
}


// ═══════════════════════════════════════════════════════════════════════════════
// PRE-FLIGHT SMOKE TEST — validates each unique measure before the main run
// ═══════════════════════════════════════════════════════════════════════════════
// Runs EVALUATE ROW("r", [Measure]) per unique measure with a tight timeout.
// Broken measures (syntax errors, missing columns, hung grand-totals) are
// added to skippedMeasures and skipped in the main run with status:"skipped".
//
// Caveat: a measure that passes the grand-total smoke test can still hang in
// a cross-product context. queryTimeoutMs / memory watchdog catch those.
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
    int smokeIdx = 0;
    foreach (var mName in measures.Distinct())
    {
        smokeIdx++;
        // IGNORE() is a SUMMARIZECOLUMNS-only modifier and is invalid inside ROW().
        // ROW() always emits one row, so IGNORE adds no semantic value here.
        var smokeDax = "EVALUATE ROW(\"r\", [" + mName + "])";
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
// JSON file BEFORE the main loop starts. Never truncated; safe to parse even
// after a force-kill. Useful for identifying which test was in flight during
// a killed run.
// ═══════════════════════════════════════════════════════════════════════════════

var testplanPath = System.IO.Path.Combine(outputDir, benchmarkLabel + "-testplan.json");
using (var planWriter = new System.IO.StreamWriter(testplanPath, false, System.Text.Encoding.UTF8))
{
    planWriter.WriteLine("{");
    planWriter.WriteLine("  \"label\": \"" + benchmarkLabel + "\",");
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
// EXECUTION ENGINE — timing-only, no result values captured
// ═══════════════════════════════════════════════════════════════════════════════

var sw = System.Diagnostics.Stopwatch.StartNew();
var errorLog = new System.Text.StringBuilder();
// timeoutLog is declared earlier (above the smoke loop) so smoke-test failures
// can be appended before main execution begins.

var timingRows = new List<Tuple<string, string, string, string, int, long, int>>();
// Fields: testId, measureName, context, status, rowCount, durationMs, distinctValues

int total = diagnosticMode ? Math.Min(8, testCases.Count) : testCases.Count;
int okCount = 0;
int errCount = 0;
int timeoutCount = 0;
int skipCount = 0;
int abortedMemoryCount = 0;
bool memoryAborted = false;

for (int i = 0; i < total; i++)
{
    var tc = testCases[i];
    var testId = tc.Item1;
    var measureName = tc.Item2;
    var context = tc.Item3;
    var daxQuery = tc.Item4;

    // ── Memory watchdog — check before starting next test ──────────────────
    if (!memoryAborted && IsMemoryCritical(memoryThresholdPct))
    {
        memoryAborted = true;

        // Write aborted_memory rows for this and all remaining tests
        for (int j = i; j < total; j++)
        {
            var atc = testCases[j];
            abortedMemoryCount++;
            timingRows.Add(Tuple.Create(atc.Item1, atc.Item2, atc.Item3, "aborted_memory", 0, 0L, 0));
        }
        break;
    }

    // ── Skip measures that failed smoke test ────────────────────────────────
    if (skippedMeasures.Contains(measureName))
    {
        skipCount++;
        timingRows.Add(Tuple.Create(testId, measureName, context, "skipped", 0, 0L, 0));
        if (diagnosticMode)
            Info($"[DIAG] {testId} SKIPPED — {measureName} [{context}] (smoke: {smokeResults[measureName]})");
        continue;
    }

    var tcSw = System.Diagnostics.Stopwatch.StartNew();
    var result = ExecuteDaxWithTimeout("EVALUATE " + daxQuery, queryTimeoutMs);
    tcSw.Stop();

    var resultStatus = result.Item2;
    var resultDt = result.Item1;
    var resultError = result.Item3;

    if (resultStatus == "ok")
    {
        okCount++;

        var rowCount = resultDt != null ? resultDt.Rows.Count : 0;
        int distinctValues = 0;
        if (resultDt != null && resultDt.Rows.Count > 0 && resultDt.Columns.Count > 0)
        {
            // Result is always the last column in the output
            var resultCol = resultDt.Columns[resultDt.Columns.Count - 1];
            var seen = new System.Collections.Generic.HashSet<string>();
            foreach (System.Data.DataRow row in resultDt.Rows)
                seen.Add(row[resultCol]?.ToString() ?? "__NULL__");
            distinctValues = seen.Count;
        }
        if (resultDt != null) resultDt.Dispose();

        timingRows.Add(Tuple.Create(testId, measureName, context, "ok", rowCount, tcSw.ElapsedMilliseconds, distinctValues));

        if (diagnosticMode)
            Info($"[DIAG] {testId} OK — {measureName} [{context}] — {rowCount} rows, {distinctValues} distinct, {tcSw.ElapsedMilliseconds}ms\nDAX: {daxQuery}");
    }
    else if (resultStatus == "timeout")
    {
        timeoutCount++;

        string timeoutType;
        var errMsg = resultError ?? "";
        if (errMsg.IndexOf("memory threshold", StringComparison.OrdinalIgnoreCase) >= 0)
            timeoutType = "memory_watchdog";
        else if (errMsg.IndexOf("wall-clock timeout", StringComparison.OrdinalIgnoreCase) >= 0)
            timeoutType = "query_timeout";
        else
            timeoutType = "query_error"; // defensive fallback — shouldn't normally hit

        errorLog.AppendLine($"{testId} | {measureName} | {context}");
        errorLog.AppendLine($"  TIMEOUT ({timeoutType}) after {tcSw.ElapsedMilliseconds}ms (limit: {queryTimeoutMs}ms)");
        errorLog.AppendLine($"  DAX: {daxQuery}");
        errorLog.AppendLine();

        timeoutLog.AppendLine($"{testId} | {measureName} | {context} | {tcSw.ElapsedMilliseconds}ms");
        timeoutLog.AppendLine($"  Type: {timeoutType}");
        timeoutLog.AppendLine($"  Reason: {errMsg}");
        timeoutLog.AppendLine($"  DAX: {daxQuery}");
        timeoutLog.AppendLine();

        timingRows.Add(Tuple.Create(testId, measureName, context, "timeout", 0, tcSw.ElapsedMilliseconds, 0));

        if (diagnosticMode)
            Info($"[DIAG] {testId} TIMEOUT ({timeoutType}) — {measureName} [{context}] — {tcSw.ElapsedMilliseconds}ms\nDAX: {daxQuery}");
    }
    else // "error"
    {
        errCount++;

        var fullError = resultError ?? "unknown error";

        errorLog.AppendLine($"{testId} | {measureName} | {context}");
        errorLog.AppendLine($"  DAX: {daxQuery}");
        errorLog.AppendLine($"  Error: {fullError}");
        errorLog.AppendLine();

        timingRows.Add(Tuple.Create(testId, measureName, context, "error", 0, tcSw.ElapsedMilliseconds, 0));

        if (diagnosticMode)
            Info($"[DIAG] {testId} ERROR — {measureName} [{context}]\nDAX: {daxQuery}\nError: {fullError}");
    }
}

sw.Stop();


// ═══════════════════════════════════════════════════════════════════════════════
// TIMING CSV — the primary output
// ═══════════════════════════════════════════════════════════════════════════════

using (var csvWriter = new System.IO.StreamWriter(timingCsvPath, false, System.Text.Encoding.UTF8))
{
    csvWriter.WriteLine("test_id,measure,context,status,row_count,duration_ms,distinct_values");
    foreach (var t in timingRows)
    {
        csvWriter.WriteLine(
            "\"" + JsonEscape(t.Item1) + "\","
            + "\"" + JsonEscape(t.Item2) + "\","
            + "\"" + JsonEscape(t.Item3) + "\","
            + t.Item4 + ","
            + t.Item5 + ","
            + t.Item6 + ","
            + t.Item7
        );
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// CONFIG CSV — filter context reference for manual validation
// ═══════════════════════════════════════════════════════════════════════════════

var configCsvPath = System.IO.Path.Combine(outputDir, benchmarkLabel + "-config.csv");

using (var cfgWriter = new System.IO.StreamWriter(configCsvPath, false, System.Text.Encoding.UTF8))
{
    cfgWriter.WriteLine("type,label,column,values");

    foreach (var kvp in globalFilters)
        cfgWriter.WriteLine("global_filter,,"
            + "\"" + JsonEscape(kvp.Key) + "\","
            + "\"" + JsonEscape(string.Join("; ", kvp.Value)) + "\"");

    foreach (var kvp in singleSliceDimensions)
        cfgWriter.WriteLine("single_slice,"
            + "\"" + JsonEscape(kvp.Key) + "\","
            + "\"" + JsonEscape(kvp.Value) + "\",");

    foreach (var col in crossProductColumns)
    {
        var valFilter = crossProductValueFilters.ContainsKey(col)
            ? string.Join("; ", crossProductValueFilters[col])
            : "(all)";
        cfgWriter.WriteLine("cross_product,,"
            + "\"" + JsonEscape(col) + "\","
            + "\"" + JsonEscape(valFilter) + "\"");
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// ERROR LOG
// ═══════════════════════════════════════════════════════════════════════════════

if (errCount > 0)
    System.IO.File.WriteAllText(errorLogPath, errorLog.ToString());

if (timeoutCount > 0 || skippedMeasures.Count > 0)
    System.IO.File.WriteAllText(timeoutLogPath, timeoutLog.ToString());


// ═══════════════════════════════════════════════════════════════════════════════
// SUMMARY REPORT
// ═══════════════════════════════════════════════════════════════════════════════

var report = new System.Text.StringBuilder();
report.AppendLine("════════════════════════════════════════════════════════════");
report.AppendLine("  Measure Benchmark Complete");
report.AppendLine("════════════════════════════════════════════════════════════");
report.AppendLine($"  Label:      {benchmarkLabel}");
report.AppendLine($"  Measures:   {measures.Count}");
report.AppendLine($"  Contexts:   1 grand_total + {singleSliceDimensions.Count} single-slice"
    + (crossProductColumns.Count > 0 ? " + 1 cross-product" : ""));
report.AppendLine($"  Test cases: {total}");
report.AppendLine($"  OK:         {okCount}");
report.AppendLine($"  Errors:     {errCount}");
report.AppendLine($"  Timeouts:   {timeoutCount}");
report.AppendLine($"  Skipped:    {skipCount}");
if (abortedMemoryCount > 0)
    report.AppendLine($"  Aborted (memory): {abortedMemoryCount}");
report.AppendLine($"  Duration:   {sw.Elapsed.TotalMinutes:F1} minutes");
report.AppendLine($"  Timing CSV: {timingCsvPath}");
if (useDirectAdomd)
    report.AppendLine($"  Timeout:    {queryTimeoutMs}ms per query (ADOMD direct, mid-query memory watchdog)");
else
    report.AppendLine($"  Timeout:    disabled (useDirectAdomd=false, EvaluateDax fallback)");
if (globalFilters.Count > 0)
    report.AppendLine($"  Global filters: {globalFilters.Count} applied (TREATAS)");
if (maxRowsPerContext > 0)
    report.AppendLine($"  Row cap: TOPN({maxRowsPerContext}) per context");
if (crossProductColumns.Count > 0)
{
    report.AppendLine($"  Cross-product columns: {crossProductColumns.Count}");
    if (crossProductValueFilters.Count > 0)
        report.AppendLine($"  Cross-product value filters (TREATAS): {crossProductValueFilters.Count} columns filtered");
}
if (skipOnSmokeTestFailure && skippedMeasures.Count > 0)
{
    report.AppendLine($"  Smoke test: {skippedMeasures.Count} measure(s) skipped");
    foreach (var kvp in smokeResults)
        report.AppendLine($"    - {kvp.Key}: {kvp.Value}");
}
if (memoryAborted)
    report.AppendLine($"  ⚠ Run ABORTED due to memory threshold ({memoryThresholdPct}%)");
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
    for (int et = 0; et < timeoutLines.Length && shownT < 9; et++)
    {
        report.AppendLine("    " + timeoutLines[et]);
        shownT++;
    }
}

// ── Top 10 Slowest (ok-only) ──
if (timingRows.Any(t => t.Item4 == "ok"))
{
    report.AppendLine();
    report.AppendLine("  Top 10 Slowest Queries (ok only):");
    report.AppendLine("  ─────────────────────────────────────────────────────");
    var top10 = timingRows
        .Where(t => t.Item4 == "ok")
        .OrderByDescending(t => t.Item6)
        .Take(10);
    foreach (var t in top10)
    {
        report.AppendLine($"    {t.Item6,8}ms | {t.Item2} [{t.Item3}] ({t.Item5} rows, {t.Item7} distinct)");
    }
    report.AppendLine("  ─────────────────────────────────────────────────────");
}

// ── Timed Out Queries ──
if (timeoutCount > 0)
{
    report.AppendLine();
    report.AppendLine($"  Timed Out Queries ({timeoutCount}):");
    report.AppendLine("  ─────────────────────────────────────────────────────");
    var timedOut = timingRows.Where(t => t.Item4 == "timeout").ToList();
    foreach (var t in timedOut.Take(20))
    {
        report.AppendLine($"    {t.Item6,8}ms | {t.Item2} [{t.Item3}]  ← TIMEOUT ({queryTimeoutMs}ms limit)");
    }
    if (timedOut.Count > 20)
        report.AppendLine($"    ... and {timedOut.Count - 20} more (see {benchmarkLabel}-timeouts.log)");
    report.AppendLine("  ─────────────────────────────────────────────────────");
    report.AppendLine($"  Investigate DAX in {timeoutLogPath}");
}

// ── False-Fast Warnings ──
var falseFast = timingRows
    .Where(t => t.Item4 == "ok" && t.Item3 != "grand_total" && t.Item7 == 1 && t.Item5 > 1)
    .ToList();
if (falseFast.Count > 0)
{
    report.AppendLine();
    report.AppendLine($"  False-Fast Warnings ({falseFast.Count} test cases):");
    report.AppendLine("  distinct_values=1 with row_count>1 — dimension may not filter this measure");
    report.AppendLine("  ─────────────────────────────────────────────────────");
    foreach (var t in falseFast.Take(15))
    {
        report.AppendLine($"    {t.Item6,8}ms | {t.Item2} [{t.Item3}] ({t.Item5} rows, 1 distinct)");
    }
    if (falseFast.Count > 15)
        report.AppendLine($"    ... and {falseFast.Count - 15} more (see CSV)");
    report.AppendLine("  ─────────────────────────────────────────────────────");
}

report.AppendLine("════════════════════════════════════════════════════════════");

Info(report.ToString());
