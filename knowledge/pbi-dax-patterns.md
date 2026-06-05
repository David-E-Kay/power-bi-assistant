# Validated DAX Patterns

<!-- Patterns that have been tested and confirmed to work correctly in these
     models. When multiple approaches are valid, document the user's preferred
     approach and why. -->

## Row/Total branching (ISINSCOPE + HASONEVALUE)
- **When to use:** Any measure that needs different logic at detail rows vs. grand total in a matrix visual
- **Pattern:**
  ```dax
  IF(
      ISINSCOPE('Table A'[Column A]) && HASONEVALUE('Date'[Month]),
      // Row-level logic here
      [Row Level Measure],
      // Total-level logic here
      [Total Level Measure]
  )
  ```
- **Key insight:** ISINSCOPE detects the literal axis column (is this column on rows/columns of the visual?). HASONEVALUE detects whether a single filter context value exists. Use BOTH together for robust branching — ISINSCOPE alone can miss subtotal levels, HASONEVALUE alone can miss multi-select scenarios.
- **Validated:** 2026-03-10
- **Affected model:** All

## USERELATIONSHIP for inactive Calendar joins
- **When to use:** Any measure that evaluates against a date column joined via an inactive relationship
- **Pattern:**
  ```dax
  CALCULATE(
      [Base Measure],
      USERELATIONSHIP('Date'[Date], 'Table A'[Column A])
  )
  ```
- **Gotchas:**
  - Only works inside CALCULATE/CALCULATETABLE
  - Cannot activate two relationships between the same pair of tables simultaneously
  - The activated relationship overrides the active one for the duration of CALCULATE
- **Validated:** 2026-03-10

## Bridge table traversal via pre-computed calc columns
- **When to use:** When a measure needs to traverse a BridgeTable and CROSSFILTER is too expensive
- **Pattern:** Create a calculated column on the FactTable that pre-computes the bridge lookup at refresh time, then filter on the calc column instead of using runtime CROSSFILTER
- **Tradeoff:** Increases model size (additional column per fact table) but eliminates the expensive runtime CROSSFILTER scan
- **Validated:** 2026-03-10
- **Affected model:** All

## Proportionate weighted average (e.g., a count-weighted age metric)
- **When to use:** Weighted averages where the weight is a count and the average must be proportionate to the contributing rows, not a simple average of averages
**Pattern:** Pre-compute ownership pct as calc column on snapshot/fact table via
  LOOKUPVALUE, then SUMX(VALUES(keys), age * pct) / SUMX(VALUES(keys), pct)
- **Key insight:** Only denormalize calculation-only fields (like ownership pct) as calc columns.
  Never denormalize dimension fields used as report slicers — those must filter through
  relationships naturally.
- **Key insight:** Use SUMPRODUCT-style pattern (SUMX of value × weight / SUM of weight) rather than AVERAGEX, which gives unweighted results when rows have different cardinalities
- **Validated:** 2026-03-10

## Proportionate() UDF — how to pass the expression correctly
- **When to use:** Any measure that wraps a CALCULATE block in `Proportionate(...)` for the proportionate-ownership branch.
- **Rule:** The argument to `Proportionate()` must be an **expression that can be re-evaluated per row context** (the UDF iterates ownership internally). A scalar VAR cannot carry context — it's evaluated once in the outer context, and the UDF has no way to re-enter the filter context from a pre-computed scalar.
- **✅ Correct patterns:**
  ```dax
  // Option A — repeat the full CALCULATE expression inline
  RETURN
      IF(
          [Proportionate Toggle] = "Yes",
          Proportionate(
              CALCULATE(
                  SUM('Fact'[Amount]),
                  <all the same filters as BaseCalc>
              )
          ),
          BaseCalc
      )

  // Option B — extract BaseCalc into its own helper measure, then pass the measure reference
  [Measure A Base] =
  CALCULATE( SUM('Fact'[Amount]), <filters> )

  [Measure A] =
  IF(
      [Proportionate Toggle] = "Yes",
      Proportionate( [Measure A Base] ),
      [Measure A Base]
  )
  ```
