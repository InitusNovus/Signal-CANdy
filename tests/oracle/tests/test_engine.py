from __future__ import annotations

from pathlib import Path
import subprocess

import pytest

from ..oracle.engine import (
    extract_message_info,
    generate_test_vectors,
    load_dbc_cantools,
    oracle_pipeline,
)


def test_load_dbc_cantools_reads_sample_file(sample_dbc_path: Path) -> None:
    db = load_dbc_cantools(str(sample_dbc_path))
    names = {message.name for message in db.messages}
    assert "MESSAGE_1" in names


@pytest.mark.integration
@pytest.mark.slow
def test_oracle_pipeline_sample_dbc(
    sample_dbc_path: Path, default_config_path: Path, tmp_path: Path
) -> None:
    out_dir = tmp_path / "sample_out"
    report = oracle_pipeline(
        dbc_path=str(sample_dbc_path),
        config_path=str(default_config_path),
        out_dir=str(out_dir),
        vectors_per_signal=2,
        verbose=False,
    )
    assert report.failed == 0
    assert report.passed > 0
    assert (out_dir / "report.json").exists()


@pytest.mark.integration
@pytest.mark.slow
def test_oracle_pipeline_comprehensive_dbc(
    comprehensive_dbc_path: Path, default_config_path: Path, tmp_path: Path
) -> None:
    out_dir = tmp_path / "comprehensive_out"
    report = oracle_pipeline(
        dbc_path=str(comprehensive_dbc_path),
        config_path=str(default_config_path),
        out_dir=str(out_dir),
        vectors_per_signal=1,
        verbose=False,
    )
    assert report.failed == 0
    assert report.passed > 0
    assert (out_dir / "report.json").exists()


@pytest.mark.integration
def test_oracle_report_structure_has_required_fields(
    sample_dbc_path: Path, default_config_path: Path, tmp_path: Path
) -> None:
    report = oracle_pipeline(
        dbc_path=str(sample_dbc_path),
        config_path=str(default_config_path),
        out_dir=str(tmp_path / "report_fields"),
        vectors_per_signal=1,
        verbose=False,
    )
    payload = report.to_dict()
    required = {
        "dbc_path",
        "config_path",
        "timestamp",
        "passed",
        "failed",
        "skipped",
        "results",
    }
    assert required.issubset(payload.keys())
    assert isinstance(payload["results"], list)


@pytest.mark.integration
@pytest.mark.slow
def test_oracle_pipeline_multiplex_dbc(
    multiplex_dbc_path: Path, default_config_path: Path, tmp_path: Path
) -> None:
    out_dir = tmp_path / "multiplex_out"
    report = oracle_pipeline(
        dbc_path=str(multiplex_dbc_path),
        config_path=str(default_config_path),
        out_dir=str(out_dir),
        vectors_per_signal=2,
        verbose=False,
    )
    skipped = [
        result
        for result in report.results
        if result.skipped and "single-config" in (result.error or "")
    ]
    assert report.passed > 0
    assert report.failed == 0
    assert len(skipped) == 0


@pytest.mark.integration
@pytest.mark.slow
def test_generate_vectors_mux_branches(
    multiplex_dbc_path: Path, tmp_path: Path
) -> None:
    out_dir = tmp_path / "mux_vectors_out"
    generator_path = Path(__file__).resolve().parents[3] / "src" / "Generator"
    result = subprocess.run(
        [
            "dotnet",
            "run",
            "--project",
            str(generator_path),
            "--",
            "--dbc",
            str(multiplex_dbc_path),
            "--out",
            str(out_dir),
        ],
        check=False,
        capture_output=True,
        text=True,
    )
    assert result.returncode == 0

    db = load_dbc_cantools(str(multiplex_dbc_path))
    messages = extract_message_info(str(out_dir))
    vectors, _ = generate_test_vectors(db, messages, vectors_per_signal=2)

    mux_vectors = [vector for vector in vectors if vector.message == "MUX_MSG"]
    branch_1_vectors = [
        vector
        for vector in mux_vectors
        if vector.signal == "Sig_m1" and vector.signals.get("MuxSwitch") == 1.0
    ]
    branch_2_vectors = [
        vector
        for vector in mux_vectors
        if vector.signal == "Sig_m2" and vector.signals.get("MuxSwitch") == 2.0
    ]

    assert mux_vectors
    assert branch_1_vectors
    assert branch_2_vectors


@pytest.mark.integration
def test_sample_dbc_has_no_mux_skips(
    sample_dbc_path: Path, default_config_path: Path, tmp_path: Path
) -> None:
    report = oracle_pipeline(
        dbc_path=str(sample_dbc_path),
        config_path=str(default_config_path),
        out_dir=str(tmp_path / "sample_no_mux_skips"),
        vectors_per_signal=1,
        verbose=False,
    )
    skipped = [
        result
        for result in report.results
        if result.skipped and "multiplex" in (result.error or "").lower()
    ]
    assert len(skipped) == 0


@pytest.mark.integration
def test_mux_switch_not_target(multiplex_dbc_path: Path, tmp_path: Path) -> None:
    out_dir = tmp_path / "mux_switch_out"
    generator_path = Path(__file__).resolve().parents[3] / "src" / "Generator"
    result = subprocess.run(
        [
            "dotnet",
            "run",
            "--project",
            str(generator_path),
            "--",
            "--dbc",
            str(multiplex_dbc_path),
            "--out",
            str(out_dir),
        ],
        check=False,
        capture_output=True,
        text=True,
    )
    assert result.returncode == 0

    db = load_dbc_cantools(str(multiplex_dbc_path))
    messages = extract_message_info(str(out_dir))
    vectors, _ = generate_test_vectors(db, messages, vectors_per_signal=2)

    mux_targets = {vector.signal for vector in vectors if vector.message == "MUX_MSG"}
    assert mux_targets
    assert "MuxSwitch" not in mux_targets
