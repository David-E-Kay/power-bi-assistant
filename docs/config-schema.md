# Config Schema — capture & benchmark runners

The TE-free runners are driven by a JSON config file (pure data — no hand-written DAX
*queries*; you supply measure names and column references, and the engine builds the
`SUMMARIZECOLUMNS` query at runtime). One schema covers both workflows, selected by the
`workflow` key:

- `"workflow": "capture"` → `python scripts/capture_snapshot.py --config <file>` (regression snapshots)
- `"workflow": "benchmark"` → `python scripts/benchmark_measures.py --config <file>` (timing sweeps)

The authoritative implementation is [`scripts/pbi_capture/config.py`](../scripts/pbi_capture/config.py)
(loading + validation). This document mirrors it.

---

## Common keys (both workflows)

| Key | Type | Default | Notes |
|---|---|---|---|
| `workflow` | string | — | **Required.** `"capture"` or `"benchmark"`. |
| `label` | string | `"run"` | Output filename prefix. Overridden by `--label` / `SNAPSHOT_LABEL` / `BENCHMARK_LABEL`. |
| `output_dir` | string | `output/regression` (capture) / `output/benchmark` (benchmark) | Where all outputs are written. |
| `connection` | object | `{}` | `{ "connection_string": <str|null>, "port": <int|null> }`. Leave empty to auto-discover the local `msmdsrv` instance. |
| `query_timeout_ms` | int | `60000` | Per-query wall-clock cap. The watchdog cancels the query on expiry. |
| `smoke_test_timeout_ms` | int | `10000` | Per-measure pre-flight smoke-test cap (`EVALUATE ROW("r", [M])`). |
| `memory_threshold_pct` | number | `80.0` | Memory watchdog trip point, as a % of **actual** total RAM. Set `0` to disable. |
| `skip_on_smoke_failure` | bool | `true` | Skip measures that fail the smoke test (vs. attempt them anyway). |
| `diagnostic_mode` | bool | `false` | Run only the first 8 tests. Also set via `--diagnostic`. |
| `max_rows_per_context` | int | `0` | TOPN row cap per context. **Capture requires `0`** (see below); benchmark may use a positive cap. |

---

## Capture config (`"workflow": "capture"`)

Captures a value+timing snapshot for regression comparison. Adds:

| Key | Type | Default | Notes |
|---|---|---|---|
| `model_name` | string | `""` | Written into the snapshot header; used as the report title by `compare-snapshots.py`. |
| `tests` | array | `[]` | **Required, non-empty.** Each item: `{ "id": <str>, "measure": <str>, "context": <str> }`. |
| `group_by_columns` | object | `{}` | Context name → DAX column(s). A single `'Table'[Column]`, or `|`-separated columns for a cross-product. |
| `global_filters` | array of strings | `[]` | DAX boolean expressions, each applied as `KEEPFILTERS` inside a `CALCULATE` around the measure. |

### Validation rules (`validate_capture`)

- `label` must be non-empty.
- **`max_rows_per_context` must be `0`.** A row cap silently truncates dimension combinations (a delta in a dropped row becomes a false pass) and the un-ordered `TOPN` returns an unstable row set across runs. For a fast smoke run use `diagnostic_mode`. The cap is a benchmark-only knob.
- `tests` must be non-empty.
- Each `test.id` must match `[A-Za-z0-9_-]+` and be unique.
- Each `test.measure` must be **bare** — no `[` or `]`. The engine adds the brackets.
- Each `test.context` must be `"grand_total"` or a key present in `group_by_columns`.

### Example

```json
{
  "workflow": "capture",
  "label": "baseline",
  "model_name": "Sales",
  "output_dir": "output/regression",
  "connection": { "connection_string": null, "port": null },
  "global_filters": [],
  "max_rows_per_context": 0,
  "query_timeout_ms": 60000,
  "smoke_test_timeout_ms": 10000,
  "memory_threshold_pct": 80,
  "skip_on_smoke_failure": true,
  "diagnostic_mode": false,
  "tests": [
    { "id": "t0001", "measure": "Total Sales", "context": "grand_total" },
    { "id": "t0002", "measure": "Total Sales", "context": "by_year" },
    { "id": "t0010", "measure": "Total Sales", "context": "by_dim_x_year" }
  ],
  "group_by_columns": {
    "by_year": "'Date'[Year]",
    "by_dim_x_year": "'Product'[Category]|'Date'[Year]"
  }
}
```

