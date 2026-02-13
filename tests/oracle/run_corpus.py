#!/usr/bin/env python3
"""Corpus test runner: batch validation across multiple DBC files."""

import argparse
import sys
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(
        description="Run corpus tests across multiple DBC files"
    )
    parser.add_argument(
        "--corpus-dir",
        type=Path,
        help="Directory containing DBC files to test",
    )
    parser.add_argument(
        "--out-dir",
        type=Path,
        help="Output directory for results",
    )
    parser.add_argument(
        "--config",
        type=Path,
        help="Path to YAML config file",
    )
    parser.add_argument(
        "--full-matrix",
        action="store_true",
        help="Run full config matrix for each DBC (slower, comprehensive)",
    )
    parser.add_argument(
        "--assert-pass",
        action="store_true",
        help="Assert all validations pass (fail on error)",
    )
    parser.add_argument(
        "--report-only",
        action="store_true",
        help="Skip execution; only generate report from existing results",
    )
    parser.add_argument(
        "--clone-opendbc",
        action="store_true",
        help="Clone public DBC files from openDBC repository",
    )
    parser.add_argument(
        "-v",
        "--verbose",
        action="store_true",
        help="Enable verbose output",
    )

    args = parser.parse_args()

    if args.verbose:
        print(f"Corpus test runner initialized")
        print(f"  Corpus dir: {args.corpus_dir}")
        print(f"  Output dir: {args.out_dir}")
        print(f"  Config: {args.config}")
        print(f"  Full matrix: {args.full_matrix}")
        print(f"  Assert pass: {args.assert_pass}")
        print(f"  Report only: {args.report_only}")
        print(f"  Clone openDBC: {args.clone_opendbc}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
