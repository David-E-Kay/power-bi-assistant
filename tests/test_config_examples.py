"""Validate every config JSON example in the docs against its schema.

Catches doc examples drifting from the contract. A "config example" is any
```json fenced block (in README.md or docs/config-schema.md) that parses to an
object with a "workflow" key; other JSON blocks are ignored. Requires
jsonschema (test-only dep).
"""

import json
import re
from pathlib import Path

import pytest

jsonschema = pytest.importorskip("jsonschema")

REPO_ROOT = Path(__file__).resolve().parents[1]
SCHEMAS_DIR = REPO_ROOT / "schemas"
DOC_SOURCES = [REPO_ROOT / "README.md", REPO_ROOT / "docs" / "config-schema.md"]

_FENCE_RE = re.compile(r"```json\s*\n(.*?)```", re.DOTALL)
_SCHEMA_FOR = {
    "capture": "capture-config.schema.json",
    "benchmark": "benchmark-config.schema.json",
}


def _load_schema(workflow):
    return json.loads((SCHEMAS_DIR / _SCHEMA_FOR[workflow]).read_text(encoding="utf-8"))


def _collect_examples():
    out = []
    for src in DOC_SOURCES:
        for i, block in enumerate(_FENCE_RE.findall(src.read_text(encoding="utf-8"))):
            try:
                data = json.loads(block)
            except json.JSONDecodeError:
                continue  # non-JSON or deliberately partial snippet — not a config
            if isinstance(data, dict) and "workflow" in data:
                out.append(pytest.param(data, id=f"{src.name}#{i}"))
    return out


EXAMPLES = _collect_examples()


def test_config_examples_were_found():
    # 2 in docs/config-schema.md + >=2 in README.md. Guards against the extractor
    # silently matching nothing and the parametrized test vacuously passing.
    assert len(EXAMPLES) >= 4


@pytest.mark.parametrize("config", EXAMPLES)
def test_doc_example_validates_against_schema(config):
    jsonschema.validate(config, _load_schema(config["workflow"]))
