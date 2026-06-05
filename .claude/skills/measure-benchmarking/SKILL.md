---
name: measure-benchmarking
description: "Use this skill when the user asks to 'benchmark measures', 'profile measure performance', 'find slow measures', 'identify slowest queries', 'time measures', 'measure timing', 'performance sweep', 'which measures are slow', or any request involving profiling query execution time across a set of Power BI measures. Covers the full lifecycle: measure selection (semantic search from .bim, domain filtering, exclusion patterns), dimension/context configuration (single-slice and cross-product with TREATAS value filters), script generation from the benchmark-measures.csx template, and result interpretation. Distinct from regression testing — this skill captures timing only (no result values) for optimization prioritization."
---

# Measure Benchmarking for Power BI Models

A conversational skill that guides Claude through planning, generating, and executing measure performance benchmarks for Power BI semantic models. The benchmark script uses `scripts/benchmark-measures.csx` as a tested template (read-only — copy to `output/{label}.csx` per session, edit the copy). Claude populates the configuration sections — `measures`, `singleSliceDimensions`, `crossProductColumns`, `crossProductValueFilters`, `globalFilters`, and `maxRowsPerContext` — based on user input. All other code (helpers, DAX construction, execution engine, timing CSV output, error handling, diagnostic mode, Teams webhook, summary report) is copied verbatim from the template.

## When to Use This Skill

Trigger when:
- The user wants to identify the slowest measures in a model for optimization prioritization
- The user asks to profile or benchmark a set of measures
- The user wants timing data across multiple dimensions without manually running queries in DAX Studio
- The user asks "which measures should I optimize first" or "what's the ROI of optimizing X"
- The user wants to compare measure timing across different filter contexts (e.g., "how does this measure perform sliced by [Column A] vs by [Column B]")

Do NOT use for:
- Pre/post comparison of a specific change (use `skill-regression-testing.md` — it captures both values and timing)
- Deep query plan analysis of a single measure (use DAX Studio Server Timings directly)
- Report-level visual performance (this skill tests the semantic model layer via EvaluateDax, not visual rendering)
- General DAX debugging or optimization (use `pbi-dax-patterns.md` or `{model}-dax-performance.md`)

### Relationship to Regression Testing

| Concern | Regression Testing | Measure Benchmarking |
|---|---|---|
| **Purpose** | Verify correctness after a change | Identify slowest measures for prioritization |
| **Output** | Values + timing (JSON + CSV) | Timing only (CSV) |
| **When** | Before and after a model change | Any time — no change required |
| **Measures** | Scoped to affected measures (tiered) | Broad sweep — all measures in a domain |
| **Script** | `capture-snapshot.csx` | `benchmark-measures.csx` |
| **Comparison** | `compare-snapshots.py` diffs two snapshots | Sort CSV by `duration_ms` descending |

## Core Principles

1. **The user drives measure selection.** Claude proposes lists based on semantic search of the .bim or model knowledge, but the user confirms before any script is generated. The user may describe measures by domain ("all [Domain A] cost measures"), by exclusion ("skip budget and time intelligence"), or by explicit list.
2. **Use the tested template.** The benchmark script at `scripts/benchmark-measures.csx` is a proven, working template (read-only — copy to `output/{label}.csx` per session). Claude MUST use it as the base and only populate the configuration sections. All other code — helpers, execution engine, TREATAS construction, TOPN logic, error handling, diagnostic mode, Teams webhook, summary report with Top 10 Slowest — is copied verbatim from the template. Do NOT regenerate these sections from scratch.
3. **Mirror real report behavior.** DAX queries are constructed to match how Power BI visual queries work: SUMMARIZECOLUMNS with filter arguments, TREATAS for slicer simulation, bare measure references (no `IGNORE` — removed in v3 because it inflated blank-row counts). This ensures timing reflects what end users actually experience.
4. **Single run per test case.** TE3 scripting cannot clear the AS engine cache between queries. Multiple runs would measure warm-cache performance after the first query, skewing results. A single pass gives the most honest mixed-cache timing, representative of typical report usage.

