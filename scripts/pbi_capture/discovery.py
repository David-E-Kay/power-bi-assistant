"""msmdsrv instance discovery for local PBI Desktop (PBIP workspace mode).

Connection resolution precedence:
  1. explicit connection string (config / CONNECTION_STRING env / --connection-string)
  2. explicit port (--port / config) -> its single catalog
  3. auto-discovery -> scan workspace roots; exactly one answering instance
     required, else fail with an actionable listing.

Port files: msmdsrv.port.txt is UTF-16 LE with BOM; current builds keep it
under Data\\, older builds at the workspace root. Digit-extraction makes the
parse encoding-agnostic (port of the .csx v9 logic).
"""
import os
from dataclasses import dataclass
from pathlib import Path

from .clr_boot import ensure_adomd


class DiscoveryError(RuntimeError):
    pass


@dataclass
class Instance:
    port: str
    workspace_dir: str
    catalogs: list[str]


def workspace_roots() -> list[Path]:
    local = Path(os.environ.get("LOCALAPPDATA", ""))
    profile = Path(os.environ.get("USERPROFILE", ""))
    return [
        local / "Microsoft" / "Power BI Desktop" / "AnalysisServicesWorkspaces",
        local / "Packages" / "Microsoft.MicrosoftPowerBIDesktop_8wekyb3d8bbwe"
        / "LocalCache" / "Local" / "Microsoft" / "Power BI Desktop Store App"
        / "AnalysisServicesWorkspaces",
        profile / "Microsoft" / "Power BI Desktop Store App" / "AnalysisServicesWorkspaces",
    ]


def parse_port_bytes(data: bytes) -> str:
    return "".join(chr(b) for b in data if 0x30 <= b <= 0x39)


def scan_ports(roots=None) -> list[tuple[str, str]]:
    found = []
    for root in (roots or workspace_roots()):
        if not root.is_dir():
            continue
        for ws in root.glob("AnalysisServicesWorkspace*"):
            for pf in (ws / "Data" / "msmdsrv.port.txt", ws / "msmdsrv.port.txt"):
                if pf.is_file():
                    port = parse_port_bytes(pf.read_bytes())
                    if port:
                        found.append((port, str(ws)))
                    break
    return found


def enumerate_catalogs(port: str) -> list[str]:
    ensure_adomd()
    from Microsoft.AnalysisServices.AdomdClient import AdomdConnection
    conn = AdomdConnection(f"Provider=MSOLAP;Data Source=localhost:{port};")
    try:
        conn.Open()
        ds = conn.GetSchemaDataSet("DBSCHEMA_CATALOGS", None)
        return [str(row["CATALOG_NAME"]) for row in ds.Tables[0].Rows]
    finally:
        conn.Dispose()


def _conn_str(port: str, catalog: str) -> str:
    return f"Provider=MSOLAP;Data Source=localhost:{port};Catalog={catalog};"


def resolve_connection(connection_string: str | None = None,
                       port: int | str | None = None) -> str:
    if connection_string:
        return connection_string
    if port:
        catalogs = enumerate_catalogs(str(port))
        if not catalogs:
            raise DiscoveryError(f"No catalogs on localhost:{port} — is the model open?")
        return _conn_str(str(port), catalogs[0])

    candidates = scan_ports()
    if not candidates:
        raise DiscoveryError(
            "No running msmdsrv instances found. Open the .pbip/.pbix in Power BI "
            "Desktop first. Workspace roots checked: "
            + " | ".join(str(r) for r in workspace_roots()))

    live: list[Instance] = []
    probe_log = []
    for p, ws in candidates:
        try:
            cats = enumerate_catalogs(p)
        except Exception as ex:
            probe_log.append(f"localhost:{p} -> [probe failed: {ex}]")
            continue
        probe_log.append(f"localhost:{p} -> {', '.join(cats) or '(no catalogs)'}")
        if cats:
            live.append(Instance(port=p, workspace_dir=ws, catalogs=cats))

    if not live:
        raise DiscoveryError(
            "msmdsrv port files found but no instance answered. Probed: "
            + " | ".join(probe_log))
    if len(live) > 1:
        listing = "; ".join(f"localhost:{i.port} (catalog {i.catalogs[0]})" for i in live)
        raise DiscoveryError(
            "Multiple PBI Desktop instances are running — cannot auto-select. "
            f"Candidates: {listing}. Pass --port or set CONNECTION_STRING.")
    inst = live[0]
    return _conn_str(inst.port, inst.catalogs[0])
