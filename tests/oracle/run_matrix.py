#!/usr/bin/env python3
"""Matrix test runner: exhaustive sweep of config permutations against DBC file."""

from __future__ import annotations

import argparse
import json
import logging
import sys
from concurrent.futures import ProcessPoolExecutor, as_completed
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from oracle.engine import oracle_pipeline


LOGGER = logging.getLogger(__name__)


def generate_config_matrix() -> list[dict[str, Any]]:
    """
    Generate all 8 valid config combinations.

    Matrix dimensions:
    - phys_type: float, fixed (2 values)
    - phys_mode: double/float (for float), fixed_double/fixed_float (for fixed) (2 values per phys_type)
    - range_check: true, false (2 values)

    Fixed values (not matrix dimensions):
    - motorola_start_bit: msb (ALWAYS - per Metis directive)
    - dispatch: direct_map (ALWAYS - not a test dimension)
    - crc_counter_check: false (ALWAYS - feature not implemented)

    Returns 8 config dicts.
    """
    configs = []

    # Combinations 1-4: phys_type=float
    for phys_mode in ["double", "float"]:
        for range_check in [False, True]:
            configs.append(
                {
                    "phys_type": "float",
                    "phys_mode": phys_mode,
                    "range_check": range_check,
                    "motorola_start_bit": "msb",
                    "dispatch": "direct_map",
                    "crc_counter_check": False,
                }
            )

    # Combinations 5-8: phys_type=fixed
    for phys_mode in ["fixed_double", "fixed_float"]:
        for range_check in [False, True]:
            configs.append(
                {
                    "phys_type": "fixed",
                    "phys_mode": phys_mode,
                    "range_check": range_check,
                    "motorola_start_bit": "msb",
                    "dispatch": "direct_map",
                    "crc_counter_check": False,
                }
            )

    return configs


def write_config_yaml(config_dict: dict[str, Any], output_path: Path) -> None:
    """
    Write config dict to YAML file.

    Format matches examples/config.yaml structure.
    """
    output_path.parent.mkdir(parents=True, exist_ok=True)

    lines = [
        "# Auto-generated config for matrix test",
        "",
        f'phys_type: "{config_dict["phys_type"]}"',
        f'phys_mode: "{config_dict["phys_mode"]}"',
        f"range_check: {str(config_dict['range_check']).lower()}",
        f'dispatch: "{config_dict["dispatch"]}"',
        f'motorola_start_bit: "{config_dict["motorola_start_bit"]}"',
        f"crc_counter_check: {str(config_dict['crc_counter_check']).lower()}",
        "",
    ]

    output_path.write_text("\n".join(lines), encoding="utf-8")


def test_single_config(
    dbc_path: str,
    config_dict: dict[str, Any],
    config_yaml_path: Path,
    out_subdir: Path,
    config_index: int,
    vectors_per_signal: int,
    verbose: bool,
) -> dict[str, Any]:
    """
    Test a single config: write YAML, run oracle_pipeline, return results.

    Returns dict with:
    - config: the config dict
    - passed: number of passed tests
    - failed: number of failed tests
    - skipped: number of skipped tests
    - negative_tests_passed: number of negative tests passed (for range_check=true)
    - report_path: path to the generated report.json
    """
    try:
        # Write YAML config
        write_config_yaml(config_dict, config_yaml_path)

        if verbose:
            LOGGER.info(
                f"[Config {config_index}] Testing: phys_type={config_dict['phys_type']}, "
                f"phys_mode={config_dict['phys_mode']}, range_check={config_dict['range_check']}"
            )

        # Run oracle pipeline
        report = oracle_pipeline(
            dbc_path=dbc_path,
            config_path=str(config_yaml_path),
            out_dir=str(out_subdir),
            vectors_per_signal=vectors_per_signal,
            verbose=verbose,
        )

        # Write report to JSON
        report_path = out_subdir / "report.json"
        report.write_json(report_path)

        # TODO: Add negative tests for range_check=true configs
        # For now, just report the basic oracle results
        negative_tests_passed = 0

        if verbose:
            LOGGER.info(
                f"[Config {config_index}] Results: {report.passed} passed, "
                f"{report.failed} failed, {report.skipped} skipped"
            )

        return {
            "config": config_dict,
            "passed": report.passed,
            "failed": report.failed,
            "skipped": report.skipped,
            "negative_tests_passed": negative_tests_passed,
            "report_path": str(report_path),
        }

    except Exception as e:
        LOGGER.error(
            f"[Config {config_index}] Failed with exception: {e}", exc_info=True
        )
        return {
            "config": config_dict,
            "passed": 0,
            "failed": 0,
            "skipped": 0,
            "negative_tests_passed": 0,
            "report_path": "",
            "error": str(e),
        }


