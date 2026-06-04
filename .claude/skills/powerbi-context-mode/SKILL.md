---
name: powerbi-context-mode
description: Use for retrieval over project-specific large Power BI artifacts (parsed .bim schema markdown in artifacts/model-schema/, large diagnostic outputs in output/, DAX Studio query plans, BPA / VertiPaq exports, regression-diff JSON). Routes through Context Mode (ctx_index, ctx_search, ctx_execute_file) instead of direct Read. Does NOT intercept DAX coding, C# scripting, TMDL editing, BPA authoring, TE CLI workflows, live TOM, PBIR JSON, theme JSON, or Fabric CLI work — those belong to the data-goblin power-bi-agentic-development plugin skills.
---

# Power BI Context Mode — Project Retrieval Wrapper

## Purpose

Reduce token usage when Claude needs *what's in this user's model* — measure definitions, relationships, table descriptions, calc-group structure — without reading 12,000+ lines of generated schema markdown. Also covers large diagnostic exports staged locally.

This skill is a retrieval helper, not a workflow. It does not author DAX, generate C# scripts, edit TMDL, or operate on PBIR/theme files. Those are owned by the data-goblin plugin.

## Scope and Skill Priority

**Mission-critical data-goblin domains — `powerbi-context-mode` MUST NOT intercept:**

- **DAX optimization / authoring / debugging** → use `semantic-models:dax`
- **C# scripting / TOM / macros / XMLA** → use `tabular-editor:c-sharp-scripting`
- **TMDL editing / BIM-to-TMDL** → use `pbip:tmdl`
- **BPA rule discovery / authoring / validation** → use `tabular-editor:bpa-rules` or `tabular-editor:suggest-rule`
- **TE CLI deployment / scripting** → use `tabular-editor:te2-cli`
- **Live TOM connection / DAX validation against PBI Desktop** → use `pbi-desktop:connect-pbid`
- **PBIR JSON / theme JSON work** → use `reports:pbir-cli` and `reports:modifying-theme-json`
- **Fabric CLI / workspace operations** → use `fabric-cli:fabric-cli`
- **Plugin internal reference files** (`<plugin>/references/*.md`, `<plugin>/assets/*`) — managed by their own skill

When any of those data-goblin skills activates, follow ITS workflow. This skill stays silent.

**`powerbi-context-mode` applies ONLY to:**

- Retrieval over `artifacts/model-schema/*.md` (parsed `.bim` snapshots — large, project-specific)
- One-shot analysis of large diagnostic outputs in `output/` (BPA results, VertiPaq exports, DAX Studio Server Timings exports, query plans, regression-diff JSON)
- Retrieval over `knowledge/confluence/*.md` (cached team-standards pages — managed by the `confluence-cache` skill; this skill only handles the search side)
- Multi-file searches across `knowledge/` only when needed

## Source-of-Truth Detection

When a task needs model metadata (relationships, measure definitions, dependencies), run this chain in order. State the chosen layer in the response so the user knows the staleness.

### Step 1 — Tabular Editor CLI (live)

```
Bash: where TabularEditor.exe 2>/dev/null
Bash: where TabularEditor 2>/dev/null
```

Also check the `POWERBI_TE_CLI_PATH` environment variable if set (user can pin a specific install).

If found: use TE CLI for live metadata, BPA scans (`-A`), TMDL export (`-TMDL`).

### Step 2 — Parsed `.bim` snapshot (current fallback)

```
Glob: artifacts/model-schema/*.md
```

If files exist: use the snapshot via Context Mode (see "Context Mode Tool Selection" below). Always flag staleness — note the file's last-modified date. If no snapshot exists but a `.bim` is staged, offer to run `python scripts/bim_to_kb_markdown.py <path>.bim --output artifacts/model-schema/<name>.md` first.

### Step 3 — Power BI MCP server (third option)

```
Read: .mcp.json (project-local)
Read: ~/.claude/.claude.json (user MCP config)
```

Look for connector names containing `power-bi`, `pbi`, or `analysis-services`. If configured, use it for live queries it supports — but TE CLI is preferred when both are available because MCP servers vary in TOM coverage.

### Announcing the source layer

When delivering metadata-dependent analysis, prefix the response with the source:

> **Source: parsed `.bim` snapshot** (`artifacts/model-schema/model-schema-mc.md`, generated 2026-04-13) — note: live model may have diverged.