- **❌ Incorrect pattern — silently wrong results:**
  ```dax
  VAR BaseCalc = CALCULATE( SUM('Fact'[Amount]), <filters> )
  RETURN
      IF(
          [Proportionate Toggle] = "Yes",
          Proportionate( BaseCalc ),  // ← scalar VAR, context lost
          BaseCalc
      )
  ```
- **Why VAR fails:** VARs evaluate eagerly to a scalar in the outer context. When that scalar is passed to `Proportionate()`, the UDF has no expression to re-evaluate per ownership row — it just multiplies the same pre-computed number, producing wrong proportionate totals.
- **Tradeoff between options:** Option A duplicates the CALCULATE block (harder to maintain, keeps measure self-contained). Option B eliminates duplication but adds a helper measure to the model. Prefer B when the same BaseCalc is used in multiple places.
- **Validated:** 2026-04-17
- **Affected model:** All models using the `Proportionate` UDF

## Distinct count via VALUES iteration (prefer over DISTINCTCOUNT)
- **When to use:** Any measure that needs a count of distinct values in a column, especially
  when the measure also involves relationship activation (USERELATIONSHIP / CROSSFILTER) or
  proportionate weighting logic.
- **Pattern:**
  ```dax
  // ✅ Preferred — leverages Vertipaq dictionary directly
  SUMX ( VALUES ( 'Table'[Column] ), 1 )

  // ❌ Avoid — uses hash-based aggregation, consistently slower
  DISTINCTCOUNT ( 'Table'[Column] )
  ```
- **Why it's faster:** `VALUES` reads filtered entries from the Vertipaq column dictionary
  directly (essentially free), then SUMX just counts rows. `DISTINCTCOUNT` takes the standard
  aggregation path — scans encoded column values, builds a hash set, returns the count. The
  dictionary-based path avoids that overhead.
- **Performance delta:** Ranges from marginal (~100ms) in simple contexts to 10× in filter
  contexts that involve relationship traversals. The gap widens when the alternative
  DISTINCTCOUNT version must activate relationships inside an iterator — VALUES resolves
  distinctness before the iteration, avoiding repeated SE queries.
- **Example:** `[Measure A]` (a distinct-count measure) — proportionate branch uses
  `SUMMARIZE` over `'Table A'` grouped by `[Column A]` and `[_WeightPct]`,
  then sums the weight. Non-proportionate branch uses `SUMX(VALUES(...), 1)` instead of
  DISTINCTCOUNT, with the same CROSSFILTER/USERELATIONSHIP modifiers. Both branches
  outperformed the equivalent DISTINCTCOUNT + `'Table B'`-iteration version.
- **Always verify:** Even when the delta appears marginal in one filter context, test under
  proportionate / multi-property / year-filtered contexts — the gap can be dramatically larger.
- **Validated:** 2026-04-11
- **Affected model:** All (pattern is model-agnostic)

## FILTER → KEEPFILTERS substitution for predicate pushdown
- **When to use:** Any measure using `FILTER('Table', predicate)` as a CALCULATE argument
  to exclude rows (e.g., deleted flags, status filters)
- **Pattern:**
  ```dax
  // ❌ Materializes full table in FE, then filters row-by-row
  CALCULATE(
      SUM( 'Table'[Amount] ),
      FILTER( 'Table', 'Table'[IsDeleted] <> "Y" )
  )

  // ✅ Pushes predicate into SE as a direct query filter
  CALCULATE(
      SUM( 'Table'[Amount] ),
      KEEPFILTERS( 'Table'[IsDeleted] <> "Y" )
  )
  ```
- **Why it's faster:** `FILTER('Table', ...)` forces the engine to materialize the entire
  table in the formula engine (FE) and iterate row-by-row. `KEEPFILTERS` with a boolean
  predicate pushes the filter into the storage engine (SE) as a direct query predicate,
  avoiding FE materialization entirely.
