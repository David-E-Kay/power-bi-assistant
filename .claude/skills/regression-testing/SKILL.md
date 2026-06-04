---
name: regression-testing
description: "Use this skill when the user asks to 'test a refactor', 'regression test', 'validate model changes', 'compare before and after', 'capture baseline', 'snapshot measures', 'diff results', 'ensure parity', or any request involving verifying that a Power BI semantic model produces identical results after DAX edits, relationship changes, calc column additions, or structural refactors. Covers the full lifecycle: change scope analysis, measure tiering (including random sampling and domain/type filtering), test case generation (single-dimension and cross-product contexts with per-group TOPN), baseline capture, post-refactor capture, and comparison. Uses capture-snapshot.csx as a tested template — only the test case list and dimension map are generated on demand; the DAX construction block is baked into the template."
---

# Regression Testing for Power BI Model Refactors

A conversational skill that guides Claude through planning, generating, and executing regression tests for Power BI semantic model changes. The capture script uses `scripts/capture-snapshot.csx` as a tested template (read-only — copy to `output/{label}.csx` per session, edit the copy). Only the test case list (`testLines`) and dimension map (`groupByColumns`) are generated on demand based on the specific model, change scope, and user confirmation. The DAX construction block is baked into the template and handles single-dimension, cross-product, and TOPN-per-group patterns automatically based on the `groupByColumns` entries. The Python comparison script is generated fresh each time.

## When to Use This Skill

Trigger when:
- The user is about to refactor model relationships, DAX measures, or table structure
- The user asks to validate that a change didn't break anything
- The user wants to capture a baseline before making changes
- The user asks to compare results between an original and modified model
- The session has produced structural changes and needs post-refactor validation (Phase 5 of `refactor-strategy/SKILL.md`)

Do NOT use for:
- General DAX debugging (use `pbi-dax-patterns.md` or `mc-dax-performance.md`)
- Performance benchmarking only (use DAX Studio traces directly)
- Report-level visual testing (this skill tests the semantic model layer)

## Core Principles

1. **Never generate test scripts without understanding the change.** The test suite is shaped entirely by what changed. A bridge refactor needs different tests than a measure rewrite.
2. **Always confirm with the user.** Every decision point — change scope, measure tiers, filter contexts, edge cases — requires user confirmation before generating artifacts.
3. **Zero tolerance by default.** Baseline and refactored models are offline copies of the same data. Results must match exactly. BLANK ≠ 0 ≠ null — these are semantically distinct in DAX and must be compared as such.
4. **Use the tested template.** The capture script at `scripts/capture-snapshot.csx` is a proven, working template (read-only — copy to `output/{label}.csx` per session). Claude MUST use it as the base and only replace two sections on demand: `testLines` and `groupByColumns`. The DAX construction block is baked into the template and handles single-dimension, cross-product, and TOPN-per-group patterns automatically. All other code — helpers, execution engine, JSON serialization, error handling, diagnostic mode, Teams webhook, summary report — is copied verbatim from the template. Do NOT regenerate these sections from scratch; doing so risks introducing bugs in code that has already been tested and validated.

---

## Phase 1: Understand the Change Scope

Before generating any test, Claude must understand two things: **what the model looks like** and **what is changing**.

### 1.1 Establish the Model Context

Check what Claude already knows:
- Search `pbi-models.md` for the model name and structure
- Check memory edits for known gotchas, relationship patterns, or calc column rules
- If a `.bim` file is available (uploaded or previously parsed), use it for structural truth

If the model is unknown, ask the user:
- Which model is this? (name, domain)
- Can you provide the `.bim` file? (preferred — enables automated dependency analysis)
- If no `.bim`, ask for a summary: key fact tables, dimensions, bridge tables, and the approximate measure count

### 1.2 Determine the Change Scope

The change scope drives everything downstream. Accept it via **one of two paths**:

**Path A — User describes the change explicitly:**
The user says something like "I'm adding a direct FK from Work Orders to Properties and changing the bridge to single-direction." Claude extracts:
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
│ Rels (+) │ Work Orders → Properties (active, single, M:1)  │
│ Rels (~) │ Bridge bidir → single direction                  │
│ Rels (-) │ Bridge → Properties (deleted)                    │
│ Measures │ 8 measures with modified DAX                     │
│ Columns  │ +1 calc column on Open Work Orders (_OwnerPct)   │
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
│  1   │   8   │ Avg Open WO Age DK, Open WO Count Prop...  │
│  2   │  14   │ Avg Open WO Age DK (YTD), WO Cost Prop... │
│  3   │  67   │ Total Budget, Leasing Revenue...            │
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
│ Work Order  │   7   │ WO Count by Completed Date, ...      │
│ Project     │   5   │ Project Approved Cost Base, ...       │
│ Open WO     │   4   │ Open Work Orders Count DK, ...       │
│ Property    │   2   │ Avg Daily Open Properties %, ...      │
│ Other       │   2   │ Average Resident Tenure, ...          │
└─────────────┴───────┴──────────────────────────────────────┘