---

## Phase 1: Select Measures

### 1.1 Establish the Model Context

Check what Claude already knows:
- Check the model's KB notes (`{model}-*.md`) and any `artifacts/model-schema/` snapshot for the model name and structure
- Check memory edits for known performance findings or measure architecture notes
- If a `.bim` file is available (uploaded or previously parsed), use it for structural truth and measure inventory

If the model is unknown, ask the user:
- Which model is this?
- Can you provide the `.bim` file? (strongly preferred — enables semantic search across all measures)
- If no `.bim`, ask the user to paste their measure list directly

### 1.2 Accept Measure Selection Criteria

The user describes which measures to include using one or more of these approaches:

**Approach A — Domain description (semantic search):**

The user describes categories of measures in natural language. Claude searches the .bim (or parsed measure inventory from model knowledge) to build a proposed list.

Example user input:
> "All measures for [Domain A], [Domain B] costs and counts, and [Domain C] counts, excluding all budget measures and all time intelligence variations"

Claude's process:
1. Parse the .bim to extract all measure names (or search the model's KB notes / memory for the known measure inventory)
2. Apply inclusion filters — match measure names containing keywords from the user's domain descriptions (e.g., "[Domain A]", "[Domain B] Cost", "[Domain B] Count", "[Domain C] Count")
3. Apply exclusion filters — remove measures matching exclusion patterns (e.g., names containing "Budget", "Prior Year", "(ly)", "(ytd)", "(mtd)", "MoM", "YoY", "vs PY", or other time intelligence suffixes from `pbi-modeling-standards.md`)
4. Present the proposed list for confirmation

**Approach B — Explicit list:**

The user pastes a list of measure names directly. Claude validates each name exists in the model (if .bim is available) and flags any mismatches.

**Approach C — Hybrid:**

The user provides a domain description plus explicit additions or removals:
> "All [Domain A] measures, plus '[Measure A]' and '[Measure B]', but skip anything with 'DK' in the name"

### 1.3 Present Proposed Measure List for Confirmation

Always present the final list before generating the script. Format as a numbered list grouped by domain:

```
Proposed Benchmark Measures (23 measures):

[Domain B] Costs & Counts:
  1. [Measure B] by [Date Role 1]
  2. [Measure B] by [Date Role 2]
  3. [Measure C] by [Date Role 1]
  ...

[Domain A] Costs & Counts:
  7. [Measure D] (Base)
  8. [Measure E] by [Date Role 2]
  9. [Measure F] Base
  ...

[Domain C]:
  15. [Measure G] base
  16. [Measure H] by [Date Role 1]
  17. [Measure I] by [Date Role 1]
  ...

Excluded (matched exclusion pattern):
  - [Measure D] (ly)        [time intelligence]
  - [Measure C] Budget       [budget]
  - [Measure B] vs PY        [time intelligence]

Add or remove any before I generate the script?
```

Wait for user confirmation. The user may say "looks good", "add X", "remove Y", or "also exclude anything with 'Base' in the name."

### 1.4 Measure Selection Approach

Use the best available source to obtain the full measure inventory, in priority order:

1. **`.bim` file** — parse measure names and DAX expressions directly
2. **MCP server** (`powerbi-modeling-mcp.exe`) — confirm it's running, then use `measure_operations` to enumerate all measures. If uncertain whether it's installed, ask: "Do you have the Power BI modeling MCP server running? You can check with `Get-Process powerbi-modeling-mcp` in a terminal."
3. **Tabular Editor CLI** — future path, not yet available; headless read will enumerate measures without a `.bim`. Fall through to Option 4 for now.
4. **Ask the user** — request the measure list directly, or describe their naming conventions

Once the inventory is known, apply **semantic interpretation** — not literal keyword/string matching. When the user says "all project measures," use your understanding of the measure names (and DAX expressions where available) to build a proposed list of plausible matches, then present it for confirmation. Do NOT assume a measure must start with or contain the exact word "Project" to qualify; naming conventions vary widely across models.

**Process:**
1. Read the full measure inventory
2. For each user-described inclusion domain, propose the measures that semantically fit — based on names and DAX content
3. For each exclusion category (time intelligence, budget, internal helpers), propose the measures to remove
4. Present the proposed inclusion/exclusion list and ask: "Does this look right? Add or remove any before I generate the script."

**Common exclusion categories to clarify:**
- Time intelligence variants (e.g., "(ly)", "(ytd)", "(mtd)", "YoY", "MoM", "vs PY") — ask the user what their convention is
- Budget/planning measures
- Internal/helper measures (e.g., `_`-prefixed, or another model-specific convention)

*Example for reference:*

| User says | Inclusion | Exclusion |
|---|---|---|
| "all [Domain A] measures" | Measures in the [Domain A] table | — |
| "[Domain B] costs and counts" | Measures aggregating [Domain B] cost or count | — |
| "[Domain C]" | Measures tracking [Domain C] activity | — |
| "skip time intelligence" | — | Names containing "(ly)", "(ytd)", "MoM", "YoY", "vs PY" |
| "skip DK variants" | — | Names containing " DK" |

---

## Phase 2: Configure Dimensions and Filters

### 2.1 Single-Slice Dimensions

Ask the user which dimensions to slice by individually. Each dimension generates a separate query per measure, returning all distinct values (subject to TOPN).

Suggest dimensions based on model structure using the best available source:

1. **`.bim` file or MCP server:** Identify dimension tables (tables on the "one" side of relationships that are used as filter tables). From those tables, propose their key low-cardinality attribute columns (status/type/flag/category columns and calendar groupings). High-cardinality columns (individual names, IDs) are optional add-ons — note the cardinality so the user can decide.
2. **Tabular Editor CLI:** Future path; headless read will provide equivalent structural information. Fall through to Option 3 for now.
3. **Ask the user:** "Which columns do users commonly apply as slicers in reports on this model? I'll use those as the single-slice dimensions."

Always present a proposed list and wait for user confirmation before proceeding. The user may add, remove, or substitute.

*Example for reference:*

```
Suggested single-slice dimensions:
  1. 'Table A'[Column A]
  2. 'Table B'[Column B]               (e.g., a toggle/flag dimension)
  3. 'Date'[Month]                     (or 'Date'[Start of Year] for annual)
  4. 'Table C'[Column C]               (e.g., a category/type column)
  5. 'Table D'[Column D]               (e.g., a status column)
  6. 'Table E'[Column E]               (high-cardinality name/ID — optional)
  7. 'Table F'[Column F]
```

### 2.2 Cross-Product Context

Ask whether the user wants a cross-product context. Explain the purpose:

> "The cross-product evaluates all measures against a single combined SUMMARIZECOLUMNS with multiple grouping columns — like a matrix visual with multiple axes and slicers. This is the most expensive query shape and often reveals performance issues that single-slice tests don't catch."

If yes, ask which columns to include. Then ask which columns should be constrained to specific values (slicer simulation via TREATAS).

Example interaction:

> **User:** "Cross-product with Column A, Month, Column C, and the toggle. Filter Column C to just Value 1 and the toggle to Value 2."
>
> **Claude generates:**
> ```
> crossProductColumns:
>   'Table A'[Column A]
>   'Date'[Month]
>   'Table C'[Column C]
>   'Table B'[Column B]
>
> crossProductValueFilters (TREATAS):
>   'Table C'[Column C] → {"Value 1"}
>   'Table B'[Column B] → {"Value 2"}
>
> Unfiltered columns (all values):
>   'Table A'[Column A]       (~40 values)
>   'Date'[Month]             (~12 values)
>
> Estimated cross-product rows: ~480 (40 × 12 × 1 × 1)
> ```

If the estimated row count is very high (>5,000), warn the user and suggest either adding value filters to narrow high-cardinality columns or using TOPN. This prevents timeout errors and keeps benchmark runtime reasonable.

### 2.3 Global Filters

Ask whether any report-level filters should apply to all queries. Ask the user which filters are typically active in their reports. Common patterns: restricting to a specific time period, or applying a standard cohort flag (e.g., "current period only", "same-store comparison").

These are applied as KEEPFILTERS inside a CALCULATE wrapper around the measure reference, matching how report-level filters work.

*(Example: `'Date'[Start of Year] = DATE(2025,1,1)` to restrict to a fiscal year, and `'Table A'[Column A] = "Value 1"` for a standard cohort flag.)*

### 2.4 TOPN Configuration

Ask whether to cap rows per context:
- **0 (default)** — return all rows. Best for accurate timing since it measures full query cost.
- **50-100** — useful if high-cardinality dimensions (e.g., a name/ID column with 500+ values) cause timeouts. Applied as TOPN wrapping SUMMARIZECOLUMNS.

For benchmarking, `0` is usually correct because the goal is to measure the full query cost that users experience. Only use TOPN if queries are timing out.

---

## Phase 3: Generate the Script

### 3.1 Template Rules

The benchmark script (`benchmark-measures.csx`) is a tested template. Claude populates ONLY these configuration sections:

| Section | What Claude fills in |
|---|---|
| `measures` | `List<string>` of measure names from Phase 1 |
| `singleSliceDimensions` | `Dictionary<string, string>` of label → DAX column from Phase 2.1 |
| `crossProductColumns` | `List<string>` of DAX columns from Phase 2.2 |
| `crossProductValueFilters` | `Dictionary<string, List<string>>` of column → values from Phase 2.2 |
| `globalFilters` | `List<string>` of KEEPFILTERS expressions from Phase 2.3 |
| `maxRowsPerContext` | Integer from Phase 2.4 |

All other code — helpers (`ExecuteDaxWithTimeout`, `IsMemoryCritical`, `BuildTreatasArgs`), test matrix generation, execution engine with timeout and memory watchdog, timing CSV writer, error handling, diagnostic mode, Teams webhook, summary report with Top 10 Slowest and Timed Out Queries — MUST be copied verbatim from the template. Do NOT regenerate these sections.

### 3.2 Script Generation Process

1. Start with the full `scripts/benchmark-measures.csx` template (copy it to `output/{label}.csx` — never edit the template in place)
2. Replace the commented-out placeholder entries in each configuration section with the confirmed values
3. Leave `benchmarkLabel`, `diagnosticMode`, `teamsWebhookUrl`, and `outputDir` at their defaults (the user adjusts these per-run)
4. Present the complete script for download

### 3.3 Validation Before Delivery

Before presenting the script, verify:
- Every measure name in the list matches a measure in the model (if .bim is available)
- Every dimension column reference uses the correct `'Table'[Column]` syntax
- Cross-product value filter columns are all present in `crossProductColumns`
- No duplicate measures in the list
- Label keys in `singleSliceDimensions` are unique and use snake_case

### 3.4 Test Case Count

Present the expected test case count for the user's awareness:

```
Test matrix: {N} measures × (1 grand_total + {S} single-slice + {C} cross-product) = {T} test cases

Estimated runtime: ~{T × avg_ms / 1000 / 60} minutes
(assuming ~2s average per query — actual time varies widely by measure complexity)
```

---

## Safety Limits (v5+)

The benchmark script includes a layered safety stack — wall-clock timeout, mid-query memory watchdog with debounce, between-test memory check, and pre-flight smoke test — to prevent machine hangs from broken or runaway measures. These are configured via env vars (CLI) or by editing the config block directly (GUI). All four are **on by default** (matches the v8 regression script) because earlier benchmark runs had real machine-crashing hung queries.

### Configuration Variables

| Variable | Default | Env var | Notes |
|---|---|---|---|
| `queryTimeoutMs` | `60000` | `QUERY_TIMEOUT_MS` | Per-query wall-clock cap. v5+ runs the query on a thread-pool task and the script thread polls every 500 ms; on wall-clock expiry the script calls `cmd.Cancel()` (the only mechanism that reliably interrupts a SE-bound Tabular query). `AdomdCommand.CommandTimeout` is set as a backstop only. Set to 0 to disable (not recommended). |
| `smokeTestTimeoutMs` | `10000` | `SMOKE_TEST_TIMEOUT_MS` | Per-measure pre-flight smoke test cap. Smoke test runs `EVALUATE ROW("r", [Measure])` per unique measure. Measures that fail this minimal check are skipped in the main run with `status:"skipped"` so a single runaway can't take down the whole benchmark. |
| `memoryThresholdPct` | `80.0` | `MEMORY_THRESHOLD_PCT` | Memory watchdog trip point as % of RAM. **Mid-query (v5+):** the per-query watchdog requires 3 consecutive critical polls (3 × 500 ms = 1.5 s sustained pressure) before cancelling, so transient working-set spikes during normal msmdsrv evaluation do not abort legitimate queries. **Between-test:** has no debounce — a single critical reading aborts the rest of the run with `status:"aborted_memory"`. Set to 0 to disable both. |
| `useDirectAdomd` | `true` | `USE_DIRECT_ADOMD` | When true, uses ADOMD with cancellable threaded execution. When false, falls back to `EvaluateDax()` (no timeout, no cancellability — a hung query freezes TE3). Use only as a compatibility escape hatch. |
| `skipOnSmokeTestFailure` | `true` | `SKIP_ON_SMOKE_FAILURE` | When `true` (default), measures failing the smoke test are skipped. Their failure shows up as one `Type: smoketest_*` entry in the timeouts log and one `"status":"skipped"` row per dimension permutation in the timing CSV. Set to `false` to attempt every measure regardless and rely on the wall-clock + memory watchdogs to catch runaways at runtime — accepts the risk of long timeouts/cancels in exchange for not pre-filtering measures.|

**Why the smoke test is on by default:** earlier benchmark runs (v3 and prior) on large measure lists hit machine-crashing runaway queries — measures whose grand-total alone allocated unbounded memory. Smoke testing each measure once with a tight timeout catches those before they enter the main loop, where a wall-clock timeout would still let the query consume RAM for ~60 s.

### How smoke-test skipping works internally

The smoke loop builds an in-memory `HashSet<string> skippedMeasures` and a `Dictionary<string,string> smokeResults` (measure → failure reason). Nothing is written to disk for smoke failures except (a) one `Type: smoketest_*` entry per failed measure in `{label}-timeouts.log`, and (b) one `"status":"skipped"` row per dimension permutation in the timing CSV (all sharing the same skip reason).

**Mechanism check:** setting `SMOKE_TEST_TIMEOUT_MS` to 200–500 ms via env var is a quick way to test the smoke pipeline end-to-end — most measures fail at that timeout and the run short-circuits with skipped rows in the CSV. Useful for verifying the smoke pipeline without waiting for a real broken measure.

### RAM Scaling

The memory watchdog uses a hard-coded 16 GB denominator (WMI/VisualBasic.Devices not guaranteed in TE3 scripting). Scale the threshold proportionally for your machine:

| Machine RAM | Recommended `memoryThresholdPct` |
|---|---|
| 16 GB | 80% |
| 32 GB | 40% |
| 64 GB | 20% |

### XMLA Limitations

- The Power BI service (XMLA endpoint) enforces its own query cap (~225s). `queryTimeoutMs > 225000` has no effect.
- The memory watchdog is meaningful only for local PBIP workspace connections. In XMLA mode, model memory lives on the Fabric capacity and is invisible to this client-side check.

---

## Phase 4: Run and Interpret Results

### 4.1 User Runs the Script

The user runs the script in TE3 connected to the target model. Outputs:
- `{label}-timing.csv` — the primary deliverable
- `{label}-config.csv` — filter context reference for validation
- `{label}-testplan.json` — pre-flight manifest of planned test cases (v5+); survives a force-kill so the user can identify which test was in flight
- `{label}-errors.log` — only if errors occurred
- `{label}-timeouts.log` — only if any queries timed out OR any measures failed the smoke test (v5+); each entry tagged with `Type:` (memory_watchdog \| query_timeout \| smoketest_timeout \| smoketest_error \| query_error) and `Reason:` (full error message)
- `Info()` popup with summary including Top 10 Slowest (ok-only), smoke-skipped measures, and Timed Out Queries sections

### 4.2 Interpreting the Timing CSV

The CSV has columns: `test_id, measure, context, status, row_count, duration_ms, distinct_values`

**Status values:**

| Status | Meaning |
|---|---|
| `ok` | Query completed successfully |
| `error` | Query errored (DAX reference error, missing relationship, etc.) |
| `timeout` | Query was cancelled mid-flight — either wall-clock (`queryTimeoutMs`) OR sustained memory pressure (memory watchdog). `duration_ms ≈ queryTimeoutMs` for wall-clock, ~1.5 s for memory-cancel. The two cancellation paths are distinguished by the `Type:` tag in `{label}-timeouts.log`. |
| `skipped` | Measure failed the pre-flight smoke test (v5+). One row per dimension permutation with `duration_ms = 0`. |
| `aborted_memory` | The whole run was aborted by the between-test memory check before this test ran. `duration_ms = 0`. |

**Benchmark philosophy — runaways are filtered, then timeouts are data.** v5+ runs a pre-flight smoke test (`EVALUATE ROW("r", [Measure])` per measure, default ON) so a single broken or runaway measure can't take down the benchmark sweep. Measures that pass smoke testing then run normally; if they time out under a richer dimension context, that IS the timing data — it tells you the measure is pathologically slow under those filters. Triage timeouts using `{label}-timeouts.log` (read the `Type:` tag first), and triage skipped measures the same way.

Guide the user through analysis:

**Sort by `duration_ms` descending** to find the slowest queries. The Top 10 from the `Info()` popup gives a quick view, but the CSV allows deeper analysis. **Wall-clock timeouts** cluster at the top (`duration_ms ≈ queryTimeoutMs`) and are clearly distinguishable from legitimate slow queries. **Memory-watchdog cancels** appear with `duration_ms ≈ 1.5–3 s` (cancelled as soon as sustained memory pressure was detected) — short duration but `status:"timeout"`. Read the `Type:` tag in `{label}-timeouts.log` to disambiguate.

**Group by `measure`** to see which measures are consistently slow across all contexts vs. slow only in specific contexts:
- Slow everywhere → likely an expensive base calculation (nested iterators, complex CALCULATE chains)
- Slow only in cross-product or high-cardinality slices → likely a context transition or cardinality explosion issue
- Timeout in all contexts → the measure is broken or requires fundamental DAX restructuring

**Compare `row_count` across contexts** for the same measure:
- Very high row counts in cross-product → the dimension combination produces a large result set, which is expensive regardless of DAX complexity
- Row count of 0 with non-zero timing → the measure returns BLANK for all combinations in that context, but the engine still evaluated it

**Check `distinct_values` against `row_count` (false-negative safeguard)** — this is NOT value validation (no baseline comparison), but it's a quick sanity check that the measure actually evaluated under the test's filter context. When `distinct_values = 1` while `row_count > 1`, the measure returned the same value for every grouping — usually a degenerate evaluation (missing relationship path, blocked filter propagation, hard-coded constant) where the engine short-circuited to a constant. A fast `duration_ms` here is NOT an optimized query. Flag the measure for the user and recommend opening the DAX in DAX Studio before dismissing it from the optimization list.

**Flag errors** — any test case with `status = error` should be investigated. Common causes:
- Measure references a table with no relationship path to a dimension in the context
- TREATAS value doesn't exist in the column (typo in configuration)

**Triage timeouts** — open `{label}-timeouts.log` and paste the DAX into DAX Studio. Run with Server Timings enabled. Look for:
- SE/FE split heavily skewed toward FE → formula engine doing heavy iteration (nested SUMX, AVERAGEX)
- Very high SE call count → many storage engine round-trips (FILTER on table, no summarized data)
- Long SE duration per call → no VertiPaq index available for the filter combination

### 4.3 Prioritization Framework

Help the user prioritize which measures to optimize by considering:

| Factor | Weight | Assessment |
|---|---|---|
| **Query time** | High | Absolute duration — anything >5s is a candidate |
| **User visibility** | High | Is this measure on a frequently-used report page? |
| **Context sensitivity** | Medium | Does it degrade sharply in cross-product contexts? |
| **Measure family size** | Medium | Is it a base measure referenced by 10+ derived measures? Optimizing the base improves all dependents |
| **Optimization feasibility** | Medium | Does the DAX have known anti-patterns (nested SUMX, FILTER on table, redundant CALCULATE)? |
| **Row count ratio** | Low | High duration ÷ low row count = expensive per-row calculation |

Present a prioritized recommendation:

```
Optimization Priority:

1. [12,400ms] [Measure Name] [ColumnA_x_Month_x_ColumnC]
   → High visibility, base measure for N derived measures
   → Known pattern: e.g., nested AVERAGEX with bridge traversal

2. [8,200ms] [Measure Name] [by_<dimension>]
   → Critical KPI on executive dashboard
   → Previously optimized — may have room for further improvement

3. [6,100ms] [Measure Name] [ColumnA_x_Month_x_ColumnC]
   → Moderate visibility, but base for a measure family
   → Likely cardinality issue with <Dim A> × <Dim B> cross-product
```
*(Example: common offenders are usually base measures with nested iterators or bridge traversals — e.g., a per-key average over a snapshot table, or a distinct-count over a high-cardinality dimension.)*

---

## DAX Query Construction Reference

This section documents how the template constructs DAX queries. Claude should NOT modify this logic — it is baked into the template. This reference exists so Claude understands what the script does when discussing results with the user.

### Grand Total

```dax
ROW("Result", [Measure Name])
```

No grouping columns. Uses `ROW`, not `SUMMARIZECOLUMNS`, because `SUMMARIZECOLUMNS` with filter arguments and no grouping columns returns 0 rows. When global filters are set, the `ROW` is wrapped in `CALCULATETABLE` so the filters apply.

### Single-Slice Dimension

```dax
SUMMARIZECOLUMNS('Table'[Column], "Result", [Measure Name])
```

One grouping column — returns one row per distinct value. Bare measure reference (no `IGNORE` — removed in v3 because it inflated blank-row counts). With TOPN:

```dax
TOPN(N, SUMMARIZECOLUMNS('Table'[Column], "Result", [Measure Name]))
```

### Cross-Product

```dax
SUMMARIZECOLUMNS(
    'Table1'[Col1],
    'Table2'[Col2],
    'Table3'[Col3],
    TREATAS({"Value1"}, 'Table3'[Col3]),
    "Result", [Measure Name]
)
```

All columns in one SUMMARIZECOLUMNS. TREATAS filter arguments constrain specific columns to selected values — matching Power BI visual query behavior where slicer selections are passed as filter arguments into SUMMARIZECOLUMNS.

### Global Filters

When `globalFilters` is non-empty, they're applied as TREATAS filter arguments inside SUMMARIZECOLUMNS (or inside the CALCULATETABLE wrapper for grand_total). This restricts the iteration space — matching how Power BI passes report-level slicer selections.

### Smoke Test (v5+)

For each unique measure, the pre-flight smoke test runs:

```dax
EVALUATE ROW("r", [Measure Name])
```

No filters, no grouping. Just verifies the measure can return *something* without hanging or throwing. Failures are skipped in the main run.

### EVALUATE prefix

The DAX construction block builds bare table expressions; the `EVALUATE` prefix is added at the call site (`ExecuteDaxWithTimeout("EVALUATE " + dax, ...)`). The smoke test query is the only place where the construction itself includes `EVALUATE` because it's a complete query, not a building block.

---

## File Outputs

| File | Source | Purpose |
|---|---|---|
| `benchmark-measures.csx` | Template from `scripts/benchmark-measures.csx`, with configuration sections populated by Claude | TE3 script — runs DAX, writes timing CSV |
| `{label}-timing.csv` | Generated by `.csx` when user runs it | Per-test-case timing: `test_id, measure, context, status, row_count, duration_ms, distinct_values`. Status values: `ok`, `error`, `timeout`, `skipped`, `aborted_memory`. |
| `{label}-config.csv` | Generated alongside the timing CSV | Filter context reference: lists every global filter, single-slice dimension, and cross-product column with its TREATAS values for manual validation. |
| `{label}-testplan.json` | Generated by `.csx` pre-flight (v5+) | Planned test order written before the main loop starts; survives a force-kill so you can identify which test was in flight. |
| `{label}-errors.log` | Generated by `.csx` if errors occur | Full exception details per failed test case. |
| `{label}-timeouts.log` | Generated by `.csx` if timeouts OR smoke failures occur (v5+) | One entry per timeout/smoke-failure with `testId \| measure \| context \| duration_ms`, `Type:` (memory_watchdog \| query_timeout \| smoketest_timeout \| smoketest_error \| query_error), `Reason:` (full error text), and full DAX. Smoke failures use synthetic test_ids `s0001..sNNNN`. Paste the DAX into DAX Studio for triage. |

Output directory: `Desktop\PBI-Benchmark\` (configurable via `outputDir` or `OUTPUT_DIR` env var).

---

## Quick Reference: User Interaction Points

At minimum, the user must confirm these before Claude generates the script:

| Decision Point | What Claude proposes | User confirms or adjusts |
|---|---|---|
| Model identity | Inferred from context or asked | "It's the [ModelName] model" |
| Measure selection criteria | Domain keywords + exclusion patterns | "Add X, remove Y, also skip Z" |
| Proposed measure list | Numbered list grouped by domain | "Looks good" or adjustments |
| Single-slice dimensions | Common slicer fields from model knowledge | "Use all" or adjustments |
| Cross-product columns | Suggested based on typical report layout | "Yes, filter Column C to Value 1" |
| Cross-product value filters | Specific values for constrained columns | Confirm values |
| Global filters | Common report-level filters | "Add [cohort flag] = Y" or "Restrict to current year" |
| TOPN | Default 0 (no limit) | "Cap at 100" or keep default |
| Test case count | Calculated from measures × contexts | Acknowledge or adjust scope |

---

## Example End-to-End Session

*The following example is generic. Substitute your own model name, measure domains, and dimension columns.*

```
User: "I want to benchmark all the [Domain A] measures and [Domain B] cost measures.
       Skip budget and time intel. Slice by Column A, Month, and Column C.
       Cross-product those same three with Column C filtered to Value 1 only.
       Global filter to 2025."

Claude:
  1. Obtains measure inventory from .bim / MCP / user
  2. Semantically interprets "[Domain A] measures" and "[Domain B] cost measures"
     against the inventory, excluding budget and time intelligence variants
  3. Presents proposed list of ~25 measures, grouped by domain, for confirmation
  4. User confirms (or adjusts)
  5. Presents dimension config:
     - Single-slice: by_col_a, by_month, by_col_c
     - Cross-product: Column A × Month × Column C, TREATAS Column C = "Value 1"
     - Global: 'Date'[Start of Year] = DATE(2025, 1, 1)
     - TOPN: 0
  6. User confirms
  7. Generates script: 25 measures × (1 + 3 + 1) = 125 test cases
  8. User runs in TE3, gets timing CSV + Top 10 Slowest in Info popup
  9. User shares CSV or Top 10, Claude helps prioritize optimization targets
```
