# Power BI Workspace — Project Instructions

You are operating inside a Power BI workspace project. Every conversation here involves Power BI semantic models, DAX, data modeling, performance tuning, or related analytics work.

You maintain a behavioral knowledge base (KB) of the user's models — performance findings, DAX gotchas, validated patterns, design decisions, and team standards. The KB lives in the `knowledge/` directory and the `.claude/skills/` directory.

**Routing rules:** Use `knowledge/knowledge-index.md` first when you need to find something. Read small curated files directly. For large generated artifacts (model schema dumps in `artifacts/model-schema/`, BPA/VertiPaq exports, DAX Studio query plans, regression-diff JSON in `output/`), use the `powerbi-context-mode` skill (`ctx_index` / `ctx_search` / `ctx_execute_file`) rather than direct `Read`.

---

## Project Structure

```
.claude/skills/                                 # Project-level skills (Anthropic convention: one folder per skill)
  powerbi-context-mode/SKILL.md                 # Context Mode retrieval routing (project artifacts only)
  bim-parsing/SKILL.md                          # .bim file parsing workflow
  measure-benchmarking/SKILL.md                 # Measure performance benchmarking workflow
  refactor-strategy/SKILL.md                    # Structural model refactor workflow
  regression-testing/SKILL.md                   # Regression testing workflow
  regression-testing/references/overview.md     # Reference docs for capture-snapshot parameters
  confluence-cache/SKILL.md                     # Confluence page cache lifecycle (grab → store → index)
  confluence-cache/references/                  # Setup, manifest schema, troubleshooting
knowledge/                                      # Curated KB (gotchas, patterns, performance, standards) — flat, small files
  knowledge-index.md                            # Routing manifest: access pattern per file
  confluence/                                   # Cached Confluence pages (OPTIONAL — managed by confluence-cache skill)
    _manifest.yaml                              # Source of truth for cached pages (empty template by default)
artifacts/                                      # Generated files (model-schema dumps, reports)
  model-schema/                                 # Parsed `.bim` markdown dumps (Context Mode index targets)
scripts/                                        # Runnable automation (Python, C# .csx)
output/                                         # Session deliverables (regression scripts, diagnostics)
```

### Context Routing

Two layers handle Power BI work in this project:

**Data-goblin plugin skills** (`power-bi-agentic-development`, installed) own *how to do* — they activate automatically and take priority for their domains:

| Domain | Skill |
|---|---|
| DAX optimization / authoring / debugging | `semantic-models:dax` |
| C# scripting / TOM / macros / XMLA | `tabular-editor:c-sharp-scripting` |
| TMDL editing / BIM-to-TMDL | `pbip:tmdl` |
| BPA rules (discovery, authoring) | `tabular-editor:bpa-rules`, `tabular-editor:suggest-rule` |
| TE CLI deployment / scripting | `tabular-editor:te2-cli` |
| Live TOM / DAX validation against PBI Desktop | `pbi-desktop:connect-pbid` |
| PBIR JSON / theme JSON | `reports:pbir-cli`, `reports:modifying-theme-json` |
| Fabric CLI / workspace ops | `fabric-cli:fabric-cli` |
| Lineage / Power Query / refresh / naming / model review | `semantic-models:*` |

**`powerbi-context-mode` skill** (project-local) owns *retrieval over project-specific large artifacts* — only:
- `artifacts/model-schema/*.md` (parsed `.bim` snapshots, when live TE CLI is unavailable)
- Large diagnostics in `output/` (BPA / VertiPaq / DAX Studio Server Timings / regression-diff JSON)
- `knowledge/confluence/*.md` (cached team-standards pages; the `confluence-cache` skill manages writes — this one handles search)
- Multi-file searches across `knowledge/` when needed

It is a retrieval helper, not a competing workflow. See `.claude/skills/powerbi-context-mode/SKILL.md` for source-of-truth detection (TE CLI → `bim_to_kb_markdown.py` snapshot → Power BI MCP) and tool selection rules.

**`confluence-cache` skill** (project-local) owns *the grab-and-curate lifecycle for Confluence pages*: resolve a page (URL/ID/title), fetch via the official Atlassian MCP in markdown format, write to `knowledge/confluence/<slug>.md`, update `_manifest.yaml`, and re-index. For ad-hoc Confluence questions that don't need caching, route directly to the live Atlassian MCP — don't invoke this skill. See `.claude/skills/confluence-cache/SKILL.md`.

Never modify marketplace plugin files in `~/.claude/plugins/cache/`.

**Tier 3 MDL escalation:** When `/dax` identifies a Tier 3 (MDL001-010) topology change as the right fix — removing bidir, consolidating relationships, eliminating bridges — invoke `refactor-strategy` via the `Skill` tool to orchestrate the impact audit, C# codegen, and validation. `/dax` describes the layout; `refactor-strategy` executes the change.

---

## User Working Style

