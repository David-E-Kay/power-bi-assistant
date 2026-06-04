"""Generate output/mc-baseline.csx from the template + session config.

Copies scripts/capture-snapshot.csx (template — never modified), then injects
the session-specific testLines and groupByColumns blocks produced by
gen-mc-testlines.py. Session config (snapshotLabel, thresholds, etc.) is
applied here via string replacements against the canonical template defaults.
Idempotent — rerunning overwrites output/mc-baseline.csx with the same content.
"""
from __future__ import annotations

import re
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
TEMPLATE = ROOT / "scripts" / "capture-snapshot.csx"
GEN = ROOT / "scripts" / "gen-mc-testlines.py"
OUT = ROOT / "output" / "mc-baseline.csx"


def run_generator() -> str:
    result = subprocess.run(
        [sys.executable, str(GEN)],
        capture_output=True,
        text=True,
        encoding="utf-8",
        env={"PYTHONIOENCODING": "utf-8", "PYTHONUTF8": "1", **__import__("os").environ},
    )
    if result.returncode != 0:
        sys.stderr.write(result.stderr)
        sys.exit(result.returncode)
    return result.stdout


def split_blocks(generated: str) -> tuple[str, str]:
    """Return (testLines_block, groupByColumns_block) as full var declarations."""
    # Match 'var testLines = new List<string> { ... };'
    test_re = re.compile(r"var testLines = new List<string>\s*\{.*?\};", re.DOTALL)
    dict_re = re.compile(r"var groupByColumns = new Dictionary<string, string>\s*\{.*?\};", re.DOTALL)
    tm = test_re.search(generated)
    dm = dict_re.search(generated)
    if not tm or not dm:
        raise SystemExit("Generator output missing expected blocks.")
    return tm.group(0), dm.group(0)


def replace_block(text: str, pattern: str, replacement: str, label: str) -> str:
    new_text, n = re.subn(pattern, lambda _m: replacement, text, count=1, flags=re.DOTALL)
    if n != 1:
        raise SystemExit(f"Failed to replace {label} block (matches={n}).")
    return new_text


def replace_once(text: str, old: str, new: str, label: str) -> str:
    """Single-occurrence string replace with assert. Catches template drift early."""
    if text.count(old) != 1:
        raise SystemExit(
            f"Expected exactly 1 occurrence of {label!r} in template; "
            f"found {text.count(old)}. Template may have changed."
        )
    return text.replace(old, new, 1)


def main() -> None:
    generated = run_generator()
    test_block, dict_block = split_blocks(generated)

    # Start from a clean copy of the template
    shutil.copy2(TEMPLATE, OUT)
    csx_text = OUT.read_text(encoding="utf-8")

    # ── Inject session-specific config ──────────────────────────────────────
    # Snapshot label: "refactor" (template default) → "mc-baseline"
    csx_text = replace_once(
        csx_text,
        '?? "refactor";',
        '?? "mc-baseline";',
        "snapshotLabel default",
    )
    # Model name: placeholder → "Maintenance and Construction"
    csx_text = replace_once(
        csx_text,
        '?? "<MODEL NAME — replaced per session by regression-testing skill>";',
        '?? "Maintenance and Construction";',
        "modelName default",
    )
    # diagnosticMode stays false (template default) — no replacement needed.
    # Global filter: enable Calendar[Year] = 2025
    csx_text = replace_once(
        csx_text,
        "    // \"'Calendar'[Start of Year] = DATE(2025, 1, 1)\",",
        "    \"'Calendar'[Year] = 2025\",",
        "globalFilters entry",
    )
    # Max rows per context: 0 (no limit) → 5
    csx_text = replace_once(
        csx_text,
        "var maxRowsPerContext = 0;   // 0 = no limit; e.g. 5 = cap at 5 rows per test",
        "var maxRowsPerContext = 5;   // 0 = no limit; e.g. 5 = cap at 5 rows per test",
        "maxRowsPerContext default",
    )
    # queryTimeoutMs stays at 60000 (template default) — no replacement needed.
    # Memory threshold: 80% (template default) → 95% — gives the M&C model
    # more headroom for large cross-product evaluations on this workstation.
    csx_text = replace_once(
        csx_text,
        ") : 80.0;",
        ") : 95.0;",
        "memoryThresholdPct default",
    )

    csx_text = replace_block(
        csx_text,
        r"var testLines = new List<string>\s*\{.*?\};",
        test_block,
        "testLines",
    )
    csx_text = replace_block(
        csx_text,
        r"var groupByColumns = new Dictionary<string, string>\s*\{.*?\};",
        dict_block,
        "groupByColumns",
    )

    OUT.write_text(csx_text, encoding="utf-8")
    print(f"Written to {OUT}")
    # Quick sanity check — sum should equal len(MEASURES) * len(CONTEXTS)
    test_count = csx_text.count('|grand_total",') + csx_text.count('|by_month",') \
                 + csx_text.count('|by_prop_toggle",') + csx_text.count('|by_market",') \
                 + csx_text.count('|by_scope",') + csx_text.count('|by_wo_status",') \
                 + csx_text.count('|by_vendor",') + csx_text.count('|by_repair_type",') \
                 + csx_text.count('|by_market_x_month",') \
                 + csx_text.count('|by_wo_status_x_month",')
    print(f"Total test-line entries detected in updated csx: {test_count}")


if __name__ == "__main__":
    main()