Want to adjust the sample size or swap any measures?
```

4. **Seed consistency** — When generating the random sample in conversation, Claude should present a deterministic selection (e.g., alphabetically first N from each stratum) rather than truly random, so the user sees the same proposal if they ask again. True randomness in the C# script is not needed — the selection happens at the conversation level.

#### 1.4b Domain or Type Filtering (user specifies a category)

When the user says "test all cost measures," "test property measures," "test the count measures," or similar:

1. **Parse the request** into domain filter, type filter, or both:
   - "cost measures" → type = cost
   - "property measures" → domain = property
   - "project count measures" → domain = project AND type = count

2. **Filter the measure list** from the `.bim` using the classification rules below.

3. **Present the filtered list** for confirmation — the user may want to exclude some.

4. **Apply tier logic on top** — Even within a filtered set, Claude should note which are Tier 1 vs. Tier 2/3 relative to the change scope (if a change scope has been established).

#### 1.4c Domain and Type Classification Rules

These rules classify measures from the model. They use the measure's home table (display folder or `measureTable` field in the `.bim`) and the measure name + DAX expression.

**Domain classification** (derived from model structure — not hardcoded):

Establish the model context using the best available source, in priority order:

**Option 1 — `.bim` file (uploaded or previously parsed):**
Use each measure's `measureTable` (home table) as its domain. Group by distinct home table name — these become the domain buckets. If the model uses display folders, use the top-level folder name instead. Present the derived domain list to the user — they may want to merge small tables (e.g., combine two closely related tables) or rename buckets.

**Option 2 — MCP server (`powerbi-modeling-mcp.exe`):**
Before using this path, confirm the MCP server is installed and connected. If uncertain, ask: "Do you have the Power BI modeling MCP server running? You can check by running `Get-Process powerbi-modeling-mcp` in a terminal, or by looking for it in your Claude Code MCP settings." If confirmed, use the MCP `model_operations` / `measure_operations` tools to enumerate all measures and their home tables — then derive domains the same way as Option 1.

**Option 3 — Tabular Editor CLI (future, not yet available):**
TE CLI will support headless read operations against Power BI models without a `.bim` file, enabling automated measure/column enumeration. When available, it will be equivalent to Option 1 for domain derivation. For now, note this as a future path and fall back to Option 4.

**Option 4 — Ask the user:**
If none of the above are available, ask: "What are the main subject areas in your model? (e.g., Work Orders, Projects, Occupancy Units) — I'll use these to group the measure list." Once the user describes the domains, use **semantic interpretation** (not literal keyword/string matching) to propose which measures belong to each domain, then confirm. Naming conventions vary widely — a measure named "Avg Days Open" may belong to a "Work Orders" domain even though those exact words don't appear in the name.

*Example for reference (M&C model only):*

| Domain | Home table |
|--------|------------|
| Open WO | "Open Work Orders" |
| Work Order | "Work Orders" |
| Project | "Projects" |
| Property | "Properties" |
| PO Detail | "PO Detail" |
| Vendor | "Vendors" |

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
- When no `.bim` is available, Claude asks the user: "Can you give me a rough breakdown of your measures by domain? For example, how many are work order measures vs. project measures?"
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
| **Specific filter value** (e.g., a known property, a known year) | Spot-check a known-good result | When the user knows what a specific answer should be |

#### Single-Dimension vs. Cross-Product Queries

**Single-dimension queries** (one SUMMARIZECOLUMNS per dimension per measure) are the default. They are safer for memory and make failure isolation trivial — a failing test immediately tells you which dimension caused the break.

**Cross-product queries** (multiple dimensions in one SUMMARIZECOLUMNS) test combined filter interactions that single-dimension queries miss. Use cross-products when:
- The change affects how multiple dimensions filter a fact table simultaneously (e.g., a bridge refactor that changes the filter path from two different dimensions to the same fact table)
- Report pages use multi-slicer layouts where dimensions interact
- The user explicitly requests it

**Cross-product cardinality warning:** Cross-products can cause OutOfMemoryException in TE3 when high-cardinality dimensions are involved (e.g., Vendor Name × Calendar Month × Property produces millions of rows serialized to JSON). Rules:
- **Always exclude high-cardinality dimensions** (Vendor Name, Property Name, etc.) from cross-products — test those as single-dimension queries
- **Only combine low-cardinality dimensions** (status flags, fiscal year, category/type fields — typically 2–20 distinct values)
- Maximum recommended cross-product: 3 low-cardinality dimensions
- When in doubt, ask the user about dimension cardinality

**Cross-product convention in the template:**

Cross-product context labels use a `_x_` separator in the test case ID (e.g., `by_dim1_x_year`). In the `groupByColumns` dictionary, the corresponding value uses a `|` pipe separator between DAX column references:

```csharp
// Generic form — replace with columns from your model:
{ "by_dim1_x_year", "'[YourDimTable]'[LowCardinalityColumn]|'Calendar'[YearColumn]" },
{ "by_dim1_x_year_x_status", "'[YourDimTable]'[LowCardinalityColumn]|'Calendar'[YearColumn]|'[StatusTable]'[StatusColumn]" },
// M&C example:
// { "by_same_home_x_year", "'Properties'[Property Current Same Home Reporting]|'Calendar'[Start of Year]" },
```

The DAX construction block splits on `|` and builds the appropriate SUMMARIZECOLUMNS with multiple grouping columns.

#### TOPN Behavior: Single vs. Cross-Product

When `maxRowsPerContext > 0`:

- **Single-dimension contexts**: TOPN wraps the entire SUMMARIZECOLUMNS, returning the top N rows overall. This matches the existing v8 template behavior.

- **Cross-product contexts**: TOPN applies **per partition group** — the first column in the `|`-separated list is the partition column, and TOPN is applied within each partition value. This ensures the test exercises the measure under each value of the primary grouping dimension rather than just returning the top N rows overall (which might all come from one partition value).

  **DAX pattern for TOPN-per-group:**
  ```dax
  GENERATE(
      VALUES('[PartitionDimTable]'[PartitionColumn]),
      TOPN(5,
          SUMMARIZECOLUMNS(
              '[DetailDimTable]'[DetailColumn],
              "Result", [Measure]
          )
      )
  )
  // M&C example: partition = 'Properties'[Property Current Same Home Reporting], detail = 'Calendar'[Start of Year]
  ```

  The first column (the partition column) serves as the outer loop. For each distinct value, TOPN returns the top N rows from the remaining dimensions. This validates that the combined filter context produces correct results across different partition slices.

  **Column ordering matters** — when defining cross-product entries in `groupByColumns`, place the lower-cardinality / more-important-to-partition-by column first. Example: `StatusFlag (2 values) | Year (5 values)` → partitions by StatusFlag, top N years within each.

Ask the user:
- "For the primary dimension grouping, should I use [DimProperty / PropertyName]? What about calendar — [DimCalendar / FiscalYear]?"
- "Any specific filter values you want to pin? For example, a property or fiscal year where you know the expected result?"
- "Are there any dimension combinations your reports use that would exercise the changed relationship paths?"
- "For cross-product tests, which dimensions should I combine? I'd recommend low-cardinality pairs (status flags, category types, fiscal year)."

### 2.2 Handle Edge Cases

Identify and ask about these before generating test queries:

- **TODAY()/NOW() measures:** "These measures use TODAY(): [list]. I'll pin them to a fixed date using TREATAS. What date should I use? (e.g., the last full fiscal month)"
- **Calculation groups:** "Your model has [N] calculation group items. Should I test Tier 1 measures with each calc group item applied (e.g., YTD, MTD, PY), or just the base measure?"
- **RLS:** "Is Row-Level Security active on this model? If so, test results depend on the identity. Which role should I test under?"
- **Large dimensions:** "Do any of your dimensions have high cardinality (e.g., individual property or entity names with 100+ distinct values)? For those, should I group by that dimension for all tiers, or only Tier 1? (Tier 2+ could use a TOPN cap to keep result sets manageable)"

### 2.3 Generate the Test Manifest

After user confirmation, Claude generates a `test-manifest.json` that records every test case. This file is the contract between the capture script and the comparison script.

**Schema:**

```json
{
  "manifest_version": "2.0",
  "model_name": "<model name>",
  "change_description": "<brief description of what changed>",
  "created": "2026-03-26T10:00:00Z",
  "tolerance": {
    "numeric": 0,
    "blank_equals_zero": false
  },
  "filter_fields": [
    {"col": "'[DimTable]'[SlicerColumn]", "label": "by_dim"},
    {"col": "'Calendar'[YearColumn]",     "label": "by_year"}
  ],
  "cross_products": [
    {
      "label": "by_dim_x_year",
      "columns": [
        "'[DimTable]'[SlicerColumn]",
        "'Calendar'[YearColumn]"
      ],
      "topn_partition_col": "'[DimTable]'[SlicerColumn]"
    }
  ],
  "_note": "M&C example: filter_fields use Properties[Property Current Same Home Reporting] and Calendar[Start of Year].",
  "measure_selection": {
    "method": "explicit | random_sample | domain_filter | type_filter",
    "sample_size": 20,
    "domain_filter": null,
    "type_filter": null,
    "seed_description": "Stratified sample: 7 WO, 5 Project, 4 OWO, 2 Property, 2 Other"
  },
  "test_cases": [
    {
      "id": "t001",
      "measure": "Avg Open WO Age DK",
      "measure_table": "Work Orders",
      "tier": 1,
      "context": "grand_total",
      "description": "Grand total — no filter context"
    },
    {
      "id": "t002",
      "measure": "Avg Open WO Age DK",
      "measure_table": "Work Orders",
      "tier": 1,
      "context": "by_same_home",
      "description": "Grouped by <dim> — tests filter propagation through direct FK"
    },
    {
      "id": "t010",
      "measure": "Avg Open WO Age DK",
      "measure_table": "Work Orders",
      "tier": 1,
      "context": "by_dim_x_year",
      "description": "Cross-product: <dim> × Year — tests combined filter interaction"
    }
  ]
}
```

**Note:** The manifest stores measure names and context labels, NOT pre-built DAX strings. DAX queries are constructed at runtime in the capture script to avoid string escaping issues between Python/JSON and C#.

**Present the manifest summary to the user before generating scripts:**

```
Test Manifest Summary:
  Tier 1: 8 measures × 5 contexts (3 single + 2 cross-product) = 40 test cases
  Tier 2: 14 measures × 3 contexts (2 single + 1 cross-product) = 42 test cases
  Tier 3: 67 measures × 1 context = 67 test cases
  Total: 149 test cases

  Cross-product contexts:
    by_same_home_x_year (2 cols, ~10 rows per measure)
    by_status_x_year (2 cols, ~25 rows per measure)

  TOPN: 5 per group (cross-products), 5 overall (single-dimension)

