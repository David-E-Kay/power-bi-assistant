---
name: regression-testing
description: "Use this skill when the user asks to 'test a refactor', 'regression test', 'validate model changes', 'compare before and after', 'capture baseline', 'snapshot measures', 'diff results', 'ensure parity', or any request involving verifying that a Power BI semantic model produces identical results after DAX edits, relationship changes, calc column additions, or structural refactors. Covers the full lifecycle: change scope analysis, measure tiering (including random sampling and domain/type filtering), test case generation (single-dimension and cross-product contexts), baseline capture, post-refactor capture, and comparison. Drives the TE-free Python runner: Claude authors a JSON config consumed by scripts/capture_snapshot.py (the stable capture engine lives in scripts/pbi_capture/ and is never edited per session). The retired TE3 .csx template remains available on request as a legacy alternative."
---

# Regression Testing for Power BI Model Refactors

A conversational skill that guides Claude through planning, generating, and executing regression tests for Power BI semantic model changes. Capture runs on the TE-free Python path: `python scripts/capture_snapshot.py --config output/{label}.config.json`. The stable capture engine lives in the `scripts/pbi_capture/` package and is **never edited per session** — the only per-session artifact is a JSON config file (pure data) that Claude authors from the specific model, change scope, and user confirmation. The config lists the test cases (`tests`) and the dimension map (`group_by_columns`); the engine builds the DAX (single-dimension and cross-product `SUMMARIZECOLUMNS`) at runtime, which avoids cross-language string-escaping issues. Comparison uses the stable `scripts/compare-snapshots.py`, unchanged. The retired TE3 `.csx` template is still in the repo and can be emitted on request (see "Legacy TE3 output (on request)").

## When to Use This Skill

Trigger when:
- The user is about to refactor model relationships, DAX measures, or table structure
- The user asks to validate that a change didn't break anything
- The user wants to capture a baseline before making changes
- The user asks to compare results between an original and modified model
- The session has produced structural changes and needs post-refactor validation (Phase 5 of `refactor-strategy/SKILL.md`)

Do NOT use for:
- General DAX debugging (use `pbi-dax-patterns.md` or `{model}-dax-performance.md`)
- Performance benchmarking only (use DAX Studio traces directly)
- Report-level visual testing (this skill tests the semantic model layer)

## Core Principles

1. **Never generate test scripts without understanding the change.** The test suite is shaped entirely by what changed. A bridge refactor needs different tests than a measure rewrite.
2. **Always confirm with the user.** Every decision point — change scope, measure tiers, filter contexts, edge cases — requires user confirmation before generating artifacts.
3. **Zero tolerance by default.** Baseline and refactored models are offline copies of the same data. Results must match exactly. BLANK ≠ 0 ≠ null — these are semantically distinct in DAX and must be compared as such.
4. **Use the stable engine; author only the config.** The capture engine at `scripts/pbi_capture/` is proven, tested code and is **never edited per session**. Claude's only per-session artifact is the JSON config (`output/{label}.config.json`) — specifically the `tests` list and `group_by_columns` map. The engine builds the DAX (single-dimension and cross-product `SUMMARIZECOLUMNS`), runs it under the safety stack, and serializes the snapshot automatically. All other behavior — helpers, execution engine, JSON serialization, error handling, diagnostic mode, summary report — lives in the package. Do NOT fork or reimplement that logic; it has already been tested and validated.

---

## Phase 1: Understand the Change Scope

Before generating any test, Claude must understand two things: **what the model looks like** and **what is changing**.

### 1.1 Establish the Model Context

Check what Claude already knows:
- Check the model's KB notes (`{model}-*.md`) and any `artifacts/model-schema/` snapshot for the model name and structure
- Check memory edits for known gotchas, relationship patterns, or calc column rules
- If a `.bim` file is available (uploaded or previously parsed), use it for structural truth

If the model is unknown, ask the user:
- Which model is this? (name, domain)
- Can you provide the `.bim` file? (preferred — enables automated dependency analysis)
- If no `.bim`, ask for a summary: key fact tables, dimensions, bridge tables, and the approximate measure count

### 1.2 Determine the Change Scope

The change scope drives everything downstream. Accept it via **one of two paths**:

**Path A — User describes the change explicitly:**
The user says something like "I'm adding a direct FK from [Table A] to [Table B] and changing the bridge to single-direction." Claude extracts:
- Which relationships are being added, removed, or modified (direction, cardinality, active/inactive)
- Which measures are being rewritten (new DAX)
- Which calc columns are being added or removed
- Which tables are structurally changing

Ask clarifying questions until the change is specific enough to trace through the dependency graph. Examples:
- "You mentioned changing the bridge direction — are any measures using `CROSSFILTER(..., BOTH)` on that bridge? Those would need DAX updates too."
- "Is the new FK column already in the source data, or are you adding a calc column?"

**Path B — Inferred from a diff:**
If the user has a before/after `.bim` (or TMDL files), Claude can diff them:
- Compare relationship lists: new, deleted, modified (cardinality, direction, active flag)
- Compare measure DAX expressions: identify measures with changed expressions
- Compare table columns: new calc columns, deleted columns, type changes
- Compare calculation groups: new/modified calc items

Present the inferred diff to the user as a summary table:

```
┌─────────────────────────────────────────────────────────────┐
│ Inferred Changes from .bim Diff                            │
├──────────┬──────────────────────────────────────────────────┤
│ Category │ Details                                          │
├──────────┼──────────────────────────────────────────────────┤
│ Rels (+) │ [Table A] → [Table B] (active, single, M:1)      │
│ Rels (~) │ Bridge bidir → single direction                  │
│ Rels (-) │ Bridge → [Table B] (deleted)                     │
│ Measures │ 8 measures with modified DAX                     │
│ Columns  │ +1 calc column on [Table C] (_CalcCol)           │
│ Inactive │ 20 unused inactive relationships removed         │
└──────────┴──────────────────────────────────────────────────┘
```

