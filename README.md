# Power BI Assistant

[![tests](https://github.com/David-E-Kay/power-bi-assistant/actions/workflows/ci.yml/badge.svg)](https://github.com/David-E-Kay/power-bi-assistant/actions/workflows/ci.yml)

A [Claude Code](https://claude.com/claude-code) workspace that turns Claude into a focused assistant for Power BI semantic-model work — DAX optimization, data modeling, performance tuning, regression testing, and schema documentation.

The project ships:

- **Behavioral instructions** (`CLAUDE.md`) that teach Claude how to work in a Power BI context — consult a knowledge base first, prefer automation over manual clicks, follow a DAX review checklist, and run a session learning loop.
- **Project skills** (`.claude/skills/`) for `.bim` parsing, measure benchmarking, regression testing, model-refactor strategy, context-mode retrieval over large artifacts, and Confluence caching.
- **A curated knowledge base** (`knowledge/`) of validated DAX patterns and team modeling standards, extended per-model as you onboard models.
- **Python + C# automation** (`scripts/`) — including a **Tabular-Editor-free** path to export a live model's schema and run DAX against an open Power BI Desktop instance, using the Analysis Services client libraries provisioned straight from NuGet.

## Prerequisites

- **Claude Code** (CLI, desktop, or IDE extension) — open this folder as a project.
- **Python 3.9+** (developed and tested on 3.14).
- **Power BI Desktop** (Windows) — required only for the *live* features (schema export and DAX execution against an open model). The offline `.bim` parsing path needs neither Power BI nor an internet connection.
- *(Recommended)* the data-goblin **`power-bi-agentic-development`** Claude Code plugin, which supplies the DAX / TMDL / C#-scripting / BPA / Fabric domain skills that `CLAUDE.md` routes to. Install it from the Claude Code marketplace. Most of the project works without it (with reduced coverage), but the **`refactor-strategy`** skill *requires* it — its topology refactors delegate to `semantic-models:dax`, `tabular-editor:bpa-rules`, and `tabular-editor:c-sharp-scripting`.

## Setup

```bash
git clone https://github.com/David-E-Kay/power-bi-assistant.git
cd power-bi-assistant

# 1. Python dependencies
#    runtime only:    pip install -r requirements.txt       (pythonnet, openpyxl)
#    with the tests:  pip install -r requirements-dev.txt   (also installs pytest)
pip install -r requirements-dev.txt

# 2. One-time: provision the Analysis Services client DLLs into libs/
#    (downloaded from NuGet — no Tabular Editor or .NET SDK required)
python scripts/pbi_capture/provision_libs.py
```

`libs/` is git-ignored; each user provisions it locally. The step is idempotent — re-running it prints `Already provisioned` unless you pass `--force`.

## Usage

Open the folder in Claude Code and ask for Power BI help directly — the skills and `CLAUDE.md` instructions activate automatically. The scripts below can also be run on their own.

### Export a live model's schema (no Tabular Editor)

With a model **open in Power BI Desktop**:

```bash
python scripts/export_schema.py
```

This auto-discovers the local Analysis Services instance, serializes the model to `.bim` via TOM (written to `output/`), and renders a RAG-friendly markdown schema to `artifacts/model-schema/model-schema-<slug>.md`. Useful flags: `--port N` / `--connection-string S` to target a specific instance, `--name` to override the model name, `--md-out` / `--bim-out` for explicit paths.

### Parse a `.bim` file offline

```bash
python scripts/bim_to_kb_markdown.py path/to/model.bim --output artifacts/model-schema/model-schema-<slug>.md
```

Both paths produce the same markdown artifact, which Claude retrieves on demand via the `powerbi-context-mode` skill rather than reading wholesale.

### Regression-test a model change

Verify a DAX / relationship / structure change produced **no unintended value changes**. Ask Claude (it drives the [`regression-testing`](.claude/skills/regression-testing/SKILL.md) skill) or run it yourself. Claude authors one JSON config; the same file captures both sides, changing only the label:

```bash
# baseline — Power BI Desktop connected to the ORIGINAL model
python scripts/capture_snapshot.py --config output/sales.config.json --label baseline
# refactored — after applying the change
python scripts/capture_snapshot.py --config output/sales.config.json --label refactored
# value + timing diff → regression-report.xlsx
python scripts/compare-snapshots.py output/regression/baseline.json output/regression/refactored.json
```

The config is pure data — test cases plus a dimension map, no DAX strings:

```json
{
  "workflow": "capture",
  "model_name": "Sales",
  "tests": [
    { "id": "t0001", "measure": "Total Sales", "context": "grand_total" },
    { "id": "t0002", "measure": "Total Sales", "context": "by_year" }
  ],
  "group_by_columns": { "by_year": "'Date'[Year]" }
}
```

Measure names are **bare** (no brackets); the engine builds the `SUMMARIZECOLUMNS` queries, runs them under a safety stack (smoke test, wall-clock timeout, memory watchdog), and serializes the snapshot. `compare-snapshots.py` then produces a six-sheet `regression-report.xlsx` flagging every value delta and timing regression. Full key reference: [`docs/config-schema.md`](docs/config-schema.md).

### Benchmark measure performance

Find the slowest measures for optimization triage (timing only — no value capture), via the [`measure-benchmarking`](.claude/skills/measure-benchmarking/SKILL.md) skill:

```bash
python scripts/benchmark_measures.py --config output/sweep.config.json
```

```json
{
  "workflow": "benchmark",
  "measures": ["Total Sales", "Margin %"],
  "single_slice_dimensions": { "by_month": "'Date'[Month]" },
  "cross_product_columns": ["'Product'[Category]", "'Date'[Month]"]
}
```

Writes a per-test `{label}-timing.csv` and a Top-10-slowest summary to `output/benchmark/`.

### Ask for a Tabular Editor (`.csx`) script

The Python path above is the default and needs no Tabular Editor. If you'd rather run the capture or benchmark **inside Tabular Editor 3** — for example to step through it or tweak it interactively — ask Claude to emit `scripts/capture-snapshot.csx` or `scripts/benchmark-measures.csx`, either raw or pre-populated with your measures and fields. The `.csx` writes the same output schema, so the comparison and analysis steps are identical. This is an opt-in legacy path; the only real difference is the execution host (Tabular Editor vs. the TE-free Python engine).

### Run the tests

```bash
# Fast suite (no Power BI required)
python -m pytest -m "not live"

# Live suite (requires a model open in Power BI Desktop + provisioned libs/)
$env:PYTHONPATH="scripts"; $env:PBI_LIVE="1"; python -m pytest tests/live -v   # PowerShell
PYTHONPATH=scripts PBI_LIVE=1 python -m pytest tests/live -v                    # bash
```

## How the assistant learns your models

Beyond running scripts, the workspace accumulates **behavioral knowledge** so advice gets more model-specific the more you use it:

- **Curated knowledge base (`knowledge/`)** — transferable [DAX patterns](knowledge/pbi-dax-patterns.md) and [team modeling standards](knowledge/pbi-modeling-standards.md) ship by default. As you onboard a model, Claude adds per-model files — `{model}-gotchas.md`, `{model}-dax-performance.md`, `{model}-design-decisions.md` — recording validated findings (e.g. "this key column is TEXT — cast before arithmetic", or "a prior refactor removed the bridge bidir scan, so don't re-suggest the old CROSSFILTER workaround").
- **Session Learning Loop** — at natural breakpoints Claude surfaces what it learned (performance discoveries, gotchas, design decisions) and, on your confirmation, writes it back to `knowledge/`. See `CLAUDE.md`.
- **Cross-session memory** — durable, frequently-referenced facts persist across all sessions, so the next conversation starts already knowing them.
- **Schema snapshots** — `export_schema.py` / `bim_to_kb_markdown.py` cache a model's structure under `artifacts/model-schema/`; Claude retrieves from it on demand via the `powerbi-context-mode` skill instead of re-reading the whole model.
- **Optional Confluence cache** — point the `confluence-cache` skill at your team's published standards to fold them into the same retrieval (off by default).

The result: instead of generic DAX advice, you get guidance grounded in *your* model's quirks, its history, and your team's conventions.

## Project layout

```
CLAUDE.md                  Project instructions Claude follows in this workspace
.claude/skills/            Project-level skills (parsing, benchmarking, regression, etc.)
knowledge/                 Curated KB: DAX patterns, modeling standards, per-model findings
scripts/                   Python + C# automation
  pbi_capture/             TE-free TOM/ADOMD engine: provisioning, discovery, schema export, query execution
  export_schema.py         CLI: live model -> schema markdown
  bim_to_kb_markdown.py    CLI: .bim file -> schema markdown
  capture_snapshot.py      CLI: regression snapshot capture
  benchmark_measures.py    CLI: measure timing sweep
  compare-snapshots.py     CLI: diff two snapshots -> regression-report.xlsx
artifacts/model-schema/    Generated schema snapshots (git-ignored; README explains the dir)
output/                    Session deliverables (git-ignored)
libs/                      NuGet-provisioned AS client DLLs (git-ignored; created by provision_libs.py)
```

## Notes

- **Machine-local config is not committed.** `.claude/settings.local.json` and `.claude/.mcp.json` are git-ignored — configure your own MCP servers and local settings.
- **Generated artifacts stay local.** `libs/`, `output/`, and generated `artifacts/model-schema/model-schema-*.md` are git-ignored so the repo stays clean and portable.
- **Roadmap.** A desktop-notification option for long-running capture / benchmark runs is planned.

## License

Released under the [MIT License](LICENSE) — free to use, modify, and redistribute (including commercially), provided the copyright and license notice are retained.
