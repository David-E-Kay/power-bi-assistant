# Power BI Assistant

[![tests](https://github.com/David-E-Kay/power-bi-assistant/actions/workflows/ci.yml/badge.svg)](https://github.com/David-E-Kay/power-bi-assistant/actions/workflows/ci.yml)

A [Claude Code](https://claude.com/claude-code) workspace that turns Claude into a focused assistant for Power BI semantic-model work — DAX optimization, data modeling, performance tuning, regression testing, model refactoring, and schema documentation.

The project ships:

- **Behavioral instructions** (`CLAUDE.md`) that teach Claude how to work in a Power BI context — consult a knowledge base first, prefer automation over manual clicks, follow a DAX review checklist, and run a session learning loop.
- **Project skills** (`.claude/skills/`) for `.bim` parsing, measure benchmarking, regression testing, model-refactor strategy, and Confluence caching.
- **A curated knowledge base** (`knowledge/`) of validated DAX patterns and team modeling standards, extended per-model as you onboard models.
- **Python + C# automation** (`scripts/`) — including a **Tabular-Editor-free** path to export a live model's schema and run DAX against an open Power BI Desktop instance, using the Analysis Services client libraries provisioned straight from NuGet.

## Contents

- **[An end-to-end model workflow](#an-end-to-end-model-workflow)** — how the skills chain into one loop (author → benchmark → regression-test → refactor → re-validate).
- **[Prerequisites](#prerequisites)** — what you need installed (Claude Code, Python, Power BI Desktop, the data-goblin plugin).
- **[Setup](#setup)** — clone, install dependencies, provision the Analysis Services DLLs.
- **[Usage](#usage)** — drive it by chat; the CLIs for schema export, regression testing, and benchmarking.
- **[How the assistant learns your models](#how-the-assistant-learns-your-models)** — the per-model knowledge base, session learning loop, and cached schema snapshots.
- **[Why it works this way](#why-it-works-this-way)** — the design rationale: parsed schema vs. live reads, **Tabular Editor scripts over MCP**, and the **memory watchdog**.
- **[Tuning the defaults](#tuning-the-defaults)** — editable run knobs (the 80% watchdog threshold, timeouts) and the two ways to change them.
- **[Project layout](#project-layout)** — what lives where in the repo.
- **[Notes](#notes)** — gitignore boundaries and machine-local config.
- **[License](#license)** — MIT.

## An end-to-end model workflow

The skills are built to **chain into one loop**, not just stand alone:

> author / optimize DAX → **benchmark** → **regression-test** → **refactor topology** → re-validate

- **Authoring & optimization** — the [data-goblin](https://github.com/data-goblin/power-bi-agentic-development) `semantic-models:dax` skill plus the DAX review checklist in `CLAUDE.md`.
- **Benchmarking** — [`measure-benchmarking`](.claude/skills/measure-benchmarking/SKILL.md) profiles a measure set to find the slowest queries.
- **Regression testing** — [`regression-testing`](.claude/skills/regression-testing/SKILL.md) proves a change didn't alter results.
- **Refactoring** — [`refactor-strategy`](.claude/skills/refactor-strategy/SKILL.md) is the **orchestrator**: it diagnoses a topology bottleneck and **delegates to the data-goblin skills** to assess and execute the fix — `semantic-models:dax` for the trace / SE–FE decision, `tabular-editor:bpa-rules` to flag bidirectional / inactive / unused relationships, and `tabular-editor:c-sharp-scripting` for the TOM relationship and DAX edits — then hands back to `regression-testing` to verify parity.

In short: the data-goblin **[`power-bi-agentic-development`](https://github.com/data-goblin/power-bi-agentic-development)** plugin supplies the *targeted, single-domain* skills (DAX, TMDL, BPA, C# scripting, Fabric); this project adds the *cross-skill workflows* (benchmarking, regression testing, refactor orchestration) and the model-learning knowledge base on top.

**Deep-dive guides** — the two most involved workflows have full developer walkthroughs:
[Regression-testing developer guide](docs/guides/regression-testing-developer-guide.md) · [Measure-benchmarking developer guide](docs/guides/measure-benchmarking-developer-guide.md) · config-key reference in [docs/config-schema.md](docs/config-schema.md).

## Prerequisites

- **Claude Code** (CLI, desktop, or IDE extension) — open this folder as a project.
- **Python 3.10+** (developed and tested on 3.14).
- **Power BI Desktop** (Windows) — required only for the *live* features (schema export and DAX execution against an open model). The offline `.bim` parsing path needs neither Power BI nor an internet connection.
- *(Recommended)* the data-goblin **[`power-bi-agentic-development`](https://github.com/data-goblin/power-bi-agentic-development)** Claude Code plugin, which supplies the DAX / TMDL / C#-scripting / BPA / Fabric domain skills that `CLAUDE.md` routes to. This is an optional Claude Code marketplace plugin you install yourself — **not** a repo or pip dependency, so a "clone and install everything" pass will **not** pull it in. Install it from the marketplace only if you want the extra DAX/TMDL/BPA/C# coverage. Most of the project works without it (with reduced coverage), but the **`refactor-strategy`** skill *requires* it — its topology refactors delegate to `semantic-models:dax`, `tabular-editor:bpa-rules`, and `tabular-editor:c-sharp-scripting`.

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

**The intended way to drive this project is conversation, not hand-editing.** Open the folder in Claude Code and describe what you want — *"export the schema of the model I have open"*, *"regression-test this measure change"*, *"benchmark these five measures by month and product"*. Claude activates the right skill, **authors and edits the JSON config and fills in the command blocks for you**, then runs them. The configs and measure lists shown below are illustrations of what Claude generates — you don't write or edit them by hand.

You *can* run every script yourself (they're plain CLIs, documented below) and nothing stops you, but you never *have* to: the edits to the code blocks are LLM-driven.

### Export a live model's schema (no Tabular Editor)

Ask Claude to export the schema of the model you have open, or run it yourself. With a model **open in Power BI Desktop**:

```bash
python scripts/export_schema.py
```

This auto-discovers the local Analysis Services instance, serializes the model to `.bim` via TOM (written to `output/`), and renders a RAG-friendly markdown schema to `artifacts/model-schema/model-schema-<slug>.md`. Useful flags: `--port N` / `--connection-string S` to target a specific instance, `--name` to override the model name, `--md-out` / `--bim-out` for explicit paths.

### Parse a `.bim` file offline

```bash
python scripts/bim_to_kb_markdown.py path/to/model.bim --output artifacts/model-schema/model-schema-<slug>.md
```

Both paths produce the same markdown artifact, which Claude retrieves on demand with `Grep` + a targeted `Read` rather than reading it wholesale.

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

Measure names are **bare** (no brackets); the engine builds the `SUMMARIZECOLUMNS` queries, runs them under a safety stack (smoke test, wall-clock timeout, memory watchdog), and serializes the snapshot. `compare-snapshots.py` then produces a six-sheet `regression-report.xlsx` flagging every value delta and timing regression. Full key reference: [`docs/config-schema.md`](docs/config-schema.md); end-to-end walkthrough: [regression-testing developer guide](docs/guides/regression-testing-developer-guide.md).

### Benchmark measure performance

Find the slowest measures for optimization triage (timing only — no value capture), via the [`measure-benchmarking`](.claude/skills/measure-benchmarking/SKILL.md) skill ([developer guide](docs/guides/measure-benchmarking-developer-guide.md)):

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

The human-facing deliverable is a styled Excel report — **`{label}-report.xlsx`** in `output/benchmark/`, built to match the regression report — with sheets **Summary · All Tests · By Measure · Slowest · False-Fast Warnings**. That `.xlsx` is the one to open and review. The run also drops the supporting raw files alongside it — `{label}-timing.csv` (per-test rows), `{label}-config.csv` (an echo of the dimension map), and `{label}-summary.txt` (the console recap, including the top-5 slowest) — but you don't need to read those. As with capture, the timing harness runs under the same safety stack (smoke test, wall-clock timeout, memory watchdog).

### Ask for a Tabular Editor (`.csx`) script

The Python path above is the default and needs no Tabular Editor. If you'd rather run the capture or benchmark **inside Tabular Editor 3** — for example to step through it or tweak it interactively — ask Claude to emit `scripts/legacy-tabular-editor/capture-snapshot.csx` or `scripts/legacy-tabular-editor/benchmark-measures.csx`, either raw or pre-populated with your measures and fields. The `.csx` writes the same output schema, so the comparison and analysis steps are identical. This is an opt-in legacy path; the only real difference is the execution host (Tabular Editor vs. the TE-free Python engine).

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

- **Curated knowledge base (`knowledge/`)** — a small, curated **seed** of transferable [DAX patterns](knowledge/pbi-dax-patterns.md) and [team modeling standards](knowledge/pbi-modeling-standards.md) ships by default (intentionally lean — only model-agnostic, fully-validated patterns). As you onboard a model, Claude adds per-model files — `{model}-gotchas.md`, `{model}-dax-performance.md`, `{model}-design-decisions.md` — recording validated findings (e.g. "this key column is TEXT — cast before arithmetic", or "a prior refactor removed the bridge bidir scan, so don't re-suggest the old CROSSFILTER workaround").
- **Session Learning Loop** — at natural breakpoints Claude surfaces what it learned (performance discoveries, gotchas, design decisions) and, on your confirmation, writes it back to `knowledge/`. See `CLAUDE.md`.
- **Cross-session memory** *(depends on your Claude Code setup)* — if your environment provides persistent memory (user-level instructions or a memory tool), the most critical findings can carry across sessions. The committed `knowledge/` files above are the portable, repo-scoped layer that travels with the project regardless.
- **Schema snapshots** — `export_schema.py` / `bim_to_kb_markdown.py` cache a model's structure under `artifacts/model-schema/`; Claude retrieves from it on demand with targeted `Grep` + `Read` instead of re-reading the whole model.
- **Optional Confluence cache** — point the `confluence-cache` skill at your team's published standards to fold them into the same retrieval (off by default).

The result: instead of generic DAX advice, you get guidance grounded in *your* model's quirks, its history, and your team's conventions.

## Why it works this way

A few choices are deliberate. Each one trades raw LLM autonomy for **lower cost, determinism, and safety** — the properties that matter when you're touching a production semantic model.

### Parsed schema snapshots over live model reads

Claude works from a compact **markdown snapshot** of the model (`export_schema.py` / `bim_to_kb_markdown.py`) and retrieves from it on demand, rather than streaming the raw model into context. The reason is token economics. A raw `.bim` for even a *medium* model is on the order of **~26k lines of JSON ≈ ~210k tokens** — that exceeds a standard context window before any actual work begins. Large models blow well past it; and even when a model technically fits, the sheer volume invites **context rot**, where the LLM loses track of schema details it "read" thousands of tokens earlier and starts giving advice grounded in the wrong table. The parsed markdown is small, structured for retrieval, and cached — so lookups are cheap, repeatable, and don't crowd out the task itself. (See [How the assistant learns your models](#how-the-assistant-learns-your-models).)

### Tabular Editor scripts over MCP for model changes

When a change *mutates* the model — DAX edits, relationships, calc groups, structural refactors — the default is a **Tabular Editor C# script**, not a live MCP write. Code beats tokens here on every axis:

- **Cheaper** — the change runs as deterministic code the engine executes, not as LLM tokens spent driving the model object-by-object.
- **Deterministic & auditable** — the script *is* the change; you can read exactly what it will do before anything happens.
- **Reviewable & reversible** — open it in Tabular Editor, eyeball the diff, and **roll it back** if it's wrong, before you commit.
- **Reusable** — re-run it across environments, or replay it after a refresh.

A live **Power BI MCP** is still wired up and used — for *inspection*, metadata discovery, validation, and the occasional explicitly-approved low-risk single-object edit. It remains the **fallback**, not the default path for mutations.

### Memory-guarded local execution

Capture and benchmark runs execute DAX against the model open on **your machine**, so the engine protects local hardware. A **memory watchdog** reads *real* physical RAM (via the Windows `GlobalMemoryStatusEx` call) and aborts the run when this process plus all `msmdsrv` (Analysis Services) working sets cross a threshold — **80% by default** — so a runaway cross-product sweep can't quietly page your machine into the ground. It rides alongside the rest of the safety stack: a **smoke test** that skips measures which error on a trivial query, and a **wall-clock timeout** (60s default) that cancels the command and disposes the connection as a backstop. Long runs also fire a **desktop toast on completion**, so you don't have to babysit them. All three are tunable — see below.

## Tuning the defaults

Those safety/behavior defaults are **shared by both capture and benchmark** and every one is editable. The most common to change is the watchdog's **80%** memory threshold:

| Knob | Default | Config key / env var | What it does |
|---|---|---|---|
| Memory abort threshold | `80` (% of RAM) | `memory_threshold_pct` / `MEMORY_THRESHOLD_PCT` | Aborts when (this process + all `msmdsrv`) working sets cross it. **Set `0` to disable the watchdog.** |
| Query timeout | `60000` ms | `query_timeout_ms` / `QUERY_TIMEOUT_MS` | Per-query wall-clock cap before cancel + connection dispose. |
| Smoke-test timeout | `10000` ms | `smoke_test_timeout_ms` / `SMOKE_TEST_TIMEOUT_MS` | Time budget for the trivial smoke query run against each measure. |
| Skip on smoke failure | `true` | `skip_on_smoke_failure` / `SKIP_ON_SMOKE_FAILURE` | Drop measures that error on the smoke query instead of running them. |
| Diagnostic dry run | `false` | `diagnostic_mode` / `DIAGNOSTIC_MODE` (or `--diagnostic`) | Cap the run to the first 8 tests for a quick sanity check. |

Two ways to set them:

**1. In the config JSON** (persistent, per-config) — add the key alongside the rest. Just tell Claude *"set the memory threshold to 70 and the timeout to 2 minutes"* and it edits the file; or do it by hand:

```json
{
  "workflow": "benchmark",
  "memory_threshold_pct": 70,
  "query_timeout_ms": 120000,
  "measures": ["Total Sales", "Margin %"]
}
```

**2. As an environment variable** (a one-off override for a single run, no file edit):

```bash
MEMORY_THRESHOLD_PCT=70 QUERY_TIMEOUT_MS=120000 python scripts/benchmark_measures.py --config output/sweep.config.json   # bash
$env:MEMORY_THRESHOLD_PCT=70; python scripts/benchmark_measures.py --config output/sweep.config.json                      # PowerShell
```

Precedence, highest wins: **CLI flag → environment variable → config file → built-in default.** The complete key reference — including the `connection` / `--port`, `--label`, and `output_dir` overrides — is in [`docs/config-schema.md`](docs/config-schema.md).

## Project layout

```
CLAUDE.md                  Project instructions Claude follows in this workspace
.claude/skills/            Project-level skills (parsing, benchmarking, regression, etc.)
knowledge/                 Curated KB: DAX patterns, modeling standards, per-model findings
docs/                      Developer guides (regression, benchmarking) + config-schema reference
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

## License

Released under the [MIT License](LICENSE) — free to use, modify, and redistribute (including commercially), provided the copyright and license notice are retained.
