"""DAX execution with wall-clock timeout + memory watchdog + Cancel()/Dispose backstop.

Port of ExecuteDaxWithTimeout (capture-snapshot.csx v9 / benchmark v5), with the
abort path revised per the 2026-06-11 spike finding (spec deviation table, row 103):
cmd.Cancel() reliably interrupts SE-bound queries (~0.04s) but is a no-op for pure
formula-engine materializations; dropping the connection (conn.Dispose()) interrupts
ANY query in ~0.03s, so there is no leaked-connection path. A fresh connection per
query makes discarding a timed-out connection free.

The TE EvaluateDax() fallback is intentionally gone (spec deviation table).
"""
import threading
import time
from dataclasses import dataclass

from .clr_boot import ensure_adomd
from .watchdog import is_memory_critical

POLL_SECONDS = 0.5
MEM_CRITICAL_POLLS_REQUIRED = 3  # 3 x 500ms = 1.5s sustained pressure (debounce)
CANCEL_GRACE_SECONDS = 3         # polite Cancel() grace before dropping the socket
CANCEL_UNWIND_SECONDS = 10       # let the worker unwind after the connection drop


@dataclass
class QueryResult:
    status: str                  # "ok" | "timeout" | "error"
    columns: list | None = None
    rows: list | None = None     # list[dict[column -> python value]]
    error: str | None = None


def _to_py(value):
    """Marshal an ADOMD cell to Python. pythonnet converts primitives already
    (Double->float, Int64->int, String->str, Boolean->bool); DBNull, Decimal
    and DateTime need explicit handling."""
    import System
    if value is None or isinstance(value, System.DBNull):
        return None
    if isinstance(value, System.Decimal):
        # Culture-safe binary conversion; compare tolerance (1e-4) + 4-decimal
        # rounding absorb the decimal->double representation change.
        return System.Decimal.ToDouble(value)
    if isinstance(value, System.DateTime):
        return value.ToString("o")  # invariant round-trip format
    return value


def execute_dax(conn_str: str, dax: str, timeout_ms: int,
                memory_threshold_pct: float) -> QueryResult:
    """Run a complete DAX query (caller includes EVALUATE) with enforcement.

    The query runs on a worker thread; this thread polls every 500ms for
    wall-clock expiry and sustained memory pressure. On abort it calls
    cmd.Cancel() (interrupts SE-bound queries); if the worker has not unwound
    within CANCEL_GRACE_SECONDS, it drops the connection with conn.Dispose(),
    which interrupts ANY query (~0.03s) by killing the socket — the server then
    cancels the orphaned session. There is no leaked-connection path: a fresh
    connection is created per query, so discarding a timed-out one is free.
    """
    ensure_adomd()
    from Microsoft.AnalysisServices.AdomdClient import AdomdCommand, AdomdConnection

    conn = AdomdConnection(conn_str)
    cmd = AdomdCommand(dax, conn)
    state = {"columns": None, "rows": None, "exc": None}

    def _run():
        try:
            reader = cmd.ExecuteReader()
            try:
                n = reader.FieldCount
                cols = [reader.GetName(i) for i in range(n)]
                rows = []
                while reader.Read():
                    rows.append({cols[i]: _to_py(reader.GetValue(i)) for i in range(n)})
                state["columns"], state["rows"] = cols, rows
            finally:
                reader.Dispose()
        except Exception as ex:  # CLR exceptions surface as Python exceptions
            state["exc"] = ex

    try:
        conn.Open()
        # Always 0: the Python watchdog (below) owns the wall-clock timeout. A
        # server-side CommandTimeout starts a CONCURRENT engine abort that blocks
        # conn.Dispose() ~30s on pure-FE queries, defeating the Cancel->Dispose
        # backstop (live finding 2026-06-13; extends spec deviation row 103).
        cmd.CommandTimeout = 0
        worker = threading.Thread(target=_run, daemon=True)
        worker.start()
        deadline = None if timeout_ms <= 0 else time.monotonic() + timeout_ms / 1000.0
        mem_critical = 0
        abort_reason = None
        while True:
            worker.join(POLL_SECONDS)
            if not worker.is_alive():
                break
            if deadline is not None and time.monotonic() >= deadline:
                abort_reason = f"wall-clock timeout after {timeout_ms}ms (cancelled by watchdog)"
                break
            if is_memory_critical(memory_threshold_pct):
                mem_critical += 1
                if mem_critical >= MEM_CRITICAL_POLLS_REQUIRED:
                    abort_reason = (
                        f"memory threshold {memory_threshold_pct}% sustained for "
                        f"{int(MEM_CRITICAL_POLLS_REQUIRED * POLL_SECONDS * 1000)}ms "
                        "mid-query (cancelled by watchdog)")
                    break
            else:
                mem_critical = 0

        if abort_reason is not None:
            try:
                cmd.Cancel()
            except Exception:
                pass  # best effort — interrupts SE-bound queries; no-op for pure FE
            worker.join(CANCEL_GRACE_SECONDS)
            if worker.is_alive():
                # Cancel() did not unwind (pure formula-engine query): drop the
                # socket. Dispose interrupts ANY query in ~0.03s; the server then
                # cancels the orphaned session.
                try:
                    conn.Dispose()
                except Exception:
                    pass
                worker.join(CANCEL_UNWIND_SECONDS)
            return QueryResult(status="timeout", error=abort_reason)

        if state["exc"] is not None:
            msg = str(state["exc"])
            is_timeout = "cancel" in msg.lower() or "timeout" in msg.lower()
            return QueryResult(status="timeout" if is_timeout else "error", error=msg)

        return QueryResult(status="ok", columns=state["columns"], rows=state["rows"])
    finally:
        # Best-effort, idempotent: in the abort path conn may already be disposed
        # (the second Dispose is caught); otherwise this releases cmd + conn.
        for obj in (cmd, conn):
            try:
                obj.Dispose()
            except Exception:
                pass
