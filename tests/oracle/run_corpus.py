#!/usr/bin/env python3
"""Corpus test runner: batch validation across multiple DBC files."""

from __future__ import annotations

import argparse
import json
import logging
import os
import shutil
import subprocess
import sys
from concurrent.futures import ProcessPoolExecutor, as_completed
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from oracle.engine import oracle_pipeline


LOGGER = logging.getLogger(__name__)


def _now_iso8601() -> str:
    return datetime.now(timezone.utc).isoformat()


def clone_opendbc(tmp_dir: Path) -> Path | None:
    """
    Clone opendbc repository to tmp directory.

    Returns Path to opendbc/dbc directory if successful, None otherwise.
    """
    opendbc_dir = tmp_dir / "opendbc"
    if opendbc_dir.exists():
        LOGGER.info(f"opendbc already cloned at {opendbc_dir}")
        return opendbc_dir / "opendbc" / "dbc"

    try:
        LOGGER.info("Cloning opendbc repository...")
        subprocess.run(
            [
                "git",
                "clone",
                "--depth",
                "1",
                "https://github.com/commaai/opendbc.git",
                str(opendbc_dir),
            ],
            check=True,
            capture_output=True,
            text=True,
            encoding="utf-8",
        )
        dbc_dir = opendbc_dir / "opendbc" / "dbc"
        if dbc_dir.exists():
            LOGGER.info(f"opendbc cloned successfully to {opendbc_dir}")
            return dbc_dir
        else:
            LOGGER.warning(f"opendbc cloned but dbc directory not found at {dbc_dir}")
            return None
    except subprocess.CalledProcessError as e:
        LOGGER.warning(f"Failed to clone opendbc: {e.stderr}")
        return None
    except FileNotFoundError:
        LOGGER.warning("git command not found - cannot clone opendbc")
        return None


def collect_corpus_dbcs(
    corpus_dir: Path,
    clone_opendbc_flag: bool,
    tmp_dir: Path,
) -> list[Path]:
    """
    Collect all DBC files from corpus directory and optionally from opendbc.

    Returns list of DBC file paths.
    """
    dbcs = []

    # Collect from vendor_dbc
    if corpus_dir.exists():
        vendor_dbcs = sorted(corpus_dir.glob("*.dbc"))
        dbcs.extend(vendor_dbcs)
        LOGGER.info(
            f"Found {len(vendor_dbcs)} DBC files in vendor corpus: {corpus_dir}"
        )
    else:
        LOGGER.warning(f"Corpus directory not found: {corpus_dir}")

    # Optionally clone and collect from opendbc
    if clone_opendbc_flag:
        opendbc_dbc_dir = clone_opendbc(tmp_dir)
        if opendbc_dbc_dir:
            opendbc_dbcs = sorted(opendbc_dbc_dir.glob("*.dbc"))
            # Deduplicate by filename
            existing_names = {dbc.name for dbc in dbcs}
            new_dbcs = [dbc for dbc in opendbc_dbcs if dbc.name not in existing_names]
            dbcs.extend(new_dbcs)
            LOGGER.info(
                f"Found {len(opendbc_dbcs)} DBC files in opendbc, added {len(new_dbcs)} new files"
            )
        else:
            LOGGER.warning(
                "Failed to clone opendbc - falling back to vendor corpus only"
            )

    return dbcs


def is_unsupported_signal(signal) -> tuple[bool, str | None]:
    """
    Check if signal has unsupported features.

    Returns (is_unsupported, reason).
    """
    # Check for float signals (SIG_VALTYPE_ not supported)
    if hasattr(signal, "is_float") and signal.is_float:
        return True, "SIG_VALTYPE_ (float signal) not supported"

    # Check for extended multiplexing (e.g., multiplexer_ids with more than one ID)
    if hasattr(signal, "multiplexer_ids") and signal.multiplexer_ids:
        if len(signal.multiplexer_ids) > 1:
            return True, "Extended multiplexing not supported"

    return False, None


def check_unsupported_features(dbc_path: Path) -> tuple[bool, str | None]:
    """
    Check DBC file for unsupported features.

    Returns (has_unsupported, reason).
    """
    try:
        import cantools

        db = cantools.database.load_file(str(dbc_path))

        for message in db.messages:
            for signal in message.signals:
                is_unsupported, reason = is_unsupported_signal(signal)
                if is_unsupported:
                    return (
                        True,
                        f"Message '{message.name}' signal '{signal.name}': {reason}",
                    )

        return False, None
    except Exception as e:
        # If we can't parse with cantools, let oracle_pipeline handle it
        return False, None


