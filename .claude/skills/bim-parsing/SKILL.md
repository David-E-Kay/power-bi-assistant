---
name: bim-parsing
description: "Use this skill when the user uploads a .bim file, asks to 'parse the bim', 'extract the model schema', 'generate model markdown', 'update the schema file', 'onboard a model', or any request involving extracting structural metadata from a Power BI .bim (Tabular Object Model JSON) file. Covers the full lifecycle: .bim parsing, structural summary, and RAG-optimized markdown generation. The single script involved is scripts/bim_to_kb_markdown.py, which writes a markdown schema to artifacts/model-schema/. The script is a stable, tested template — run it in place, never modify it in-session."
---

# .bim Parsing for Power BI Models

A procedural skill that defines the exact execution sequence for parsing Power BI `.bim` files and producing persistent model schema artifacts. One script handles this:

| Script | Output | Purpose | Persist? |
|--------|--------|---------|----------|
| `scripts/bim_to_kb_markdown.py` | Markdown (`artifacts/model-schema/model-schema-<slug>.md`) | RAG-retrievable model metadata for future sessions | Yes |

The script is a **stable, tested template** in `scripts/`. Run it in place — **never modify it in-session**. If the script fails, fix the underlying script in `scripts/` so all future sessions inherit the fix; do not patch in-session for a single run.

## Live alternative: export directly from an open model (no `.bim` file)

When the model is **open in Power BI Desktop** and you don't have a `.bim` on disk, produce the same artifact without Tabular Editor:

```bash
python scripts/export_schema.py            # auto-discovers the local Desktop instance
```

This connects to the live model over the local Analysis Services port via TOM, serializes it to `.bim` JSON (written to `output/`), then feeds it through the **same** `bim_to_kb_markdown.py` parser — producing `artifacts/model-schema/model-schema-<slug>.md` identically. Optional flags: `--port N` / `--connection-string S` to target a specific instance, `--name` to override the model name, `--md-out` / `--bim-out` for explicit paths.

**One-time prerequisite:** provision the Analysis Services client DLLs into `libs/` (downloaded from NuGet — no Tabular Editor or .NET SDK required):

```bash
python scripts/pbi_capture/provision_libs.py
```

Everything below applies equally to the live path — the markdown is the same generated cache snapshot.

## When to Use This Skill

Trigger when:
- The user uploads a `.bim` file (any filename ending in `.bim`)
- The user asks to parse, onboard, or extract a model schema
- The user asks to regenerate the model schema markdown
- Step 5 of the `.bim File Onboarding` section in project instructions applies

Do NOT use for:
- Reading an existing `artifacts/model-schema/model-schema-*.md` file — `Grep` to locate the target object, then `Read` only that span rather than full-reading it
- Behavioral findings like performance, gotchas, or patterns (those go in their respective `knowledge/` KB files)
- DAX dependency analysis on a specific measure (use live TE CLI / TOM, or parse the .bim JSON inline for one-off queries)

## Core Principles

1. **Script is a template, not a starting point.** Never regenerate or modify the parsing script in-session. If a bug is found, fix it in `scripts/` so the change persists for future sessions; do not patch in-session for a single run.
2. **Markdown schema is for RAG.** The markdown file is structured with headings per table/section so RAG can retrieve relevant chunks. Full DAX expressions and relationship metadata are preserved so dependency tracing and filter analysis work correctly.
3. **Only regenerate on structural changes.** The markdown schema file should be replaced only when the TOM structure materially changes: new/removed tables, relationship topology changes, new measures, or measure rewrites the user needs Claude to reference. Behavioral findings (performance, gotchas, patterns) belong in their respective KB files, not in the schema markdown.
4. **Present a summary after generation.** After running the script, summarize what was produced (table/measure/relationship counts, output path) so the user can verify the parse looks right.

---

## Execution Procedure

### Step 1: Locate the .bim file