def run_matrix_test(
    dbc_path: str,
    out_dir: str,
    parallel: int,
    vectors_per_signal: int = 10,
    verbose: bool = False,
) -> dict[str, Any]:
    """
    Run matrix test: generate all 8 configs, test each, collect results.

    Args:
        dbc_path: Path to DBC file
        out_dir: Output directory for all results
        parallel: Number of parallel workers (1 for sequential)
        vectors_per_signal: Number of test vectors per signal (default 10)
        verbose: Enable verbose logging

    Returns:
        dict with:
        - dbc_path: input DBC path
        - timestamp: ISO8601 timestamp
        - configs_tested: number of configs tested (should be 8)
        - configs: list of per-config results
        - summary: overall summary {total_passed, total_failed, total_skipped, total_negative_tests_passed}
    """
    out_path = Path(out_dir)
    out_path.mkdir(parents=True, exist_ok=True)

    configs = generate_config_matrix()

    if verbose:
        LOGGER.info(f"Generated {len(configs)} config combinations")
        LOGGER.info(f"Parallel workers: {parallel}")

    results = []

    if parallel == 1:
        # Sequential execution
        for i, config in enumerate(configs):
            config_yaml_path = out_path / f"config_{i}.yaml"
            config_subdir = out_path / f"config_{i}"

            result = test_single_config(
                dbc_path=dbc_path,
                config_dict=config,
                config_yaml_path=config_yaml_path,
                out_subdir=config_subdir,
                config_index=i,
                vectors_per_signal=vectors_per_signal,
                verbose=verbose,
            )
            results.append(result)
    else:
        # Parallel execution
        with ProcessPoolExecutor(max_workers=parallel) as executor:
            futures = {}
            for i, config in enumerate(configs):
                config_yaml_path = out_path / f"config_{i}.yaml"
                config_subdir = out_path / f"config_{i}"

                future = executor.submit(
                    test_single_config,
                    dbc_path,
                    config,
                    config_yaml_path,
                    config_subdir,
                    i,
                    vectors_per_signal,
                    verbose,
                )
                futures[future] = i

            for future in as_completed(futures):
                result = future.result()
                results.append(result)

    # Sort results by config index to maintain order
    # (parallel execution may complete out of order)
    results.sort(key=lambda r: configs.index(r["config"]))

    # Compute summary
    summary = {
        "total_passed": sum(r["passed"] for r in results),
        "total_failed": sum(r["failed"] for r in results),
        "total_skipped": sum(r["skipped"] for r in results),
        "total_negative_tests_passed": sum(r["negative_tests_passed"] for r in results),
    }

    return {
        "dbc_path": dbc_path,
        "timestamp": datetime.now(timezone.utc).isoformat(),
        "configs_tested": len(configs),
        "configs": results,
        "summary": summary,
    }


def main():
    parser = argparse.ArgumentParser(
        description="Run matrix tests across config permutations"
    )
    parser.add_argument(
        "--dbc",
        type=Path,
        required=True,
        help="Path to input DBC file",
    )
    parser.add_argument(
        "--out-dir",
        type=Path,
        required=True,
        help="Output directory for generated results",
    )
    parser.add_argument(
        "--parallel",
        type=int,
        default=1,
        help="Number of parallel workers (default: 1)",
    )
    parser.add_argument(
        "-v",
        "--verbose",
        action="store_true",
        help="Enable verbose output",
    )
    parser.add_argument(
        "--vectors-per-signal",
        type=int,
        default=10,
        help="Number of test vectors per signal (default: 10)",
    )

    args = parser.parse_args()

    # Configure logging
    logging.basicConfig(
        level=logging.INFO if args.verbose else logging.WARNING,
        format="%(asctime)s [%(levelname)s] %(message)s",
    )

    if args.verbose:
        print(f"Matrix test runner initialized")
        print(f"  DBC: {args.dbc}")
        print(f"  Output dir: {args.out_dir}")
        print(f"  Parallel workers: {args.parallel}")
        print(f"  Vectors per signal: {args.vectors_per_signal}")

    # Run matrix test
    result = run_matrix_test(
        dbc_path=str(args.dbc),
        out_dir=str(args.out_dir),
        parallel=args.parallel,
        vectors_per_signal=args.vectors_per_signal,
        verbose=args.verbose,
    )

    # Write matrix report
    report_path = args.out_dir / "matrix_report.json"
    report_path.write_text(
        json.dumps(result, indent=2, sort_keys=False),
        encoding="utf-8",
    )

    # Print summary
    summary = result["summary"]
    print(
        f"Matrix test: {result['configs_tested']} configs, "
        f"{summary['total_passed']} passed, "
        f"{summary['total_failed']} failed, "
        f"{summary['total_skipped']} skipped"
    )

    if args.verbose:
        print(f"Full report written to: {report_path}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