Estimated execution time: ~3-5 minutes in Tabular Editor
(based on ~1-2s per EvaluateDax call)

Ready to generate the capture script?
```

---

## Phase 3: Capture Snapshots

### 3.1 Generate the Capture Script from Template

> **READ-ONLY TEMPLATE — copy first, edit second.**
> `scripts/capture-snapshot.csx` is a tested template and MUST NOT be edited
> in place. Always copy it to `output/{label}.csx` (e.g.
> `output/mc-baseline.csx`, `output/occupancy-refactored.csx`) and apply
> session-specific edits to the **copy**. The user runs the copy in
> `output/`; the template stays untouched so future sessions inherit the
> verified version. If you find yourself about to edit
> `scripts/capture-snapshot.csx` directly, stop — copy it first.

Claude MUST use `scripts/capture-snapshot.csx` as the template. **Do NOT generate the capture script from scratch.** The template contains tested, working code for all boilerplate sections including the DAX construction block (which handles single-dimension, cross-product, and TOPN-per-group patterns automatically). Claude's job is to read the template, copy it to `output/`, and replace only these four sections in the copy:

**Section 1 — Header `PURPOSE` comment + test-case count:**
The placeholder at the top of the script reads:

```csharp
// PURPOSE:  <Set per session — describes which model and refactor this
//            snapshot validates.>
//           <N test cases — set per session.>
```

Replace it with the session's actual purpose and test-case total. Example:

```csharp
// PURPOSE:  Capture regression test snapshot for M&C ownership percentage fix.
//           96 test cases (12 Tier 1 measures × 8 contexts incl. 2 cross-products).
```

Both this comment and the `modelName` default (Section 2 below) describe the same scope from different angles — keep them in sync.

**Section 2 — `modelName` (config var at top of script):**
Replace the default value of `var modelName = ... ?? "<MODEL NAME — replaced per session by regression-testing skill>";` with the actual model name derived from the conversation (or explicitly stated by the user). Example:

```csharp
var modelName = System.Environment.GetEnvironmentVariable("MODEL_NAME")
    ?? "Occupancy";
