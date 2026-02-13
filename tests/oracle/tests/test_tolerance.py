from __future__ import annotations

from ..oracle.tolerance import (
    FLT_EPSILON,
    compare_bytes,
    compare_physical,
    compute_tolerance,
)


def test_compute_tolerance_integer_signal_is_zero() -> None:
    value = compute_tolerance(
        factor=1.0,
        offset=0.0,
        expected_phys=123.0,
        bit_length=16,
        is_signed=False,
    )
    assert value == 0.0


def test_compute_tolerance_small_factor_uses_lsb_floor() -> None:
    value = compute_tolerance(
        factor=0.01,
        offset=0.0,
        expected_phys=10.0,
        bit_length=16,
        is_signed=False,
    )
    assert value == 0.005


def test_compute_tolerance_large_expected_value_uses_float_component() -> None:
    expected = 2_000_000.0
    value = compute_tolerance(
        factor=0.000001,
        offset=0.0,
        expected_phys=expected,
        bit_length=32,
        is_signed=False,
    )
    float_component = abs(expected) * FLT_EPSILON * 8
    assert value == float_component


def test_compare_physical_exact_match() -> None:
    assert compare_physical(12.5, 12.5, 0.0)


def test_compare_physical_within_tolerance() -> None:
    assert compare_physical(100.0, 100.004, 0.005)


def test_compare_physical_outside_tolerance() -> None:
    assert not compare_physical(100.0, 100.01, 0.005)


def test_compare_bytes_exact_match() -> None:
    matched, differences = compare_bytes(b"\x10\x20\x30", b"\x10\x20\x30")
    assert matched
    assert differences == []


def test_compare_bytes_single_lsb_difference_is_allowed() -> None:
    matched, differences = compare_bytes(b"\x10\x20\x30", b"\x10\x21\x30")
    assert matched
    assert differences == []


def test_compare_bytes_large_difference_fails() -> None:
    matched, differences = compare_bytes(b"\x10\x20\x30", b"\x10\x24\x30")
    assert not matched
    assert len(differences) == 1
    assert "Byte[1]" in differences[0]


def test_compare_bytes_length_mismatch_fails() -> None:
    matched, differences = compare_bytes(b"\x10\x20", b"\x10\x20\x30")
    assert not matched
    assert "Length mismatch" in differences[0]
