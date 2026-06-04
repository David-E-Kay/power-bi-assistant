---
name: refactor-strategy
description: "Use this skill when the user wants to restructure a Power BI semantic model's topology — eliminate bidirectional filtering, clean up relationships, activate/deactivate relationships, remove redundant paths, consolidate bridge tables, or address topology-driven performance bottlenecks. This skill orchestrates the full lifecycle (diagnose → audit → plan → codegen → validate → document) by delegating to specialist skills and keeping only the unique orchestration glue and C# codegen patterns. Trigger phrases: 'clean up the model', 'optimize relationships', 'refactor the model structure', 'remove bidirectional filtering', 'fix the bridge', 'eliminate the cross-filter scan', 'why is this relationship inactive', and any request involving relationship debt or model topology changes."
---

# Power BI Model Refactor Strategy

Topology refactor orchestrator + C# codegen for coordinated relationship/DAX cleanup. Model-agnostic — adapt to the model at hand.

## Specialist Handoff Map

When a phase below says "use [skill] for X", **invoke it via the `Skill` tool and follow it** — do not paraphrase the specialist's workflow inline. The handoff table is the contract.

| Sub-task | Skill | Invocation |
|---|---|---|
| DAX trace capture, SE/FE split, MDL001-010 layout choice | `/dax` | `Skill(skill='semantic-models:dax')` |
| Detecting bidir/M:M/inactive/unused-hidden via authored rule | `/bpa-rules` | `Skill(skill='tabular-editor:bpa-rules')` |
| TOM relationship CRUD, measure expression updates | `/c-sharp-scripting` | `Skill(skill='tabular-editor:c-sharp-scripting')` |
| Pre/post snapshot diff, Tier 1/2/3 validation | `regression-testing` | `Skill(skill='regression-testing')` |
| Re-parse `.bim` after refactor | `bim-parsing` | `Skill(skill='bim-parsing')` |

---

## Phase 1: Confirm Bottleneck Is Structural

Use `/dax` (see handoff map) for trace capture and SE/FE analysis first. Return here when `/dax` escalates to Tier 3 (MDL001-010) or surfaces a topology bottleneck.

A bottleneck is structural — not DAX-fixable — when:
- The same bridge/factless-fact scan appears across many different measures
- Bidir relationships cause large row materializations for filter propagation
- Filter paths traverse multiple tables when a direct FK would suffice
- Bridge tables exist for relationships that are actually 1:1

If structural, proceed to Phase 2.

---

## Phase 2: Impact Audit

### 2.1 Map the relationship chain for the problem area

Trace the filter propagation path from the user's slicer to the measure's fact table. Document:
- Which relationships are active vs inactive
- Which are bidir vs single direction
- Where bridge/factless-fact tables sit in the chain
- Which USERELATIONSHIP/CROSSFILTER calls measures use to override defaults

### 2.2 Identify bridge table necessity

For each bridge table, ask:
- **Is the FK unique?** If the bridge has a 1:1 relationship with a fact table, the FK columns could live directly on the fact table.
- **Is the relationship truly M:M?** If every fact row maps to exactly one dimension row, a direct FK replaces the bridge.
- **Is the bridge serving multiple purposes?** Some links may be 1:1 (could be direct FKs) while others are genuinely M:M (must stay as bridge).

DAX helper to check for duplicates:
```dax
EVALUATE
ROW (
    "Total Rows", COUNTROWS ( 'BridgeTable' ),
    "Distinct Keys", DISTINCTCOUNT ( 'BridgeTable'[FactKey] ),
    "Duplicate Keys",
        COUNTROWS (
            FILTER (
                SUMMARIZECOLUMNS (
                    'BridgeTable'[FactKey],
                    "Cnt", COUNTROWS ( 'BridgeTable' )
                ),
                [Cnt] > 1
            )
        )
)
```

### 2.3 Identify unused inactive relationships

Use `/bpa-rules` (see handoff map) to author and run a rule that flags inactive relationships with zero DAX references. Caveat: columns may be referenced in DAX but not through relationship traversal (e.g., same-table calculated columns). Only the relationship itself needs to be unreferenced, not the columns.

### 2.4 Identify bidir reduction candidates

For each bidir relationship, determine why bidir exists:
- **Many-to-one where filter needs to flow "backwards"** — check if a direct FK would eliminate the need.
- **True M:M** — bidir is structurally required. Cannot be reduced.
- **Historical accident** — bidir was set but single direction would work fine.

**Critical check before reducing bidir to single:** Count how many measures would need compensating `CROSSFILTER(…, BOTH)` calls. If the majority of measures referencing the relationship need reverse filter propagation, reducing bidir forces per-measure overrides that recreate the bidir scan at evaluation time — worse than keeping the default bidir. Only reduce when a small minority of measures need the reverse direction.

---

## Phase 3: Plan Topology Change

### 3.1 Propose direct FK relationships

For each dimension currently reached through a bridge:
- Can the FK column be added directly to the fact table? (ETL/source change)
- Does the fact table already have the FK column but no relationship? (model-only change)
- Would activating an existing inactive direct relationship work?

### 3.2 Check for ambiguous paths

