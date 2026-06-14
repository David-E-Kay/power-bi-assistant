"""Live model schema export via TOM JsonSerializer.SerializeDatabase.

Connects to the running msmdsrv instance, serializes the model to the same
.bim/TMSL JSON Tabular Editor would export, and feeds it into the existing
bim_to_kb_markdown parser (unchanged). See
docs/superpowers/plans/2026-06-14-tom-schema-export-spec.md.
"""
import re
from pathlib import Path

from .clr_boot import ensure_tom

_REPO_ROOT = Path(__file__).resolve().parents[2]


class SchemaExportError(RuntimeError):
    pass


def parse_catalog(conn_str: str) -> str:
    for part in conn_str.split(";"):
        key, _, value = part.partition("=")
        if key.strip().lower() == "catalog" and value.strip():
            return value.strip()
    raise SchemaExportError(f"no Catalog= in connection string: {conn_str!r}")


def model_slug(name: str) -> str:
    slug = re.sub(r"[^A-Za-z0-9]+", "-", name).strip("-").lower()
    if not slug:
        raise SchemaExportError(f"model name {name!r} slugifies to empty")
    return slug


def serialize_live_database(conn_str: str) -> str:
    """Connect via TOM and serialize the catalog's model to a .bim JSON string."""
    ensure_tom()
    from Microsoft.AnalysisServices.Tabular import JsonSerializer, Server
    catalog = parse_catalog(conn_str)
    server = Server()
    try:
        server.Connect(conn_str)
        db = server.Databases.GetByName(catalog)
        return JsonSerializer.SerializeDatabase(db)
    finally:
        try:
            server.Disconnect()
        except Exception:
            pass


def export_schema_markdown(conn_str: str, *, name: str | None = None,
                           bim_out=None, md_out=None,
                           serializer=serialize_live_database) -> Path:
    """Serialize the live model to .bim, then run the existing parser to markdown.

    `serializer` is injectable so the orchestration is unit-testable without CLR.
    """
    import bim_to_kb_markdown  # on sys.path via scripts/; conftest adds it for tests

    bim_json = serializer(conn_str)
    model_name = name or parse_catalog(conn_str)
    slug = model_slug(model_name)

    bim_out = Path(bim_out) if bim_out else _REPO_ROOT / "output" / f"{slug}.bim"
    md_out = Path(md_out) if md_out else (
        _REPO_ROOT / "artifacts" / "model-schema" / f"model-schema-{slug}.md")
    bim_out.parent.mkdir(parents=True, exist_ok=True)
    md_out.parent.mkdir(parents=True, exist_ok=True)

    bim_out.write_text(bim_json, encoding="utf-8")
    markdown = bim_to_kb_markdown.parse_bim_to_markdown(
        str(bim_out), model_name=model_name)
    md_out.write_text(markdown, encoding="utf-8")
    return md_out
