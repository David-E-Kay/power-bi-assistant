"""Run orchestration: ports the execution engines of capture-snapshot.csx (v9)
and benchmark-measures.csx (v5). The stdout report replaces TE's Info() popup
and is also written to {label}-summary.txt for powerbi-context-mode analysis.
"""
import json
import time
import urllib.request
from pathlib import Path

from . import daxgen
from . import serialization as ser
from .config import BenchmarkConfig, CaptureConfig
from .discovery import resolve_connection
from .executor import execute_dax
from .watchdog import is_memory_critical

DIAG_TEST_CAP = 8


# ── shared helpers ───────────────────────────────────────────────────────────

def _build_capture_cases(cfg: CaptureConfig):
    cases = []
    for t in cfg.tests:
        ref = daxgen.build_measure_ref(t.measure, cfg.global_filters)
        dax = daxgen.build_capture_query(t.context, cfg.group_by_columns, ref,
                                         cfg.max_rows_per_context)
        cases.append((t.id, t.measure, t.context, dax))
    return cases


def _timeout_type(error_msg: str) -> str:
    msg = (error_msg or "").lower()
    if "memory threshold" in msg:
        return "memory_watchdog"
    if "wall-clock timeout" in msg:
        return "query_timeout"
    return "query_error"  # defensive fallback — shouldn't normally hit


def _smoke_type(status: str, reason: str) -> str:
    if "memory threshold" in (reason or "").lower():
        return "memory_watchdog"
    if status == "timeout":
        return "smoketest_timeout"
    return "smoketest_error"


def _run_smoke(conn_str, measures, cfg, measure_ref_fn, timeout_log):
    """Pre-flight: EVALUATE ROW per unique measure. Broken measures are skipped
    in the main run (one skipped record per permutation, like the .csx)."""
    skipped, smoke_results = set(), {}
    for idx, m in enumerate(measures, 1):
        dax = daxgen.smoke_query(measure_ref_fn(m))
        start = time.monotonic()
        res = execute_dax(conn_str, dax, cfg.smoke_test_timeout_ms,
                          cfg.memory_threshold_pct)
        elapsed = int((time.monotonic() - start) * 1000)
        if res.status != "ok":
            reason = res.error or "unknown error"
            skipped.add(m)
            smoke_results[m] = f"{res.status}: {reason}"
            timeout_log.append(ser.timeout_log_entry(
                f"s{idx:04d}", m, "smoke_test", elapsed,
                _smoke_type(res.status, reason), reason, dax))
    return skipped, smoke_results


def _adaptive_card(title: str, facts: list[tuple[str, str]], extra_blocks=None) -> dict:
    body = [{"type": "TextBlock", "text": title, "weight": "Bolder", "size": "Medium"},
            {"type": "FactSet",
             "facts": [{"title": k, "value": v} for k, v in facts]}]
    if extra_blocks:
        body.extend(extra_blocks)
    return {"type": "message", "attachments": [{
        "contentType": "application/vnd.microsoft.card.adaptive",
        "content": {"$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
                    "type": "AdaptiveCard", "version": "1.4", "body": body}}]}


def _send_teams_card(url, card: dict) -> str | None:
    """Returns a warning string on failure (appended to the report), else None."""
    if not url:
        return None
    try:
        req = urllib.request.Request(
            url, data=json.dumps(card).encode("utf-8"),
            headers={"Content-Type": "application/json"})
        urllib.request.urlopen(req, timeout=15).read()
        return None
    except Exception as ex:
        return f"  Teams notification failed: {ex}"


def _first_lines(entries: list[str], n_entries: int = 3) -> list[str]:
    lines = "".join(entries[:n_entries]).rstrip("\n").split("\n")
    return [f"    {ln}" for ln in lines]


# ── capture ──────────────────────────────────────────────────────────────────

