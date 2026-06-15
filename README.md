# Power BI Assistant

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
- *(Recommended)* the data-goblin **`power-bi-agentic-development`** Claude Code plugin, which supplies the DAX / TMDL / C#-scripting / Fabric domain skills that `CLAUDE.md` routes to. Install it separately in Claude Code; the project works without it but with reduced coverage.

## Setup

```bash
git clone https://github.com/David-E-Kay/power-bi-assistant.git
cd power-bi-assistant

# 1. Python dependencies (pythonnet, pytest, openpyxl)
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

### Run the tests

```bash
# Fast suite (no Power BI required)
python -m pytest -m "not live"

# Live suite (requires a model open in Power BI Desktop + provisioned libs/)
$env:PYTHONPATH="scripts"; $env:PBI_LIVE="1"; python -m pytest tests/live -v   # PowerShell
PYTHONPATH=scripts PBI_LIVE=1 python -m pytest tests/live -v                    # bash
```

## Project layout

```
CLAUDE.md                  Project instructions Claude follows in this workspace
.claude/skills/            Project-level skills (parsing, benchmarking, regression, etc.)
knowledge/                 Curated KB: DAX patterns, modeling standards, per-model findings
scripts/                   Python + C# automation
  pbi_capture/             TE-free TOM/ADOMD package: provisioning, discovery, schema export, query execution
  export_schema.py         CLI: live model -> schema markdown
  bim_to_kb_markdown.py    CLI: .bim file -> schema markdown
artifacts/model-schema/    Generated schema snapshots (git-ignored; README explains the dir)
output/                    Session deliverables (git-ignored)
libs/                      NuGet-provisioned AS client DLLs (git-ignored; created by provision_libs.py)
```

## Notes

- **Machine-local config is not committed.** `.claude/settings.local.json` and `.claude/.mcp.json` are git-ignored — configure your own MCP servers and local settings.
- **Generated artifacts stay local.** `libs/`, `output/`, generated `artifacts/model-schema/model-schema-*.md`, and `graphify-out/` are git-ignored so the repo stays clean and portable.
