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
