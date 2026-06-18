# Power BI Workspace — Project Instructions

You are operating inside a Power BI workspace project. Every conversation here involves Power BI semantic models, DAX, data modeling, performance tuning, or related analytics work.

You maintain a behavioral knowledge base (KB) of the user's models — performance findings, DAX gotchas, validated patterns, design decisions, and team standards. The KB lives in the `knowledge/` directory and the `.claude/skills/` directory.

**Routing rules:** Use `knowledge/knowledge-index.md` first when you need to find something. Read small curated files directly. For large generated artifacts (model schema dumps in `artifacts/model-schema/`, BPA/VertiPaq exports, DAX Studio query plans, regression-diff JSON in `output/`), `Grep` to locate the target span first, then `Read` only that span with `offset`/`limit` — never full-read these files into context.

---

## Project Structure

```
.claude/skills/                                 # Project-level skills (Anthropic convention: one folder per skill)
  bim-parsing/SKILL.md                          # .bim file parsing workflow
  measure-benchmarking/SKILL.md                 # Measure performance benchmarking workflow
  refactor-strategy/SKILL.md                    # Structural model refactor workflow
  regression-testing/SKILL.md                   # Regression testing workflow
  regression-testing/references/overview.md     # Reference docs for capture-snapshot parameters
  confluence-cache/SKILL.md                     # Confluence page cache lifecycle (grab → store → manifest)
  confluence-cache/references/                  # Setup, manifest schema, troubleshooting
knowledge/                                      # Curated KB (gotchas, patterns, performance, standards) — flat, small files
  knowledge-index.md                            # Routing manifest: access pattern per file
  confluence/                                   # Cached Confluence pages (OPTIONAL — managed by confluence-cache skill)
    _manifest.yaml                              # Source of truth for cached pages (empty template by default)
artifacts/                                      # Generated files (model-schema dumps, reports)
  model-schema/                                 # Parsed `.bim` markdown dumps (Grep-then-targeted-Read targets)
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

**Targeted retrieval over project-specific large artifacts** — for these, `Grep` to locate the target table/measure/relationship first, then `Read` only that span with `offset`/`limit`; never full-read them into context:
- `artifacts/model-schema/*.md` (parsed `.bim` snapshots, when live TE CLI is unavailable)
- Large diagnostics in `output/` (BPA / VertiPaq / DAX Studio Server Timings / regression-diff JSON)
- `knowledge/confluence/*.md` (cached team-standards pages — small, so direct-`Read` or `Grep` across the folder; the `confluence-cache` skill manages writes)
- Multi-file searches across `knowledge/` when needed

For model metadata, follow the source-of-truth chain: live Tabular Editor CLI → live TOM export (`scripts/export_schema.py`) → parsed `.bim` snapshot (`bim_to_kb_markdown.py`, in `artifacts/model-schema/`) → Power BI MCP. Prefer live sources when available; flag staleness when working from a snapshot.

**`confluence-cache` skill** (project-local) owns *the grab-and-curate lifecycle for Confluence pages*: resolve a page (URL/ID/title), fetch via the official Atlassian MCP in markdown format, write to `knowledge/confluence/<slug>.md`, and update `_manifest.yaml`. For ad-hoc Confluence questions that don't need caching, route directly to the live Atlassian MCP — don't invoke this skill. See `.claude/skills/confluence-cache/SKILL.md`.

Never modify marketplace plugin files in `~/.claude/plugins/cache/`.

**Tier 3 MDL escalation:** When `/dax` identifies a Tier 3 (MDL001-010) topology change as the right fix — removing bidir, consolidating relationships, eliminating bridges — invoke `refactor-strategy` via the `Skill` tool to orchestrate the impact audit, C# codegen, and validation. `/dax` describes the layout; `refactor-strategy` executes the change.

---

## User Working Style

- **Automation over manual edits.** Always prefer Tabular Editor C# scripts for model changes. The user runs scripts in TE3 against a local PBIP or via XMLA to Fabric. Never suggest "open Power BI Desktop and click…" when a script can do it.
- **TE-first semantic model mutations.** For semantic model mutations that affect DAX, relationships, object structure, calculation groups, bulk metadata, or production models, generate a Tabular Editor C# script or `te`-based repeatable operation first. Prefer this path because it is auditable, repeatable, cheaper to rerun, and reviewable/undoable in Tabular Editor before commit. Use MCP only for inspection, metadata discovery, validation, or explicitly approved low-risk single-object edits.
- **Incremental validation.** The user prefers configuration toggles and phased rollouts. Present changes in reviewable chunks, not monolithic scripts.
- **Copy-paste-ready DAX.** When presenting DAX measures, include all USERELATIONSHIP, CROSSFILTER, and KEEPFILTERS lines — never abbreviate with "add your filters here." The user copies directly into TE or the service.
- **Trace-driven optimization.** Performance work starts from DAX Studio traces with Server Timings. Always ask for the SE/FE split and row counts before proposing optimizations.
- **Local execution.** You can run Python scripts directly in this project. Write output files to `output/`. Edit KB files in place — no download/re-upload needed.

---

## Your Workflow

### 1. Consult what you know without overloading context

Start every Power BI task at `knowledge/knowledge-index.md` to identify the relevant KB file and its access pattern (direct-read vs Grep-then-targeted-Read). Direct-read for small curated files; for large generated artifacts, `Grep` to locate the span, then `Read` only that span with `offset`/`limit`.

| Question type | File / source |
|---|---|
| Per-model gotchas, performance findings, design decisions | `knowledge/{model}-gotchas.md`, `knowledge/{model}-dax-performance.md`, `knowledge/{model}-design-decisions.md` (direct-read — created on demand when a model is onboarded; none ship by default) |
| Validated DAX patterns (transferable) | `knowledge/pbi-dax-patterns.md` (direct-read) |
| Team modeling standards (team-wide, local KB) | `knowledge/pbi-modeling-standards.md` (direct-read) |
| Team standards published in Confluence (cached) — **optional** | `knowledge/confluence/` (direct-read, or `Grep` across the folder). Off by default; configure via the `confluence-cache` skill to enable. For pages NOT cached, use the live Atlassian MCP. |
| Model inventory and structure (per-model snapshot) | `artifacts/model-schema/model-schema-<model>.md` (`Grep` to locate, then targeted `Read` — do not full-read) |
| Topology refactor orchestration (post `/dax` Tier 3, or relationship-debt cleanup) | `.claude/skills/refactor-strategy/SKILL.md` |
| Regression testing procedures | `.claude/skills/regression-testing/SKILL.md` |
| Measure benchmarking | `.claude/skills/measure-benchmarking/SKILL.md` |
| .bim file parsing | `.claude/skills/bim-parsing/SKILL.md` |
| Confluence page caching (grab/refresh/list/remove) | `.claude/skills/confluence-cache/SKILL.md` |

Use the retrieved context to give model-specific advice rather than generic guidance — e.g., if you know the `Date` table uses inactive relationships, suggest `CALCULATE` with the specific `USERELATIONSHIP` rather than a bare `CALCULATE`. For schema-derived facts, prefer live TE CLI / TOM when available; otherwise use the snapshot at `artifacts/model-schema/` and flag staleness in your answer.

### 2. Defer to data-goblin plugin skills for plugin-domain work

For DAX, C# scripting / TOM, TMDL, BPA, TE CLI, live TOM, PBIR / theme, Fabric CLI, lineage, refresh, naming, and Power Query — see the Context Routing table above and follow the relevant data-goblin skill. Don't restate or reroute their workflows.

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

**Persist the durable ones.** The committed `knowledge/` files are the portable, repo-scoped memory that ships with the project. If your Claude Code setup also provides cross-session memory (user-level `~/.claude/CLAUDE.md` or a memory tool), mirror only the most critical, frequently-referenced findings there.

### What's worth keeping

Good entries are reusable, non-obvious, specific, and validated. Skip generic DAX knowledge, one-off debugging states, and unconfirmed structural facts.

**Where it lands — and the bar for shipping it:**
- **Transferable, model-agnostic patterns** → the shared `knowledge/pbi-dax-patterns.md`.
  The bar here is high: include a pattern only when it is **100% valid, genuinely reusable
  across models, and free of any company- or model-specific business logic**. When in doubt,
  keep it out of the shared file.
- **Model-specific findings** (a measure's quirk, a relationship trap, an optimization tied
  to one model's grain) → that model's per-model KB files (`{model}-dax-performance.md`,
  `{model}-gotchas.md`, `{model}-design-decisions.md`) — created on demand via this loop,
  never mixed into the shared `pbi-*` files.

The shared `pbi-*` files ship as a small curated **seed**, not a dumping ground — most
knowledge should accumulate per-model through the loop rather than be pre-populated.

---

## Script Execution

You can run scripts directly in this project:

- **Python scripts:** `python scripts/compare-snapshots.py before.json after.json`
- **Schema generation (from a `.bim`):** `python scripts/bim_to_kb_markdown.py path/to/model.bim --output artifacts/model-schema/<name>.md`
- **Live schema export (model open in PBI Desktop):** `python scripts/export_schema.py` — TE-free, via TOM (one-time setup: `python scripts/pbi_capture/provision_libs.py`)

Write new scripts to `scripts/`. Write output files (reports, diffs, snapshots) to `output/`.

For any script, TE CLI, parser, benchmark, trace, or validation command that may produce large output, write full results to `output/` and analyze them with `Grep` + targeted `Read` (never full-read the raw dump). Return concise summaries, warnings, errors, changed object names, and file paths — not raw dumps.

C# scripts (.csx) cannot be executed directly here — they run in Tabular Editor. Write them to `scripts/` or `output/` for the user to open in TE3.

---

## .bim File Onboarding

When the user provides a `.bim` file:

1. Run `python scripts/bim_to_kb_markdown.py path/to/file.bim --output artifacts/model-schema/<name>.md` to generate the schema markdown
2. Present a brief summary (table/measure/relationship counts, model name) for review

**Live alternative (model open, no `.bim` file):** run `python scripts/export_schema.py` to export an open Power BI Desktop model's schema directly via TOM — no Tabular Editor required. It serializes the live model to `.bim` and runs the same parser, producing the same `artifacts/model-schema/model-schema-<slug>.md`. One-time prerequisite: `python scripts/pbi_capture/provision_libs.py` (downloads the Analysis Services client DLLs into `libs/`).

Generated schema markdown is a documentation/cache snapshot — do NOT read it wholesale after onboarding. `Grep` to locate the target object, then `Read` only that span. When live TE CLI / TOM is available, prefer it as the source of truth.

---

## Multi-Model Awareness

The user may work across multiple Power BI semantic models. Always tag findings to the correct model — per-model behavioral KB files follow the `{model}-gotchas.md` / `{model}-dax-performance.md` / `{model}-design-decisions.md` naming pattern and are created on demand when a model is onboarded. None ship by default; the shipped generic KB is `pbi-dax-patterns.md` + `pbi-modeling-standards.md`.

---

## Regression Testing

For the full workflow, see `.claude/skills/regression-testing/SKILL.md`. Capture runs on the TE-free Python path: `python scripts/capture_snapshot.py --config output/{label}.config.json`. The engine (`scripts/pbi_capture/`) and `scripts/compare-snapshots.py` are stable and never edited per session — Claude authors only a JSON config (test cases + dimension map; schema in `docs/config-schema.md`). The retired `scripts/legacy-tabular-editor/capture-snapshot.csx` remains available on request for Tabular Editor 3.
