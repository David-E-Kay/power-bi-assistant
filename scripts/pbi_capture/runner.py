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
from .notify import send_desktop_toast
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
    toast_warn = send_desktop_toast(
        f"PBI Regression — {status}",
        [f"Label: {cfg.label}",
         f"Tests: {counts['ok']} OK / {counts['error']} err / "
         f"{counts['timeout']} timeout / {total} total",
         f"Duration: {total_ms / 60000:.1f} min"])
    if toast_warn:
        report += toast_warn + "\n"

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


# ── benchmark ────────────────────────────────────────────────────────────────

def _build_benchmark_cases(cfg: BenchmarkConfig):
    g_args = daxgen.build_treatas_args(cfg.global_filters)
    g_frag = daxgen.filter_fragment(g_args)
    x_args = daxgen.build_treatas_args(cfg.cross_product_value_filters)
    x_frag = daxgen.filter_fragment(x_args)
    cases = []
    n = 0
    for m in cfg.measures:
        ref = f"[{m}]"
        n += 1
        cases.append((f"b{n:04d}", m, "grand_total",
                      daxgen.build_benchmark_grand_total(ref, g_args)))
        for label, col in cfg.single_slice_dimensions.items():
            n += 1
            cases.append((f"b{n:04d}", m, label,
                          daxgen.build_benchmark_slice(col, g_frag, ref,
                                                       cfg.max_rows_per_context)))
        if cfg.cross_product_columns:
            n += 1
            cases.append((f"b{n:04d}", m,
                          daxgen.cross_product_label(cfg.cross_product_columns),
                          daxgen.build_benchmark_cross_product(
                              cfg.cross_product_columns, g_frag, x_frag, ref,
                              cfg.max_rows_per_context)))
    return cases


def _distinct_values(columns, rows) -> int:
    """Distinct count over the LAST column (the Result column by construction)."""
    if not columns or not rows:
        return 0
    last = columns[-1]
    return len({str(r[last]) if r[last] is not None else "__NULL__" for r in rows})


def _false_fast(timing_rows):
    """distinct_values==1 with row_count>1 on a non-grand-total context means
    the dimension isn't filtering the measure (no relationship path)."""
    return [t for t in timing_rows
            if t["status"] == "ok" and t["context"] != "grand_total"
            and t.get("distinct_values") == 1 and t["row_count"] > 1]


def _write_benchmark_xlsx(path, cfg, counts, total, total_ms, timing_rows,
                          skipped, smoke_results, memory_aborted) -> str:
    """Write the polished benchmark .xlsx (mirrors the regression report).
    Optional and non-fatal — returns a one-line note for the summary report."""
    try:
        from .benchmark_report import write_benchmark_report
        write_benchmark_report(path, cfg=cfg, counts=counts, total=total,
                               total_ms=total_ms, timing_rows=timing_rows,
                               skipped=skipped, smoke_results=smoke_results,
                               memory_aborted=memory_aborted)
        return f"  Report XLSX: {path}"
    except ImportError:
        return "  Report XLSX: skipped (pip install openpyxl to enable)"
    except Exception as ex:  # never fail a completed run over the report
        return f"  Report XLSX: failed ({ex})"


def _report_launch_uri(report_path: Path):
    """file:// URI for the benchmark report toast, or None when the report
    wasn't written (e.g. openpyxl missing). None keeps the toast non-clickable
    instead of pointing Explorer at a missing file."""
    return report_path.as_uri() if report_path.exists() else None


def _status_emoji(is_clean: bool) -> str:
    """Toast-title status glyph, matching the capture path's convention.
    Used only in the desktop-toast title — the benchmark `status` string
    (shared with the Teams card) stays emoji-free."""
    return "✅" if is_clean else "⚠️"


