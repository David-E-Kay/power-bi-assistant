#!/usr/bin/env python3
"""TE-free regression snapshot capture.

Usage:
    python scripts/capture_snapshot.py --config output/baseline.config.json
    python scripts/capture_snapshot.py --config c.json --label refactored --port 51234

Config schema: docs/config-schema.md.
Env overrides (SNAPSHOT_LABEL, MODEL_NAME, OUTPUT_DIR, QUERY_TIMEOUT_MS, ...)
match the retired capture-snapshot.csx. Exit codes: 0 = run completed
(per-test errors are data), 2 = fatal (config/connection/CLR failure).
"""
import argparse
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

# Reconfigure stdout to UTF-8 on Windows (cp1252 default can't encode box-drawing chars).
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", line_buffering=True)

from pbi_capture.clr_boot import ClrBootError
from pbi_capture.config import (CaptureConfig, ConfigError, apply_env_overrides,
                                load_config, validate_capture)
from pbi_capture.discovery import DiscoveryError
from pbi_capture.runner import run_capture


def main() -> int:
    ap = argparse.ArgumentParser(description="TE-free regression snapshot capture")
    ap.add_argument("--config", required=True, help="path to capture config JSON")
    ap.add_argument("--label", help="override snapshot label")
    ap.add_argument("--port", type=int, help="msmdsrv port (skips auto-discovery)")
    ap.add_argument("--connection-string", help="full connection string override")
    ap.add_argument("--diagnostic", action="store_true",
                    help="diagnostic mode: first 8 tests, verbose per-test output")
    args = ap.parse_args()
    try:
        cfg = load_config(args.config)
        if not isinstance(cfg, CaptureConfig):
            raise ConfigError('this entry point requires "workflow": "capture"')
        apply_env_overrides(cfg, label_env="SNAPSHOT_LABEL")
        if args.label:
            cfg.label = args.label
        if args.port:
            cfg.connection.port = args.port
        if args.connection_string:
            cfg.connection.connection_string = args.connection_string
        if args.diagnostic:
            cfg.diagnostic_mode = True
        validate_capture(cfg)
        return run_capture(cfg)
    except (ConfigError, DiscoveryError, FileNotFoundError, json.JSONDecodeError,
            ClrBootError) as ex:
        print(f"FATAL: {ex}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
