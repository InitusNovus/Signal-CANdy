from __future__ import annotations

from ..oracle.metadata_compare import (
    compare_all,
    compare_signal_metadata,
    extract_candy_metadata,
    extract_cantools_metadata,
)


def test_extract_cantools_metadata_has_known_signal_fields(
    cantools_module, sample_dbc_path
) -> None:
    db = cantools_module.database.load_file(str(sample_dbc_path))
    metadata = extract_cantools_metadata(db)
    signal = metadata["MESSAGE_1"]["Signal_2"]
    assert signal["start_bit"] == 8
    assert signal["length"] == 16
    assert signal["byte_order"] == "little_endian"
    assert signal["factor"] == 0.1
    assert signal["offset"] == 0


def test_extract_candy_metadata_matches_cantools_for_same_db(
    cantools_module, sample_dbc_path
) -> None:
    db = cantools_module.database.load_file(str(sample_dbc_path))
    cantools_meta = extract_cantools_metadata(db)
    candy_meta = extract_candy_metadata(db)
    assert candy_meta == cantools_meta


def test_compare_signal_metadata_detects_divergence() -> None:
    cantools_signal = {
        "start_bit": 8,
        "length": 16,
        "byte_order": "little_endian",
        "is_signed": False,
        "factor": 0.1,
        "offset": 0.0,
    }
    candy_signal = {
        "start_bit": 8,
        "length": 16,
        "byte_order": "big_endian",
        "is_signed": False,
        "factor": 0.1,
        "offset": 0.0,
    }
    divergences = compare_signal_metadata(cantools_signal, candy_signal)
    assert len(divergences) == 1
    assert "byte_order" in divergences[0]


def test_compare_all_reports_diverged_and_missing_signals(
    cantools_module, sample_dbc_path
) -> None:
    db = cantools_module.database.load_file(str(sample_dbc_path))
    cantools_meta = extract_cantools_metadata(db)
    candy_meta = extract_candy_metadata(db)
    candy_meta["MESSAGE_1"]["Signal_1"]["byte_order"] = "big_endian"
    del candy_meta["MESSAGE_1"]["Signal_2"]

    report = compare_all(cantools_meta, candy_meta)
    assert report.total_signals == 2
    assert report.matched == 0
    assert report.diverged == 2
    fields = {entry["field"] for entry in report.divergences}
    assert "byte_order" in fields
    assert "<missing>" in fields