def run_benchmark(cfg: BenchmarkConfig) -> int:
    import csv

    out_dir = Path(cfg.output_dir)
    out_dir.mkdir(parents=True, exist_ok=True)
    conn_str = resolve_connection(cfg.connection.connection_string, cfg.connection.port)

    if not cfg.single_slice_dimensions and not cfg.cross_product_columns:
        print("WARNING: no dimensions defined — only grand_total will be tested.")

    cases = _build_benchmark_cases(cfg)

    timeout_log: list[str] = []
    error_log: list[str] = []
    skipped, smoke_results = set(), {}
    if cfg.skip_on_smoke_failure:
        # Benchmark smoke uses the bare measure ref (no global-filter wrap) — .csx parity.
        skipped, smoke_results = _run_smoke(
            conn_str, list(dict.fromkeys(cfg.measures)), cfg,
            lambda m: f"[{m}]", timeout_log)

    ser.write_testplan(out_dir / f"{cfg.label}-testplan.json", cfg.label,
                       [{"test_id": i, "measure": m, "context": c, "dax": d}
                        for i, m, c, d in cases])

    timing_rows = []
    counts = {"ok": 0, "error": 0, "timeout": 0, "skipped": 0, "aborted_memory": 0}
    total = min(DIAG_TEST_CAP, len(cases)) if cfg.diagnostic_mode else len(cases)
    run_start = time.monotonic()
    memory_aborted = False

    for i in range(total):
        test_id, measure, context, dax = cases[i]

        if not memory_aborted and is_memory_critical(cfg.memory_threshold_pct):
            memory_aborted = True  # benchmark aborts immediately (no settle) — .csx parity
            for j in range(i, total):
                a_id, a_m, a_c, _ = cases[j]
                counts["aborted_memory"] += 1
                timing_rows.append({"test_id": a_id, "measure": a_m, "context": a_c,
                                    "status": "aborted_memory", "row_count": 0,
                                    "duration_ms": 0, "distinct_values": 0})
            break

        if measure in skipped:
            counts["skipped"] += 1
            timing_rows.append({"test_id": test_id, "measure": measure,
                                "context": context, "status": "skipped",
                                "row_count": 0, "duration_ms": 0, "distinct_values": 0})
            continue

        start = time.monotonic()
        res = execute_dax(conn_str, "EVALUATE " + dax, cfg.query_timeout_ms,
                          cfg.memory_threshold_pct)
        elapsed = int((time.monotonic() - start) * 1000)

        if res.status == "ok":
            counts["ok"] += 1
            rows = res.rows or []
            timing_rows.append({"test_id": test_id, "measure": measure,
                                "context": context, "status": "ok",
                                "row_count": len(rows), "duration_ms": elapsed,
                                "distinct_values": _distinct_values(res.columns or [], rows)})
        elif res.status == "timeout":
            counts["timeout"] += 1
            ttype = _timeout_type(res.error)
            error_log.append(f"{test_id} | {measure} | {context}\n"
                             f"  TIMEOUT ({ttype}) after {elapsed}ms "
                             f"(limit: {cfg.query_timeout_ms}ms)\n  DAX: {dax}\n\n")
            timeout_log.append(ser.timeout_log_entry(test_id, measure, context,
                                                     elapsed, ttype, res.error or "", dax))
            timing_rows.append({"test_id": test_id, "measure": measure,
                                "context": context, "status": "timeout",
                                "row_count": 0, "duration_ms": elapsed,
                                "distinct_values": 0})
        else:
            counts["error"] += 1
            error_log.append(ser.error_log_entry(test_id, measure, context, dax,
                                                 res.error or "unknown error"))
            timing_rows.append({"test_id": test_id, "measure": measure,
                                "context": context, "status": "error",
                                "row_count": 0, "duration_ms": elapsed,
                                "distinct_values": 0})

    total_ms = int((time.monotonic() - run_start) * 1000)

    ser.write_timing_csv(out_dir / f"{cfg.label}-timing.csv", timing_rows,
                         include_distinct=True)

    with open(out_dir / f"{cfg.label}-config.csv", "w", encoding="utf-8",
              newline="") as f:
        w = csv.writer(f)
        w.writerow(["type", "label", "column", "values"])
        for col, vals in cfg.global_filters.items():
            w.writerow(["global_filter", "", col, "; ".join(vals)])
        for label, col in cfg.single_slice_dimensions.items():
            w.writerow(["single_slice", label, col, ""])
        for col in cfg.cross_product_columns:
            vals = "; ".join(cfg.cross_product_value_filters.get(col, [])) or "(all)"
            w.writerow(["cross_product", "", col, vals])

    if counts["error"] > 0:
        (out_dir / f"{cfg.label}-errors.log").write_text("".join(error_log),
                                                         encoding="utf-8")
    if counts["timeout"] > 0 or skipped:
        (out_dir / f"{cfg.label}-timeouts.log").write_text("".join(timeout_log),
                                                           encoding="utf-8")

    xlsx_note = _write_benchmark_xlsx(out_dir / f"{cfg.label}-report.xlsx", cfg,
                                      counts, total, total_ms, timing_rows,
                                      skipped, smoke_results, memory_aborted)

    report = _benchmark_report(cfg, counts, total, total_ms, skipped, smoke_results,
                               timing_rows, memory_aborted, out_dir, xlsx_note)
    top5 = [f"{t['duration_ms']}ms - {t['measure']} [{t['context']}]"
            for t in sorted((t for t in timing_rows if t["status"] == "ok"),
                            key=lambda t: -t["duration_ms"])[:5]]
    is_clean = (counts["error"] == 0 and counts["timeout"] == 0
                and counts["skipped"] == 0 and counts["aborted_memory"] == 0)
    status = "All Passed" if is_clean else (
        f"{counts['error']} errors / {counts['timeout']} timeouts / "
        f"{counts['skipped']} skipped"
        + (f" / {counts['aborted_memory']} aborted" if counts["aborted_memory"] else ""))
    facts = [("Label", cfg.label), ("Status", status),
             ("Tests", f"{counts['ok']} OK / {counts['error']} errors / "
                       f"{counts['timeout']} timeouts / {total} total"),
             ("Measures", str(len(cfg.measures))),
             ("Duration", f"{total_ms / 60000:.1f} min")]
    extra = [{"type": "TextBlock", "text": "Top 5 Slowest (ok only)",
              "weight": "Bolder", "spacing": "Medium"},
             {"type": "TextBlock", "text": "\n".join(top5), "wrap": True,
              "fontType": "Monospace", "size": "Small"}] if top5 else None
    warn = _send_teams_card(cfg.teams_webhook_url,
                            _adaptive_card("PBI Measure Benchmark Complete", facts, extra))
    if warn:
        report += warn + "\n"
    report_path = (out_dir / f"{cfg.label}-report.xlsx").resolve()
    toast_warn = send_desktop_toast(
        f"PBI Benchmark — {_status_emoji(is_clean)} {status}",
        [f"Label: {cfg.label}",
         f"Tests: {counts['ok']} OK / {counts['error']} err / "
         f"{counts['timeout']} timeout / {total} total",
         f"Measures: {len(cfg.measures)}",
         f"Duration: {total_ms / 60000:.1f} min"],
        launch_uri=_report_launch_uri(report_path))
    if toast_warn:
        report += toast_warn + "\n"

    (out_dir / f"{cfg.label}-summary.txt").write_text(report, encoding="utf-8")
    print(report)
    return 0


