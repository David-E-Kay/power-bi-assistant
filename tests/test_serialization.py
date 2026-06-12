import json
import os
import subprocess
import sys
from pathlib import Path

from pbi_capture import serialization as ser


def test_sentinels_and_rounding():
    assert ser.to_jsonable(None) == "__BLANK__"
    assert ser.to_jsonable(float("nan")) == "__NaN__"
    assert ser.to_jsonable(float("inf")) == "__INF__"
    assert ser.to_jsonable(float("-inf")) == "__INF__"
    assert ser.to_jsonable(True) == "True"      # bool before int: C# ToString parity
    assert ser.to_jsonable(7) == 7
    assert ser.to_jsonable(1.23456) == 1.2346
    assert ser.to_jsonable("text") == "text"


def _make_snapshot(tmp_path, name, value):
    w = ser.SnapshotWriter(
        tmp_path / name, model_name="TestModel", label=name.replace(".json", ""),
        global_filters=[], max_rows_per_context=0, query_timeout_ms=60000)
    w.write_result("t0001", ser.ok_record(
        "M1", "grand_total", ["[Result]"], [{"[Result]": value}], 12))
    w.write_result("t0002", ser.skipped_record("M2", "by_year", "timeout: smoke"))
    w.finish({"total": 2, "ok": 1, "error": 0, "timeout": 0, "skipped": 1,
              "aborted_memory": 0,
              "smoke_test": {"measures_tested": 2, "measures_skipped": 1},
              "total_duration_ms": 12})
    return tmp_path / name


def test_snapshot_is_valid_json_with_contract_keys(tmp_path):
    p = _make_snapshot(tmp_path, "a.json", 1.23456)
    data = json.loads(p.read_text(encoding="utf-8"))
    for key in ("snapshot_version", "model_name", "captured_at", "label",
                "global_filters", "max_rows_per_context", "query_timeout_ms",
                "results", "summary"):
        assert key in data, key
    t1 = data["results"]["t0001"]
    assert t1["status"] == "ok"
    assert t1["row_count"] == 1
    assert t1["columns"] == ["[Result]"]
    assert t1["rows"][0]["[Result]"] == 1.2346
    t2 = data["results"]["t0002"]
    assert t2["status"] == "skipped"
    assert t2["skip_reason"] == "timeout: smoke"
    assert t2["duration_ms"] == 0


def test_record_builders_match_contract():
    r = ser.timeout_record("M", "by_x", 60000, 60500, "SUMMARIZECOLUMNS(...)", None)
    assert r == {"status": "timeout", "measure": "M", "context": "by_x",
                 "timeout_limit_ms": 60000, "duration_ms": 60500,
                 "dax": "SUMMARIZECOLUMNS(...)", "error": "Query timeout"}
    r = ser.error_record("M", "by_x", "boom", 5)
    assert r == {"status": "error", "measure": "M", "context": "by_x",
                 "error": "boom", "duration_ms": 5}
    r = ser.aborted_record("M", "by_x")
    assert r == {"status": "aborted_memory", "measure": "M", "context": "by_x",
                 "duration_ms": 0}


def test_empty_snapshot_is_valid_json(tmp_path):
    w = ser.SnapshotWriter(tmp_path / "e.json", model_name="m", label="e",
                           global_filters=[], max_rows_per_context=0,
                           query_timeout_ms=0)
    w.finish({"total": 0, "ok": 0, "error": 0, "timeout": 0, "skipped": 0,
              "aborted_memory": 0,
              "smoke_test": {"measures_tested": 0, "measures_skipped": 0},
              "total_duration_ms": 0})
    assert json.loads((tmp_path / "e.json").read_text(encoding="utf-8"))["results"] == {}


def test_timing_csv(tmp_path):
    rows = [{"test_id": "t1", "measure": "M, with comma", "context": "c",
             "status": "ok", "row_count": 3, "duration_ms": 12, "distinct_values": 2}]
    ser.write_timing_csv(tmp_path / "t.csv", rows)
    text = (tmp_path / "t.csv").read_text(encoding="utf-8")
    assert text.splitlines()[0] == "test_id,measure,context,status,row_count,duration_ms"
    ser.write_timing_csv(tmp_path / "t2.csv", rows, include_distinct=True)
    assert (tmp_path / "t2.csv").read_text(encoding="utf-8").splitlines()[0].endswith(",distinct_values")


def test_log_entry_formats():
    e = ser.timeout_log_entry("t0081", "M", "by_x", 2547, "memory_watchdog", "reason text", "DAXQ")
    assert e == ("t0081 | M | by_x | 2547ms\n  Type: memory_watchdog\n"
                 "  Reason: reason text\n  DAX: DAXQ\n\n")
    e = ser.error_log_entry("t1", "M", "c", "DAXQ", "err")
    assert e == "t1 | M | c\n  DAX: DAXQ\n  Error: err\n\n"
    e = ser.timeout_errorlog_entry("t1", "M", "c", 60500, 60000, "DAXQ")
    assert e == "t1 | M | c\n  TIMEOUT after 60500ms (limit: 60000ms)\n  DAX: DAXQ\n\n"


def test_testplan(tmp_path):
    ser.write_testplan(tmp_path / "p.json", "lbl",
                       [{"test_id": "t1", "measure": "M", "context": "c", "dax": "Q"}])
    data = json.loads((tmp_path / "p.json").read_text(encoding="utf-8"))
    assert data["label"] == "lbl"
    assert data["tests"][0]["dax"] == "Q"


def test_compare_snapshots_compat(tmp_path):
    """Golden test: the unchanged compare-snapshots.py must accept our output."""
    base = _make_snapshot(tmp_path, "base.json", 100.0)
    same = _make_snapshot(tmp_path, "same.json", 100.0)
    diff = _make_snapshot(tmp_path, "diff.json", 200.0)
    script = Path(__file__).resolve().parents[1] / "scripts" / "compare-snapshots.py"
    # PYTHONIOENCODING=utf-8 ensures box-drawing chars in compare-snapshots.py
    # summary output don't trigger UnicodeEncodeError on Windows cp1252 consoles.
    env = {**os.environ, "PYTHONIOENCODING": "utf-8"}
    ok = subprocess.run([sys.executable, str(script), str(base), str(same),
                         "--output", str(tmp_path / "ok.xlsx")],
                        capture_output=True, text=True, encoding="utf-8", env=env)
    assert ok.returncode == 0, ok.stdout + ok.stderr
    assert (tmp_path / "ok.xlsx").is_file()
    bad = subprocess.run([sys.executable, str(script), str(base), str(diff),
                          "--output", str(tmp_path / "bad.xlsx")],
                         capture_output=True, text=True, encoding="utf-8", env=env)
    assert bad.returncode == 1, bad.stdout + bad.stderr
