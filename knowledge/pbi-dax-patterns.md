# Validated DAX Patterns

<!-- Transferable, model-agnostic DAX patterns confirmed to work and worth
     reaching for by default. Curation bar is HIGH: include a pattern only when
     it is 100% valid, genuinely reusable across models, and free of any
     company- or model-specific business logic. Model-specific findings do NOT
     belong here — they accumulate in per-model KB files
     ({model}-dax-performance.md) via the Session Learning Loop. -->

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
- **Caveat — per-row weighted averages:** When the measure applies genuine per-row weighting
  (the weight varies per entity per row), AVERAGEX must stay — the per-row context is
  mathematically required. However, hoisting shared relationship modifiers
  (CROSSFILTER/USERELATIONSHIP) into an outer CALCULATE still helps by avoiding
  per-iteration relationship setup.

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
