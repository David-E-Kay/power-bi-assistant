"""Config loading/validation for capture & benchmark runs. Pure — no CLR.

Precedence: CLI flags (entry scripts) > env vars > config file > defaults.
Env var names preserved from the .csx templates (spec contract #4).
USE_DIRECT_ADOMD is intentionally gone — direct ADOMD is the only mode now.
"""
import json
import os
import re
from dataclasses import dataclass, field


class ConfigError(ValueError):
    pass


_ID_RE = re.compile(r"^[A-Za-z0-9_-]+$")


@dataclass
class ConnectionConfig:
    connection_string: str | None = None
    port: int | None = None


@dataclass
class CommonConfig:
    label: str = "run"
    output_dir: str = ""
    connection: ConnectionConfig = field(default_factory=ConnectionConfig)
    max_rows_per_context: int = 0
    query_timeout_ms: int = 60000
    smoke_test_timeout_ms: int = 10000
    memory_threshold_pct: float = 80.0
    skip_on_smoke_failure: bool = True
    diagnostic_mode: bool = False


@dataclass
class TestCase:
    __test__ = False  # ponytail: prevents pytest from collecting this as a test class
    id: str
    measure: str
    context: str


@dataclass
class CaptureConfig(CommonConfig):
    model_name: str = ""
    global_filters: list[str] = field(default_factory=list)
    tests: list[TestCase] = field(default_factory=list)
    group_by_columns: dict[str, str] = field(default_factory=dict)


@dataclass
class BenchmarkConfig(CommonConfig):
    measures: list[str] = field(default_factory=list)
    single_slice_dimensions: dict[str, str] = field(default_factory=dict)
    cross_product_columns: list[str] = field(default_factory=list)
    cross_product_value_filters: dict[str, list[str]] = field(default_factory=dict)
    global_filters: dict[str, list[str]] = field(default_factory=dict)


_COMMON_KEYS = ("label", "output_dir", "max_rows_per_context", "query_timeout_ms",
                "smoke_test_timeout_ms", "memory_threshold_pct",
                "skip_on_smoke_failure", "diagnostic_mode")


def _fill_common(cfg, raw, default_output_dir):
    for key in _COMMON_KEYS:
        if key in raw:
            setattr(cfg, key, raw[key])
    cfg.connection = ConnectionConfig(**(raw.get("connection") or {}))
    if not cfg.output_dir:
        cfg.output_dir = default_output_dir
    return cfg


def load_config(path):
    with open(path, encoding="utf-8") as f:
        raw = json.load(f)
    workflow = raw.get("workflow")
    if workflow == "capture":
        cfg = CaptureConfig(
            model_name=raw.get("model_name", ""),
            global_filters=list(raw.get("global_filters", [])),
            tests=[TestCase(**t) for t in raw.get("tests", [])],
            group_by_columns=dict(raw.get("group_by_columns", {})),
        )
        return _fill_common(cfg, raw, "output/regression")
    if workflow == "benchmark":
        cfg = BenchmarkConfig(
            measures=list(raw.get("measures", [])),
            single_slice_dimensions=dict(raw.get("single_slice_dimensions", {})),
            cross_product_columns=list(raw.get("cross_product_columns", [])),
            cross_product_value_filters={k: list(v) for k, v in
                                         raw.get("cross_product_value_filters", {}).items()},
            global_filters={k: list(v) for k, v in raw.get("global_filters", {}).items()},
        )
        return _fill_common(cfg, raw, "output/benchmark")
    raise ConfigError('config "workflow" must be "capture" or "benchmark"')


def _as_bool(s: str) -> bool:
    return s.strip().lower() == "true"


def apply_env_overrides(cfg, *, label_env: str) -> None:
    env = os.environ
    if env.get(label_env):
        cfg.label = env[label_env]
    if isinstance(cfg, CaptureConfig) and env.get("MODEL_NAME"):
        cfg.model_name = env["MODEL_NAME"]
    if "DIAGNOSTIC_MODE" in env:
        cfg.diagnostic_mode = _as_bool(env["DIAGNOSTIC_MODE"])
    if "OUTPUT_DIR" in env:
        cfg.output_dir = env["OUTPUT_DIR"]
    if "QUERY_TIMEOUT_MS" in env:
        cfg.query_timeout_ms = int(env["QUERY_TIMEOUT_MS"])
    if "SMOKE_TEST_TIMEOUT_MS" in env:
        cfg.smoke_test_timeout_ms = int(env["SMOKE_TEST_TIMEOUT_MS"])
    if "MEMORY_THRESHOLD_PCT" in env:
        cfg.memory_threshold_pct = float(env["MEMORY_THRESHOLD_PCT"])
    if "SKIP_ON_SMOKE_FAILURE" in env:
        cfg.skip_on_smoke_failure = _as_bool(env["SKIP_ON_SMOKE_FAILURE"])
    if "CONNECTION_STRING" in env:
        cfg.connection.connection_string = env["CONNECTION_STRING"]


def _validate_measure_name(name: str) -> None:
    if "[" in name or "]" in name:
        raise ConfigError(
            f"Measure name {name!r} must be bare — no brackets. The engine adds "
            "[..] itself (the old .csx comment examples were misleading).")


def validate_capture(cfg: CaptureConfig) -> None:
    if not cfg.label:
        raise ConfigError("label must be non-empty")
    if cfg.max_rows_per_context != 0:
        raise ConfigError(
            "max_rows_per_context must be 0 for regression capture (got "
            f"{cfg.max_rows_per_context}). A TOPN row cap truncates "
            "dimension-combination values — a delta in a dropped row becomes a "
            "silent false-pass — and TOPN without ORDER BY returns an unstable "
            "row set across runs. For a fast smoke run use diagnostic_mode "
            "(caps test count, not rows). Row caps are a benchmark-only knob.")
    if not cfg.tests:
        raise ConfigError("capture config has no tests")
    seen = set()
    for t in cfg.tests:
        if not _ID_RE.match(t.id or ""):
            raise ConfigError(f"test id {t.id!r} must match [A-Za-z0-9_-]+")
        if t.id in seen:
            raise ConfigError(f"duplicate test id {t.id!r}")
        seen.add(t.id)
        _validate_measure_name(t.measure)
        if t.context != "grand_total" and t.context not in cfg.group_by_columns:
            raise ConfigError(
                f"test {t.id}: context {t.context!r} not found in group_by_columns")


def validate_benchmark(cfg: BenchmarkConfig) -> None:
    if not cfg.label:
        raise ConfigError("label must be non-empty")
    if not cfg.measures:
        raise ConfigError("benchmark config has no measures")
    for m in cfg.measures:
        _validate_measure_name(m)
    for col in cfg.cross_product_value_filters:
        if col not in cfg.cross_product_columns:
            raise ConfigError(
                f"cross_product_value_filters references {col!r} which is not in "
                "cross_product_columns")