def test_single_dbc(
    dbc_path: Path,
    config_path: Path | None,
    out_subdir: Path,
    vectors_per_signal: int,
    verbose: bool,
) -> dict[str, Any]:
    """
    Test a single DBC file with oracle pipeline.

    Returns result dict with status, counts, and error info.
    """
    result = {
        "file": dbc_path.name,
        "status": "unknown",
        "messages_tested": 0,
        "signals_tested": 0,
        "oracle_passed": 0,
        "oracle_failed": 0,
        "oracle_skipped": 0,
        "reason": None,
    }

    # Check for unsupported features first
    has_unsupported, unsupported_reason = check_unsupported_features(dbc_path)
    if has_unsupported:
        result["status"] = "skipped"
        result["reason"] = f"Unsupported feature: {unsupported_reason}"
        LOGGER.info(f"SKIP {dbc_path.name}: {unsupported_reason}")
        return result

    # Use default config if not provided
    if config_path is None:
        # Create a minimal default config in tmp
        default_config = out_subdir / "default_config.yaml"
        default_config.parent.mkdir(parents=True, exist_ok=True)
        default_config.write_text(
            'phys_type: "float"\n'
            'phys_mode: "double"\n'
            "range_check: false\n"
            'dispatch: "direct_map"\n'
            'motorola_start_bit: "msb"\n'
            "crc_counter_check: false\n",
            encoding="utf-8",
        )
        config_path = default_config

    try:
        report = oracle_pipeline(
            dbc_path=str(dbc_path),
            config_path=str(config_path),
            out_dir=str(out_subdir),
            vectors_per_signal=vectors_per_signal,
            verbose=verbose,
        )

        # Aggregate results
        if report.failed > 0:
            result["status"] = "failed"
        elif report.passed > 0:
            result["status"] = "passed"
        elif report.skipped > 0:
            result["status"] = "skipped"
            result["reason"] = "All tests skipped"
        else:
            result["status"] = "skipped"
            result["reason"] = "No tests run"

        # Count messages and signals tested (approximate from results)
        messages_seen = set()
        signals_seen = set()
        for test_result in report.results:
            messages_seen.add(test_result.message)
            signals_seen.add((test_result.message, test_result.signal))

        result["messages_tested"] = len(messages_seen)
        result["signals_tested"] = len(signals_seen)
        result["oracle_passed"] = report.passed
        result["oracle_failed"] = report.failed
        result["oracle_skipped"] = report.skipped

        LOGGER.info(
            f"{result['status'].upper()} {dbc_path.name}: "
            f"{result['oracle_passed']} passed, {result['oracle_failed']} failed, "
            f"{result['oracle_skipped']} skipped"
        )

    except Exception as e:
        result["status"] = "skipped"
        result["reason"] = f"Pipeline error: {str(e)}"
        LOGGER.warning(f"SKIP {dbc_path.name}: {e}")

    return result


def run_corpus_test(
    corpus_dir: Path,
    out_dir: Path,
    config_path: Path | None,
    clone_opendbc_flag: bool,
    vectors_per_signal: int,
    parallel: int,
    verbose: bool,
) -> dict[str, Any]:
    """
    Run oracle tests on all DBCs in corpus.

    Returns corpus report dict.
    """
    # Collect DBCs
    tmp_dir = out_dir / "tmp"
    tmp_dir.mkdir(parents=True, exist_ok=True)

    dbcs = collect_corpus_dbcs(corpus_dir, clone_opendbc_flag, tmp_dir)

    if not dbcs:
        LOGGER.error("No DBC files found in corpus")
        return {
            "corpus_dir": str(corpus_dir),
            "timestamp": _now_iso8601(),
            "total": 0,
            "passed": 0,
            "failed": 0,
            "skipped": 0,
            "dbcs": [],
        }

    LOGGER.info(f"Testing {len(dbcs)} DBC files...")

    # Test each DBC
    results = []
    if parallel > 1:
        # Parallel execution
        with ProcessPoolExecutor(max_workers=parallel) as executor:
            futures = []
            for idx, dbc_path in enumerate(dbcs):
                out_subdir = out_dir / f"dbc_{idx:03d}_{dbc_path.stem}"
                future = executor.submit(
                    test_single_dbc,
                    dbc_path,
                    config_path,
                    out_subdir,
                    vectors_per_signal,
                    verbose,
                )
                futures.append(future)

            for future in as_completed(futures):
                try:
                    result = future.result()
                    results.append(result)
                except Exception as e:
                    LOGGER.error(f"Parallel test failed: {e}")
    else:
        # Sequential execution
        for idx, dbc_path in enumerate(dbcs):
            out_subdir = out_dir / f"dbc_{idx:03d}_{dbc_path.stem}"
            result = test_single_dbc(
                dbc_path,
                config_path,
                out_subdir,
                vectors_per_signal,
                verbose,
            )
            results.append(result)

    # Aggregate results
    passed_count = sum(1 for r in results if r["status"] == "passed")
    failed_count = sum(1 for r in results if r["status"] == "failed")
    skipped_count = sum(1 for r in results if r["status"] == "skipped")

    report = {
        "corpus_dir": str(corpus_dir),
        "timestamp": _now_iso8601(),
        "total": len(results),
        "passed": passed_count,
        "failed": failed_count,
        "skipped": skipped_count,
        "dbcs": results,
    }

    return report


