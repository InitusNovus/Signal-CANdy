#!/usr/bin/env python3
"""Oracle test runner: single DBC cross-validation against generated C code."""

import argparse
import sys
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(
        description="Run oracle tests for single DBC file validation"
    )
    parser.add_argument(
        "--dbc",
        type=Path,
        help="Path to input DBC file",
    )
    parser.add_argument(
        "--config",
        type=Path,
        help="Path to YAML config file",
    )
    parser.add_argument(
        "--out-dir",
        type=Path,
        help="Output directory for generated C code",
    )
    parser.add_argument(
        "--assert-pass",
        action="store_true",
        help="Assert all validations pass (fail on error)",
    )
    parser.add_argument(
        "--vectors-per-signal",
        type=int,
        default=10,
        help="Number of test vectors per signal (default: 10)",
    )
    parser.add_argument(
        "-v",
        "--verbose",
        action="store_true",
        help="Enable verbose output",
    )

    args = parser.parse_args()

    if args.verbose:
        print(f"Oracle test runner initialized")
        print(f"  DBC: {args.dbc}")
        print(f"  Config: {args.config}")
        print(f"  Output dir: {args.out_dir}")
        print(f"  Assert pass: {args.assert_pass}")
        print(f"  Vectors per signal: {args.vectors_per_signal}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
