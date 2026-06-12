"""Snapshot/timing/log serialization. Pure Python — no CLR imports.

Schema-compatible (in meaning, not bytes) with capture-snapshot.csx v9 so the
unchanged compare-snapshots.py keeps working — spec contract #1/#3.
"""
import csv
import json
import math
from datetime import datetime, timezone

BLANK = "__BLANK__"
NAN = "__NaN__"
INF = "__INF__"


def to_jsonable(value):
    """Port of C# SerializeValue: sentinels + 4-decimal rounding.

    bool is checked before int (bool subclasses int in Python); C# serialized
    booleans via ToString() => "True"/"False" strings — parity preserved."""
    if value is None:
        return BLANK
    if isinstance(value, bool):
        return str(value)
    if isinstance(value, float):
        if math.isnan(value):
            return NAN
        if math.isinf(value):
            return INF
        return round(value, 4)
    if isinstance(value, int):
        return value
    return str(value)


def utc_now_iso() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


class SnapshotWriter:
    """Streams one JSON result record per test case, flushing after each, so a
    force-killed run leaves inspectable output and memory stays flat."""

    def __init__(self, path, *, model_name, label, global_filters,
                 max_rows_per_context, query_timeout_ms):
        self._f = open(path, "w", encoding="utf-8", newline="\n")
        self._first = True
        header = {
            "snapshot_version": "1.0",
            "model_name": model_name,
            "captured_at": utc_now_iso(),
            "label": label,
            "global_filters": list(global_filters),
            "max_rows_per_context": max_rows_per_context,
            "query_timeout_ms": query_timeout_ms,
        }
        head = json.dumps(header, ensure_ascii=False)[1:-1]  # strip outer braces
        self._f.write("{\n" + head + ",\n\"results\": {\n")
        self._f.flush()

    def write_result(self, test_id: str, record: dict) -> None:
        prefix = "" if self._first else ",\n"
        self._first = False
        self._f.write(f'{prefix}"{test_id}": {json.dumps(record, ensure_ascii=False)}')
        self._f.flush()

    def finish(self, summary: dict) -> None:
        self._f.write("\n},\n\"summary\": "
                      + json.dumps(summary, ensure_ascii=False, indent=2) + "\n}\n")
        self._f.close()


def ok_record(measure, context, columns, rows, duration_ms):
    columns = list(columns or [])
    rows = rows or []
    return {
        "status": "ok", "measure": measure, "context": context,
        "row_count": len(rows),
        "columns": columns,
        "rows": [{col: to_jsonable(r[col]) for col in columns} for r in rows],
        "duration_ms": duration_ms,
    }


def timeout_record(measure, context, timeout_limit_ms, duration_ms, dax, error):
    return {"status": "timeout", "measure": measure, "context": context,
            "timeout_limit_ms": timeout_limit_ms, "duration_ms": duration_ms,
            "dax": dax, "error": error or "Query timeout"}


def error_record(measure, context, error, duration_ms):
    return {"status": "error", "measure": measure, "context": context,
            "error": error or "", "duration_ms": duration_ms}


def skipped_record(measure, context, skip_reason):
    return {"status": "skipped", "measure": measure, "context": context,
            "skip_reason": skip_reason, "duration_ms": 0}


def aborted_record(measure, context):
    return {"status": "aborted_memory", "measure": measure, "context": context,
            "duration_ms": 0}


def write_testplan(path, label, tests):
    """tests: list of dicts with test_id, measure, context, dax. Written
    pre-flight so a force-killed run can still be inspected."""
    doc = {"label": label, "captured_at": utc_now_iso(), "tests": tests}
    with open(path, "w", encoding="utf-8") as f:
        json.dump(doc, f, ensure_ascii=False, indent=2)


def write_timing_csv(path, rows, include_distinct=False):
    cols = ["test_id", "measure", "context", "status", "row_count", "duration_ms"]
    if include_distinct:
        cols.append("distinct_values")
    with open(path, "w", encoding="utf-8", newline="") as f:
        w = csv.writer(f)
        w.writerow(cols)
        for r in rows:
            w.writerow([r[c] for c in cols])


def timeout_log_entry(test_id, measure, context, elapsed_ms, type_tag, reason, dax):
    return (f"{test_id} | {measure} | {context} | {elapsed_ms}ms\n"
            f"  Type: {type_tag}\n  Reason: {reason}\n  DAX: {dax}\n\n")


def error_log_entry(test_id, measure, context, dax, error):
    return f"{test_id} | {measure} | {context}\n  DAX: {dax}\n  Error: {error}\n\n"


def timeout_errorlog_entry(test_id, measure, context, elapsed_ms, limit_ms, dax):
    return (f"{test_id} | {measure} | {context}\n"
            f"  TIMEOUT after {elapsed_ms}ms (limit: {limit_ms}ms)\n  DAX: {dax}\n\n")