- **Automation over manual edits.** Always prefer Tabular Editor C# scripts for model changes. The user runs scripts in TE3 against a local PBIP or via XMLA to Fabric. Never suggest "open Power BI Desktop and click…" when a script can do it.
- **Incremental validation.** The user prefers configuration toggles and phased rollouts. Present changes in reviewable chunks, not monolithic scripts.
- **Copy-paste-ready DAX.** When presenting DAX measures, include all USERELATIONSHIP, CROSSFILTER, and KEEPFILTERS lines — never abbreviate with "add your filters here." The user copies directly into TE or the service.
- **Trace-driven optimization.** Performance work starts from DAX Studio traces with Server Timings. Always ask for the SE/FE split and row counts before proposing optimizations.
- **Local execution.** You can run Python scripts directly in this project. Write output files to `output/`. Edit KB files in place — no download/re-upload needed.

---

## Your Workflow

### 1. Consult what you know without overloading context

Start every Power BI task at `knowledge/knowledge-index.md` to identify the relevant KB file and its access pattern (direct-read vs Context Mode index/search). Direct-read for small curated files; route large generated artifacts through `powerbi-context-mode`.

| Question type | File / source |
|---|---|
| Per-model gotchas, performance findings, design decisions | `knowledge/{model}-gotchas.md`, `knowledge/{model}-dax-performance.md`, `knowledge/{model}-design-decisions.md` (direct-read — created on demand when a model is onboarded; none ship by default) |
| Validated DAX patterns (transferable) | `knowledge/pbi-dax-patterns.md` (direct-read) |
| Team modeling standards (team-wide, local KB) | `knowledge/pbi-modeling-standards.md` (direct-read) |
| Team standards published in Confluence (cached) — **optional** | `knowledge/confluence/` (Context Mode index/search). Off by default; configure via the `confluence-cache` skill to enable. For pages NOT cached, use the live Atlassian MCP. |
| Model inventory and structure (per-model snapshot) | `artifacts/model-schema/model-schema-<model>.md` (Context Mode index/search — do not full-read) |
| Topology refactor orchestration (post `/dax` Tier 3, or relationship-debt cleanup) | `.claude/skills/refactor-strategy/SKILL.md` |
| Regression testing procedures | `.claude/skills/regression-testing/SKILL.md` |
| Measure benchmarking | `.claude/skills/measure-benchmarking/SKILL.md` |
| .bim file parsing | `.claude/skills/bim-parsing/SKILL.md` |
| Confluence page caching (grab/refresh/list/remove) | `.claude/skills/confluence-cache/SKILL.md` |
| Context Mode routing | `.claude/skills/powerbi-context-mode/SKILL.md` |

Use the retrieved context to give model-specific advice rather than generic guidance — e.g., if you know the `Date` table uses inactive relationships, suggest `CALCULATE` with the specific `USERELATIONSHIP` rather than a bare `CALCULATE`. For schema-derived facts, prefer live TE CLI / TOM when available; otherwise use the snapshot at `artifacts/model-schema/` and flag staleness in your answer.

### 2. Defer to data-goblin plugin skills for plugin-domain work

For DAX, C# scripting / TOM, TMDL, BPA, TE CLI, live TOM, PBIR / theme, Fabric CLI, lineage, refresh, naming, and Power Query — see the Context Routing table above and follow the relevant data-goblin skill. Don't restate or reroute their workflows. `powerbi-context-mode` does not intercept those.

### 3. Answer with full context

Combine behavioral knowledge + structural context for precise, model-specific guidance. Examples:

- If `{model}-gotchas.md` says a key column is TEXT → warn about implicit cast failures before the user hits them
- If `{model}-dax-performance.md` says a bridge bidir scan was eliminated by a prior refactor → don't suggest the old CROSSFILTER workaround
- If `pbi-dax-patterns.md` has a validated row/total branching pattern → use it instead of guessing
- If `pbi-modeling-standards.md` specifies naming conventions → apply them when creating measures

### 4. Flag conflicts

If something in the conversation contradicts the KB, surface it immediately:

> **Conflict detected:** The KB records `'Date'[Period]` as TEXT type, but your DAX is successfully joining it as DATE. Did something change? Should I update the KB?

Never silently work from stale knowledge.

### 5. Watch for anti-patterns

Proactively flag these anti-patterns when you see them:

- **Speculative refactoring** — Never refactor without a measured performance problem.
- **Denormalizing slicer fields** onto fact tables — use calc columns only for calculation-only values, never for user-facing report slicers.
- **Flagging SUMMARIZECOLUMNS inside CALCULATETABLE** as a problem — it's the correct pattern per SQLBI.
- **Removing inactive relationships without DAX audit** — always scan all DAX for USERELATIONSHIP/CROSSFILTER references first.
- **Assuming bidir can be removed** without checking for CROSSFILTER(…, BOTH) calls in measures.
- **Activating relationships without checking ambiguous paths** — always trace all active paths between every pair of tables.
---

## DAX Review Checklist

Apply this checklist when reviewing or writing measures:

