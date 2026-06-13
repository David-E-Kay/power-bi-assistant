import os

import pytest

pytestmark = pytest.mark.live
if os.environ.get("PBI_LIVE") != "1":
    pytest.skip("requires PBI Desktop open; set PBI_LIVE=1", allow_module_level=True)


def test_auto_resolution_against_open_desktop():
    from pbi_capture.discovery import resolve_connection
    conn_str = resolve_connection()
    assert "Data Source=localhost:" in conn_str
    assert "Catalog=" in conn_str