**In either path**, ask the user to confirm: "Does this capture the full scope of changes? Anything I'm missing?"

### 1.3 Classify Measures into Test Tiers

Once the change scope is confirmed, classify every measure in the model. The classification logic depends on what changed — these are the general rules, but Claude must adapt them to the specific change:

**Tier 1 — Directly affected:**
- Measure's DAX was rewritten as part of the refactor
- Measure's DAX references a relationship that was added, removed, or modified (via `USERELATIONSHIP`, `CROSSFILTER`, or implicit filter propagation through a changed path)
- Measure references a column that was added, removed, or changed type
- Measure references a table whose active relationship path to a dimension changed

**Tier 2 — Transitively affected:**
- Measure depends on (calls) a Tier 1 measure
- Walk the dependency chain: if Measure A calls Measure B which calls Tier 1 Measure C, then both A and B are Tier 2
- Calculation group items that modify Tier 1 or Tier 2 measures are also Tier 2
- **Note:** Tier 2 measures that only delegate to a Tier 1 base (e.g., time intelligence wrappers like MoM%, YoY%) may not need separate testing if their logic adds no unique filters or relationship modifiers beyond what the Tier 1 base already tests. Ask the user whether to include or exclude Tier 2 based on their risk tolerance and time budget.

**Tier 3 — Unaffected (spot-check):**
- Measure was not modified and does not depend on anything that was
- Test at grand-total level only to catch unintended side effects from relationship direction changes

**Skip:**
- Measures that return static text, formatting strings, or SVG markup
- Measures prefixed with `_` that are internal/helper and are already tested transitively via their parent
- Budget or planning measures when the change scope is limited to operational measures (confirm with user)

**When a `.bim` is available**, automate this classification:
- Parse all measures and their DAX expressions
- Build a dependency graph (measure → measure, measure → column, measure → table)
- Cross-reference against the change scope to assign tiers
- Present the result to the user for confirmation

**When no `.bim` is available**, ask the user:
- "Which measures are most critical to test? (Tier 1 candidates)"
- "Are there measures that call those measures? (Tier 2 candidates)"
- "How many total measures are in the model? Should we spot-check the rest?"

Always present the tier classification before proceeding:

```
┌────────────────────────────────────────────────────────────┐
│ Measure Test Tiers                                         │
├──────┬───────┬─────────────────────────────────────────────┤
│ Tier │ Count │ Examples                                     │
├──────┼───────┼─────────────────────────────────────────────┤
│  1   │   8   │ [Measure A], [Measure B], ...               │
│  2   │  14   │ [Measure A] (YTD), [Measure C], ...         │
│  3   │  67   │ [Measure D], [Measure E], ...               │
│ Skip │  11   │ _FormatColor, _SVGBar, _TooltipHeader...    │
└──────┴───────┴─────────────────────────────────────────────┘

Does this tiering look right? Any measures to move between tiers?
```

### 1.4 Measure Selection Shortcuts

When the user does not specify exact measures, Claude can select measures efficiently using these strategies. All require user confirmation before proceeding.

#### 1.4a Random Sampling (no explicit guidance)

When the user says something like "test some measures," "spot-check the model," or "run a quick regression test" without specifying which measures:

1. **Build the eligible pool** — Exclude measures that should always be skipped:
   - `_`-prefixed internal/helper measures
   - Measures whose DAX returns only static text, SVG, or formatting (detect via: expression contains no aggregation functions and returns a string literal or concatenation)
   - Budget/planning measures (unless the change touches those domains)

2. **Classify by domain and type** using the rules in Section 1.4c below.

3. **Stratified random sample** — Sample proportionally from each domain×type stratum to ensure coverage breadth. Default target: **20 measures** (adjustable by user). If a stratum has only 1-2 measures, include all of them. Present the sample grouped by domain:

```
┌────────────────────────────────────────────────────────────┐
│ Random Sample: 20 measures from 89 eligible                │
├─────────────┬───────┬──────────────────────────────────────┤
│ Domain      │ Count │ Examples                              │
├─────────────┼───────┼──────────────────────────────────────┤
│ [Domain A]  │   7   │ [Measure A], [Measure B], ...         │
│ [Domain B]  │   5   │ [Measure C], ...                      │
│ [Domain C]  │   4   │ [Measure D], ...                      │
│ [Domain D]  │   2   │ [Measure E], ...                      │
│ Other       │   2   │ [Measure F], ...                      │
└─────────────┴───────┴──────────────────────────────────────┘

Want to adjust the sample size or swap any measures?
```

4. **Seed consistency** — When generating the random sample in conversation, Claude should present a deterministic selection (e.g., alphabetically first N from each stratum) rather than truly random, so the user sees the same proposal if they ask again. True randomness in the C# script is not needed — the selection happens at the conversation level.

#### 1.4b Domain or Type Filtering (user specifies a category)

When the user says "test all cost measures," "test [domain] measures," "test the count measures," or similar:

1. **Parse the request** into domain filter, type filter, or both:
   - "cost measures" → type = cost
   - "[domain] measures" → domain = [domain]
   - "[domain] count measures" → domain = [domain] AND type = count

2. **Filter the measure list** from the `.bim` using the classification rules below.

3. **Present the filtered list** for confirmation — the user may want to exclude some.

4. **Apply tier logic on top** — Even within a filtered set, Claude should note which are Tier 1 vs. Tier 2/3 relative to the change scope (if a change scope has been established).

#### 1.4c Domain and Type Classification Rules

These rules classify measures from the model. They use the measure's home table (display folder or `measureTable` field in the `.bim`) and the measure name + DAX expression.

**Domain classification** (derived from model structure — not hardcoded):

Establish the model context using the best available source, in priority order:

**Option 1 — `.bim` file (uploaded or previously parsed):**
Use each measure's `measureTable` (home table) as its domain. Group by distinct home table name — these become the domain buckets. If the model uses display folders, use the top-level folder name instead. Present the derived domain list to the user — they may want to merge small tables (e.g., combine two closely related tables) or rename buckets.