The user provides a path (usually somewhere they've saved the file locally). No copying or staging needed — the script runs against the path directly.

### Step 2: Generate the markdown schema

```bash
python scripts/bim_to_kb_markdown.py "<bim_path>" --model-name "<Model Display Name>" -o artifacts/model-schema/model-schema-<slug>.md
```

Flag values:
- `--model-name`: The human-readable model name (e.g., `"Sales"`, `"Inventory"`)
- `-o`: Output path. Write directly to `artifacts/model-schema/model-schema-<slug>.md` where `<slug>` is a lowercase hyphenated version of the model name (e.g., `artifacts/model-schema/model-schema-sales.md`, `artifacts/model-schema/model-schema-inventory.md`)

If the user doesn't specify a model name, the script derives one from the .bim metadata or filename.

### Step 3: Confirm outputs

The markdown file is now in `artifacts/model-schema/` from Step 2 — no copy/upload step needed. Tell the user:
- The schema markdown is at `artifacts/model-schema/model-schema-<slug>.md`. It is a generated cache/snapshot.
- Total counts (tables, columns, measures, relationships, calculation groups) for sanity check.
- For future sessions, retrieve from this file with `Grep` + targeted `Read` rather than full-reading it.
- If replacing an existing schema file, the previous version was overwritten by the `-o` flag.

---

## Known .bim Format Characteristics

These are handled by the script. Document them here so that if a new edge case surfaces, the fix can be evaluated against this list before patching.

### JSON array expressions and descriptions

The TOM serialization format stores `expression` and `description` fields as **either** a plain string **or** a JSON array of strings (one element per line). This is not an edge case — it's the standard format for multi-line DAX expressions in .bim files exported from Tabular Editor or the XMLA endpoint.

The script normalizes these:
- `expression` fields: `"\n".join(array)` to reconstruct multi-line DAX
- `description` fields: `" ".join(array)` to reconstruct prose

If a new field exhibits this pattern, add the same normalization. The `bim_to_kb_markdown.py` script uses a `normalize_expression()` helper.

### BOM encoding

.bim files may include a UTF-8 BOM (byte order mark). The script opens files with `encoding="utf-8-sig"` to handle this transparently.

### isActive defaults

In TOM JSON, `isActive` on relationships defaults to `true` when the property is absent. The script handles this: `rel.get("isActive", True)`.

### Cardinality and cross-filter values

TOM uses both string values (`"manyToOne"`, `"oneDirection"`) and numeric values (`1`, `2`) depending on the .bim version. The script maps both formats.

---

## Known Limitations

### Table type inference

The heuristic classifies tables based on naming conventions first, then relationship topology. Tables that sit on both the "many" and "one" sides of multiple relationships get classified as `BRIDGE`, which is often wrong for core operational tables.

Table-type inference is heuristic and may need per-model correction. Common misclassifications:
- A dimension with many FKs in and out can be mislabeled `BRIDGE`.
- A core operational fact/snapshot table sitting between dimensions can be mislabeled `BRIDGE`.
- A snapshot/fact table with few inbound relationships can be mislabeled `DIM`.

The type labels in the markdown are for orientation only — they don't affect the structural metadata (columns, relationships, measures all render correctly regardless). If the user wants corrected types, they can either:
- Override the type manually in the output file after generation
- Request a `--type-overrides` flag be added to the script (future enhancement)

### Measure home table

The .bim stores measures on a specific table. Many models put most measures on a dedicated "Measure" table (a disconnected table with no columns). The markdown groups measures by their home table, so a model with a Measure table will have one very large section. This is by design — the heading structure still enables RAG retrieval of individual measures by name.

---

## Markdown Schema Structure

The output markdown is structured for optimal RAG chunking:

```
# Model Schema: <Model Name>               ← L1: model identity + parse metadata
## Table Inventory                          ← L2: compact summary table
## Relationships                            ← L2: split into Active / Inactive
### Active Relationships                    ← L3: markdown table with from→to
### Inactive Relationships                  ← L3: markdown table + USERELATIONSHIP note
## Table Details                            ← L2: one subsection per table
### <Table Name> (<TYPE>)                   ← L3: columns, calc columns, hierarchies
## Calculation Groups                       ← L2: one subsection per calc group
### <Calc Group Name>                       ← L3: items with ordinals and DAX
## Measures                                 ← L2: one subsection per home table
### Measures on <Table Name>                ← L3: grouped by display folder
#### <Measure Name>                         ← L4: full DAX in code block
```

Each heading level creates a natural RAG chunk boundary. When Claude searches for "Sales relationships," RAG returns the relationships section and the Sales table detail — not the entire 12,000-line file.

---

## When to Regenerate

**Regenerate** when:
- Tables have been added or removed from the model
- Relationship topology has changed (new relationships, direction changes, active/inactive changes)
- Measures have been rewritten and Claude needs to reference the updated DAX
- Calculated columns have been added or modified
- Calculation group items have changed

**Do NOT regenerate** for:
- Performance findings (→ `{model}-dax-performance.md`)
- Behavioral gotchas (→ `{model}-gotchas.md`)
- Validated DAX patterns (→ `pbi-dax-patterns.md`)
- Design decisions (→ `{model}-design-decisions.md`)
- Formatting, description, or display folder changes only (low value-to-effort ratio)

---

## Troubleshooting

### Script crashes with TypeError on join

**Symptom:** `TypeError: sequence item 0: expected str instance, list found`

**Cause:** A field (`expression` or `description`) in the .bim is stored as a JSON array of strings, and the script isn't normalizing it.

**Fix:** Add `isinstance(val, list)` check before the field is used in string operations. Fix goes into the project copy of the script, not as an in-session patch. See "JSON array expressions and descriptions" above.

### Script produces empty or missing sections

**Symptom:** The markdown file is generated but some tables or measures are missing.

**Cause:** Internal/system tables are filtered out by `is_internal_table()`. Check if the missing table has a name starting with `LocalDateTable_`, `DateTableTemplate_`, `RowNumber-`, or `$`.

### Output file is very large

**Expected.** A model with 600+ measures will produce a 10,000+ line markdown file. This is by design — full DAX expressions are needed for dependency tracing and filter analysis. RAG chunking handles the retrieval; the user never needs to read the full file.
