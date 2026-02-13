from __future__ import annotations

from ..oracle.vector_gen import (
    generate_adversarial_vectors,
    generate_vectors_for_message,
    generate_vectors_for_signal,
)


def test_generate_vectors_for_unsigned_signal_contains_boundaries(
    cantools_module, sample_dbc_path
) -> None:
    db = cantools_module.database.load_file(str(sample_dbc_path))
    signal = db.get_message_by_name("MESSAGE_1").get_signal_by_name("Signal_1")
    vectors = generate_vectors_for_signal(signal, count=5)
    values = [value for value, _ in vectors]
    assert signal.minimum in values
    assert signal.maximum in values
    assert 0.0 in values


def test_generate_vectors_for_signed_signal_contains_negative_boundaries(
    cantools_module, comprehensive_dbc_path
) -> None:
    db = cantools_module.database.load_file(str(comprehensive_dbc_path))
    signal = db.get_message_by_name("MSG_COMP_SIGNED").get_signal_by_name("S_LE_8")
    vectors = generate_vectors_for_signal(signal, count=3)
    values = [value for value, _ in vectors]
    assert signal.minimum in values
    assert -1.0 in values


def test_generate_vectors_for_big_endian_signal_respects_range(
    cantools_module, comprehensive_dbc_path
) -> None:
    db = cantools_module.database.load_file(str(comprehensive_dbc_path))
    signal = db.get_message_by_name("MSG_COMP_BE").get_signal_by_name("BE_16")
    vectors = generate_vectors_for_signal(signal, count=12)
    assert all(signal.minimum <= value <= signal.maximum for value, _ in vectors)


def test_generate_vectors_for_signal_random_values_within_bounds(
    cantools_module, comprehensive_dbc_path
) -> None:
    db = cantools_module.database.load_file(str(comprehensive_dbc_path))
    signal = db.get_message_by_name("MSG_COMP_SCALE").get_signal_by_name("SC_NEG_OFF")
    vectors = generate_vectors_for_signal(signal, count=20)
    random_values = [value for value, tags in vectors if "random" in tags]
    assert len(random_values) == 20
    assert all(signal.minimum <= value <= signal.maximum for value in random_values)


def test_generate_adversarial_vectors_include_rounding_boundaries(
    cantools_module, sample_dbc_path
) -> None:
    db = cantools_module.database.load_file(str(sample_dbc_path))
    signal = db.get_message_by_name("MESSAGE_1").get_signal_by_name("Signal_2")
    vectors = generate_adversarial_vectors(signal)
    rounding = [(value, tags) for value, tags in vectors if "rounding_boundary" in tags]
    assert rounding
    assert len(rounding) <= 10
    assert all("adversarial" in tags for _, tags in rounding)


def test_generate_vectors_for_message_multiplexed_has_per_branch_vectors(
    cantools_module, multiplex_dbc_path
) -> None:
    db = cantools_module.database.load_file(str(multiplex_dbc_path))
    message = db.get_message_by_name("MUX_MSG")
    vectors = generate_vectors_for_message(message, count_per_signal=2)
    mux1 = [vector for vector in vectors if "mux_branch_1" in vector.tags]
    mux2 = [vector for vector in vectors if "mux_branch_2" in vector.tags]
    assert mux1
    assert mux2
    assert any(vector.signal_values["MuxSwitch"] == 1.0 for vector in mux1)
    assert any(vector.signal_values["MuxSwitch"] == 2.0 for vector in mux2)


def test_generate_vectors_for_message_returns_testvector_instances(
    cantools_module, sample_dbc_path
) -> None:
    db = cantools_module.database.load_file(str(sample_dbc_path))
    message = db.get_message_by_name("MESSAGE_1")
    vectors = generate_vectors_for_message(message, count_per_signal=1)
    assert vectors
    assert all(hasattr(vector, "message_name") for vector in vectors)
    assert all(hasattr(vector, "signal_values") for vector in vectors)
    assert all(hasattr(vector, "tags") for vector in vectors)
