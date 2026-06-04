# Design Decisions

<!-- Captures the "why" behind structural modeling choices. When someone asks
     "why is this relationship inactive?" or "why is there a bridge table?",
     the answer should be here. -->

## BridgeProjectProperty as factless fact bridge
- **Decision:** Use a many-to-many bridge table linking Projects to Properties rather than denormalizing the relationship onto fact tables
- **Rationale:** A single project can span multiple properties and a property can have multiple projects. The bridge preserves this cardinality without duplicating fact rows.
- **Tradeoff:** Adds query complexity (CROSSFILTER or calc column workarounds needed) and performance overhead (~2.6M rows in bridge). Accepted because denormalization would create worse data quality issues.
- **Date:** Pre-existing design

## Work Order (Factless) bridge — scope and refactor (Maintenance & Construction)
- **Decision:** Refactored the Work Order (Factless) bridge to serve only its core M:M purpose — the optional Work Orders ↔ Projects linkage. Previously it routed all dimensional filter propagation (Properties, Vendors, Resident) through a bidir bridge scan of ~2.4M rows.
- **Rationale:** Work Order Key is unique in the bridge (1:1 with Work Orders), and every Work Order already has Property Key and Vendor Key columns. The bridge pattern is designed for true M:M — but the only genuinely optional/partial-overlap relationship is Work Orders ↔ Projects (not all WOs have projects, not all projects have WOs). Properties and Vendors can connect directly to Work Orders.
- **Refactor history:**
  - **v1 (2026-03-25):** Added direct WO→Properties FK, activated PO Detail→Projects, changed bridge and OWO bidir to single direction, deleted Bridge→Properties, removed 35 USERELATIONSHIP/CROSSFILTER calls, removed 20 unused inactive relationships. Added compensating CROSSFILTER(BOTH) to 47 measures via Pass 1–4 scripts.
  - **v1 problem:** Changing bridge→WO to single direction broke Vendor and Project slicer filtering on 64 measures that lacked the compensating CROSSFILTER override. Also, activating PO Detail→Projects created an ambiguous path when combined with bidir bridge.
  - **v2 (2026-04-09):** Started from pre-refactor .bim. Applied only the safe structural changes, keeping the bridge bidir.
- **Changes applied (v2 — current state):**
  - **N1:** Added direct `Work Orders[Property Key] → Properties[Property Key]` (active, single direction)
  - **N2:** Added direct `Work Orders[Work Order Vendor Key] → Vendors[Vendor Key]` (active, single direction)
  - **E1:** Deleted `Bridge[Work Order Property Key] → Properties[Property Key]` (redundant with N1)
  - **E2:** Deleted `Bridge[Work Order Vendor Key] → Vendors[Vendor Key]` (redundant with N2)
  - **DAX-E1:** Removed 32 CROSSFILTER references to deleted E1 relationship. Cleanup gotcha: also scan for USERELATIONSHIP references to the same deleted relationship — v2 cleanup initially missed 2 such calls (rule: when deleting a relationship, scan for both CROSSFILTER and USERELATIONSHIP)
  - **Cleanup:** Removed 21 unused inactive relationships
  - Bridge→WO and OWO→WO remain **bidir** (not changed)
  - `PO Detail[Custom Project Key] → Projects[Project Key]` remains **inactive** (not activated)
- **What the bridge now connects (3 active relationships only):**
  - Bridge→Work Orders (bidir) — core WO↔Projects linkage
  - Bridge→Projects (bidir) — core WO↔Projects linkage
  - Bridge→Property Move In WO Flag (bidir) — small lookup
- **Ambiguous path gotchas (both still apply):**
  - `Projects[Property Key] → Properties[Property Key]` **MUST stay inactive** — activating it creates two paths from the bridge to Properties: Bridge→WO→Properties and Bridge→Projects→Properties
  - `PO Detail[Custom Project Key] → Projects[Project Key]` **MUST stay inactive** — activating it creates two paths from PO Detail to the bridge: PO Detail→WO→Bridge and PO Detail→Projects→Bridge (because bridge is bidir)
- **Performance impact:** N1 and N2 eliminated the bridge detour for Property and Vendor filtering on all pure WO measures (~100+ measures). The bridge bidir scan still occurs for measures that involve the WO↔Projects relationship, but this is a smaller set (~40-50 project measures).
- **Affected model:** Maintenance & Construction only.

