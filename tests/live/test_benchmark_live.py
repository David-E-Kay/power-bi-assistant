import csv
import os

import pytest

pytestmark = pytest.mark.live
if os.environ.get("PBI_LIVE") != "1":
    pytest.skip("requires PBI Desktop open; set PBI_LIVE=1", allow_module_level=True)


def test_benchmark_run(tmp_path):
    from pbi_capture.config import BenchmarkConfig
    from pbi_capture.discovery import resolve_connection
    from pbi_capture.executor import execute_dax
    from pbi_capture.runner import run_benchmark

    conn_str = resolve_connection()
    res = execute_dax(conn_str,
                      'EVALUATE SELECTCOLUMNS(TOPN(2, INFO.MEASURES(), [Name], ASC), '
                      '"name", [Name])', 30000, 0)
    if res.status != "ok" or not res.rows:
        pytest.skip("INFO.MEASURES() unavailable — run benchmark_measures.py manually")
    measures = [r[res.columns[0]] for r in res.rows]

    cfg = BenchmarkConfig(label="live-bench", output_dir=str(tmp_path),
                          measures=measures)
    cfg.connection.connection_string = conn_str
    assert run_benchmark(cfg) == 0
    csv_path = tmp_path / "live-bench-timing.csv"
    assert csv_path.is_file()
    with open(csv_path, encoding="utf-8", newline="") as f:
        rows = list(csv.DictReader(f))
    assert len(rows) == len(measures)  # grand_total only (no dimensions configured)
    assert all(r["status"] in ("ok", "error", "timeout", "skipped") for r in rows)
    assert (tmp_path / "live-bench-config.csv").is_file()
    assert (tmp_path / "live-bench-summary.txt").is_file()
