# DAX Performance Findings

<!-- Performance discoveries from debugging sessions. Include concrete numbers
     (query times, row counts) and the resolution that worked. Date each entry
     for freshness tracking. -->

## CROSSFILTER on BridgeProjectProperty
- **Impact:** High — adds 5-8s to queries involving project-property traversal
- **Root cause:** Bridge table has ~2.6M rows; CROSSFILTER forces full scan at evaluation time
- **Resolution:** Pre-compute bridge traversal as calculated column on the fact table, eliminating runtime CROSSFILTER overhead
- **Validated:** 2026-03-10
- **Affected model:** Maintenance & Construction
- **Related measures:** [Project with Cost ≤ Approved], [Project Count by Property]

## Iterator patterns on bridge table
- **Impact:** Medium — SUMX/COUNTX/AVERAGEX scale poorly beyond 500K rows on bridge
- **Resolution:** Replace with pre-aggregated calculated columns or push aggregation to ETL/Snowflake layer
- **Validated:** 2026-03-10
- **Affected model:** All models using BridgeProjectProperty

## Consolidate duplicate CALCULATE branches
- **Impact:** Medium — redundant CALCULATE nesting inflates query plan complexity
- **Resolution:** Combine filter arguments into a single CALCULATE when possible; reduces engine overhead
- **Validated:** 2026-03-10
- **Affected model:** Maintenance & Construction

## SUMMARIZECOLUMNS inside CALCULATETABLE
- **Behavior (updated 2026-04-07):** Since the June 2024 engine update,
  SUMMARIZECOLUMNS DOES inherit external filter context in measures.
  CALCULATETABLE is still the SQLBI-recommended wrapper for adding explicit filters for controlling
  filter/group-by column interactions and ensuring clean context transition —
  not for making external filters work.
- **Model property:** Set "value filter behavior" to Independent (default for
  models created 2025+). Legacy Coalesced setting can cause unexpected filter
  interactions.
- **Ref:** https://www.sqlbi.com/articles/summarizecolumns-best-practices/
- **Previous guidance corrected:** Earlier entry incorrectly stated SC does not
  inherit outer filter context. This was true pre-June 2024 only.
- **Validated:** 2026-04-07

## Open Work Order Costs — FILTER→KEEPFILTERS + shared modifier hoisting
- **Impact:** Medium — eliminated FE table materialization and duplicated relationship setup
- **Root cause:** Original measure used `FILTER('Open Work Orders Cost Summary', ...)` which
  materialized the full table in FE. CROSSFILTER/USERELATIONSHIP modifiers were duplicated
  in both the raw and proportionate branches.
- **Resolution:**
  1. Replaced `FILTER(table, predicate)` with `KEEPFILTERS(predicate)` — pushes filter to SE
  2. Hoisted shared CROSSFILTER/USERELATIONSHIP into single outer CALCULATE
  3. Proportionate logic inlined (removed UDF call) — UDF required expression re-evaluation
     per property row, so pre-computed scalar couldn't be passed
- **Validated:** 2026-04-11
- **Affected model:** Maintenance & Construction
- **Related measures:** [Open Work Order Costs]

## Avg Cost per Open Work Order — AVERAGEX→DIVIDE with single-pass ADDCOLUMNS
- **Impact:** Medium — non-proportionate path improved; proportionate path improved more
  significantly due to hoisted relationship modifiers
- **Root cause:** Original `AVERAGEX(VALUES(WO Key), [Open Work Order Costs])` re-evaluated
  the full measure (CROSSFILTER + USERELATIONSHIP + KEEPFILTERS + proportionate SUMX) per
  WO key. Each iteration re-entered the relationship context.
- **Resolution:**
  1. Non-proportionate: replaced AVERAGEX with ADDCOLUMNS on VALUES to materialize per-WO
     costs in one SE query, then SUMX + COUNTROWS + DIVIDE on the in-memory result. Eliminated
     second SE roundtrip vs. separate SUM + DISTINCTCOUNT approach.
  2. Proportionate: stays as AVERAGEX (ownership pct varies per property per WO — math
     requires per-row iteration). Hoisting CROSSFILTER/USERELATIONSHIP into outer CALCULATE
     eliminated per-iteration relationship setup — this was the bigger win.
- **Observation:** Two-aggregate approach (separate SUM + DISTINCTCOUNT queries) was sometimes
  worse than original AVERAGEX in narrow filter contexts — the second SE query offset iterator
  savings. Single-pass ADDCOLUMNS resolved this.
- **Resolved:** Zero-cost WOs are excluded from the denominator. DAX updated 2026-04-16.
- **Validated:** 2026-04-11
- **Affected model:** Maintenance & Construction
- **Related measures:** [Avg Cost per Open Work Order], [Open Work Order Costs]
