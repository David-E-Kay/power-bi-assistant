"""CLI: export a live PBI Desktop model's schema to markdown via TOM — no
Tabular Editor required. See the README ("Export a live model's schema").

Usage:
  python scripts/export_schema.py [--connection-string S | --port N]
                                  [--name NAME] [--bim-out PATH] [--md-out PATH]
"""
import argparse
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))  # make pbi_capture / bim_to_kb importable

from pbi_capture import discovery, schema_export  # noqa: E402


def main(argv=None) -> int:
    p = argparse.ArgumentParser(description="Export live model schema markdown via TOM")
    p.add_argument("--connection-string", default=None)
    p.add_argument("--port", type=int, default=None)
    p.add_argument("--name", default=None, help="override model display name")
    p.add_argument("--bim-out", default=None)
    p.add_argument("--md-out", default=None)
    args = p.parse_args(argv)

    try:
        conn_str = discovery.resolve_connection(
            connection_string=args.connection_string, port=args.port)
        md_path = schema_export.export_schema_markdown(
            conn_str, name=args.name, bim_out=args.bim_out, md_out=args.md_out,
            serializer=schema_export.serialize_live_database)
    except (discovery.DiscoveryError, schema_export.SchemaExportError,
            FileNotFoundError) as ex:
        print(f"FATAL: {ex}", file=sys.stderr)
        return 2

    print(f"Schema markdown written to: {md_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
