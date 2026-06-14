#!/usr/bin/env python3
"""TE-free measure benchmark sweep (timing only — no result values).

Usage:
    python scripts/benchmark_measures.py --config output/sweep.config.json

Config schema: docs/superpowers/plans/2026-06-10-te-free-capture-spec.md.
Env overrides (BENCHMARK_LABEL, OUTPUT_DIR, QUERY_TIMEOUT_MS, ...) match the
retired benchmark-measures.csx. Exit codes: 0 = run completed, 2 = fatal.
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
from pbi_capture.config import (BenchmarkConfig, ConfigError, apply_env_overrides,
                                load_config, validate_benchmark)
from pbi_capture.discovery import DiscoveryError
from pbi_capture.runner import run_benchmark


def main() -> int:
    ap = argparse.ArgumentParser(description="TE-free measure benchmark sweep")
    ap.add_argument("--config", required=True, help="path to benchmark config JSON")
    ap.add_argument("--label", help="override benchmark label")
    ap.add_argument("--port", type=int, help="msmdsrv port (skips auto-discovery)")
    ap.add_argument("--connection-string", help="full connection string override")
    ap.add_argument("--diagnostic", action="store_true",
                    help="diagnostic mode: first 8 tests only")
    args = ap.parse_args()
    try:
        cfg = load_config(args.config)
        if not isinstance(cfg, BenchmarkConfig):
            raise ConfigError('this entry point requires "workflow": "benchmark"')
        apply_env_overrides(cfg, label_env="BENCHMARK_LABEL")
        if args.label:
            cfg.label = args.label
        if args.port:
            cfg.connection.port = args.port
        if args.connection_string:
            cfg.connection.connection_string = args.connection_string
        if args.diagnostic:
            cfg.diagnostic_mode = True
        validate_benchmark(cfg)
        return run_benchmark(cfg)
    except (ConfigError, DiscoveryError, FileNotFoundError, json.JSONDecodeError,
            ClrBootError) as ex:
        print(f"FATAL: {ex}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
