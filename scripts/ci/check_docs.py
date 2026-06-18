"""Documentation-consistency checks. Cheap, deterministic, stdlib + jsonschema.

Run from the repo root:  python scripts/ci/check_docs.py
Exit 0 = all checks pass (warnings allowed); exit 1 = at least one failure.

Guards the "generated/regenerable", "Python 3.10+", and "docs mirror the code"
claims the repo makes, so they can't silently rot. Each check is conservative:
it would rather miss an exotic case than red-flag a healthy tree.
"""
import json
import re
import subprocess
import sys
from dataclasses import fields
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
SCHEMAS_DIR = REPO_ROOT / "schemas"

sys.path.insert(0, str(REPO_ROOT / "scripts"))
from pbi_capture.config import (  # noqa: E402
    _COMMON_KEYS, BenchmarkConfig, CaptureConfig)

# Top-level dirs whose concrete paths we trust enough to existence-check.
_TRACKED_DIRS = ("docs/", "scripts/", "knowledge/", "artifacts/", ".claude/",
                 "tests/", "schemas/", "output/")
_PATH_EXTS = (".md", ".py", ".json", ".yaml", ".yml", ".csx", ".txt")
_PLACEHOLDER_CHARS = set("{}<>*~")

_JSON_FENCE_RE = re.compile(r"```json\s*\n(.*?)```", re.DOTALL)
_LINK_RE = re.compile(r"\[[^\]]*\]\(([^)]+)\)")
_BACKTICK_RE = re.compile(r"`([^`]+)`")
# "Python 3.9", "python 3.8", etc. — single-digit minor (< 3.10). The negative
# lookahead lets "3.10".."3.14" through.
_OLD_PY_RE = re.compile(r"[Pp]ython\s*3\.[0-9](?![0-9])")
_ON_DEMAND_RE = re.compile(
    r"on demand|created on demand|none ship|empty by default|optional", re.I)


def _git(*args):
    return subprocess.run(["git", *args], cwd=REPO_ROOT, capture_output=True,
                          text=True, check=True).stdout


def _tracked_md():
    return [REPO_ROOT / p for p in _git("ls-files", "*.md").splitlines() if p]


def _read(path):
    return path.read_text(encoding="utf-8")


# ── 1. Python version consistency ─────────────────────────────────────────────

def check_python_version(errors, warnings):
    for md in _tracked_md():
        for n, line in enumerate(_read(md).splitlines(), 1):
            if _OLD_PY_RE.search(line):
                errors.append(
                    f"{md.relative_to(REPO_ROOT)}:{n}: references a Python < 3.10 "
                    f"version: {line.strip()!r}")


# ── 2. Broken relative links ──────────────────────────────────────────────────

def check_relative_links(errors, warnings):
    for md in _tracked_md():
        for target in _LINK_RE.findall(_read(md)):
            target = target.strip()
            # Skip URLs, mailto, pure anchors, and placeholder/glob templates.
            if (not target or target.startswith(("http://", "https://", "mailto:", "#"))
                    or "://" in target or any(c in _PLACEHOLDER_CHARS for c in target)):
                continue
            rel = target.split("#", 1)[0]          # drop any #anchor
            if not rel:
                continue
            if not (md.parent / rel).exists():
                errors.append(
                    f"{md.relative_to(REPO_ROOT)}: broken relative link -> {target!r}")


# ── 3. SKILL.md size (warn only) ──────────────────────────────────────────────

def check_skill_sizes(errors, warnings):
    tracked = [REPO_ROOT / p for p in _git("ls-files", ".claude/skills").splitlines()
               if p.endswith("SKILL.md")]
    for p in tracked:
        n = len(_read(p).splitlines())
        if n > 500:
            warnings.append(f"{p.relative_to(REPO_ROOT)}: {n} lines (> 500; consider trimming)")


# ── 4. knowledge-index paths exist ────────────────────────────────────────────