---

## Benchmark config (`"workflow": "benchmark"`)

Captures timing only (no values) for optimization triage. Adds:

| Key | Type | Default | Notes |
|---|---|---|---|
| `measures` | array of strings | `[]` | **Required, non-empty.** Bare measure names (no brackets). |
| `single_slice_dimensions` | object | `{}` | Label → DAX column. One query per dimension per measure. |
| `cross_product_columns` | array of strings | `[]` | DAX columns combined into one `SUMMARIZECOLUMNS` (matrix-visual shape). |
| `cross_product_value_filters` | object | `{}` | Column → list of values, applied as `TREATAS` within the cross-product. |
| `global_filters` | object | `{}` | Column → list of values, applied as `TREATAS` to **every** query. |

> **`global_filters` shape differs by workflow.** Capture: a *list of DAX boolean
> expressions* (KEEPFILTERS). Benchmark: an *object of column → values* (TREATAS).

### Validation rules (`validate_benchmark`)

- `label` must be non-empty.
- `measures` must be non-empty; each must be **bare** (no brackets).
- Every key in `cross_product_value_filters` must also appear in `cross_product_columns`.

### Example

```json
{
  "workflow": "benchmark",
  "label": "benchmark",
  "output_dir": "output/benchmark",
  "connection": { "connection_string": null, "port": null },
  "measures": ["Total Sales", "Margin %"],
  "single_slice_dimensions": { "by_month": "'Date'[Month]" },
  "cross_product_columns": ["'Product'[Category]", "'Date'[Month]"],
  "cross_product_value_filters": { "'Product'[Category]": ["Bikes"] },
  "global_filters": { "'Date'[Year]": ["2025"] },
  "max_rows_per_context": 0,
  "query_timeout_ms": 60000,
  "smoke_test_timeout_ms": 10000,
  "memory_threshold_pct": 80,
  "skip_on_smoke_failure": true,
  "diagnostic_mode": false
}
```

---

## Overrides & precedence

Effective precedence is **CLI flag > environment variable > config file > default**.

| Setting | CLI flag | Env var |
|---|---|---|
| Label | `--label` | `SNAPSHOT_LABEL` (capture) / `BENCHMARK_LABEL` (benchmark) |
| Model name (capture) | — | `MODEL_NAME` |
| Diagnostic mode | `--diagnostic` | `DIAGNOSTIC_MODE` |
| Output dir | — | `OUTPUT_DIR` |
| msmdsrv port | `--port` | — |
| Connection string | `--connection-string` | `CONNECTION_STRING` |
| Query timeout (ms) | — | `QUERY_TIMEOUT_MS` |
| Smoke timeout (ms) | — | `SMOKE_TEST_TIMEOUT_MS` |
| Memory threshold (%) | — | `MEMORY_THRESHOLD_PCT` |
| Skip on smoke fail | — | `SKIP_ON_SMOKE_FAILURE` |

## Exit codes

- `0` — run completed. Per-test errors and timeouts are recorded as data, not process failures.
- `2` — fatal: invalid config, no/ambiguous `msmdsrv` instance, or a CLR/DLL load failure.

(`compare-snapshots.py` has its own codes: `0` = all value tests pass; `1` = value failures or a new timeout vs. baseline.)

## Output files

Written to `output_dir` (default `output/regression` or `output/benchmark`):

- `{label}.json` — capture snapshot (capture only).
- `{label}-timing.csv` — per-test timing.
- `{label}-config.csv` — filter-context audit (benchmark only).
- `{label}-testplan.json` — planned test order (survives a force-kill).
- `{label}-summary.txt` — run summary (also printed to stdout).
- `{label}-errors.log` / `{label}-timeouts.log` — only when those events occur.