**Option 2 — Live TOM export (TE-free):**
Run `python scripts/export_schema.py` to serialize an open Power BI Desktop model via TOM into the same schema markdown, then derive domains the same way as Option 1. This is the preferred live-capture path when no `.bim` is on hand (one-time setup: `python scripts/pbi_capture/provision_libs.py`).

**Option 3 — Tabular Editor (desktop or CLI):**
If the live TOM export isn't usable, use Tabular Editor to read the model and enumerate measures and their home tables — then derive domains the same way as Option 1.

> **Not used here: the Power BI modeling MCP.** Regression testing needs broad model context, and MCP is costly for large `.bim` enumeration — deriving domains over MCP would burn context for no benefit over the cheaper snapshot/TOM paths above. (This is a workflow-specific exclusion; MCP remains fine for targeted inspection, discovery, and validation elsewhere.)

**Option 4 — Ask the user:**
If none of the above are available, ask: "What are the main subject areas in your model? (e.g., Sales, Inventory, Customers) — I'll use these to group the measure list." Once the user describes the domains, use **semantic interpretation** (not literal keyword/string matching) to propose which measures belong to each domain, then confirm. Naming conventions vary widely — a measure named "Avg Days to Close" may belong to a "Sales" domain even though those exact words don't appear in the name.

*Example for reference:*

| Domain | Home table |
|--------|------------|
| [Domain A] | "[Table A]" |
| [Domain B] | "[Table B]" |
| [Domain C] | "[Table C]" |
| [Domain D] | "[Table D]" |
| [Domain E] | "[Table E]" |
| [Domain F] | "[Table F]" |

**Type classification** (by DAX expression pattern — first match wins):

| Type | Detection rule (regex on DAX expression) |
|------|------------------------------------------|
| Count | Contains `COUNTROWS`, `DISTINCTCOUNT`, `COUNT(`, or name contains "Count" |
| Cost | Contains `SUM(` referencing a column with "Cost" or "Amount" in its name, or name contains "Cost" or "Costs" |
| Average/Ratio | Contains `AVERAGE(`, `AVERAGEX(`, or `DIVIDE(`, or name starts with "Avg" or "Average" |
| Percentage | Name contains "%" or "Pct" or "Percent" or "Proportion" |
| Timeline/Duration | Name contains "Age", "Tenure", "Timeline", "Duration", "Days" |
| Sum (other) | Contains `SUM(` or `SUMX(` not matching cost |
| Other | Everything else |

**Implementation notes:**
- When a `.bim` is available, Claude can automate this classification by parsing measure names and DAX expressions with simple string matching (no regex engine needed — `Contains()` is sufficient).
- When no `.bim` is available, Claude asks the user: "Can you give me a rough breakdown of your measures by domain? For example, how many are [domain A] measures vs. [domain B] measures?"
- These classifications are heuristic. Always present the result for user confirmation — edge cases exist (e.g., a measure may span two domains based on its name or logic).

---

## Phase 2: Design the Test Matrix

### 2.1 Determine Filter Contexts

The filter contexts to test depend on **which dimensions are connected to the affected tables** and **which dimensions users actually slicer by in reports**. Claude should:

1. From the `.bim` or model knowledge, identify all dimensions with active relationships to tables involved in the change
2. Ask the user which of those dimensions are used as slicers in reports
3. Propose a test matrix

**Standard contexts (propose these, then let user adjust):**

| Context | What it tests | When to include |
|---------|--------------|-----------------|
| **Grand total** (no grouping) | Overall aggregation correctness | Always — every measure |
| **Group by primary dimension** | Filter propagation through the main relationship path | Always for Tier 1 & 2 — use the dimension most connected to the changed tables |
| **Group by calendar** (Year or Month) | Time-based filter propagation | When calendar relationships are involved in the change |
| **Cross-dimension** (Dim A × Dim B) | Combined filter interaction | Tier 1 only — when the change affects how multiple dimensions filter a fact table |
| **Specific filter value** (e.g., a known dimension value, a known year) | Spot-check a known-good result | When the user knows what a specific answer should be |

#### Single-Dimension vs. Cross-Product Queries

**Single-dimension queries** (one SUMMARIZECOLUMNS per dimension per measure) are the default. They are safer for memory and make failure isolation trivial — a failing test immediately tells you which dimension caused the break.

**Cross-product queries** (multiple dimensions in one SUMMARIZECOLUMNS) test combined filter interactions that single-dimension queries miss. Use cross-products when:
- The change affects how multiple dimensions filter a fact table simultaneously (e.g., a bridge refactor that changes the filter path from two different dimensions to the same fact table)
- Report pages use multi-slicer layouts where dimensions interact
- The user explicitly requests it

**Cross-product cardinality warning:** Cross-products can exhaust engine memory when high-cardinality dimensions are involved (e.g., a name column × Month × another name column produces millions of rows serialized to JSON). Rules:
- **Always exclude high-cardinality dimensions** (individual name/ID columns, etc.) from cross-products — test those as single-dimension queries
- **Only combine low-cardinality dimensions** (status flags, fiscal year, category/type fields — typically 2–20 distinct values)
- Maximum recommended cross-product: 3 low-cardinality dimensions
- When in doubt, ask the user about dimension cardinality

**Cross-product convention in the config:**

Cross-product context labels use a `_x_` separator in the context name (e.g., `by_dim1_x_year`). In the `group_by_columns` map, the corresponding value uses a `|` pipe separator between DAX column references:

```json
"group_by_columns": {
  "by_dim1_x_year": "'[YourDimTable]'[LowCardinalityColumn]|'Date'[Year]",
  "by_dim1_x_year_x_status": "'[YourDimTable]'[LowCardinalityColumn]|'Date'[Year]|'[StatusTable]'[StatusColumn]"
}
```