def check_knowledge_index_paths(errors, warnings):
    index = REPO_ROOT / "knowledge" / "knowledge-index.md"
    if not index.exists():
        errors.append("knowledge/knowledge-index.md is missing")
        return
    for n, line in enumerate(_read(index).splitlines(), 1):
        if _ON_DEMAND_RE.search(line):
            continue
        for tok in _BACKTICK_RE.findall(line):
            tok = tok.strip()
            if (any(c in _PLACEHOLDER_CHARS for c in tok)
                    or not tok.startswith(_TRACKED_DIRS)
                    or not tok.endswith(_PATH_EXTS)):
                continue
            if not (REPO_ROOT / tok).exists():
                errors.append(
                    f"knowledge-index.md:{n}: references missing path {tok!r}")


# ── 5. Documented config keys vs implementation ───────────────────────────────

def check_config_keys(errors, warnings):
    doc = REPO_ROOT / "docs" / "config-schema.md"
    documented = set()
    for line in _read(doc).splitlines():
        stripped = line.lstrip()
        if not stripped.startswith("|"):
            continue
        # Only the first table cell is the key column. Defaults like `true` /
        # `false` and notes like `msmdsrv` live in later cells — ignore them.
        cells = stripped.split("|")
        first_cell = cells[1] if len(cells) > 1 else ""
        for tok in _BACKTICK_RE.findall(first_cell):
            if re.fullmatch(r"[a-z][a-z0-9_]*", tok):
                documented.add(tok)
    impl = {"workflow"} | {f.name for f in fields(CaptureConfig)} \
        | {f.name for f in fields(BenchmarkConfig)}
    # _COMMON_KEYS is a subset of the dataclass fields; assert that invariant too
    # so a key dropped from one but not the other is caught.
    impl |= set(_COMMON_KEYS)
    missing = impl - documented
    extra = documented - impl
    if missing:
        errors.append(f"config-schema.md is missing documented keys: {sorted(missing)}")
    if extra:
        errors.append(
            f"config-schema.md documents keys absent from config.py: {sorted(extra)}")


# ── 6. Doc config examples validate against the schemas ───────────────────────

def check_config_examples(errors, warnings):
    import jsonschema
    schema_for = {
        "capture": json.loads(_read(SCHEMAS_DIR / "capture-config.schema.json")),
        "benchmark": json.loads(_read(SCHEMAS_DIR / "benchmark-config.schema.json")),
    }
    sources = [REPO_ROOT / "README.md", REPO_ROOT / "docs" / "config-schema.md"]
    found = 0
    for src in sources:
        for i, block in enumerate(_JSON_FENCE_RE.findall(_read(src))):
            try:
                data = json.loads(block)
            except json.JSONDecodeError:
                continue
            if not (isinstance(data, dict) and "workflow" in data):
                continue
            found += 1
            schema = schema_for.get(data["workflow"])
            if schema is None:
                errors.append(f"{src.name}#{i}: unknown workflow {data['workflow']!r}")
                continue
            try:
                jsonschema.validate(data, schema)
            except jsonschema.ValidationError as ex:
                errors.append(f"{src.name}#{i}: example fails schema: {ex.message}")
    if found < 4:
        errors.append(f"expected >=4 doc config examples, found {found}")


# ── 7. .gitignore policy for generated artifacts ──────────────────────────────

def check_gitignore_policy(errors, warnings):
    must_ignore = ["artifacts/model-schema/model-schema-sample.md", "output/sample.json"]
    res = subprocess.run(["git", "check-ignore", *must_ignore],
                         cwd=REPO_ROOT, capture_output=True, text=True)
    ignored = set(res.stdout.split())
    for p in must_ignore:
        if p not in ignored:
            errors.append(
                f".gitignore policy: {p!r} should be git-ignored (generated/regenerable) "
                "but is not")


CHECKS = [check_python_version, check_relative_links, check_skill_sizes,
          check_knowledge_index_paths, check_config_keys, check_config_examples,
          check_gitignore_policy]


def main():
    errors, warnings = [], []
    for check in CHECKS:
        check(errors, warnings)
    for w in warnings:
        print(f"WARN  {w}")
    for e in errors:
        print(f"FAIL  {e}")
    if errors:
        print(f"\n{len(errors)} failure(s), {len(warnings)} warning(s).")
        return 1
    print(f"OK    docs-check passed ({len(warnings)} warning(s)).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