```

The `MODEL_NAME` env var override stays — it lets the user change the name per-run via the CLI without editing the script. The value flows into the JSON snapshot header (`"model_name": "Occupancy"`) which `compare-snapshots.py` reads for the report title.

**Section 3 — `testLines` (test case definitions):**
Replace the `var testLines = new List<string> { ... };` block with the test cases generated from Phase 2. Each line follows the format `id|measure|context`. Example:

```csharp
var testLines = new List<string>
{
    "t0001|Avg Open WO Age DK|grand_total",
    "t0002|Avg Open WO Age DK|by_same_home",
    "t0003|Avg Open WO Age DK|by_year",
    "t0010|Avg Open WO Age DK|by_same_home_x_year",
    // ... generated from the confirmed test manifest
};
```

**Section 4 — `groupByColumns` (dimension map):**
Replace the `var groupByColumns = new Dictionary<string, string> { ... };` block with the dimensions confirmed in Phase 2. Single-dimension entries map to a single DAX column reference. Cross-product entries use a `|` pipe separator. Example:

```csharp
var groupByColumns = new Dictionary<string, string>
{
    // Single-dimension contexts — replace with columns from your model:
    { "by_dim1", "'[YourDimTable]'[SlicerColumn]" },
    { "by_year", "'Calendar'[YearColumn]" },
    { "by_status", "'[StatusTable]'[StatusColumn]" },

    // Cross-product contexts (pipe-separated, first column = TOPN partition)
    { "by_dim1_x_year", "'[YourDimTable]'[SlicerColumn]|'Calendar'[YearColumn]" },
    { "by_status_x_year", "'[StatusTable]'[StatusColumn]|'Calendar'[YearColumn]" },
};
// M&C example: by_same_home = 'Properties'[Property Current Same Home Reporting],
//              by_wo_status = 'Work Orders'[Work Order Status Desc]
```

**Everything else in the template is copied verbatim**, including:
- Configuration section (snapshotLabel, diagnosticMode, outputDir, teamsWebhookUrl, globalFilters, maxRowsPerContext)
- Helper functions (JsonEscape, SerializeValue, BuildMeasureRef)
- Global filter fragment builder
- DAX construction block (handles single-dimension, cross-product, and TOPN-per-group automatically based on `groupByColumns` entries)
- Execution engine (StreamWriter, DataTable.Dispose, try/catch, Stopwatch)
- Error logging
- Summary report builder
- Teams webhook notification
- Final Info() popup

The `MODEL_NAME` env var (alongside `SNAPSHOT_LABEL`, `DIAGNOSTIC_MODE`, `OUTPUT_DIR`, `TEAMS_WEBHOOK_URL`, `CONNECTION_STRING`, etc.) lets the user override any of these from the CLI without editing the script. See the env-var summary at the top of the template for the full list.

### 3.2 Webhook Notification (Optional)

The capture script supports an optional Teams notification on completion via Power Automate incoming webhook. When `teamsWebhookUrl` is set in the config section, the script sends an Adaptive Card with:
- Snapshot label (baseline/refactored)
- OK / error counts
- Total duration

**Setup:** In the Teams channel, add the "Post to a channel when a webhook request is received" Workflow, copy the generated URL, and paste it into the script's config.

**Reference implementation — use this exact code block at the end of the capture script, after building the `report` StringBuilder and before the final `Info(report.ToString())`:**

```csharp
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
```

**Note:** The `skipped` and `aborted` fact rows are conditional — they appear only when those counts are > 0.

If the webhook fails, the error is appended to the summary report but does not crash the script. This allows the user to step away from their machine during long-running capture sessions and be notified when the script completes.

### 3.3 Execution Workflow

**GUI (Tabular Editor 3):**

1. **Save the capture script** (e.g., `Desktop\PBI-Regression\capture-snapshot.csx`)
2. **Run diagnostic mode first**: Set `diagnosticMode = true` (or `set DIAGNOSTIC_MODE=true`), run the script in TE3 connected to the target model. Verify the first measure's tests succeed (popups show DAX + results). Fix any issues before proceeding.
3. **Capture baseline**: Set `diagnosticMode = false`, `snapshotLabel = "baseline"`. Connect TE3 to the **original** (pre-refactor) model. Run the script. Output: `Desktop\PBI-Regression\baseline.json`
4. **Capture refactored**: Change `snapshotLabel = "refactored"`. Connect TE3 to the **refactored** model (or the same model after applying change scripts). Run the script. Output: `Desktop\PBI-Regression\refactored.json`
5. If any errors occurred, check `{label}-errors.log` in the same directory for full exception details and the DAX query that failed.

**CLI (Claude Code / automation):**

Use environment variables to toggle the label without editing the script:

```bash
set SNAPSHOT_LABEL=baseline
set DIAGNOSTIC_MODE=false
TabularEditor.exe model.bim -S capture-snapshot.csx

