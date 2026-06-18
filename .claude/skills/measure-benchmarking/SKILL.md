---
name: measure-benchmarking
description: "Use this skill when the user asks to 'benchmark measures', 'profile measure performance', 'find slow measures', 'identify slowest queries', 'time measures', 'measure timing', 'performance sweep', 'which measures are slow', or any request involving profiling query execution time across a set of Power BI measures. Covers the full lifecycle: measure selection (semantic search from .bim, domain filtering, exclusion patterns), dimension/context configuration (single-slice and cross-product with TREATAS value filters), authoring the JSON config consumed by scripts/benchmark_measures.py, and result interpretation. The stable benchmark engine lives in scripts/pbi_capture/ (TE-free Python); the retired benchmark-measures.csx template remains available on request. Distinct from regression testing — this skill captures timing only (no result values) for optimization prioritization."
---

# Measure Benchmarking for Power BI Models

A conversational skill that guides Claude through planning, generating, and executing measure performance benchmarks for Power BI semantic models. Benchmarks run on the TE-free Python path: `python scripts/benchmark_measures.py --config output/{label}.config.json`. The stable engine lives in the `scripts/pbi_capture/` package and is **never edited per session** — Claude authors only a JSON config (`"workflow": "benchmark"`) with the fields `measures`, `single_slice_dimensions`, `cross_product_columns`, `cross_product_value_filters`, `global_filters`, and `max_rows_per_context`. The engine builds the DAX, runs it under the safety stack, and writes the timing CSV. The retired `benchmark-measures.csx` template is still in the repo (under `scripts/legacy-tabular-editor/`) and can be emitted on request (see "Legacy TE3 output (on request)").

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
- Report-level visual performance (this skill tests the semantic model layer via DAX query execution, not visual rendering)
- General DAX debugging or optimization (use `pbi-dax-patterns.md` or `{model}-dax-performance.md`)

### Relationship to Regression Testing

| Concern | Regression Testing | Measure Benchmarking |
|---|---|---|
| **Purpose** | Verify correctness after a change | Identify slowest measures for prioritization |
| **Output** | Values + timing (JSON + CSV) | Timing only (CSV) |
| **When** | Before and after a model change | Any time — no change required |
| **Measures** | Scoped to affected measures (tiered) | Broad sweep — all measures in a domain |
| **Runner** | `capture_snapshot.py` | `benchmark_measures.py` |
| **Comparison** | `compare-snapshots.py` diffs two snapshots | Sort CSV by `duration_ms` descending |

## Core Principles

1. **The user drives measure selection.** Claude proposes lists based on semantic search of the .bim or model knowledge, but the user confirms before any script is generated. The user may describe measures by domain ("all [Domain A] cost measures"), by exclusion ("skip budget and time intelligence"), or by explicit list.
2. **Use the stable engine; author only the config.** The benchmark engine (`scripts/pbi_capture/`, run via `scripts/benchmark_measures.py`) is proven, tested code and is **never edited per session**. Claude's only per-session artifact is the JSON config. The engine handles TREATAS construction, TOPN logic, the safety stack, error handling, diagnostic mode, and the summary report with Top 10 Slowest. Do NOT fork or reimplement that logic.
3. **Mirror real report behavior.** DAX queries are constructed to match how Power BI visual queries work: SUMMARIZECOLUMNS with filter arguments, TREATAS for slicer simulation, bare measure references (no `IGNORE` — removed in v3 because it inflated blank-row counts). This ensures timing reflects what end users actually experience.
4. **Single run per test case.** The benchmark cannot clear the AS engine cache between queries. Multiple runs would measure warm-cache performance after the first query, skewing results. A single pass gives the most honest mixed-cache timing, representative of typical report usage.

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
> cross_product_columns:
>   'Table A'[Column A]
>   'Date'[Month]
>   'Table C'[Column C]
>   'Table B'[Column B]
>
> cross_product_value_filters (TREATAS):
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

## Phase 3: Author the Config

### 3.1 Config Fields