Before activating or creating relationships, trace ALL paths from every table to every dimension. The engine rejects ambiguous paths — two active paths from table A to table B through different intermediaries.

**Bidir amplifies ambiguity:** When a bridge has bidir relationships, every active relationship connected to tables on either side of the bridge becomes a potential second path. Always re-check ambiguous paths with bidir on vs. off — a change that was safe with single-direction relationships may become ambiguous with bidir.

Resolution: keep one path inactive and use USERELATIONSHIP in measures. Choose the path used less frequently to stay inactive.

### 3.3 Categorize relationship changes

- **New relationships** (N) — direct FKs replacing bridge paths
- **Activate existing** (A) — inactive relationships becoming active
- **Change direction** (C) — bidir → single direction
- **Delete** (E) — redundant relationships after refactor
- **Delete unused** (U) — inactive relationships with zero DAX references

### 3.4 Categorize DAX measure changes

For each relationship change, find all measures affected:
- **Activated relationships** → remove corresponding USERELATIONSHIP calls
- **Deleted relationships** → remove corresponding USERELATIONSHIP **and** CROSSFILTER calls
- **Direction changes** → review measures that use CROSSFILTER(…, BOTH) on the changed relationship

Use regex pattern matching against the .bim to find affected measures. Group by pattern for bulk updates.

### 3.5 Document the plan

Produce: ETL/source changes required, each relationship change with rationale, ambiguous paths identified and resolution, all affected measures listed by category, implementation order.

---

## Phase 4: Codegen

Use `/c-sharp-scripting` (see handoff map) for TOM relationship CRUD primitives. This phase owns only the orchestration patterns unique to topology refactors.

### 4.1 Bulk DAX updates with regex

The centerpiece C# pattern for removing USERELATIONSHIP/CROSSFILTER calls including surrounding commas:

```csharp
var pattern = "(?i)[,\\s]*USERELATIONSHIP\\s*\\(\\s*'?TableA'?\\s*\\[...\\]...\\)\\s*,?";

foreach (var m in Model.AllMeasures)
{
    var modified = Regex.Replace(m.Expression, pattern, match => {
        // Smart comma handler: if match starts AND ends with comma, keep one
        bool startsComma = match.Value.TrimStart().StartsWith(",");
        bool endsComma = match.Value.TrimEnd().EndsWith(",");
        return (startsComma && endsComma) ? "," : "";
    });
    if (modified != m.Expression) m.Expression = modified;
}
```

### 4.2 Configuration toggles

Add boolean flags at the top of the script to enable/disable sections independently:
```csharp
var applyRelationshipChanges = true;
var applyDaxCleanup = true;
var removeRedundantRelationship = true; // set false for conservative first run
```

### 4.3 Two-script split

Produce two scripts:
1. **Core refactor script** — relationship changes + DAX updates (the structural changes)
2. **Cleanup script** — removal of unused inactive relationships (safe to run independently)

Separation reduces risk. The cleanup script can run after the refactor is validated.

### 4.4 Post-cleanup validation

After running scripts, check for:
- Comma/syntax errors in modified DAX (trailing commas, empty CALCULATE argument lists)
- Measures that reference deleted relationships (would error at query time) — scan for **both** USERELATIONSHIP and CROSSFILTER referencing the deleted relationship's columns
- Ambiguous path errors when saving to the service

---

## Phase 5: Validate

Use `regression-testing` (see handoff map) for pre/post snapshot diff. The N/A/C/E/U categories from Phase 3 map to regression tiers:
- **Tier 1:** Measures where the relationship path fundamentally changed (N, E categories)
- **Tier 2:** Measures where USERELATIONSHIP/CROSSFILTER calls were removed (A, C categories)
- **Tier 3:** Unmodified measures (verify no unintended side effects from topology changes)

---

## Phase 6: Document

Trigger the Session Learning Loop (defined in CLAUDE.md). Update `knowledge/{model}-design-decisions.md`, `{model}-gotchas.md`, and `{model}-dax-performance.md` as appropriate. If structural changes were made, re-parse the `.bim` using `bim-parsing` (see handoff map).

---

## Anti-Patterns

- **Speculative refactoring** — never refactor without a measured performance problem. Model changes are high-risk and affect all downstream reports.
- **Activating relationships without checking ambiguous paths** — always trace all active paths between every pair of tables before activating a relationship. Bidir relationships amplify the problem — a relationship activation that's safe with single-direction may create ambiguous paths when other relationships are bidir.
- **Removing inactive relationships without DAX audit** — an inactive relationship with zero USERELATIONSHIP references might still be needed for future development. Flag these for the user rather than deleting silently.
- **Changing bidir to single when most measures need reverse flow** — if the majority of measures referencing a relationship need filter propagation in both directions, reducing to single direction forces per-measure `CROSSFILTER(…, BOTH)` overrides. This is worse than bidir at the model level.
- **Deleting relationships without scanning for USERELATIONSHIP** — the DAX cleanup step for deleted relationships must scan for both CROSSFILTER and USERELATIONSHIP references. Missing USERELATIONSHIP calls creates runtime errors.
- **Denormalizing slicer fields onto fact tables** — only pre-compute columns used purely for calculations. Dimension fields used as user-facing slicers must stay on the dimension to filter naturally through relationships.
