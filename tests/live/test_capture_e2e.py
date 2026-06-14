"""Live E2E: capture the open model twice, compare — zero deltas expected.
Spec acceptance criterion #1."""
import json
import os
import subprocess
import sys
from pathlib import Path

import pytest

pytestmark = pytest.mark.live
if os.environ.get("PBI_LIVE") != "1":
    pytest.skip("requires PBI Desktop open; set PBI_LIVE=1", allow_module_level=True)


def _discover_measures(conn_str, n=3):
    from pbi_capture.executor import execute_dax
    res = execute_dax(
        conn_str,
        'EVALUATE SELECTCOLUMNS(TOPN(%d, INFO.MEASURES(), [Name], ASC), "name", [Name])' % n,
        30000, 0)
    if res.status != "ok" or not res.rows:
        pytest.skip("INFO.MEASURES() unavailable on this engine — build the "
                    "config manually and run capture_snapshot.py by hand")
    return [r[res.columns[0]] for r in res.rows]


def test_same_model_twice_zero_deltas(tmp_path):
    from pbi_capture.config import CaptureConfig, TestCase
    from pbi_capture.discovery import resolve_connection
    from pbi_capture.runner import run_capture

    conn_str = resolve_connection()
    measures = _discover_measures(conn_str)
    tests = [TestCase(f"t{i:04d}", m, "grand_total")
             for i, m in enumerate(measures, 1)]

    snapshots = []
    for label in ("live-a", "live-b"):
        cfg = CaptureConfig(label=label, model_name="LiveE2E",
                            output_dir=str(tmp_path), tests=tests)
        cfg.connection.connection_string = conn_str
        assert run_capture(cfg) == 0
        snap = tmp_path / f"{label}.json"
        assert snap.is_file()
        data = json.loads(snap.read_text(encoding="utf-8"))
        assert data["summary"]["total"] == len(tests)
        snapshots.append(snap)
        assert (tmp_path / f"{label}-testplan.json").is_file()
        assert (tmp_path / f"{label}-timing.csv").is_file()
        assert (tmp_path / f"{label}-summary.txt").is_file()

    script = Path(__file__).resolve().parents[2] / "scripts" / "compare-snapshots.py"
    cmp_run = subprocess.run(
        [sys.executable, str(script), str(snapshots[0]), str(snapshots[1]),
         "--output", str(tmp_path / "report.xlsx")],
        capture_output=True, text=True, encoding="utf-8",
        env={**os.environ, "PYTHONIOENCODING": "utf-8"})
    assert cmp_run.returncode == 0, cmp_run.stdout + cmp_run.stderr