set SNAPSHOT_LABEL=refactored
TabularEditor.exe model.bim -S capture-snapshot.csx

python compare-snapshots.py baseline.json refactored.json
```

The `OUTPUT_DIR` env var can also be set to redirect output away from the Desktop default.

**Critical reminder to the user:** "Capture the baseline BEFORE making any changes. If you've already made changes and don't have a baseline, you'll need to revert first (undo in TE, or restore from a .bim backup)."

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
- Cross-product results contain multiple grouping columns. The script sorts by ALL grouping columns before row-by-row comparison.
- TOPN-per-group results from GENERATE will have the partition column repeated — handled naturally by sorting on all non-measure columns.
- Row count mismatches on cross-product tests are flagged clearly as Delta=Y with detail "Row count: N → M".

**Usage:**

```
Prerequisites:
- Python 3.x
- openpyxl is auto-installed on first run if missing

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
  Model: Maintenance & Construction
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

**Why the `"timeout"` status is overloaded:** From v8 onward, the per-query memory watchdog (in `ExecuteDaxWithTimeout`) and the wall-clock timeout both surface as `"timeout"` in JSON because both call `cmd.Cancel()` and return the same status from the executor. The two paths are distinguished by the `Type:` tag in `{label}-timeouts.log`:
- `Type: query_timeout` → wall-clock exceeded `queryTimeoutMs`
- `Type: memory_watchdog` → memory pressure sustained for 1.5s mid-query
- `Type: smoketest_timeout` / `smoketest_error` → smoke test failure (also surfaces in `skippedMeasures`)
- `Type: query_error` → defensive fallback (rare; status="timeout" but neither pattern matched)

**Triage guide for timeouts and smoke failures (`{label}-timeouts.log`):**