The engine splits on `|` and builds a single `SUMMARIZECOLUMNS` with multiple grouping columns.

#### Row caps (`max_rows_per_context`) are rejected for regression capture

For regression capture, `max_rows_per_context` **must be 0** — the Python config validator (`validate_capture` in `scripts/pbi_capture/config.py`) hard-rejects any other value. A row cap is unsafe for value comparison:

- A `TOPN` cap silently truncates dimension combinations, so a delta in a dropped row becomes an undetectable **false pass**.
- The generated `TOPN(N, …)` has no `ORDER BY`, so the kept row set is **unstable across the baseline vs. refactored captures** — you'd diff different rows.

The cap exists only as a *benchmark* knob (timing, not values), where truncation is acceptable. For a fast regression smoke run, use **diagnostic mode** (`--diagnostic` flag or `"diagnostic_mode": true`), which caps the *test count* (first 8 tests), not the rows. So in capture, every cross-product context is a plain `SUMMARIZECOLUMNS` over all its grouping columns — no `TOPN`, no `GENERATE`.

**Column ordering still matters for readability** — when defining cross-product entries in `group_by_columns`, place the lower-cardinality column first (e.g., `StatusFlag (2 values) | Year (5 values)`) so the serialized result groups naturally.

Ask the user:
- "For the primary dimension grouping, should I use ['Table A' / Column A]? What about calendar — ['Date' / Year]?"
- "Any specific filter values you want to pin? For example, a dimension value or fiscal year where you know the expected result?"
- "Are there any dimension combinations your reports use that would exercise the changed relationship paths?"
- "For cross-product tests, which dimensions should I combine? I'd recommend low-cardinality pairs (status flags, category types, fiscal year)."

### 2.2 Handle Edge Cases

Identify and ask about these before generating test queries:

- **TODAY()/NOW() measures:** "These measures use TODAY(): [list]. I'll pin them to a fixed date using TREATAS. What date should I use? (e.g., the last full fiscal month)"
- **Calculation groups:** "Your model has [N] calculation group items. Should I test Tier 1 measures with each calc group item applied (e.g., YTD, MTD, PY), or just the base measure?"
- **RLS:** "Is Row-Level Security active on this model? If so, test results depend on the identity. Which role should I test under?"
- **Large dimensions:** "Do any of your dimensions have high cardinality (e.g., individual entity or item names with 100+ distinct values)? For those, should I group by that dimension for all tiers, or only Tier 1? (Regression capture can't use a row cap — it forbids `TOPN` — so for very large dimensions, restrict the grouping to Tier 1 rather than capping rows.)"

### 2.3 Generate the Capture Config

After user confirmation, Claude writes `output/{label}.config.json` — the executable contract consumed by `scripts/capture_snapshot.py`. It is **pure data**: the engine builds the DAX at runtime from `tests` + `group_by_columns`, so the config stores **no DAX query strings** (this avoids cross-language escaping issues). The same file drives both captures — only the `label` changes, via `--label` at the CLI.

**Schema** (`"workflow": "capture"` — full key reference in [`docs/config-schema.md`](../../../docs/config-schema.md)):

```json
{
  "workflow": "capture",
  "label": "baseline",
  "model_name": "<model name>",
  "output_dir": "output/regression",
  "global_filters": [],
  "max_rows_per_context": 0,
  "query_timeout_ms": 60000,
  "smoke_test_timeout_ms": 10000,
  "memory_threshold_pct": 80,
  "skip_on_smoke_failure": true,
  "diagnostic_mode": false,
  "tests": [
    { "id": "t0001", "measure": "Measure A", "context": "grand_total" },
    { "id": "t0002", "measure": "Measure A", "context": "by_dim" },
    { "id": "t0010", "measure": "Measure A", "context": "by_dim_x_year" }
  ],
  "group_by_columns": {
    "by_dim": "'[DimTable]'[SlicerColumn]",
    "by_year": "'Date'[Year]",
    "by_dim_x_year": "'[DimTable]'[SlicerColumn]|'Date'[Year]"
  }
}
```

**Config rules (enforced by `validate_capture`):**
- **Measure names are bare — no brackets.** Write `"Measure A"`, not `"[Measure A]"`. The engine adds `[...]` itself; a bracketed name is rejected with a clear error.
- **`max_rows_per_context` must be `0`** (the row-cap rule in §2.1 above — TOPN is a benchmark-only knob).
- Every `test.id` must match `[A-Za-z0-9_-]+` and be unique within the file.
- Every `test.context` must be either `"grand_total"` or a key present in `group_by_columns`.
- `global_filters` is a list of DAX boolean expressions (e.g. `["'Date'[Year] = 2025"]`); each is applied as `KEEPFILTERS` inside a `CALCULATE` around the measure — matching a report-level slicer. Identical for both captures.

**Present the config summary to the user before running:**

```
Capture config summary (output/{model}.config.json):
  Tier 1:  8 measures × 5 contexts (3 single + 2 cross-product) = 40 test cases
  Tier 2: 14 measures × 3 contexts                              = 42 test cases
  Tier 3: 67 measures × 1 context (grand_total)                 = 67 test cases
  Total: 149 test cases

  Cross-product contexts:
    by_dim_x_year     ('[DimTable]'[SlicerColumn] | 'Date'[Year])
    by_status_x_year  ('[StatusTable]'[StatusColumn] | 'Date'[Year])

  Row cap: none (regression forbids TOPN — full result sets are compared)

Ready to capture the baseline?
```

---

## Phase 3: Capture Snapshots

### 3.1 Author the Capture Config

Write `output/{label}.config.json` per §2.3 (full key reference in [`docs/config-schema.md`](../../../docs/config-schema.md)). There is **no script to copy or edit** — the capture engine is the `scripts/pbi_capture/` package, invoked via `scripts/capture_snapshot.py`. Per session you produce only the JSON config:

