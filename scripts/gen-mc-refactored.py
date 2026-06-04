"""Generate output/mc-refactored.csx from the template + mc-baseline-timing.csv.

Sources test cases from the ok rows of mc-baseline-timing.csv rather than from
the hardcoded MEASURES list in gen-mc-testlines.py, so the new script covers
exactly the measures that ran successfully in the baseline — excluding the ~90
manually pre-excluded measures and the 2 that timed out (t0020, t0040).

Original test IDs are preserved for traceability against the baseline snapshot.
"""
from __future__ import annotations

import csv
import re
import shutil
import sys
from pathlib import Path

ROOT         = Path(__file__).resolve().parent.parent
TEMPLATE     = ROOT / "scripts" / "capture-snapshot.csx"
OUT          = ROOT / "output" / "mc-refactored.csx"
TIMING_CSV   = Path(r"C:\Users\dkay\Desktop\PBI-Regression\mc-baseline-timing.csv")

SNAPSHOT_LABEL = "mc-refactored"
MODEL_NAME     = "Maintenance and Construction"

GROUP_BY_COLUMNS = {
    "by_month":             "'Calendar'[Start of Month]",
    "by_prop_toggle":       "'Proportionate Ownership Toggle'[Proportionate Values]",
    "by_market":            "'Properties'[Property Market Reporting]",
    "by_scope":             "'Projects'[Project Scope Type Desc]",
    "by_wo_status":         "'Work Orders'[Work Order Status Desc]",
    "by_vendor":            "'Vendors'[Vendor Name]",
    "by_repair_type":       "'Repair Type'[Repair Type]",
    "by_market_x_month":    "'Properties'[Property Market Reporting]|'Calendar'[Start of Month]",
    "by_wo_status_x_month": "'Work Orders'[Work Order Status Desc]|'Calendar'[Start of Month]",
}


def replace_once(text: str, old: str, new: str, label: str) -> str:
    if text.count(old) != 1:
        raise SystemExit(
            f"Expected exactly 1 occurrence of {label!r} in template; "
            f"found {text.count(old)}. Template may have drifted."
        )
    return text.replace(old, new, 1)


def replace_block(text: str, pattern: str, replacement: str, label: str) -> str:
    new_text, n = re.subn(pattern, lambda _m: replacement, text, count=1, flags=re.DOTALL)
    if n != 1:
        raise SystemExit(f"Failed to replace {label} block (matches={n}).")
    return new_text


def read_ok_tests(timing_csv: Path) -> list[tuple[str, str, str]]:
    """Return [(test_id, measure, context), ...] for all ok rows, in file order."""
    rows = []
    skipped = []
    with timing_csv.open(newline="", encoding="utf-8-sig") as f:
        for row in csv.DictReader(f):
            if row["status"] == "ok":
                rows.append((row["test_id"], row["measure"], row["context"]))
            else:
                skipped.append(row["test_id"])
    return rows, skipped


def build_test_lines_block(ok_tests: list[tuple[str, str, str]]) -> str:
    lines = []
    for test_id, measure, context in ok_tests:
        escaped = measure.replace("\\", "\\\\").replace('"', '\\"')
        lines.append(f'    "{test_id}|{escaped}|{context}",')
    inner = "\n".join(lines)
    return f"var testLines = new List<string>\n{{\n{inner}\n}};"


def build_group_by_block() -> str:
    lines = []
    for ctx, col in GROUP_BY_COLUMNS.items():
        lines.append(f'    {{ "{ctx}", "{col}" }},')
    inner = "\n".join(lines)
    return f"var groupByColumns = new Dictionary<string, string>\n{{\n{inner}\n}};"


def main() -> None:
    if not TIMING_CSV.exists():
        raise SystemExit(f"Timing CSV not found: {TIMING_CSV}")
    if not TEMPLATE.exists():
        raise SystemExit(f"Template not found: {TEMPLATE}")

    ok_tests, skipped = read_ok_tests(TIMING_CSV)
    print(f"Timing CSV: {len(ok_tests)} ok, {len(skipped)} excluded/timeout")
    if skipped:
        print(f"  Excluded test IDs: {', '.join(skipped)}")

    test_lines_block   = build_test_lines_block(ok_tests)
    group_by_block     = build_group_by_block()

    shutil.copy2(TEMPLATE, OUT)
    csx = OUT.read_text(encoding="utf-8")

    csx = replace_once(csx, '?? "refactor";',                  f'?? "{SNAPSHOT_LABEL}";',  "snapshotLabel default")
    csx = replace_once(csx, '?? "<MODEL NAME — replaced per session by regression-testing skill>";',
                            f'?? "{MODEL_NAME}";',              "modelName default")
    csx = replace_once(csx, "    // \"'Calendar'[Start of Year] = DATE(2025, 1, 1)\",",
                            "    \"'Calendar'[Year] = 2025\",", "globalFilters entry")
    csx = replace_once(csx, "var maxRowsPerContext = 0;   // 0 = no limit; e.g. 5 = cap at 5 rows per test",
                            "var maxRowsPerContext = 5;   // 0 = no limit; e.g. 5 = cap at 5 rows per test",
                            "maxRowsPerContext default")
    csx = replace_once(csx, ") : 80.0;", ") : 95.0;",          "memoryThresholdPct default")

    csx = replace_block(csx, r"var testLines = new List<string>\s*\{.*?\};",
                        test_lines_block, "testLines")
    csx = replace_block(csx, r"var groupByColumns = new Dictionary<string, string>\s*\{.*?\};",
                        group_by_block,   "groupByColumns")

    OUT.write_text(csx, encoding="utf-8")

    # Sanity check
    context_keys = list(GROUP_BY_COLUMNS.keys()) + ["grand_total"]
    detected = sum(csx.count(f"|{k}\",") for k in context_keys)
    print(f"Written: {OUT.relative_to(ROOT)} — {detected} test-line entries")
    if detected != len(ok_tests):
        print(f"  WARNING: expected {len(ok_tests)}, detected {detected}. Check output.", file=sys.stderr)


if __name__ == "__main__":
    main()