## Use Context Mode For

- `artifacts/model-schema/*.md` — when live TE CLI is unavailable (schema fallback)
- `output/*.json`, `output/*.csv`, `output/*.log` when large (BPA / VertiPaq / DAX Studio exports, regression-diff JSON)
- DAX Studio query plans / Server Timings exports staged locally
- `knowledge/confluence/*.md` — cached Confluence pages (stable source name `confluence-team-standards`). Re-index trigger: after the `confluence-cache` skill reports any `updated`. For pages NOT in the cache, use the live Atlassian MCP (`mcp__…__getConfluencePage`) directly instead of routing through this skill.
- Multi-file searches across `knowledge/` only when needed

### Size-routing rule

Before reading any file under those paths, check size first:

```
Bash: wc -l <path>
Glob: returns size metadata
```

If >500 lines or >50 KB, route through Context Mode. `ctx_index` is idempotent — calling it on an already-indexed unchanged file is cheap (returns cached state). Always-call-then-search is a safe default.

## Context Mode Tool Selection

| Artifact / situation | Tool | Why |
|---|---|---|
| Repeated retrieval over a stable schema dump | `ctx_index(path: ...)` then `ctx_search(...)` | Index once, search many times with stable source name |
| One-shot analysis of a large diagnostic file | `ctx_execute_file(path: ...)` | No need to keep the index around |
| Command/CLI output likely to be large | `ctx_execute(...)` | Marketplace plugin's PreToolUse hook handles many cases automatically |
| External docs (Microsoft Learn, SQLBI) | `ctx_fetch_and_index(...)` then `ctx_search(...)` | Standard external-docs pattern |

**Do not** call `ctx_index(content: <large_data>)`. Always pass `path:` so the file is read server-side rather than through context.

Use stable, descriptive source names: `model-schema-mc`, `bpa-2026-04`, `vertipaq-mc-2026-04`. Don't reuse the same name for different content.

## Explicitly NOT Scoped

These are owned by data-goblin plugin skills. Do not insert Context Mode into their workflows.

- **DAX coding / optimization** → `semantic-models:dax`
- **C# scripting / TOM / macros** → `tabular-editor:c-sharp-scripting`
- **TMDL editing** → `pbip:tmdl`
- **BPA rule discovery / authoring** → `tabular-editor:bpa-rules`, `tabular-editor:suggest-rule`
- **TE CLI workflows** → `tabular-editor:te2-cli`
- **Live TOM connection** → `pbi-desktop:connect-pbid`
- **PBIR JSON files** → `reports:pbir-cli`
- **Theme JSON files** → `reports:modifying-theme-json`
- **Fabric CLI / workspace ops** → `fabric-cli:fabric-cli`
- **Plugin reference files** (`<plugin>/references/*.md`, `<plugin>/assets/*`) — plugin-managed

## Compact Context Pack

Before proposing model-aware DAX or refactor recommendations, assemble the smallest useful retrieval pack from the chosen source:

- target object name (table/measure/relationship)
- measure DAX definition + dependency chain (1–2 levels)
- related tables and relevant columns
- relevant relationships with active/inactive flag and cross-filter direction
- related calculation groups, if any
- relevant `knowledge/` snippets (gotchas, validated patterns)
- known model-specific BPA / VertiPaq findings, if available

Exclude: unrelated tables, unrelated measures, full schema files, full DAX query result rows, full BPA / VertiPaq output, full trace logs.

## Knowledge Base Routing

Use `knowledge/knowledge-index.md` as the project routing manifest. It marks each file's access pattern (direct-read / Context Mode index/search / archived). The index covers ONLY files under `knowledge/`, `artifacts/`, and `.claude/skills/`. Plugin cache paths are out of scope.

## Final Response Expectations

For non-trivial Power BI tasks, structure the response as:

1. Source layer used (TE CLI / `.bim` snapshot / MCP) and any staleness note
2. Retrieved facts (relationships, definitions, dependencies — only what's relevant)
3. Recommendation (DAX, C# script handed off to data-goblin's domain skills, or refactor plan)
4. Local validation steps
5. Files written or to inspect (paths in `output/`, `scripts/`, `artifacts/`)
6. KB update candidates (only validated, reusable findings)

Do not paste large raw outputs into the final answer. Keep diagnostic dumps in `output/`; return summaries, snippets, object names, warnings, errors, and file paths.