- `model_name` — flows into the snapshot header (`"model_name"`), which `compare-snapshots.py` uses as the report title.
- `tests` — the `{ "id", "measure", "context" }` cases from Phase 2 (bare measure names, no brackets).
- `group_by_columns` — the context→column map from Phase 2 (a single DAX column, or `|`-separated columns for a cross-product).
- Safety knobs (`query_timeout_ms`, `smoke_test_timeout_ms`, `memory_threshold_pct`, `skip_on_smoke_failure`) — leave at defaults unless the user asks; see Safety Limits below.

CLI flags and env vars override config values — precedence is **CLI flag > env var > config file > default** — so one config file drives both the baseline and refactored captures:

| Override | CLI flag | Env var |
|---|---|---|
| Snapshot label | `--label` | `SNAPSHOT_LABEL` |
| Model name | — | `MODEL_NAME` |
| `msmdsrv` port (skip auto-discovery) | `--port` | — |
| Full connection string | `--connection-string` | `CONNECTION_STRING` |
| Diagnostic mode (first 8 tests) | `--diagnostic` | `DIAGNOSTIC_MODE` |
| Output directory | — | `OUTPUT_DIR` |

The engine auto-discovers the local Power BI Desktop `msmdsrv` instance; pass `--port` or `--connection-string` only when several models are open or for an XMLA endpoint.

### 3.2 Run the Captures

Run the same config twice, changing only the label:

```bash
# 1. (optional) dry-run the first few tests to catch config errors early
python scripts/capture_snapshot.py --config output/{model}.config.json --label smoke --diagnostic

# 2. Baseline — connect Power BI Desktop to the ORIGINAL (pre-change) model, then:
python scripts/capture_snapshot.py --config output/{model}.config.json --label baseline

# 3. Apply the change (run the refactor script / swap models), then capture again:
python scripts/capture_snapshot.py --config output/{model}.config.json --label refactored

# 4. Compare:
python scripts/compare-snapshots.py output/regression/baseline.json output/regression/refactored.json
```

Each run writes to `output/regression/` (override with `OUTPUT_DIR` or `output_dir`): `{label}.json` (the snapshot), `{label}-timing.csv`, `{label}-testplan.json`, `{label}-summary.txt`, plus `{label}-errors.log` / `{label}-timeouts.log` when those events occur. Exit code `0` = run completed (per-test errors are recorded as data); `2` = fatal (bad config, no/ambiguous instance, CLR load failure).

**Critical reminder to the user:** "Capture the baseline BEFORE making any changes. If you've already changed the model and have no baseline, revert first — restore from a `.bim`/PBIX backup or undo the change script — then capture."

#### Legacy TE3 output (on request)

The Python path is the default. On explicit request, Claude can emit the retired Tabular Editor script `scripts/legacy-tabular-editor/capture-snapshot.csx` for the user to run in TE3 (press F5) instead — either the raw template, or a copy populated with the session's `modelName`, `testLines` (`id|measure|context`), and `groupByColumns`. The `.csx` writes the same snapshot JSON schema, so `compare-snapshots.py` consumes it identically. Treat this as an opt-in legacy alternative — don't steer users to it unprompted.

---

## Phase 4: Compare Snapshots

### 4.1 The Comparison Script

The project includes `compare-snapshots.py` — a single Python script that performs both **value comparison** (regression testing) and **timing comparison** (performance analysis) in one pass, producing a single `.xlsx` report.

**Dependency:** `openpyxl` (auto-installed on first run if missing). This is the only non-stdlib dependency across the entire toolchain.