There is no script to copy. Claude writes `output/{label}.config.json` with `"workflow": "benchmark"` (full key reference in [`docs/config-schema.md`](../../../docs/config-schema.md)). The fields Claude fills, from Phases 1–2:

| Field | Type | From |
|---|---|---|
| `measures` | list of strings (**bare** names, no brackets) | Phase 1 |
| `single_slice_dimensions` | object: `label → "'Table'[Column]"` | Phase 2.1 |
| `cross_product_columns` | list of `"'Table'[Column]"` | Phase 2.2 |
| `cross_product_value_filters` | object: `"'Table'[Column]" → ["Value", …]` (TREATAS) | Phase 2.2 |
| `global_filters` | object: `"'Table'[Column]" → ["Value", …]` (TREATAS, applied to every query) | Phase 2.3 |
| `max_rows_per_context` | integer (0 = no cap; benchmark *may* cap, unlike capture) | Phase 2.4 |

Leave the safety knobs (`query_timeout_ms`, `smoke_test_timeout_ms`, `memory_threshold_pct`, `skip_on_smoke_failure`) and `diagnostic_mode` at their defaults unless the user asks. The engine builds the DAX and runs the sweep.

Example skeleton:

```json
{
  "workflow": "benchmark",
  "label": "benchmark",
  "measures": ["Measure A", "Measure B"],
  "single_slice_dimensions": { "by_month": "'Date'[Month]" },
  "cross_product_columns": ["'Dim'[Col]", "'Date'[Month]"],
  "cross_product_value_filters": { "'Dim'[Col]": ["Value 1"] },
  "global_filters": { "'Date'[Year]": ["2026"] },
  "max_rows_per_context": 0
}
```

> **Note the `global_filters` shape differs from regression capture.** Here it is an object (`column → [values]`) applied as TREATAS. In the capture config it is a list of DAX boolean expressions wrapped as KEEPFILTERS. Use the benchmark form in benchmark configs.

### 3.2 Validation Before Running

Before running, verify (the engine also validates and rejects violations):
- Every measure name matches a model measure (if a `.bim`/schema is available) and is **bare** (no brackets).
- Every column reference uses `'Table'[Column]` syntax.
- Every key in `cross_product_value_filters` also appears in `cross_product_columns` (the validator rejects orphans).
- No duplicate measures; `single_slice_dimensions` labels are unique snake_case.

### 3.3 Run It

```bash
python scripts/benchmark_measures.py --config output/{label}.config.json
```

Writes to `output/benchmark/` (override with `OUTPUT_DIR` / `output_dir`). Use `--diagnostic` (or `"diagnostic_mode": true`) to run only the first 8 tests as a quick sanity pass. Exit code `0` = run completed (per-test errors/timeouts are data); `2` = fatal (bad config, no/ambiguous instance, CLR load failure).

### 3.4 Test Case Count

Present the expected test case count for the user's awareness:

```
Test matrix: {N} measures × (1 grand_total + {S} single-slice + {C} cross-product) = {T} test cases

Estimated runtime: ~{T × avg_ms / 1000 / 60} minutes
(assuming ~2s average per query — actual time varies widely by measure complexity)
```

#### Legacy TE3 output (on request)

