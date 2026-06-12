"""Spike: proves CLR load, local connectivity, and cancel-in-flight.

The port scan here is deliberately duplicated inline (discovery.py doesn't
exist yet at spike time) — it is replaced by pbi_capture.discovery in Task 6.
"""
import os
import threading
import time
from pathlib import Path

import pytest

pytestmark = pytest.mark.live
LIVE = os.environ.get("PBI_LIVE") == "1"
if not LIVE:
    pytest.skip("requires PBI Desktop open; set PBI_LIVE=1", allow_module_level=True)

# Note: CROSSJOIN requires distinct column names; SELECTCOLUMNS renames [Date] -> "D1"
# so the two sides have [D1] and [Date] — no collision. The spec's original used two
# bare CALENDAR() tables which both produce [Date] and fail with a DAX error.
SLOW_DAX = (
    'EVALUATE ROW("x", COUNTROWS(CROSSJOIN('
    'SELECTCOLUMNS(CALENDAR(DATE(1900,1,1), DATE(2200,12,31)), "D1", [Date]), '
    "CALENDAR(DATE(1900,1,1), DATE(2200,12,31)))))"
)


def _find_local_port() -> str:
    import socket

    roots = [
        Path(os.environ["LOCALAPPDATA"]) / "Microsoft/Power BI Desktop/AnalysisServicesWorkspaces",
        Path(os.environ["LOCALAPPDATA"]) / "Packages/Microsoft.MicrosoftPowerBIDesktop_8wekyb3d8bbwe"
        / "LocalCache/Local/Microsoft/Power BI Desktop Store App/AnalysisServicesWorkspaces",
        Path(os.environ["USERPROFILE"]) / "Microsoft/Power BI Desktop Store App/AnalysisServicesWorkspaces",
    ]
    ports = []
    for root in roots:
        if not root.is_dir():
            continue
        for ws in root.glob("AnalysisServicesWorkspace*"):
            for pf in (ws / "Data" / "msmdsrv.port.txt", ws / "msmdsrv.port.txt"):
                if pf.is_file():
                    digits = "".join(ch for ch in pf.read_bytes().decode("ascii", "ignore") if ch.isdigit())
                    if digits:
                        ports.append(digits)
                    break
    assert ports, "No running msmdsrv found — open the model in PBI Desktop first"
    # Multiple stale workspaces may exist; probe for the live one.
    for port in ports:
        try:
            with socket.create_connection(("localhost", int(port)), timeout=1):
                return port
        except OSError:
            continue
    raise RuntimeError(
        f"msmdsrv port files found ({ports}) but none are accepting connections — "
        "is a Power BI Desktop model open?"
    )


def _open_connection():
    from pbi_capture.clr_boot import ensure_adomd
    ensure_adomd()
    from Microsoft.AnalysisServices.AdomdClient import AdomdConnection
    port = _find_local_port()
    conn = AdomdConnection(f"Provider=MSOLAP;Data Source=localhost:{port};")
    conn.Open()
    ds = conn.GetSchemaDataSet("DBSCHEMA_CATALOGS", None)
    conn.ChangeDatabase(str(ds.Tables[0].Rows[0]["CATALOG_NAME"]))
    return conn


def test_connect_and_evaluate():
    conn = _open_connection()
    from Microsoft.AnalysisServices.AdomdClient import AdomdCommand
    cmd = AdomdCommand('EVALUATE ROW("x", 1 + 1)', conn)
    reader = cmd.ExecuteReader()
    assert reader.Read()
    assert int(reader.GetValue(0)) == 2
    reader.Dispose()
    conn.Dispose()


def test_watchdog_interrupts_blocked_query():
    """THE gate: a watchdog thread must be able to interrupt a running query and
    reclaim resources. Proven mechanism on this machine:

    - The GIL IS released during ExecuteReader (this thread runs the whole time).
    - cmd.Cancel() interrupts SE-bound queries (the common slow-measure case) in
      ~0.04s, BUT is a no-op for a pure formula-engine materialization like the
      CROSSJOIN below (90s+ with no effect).
    - Dropping the connection (conn.Dispose) interrupts ANY query in ~0.03s — it
      kills the client socket (WSACancelBlockingCall) and the server cancels the
      orphaned session. Since the executor uses a fresh connection per query,
      discarding a timed-out one is free.

    The executor therefore uses: Cancel() -> short grace -> Dispose() backstop.
    This test exercises the WORST case (FE CROSSJOIN): Cancel will not help, so
    the Dispose backstop must unblock the worker fast. (SE-bound Cancel is
    validated against real measures in the Task 7/8 live tests.)"""
    conn = _open_connection()
    from Microsoft.AnalysisServices.AdomdClient import AdomdCommand
    cmd = AdomdCommand(SLOW_DAX, conn)
    done = threading.Event()
    err: list[str] = []

    def run():
        try:
            cmd.ExecuteReader()
        except Exception as ex:
            err.append(str(ex))
        finally:
            done.set()

    t = threading.Thread(target=run, daemon=True)
    start = time.monotonic()
    t.start()
    time.sleep(2.0)
    assert not done.is_set(), "slow query finished too fast — widen the CALENDAR range"
    cmd.Cancel()                  # polite cancel; a no-op for this FE-bound query
    if not done.wait(3):          # short grace for the SE-cancel path
        conn.Dispose()            # backstop: drop the connection to force-interrupt
    assert done.wait(10), "watchdog could not interrupt the query (Cancel + Dispose both failed)"
    assert time.monotonic() - start < 20
    assert err, "expected the interrupted query to raise"