**What it does:**
- Loads both snapshot JSON files and joins on test case ID
- Compares every cell value with configurable tolerance (default: 1e-4)
- Handles `__BLANK__` sentinel correctly (BLANK ≠ 0 ≠ null ≠ empty string)
- Compares timing side-by-side with configurable regression/improvement thresholds
- Adds a **Delta** flag (Y/N) to every test case indicating whether values changed
- Produces a single formatted `.xlsx` with six sheets
- Prints a combined console summary covering both values and timing
- Exit code: 0 = all value tests pass, 1 = value failures (timing regressions don't affect exit code). Exit code 1 now also triggers when any test has `status:"timeout"` in the refactored snapshot that was `"ok"` in the baseline (a new hang = a regression).

**Cross-product comparison notes:**
- Cross-product results contain multiple grouping columns. The script sorts by ALL non-measure columns before row-by-row comparison, so DAX's unordered output doesn't cause spurious deltas.
- Row count mismatches on cross-product tests are flagged clearly as Delta=Y with detail "Row count: N → M".

**Usage:**

```
Prerequisites:
- Python 3.10+
- openpyxl (declared in requirements.txt: `pip install -r requirements.txt`)

To run:
1. Open a terminal in the folder containing both JSON files
2. Run: python compare-snapshots.py baseline.json refactored.json
3. Optional: python compare-snapshots.py baseline.json refactored.json --output my-report.xlsx
```

### 4.2 Report Format

**Output: `regression-report.xlsx`** (6 sheets)

| Sheet | Purpose |
|-------|---------|
| **All Tests** | Every test case joined on ID. Columns: Test ID, Measure, Context, **Delta (Y/N)**, Delta Detail, Baseline ms, Refactored ms, Δ ms, Δ %, Timing Verdict, row counts. Delta=Y rows sort to top. Filter on Delta column for regression review, or sort by Δ ms for performance analysis. |
| **Value Deltas** | Cell-level mismatch detail — only rows where Delta=Y. Shows row key, column, baseline value, refactored value. Empty if all tests pass. |
| **By Measure** | Timing + value deltas aggregated per measure. Shows total/avg ms, delta counts, regression/improvement counts. Sorted by timing delta descending. |
| **By Context** | Same aggregation by context type (grand_total, by_year, cross-products, etc.). Reveals which filter dimensions are most affected by the change. |
| **Top Movers** | Top 20 timing regressions + top 20 improvements at a glance. Includes Delta flag for cross-reference. |
| **Timeout Regressions** | Tests that newly timed out in the refactored snapshot vs. baseline. Includes direction (regression vs. improvement), timing, and truncated DAX for immediate investigation. |

**Conditional formatting:**
- Delta = Y → red background; Delta = N → green background
- Timing verdict REGRESSION → red; IMPROVEMENT → green
- Value delta counts > 0 → red highlight in aggregation sheets

**Console output (summary):**

```
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  Regression Test Report
  Model: Sales
  Baseline:   2026-03-26T10:05:00
  Refactored: 2026-03-26T10:22:00
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

  VALUE COMPARISON
    ✅ Pass:               115 / 119
    ❌ Fail:                 2 / 119
    🔢 Row count:            0 / 119
    🔲 Missing:              0 / 119
    ⚠️  Errors:               2 / 119
    ── Delta = Y:            4 / 119

  TIMING COMPARISON
    Baseline total:       142,300 ms (142.3s)
    Refactored total:      98,450 ms (98.5s)
    Δ total:              -43,850 ms (-43.9s)
    Δ overall:              -30.8%
    Regressions:             3
    Improvements:           28

  Output: /path/to/regression-report.xlsx
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
```

---

## Phase 5: Interpret and Act on Results

### 5.1 Triage Failures

#### JSON Result Status Values (v8+)

| Status | Meaning | Delta flag in comparison |
|---|---|---|
| `"ok"` | Query succeeded | `N` (unless values differ) |
| `"error"` | Query threw an exception | `Y` |
| `"timeout"` | Query was cancelled mid-flight — either wall-clock (`queryTimeoutMs`) OR sustained memory pressure (memory watchdog) | `Y` (regression if newly timed out) |
| `"skipped"` | Measure failed the pre-flight smoke test (one record per dimension permutation for that measure) | `Y` (delta vs a passing baseline) |
| `"aborted_memory"` | Whole run halted due to between-test memory threshold check | `Y` |

**Why the `"timeout"` status is overloaded:** the per-query memory watchdog and the wall-clock timeout both surface as `"timeout"` in JSON because both abort the in-flight query (`cmd.Cancel()`, then a `conn.Dispose()` backstop) and return the same status from the executor. The two paths are distinguished by the `Type:` tag in `{label}-timeouts.log`:
- `Type: query_timeout` → wall-clock exceeded `queryTimeoutMs`
- `Type: memory_watchdog` → memory pressure sustained for 1.5s mid-query
- `Type: smoketest_timeout` / `smoketest_error` → smoke test failure (also surfaces in `skippedMeasures`)
- `Type: query_error` → defensive fallback (rare; status="timeout" but neither pattern matched)

**Triage guide for timeouts and smoke failures (`{label}-timeouts.log`):**

Each entry has this format:
```
t0081 | [Measure A] | by_dim1_x_month | 2547ms
  Type: memory_watchdog
  Reason: memory threshold 80% sustained for 1500ms mid-query (cancelled by watchdog)
  DAX: <full query>
```

- Smoke-test failures use synthetic IDs `s0001..sNNNN` and context `smoke_test`. They appear in the same log as runtime timeouts.
- The `Type:` tag tells you which watchdog tripped — read it before pasting the DAX into DAX Studio.
- For `memory_watchdog`: the measure isn't necessarily slow; it's allocating too much engine memory. Look for unbounded iterators (`SUMX` over millions of rows), Cartesian fan-out, or relationship paths that materialize huge intermediates.
- For `query_timeout`: paste the DAX into DAX Studio with Server Timings on. Common causes: broken CROSSFILTER paths, iterator over 500K+ row tables without early filtering, implicit bidir scans.
- For `smoketest_*`: a smoke failure on a measure that WAS passing before the refactor means the refactor broke the measure's basic syntax or a dependency it references. Fix the measure, re-run.
- Fix the measure DAX, re-run the regression suite — the timeout/skip entry should disappear.

**Note on the `skipped` status:** When a measure fails smoke testing, the script marks it skipped and writes one `"status": "skipped"` JSON record per dimension permutation that measure had in the test plan (e.g., 5 contexts → 5 skipped records). All of those share the same `skip_reason`, derived from the smoke result. The smoke loop itself only emits one `Type: smoketest_*` line in the timeouts log per failed measure (not per permutation).

When failures are found, Claude helps interpret them:

- **BLANK → 0 changes:** Usually means a relationship path that previously returned no match (BLANK) now returns a match through a new direct FK. This can be correct or incorrect depending on intent. Ask the user: "Is it expected that [entity name] now returns 0 instead of BLANK for this measure? This could mean the new FK found a matching row where the bridge previously didn't."
- **Row count mismatches:** Almost always a broken filter propagation path. A dimension value that was reachable through the old relationship topology is no longer reachable. This is a regression. For cross-product tests, also check whether a partition value itself disappeared (e.g., a Year that was reachable via the old bridge path is no longer reachable).
- **Numeric differences (with zero tolerance):** If the DAX logic was truly equivalent, these shouldn't happen. Investigate whether the difference traces to the relationship change or to a DAX rewrite error.
- **Expected intentional changes:** The user may have intentionally corrected bugs (e.g., adding weighting logic that didn't exist before, fixing a table interaction). These will show as legitimate value differences, not row count mismatches. The user should triage these manually from the diff CSV.
- **Cross-product specific:** If a cross-product test fails but its constituent single-dimension tests pass, the issue is in the filter interaction — both dimensions filter correctly in isolation but break when combined. This is the key scenario cross-product tests are designed to catch.
- **Timeout (newly):** Measure was fast before the refactor but now hangs → the refactor changed a relationship or filter path that caused the engine to scan a much larger table. Check for new bidir relationships, removed CROSSFILTER restrictions, or a removed inactive relationship that now activates an expensive path.

### 5.2 Iterate

If failures are found:
1. Fix the issue (DAX or relationship correction)
2. Re-run the capture script (only the refactored snapshot needs to refresh)
3. Re-run the comparison
4. Repeat until all tests pass

### 5.3 Document

Once all tests pass, trigger the session learning loop (see `CLAUDE.md`):
- Record the test results as validation evidence for the refactor
- Note any unexpected findings (e.g., "BLANK → 0 on 4 dimension values was expected because the new direct FK covers cases the bridge missed")
- Update `{model}-gotchas.md` if new edge cases were discovered

---

## Engine, Config & Comparison Reference

### What Claude authors per session (capture config)

Claude produces only `output/{label}.config.json` — never engine code. The fields it fills:

- `model_name` — surfaced in the snapshot header (`"model_name": "..."`) for the `compare-snapshots.py` report title.
- `tests` — `{ "id", "measure", "context" }` objects. `id` matches `[A-Za-z0-9_-]+` and is unique; `measure` is **bare** (no brackets); `context` is `"grand_total"` or a `group_by_columns` key.
- `group_by_columns` — context→DAX-column map. A single column, or `|`-separated columns for a cross-product.
- `global_filters` — list of DAX boolean expressions, each wrapped as `KEEPFILTERS` around the measure.
- `max_rows_per_context` — **must be 0** for capture (TOPN is a benchmark-only knob; see §2.1).

The engine (`scripts/pbi_capture/`) builds the DAX, runs it under the safety stack, and serializes the snapshot. Do not reimplement any of it.

### Safety Limits

The capture engine runs a layered safety stack — pre-flight smoke test, per-query wall-clock timeout, mid-query memory watchdog (with debounce), and a between-test memory check. All are on by default. Set them via config keys (or the env-var equivalents).

| Config key | Default | Env var | Description |
|---|---|---|---|
| `query_timeout_ms` | `60000` | `QUERY_TIMEOUT_MS` | Per-query wall-clock cap. A watchdog thread polls every 500 ms; on expiry the engine cancels the query (`cmd.Cancel()`, then a `conn.Dispose()` backstop that interrupts even pure formula-engine queries). The Python watchdog is the **sole** timeout enforcement — server-side `CommandTimeout` is deliberately `0`. |
| `smoke_test_timeout_ms` | `10000` | `SMOKE_TEST_TIMEOUT_MS` | Pre-flight smoke test cap per unique measure (`EVALUATE ROW("r", [M])`). **Mechanism check:** set to 200–500 ms to force most measures to "fail" — the run short-circuits with `"status": "skipped"` rows and `Type: smoketest_*` log entries, verifying the pipeline end-to-end. |
| `memory_threshold_pct` | `80` | `MEMORY_THRESHOLD_PCT` | Watchdog trip point as a true % of **actual** total RAM (read via `GlobalMemoryStatusEx` — no hard-coded denominator, so no per-machine scaling needed). The mid-query watchdog requires 3 consecutive critical polls (3 × 500 ms = 1.5 s sustained) before cancelling, so transient spikes don't abort legitimate queries. The between-test check (`aborted_memory`) has no debounce — a single critical reading aborts the run. Set to 0 to disable. |
| `skip_on_smoke_failure` | `true` | `SKIP_ON_SMOKE_FAILURE` | When `true`, measures failing the smoke test are skipped (`status:"skipped"` per permutation). When `false`, they still attempt to run and rely on the wall-clock + memory watchdogs. |
| connection | *(auto-discovered)* | `CONNECTION_STRING` | The engine auto-discovers the local `msmdsrv` instance and its single catalog. Multiple open instances → it fails with an actionable list; disambiguate with `--port` or `--connection-string` / `CONNECTION_STRING`. |

**Smoke-skip mechanics:** a failed smoke test writes (a) one `Type: smoketest_*` entry per failed measure in `{label}-timeouts.log`, and (b) one `"status": "skipped"` record per dimension permutation in the snapshot — all sharing the same `skip_reason`, zero duration. The skip is *per measure, not per test*.

**XMLA note:** the Power BI gateway enforces its own ~225 s query cap, so `query_timeout_ms > 225000` has no effect against the service; the memory watchdog is also ineffective in XMLA mode (model memory lives on the Fabric capacity).

### Comparison (`scripts/compare-snapshots.py`)

A stable, tested script — not generated per session. It performs value comparison and timing analysis in one pass.

- **Dependency:** `openpyxl` (auto-installed on first run if missing) — the only non-stdlib dependency in the toolchain.
- **Input:** two snapshot JSONs (baseline + refactored) from `capture_snapshot.py`.
- **Output:** single `regression-report.xlsx` with 6 sheets (All Tests, Value Deltas, By Measure, By Context, Top Movers, Timeout Regressions).
- Reads JSON as `utf-8-sig` (Windows BOM); sorts rows by all non-measure columns before comparing (DAX doesn't guarantee order).
- CLI: `python scripts/compare-snapshots.py <baseline.json> <refactored.json> [--output report.xlsx]`.
- Exit code: 0 = all value tests pass; 1 = value failures, **or** any test that is `status:"timeout"` in refactored but was `"ok"` in baseline (a new hang = a regression).
- **Delta flag** per test (Y/N) enables filtering for regression review (Delta=Y) or performance review (sort by Δ ms).
- Configurable thresholds at the top of the script: `NUMERIC_TOLERANCE`, `REGRESSION_PCT`, `IMPROVEMENT_PCT`, `MIN_MS_FOR_PCT`.

### DAX the engine builds (for context when discussing results)

- Queries are **bare table expressions**; the engine prepends `EVALUATE` at the call site. (The smoke query is the exception — `EVALUATE ROW("r", <ref>)`.)
- Grouped measures use `SUMMARIZECOLUMNS` (auto-removes blank rows, matching report-visual behavior), not `ADDCOLUMNS` over `VALUES`.
- The measure reference is **never** wrapped in `IGNORE()` — it's invalid inside `ROW()` and adds nothing inside `SUMMARIZECOLUMNS`.
- `global_filters` wrap the measure as `CALCULATE([M], KEEPFILTERS(<f>), …)`, matching a report-level slicer.
- Cross-products put all grouping columns in one `SUMMARIZECOLUMNS`. **No `TOPN`/`GENERATE` in capture** — the row cap is rejected, so full result sets are compared.

---

## Adapting to Different Change Types

This skill adapts its test strategy based on the type of change. Here are common patterns:

### Relationship Changes (add/remove/modify)
- **Focus:** Filter propagation — does every dimension still reach every fact table correctly?
- **Key test:** Group by each dimension connected to the changed relationship, check for row count parity and BLANK/non-BLANK parity
- **Cross-product value:** High — relationship changes are most likely to cause combined-filter regressions. Include at least one cross-product test pairing the changed relationship's dimension with another high-use dimension.
- **Gotcha:** Changing bidir to single can break measures that relied on implicit back-filtering

### Measure Rewrites (DAX changes)
- **Focus:** Numerical parity — does the new DAX produce the same numbers?
- **Key test:** Grand total + grouped by each dimension used in the measure's typical report context
- **Cross-product value:** Medium — only needed if the rewrite changed how the measure interacts with filter context
- **Gotcha:** Measures using `CALCULATE` with `KEEPFILTERS` vs `ALL` behave differently under external filters

### Calc Column Additions
- **Focus:** Downstream measures that use the calc column — do they produce correct results?
- **Key test:** Measures that previously used runtime lookups (e.g., `RELATED()`, `LOOKUPVALUE()`) and now use the pre-computed calc column
- **Cross-product value:** Low — calc column changes rarely affect filter interactions
- **Gotcha:** Calc column type mismatches (e.g., TEXT vs DECIMAL for a percentage column — requires VALUE() conversion)

### Calculation Group Changes
- **Focus:** All measures that the calc group modifies
- **Key test:** Each affected measure with each calc group item applied as a filter
- **Cross-product value:** Medium — test calc group items in combination with key slicers
- **Gotcha:** Calc group precedence — if multiple calc groups exist, test the combination

### Table Structure Changes (new tables, removed tables, column type changes)
- **Focus:** Measures on the changed table + measures that reference it through relationships
- **Key test:** Grand total + grouped by dimensions that filter the changed table
- **Cross-product value:** Medium to high depending on how many relationship paths changed
- **Gotcha:** Column type changes (e.g., INTEGER → TEXT) can silently break `RELATED()` joins

---

## Quick Reference: User Interaction Points

At minimum, the user must confirm these before Claude generates the capture config:

| Decision Point | What Claude proposes | User confirms or adjusts |
|---|---|---|
| Change scope | Parsed from description or .bim diff | "Yes, that's the full scope" |
| Measure tiers | Auto-classified via dependency graph | "Move X to Tier 1, skip Y" |
| Measure selection method | Explicit list, random sample, or domain/type filter | "Random sample of 20" or "All cost measures" |
| Tier 2 inclusion | Include or exclude transitive dependents | "Just Tier 1" or "Include Tier 2" |
| Filter contexts | Dimensions identified from model structure | "Add Region, skip Portfolio" |
| Query approach | Single-dimension, cross-product, or both | "Single + one cross-product" |
| Cross-product columns | Low-cardinality dimension pairs | "StatusFlag × Year, Category × Year" |
| Specific filter values | Suggested based on model knowledge | "Use FY2025, [entity] = [known value]" |
| Edge cases (TODAY, RLS, calc groups) | Identified from DAX patterns | "Pin to 2025-12-31, skip RLS" |
| Capture config summary | Count of test cases by tier | "Looks good, run the baseline" |

---

## File Outputs

Capture writes to `output/regression/` by default (override with `output_dir` / `OUTPUT_DIR`):

| File | Source | Purpose |
|---|---|---|
| `output/{label}.config.json` | Claude (per session) | The capture contract: `tests` + `group_by_columns` + safety knobs (no embedded DAX). One file drives both captures via `--label`. |
| `{label}.json` | `capture_snapshot.py` | Snapshot of model results (per-test values + timing). |
| `{label}-testplan.json` | `capture_snapshot.py` (pre-flight) | Planned test order, written before execution; survives a force-kill so you can see which test was in flight. |
| `{label}-timing.csv` | `capture_snapshot.py` | Lightweight per-test-case timing. |
| `{label}-summary.txt` | `capture_snapshot.py` | The run summary (also printed to stdout); analyze with `Grep` + targeted `Read`. |
| `{label}-errors.log` | `capture_snapshot.py` (if errors) | Full exception details per failed test case. |
| `{label}-timeouts.log` | `capture_snapshot.py` (if timeouts/smoke failures) | One entry per timeout/smoke-failure with `Type:` (memory_watchdog \| query_timeout \| smoketest_timeout \| smoketest_error \| query_error), `Reason:`, and full DAX. |
| `regression-report.xlsx` | `compare-snapshots.py` | Unified report: value deltas + timing comparison in one workbook (6 sheets). |

`scripts/capture_snapshot.py` (+ the `scripts/pbi_capture/` engine) and `scripts/compare-snapshots.py` are stable, tested code — never edited per session. Claude only authors the JSON config.

**End-to-end workflow:**

```bash
# capture both sides from one config, then compare
python scripts/capture_snapshot.py --config output/{model}.config.json --label baseline
python scripts/capture_snapshot.py --config output/{model}.config.json --label refactored
python scripts/compare-snapshots.py output/regression/baseline.json output/regression/refactored.json
# open regression-report.xlsx — filter Delta=Y for value regressions, sort by Δ ms for performance
```

**Legacy TE3 (on request):** Claude can instead emit `scripts/legacy-tabular-editor/capture-snapshot.csx` (raw or populated) to run in TE3; it writes the same snapshot schema, so the comparison step is identical. Opt-in only.
