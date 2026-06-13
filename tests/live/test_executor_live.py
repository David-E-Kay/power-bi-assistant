import os
import time

import pytest

pytestmark = pytest.mark.live
if os.environ.get("PBI_LIVE") != "1":
    pytest.skip("requires PBI Desktop open; set PBI_LIVE=1", allow_module_level=True)

# Note: CROSSJOIN requires distinct column names; SELECTCOLUMNS renames [Date] -> "D1"
# so the two sides have [D1] and [Date] — no collision. Two bare CALENDAR() tables
# both produce [Date] and fail immediately with a DAX error (not a timeout).
SLOW_DAX = (
    'EVALUATE ROW("x", COUNTROWS(CROSSJOIN('
    'SELECTCOLUMNS(CALENDAR(DATE(1900,1,1), DATE(2200,12,31)), "D1", [Date]), '
    "CALENDAR(DATE(1900,1,1), DATE(2200,12,31)))))"
)


def _conn():
    from pbi_capture.discovery import resolve_connection
    return resolve_connection()


def test_simple_query_ok():
    from pbi_capture.executor import execute_dax
    res = execute_dax(_conn(), 'EVALUATE ROW("x", 1 + 1)', 30000, 0)
    assert res.status == "ok"
    assert res.rows[0][res.columns[0]] == 2


def test_blank_marshals_to_none():
    from pbi_capture.executor import execute_dax
    res = execute_dax(_conn(), 'EVALUATE ROW("x", BLANK())', 30000, 0)
    assert res.status == "ok"
    assert res.rows[0][res.columns[0]] is None


def test_syntax_error_reports_error():
    from pbi_capture.executor import execute_dax
    res = execute_dax(_conn(), "EVALUATE THIS IS NOT DAX", 30000, 0)
    assert res.status == "error"
    assert res.error


def test_wall_clock_timeout_cancels():
    """Spec acceptance criterion #2."""
    from pbi_capture.executor import execute_dax
    conn_str = _conn()          # resolve before timing — discovery probes SSAS
    start = time.monotonic()
    res = execute_dax(conn_str, SLOW_DAX, 3000, 0)
    elapsed = time.monotonic() - start
    assert res.status == "timeout"
    assert "wall-clock timeout" in res.error
    assert elapsed < 3 + 3 + 10 + 2  # timeout + grace + unwind budget + slack


def test_fresh_connection_after_timeout():
    """Abort-path revision: discarding a timed-out connection (socket dropped via
    conn.Dispose()) must not poison the next query — a fresh connection succeeds."""
    from pbi_capture.executor import execute_dax
    timed_out = execute_dax(_conn(), SLOW_DAX, 3000, 0)
    assert timed_out.status == "timeout"
    ok = execute_dax(_conn(), 'EVALUATE ROW("x", 1 + 1)', 30000, 0)
    assert ok.status == "ok"
    assert ok.rows[0][ok.columns[0]] == 2
