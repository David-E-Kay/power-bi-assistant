import os

import pytest

pytestmark = pytest.mark.live
if os.environ.get("PBI_LIVE") != "1":
    pytest.skip("requires PBI Desktop open; set PBI_LIVE=1", allow_module_level=True)


def test_serialize_live_database_returns_bim_json():
    from pbi_capture.discovery import resolve_connection
    from pbi_capture.schema_export import serialize_live_database
    bim = serialize_live_database(resolve_connection())
    assert bim.lstrip().startswith("{")
    assert '"model"' in bim


def test_export_schema_markdown_end_to_end(tmp_path):
    from pbi_capture.discovery import resolve_connection
    from pbi_capture.schema_export import export_schema_markdown
    md = export_schema_markdown(
        resolve_connection(),
        bim_out=tmp_path / "live.bim",
        md_out=tmp_path / "live.md")
    text = md.read_text(encoding="utf-8")
    assert text.startswith("# Model Schema:")
    assert len(text) > 200          # real schema, not an empty shell