def run_capture(cfg: CaptureConfig) -> int:
    out_dir = Path(cfg.output_dir)
    out_dir.mkdir(parents=True, exist_ok=True)
    conn_str = resolve_connection(cfg.connection.connection_string, cfg.connection.port)
    cases = _build_capture_cases(cfg)

    timeout_log: list[str] = []
    error_log: list[str] = []
    skipped, smoke_results = set(), {}
    unique_measures = list(dict.fromkeys(m for _, m, _, _ in cases))
    if cfg.skip_on_smoke_failure:
        skipped, smoke_results = _run_smoke(
            conn_str, unique_measures, cfg,
            lambda m: daxgen.build_measure_ref(m, cfg.global_filters), timeout_log)

    ser.write_testplan(out_dir / f"{cfg.label}-testplan.json", cfg.label,
                       [{"test_id": i, "measure": m, "context": c, "dax": d}
                        for i, m, c, d in cases])

    writer = ser.SnapshotWriter(
        out_dir / f"{cfg.label}.json", model_name=cfg.model_name, label=cfg.label,
        global_filters=cfg.global_filters,
        max_rows_per_context=cfg.max_rows_per_context,
        query_timeout_ms=cfg.query_timeout_ms)

    timing_rows = []
    counts = {"ok": 0, "error": 0, "timeout": 0, "skipped": 0, "aborted_memory": 0}
    total = min(DIAG_TEST_CAP, len(cases)) if cfg.diagnostic_mode else len(cases)
    run_start = time.monotonic()
    memory_aborted = False

    for i in range(total):
        test_id, measure, context, dax = cases[i]

        # Between-test memory check with a 1s settle window (post-query
        # working-set release is cleanup noise, not sustained strain).
        if not memory_aborted and is_memory_critical(cfg.memory_threshold_pct):
            time.sleep(1.0)
            if is_memory_critical(cfg.memory_threshold_pct):
                memory_aborted = True
                for j in range(i, total):
                    a_id, a_m, a_c, _ = cases[j]
                    writer.write_result(a_id, ser.aborted_record(a_m, a_c))
                    counts["aborted_memory"] += 1
                    timing_rows.append({"test_id": a_id, "measure": a_m,
                                        "context": a_c, "status": "aborted_memory",
                                        "row_count": 0, "duration_ms": 0})
                break

        if measure in skipped:
            counts["skipped"] += 1
            writer.write_result(test_id, ser.skipped_record(measure, context,
                                                            smoke_results[measure]))
            timing_rows.append({"test_id": test_id, "measure": measure,
                                "context": context, "status": "skipped",
                                "row_count": 0, "duration_ms": 0})
            continue

        start = time.monotonic()
        res = execute_dax(conn_str, "EVALUATE " + dax, cfg.query_timeout_ms,
                          cfg.memory_threshold_pct)
        elapsed = int((time.monotonic() - start) * 1000)

        if res.status == "ok":
            counts["ok"] += 1
            rows = res.rows or []
            writer.write_result(test_id, ser.ok_record(measure, context,
                                                       res.columns or [], rows, elapsed))
            timing_rows.append({"test_id": test_id, "measure": measure,
                                "context": context, "status": "ok",
                                "row_count": len(rows), "duration_ms": elapsed})
            if cfg.diagnostic_mode:
                print(f"[DIAG] {test_id} OK — {measure} [{context}] — "
                      f"{len(rows)} rows, {elapsed}ms\nDAX: {dax}")
        elif res.status == "timeout":
            counts["timeout"] += 1
            error_log.append(ser.timeout_errorlog_entry(
                test_id, measure, context, elapsed, cfg.query_timeout_ms, dax))
            timeout_log.append(ser.timeout_log_entry(
                test_id, measure, context, elapsed, _timeout_type(res.error),
                res.error or "", dax))
            writer.write_result(test_id, ser.timeout_record(
                measure, context, cfg.query_timeout_ms, elapsed, dax, res.error))
            timing_rows.append({"test_id": test_id, "measure": measure,
                                "context": context, "status": "timeout",
                                "row_count": 0, "duration_ms": elapsed})
            if cfg.diagnostic_mode:
                print(f"[DIAG] {test_id} TIMEOUT — {measure} [{context}] — {elapsed}ms")
        else:
            counts["error"] += 1
            error_log.append(ser.error_log_entry(test_id, measure, context, dax,
                                                 res.error or ""))
            writer.write_result(test_id, ser.error_record(measure, context,
                                                          res.error, elapsed))
            timing_rows.append({"test_id": test_id, "measure": measure,
                                "context": context, "status": "error",
                                "row_count": 0, "duration_ms": elapsed})
            if cfg.diagnostic_mode:
                print(f"[DIAG] {test_id} ERROR — {measure} [{context}]\nError: {res.error}")

    total_ms = int((time.monotonic() - run_start) * 1000)
    writer.finish({"total": total, **counts,
                   "smoke_test": {
                       "measures_tested": len(unique_measures) if cfg.skip_on_smoke_failure else 0,
                       "measures_skipped": len(skipped)},
                   "total_duration_ms": total_ms})

    if counts["error"] > 0:
        (out_dir / f"{cfg.label}-errors.log").write_text("".join(error_log),
                                                         encoding="utf-8")
    if counts["timeout"] > 0 or skipped:
        (out_dir / f"{cfg.label}-timeouts.log").write_text("".join(timeout_log),
                                                           encoding="utf-8")
    ser.write_timing_csv(out_dir / f"{cfg.label}-timing.csv", timing_rows)

    report = _capture_report(cfg, counts, total, total_ms, skipped, smoke_results,
                             error_log, timeout_log, memory_aborted, out_dir)
    is_clean = (counts["error"] == 0 and counts["timeout"] == 0
                and counts["skipped"] == 0 and counts["aborted_memory"] == 0)
    status = "✅ All Passed" if is_clean else (
        f"⚠️ {counts['error']} errors / {counts['timeout']} timeouts / "
        f"{counts['skipped']} skipped"
        + (f" / {counts['aborted_memory']} aborted" if counts["aborted_memory"] else ""))
    facts = [("Label", cfg.label), ("Status", status),
             ("Tests", f"{counts['ok']} OK / {counts['error']} errors / "
                       f"{counts['timeout']} timeouts / {total} total"),
             ("Duration", f"{total_ms / 60000:.1f} min")]
    if counts["skipped"]:
        facts.append(("Skipped", f"{counts['skipped']} (smoke test)"))
    if counts["aborted_memory"]:
        facts.append(("Aborted (memory)", str(counts["aborted_memory"])))
    warn = _send_teams_card(cfg.teams_webhook_url,
                            _adaptive_card("PBI Regression Test Complete", facts))
    if warn:
        report += warn + "\n"

    (out_dir / f"{cfg.label}-summary.txt").write_text(report, encoding="utf-8")
    print(report)
    return 0