## Separate Work Order vs. Project measure sets — date alignment (Maintenance & Construction)
- **Decision:** The Maintenance & Construction model maintains two distinct families of measures — work order measures and project measures — even though both may aggregate costs from the same underlying PO Detail table.
- **Rationale:** Work order measures must align counts and costs with work order dates (created date, completed date, etc.), while project measures must align with project dates (start date, target completion, etc.). The analytical questions are fundamentally different: "What did we spend on work orders completed this month?" vs. "What is the total cost of projects started this quarter?" These require different Calendar relationship activations (USERELATIONSHIP to the relevant date column) and different filter contexts.
- **Why not a hierarchy roll-up:** Although projects can contain work orders (and work orders can belong to projects), this is a ragged/optional relationship — not all work orders have projects, and not all projects have work orders. A single hierarchy roll-up would force a shared date context, which would misalign either the work order view or the project view. Separate measure sets allow each analytical lens to use its own date alignment without compromise.
- **Implications for DAX authoring:**
  - When writing or reviewing a PO Detail cost measure in the Maintenance & Construction model, always determine whether it serves the work order context or the project context — this governs which date column and USERELATIONSHIP call to use.
  - Project measures that aggregate PO costs use `USERELATIONSHIP('PO Detail'[Custom Project Key], Projects[Project Key])` to activate the inactive PO Detail→Projects path. This is required because PO Detail→Projects stays inactive to avoid ambiguous paths with the bidir bridge.
  - Do not assume a single "PO Costs" base measure can serve both contexts — the USERELATIONSHIP requirements differ depending on which date alignment the caller expects.
- **Date:** 2026-03-29 (documented; pattern is pre-existing). Updated 2026-04-09 to reflect v2 refactor (A2 reverted, PO Detail→Projects stays inactive).
- **Affected model:** Maintenance & Construction

## Multiple inactive Calendar relationships
- **Decision:** Fact tables have multiple date columns (e.g., WorkOrderDate, CompletionDate on FactWorkOrder) with only one active relationship to DimCalendar
- **Rationale:** Power BI allows only one active relationship between any two tables. The most commonly filtered date column gets the active relationship; others use USERELATIONSHIP in DAX.
- **Convention:** WorkOrderDate/PODate are typically the active relationship; CompletionDate and other secondary dates are inactive
- **Date:** Pre-existing design

## Occupancy fact table is a daily snapshot
- **Decision:** The `Occupancy` fact table in the Occupancy model is structured
  as a daily snapshot — one row per property (per composite key) per day — rather
  than a transactional/event fact.
- **Rationale:** Rent amounts, occupancy status, and resident attributes change
  over time for the same property. A snapshot grain preserves the time series so
  reports can compute period-over-period averages, point-in-time status, and
  daily/weekly/monthly trends without derived calculations.
- **Implications for DAX authoring:**
  - A single property has many rows across snapshot days. Measures that iterate
    Occupancy rows are iterating *property-days*, not *properties*.
  - Rent amount (`Resident Curr Rent Amt`) varies across snapshot days for the
    same property even when ownership pct is stable. Proportionate-rent measures
    must account for this — see `pbi-dax-patterns.md` →
    "Proportionate rent over daily-snapshot Occupancy — grain-dependent
    branching" for the validated pattern.
  - Ownership pct (`Ownership Percentage`) is typically constant per property
    across its snapshot rows. This uniformity is what allows AVERAGEX-style
    averaging at single-property grain to produce the correct proportionate
    result without additional denominator weighting.
  - Row counts scale with time × property count — traces and performance work
    should account for this when comparing Occupancy-based measures to
    transactional-fact measures in other models.
- **Date:** 2026-04-22 (documented; pattern is pre-existing).
- **Affected model:** Occupancy only. Other models may or may not use snapshot
  grain — confirm per model before transferring assumptions.

## Snowflake as source via medallion architecture
- **Decision:** All models source from Snowflake through bronze/silver/gold medallion layers, with Power BI importing from the gold layer
- **Rationale:** Centralizes transformation logic in Snowflake/dbt, keeps Power BI models as thin semantic layers, reduces refresh complexity
- **Date:** Pre-existing architecture

<!-- Add new decisions as they arise. Format:
## [Short title]
- **Decision:** What was decided
- **Rationale:** Why this choice over alternatives
- **Tradeoff:** What you give up
- **Date:** When decided
-->