- **Caveat:** KEEPFILTERS intersects with external filter context rather than overriding it
  (unlike bare predicates in CALCULATE). This is usually desirable — report slicers remain
  additive. If you need to override external context, use a bare predicate without KEEPFILTERS.
- **Validated:** 2026-04-11
- **Affected model:** All (pattern is model-agnostic)

## AVERAGEX → DIVIDE with single-pass ADDCOLUMNS
- **When to use:** Any measure using `AVERAGEX(VALUES(key), [complex measure])` where
  the inner measure involves relationship modifiers (CROSSFILTER/USERELATIONSHIP) or
  other expensive context setup
- **Pattern:**
  ```dax
  // ❌ Re-evaluates full measure per key — N iterations, repeated SE queries
  AVERAGEX(
      VALUES( 'Table'[Key] ),
      [Expensive Measure]
  )

  // ✅ Single SE query, then in-memory scan for both sum and count
  VAR _detail =
      ADDCOLUMNS(
          VALUES( 'Table'[Key] ),
          "@Value", CALCULATE( SUM( 'Table'[Amount] ), <filters> )
      )
  VAR _total = SUMX( _detail, [@Value] )
  VAR _count = COUNTROWS( _detail )
  RETURN DIVIDE( _total, _count )
  ```
- **Why it's faster:** ADDCOLUMNS materializes the per-key values in a single SE query.
  SUMX and COUNTROWS then scan the in-memory table — no second SE roundtrip. The original
  AVERAGEX re-enters the full measure context (relationship activation, filter setup) per
  iteration.
- **Tradeoff:** Materializes a table in FE. For very high cardinality key columns this
  could add FE memory pressure — test with traces. For small-to-moderate key counts it's
  a net win.
- **Caveat — proportionate measures:** When the measure applies per-row weighting (e.g.,
  the weight varies per entity per row), AVERAGEX must stay — the per-row context
  is mathematically required. However, hoisting shared relationship modifiers
  (CROSSFILTER/USERELATIONSHIP) into an outer CALCULATE still helps by avoiding
  per-iteration relationship setup.
- **Example:** `[Measure A]` (a per-key average cost) — non-proportionate branch uses
  ADDCOLUMNS + DIVIDE; proportionate branch stays as AVERAGEX with hoisted outer CALCULATE.
- **Validated:** 2026-04-11
- **Affected model:** All (pattern is model-agnostic)

## Proportionate metric over a daily-snapshot fact table — grain-dependent branching
- **When to use:** Proportionate weighted-average measures over a daily-snapshot fact
  table that must branch between single-entity and multi-entity contexts. In a
  **daily snapshot**, a single entity has many rows over time (one per day) even
  when the weight (e.g., an ownership pct) is stable, and the measured value can
  change across snapshot days.
- **Pattern:**
  ```dax
  // Single-entity grain — AVERAGEX of value × weight
  // Rationale: the weight is constant across the entity's snapshot rows, so
  // AVERAGEX(rows, value*weight) naturally averages the value across snapshot days
  // and the constant weight just scales the result. No extra denominator weighting needed.
  VAR ProportionateEntity =
      AVERAGEX (
          RelevantRows,
          'Table A'[Column A] * 'Table A'[Column B]
      )

  // Multi-entity grain — SUMPRODUCT-style weighted average
  // Rationale: the weight varies across entities, so both numerator and denominator
  // must be weight-scaled to produce a correct proportionate average.
  VAR ProportionateAggregate =
      DIVIDE (
          SUMX ( RelevantRows, 'Table A'[Column A] * 'Table A'[Column B] ),
          SUMX ( RelevantRows, 'Table A'[Column B] )
      )

  // Branch selection
  RETURN
      IF (
          HASONEVALUE ( 'Table A'[Key] ),
          ProportionateEntity,
          ProportionateAggregate
      )
  ```
