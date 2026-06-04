# Model Gotchas & Known Issues

<!-- Behavioral quirks, type mismatches, and unexpected behaviors specific to
     these models. These are things the model metadata alone won't warn you
     about. Mark resolved issues rather than deleting them. -->

## AccountingPeriod is TEXT, not DATE
- **Affected:** DimCalendar[AccountingPeriod] across multiple models
- **Symptom:** Implicit cast failures when joining or comparing against DATE columns; silent wrong results in some DAX contexts
- **Workaround:** Use DimCalendar[StartOfMonth] (DATETIME type) instead, or explicitly convert with DATEVALUE()
- **Status:** Active
- **Discovered:** 2026-03-10

## DimCalendar has multiple inactive relationships
- **Affected:** DimCalendar → FactWorkOrder, FactPurchaseOrder, and other fact tables
- **Symptom:** Bare CALCULATE without USERELATIONSHIP silently evaluates against the wrong date column (or no date filter at all)
- **Workaround:** Always use USERELATIONSHIP(DimCalendar[DateKey], Fact[TargetDateColumn]) inside CALCULATE
- **Status:** Active (by design — multiple date columns per fact table)
- **Discovered:** 2026-03-10

## Non-additive totals with USERELATIONSHIP + CROSSFILTER
- **Affected:** Matrix visuals combining Calendar slicer with bridge-traversal measures
- **Symptom:** Row-level values are correct but grand total is wrong (over/under-counted)
- **Workaround:** Use combined ISINSCOPE (for literal axis column detection) + HASONEVALUE (for filter context detection) branching pattern to handle row vs. total differently
- **Status:** Active
- **Discovered:** 2026-03-10

## ALLEXCEPT behavior with Calendar context
- **Affected:** Measures using ALLEXCEPT to preserve Calendar filters while clearing other filters
- **Symptom:** Calendar filter context can "leak" or be unexpectedly removed depending on relationship chain
- **Workaround:** Explicit CALCULATE with VALUES(DimCalendar[DateKey]) in filter instead of relying on ALLEXCEPT to preserve it
- **Status:** Active
- **Discovered:** 2026-03-10

## Properties[Ownership Proportionate Perc] is TEXT
- Column is string type in the model. Any DAX arithmetic requires explicit VALUE() conversion.
  Implicit cast is fragile — non-numeric values silently return BLANK.
- **Affected model:** Maintenance & Construction
- **Status:** Resolved 2026-04-16 — Snowflake view `V_D_PROPERTY` change deployed; column is now DECIMAL. Scripts (`fix-ownership-pct-source.csx` + `fix-ownership-pct-measures.csx`) were applied: `/100` division removed from M query, calc columns, UDF, and ~69 measures. VALUE() wrappers removed. No further action needed.
- **Validated:** 2026-03-24, scripts generated 2026-04-01, fix applied 2026-04-16

## TE3 EvaluateDax() requires bare expressions (no EVALUATE keyword)
- **Affected:** All C# scripts using `EvaluateDax()` in Tabular Editor 3
- **Symptom:** `DaxQueryException: The syntax for 'EVALUATE' is incorrect` — TE3 internally wraps the expression in `EVALUATE ROW("Value", <expression>)`, so including `EVALUATE` produces `EVALUATE ROW("Value", EVALUATE SUMMARIZECOLUMNS(...))` which is invalid DAX
- **Workaround:** Pass only the bare table expression: `EvaluateDax("SUMMARIZECOLUMNS(...)") as System.Data.DataTable`, never `EvaluateDax("EVALUATE SUMMARIZECOLUMNS(...)")`
- **Status:** Active (TE3 behavior, unlikely to change)
- **Discovered:** 2026-04-05

## TE3 Info() is modal — blocks execution
- **Affected:** All C# scripts using `Info()` in Tabular Editor 3
- **Symptom:** Each `Info()` call shows a popup dialog requiring the user to click OK before execution continues. Using `Info()` for progress reporting inside a loop forces manual interaction per iteration.
- **Workaround:** Never use `Info()` inside loops. Use a single `Info()` at script end for the summary. For debugging, use a `diagnosticMode` toggle that limits to a small subset and intentionally uses `Info()` popups.
- **Status:** Active (TE3 behavior)
- **Discovered:** 2026-04-05

