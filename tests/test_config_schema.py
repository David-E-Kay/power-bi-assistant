"""Drift guard: the JSON Schemas in schemas/ must mirror the config dataclasses.

The schemas are a hand-written mirror of CaptureConfig / BenchmarkConfig in
scripts/pbi_capture/config.py. This test fails CI the moment someone adds (or
removes) a config field without updating the matching schema — the exact rot the
repo audit flagged in the prose docs. Pure stdlib: no jsonschema needed here.

The repo-root conftest puts ``scripts/`` on ``sys.path``.
"""

import dataclasses
import json
from pathlib import Path

import pytest

from pbi_capture.config import BenchmarkConfig, CaptureConfig

REPO_ROOT = Path(__file__).resolve().parents[1]
SCHEMAS_DIR = REPO_ROOT / "schemas"

CASES = [
    ("capture-config.schema.json", CaptureConfig, "capture"),
    ("benchmark-config.schema.json", BenchmarkConfig, "benchmark"),
]


def _schema(name):
    return json.loads((SCHEMAS_DIR / name).read_text(encoding="utf-8"))


@pytest.mark.parametrize("schema_file, dataclass_, workflow", CASES)
def test_schema_properties_match_dataclass_fields(schema_file, dataclass_, workflow):
    """schema.properties (minus the `workflow` discriminator) == dataclass fields."""
    schema_keys = set(_schema(schema_file)["properties"])
    field_names = {f.name for f in dataclasses.fields(dataclass_)}
    assert "workflow" in schema_keys, "schema must declare the workflow discriminator"
    assert schema_keys - {"workflow"} == field_names


@pytest.mark.parametrize("schema_file, dataclass_, workflow", CASES)
def test_schema_workflow_const_matches(schema_file, dataclass_, workflow):
    """The workflow property is pinned to the right const value."""
    assert _schema(schema_file)["properties"]["workflow"]["const"] == workflow


@pytest.mark.parametrize("schema_file, dataclass_, workflow", CASES)
def test_schema_rejects_unknown_keys(schema_file, dataclass_, workflow):
    """additionalProperties:false is the deliberate tightening the engine lacks."""
    assert _schema(schema_file)["additionalProperties"] is False


def test_schemas_are_valid_json_schema():
    """Each schema is itself a well-formed Draft 2020-12 schema."""
    jsonschema = pytest.importorskip("jsonschema")
    for name, _, _ in CASES:
        jsonschema.Draft202012Validator.check_schema(_schema(name))
