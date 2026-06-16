import sys
import xml.dom.minidom

import pytest

from pbi_capture import notify


def test_toast_xml_well_formed_with_title_and_body():
    x = notify.build_toast_xml("Title", ["line a", "line b"])
    xml.dom.minidom.parseString(x)  # raises if not well-formed
    assert "<text>Title</text>" in x
    assert "<text>line a</text>" in x
    assert "<text>line b</text>" in x


def test_toast_xml_has_no_activation_without_launch_uri():
    x = notify.build_toast_xml("T", ["b"])
    assert "activationType" not in x
    assert "launch=" not in x


def test_toast_xml_has_protocol_activation_with_launch_uri():
    x = notify.build_toast_xml("T", ["b"], launch_uri="file:///C:/x.xlsx")
    assert 'activationType="protocol"' in x
    assert 'launch="file:///C:/x.xlsx"' in x


def test_toast_xml_escapes_special_characters():
    x = notify.build_toast_xml("a & b <c>", ["x > y"])
    assert "&amp;" in x
    assert "&lt;c&gt;" in x
    assert "&gt; y" in x
    xml.dom.minidom.parseString(x)  # still well-formed


@pytest.mark.skipif(sys.platform != "win32",
                    reason="file:// URI form is platform-specific")
def test_spaced_path_yields_percent_encoded_uri_in_xml():
    from pathlib import Path
    uri = Path(r"C:\temp\Power BI Assistant\r.xlsx").as_uri()
    assert "Power%20BI%20Assistant" in uri
    x = notify.build_toast_xml("T", ["b"], launch_uri=uri)
    assert "Power%20BI%20Assistant" in x


def test_send_desktop_toast_noop_off_windows(monkeypatch):
    monkeypatch.setattr(notify.sys, "platform", "linux")
    assert notify.send_desktop_toast("T", ["b"]) is None
