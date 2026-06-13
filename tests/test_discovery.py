import pytest

from pbi_capture import discovery
from pbi_capture.discovery import DiscoveryError, Instance


def test_parse_port_bytes_utf16le_bom():
    data = "﻿51234".encode("utf-16-le")
    assert discovery.parse_port_bytes(data) == "51234"


def test_parse_port_bytes_plain_ascii_with_noise():
    assert discovery.parse_port_bytes(b"\r\n 60777 \r\n") == "60777"
    assert discovery.parse_port_bytes(b"") == ""


def test_scan_ports(tmp_path):
    ws = tmp_path / "AnalysisServicesWorkspace_abc" / "Data"
    ws.mkdir(parents=True)
    (ws / "msmdsrv.port.txt").write_bytes("﻿51234".encode("utf-16-le"))
    ws2 = tmp_path / "AnalysisServicesWorkspace_def"
    ws2.mkdir()
    (ws2 / "msmdsrv.port.txt").write_bytes(b"60777")  # port file at workspace root (older builds)
    found = discovery.scan_ports(roots=[tmp_path])
    assert sorted(p for p, _ in found) == ["51234", "60777"]


def test_resolve_prefers_connection_string(monkeypatch):
    assert discovery.resolve_connection("Data Source=x;Catalog=y;", 999) == \
        "Data Source=x;Catalog=y;"


def test_resolve_port_uses_first_catalog(monkeypatch):
    monkeypatch.setattr(discovery, "enumerate_catalogs", lambda p: ["guid-cat"])
    assert discovery.resolve_connection(None, 51234) == \
        "Provider=MSOLAP;Data Source=localhost:51234;Catalog=guid-cat;"


def test_resolve_auto_single_instance(monkeypatch):
    monkeypatch.setattr(discovery, "scan_ports", lambda roots=None: [("51234", "ws")])
    monkeypatch.setattr(discovery, "enumerate_catalogs", lambda p: ["guid-cat"])
    assert discovery.resolve_connection(None, None) == \
        "Provider=MSOLAP;Data Source=localhost:51234;Catalog=guid-cat;"


def test_resolve_auto_no_instances(monkeypatch):
    monkeypatch.setattr(discovery, "scan_ports", lambda roots=None: [])
    with pytest.raises(DiscoveryError, match="Open the .pbip"):
        discovery.resolve_connection(None, None)


def test_resolve_auto_multiple_instances(monkeypatch):
    monkeypatch.setattr(discovery, "scan_ports",
                        lambda roots=None: [("1", "w1"), ("2", "w2")])
    monkeypatch.setattr(discovery, "enumerate_catalogs", lambda p: [f"cat{p}"])
    with pytest.raises(DiscoveryError, match="--port"):
        discovery.resolve_connection(None, None)


def test_resolve_auto_dead_port_files(monkeypatch):
    """Stale port files that don't answer are skipped; one live instance wins."""
    monkeypatch.setattr(discovery, "scan_ports",
                        lambda roots=None: [("1", "w1"), ("2", "w2")])

    def cats(port):
        if port == "1":
            raise RuntimeError("connection refused")
        return ["cat2"]

    monkeypatch.setattr(discovery, "enumerate_catalogs", cats)
    assert discovery.resolve_connection(None, None) == \
        "Provider=MSOLAP;Data Source=localhost:2;Catalog=cat2;"
