import json

import pytest

from pbi_capture import config as cfgmod
from pbi_capture.config import (BenchmarkConfig, CaptureConfig, ConfigError,
                                apply_env_overrides, load_config,
                                validate_benchmark, validate_capture)


def _write(tmp_path, doc):
    p = tmp_path / "c.json"
    p.write_text(json.dumps(doc), encoding="utf-8")
    return p


CAPTURE_DOC = {
    "workflow": "capture", "label": "baseline", "model_name": "Sales",
    "tests": [{"id": "t0001", "measure": "M One", "context": "grand_total"},
              {"id": "t0002", "measure": "M One", "context": "by_year"}],
    "group_by_columns": {"by_year": "'Date'[Year]"},
}


def test_load_capture(tmp_path):
    cfg = load_config(_write(tmp_path, CAPTURE_DOC))
    assert isinstance(cfg, CaptureConfig)
    assert cfg.label == "baseline"
    assert cfg.model_name == "Sales"
    assert cfg.tests[1].context == "by_year"
    assert cfg.output_dir == "output/regression"      # workflow default
    assert cfg.query_timeout_ms == 60000
    validate_capture(cfg)


def test_load_benchmark_defaults(tmp_path):
    cfg = load_config(_write(tmp_path, {"workflow": "benchmark", "label": "b",
                                        "measures": ["M"]}))
    assert isinstance(cfg, BenchmarkConfig)
    assert cfg.output_dir == "output/benchmark"
    validate_benchmark(cfg)


def test_bad_workflow(tmp_path):
    with pytest.raises(ConfigError):
        load_config(_write(tmp_path, {"workflow": "nope"}))


def test_env_overrides(tmp_path, monkeypatch):
    cfg = load_config(_write(tmp_path, CAPTURE_DOC))
    monkeypatch.setenv("SNAPSHOT_LABEL", "refactored")
    monkeypatch.setenv("MODEL_NAME", "Other")
    monkeypatch.setenv("DIAGNOSTIC_MODE", "true")
    monkeypatch.setenv("QUERY_TIMEOUT_MS", "5000")
    monkeypatch.setenv("MEMORY_THRESHOLD_PCT", "40")
    monkeypatch.setenv("SKIP_ON_SMOKE_FAILURE", "false")
    monkeypatch.setenv("OUTPUT_DIR", "elsewhere")
    monkeypatch.setenv("CONNECTION_STRING", "Data Source=localhost:1234;")
    apply_env_overrides(cfg, label_env="SNAPSHOT_LABEL")
    assert cfg.label == "refactored"
    assert cfg.model_name == "Other"
    assert cfg.diagnostic_mode is True
    assert cfg.query_timeout_ms == 5000
    assert cfg.memory_threshold_pct == 40.0
    assert cfg.skip_on_smoke_failure is False
    assert cfg.output_dir == "elsewhere"
    assert cfg.connection.connection_string == "Data Source=localhost:1234;"


def test_validate_rejects_duplicate_ids(tmp_path):
    doc = dict(CAPTURE_DOC, tests=[
        {"id": "t1", "measure": "M", "context": "grand_total"},
        {"id": "t1", "measure": "M", "context": "by_year"}])
    with pytest.raises(ConfigError, match="duplicate"):
        validate_capture(load_config(_write(tmp_path, doc)))


def test_validate_rejects_unknown_context(tmp_path):
    doc = dict(CAPTURE_DOC, tests=[{"id": "t1", "measure": "M", "context": "by_nope"}])
    with pytest.raises(ConfigError, match="by_nope"):
        validate_capture(load_config(_write(tmp_path, doc)))


def test_validate_rejects_bracketed_measure(tmp_path):
    doc = dict(CAPTURE_DOC, tests=[{"id": "t1", "measure": "[M]", "context": "grand_total"}])
    with pytest.raises(ConfigError, match="bare"):
        validate_capture(load_config(_write(tmp_path, doc)))


def test_validate_rejects_unsafe_test_id(tmp_path):
    doc = dict(CAPTURE_DOC, tests=[{"id": 't"1', "measure": "M", "context": "grand_total"}])
    with pytest.raises(ConfigError, match="id"):
        validate_capture(load_config(_write(tmp_path, doc)))


def test_validate_benchmark_filter_subset(tmp_path):
    doc = {"workflow": "benchmark", "label": "b", "measures": ["M"],
           "cross_product_columns": ["'A'[B]"],
           "cross_product_value_filters": {"'C'[D]": ["x"]}}
    with pytest.raises(ConfigError, match="cross_product_columns"):
        validate_benchmark(load_config(_write(tmp_path, doc)))


def test_validate_benchmark_requires_measures(tmp_path):
    with pytest.raises(ConfigError, match="measures"):
        validate_benchmark(load_config(_write(tmp_path, {"workflow": "benchmark", "label": "b"})))
