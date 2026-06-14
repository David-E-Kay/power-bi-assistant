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
