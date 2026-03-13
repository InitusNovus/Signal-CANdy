from __future__ import annotations

from pathlib import Path

import cantools
import pytest


DBC_PATH = Path(__file__).resolve().parents[3] / "examples" / "multiplex_suite.dbc"
MESSAGE_NAME = "MUX_MSG"


def _load_mux_message() -> cantools.database.can.message.Message:
    db = cantools.database.load_file(str(DBC_PATH))
    message = db.get_message_by_name(MESSAGE_NAME)
    assert message is not None, f"Expected message {MESSAGE_NAME}, got None"
    return message


def _is_multiplexed_truthy(message: cantools.database.can.message.Message) -> bool:
    value = getattr(message, "is_multiplexed", None)
    if callable(value):
        return bool(value())
    return bool(value)


@pytest.mark.integration
def test_a1_message_is_multiplexed_truthy() -> None:
    message = _load_mux_message()
    actual = _is_multiplexed_truthy(message)
    assert actual is True, f"A1 failed: expected True, actual={actual!r}"


@pytest.mark.integration
def test_a2_exactly_one_multiplexer_signal_muxswitch() -> None:
    message = _load_mux_message()
    multiplexer_signals = [
        s.name for s in message.signals if bool(getattr(s, "is_multiplexer", False))
    ]
    assert len(multiplexer_signals) == 1, (
        f"A2 failed: expected exactly one multiplexer signal, actual={multiplexer_signals!r}"
    )
    assert multiplexer_signals[0] == "MuxSwitch", (
        f"A2 failed: expected multiplexer signal 'MuxSwitch', actual={multiplexer_signals[0]!r}"
    )


@pytest.mark.integration
def test_a3_multiplexer_ids_layout_matches_expectation() -> None:
    message = _load_mux_message()
    by_name = {signal.name: signal for signal in message.signals}
    expected = {
        "MuxSwitch": None,
        "Base_8": None,
        "Sig_m1": [1],
        "Sig_m2": [2],
    }

    for signal_name, expected_ids in expected.items():
        signal = by_name[signal_name]
        actual_ids = getattr(signal, "multiplexer_ids", None)
        if expected_ids is None:
            assert actual_ids in (None, []), (
                f"A3 failed for {signal_name}: expected None/[], actual={actual_ids!r}"
            )
        else:
            assert list(actual_ids or []) == expected_ids, (
                f"A3 failed for {signal_name}: expected={expected_ids!r}, actual={actual_ids!r}"
            )


@pytest.mark.integration
def test_a4_decode_returns_only_active_branch_keys_for_mux_1() -> None:
    message = _load_mux_message()
    payload = bytes([0x01, 0x0A, 0x2A, 0x00, 0x00, 0x00, 0x00, 0x00])
    decoded = message.decode(payload, decode_choices=False)
    keys = set(decoded.keys())
    expected_present = {"MuxSwitch", "Base_8", "Sig_m1"}
    expected_absent = "Sig_m2"

    assert expected_present.issubset(keys), (
        f"A4 failed: expected keys {sorted(expected_present)!r} present, actual keys={sorted(keys)!r}"
    )
    assert expected_absent not in keys, (
        f"A4 failed: expected {expected_absent!r} absent, actual keys={sorted(keys)!r}"
    )


@pytest.mark.integration
def test_a5_encode_accepts_float_values_with_strict_false() -> None:
    message = _load_mux_message()
    signals = {"MuxSwitch": 1.0, "Sig_m1": 42.0, "Base_8": 10.0}
    encoded = message.encode(signals, strict=False)
    assert encoded is not None, (
        f"A5 failed: expected encoded payload, actual={encoded!r}"
    )
    assert len(bytes(encoded)) == message.length, (
        f"A5 failed: expected payload length={message.length}, actual length={len(bytes(encoded))}"
    )


@pytest.mark.integration
def test_a6_normal_mux_signals_have_single_multiplexer_id() -> None:
    message = _load_mux_message()
    by_name = {signal.name: signal for signal in message.signals}

    for signal_name in ("Sig_m1", "Sig_m2"):
        mux_ids = getattr(by_name[signal_name], "multiplexer_ids", None)
        actual_len = len(mux_ids or [])
        assert actual_len == 1, (
            f"A6 failed for {signal_name}: expected len(multiplexer_ids)==1, actual {mux_ids!r}"
        )


@pytest.mark.integration
def test_a7_encode_decode_roundtrip_branch_1_values() -> None:
    message = _load_mux_message()
    signals = {"MuxSwitch": 1, "Sig_m1": 42, "Base_8": 10}
    encoded = bytes(message.encode(signals, strict=False))
    decoded = message.decode(encoded, decode_choices=False)

    expected = {"MuxSwitch": 1, "Sig_m1": 42, "Base_8": 10}
    for key, expected_value in expected.items():
        actual_value = decoded.get(key)
        assert actual_value == expected_value, (
            f"A7 failed for {key}: expected={expected_value!r}, actual={actual_value!r}"
        )
