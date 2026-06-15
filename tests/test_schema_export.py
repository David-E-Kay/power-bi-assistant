import json

import pytest

from pbi_capture import schema_export
from pbi_capture.schema_export import SchemaExportError


def test_parse_catalog_extracts_value():
    cs = "Provider=MSOLAP;Data Source=localhost:51234;Catalog=abc-guid;"
    assert schema_export.parse_catalog(cs) == "abc-guid"


def test_parse_catalog_case_insensitive_key():
    assert schema_export.parse_catalog("data source=x;CATALOG=My Model;") == "My Model"


def test_parse_catalog_missing_raises():
    with pytest.raises(SchemaExportError):
        schema_export.parse_catalog("Provider=MSOLAP;Data Source=localhost:51234;")


def test_parse_catalog_value_with_equals():
    # partition (not split("=")) keeps everything after the first '=' in the value.
    assert schema_export.parse_catalog("Catalog=YWJj=ZGVm;") == "YWJj=ZGVm"


def test_parse_catalog_empty_value_raises():
    # An empty Catalog= value must fall through to the raise, not return "".
    with pytest.raises(SchemaExportError):
        schema_export.parse_catalog("Data Source=x;Catalog=;")


def test_model_slug_normalizes():
    assert schema_export.model_slug("Sales & Margin (2026)") == "sales-margin-2026"


def test_model_slug_empty_raises():
    with pytest.raises(SchemaExportError):
        schema_export.model_slug("!!!")


_FIXTURE_BIM = {
    "name": "Sales",
    "compatibilityLevel": 1567,
    "model": {
        "name": "Sales",
        "tables": [
            {
                "name": "Date",
                "columns": [{"name": "Year", "dataType": "int64"}],
                "measures": [{"name": "Total Sales", "expression": "SUM(Date[Year])"}],
            }
        ],
        "relationships": [],
    },
}


def test_export_schema_markdown_reuses_parser(tmp_path):
    cs = "Data Source=localhost:51234;Catalog=Sales;"
    bim_out = tmp_path / "sales.bim"
    md_out = tmp_path / "schema.md"

    def fake_serializer(_conn):
        return json.dumps(_FIXTURE_BIM)

    result = schema_export.export_schema_markdown(
        cs, name="Sales", bim_out=bim_out, md_out=md_out,
        serializer=fake_serializer)

    assert result == md_out
    assert bim_out.is_file()                       # .bim artifact retained
    text = md_out.read_text(encoding="utf-8")
    assert "Model Schema: Sales" in text           # parser output, real code path
    assert "Total Sales" in text                   # measure carried through


def test_export_schema_markdown_default_paths(tmp_path, monkeypatch):
    # With no bim_out/md_out, paths derive from _REPO_ROOT + model slug.
    monkeypatch.setattr(schema_export, "_REPO_ROOT", tmp_path)

    md = schema_export.export_schema_markdown(
        "Data Source=x;Catalog=Sales;", name="Sales & Margin (2026)",
        serializer=lambda _conn: json.dumps(_FIXTURE_BIM))

    assert md == tmp_path / "artifacts" / "model-schema" / "model-schema-sales-margin-2026.md"
    assert (tmp_path / "output" / "sales-margin-2026.bim").is_file()
    assert "Model Schema: Sales & Margin (2026)" in md.read_text(encoding="utf-8")
