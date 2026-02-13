from __future__ import annotations

from pathlib import Path

import pytest

from ..oracle.engine import load_dbc_cantools, oracle_pipeline


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