def _capture_report(cfg, counts, total, total_ms, skipped, smoke_results,
                    error_log, timeout_log, memory_aborted, out_dir) -> str:
    bar = "═" * 60
    lines = [bar, "  Regression Test Capture Complete", bar,
             f"  Label:    {cfg.label}",
             f"  Tests:    {total}",
             f"  OK:       {counts['ok']}",
             f"  Errors:   {counts['error']}",
             f"  Timeouts: {counts['timeout']}",
             f"  Skipped:  {counts['skipped']}"]
    if counts["aborted_memory"]:
        lines.append(f"  Aborted (memory): {counts['aborted_memory']}")
    lines += [f"  Duration: {total_ms / 60000:.1f} minutes",
              f"  Output:   {out_dir / (cfg.label + '.json')}",
              f"  Timing:   {out_dir / (cfg.label + '-timing.csv')}"]
    if cfg.global_filters:
        lines.append(f"  Global filters: {len(cfg.global_filters)} applied")
    if cfg.max_rows_per_context > 0:
        lines.append(f"  Row cap: TOPN({cfg.max_rows_per_context}) per grouped context")
    lines.append(f"  Timeout: {cfg.query_timeout_ms}ms per query (ADOMD direct)")
    if skipped:
        lines.append(f"  Smoke test: {len(skipped)} measure(s) skipped")
        lines += [f"    - {m}: {r}" for m, r in smoke_results.items()]
    if counts["error"]:
        lines.append(f"  Error log: {out_dir / (cfg.label + '-errors.log')}")
        lines.append("")
        lines.append("  First 3 errors:")
        lines += _first_lines(error_log)
    if counts["timeout"]:
        lines.append(f"  Timeout log: {out_dir / (cfg.label + '-timeouts.log')}")
        lines.append("")
        lines.append("  First 3 timeouts:")
        lines += _first_lines(timeout_log)
    if memory_aborted:
        lines.append(f"  ⚠ Run ABORTED due to memory threshold "
                     f"({cfg.memory_threshold_pct}%)")
    lines.append(bar)
    return "\n".join(lines) + "\n"


# --- benchmark (Task 9) ---