1. **Shared filter hoisting** — If the measure combines multiple CALCULATE blocks, can shared KEEPFILTERS be hoisted into a single outer CALCULATE?
2. **Redundant filter elimination** — If the measure wraps another measure in CALCULATE, trace the dependency chain. Does the base measure already apply any of the same filters? Remove exact duplicates.
3. **Calc column denormalization rule** — If suggesting a calc column, is it for calculation-only use or for slicing? Only the former is valid.
4. **TEXT type columns** — Check if any referenced column is TEXT type when arithmetic is needed. Add explicit `VALUE()` conversion.
5. **SUMMARIZECOLUMNS context** — If using SUMMARIZECOLUMNS inside a measure, it MUST be wrapped in CALCULATETABLE.
6. **Ambiguous path check** — If suggesting USERELATIONSHIP or activating a relationship, trace all active paths between the two tables.
7. **Inline comments** — Does the measure include comments explaining relationship activation, filter rationale, branch logic, performance choices, and business rules?

---

## Change Workflow

When making model changes:

1. **Diagnose** — Start from a concrete symptom, not speculation.
2. **Consult the KB** — Read the relevant files from `knowledge/` and `.claude/skills/`.
3. **Propose the change** — Present DAX or model change with rationale.
4. **Generate automation** — Write Tabular Editor C# scripts. Save them to `scripts/` or `output/`.
5. **Test** — Follow `.claude/skills/regression-testing/SKILL.md`. Generate capture scripts, run comparisons.
6. **Document** — Trigger the Session Learning Loop. Update KB files directly.

---

## Session Learning Loop

### During the session

Tag findings that fall into these categories:
- Performance discovery
- Behavioral surprise
- Validated DAX pattern
- Design insight
- New model structure
- Modeling standard
- Stale/incorrect KB entry

### At session end (or at natural breakpoints)

If ANY findings occurred, present a structured summary:

```
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
📝 Session Learnings
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

1. [Performance] Description of finding
   → Update KB file? (Y/N)

2. [Gotcha] Description of finding
   → Update KB file? (Y/N)

Nothing new for: design-decisions, modeling-standards
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
```

### On confirmation

**Update KB files directly.** You have write access to `knowledge/` and `.claude/skills/`. Edit the appropriate file in place — append the new finding in the existing format. No download/re-upload cycle needed.

**Update memory edits** for critical, frequently-referenced findings that should persist across all projects.

### What's worth keeping

Good entries are reusable, non-obvious, specific, and validated. Skip generic DAX knowledge, one-off debugging states, and unconfirmed structural facts.

---

## Script Execution

You can run scripts directly in this project:

- **Python scripts:** `python scripts/compare-snapshots.py before.json after.json`
- **Schema generation:** `python scripts/bim_to_kb_markdown.py path/to/model.bim --output artifacts/model-schema/<name>.md`

Write new scripts to `scripts/`. Write output files (reports, diffs, snapshots) to `output/`.

For any script, TE CLI, parser, benchmark, trace, or validation command that may produce large output, write full results to `output/` and use `powerbi-context-mode` (`ctx_execute_file` / `ctx_search`) to analyze. Return concise summaries, warnings, errors, changed object names, and file paths — not raw dumps.

C# scripts (.csx) cannot be executed directly here — they run in Tabular Editor. Write them to `scripts/` or `output/` for the user to open in TE3.

---

## .bim File Onboarding

When the user provides a `.bim` file:

1. Run `python scripts/bim_to_kb_markdown.py path/to/file.bim --output artifacts/model-schema/<name>.md` to generate the schema markdown
2. Present a brief summary (table/measure/relationship counts, model name) for review

Generated schema markdown is a documentation/cache snapshot — do NOT read it wholesale after onboarding. Use `powerbi-context-mode` (`ctx_index` + `ctx_search`) for targeted retrieval. When live TE CLI / TOM is available, prefer it as the source of truth.

---

## Multi-Model Awareness

The user may work across multiple Power BI semantic models. Always tag findings to the correct model — per-model behavioral KB files follow the `{model}-gotchas.md` / `{model}-dax-performance.md` / `{model}-design-decisions.md` naming pattern and are created on demand when a model is onboarded. None ship by default; the shipped generic KB is `pbi-dax-patterns.md` + `pbi-modeling-standards.md`.

---

## Regression Testing

For the full workflow, see `.claude/skills/regression-testing/SKILL.md`. `scripts/capture-snapshot.csx` is a read-only template — never modify it; copy to `output/{model}-{label}.csx` and inject session-specific config there. Claude generates the `testLines` and `groupByColumns` blocks at runtime from the model schema — no pre-built helper scripts are needed.

## graphify

This project has a knowledge graph at graphify-out/ with god nodes, community structure, and cross-file relationships.

Rules:
- For codebase questions, first run `graphify query "<question>"` when graphify-out/graph.json exists. Use `graphify path "<A>" "<B>"` for relationships and `graphify explain "<concept>"` for focused concepts. These return a scoped subgraph, usually much smaller than GRAPH_REPORT.md or raw grep output.
- If graphify-out/wiki/index.md exists, use it for broad navigation instead of raw source browsing.
- Read graphify-out/GRAPH_REPORT.md only for broad architecture review or when query/path/explain do not surface enough context.
- After modifying code, run `graphify update .` to keep the graph current (AST-only, no API cost).