def _benchmark_report(cfg, counts, total, total_ms, skipped, smoke_results,
                      timing_rows, memory_aborted, out_dir, xlsx_note="") -> str:
    bar = "═" * 60
    sub = "  " + "─" * 53
    lines = [bar, "  Measure Benchmark Complete", bar,
             f"  Label:      {cfg.label}",
             f"  Measures:   {len(cfg.measures)}",
             f"  Contexts:   1 grand_total + {len(cfg.single_slice_dimensions)} single-slice"
             + (" + 1 cross-product" if cfg.cross_product_columns else ""),
             f"  Test cases: {total}",
             f"  OK:         {counts['ok']}",
             f"  Errors:     {counts['error']}",
             f"  Timeouts:   {counts['timeout']}",
             f"  Skipped:    {counts['skipped']}"]
    if counts["aborted_memory"]:
        lines.append(f"  Aborted (memory): {counts['aborted_memory']}")
    lines += [f"  Duration:   {total_ms / 60000:.1f} minutes",
              f"  Timing CSV: {out_dir / (cfg.label + '-timing.csv')}"]
    if xlsx_note:
        lines.append(xlsx_note)
    lines.append(f"  Timeout:    {cfg.query_timeout_ms}ms per query "
                 "(ADOMD direct, mid-query memory watchdog)")
    if cfg.global_filters:
        lines.append(f"  Global filters: {len(cfg.global_filters)} applied (TREATAS)")
    if cfg.max_rows_per_context > 0:
        lines.append(f"  Row cap: TOPN({cfg.max_rows_per_context}) per context")
    if cfg.cross_product_columns:
        lines.append(f"  Cross-product columns: {len(cfg.cross_product_columns)}")
        if cfg.cross_product_value_filters:
            lines.append(f"  Cross-product value filters (TREATAS): "
                         f"{len(cfg.cross_product_value_filters)} columns filtered")
    if skipped:
        lines.append(f"  Smoke test: {len(skipped)} measure(s) skipped")
        lines += [f"    - {m}: {r}" for m, r in smoke_results.items()]
    if memory_aborted:
        lines.append(f"  ⚠ Run ABORTED due to memory threshold "
                     f"({cfg.memory_threshold_pct}%)")
    ok_rows = [t for t in timing_rows if t["status"] == "ok"]
    if ok_rows:
        lines += ["", "  Top 10 Slowest Queries (ok only):", sub]
        for t in sorted(ok_rows, key=lambda t: -t["duration_ms"])[:10]:
            lines.append(f"    {t['duration_ms']:>8}ms | {t['measure']} "
                         f"[{t['context']}] ({t['row_count']} rows, "
                         f"{t['distinct_values']} distinct)")
        lines.append(sub)
    timed_out = [t for t in timing_rows if t["status"] == "timeout"]
    if timed_out:
        lines += ["", f"  Timed Out Queries ({len(timed_out)}):", sub]
        for t in timed_out[:20]:
            lines.append(f"    {t['duration_ms']:>8}ms | {t['measure']} "
                         f"[{t['context']}]  ← TIMEOUT ({cfg.query_timeout_ms}ms limit)")
        if len(timed_out) > 20:
            lines.append(f"    ... and {len(timed_out) - 20} more "
                         f"(see {cfg.label}-timeouts.log)")
        lines.append(sub)
    flagged = _false_fast(timing_rows)
    if flagged:
        lines += ["", f"  False-Fast Warnings ({len(flagged)} test cases):",
                  "  distinct_values=1 with row_count>1 — dimension may not "
                  "filter this measure", sub]
        for t in flagged[:15]:
            lines.append(f"    {t['duration_ms']:>8}ms | {t['measure']} "
                         f"[{t['context']}] ({t['row_count']} rows, 1 distinct)")
        if len(flagged) > 15:
            lines.append(f"    ... and {len(flagged) - 15} more (see CSV)")
        lines.append(sub)
    lines.append(bar)
    return "\n".join(lines) + "\n"