Each entry has this format:
```
t0081 | Avg Open Work Orders Age | by_market_x_month | 2547ms
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
- **Expected intentional changes:** The user may have intentionally corrected bugs (e.g., adding proportionate ownership logic that didn't exist before, fixing project table interactions). These will show as legitimate value differences, not row count mismatches. The user should triage these manually from the diff CSV.
- **Cross-product specific:** If a cross-product test fails but its constituent single-dimension tests pass, the issue is in the filter interaction — both dimensions filter correctly in isolation but break when combined. This is the key scenario cross-product tests are designed to catch.
- **Timeout (newly):** Measure was fast before the refactor but now hangs → the refactor changed a relationship or filter path that caused the engine to scan a much larger table. Check for new bidir relationships, removed CROSSFILTER restrictions, or a removed inactive relationship that now activates an expensive path.

### 5.2 Iterate

If failures are found:
1. Fix the issue (DAX or relationship correction)
2. Re-run the capture script (only the refactored snapshot needs to refresh)
3. Re-run the comparison
4. Repeat until all tests pass

### 5.3 Document

Once all tests pass, trigger the session learning loop from `pbi-project-instructions-webchat.md`:
- Record the test results as validation evidence for the refactor
- Note any unexpected findings (e.g., "BLANK → 0 on 4 properties was expected because the new direct FK covers cases the bridge missed")
- Update `mc-gotchas.md` if new edge cases were discovered

---

## Script Generation Guidelines

### C# / Tabular Editor (.csx) — Template-Based

**CRITICAL: Always start from `scripts/capture-snapshot.csx` (read-only template).** Read the template file first, copy it to `output/{label}.csx`, then replace only four sections in the copy: header `PURPOSE` + test-case count, `modelName`, `testLines`, and `groupByColumns`. Do NOT touch the DAX construction block — it is verbatim template code that handles single-dimension, cross-product, and TOPN-per-group patterns automatically. Do not rewrite helpers, serialization, execution engine, error handling, diagnostic mode, webhook, or summary report sections.

Reference these files for understanding the template's patterns (but do not use them to regenerate template code):
- `skill-te-csharp-scripting.md` for TE scripting patterns
- `dax-results-handling.md` for DataTable serialization
- `performance-patterns.md` for execution efficiency

**Template invariants (never change these):**

- `System.IO.StreamWriter` with `Flush()` per test case for JSON output
- `DataTable.Dispose()` after each test case (capture row count BEFORE Dispose)
- `JsonEscape` helper for all string values
- `SerializeValue` helper with `__BLANK__`, `__NaN__`, `__INF__` sentinels
- `double.ToString("R")` for round-trip fidelity
- `diagnosticMode` toggle with `Info()` popups for the first measure only
- `teamsWebhookUrl` config with tested Adaptive Card webhook block (see Section 3.2)
- Single `Info()` at script end — never inside the execution loop
- Separate `{label}-errors.log` for full exception details
- The DAX construction block builds **bare table expressions** (no `EVALUATE` keyword). The `EVALUATE` prefix is added at the call site (v8+: `ExecuteDaxWithTimeout("EVALUATE " + dax, queryTimeoutMs)`) for the ADOMD path, and `EvaluateDax()` adds it implicitly for the non-direct fallback. Don't add `EVALUATE` inside the construction block.
- `BuildMeasureRef` helper for global filter wrapping
- Port discovery block (scans local PBI Desktop `AnalysisServicesWorkspaces` for msmdsrv port; matches by `Model.Database.Name` GUID; falls back to `CONNECTION_STRING` env var for XMLA/non-standard installs) — required for `useDirectAdomd=true` because `Model.Database.Server.ConnectionString` is inaccessible via TE3's wrapper
- DAX construction block (single-dimension, cross-product with `|` split, TOPN-per-group via GENERATE) — no `IGNORE()` wrappers (removed v8+; IGNORE() is invalid inside `ROW()` and unnecessary inside `SUMMARIZECOLUMNS` once measures are referenced directly)
- `globalFilters` and `maxRowsPerContext` config variables
- Environment variable overrides (`SNAPSHOT_LABEL`, `MODEL_NAME`, `DIAGNOSTIC_MODE`, `OUTPUT_DIR`, `TEAMS_WEBHOOK_URL`) with hardcoded fallback defaults
- Timing CSV output (`{label}-timing.csv`) alongside JSON

#### Safety Limits (v8+)

| Variable | Default | Env var | Description |
|---|---|---|---|
| `queryTimeoutMs` | `60000` | `QUERY_TIMEOUT_MS` | Per-query hard cap in ms. v8+ runs the query on a thread-pool task and the script thread polls every 500 ms; on wall-clock expiry the script calls `cmd.Cancel()` (the only mechanism that reliably interrupts a SE-bound Tabular query). `AdomdCommand.CommandTimeout` is set as a backstop only. |
| `smokeTestTimeoutMs` | `10000` | `SMOKE_TEST_TIMEOUT_MS` | Timeout for the pre-flight smoke test per unique measure. **Mechanism check:** set this to 200–500 ms via env var to force most measures to "fail" the smoke test — the regression run will then short-circuit, the JSON gets `"status": "skipped"` per permutation, and the timeouts.log gets `Type: smoketest_*` entries. Useful for verifying the smoke pipeline end-to-end without waiting for a real broken measure. |
| `memoryThresholdPct` | `80` | `MEMORY_THRESHOLD_PCT` | Watchdog trip point as % of RAM (hardcoded 16 GB denominator). Set to 0 to disable. Meaningful only in local PBIP workspace mode. On 32 GB use ~40%, on 64 GB use ~20% to match the same absolute threshold. **Debounce (v8+):** the per-query memory watchdog requires 3 consecutive critical polls (3 × 500 ms = 1.5 s sustained pressure) before cancelling, so transient working-set spikes during normal msmdsrv evaluation do not abort legitimate queries. The between-test check (run-level `aborted_memory`) has no debounce — a single critical reading aborts the run. |
| `useDirectAdomd` | `true` | `USE_DIRECT_ADOMD` | Set to `false` to fall back to `EvaluateDax()` (no timeout enforcement, no cancellability — a hung query freezes TE3). Use only as a compatibility escape hatch. |
| `skipOnSmokeTestFailure` | `true` | `SKIP_ON_SMOKE_FAILURE` | When `true`, measures failing the smoke test are skipped in the main run with `status:"skipped"` per dimension permutation. When `false`, they still attempt to run (caught by `queryTimeoutMs`). |
| `discoveredConnStr` | *(auto via port discovery)* | `CONNECTION_STRING` | Full MSOLAP connection string. Set this env var to skip port discovery — required for XMLA endpoints or non-standard PBI Desktop installs. When unset, the script probes local `AnalysisServicesWorkspaces` folders and matches by `Model.Database.Name`. |

**How smoke-test skipping works internally (v8+):** the smoke loop builds an in-memory `HashSet<string> skippedMeasures` and a `Dictionary<string,string> smokeResults` (measure → failure reason). Nothing is written to disk for smoke failures except (a) one `Type: smoketest_*` entry per failed measure in `{label}-timeouts.log`, and (b) one `"status": "skipped"` JSON record per dimension permutation in the main snapshot. The skip is *per measure, not per test* — every test case for a failed measure gets the same `skip_reason` and zero duration.

**XMLA note:** Power BI gateway enforces its own query cap (~225s). `queryTimeoutMs > 225000` has no effect against the Power BI service. The memory watchdog is also ineffective in XMLA mode (model memory lives on the Fabric capacity).

**What Claude generates on demand:**

- `modelName` — the model display name, derived from the conversation (or supplied by the user); written as the default fallback in the script's config section and surfaced in the JSON snapshot header (`"model_name": "..."`) for use by `compare-snapshots.py`
- `testLines` — the `id|measure|context` entries from the confirmed test manifest
- `groupByColumns` — the context-label-to-DAX-column dictionary from confirmed filter contexts (single-dimension entries map to a single DAX column; cross-product entries use `|` pipe separator between columns, with the first column serving as the TOPN partition)
- Header comment updates (PURPOSE line, test case count)

### Python (comparison)

`scripts/compare-snapshots.py` is a stable, tested script — not generated fresh each session. It performs both value comparison and timing analysis in one pass.

- **Dependency:** `openpyxl` (auto-installed on first run if missing) — the only non-stdlib dependency in the toolchain
- **Input:** two snapshot JSONs (baseline + refactored) from `capture-snapshot.csx`
- **Output:** single `regression-report.xlsx` with 6 sheets (All Tests, Value Deltas, By Measure, By Context, Top Movers, Timeout Regressions)
- Handle encoding: read JSON with `encoding='utf-8-sig'` (Windows BOM)
- Sort rows by all non-measure columns before comparing (DAX doesn't guarantee order)
- Use `argparse` for CLI: `python compare-snapshots.py <baseline> <refactored> [--output report.xlsx]`
- Exit code: 0 = all value tests pass, 1 = value failures found (timing regressions don't affect exit code). Exit code 1 now also triggers when any test has `status:"timeout"` in the refactored snapshot that was `"ok"` in the baseline (a new hang = a regression).
- **Delta flag:** every test case gets Delta = Y or N based on value comparison. Enables filtering the single report for regression review (Delta=Y) or performance review (sort by Δ ms)
- **Configurable thresholds** at top of script: `NUMERIC_TOLERANCE`, `REGRESSION_PCT`, `IMPROVEMENT_PCT`, `MIN_MS_FOR_PCT`

### DAX Query Generation

- **The DAX construction block builds bare table expressions — no `EVALUATE` keyword**. The prefix is added at the call site (v8+: `ExecuteDaxWithTimeout("EVALUATE " + dax, queryTimeoutMs)`) for the ADOMD path; `EvaluateDax()` adds it implicitly for the non-direct fallback. Adding `EVALUATE` inside the construction block double-wraps and throws.
- **Build DAX at runtime in the C# script**, not as pre-embedded strings in the test case data. Store test cases as simple `id|measure|context` entries, and construct the SUMMARIZECOLUMNS call from a dimension map dictionary. This avoids all cross-language string escaping issues.
- Use `SUMMARIZECOLUMNS` for grouped measures (not `ADDCOLUMNS` over `VALUES` — SUMMARIZECOLUMNS auto-removes blank rows which matches report visual behavior).
- **Do NOT wrap the measure in `IGNORE()`** (v8+ behavior). `IGNORE()` is a SUMMARIZECOLUMNS modifier that suppresses blank totals per measure expression — it does NOT mean "ignore filters." Wrapping the bare measure reference in `IGNORE()` adds no semantic value to a regression test, and `IGNORE()` is **invalid inside `ROW()`** (the smoke-test query), so removing it everywhere keeps the construction uniform.
- For pinned filter values, use `CALCULATETABLE(SUMMARIZECOLUMNS(...), TREATAS({value}, 'Table'[Column]))` — this is the correct pattern per SQLBI guidance.
- For calculation group testing, add the calc group column as a filter: `TREATAS({"YTD"}, 'Time Intelligence'[Selection])`.
- Never use `ALL()` in test queries — we want to test under the natural filter context.
- Avoid `TOPN` in test queries when full comparison is needed — but use TOPN for high-cardinality dimensions where full evaluation is impractical.
- **Cross-product TOPN uses GENERATE pattern** — `GENERATE(VALUES(partitionCol), TOPN(N, SUMMARIZECOLUMNS(detailCols, "Result", measureRef)))` — partition column is always the first in the `|`-separated list.

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
- **Gotcha:** Calc column type mismatches (e.g., TEXT vs DECIMAL for Ownership Pct — requires VALUE() conversion)

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

At minimum, the user must confirm these before Claude generates scripts:

| Decision Point | What Claude proposes | User confirms or adjusts |
|---|---|---|
| Change scope | Parsed from description or .bim diff | "Yes, that's the full scope" |
| Measure tiers | Auto-classified via dependency graph | "Move X to Tier 1, skip Y" |
| Measure selection method | Explicit list, random sample, or domain/type filter | "Random sample of 20" or "All cost measures" |
| Tier 2 inclusion | Include or exclude transitive dependents | "Just Tier 1" or "Include Tier 2" |
| Filter contexts | Dimensions identified from model structure | "Add Region, skip Portfolio" |
| Query approach | Single-dimension, cross-product, or both | "Single + one cross-product" |
| Cross-product columns | Low-cardinality dimension pairs | "StatusFlag × Year, Category × Year" |
| TOPN strategy | Per-group (cross-product) vs overall (single) | "5 per group" |
| Specific filter values | Suggested based on model knowledge | "Use FY2025, [entity] = [known value]" |
| Edge cases (TODAY, RLS, calc groups) | Identified from DAX patterns | "Pin to 2025-12-31, skip RLS" |
| Test manifest summary | Count of test cases by tier | "Looks good, generate the scripts" |

---

## File Outputs

The skill produces these files:

| File | Source | Purpose |
|---|---|---|
| `test-manifest.json` | Claude (in conversation) | Records test cases, measures, tiers, contexts, and selection method (no embedded DAX) |
| `output/{label}.csx` | Copy of `scripts/capture-snapshot.csx` template, with `modelName`, `testLines`, and `groupByColumns` replaced by Claude (the template stays untouched) | TE3 script — runs DAX, streams snapshot JSON + timing CSV to disk |
| `{label}.json` | User runs `.csx` in TE3 | Snapshot of model results (includes per-test timing) |
| `{label}-testplan.json` | Generated by `.csx` pre-flight (v8+) | Planned test order written before execution begins; survives a force-kill so you can identify which test was in flight |
| `{label}-timing.csv` | Generated by `.csx` alongside JSON | Lightweight per-test-case timing for quick reference |
| `{label}-errors.log` | Generated by `.csx` if errors occur | Full exception details per failed test case |
| `{label}-timeouts.log` | Generated by `.csx` if timeouts OR smoke-test failures occur (v8+) | Timed-out tests AND smoke-test failures, each with `Type:` (memory_watchdog \| query_timeout \| smoketest_timeout \| smoketest_error \| query_error), `Reason:` (full error message), and full DAX |
| `scripts/compare-snapshots.py` | Stable, tested script in repo | Python comparison — reads two snapshot JSONs, outputs single xlsx report |
| `regression-report.xlsx` | User runs `.py` | Unified report: value deltas + timing comparison in one workbook (6 sheets) |

Both `scripts/capture-snapshot.csx` and `scripts/compare-snapshots.py` are stable, tested scripts in the repo. Claude only generates the session-specific `testLines` and `groupByColumns` sections for the capture script (writing them into the `output/{label}.csx` copy, never the template). The comparison script is used as-is with no per-session changes.

**End-to-end workflow — GUI (3 steps after script setup):**

1. Run `capture-snapshot.csx` in TE3 with `snapshotLabel = "baseline"`, then again with `"refactored"`
2. Run `python compare-snapshots.py baseline.json refactored.json`
3. Open `regression-report.xlsx` — filter Delta=Y for value regressions, sort by Δ ms for performance

**End-to-end workflow — CLI (Claude Code / automation):**

The capture script supports environment variable overrides so Claude Code can toggle the label without editing the file:

```bash
# Windows (cmd)
set SNAPSHOT_LABEL=baseline
TabularEditor.exe model.bim -S capture-snapshot.csx

set SNAPSHOT_LABEL=refactored
TabularEditor.exe model.bim -S capture-snapshot.csx

python compare-snapshots.py baseline.json refactored.json
```

Environment variables: `SNAPSHOT_LABEL`, `DIAGNOSTIC_MODE` ("true"/"false"), `OUTPUT_DIR` (full path). When not set, the script falls back to its hardcoded defaults (unchanged GUI behavior).