The Python path is the default. On explicit request, Claude can emit the retired `scripts/legacy-tabular-editor/benchmark-measures.csx` (raw or populated with the session's `measures`, `singleSliceDimensions`, `crossProductColumns`, `crossProductValueFilters`, `globalFilters`, `maxRowsPerContext`) for the user to run in TE3 (press F5). It writes the same timing CSV columns. Opt-in only — don't steer users to it unprompted.

---

## Safety Limits

The benchmark engine runs a layered safety stack — wall-clock timeout, mid-query memory watchdog with debounce, between-test memory check, and pre-flight smoke test — to prevent machine hangs from broken or runaway measures. Set them via config keys (or the env-var equivalents). All four are **on by default** because earlier benchmark runs hit real machine-crashing hung queries.

### Configuration Keys

| Config key | Default | Env var | Notes |
|---|---|---|---|
| `query_timeout_ms` | `60000` | `QUERY_TIMEOUT_MS` | Per-query wall-clock cap. A watchdog thread polls every 500 ms; on expiry the engine cancels the query (`cmd.Cancel()`, then a `conn.Dispose()` backstop that interrupts even pure formula-engine queries). The Python watchdog is the sole timeout enforcement — server-side `CommandTimeout` is deliberately `0`. Set to 0 to disable (not recommended). |
| `smoke_test_timeout_ms` | `10000` | `SMOKE_TEST_TIMEOUT_MS` | Per-measure pre-flight smoke test cap. Runs `EVALUATE ROW("r", [Measure])` per unique measure; failures are skipped in the main run (`status:"skipped"`) so a single runaway can't take down the sweep. |
| `memory_threshold_pct` | `80` | `MEMORY_THRESHOLD_PCT` | Memory watchdog trip point as a true % of **actual** total RAM (read via `GlobalMemoryStatusEx` — no hard-coded denominator, so no per-machine scaling needed). **Mid-query:** 3 consecutive critical polls (1.5 s sustained) before cancelling, so transient spikes don't abort legitimate queries. **Between-test:** no debounce — a single critical reading aborts the rest of the run (`status:"aborted_memory"`). Set to 0 to disable both. |
| `skip_on_smoke_failure` | `true` | `SKIP_ON_SMOKE_FAILURE` | When `true`, measures failing the smoke test are skipped (one `Type: smoketest_*` log entry + one `"status":"skipped"` row per dimension permutation in the timing CSV). Set to `false` to attempt every measure and rely on the wall-clock + memory watchdogs at runtime. |

**Why the smoke test is on by default:** earlier benchmark runs on large measure lists hit machine-crashing runaway queries — measures whose grand-total alone allocated unbounded memory. Smoke testing each measure once with a tight timeout catches those before they enter the main loop, where a wall-clock timeout would still let the query consume RAM for ~60 s.

**Mechanism check:** setting `SMOKE_TEST_TIMEOUT_MS` to 200–500 ms is a quick way to test the smoke pipeline end-to-end — most measures fail at that timeout and the run short-circuits with skipped rows in the CSV, without waiting for a real broken measure.

### XMLA Limitations

- The Power BI service (XMLA endpoint) enforces its own query cap (~225 s). `query_timeout_ms > 225000` has no effect.
- The memory watchdog is meaningful only for local PBIP workspace connections. In XMLA mode, model memory lives on the Fabric capacity and is invisible to this client-side check.

---

## Phase 4: Run and Interpret Results

### 4.1 Run the Sweep

```bash
python scripts/benchmark_measures.py --config output/{label}.config.json
```

Outputs in `output/benchmark/`:
- `{label}-timing.csv` — the primary deliverable
- `{label}-config.csv` — filter context reference for validation
- `{label}-testplan.json` — pre-flight manifest of planned test cases; survives a force-kill so you can identify which test was in flight
- `{label}-summary.txt` — run summary (also printed to stdout): Top 10 Slowest (ok-only), smoke-skipped measures, and Timed Out Queries
- `{label}-errors.log` — only if errors occurred
- `{label}-timeouts.log` — only if any queries timed out OR any measures failed the smoke test; each entry tagged with `Type:` (memory_watchdog \| query_timeout \| smoketest_timeout \| smoketest_error \| query_error) and `Reason:` (full error message)

### 4.2 Interpreting the Timing CSV

The CSV has columns: `test_id, measure, context, status, row_count, duration_ms, distinct_values`

**Status values:**

| Status | Meaning |
|---|---|
| `ok` | Query completed successfully |
| `error` | Query errored (DAX reference error, missing relationship, etc.) |
| `timeout` | Query was cancelled mid-flight — either wall-clock (`query_timeout_ms`) OR sustained memory pressure (memory watchdog). `duration_ms ≈ query_timeout_ms` for wall-clock, ~1.5 s for memory-cancel. The two cancellation paths are distinguished by the `Type:` tag in `{label}-timeouts.log`. |
| `skipped` | Measure failed the pre-flight smoke test. One row per dimension permutation with `duration_ms = 0`. |
| `aborted_memory` | The whole run was aborted by the between-test memory check before this test ran. `duration_ms = 0`. |

**Benchmark philosophy — runaways are filtered, then timeouts are data.** The engine runs a pre-flight smoke test (`EVALUATE ROW("r", [Measure])` per measure, default ON) so a single broken or runaway measure can't take down the benchmark sweep. Measures that pass smoke testing then run normally; if they time out under a richer dimension context, that IS the timing data — it tells you the measure is pathologically slow under those filters. Triage timeouts using `{label}-timeouts.log` (read the `Type:` tag first), and triage skipped measures the same way.

Guide the user through analysis:

**Sort by `duration_ms` descending** to find the slowest queries. The Top 10 in `{label}-summary.txt` gives a quick view, but the CSV allows deeper analysis. **Wall-clock timeouts** cluster at the top (`duration_ms ≈ query_timeout_ms`) and are clearly distinguishable from legitimate slow queries. **Memory-watchdog cancels** appear with `duration_ms ≈ 1.5–3 s` (cancelled as soon as sustained memory pressure was detected) — short duration but `status:"timeout"`. Read the `Type:` tag in `{label}-timeouts.log` to disambiguate.

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

When `global_filters` is non-empty, they're applied as TREATAS filter arguments inside SUMMARIZECOLUMNS (or inside the CALCULATETABLE wrapper for grand_total). This restricts the iteration space — matching how Power BI passes report-level slicer selections.

### Smoke Test

For each unique measure, the pre-flight smoke test runs:

```dax
EVALUATE ROW("r", [Measure Name])
```

No filters, no grouping. Just verifies the measure can return *something* without hanging or throwing. Failures are skipped in the main run.

### EVALUATE prefix

The engine builds bare table expressions; the `EVALUATE` prefix is added at the call site when the query runs. The smoke test query is the only place where the construction itself includes `EVALUATE` because it's a complete query, not a building block.

---

## File Outputs

Written to `output/benchmark/` by default (override with `output_dir` / `OUTPUT_DIR`):

| File | Source | Purpose |
|---|---|---|
| `output/{label}.config.json` | Claude (per session) | The benchmark contract: `measures`, dimensions, filters, `max_rows_per_context`. |
| `{label}-timing.csv` | `benchmark_measures.py` | Per-test-case timing: `test_id, measure, context, status, row_count, duration_ms, distinct_values`. Status values: `ok`, `error`, `timeout`, `skipped`, `aborted_memory`. |
| `{label}-config.csv` | `benchmark_measures.py` | Filter context reference: every global filter, single-slice dimension, and cross-product column with its TREATAS values, for manual validation. |
| `{label}-testplan.json` | `benchmark_measures.py` (pre-flight) | Planned test order, written before the main loop; survives a force-kill so you can identify which test was in flight. |
| `{label}-summary.txt` | `benchmark_measures.py` | Run summary (also stdout): Top 10 Slowest, smoke-skipped, Timed Out Queries. |
| `{label}-errors.log` | `benchmark_measures.py` (if errors) | Full exception details per failed test case. |
| `{label}-timeouts.log` | `benchmark_measures.py` (if timeouts/smoke failures) | One entry per timeout/smoke-failure with `testId \| measure \| context \| duration_ms`, `Type:` (memory_watchdog \| query_timeout \| smoketest_timeout \| smoketest_error \| query_error), `Reason:`, and full DAX. Smoke failures use synthetic test_ids `s0001..sNNNN`. Paste the DAX into DAX Studio for triage. |

`scripts/benchmark_measures.py` (+ the `scripts/pbi_capture/` engine) is stable, tested code — never edited per session.

---

## Quick Reference: User Interaction Points

At minimum, the user must confirm these before Claude generates the config:

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
     - Global: 'Date'[Year] → ["2025"]  (TREATAS)
     - TOPN: 0
  6. User confirms
  7. Writes output/benchmark.config.json: 25 measures × (1 + 3 + 1) = 125 test cases
  8. Runs `python scripts/benchmark_measures.py --config ...`; timing CSV + Top 10 Slowest in {label}-summary.txt
  9. Claude reads the CSV/summary and helps prioritize optimization targets
```
