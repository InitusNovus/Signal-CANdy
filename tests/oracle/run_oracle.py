#!/usr/bin/env python3
"""Oracle test runner CLI - single DBC + single config."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import sys
import traceback

sys.path.insert(0, str(Path(__file__).parent))

from oracle.engine import oracle_pipeline


def main() -> None:
    parser = argparse.ArgumentParser(description="Run oracle tests on a DBC file")
    parser.add_argument("--dbc", required=True, help="Input DBC file path")
    parser.add_argument(
        "--config",
        default="examples/config.yaml",
        help="YAML config file",
    )
    parser.add_argument(
        "--out-dir",
        required=True,
        help="Output directory for artifacts and reports",
    )
    parser.add_argument(
        "--assert-pass",
        action="store_true",
        help="Exit 1 if any test fails",
    )
    parser.add_argument(
        "--vectors-per-signal",
        type=int,
        default=10,
        help="Test vectors per signal",
    )
    parser.add_argument(
        "-v",
        "--verbose",
        action="store_true",
        help="Print per-signal results",
    )

    args = parser.parse_args()

    try:
        report = oracle_pipeline(
            args.dbc,
            args.config,
            args.out_dir,
            args.vectors_per_signal,
            args.verbose,
        )

        report_path = Path(args.out_dir) / "report.json"
        report_path.parent.mkdir(parents=True, exist_ok=True)

        with report_path.open("w", encoding="utf-8") as report_file:
            json.dump(
                {
                    "dbc_path": report.dbc_path,
                    "config_path": report.config_path,
                    "timestamp": report.timestamp,
                    "passed": report.passed,
                    "failed": report.failed,
                    "skipped": report.skipped,
                    "results": [
                        {
                            "message": result.message,
                            "signal": result.signal,
                            "test_type": result.test_type,
                            "passed": result.passed,
                            "error": result.error,
                            "cantools_value": result.cantools_value,
                            "c_value": result.c_value,
                            "tolerance": result.tolerance,
                            "skipped": result.skipped,
                        }
                        for result in report.results
                    ],
                },
                report_file,
                indent=2,
            )

        print(
            f"Oracle test complete: {report.passed} passed, {report.failed} failed, {report.skipped} skipped"
        )
        print(f"Report saved to: {report_path}")

        if args.verbose:
            for result in report.results:
                status = (
                    "SKIP" if result.skipped else ("PASS" if result.passed else "FAIL")
                )
                print(f"{status} {result.message}.{result.signal} ({result.test_type})")

        if args.assert_pass and report.failed > 0:
            sys.exit(1)

        sys.exit(0)

    except Exception as ex:
        print(f"ERROR: {ex}", file=sys.stderr)
        traceback.print_exc()
        sys.exit(1)


if __name__ == "__main__":
    main()