- **Key insight:** The two branches are *not* mathematically equivalent — this is
  intentional. At single-entity grain, the weight is constant, so AVERAGEX returns
  `weight × AVG(value)` which is the proportionate average over snapshot days.
  Applying the SUMPRODUCT form at single-entity grain would collapse to plain
  `AVG(value)` (the weight cancels), losing the proportionate scaling. Conversely, using
  AVERAGEX at multi-entity grain would produce an unweighted mean of value×weight
  products — wrong when the weight varies.
- **Why snapshot grain matters:** This pattern is validated against a daily-snapshot
  fact table. It may generalize to other snapshot fact tables, but each candidate
  should be confirmed individually — the correctness hinges on (a) the weight being
  constant per entity across snapshot rows at the single-entity grain, and (b) the
  measured value varying meaningfully across snapshot days.
- **Validated:** 2026-04-22
- **Affected model:** Validated on a daily-snapshot fact table. Do not apply blindly
  to other models' snapshot/proportionate measures without confirming the grain
  assumptions.

## Consolidate shared filters across combined CALCULATE blocks
- **When to use:** Any measure that sums or combines multiple CALCULATE blocks sharing
  common filter conditions (KEEPFILTERS or otherwise)
- **Pattern:** Hoist shared filters into a single outer CALCULATE wrapper. Each inner
  CALCULATE should contain only its distinguishing filter(s).
  ```dax
  CALCULATE(
      CALCULATE( [Base Measure], KEEPFILTERS( <unique filter A> ) )
      +
      CALCULATE( [Base Measure], KEEPFILTERS( <unique filter B> ) ),
      // --- Shared filters (applied once) ---
      KEEPFILTERS( <shared filter 1> ),
      KEEPFILTERS( <shared filter 2> ),
      KEEPFILTERS( <shared filter 3> )
  )
  ```
- **Benefits:** Reduces code duplication, simplifies maintenance (shared filter changes
  happen in one place), and gives the engine a simpler query plan.
- **Watch for:** Accidental table mismatches in duplicated filters — e.g., the same
  logical filter targeting `'Table A'` in one block and `'Table B'`
  in another when they should match.
- **Example:** A reporting measure went from 14 KEEPFILTERS across 2 blocks (5 duplicated)
  down to 8 — 6 shared in outer CALCULATE, 1 unique per inner block.
- **Validated:** 2026-03-25
- **Affected model:** All

## Eliminate redundant filters across the measure dependency chain
- **When to use:** Any time you review or refactor a measure that wraps another measure
  in CALCULATE. Requires walking the dependency chain (ideally from a parsed .bim) to
  inspect what filters the base measure already applies.
- **Pattern:** If a base measure already applies a filter (e.g.,
  `KEEPFILTERS('Table A'[Column A] = "Value 1")`), any dependent measure that wraps it
  in CALCULATE does NOT need to repeat that same filter — it's already enforced by the
  inner measure and the outer KEEPFILTERS just adds evaluation cost for no effect.
  ```dax
  // Base measure already filters [Column A] = "Value 1"
  // ❌ Redundant — filter is duplicated
  Dependent Measure =
  CALCULATE(
      [Base Measure],
      KEEPFILTERS( 'Table A'[Column A] = "Value 1" ),  // already in base
      KEEPFILTERS( 'Table A'[Column B] IN { "Value 2" } )
  )

  // ✅ Correct — only the additive filter remains
  Dependent Measure =
  CALCULATE(
      [Base Measure],
      KEEPFILTERS( 'Table A'[Column B] IN { "Value 2" } )
  )
  ```
- **Key insight:** KEEPFILTERS intersects with existing filter context. If the base measure
  already constrains a column to a specific set of values, an outer KEEPFILTERS on the same
  column with the same values is a no-op — it just adds engine work to confirm what's
  already true.
- **Caveat:** This only applies when the filters are truly identical. If the outer measure
  filters to a *subset* of what the base allows (narrowing the filter), that's additive and
  should stay. Only remove when the filter is an exact duplicate.
- **Review process:** When refactoring a measure, always trace `[Base Measure]` → open its
  DAX → list its filters → compare against the outer CALCULATE's filters → remove exact
  duplicates.
- **Validated:** 2026-03-25
- **Affected model:** All