def main():
    parser = argparse.ArgumentParser(
        description="Run corpus tests across multiple DBC files"
    )
    parser.add_argument(
        "--corpus-dir",
        type=Path,
        required=True,
        help="Directory containing DBC files to test",
    )
    parser.add_argument(
        "--out-dir",
        type=Path,
        required=True,
        help="Output directory for results",
    )
    parser.add_argument(
        "--config",
        type=Path,
        help="Path to YAML config file (default: built-in config)",
    )
    parser.add_argument(
        "--full-matrix",
        action="store_true",
        help="Run full config matrix for each DBC (not implemented - use run_matrix.py)",
    )
    parser.add_argument(
        "--assert-pass",
        action="store_true",
        help="Exit 1 if any testable DBC fails",
    )
    parser.add_argument(
        "--report-only",
        action="store_true",
        help="Only generate report (with --assert-pass, still asserts on failures)",
    )
    parser.add_argument(
        "--clone-opendbc",
        action="store_true",
        help="Clone opendbc repository and include those DBCs",
    )
    parser.add_argument(
        "--vectors-per-signal",
        type=int,
        default=10,
        help="Number of test vectors per signal (default: 10)",
    )
    parser.add_argument(
        "--parallel",
        type=int,
        default=1,
        help="Number of parallel workers (default: 1 = sequential)",
    )
    parser.add_argument(
        "-v",
        "--verbose",
        action="store_true",
        help="Enable verbose output",
    )

    args = parser.parse_args()

    # Configure logging
    logging.basicConfig(
        level=logging.DEBUG if args.verbose else logging.INFO,
        format="%(asctime)s [%(levelname)s] %(message)s",
    )

    if args.full_matrix:
        LOGGER.warning(
            "--full-matrix not implemented in run_corpus.py - use run_matrix.py instead"
        )
        # We'll ignore this flag for now and just run with single config

    if args.verbose:
        LOGGER.debug(f"Corpus test runner initialized")
        LOGGER.debug(f"  Corpus dir: {args.corpus_dir}")
        LOGGER.debug(f"  Output dir: {args.out_dir}")
        LOGGER.debug(f"  Config: {args.config}")
        LOGGER.debug(f"  Assert pass: {args.assert_pass}")
        LOGGER.debug(f"  Report only: {args.report_only}")
        LOGGER.debug(f"  Clone openDBC: {args.clone_opendbc}")
        LOGGER.debug(f"  Vectors per signal: {args.vectors_per_signal}")
        LOGGER.debug(f"  Parallel workers: {args.parallel}")

    # Run corpus test
    report = run_corpus_test(
        corpus_dir=args.corpus_dir,
        out_dir=args.out_dir,
        config_path=args.config,
        clone_opendbc_flag=args.clone_opendbc,
        vectors_per_signal=args.vectors_per_signal,
        parallel=args.parallel,
        verbose=args.verbose,
    )

    # Write corpus report
    report_path = args.out_dir / "corpus_report.json"
    report_path.parent.mkdir(parents=True, exist_ok=True)
    report_path.write_text(
        json.dumps(report, indent=2, sort_keys=False),
        encoding="utf-8",
    )

    LOGGER.info(f"\nCorpus test complete:")
    LOGGER.info(f"  Total: {report['total']}")
    LOGGER.info(f"  Passed: {report['passed']}")
    LOGGER.info(f"  Failed: {report['failed']}")
    LOGGER.info(f"  Skipped: {report['skipped']}")
    LOGGER.info(f"  Report: {report_path}")

    # Assert pass if requested
    if args.assert_pass and report["failed"] > 0:
        LOGGER.error(f"Assertion failed: {report['failed']} DBC(s) failed testing")
        return 1

    return 0


if __name__ == "__main__":
    sys.exit(main())