## TE3 StringBuilder causes OOM on large DAX result serialization
- **Affected:** C# scripts that serialize many large `EvaluateDax()` results to JSON
- **Symptom:** `System.OutOfMemoryException` when accumulating hundreds of test case results (potentially thousands of rows each) in a single `StringBuilder`
- **Workaround:** Use `System.IO.StreamWriter` with `Flush()` after each test case, and call `DataTable.Dispose()` to release memory. This keeps memory usage flat regardless of total result size.
- **Status:** Active (TE3 memory constraints)
- **Discovered:** 2026-04-05

## PO Detail→Projects must stay inactive (ambiguous path with bidir bridge)
- **Affected:** Maintenance & Construction — `PO Detail[Custom Project Key] → Projects[Project Key]`
- **Symptom:** Activating this relationship causes "ambiguous paths" error: PO Detail→WO→Bridge and PO Detail→Projects→Bridge are both active paths to the bridge when bridge→WO is bidir
- **Workaround:** Keep inactive. Measures that need PO costs by project use `USERELATIONSHIP('PO Detail'[Custom Project Key], Projects[Project Key])` explicitly. This was safe to activate during v1 refactor (when bridge→WO was single direction) but became ambiguous when bidir was restored in v2.
- **Status:** Active (by design in v2 topology)
- **Discovered:** 2026-04-09

## Projects→Properties must stay inactive (ambiguous path through bridge)
- **Affected:** Maintenance & Construction — `Projects[Property Key] → Properties[Property Key]`
- **Symptom:** Activating creates two paths from the bridge to Properties: Bridge→WO→Properties (via N1) and Bridge→Projects→Properties
- **Workaround:** Keep inactive. 32 measures use `USERELATIONSHIP(Projects[Property Key], Properties[Property Key])` explicitly.
- **Status:** Active (by design — documented since v1)
- **Discovered:** 2026-03-25

## Proportionate() UDF loses context when passed a scalar VAR
- **Affected:** Any measure using the `Proportionate` UDF across all models that have it defined
- **Symptom:** Measure returns numerically incorrect proportionate totals (typically understated or overstated by a constant factor) with no error. Values look plausible, so the bug is easy to miss without regression testing.
- **Root cause:** `Proportionate(expr)` internally iterates ownership rows and re-evaluates `expr` per row. A scalar VAR (e.g., `VAR BaseCalc = CALCULATE(...)`) evaluates once in the outer context; passing that scalar into the UDF provides no expression for the UDF to re-enter — it just multiplies the same pre-computed number per iteration.
- **Workaround:** Never pass a scalar VAR to `Proportionate()`. Use one of:
  - **Option A:** Repeat the full CALCULATE expression inline as the UDF argument (duplicated code, but self-contained)
  - **Option B:** Extract the base CALCULATE into its own helper measure, then pass the measure reference (`Proportionate([Base Measure])`) — measure references re-evaluate correctly per context
- **See:** `pbi-dax-patterns.md` → "Proportionate() UDF — how to pass the expression correctly" for code examples
- **Status:** Active (UDF behavior, by design)
- **Discovered:** 2026-04-17

## PBI Desktop "Another transaction in progress" after TE3 script
- **Affected:** PBI Desktop when a TE3 .csx script modifies the model via XMLA while Desktop also has the model open
- **Symptom:** Clicking "Refresh" in Desktop immediately after TE3 script execution triggers `System.InvalidOperationException: Another transaction is in progress`. Desktop may hang, requiring a hard close (unsaved changes lost).
- **Workaround:** After running a .csx script in TE3, save in TE3 first (Ctrl+S), then switch to Desktop and wait a few seconds for it to sync before refreshing. The model changes persist in TE3's save even if Desktop must be hard-closed.
- **Status:** Active (PBI Desktop behavior)
- **Discovered:** 2026-04-09

<!-- Add new gotchas as they arise. Format:
## [Short title]
- **Affected:** Which tables/columns/models
- **Symptom:** What goes wrong
- **Workaround:** How to handle it
- **Status:** Active / Resolved (date)
- **Discovered:** date
-->
