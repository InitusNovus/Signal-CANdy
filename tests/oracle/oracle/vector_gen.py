"""Test vector generation for encode/decode validation.

Generates comprehensive test vectors including:
- Boundary values (min, max, mid-range, raw extremes)
- Random uniform samples within valid range
- Adversarial cases (rounding boundaries where cantools/Signal-CANdy diverge)
- Multiplexed message support (per-branch vectors)
"""

from __future__ import annotations

from dataclasses import dataclass
import importlib
import random
from typing import Any


_MAX_ADVERSARIAL_RAW_SPAN = 1 << 20
_MAX_ROUNDING_BOUNDARY_VECTORS = 10
_UNIFORM_RAW_SAMPLES = 16


@dataclass(frozen=True)
class TestVector:
    """Complete test vector for a single message instance.

    Attributes:
        message_name: Name of the CAN message
        signal_values: Dict mapping signal name to physical value
        tags: Set of string tags describing the vector type
    """

    message_name: str
    signal_values: dict[str, float]
    tags: set[str]


def generate_vectors_for_signal(
    signal: Any, count: int = 10
) -> list[tuple[float, set[str]]]:
    """Generate test vectors for a single signal.

    Produces boundary values (always included), random samples, and mid-range value.
    All values are guaranteed to be within [signal.minimum, signal.maximum].

    Args:
        signal: cantools.Signal instance with attributes: length, is_signed, scale,
                offset, minimum, maximum
        count: Number of random vectors to generate (max 100, per plan constraint)

    Returns:
        List of (physical_value, tags_set) tuples. Tags include:
        - "boundary": raw=0, raw=max, raw=min (for signed)
        - "mid_range": midpoint between min and max
        - "random": uniformly sampled from valid range
    """
    # Import cantools dynamically to handle missing dependency gracefully
    try:
        importlib.import_module("cantools")
    except ImportError:
        raise ImportError("cantools is required for vector generation")

    vectors: list[tuple[float, set[str]]] = []

    # Cap random vectors at 100 per plan
    count = min(count, 100)

    # Compute raw value bounds
    if signal.is_signed:
        min_raw = -(2 ** (signal.length - 1))
        max_raw = 2 ** (signal.length - 1) - 1
    else:
        min_raw = 0
        max_raw = 2**signal.length - 1

    # Helper to convert raw to physical
    def raw_to_phys(raw_val: int) -> float:
        return signal.offset + signal.scale * raw_val

    # Helper to check if physical value is within signal bounds
    def is_valid(phys: float) -> bool:
        return signal.minimum <= phys <= signal.maximum

    # Always include signal.minimum and signal.maximum as boundary values
    vectors.append((signal.minimum, {"boundary"}))
    vectors.append((signal.maximum, {"boundary"}))

    # Boundary: raw=0 (if different from minimum)
    phys_zero = raw_to_phys(0)
    if is_valid(phys_zero) and abs(phys_zero - signal.minimum) > 1e-9:
        vectors.append((phys_zero, {"boundary"}))

    # Boundary: raw=max (if different from maximum)
    phys_max = raw_to_phys(max_raw)
    if is_valid(phys_max) and abs(phys_max - signal.maximum) > 1e-9:
        vectors.append((phys_max, {"boundary"}))

    # Boundary: raw=min (signed only, if different from minimum)
    if signal.is_signed:
        phys_min = raw_to_phys(min_raw)
        if is_valid(phys_min) and abs(phys_min - signal.minimum) > 1e-9:
            vectors.append((phys_min, {"boundary"}))

        # Boundary: raw=-1 (signed only)
        phys_neg1 = raw_to_phys(-1)
        if is_valid(phys_neg1):
            # Check it's not a duplicate
            existing = {signal.minimum, signal.maximum, phys_min, phys_max}
            if not any(abs(phys_neg1 - e) < 1e-9 for e in existing):
                vectors.append((phys_neg1, {"boundary"}))

    # Mid-range: average of signal.minimum and signal.maximum
    mid_phys = (signal.minimum + signal.maximum) / 2.0
    if is_valid(mid_phys):
        vectors.append((mid_phys, {"mid_range"}))

    # Quarter-range boundary (additional coverage)
    quarter_phys = signal.minimum + (signal.maximum - signal.minimum) * 0.25
    if is_valid(quarter_phys):
        vectors.append((quarter_phys, {"boundary"}))

    # Random uniform samples
    for _ in range(count):
        rand_phys = random.uniform(signal.minimum, signal.maximum)
        vectors.append((rand_phys, {"random"}))

    return vectors


def generate_adversarial_vectors(signal: Any) -> list[tuple[float, set[str]]]:
    """Generate adversarial test vectors for rounding edge cases.

    Generates values at rounding boundaries where Signal-CANdy (round-half-away-from-zero)
    and cantools (banker's rounding) may diverge. These occur at:
        phys = offset + scale * (raw + 0.5)

    Also includes float32 edge cases for signals with large ranges.

    Args:
        signal: cantools.Signal instance

    Returns:
        List of (physical_value, tags_set) tuples with tags:
        - "adversarial", "rounding_boundary": rounding edge cases
        - "adversarial", "float_edge": float32 extreme values
    """
    try:
        importlib.import_module("cantools")
    except ImportError:
        raise ImportError("cantools is required for vector generation")

    vectors: list[tuple[float, set[str]]] = []

    # Compute raw value bounds
    if signal.is_signed:
        min_raw = -(2 ** (signal.length - 1))
        max_raw = 2 ** (signal.length - 1) - 1
    else:
        min_raw = 0
        max_raw = 2**signal.length - 1

    # Helper to check if physical value is within signal bounds
    def is_valid(phys: float) -> bool:
        return signal.minimum <= phys <= signal.maximum

    def candidate_raw_values() -> list[int] | range:
        raw_span = max_raw - min_raw + 1
        if raw_span <= _MAX_ADVERSARIAL_RAW_SPAN:
            return range(min_raw, max_raw + 1)

        sample_points = {min_raw, max_raw}
        for index in range(1, _UNIFORM_RAW_SAMPLES + 1):
            fraction = index / (_UNIFORM_RAW_SAMPLES + 1)
            sample = min_raw + int(round((raw_span - 1) * fraction))
            sample_points.add(sample)

        return sorted(sample_points)

    # Rounding boundaries: phys = offset + scale * (raw + 0.5)
    # Cap at 10 vectors to avoid explosion (per plan)
    rounding_count = 0
    for raw in candidate_raw_values():
        if rounding_count >= _MAX_ROUNDING_BOUNDARY_VECTORS:
            break

        phys_boundary = signal.offset + signal.scale * (raw + 0.5)
        if is_valid(phys_boundary):
            vectors.append((phys_boundary, {"adversarial", "rounding_boundary"}))
            rounding_count += 1

    # Float32 edge cases (if signal range allows)
    # ±3.4e38 is approximately float32 max
    float32_max = 3.4e38
    if signal.maximum >= float32_max * 0.9:
        if is_valid(float32_max * 0.9):
            vectors.append((float32_max * 0.9, {"adversarial", "float_edge"}))

    if signal.minimum <= -float32_max * 0.9:
        if is_valid(-float32_max * 0.9):
            vectors.append((-float32_max * 0.9, {"adversarial", "float_edge"}))

    return vectors


def generate_vectors_for_message(
    message: Any, count_per_signal: int = 10
) -> list[TestVector]:
    """Generate comprehensive test vectors for a complete message.

    Creates both isolated (single-signal) and combined (all-signals) test vectors.
    Handles multiplexed messages by generating per-branch vectors.

    Args:
        message: cantools.Message instance with attributes: name, signals
        count_per_signal: Number of random vectors per signal (max 100)

    Returns:
        List of TestVector instances. For multiplexed messages, each vector includes
        the switch signal value and only the active branch signals. Tags include:
        - "isolated": only one signal set to test value, others at minimum/0
        - "combined": all signals set to random values
        - "multiplexed", "mux_branch_{N}": for multiplexed message vectors
    """
    try:
        importlib.import_module("cantools")
    except ImportError:
        raise ImportError("cantools is required for vector generation")

    vectors: list[TestVector] = []

    # Cap per-signal count at 100
    count_per_signal = min(count_per_signal, 100)

    # Check if message is multiplexed
    is_multiplexed = any(sig.multiplexer_ids is not None for sig in message.signals)

    if is_multiplexed:
        # Handle multiplexed message
        vectors.extend(_generate_multiplexed_vectors(message, count_per_signal))
    else:
        # Handle non-multiplexed message
        vectors.extend(_generate_standard_vectors(message, count_per_signal))

    return vectors


def _generate_standard_vectors(message: Any, count_per_signal: int) -> list[TestVector]:
    """Generate vectors for non-multiplexed messages.

    Creates isolated (single-signal) and combined (all-signals) test vectors.
    """
    vectors: list[TestVector] = []

    # Collect all test values for each signal
    signal_test_values: dict[str, list[tuple[float, set[str]]]] = {}

    for signal in message.signals:
        # Combine boundary, random, and adversarial vectors
        sig_vectors = generate_vectors_for_signal(signal, count_per_signal)
        sig_vectors.extend(generate_adversarial_vectors(signal))
        signal_test_values[signal.name] = sig_vectors

    # Generate isolated vectors (one signal at test value, others at minimum/0)
    for signal in message.signals:
        for test_val, val_tags in signal_test_values[signal.name]:
            signal_values = {}

            # Set test signal to test value
            signal_values[signal.name] = test_val

            # Set other signals to minimum or 0
            for other_sig in message.signals:
                if other_sig.name != signal.name:
                    # Use minimum if available, else 0
                    signal_values[other_sig.name] = (
                        other_sig.minimum if other_sig.minimum is not None else 0.0
                    )

            # Merge tags
            combined_tags = {"isolated"} | val_tags
            vectors.append(
                TestVector(
                    message_name=message.name,
                    signal_values=signal_values,
                    tags=combined_tags,
                )
            )

    # Generate combined vectors (all signals at random values simultaneously)
    # Use count_per_signal as number of combined vectors
    for _ in range(count_per_signal):
        signal_values = {}
        for signal in message.signals:
            # Pick random value from valid range
            rand_val = random.uniform(signal.minimum, signal.maximum)
            signal_values[signal.name] = rand_val

        vectors.append(
            TestVector(
                message_name=message.name,
                signal_values=signal_values,
                tags={"combined", "random"},
            )
        )

    return vectors


def _generate_multiplexed_vectors(
    message: Any, count_per_signal: int
) -> list[TestVector]:
    """Generate vectors for multiplexed messages.

    Groups signals by multiplexer branch and generates per-branch vectors.
    Each vector includes the switch signal value and only active branch signals.
    """
    vectors: list[TestVector] = []

    # Find the multiplexer (switch) signal
    switch_signal = None
    for sig in message.signals:
        if (
            hasattr(sig, "multiplexer_signal")
            and sig.multiplexer_signal is None
            and any(s.multiplexer_signal == sig.name for s in message.signals)
        ):
            switch_signal = sig
            break

    if switch_signal is None:
        # Fallback: treat as standard message if switch not found
        return _generate_standard_vectors(message, count_per_signal)

    # Group signals by multiplexer_ids
    base_signals = []  # No multiplexer_ids
    branch_signals: dict[int, list[Any]] = {}  # mux_id -> list of signals

    for sig in message.signals:
        if sig.name == switch_signal.name:
            continue  # Skip switch signal itself

        if sig.multiplexer_ids is None or len(sig.multiplexer_ids) == 0:
            # Base signal (always active)
            base_signals.append(sig)
        else:
            # Branch signal
            for mux_id in sig.multiplexer_ids:
                if mux_id not in branch_signals:
                    branch_signals[mux_id] = []
                branch_signals[mux_id].append(sig)

    # Generate vectors for each branch
    for mux_id, branch_sigs in branch_signals.items():
        # Active signals: switch + base + branch
        active_signals = [switch_signal] + base_signals + branch_sigs

        # Collect test values for active signals
        signal_test_values: dict[str, list[tuple[float, set[str]]]] = {}
        for signal in active_signals:
            sig_vectors = generate_vectors_for_signal(signal, count_per_signal)
            sig_vectors.extend(generate_adversarial_vectors(signal))
            signal_test_values[signal.name] = sig_vectors

        # Generate isolated vectors for this branch
        for signal in active_signals:
            for test_val, val_tags in signal_test_values[signal.name]:
                signal_values = {}

                # Set switch to mux_id
                signal_values[switch_signal.name] = float(mux_id)

                # Set test signal to test value
                signal_values[signal.name] = test_val

                # Set other active signals to minimum/0
                for other_sig in active_signals:
                    if (
                        other_sig.name != signal.name
                        and other_sig.name != switch_signal.name
                    ):
                        signal_values[other_sig.name] = (
                            other_sig.minimum if other_sig.minimum is not None else 0.0
                        )

                # Merge tags
                combined_tags = {
                    "isolated",
                    "multiplexed",
                    f"mux_branch_{mux_id}",
                } | val_tags
                vectors.append(
                    TestVector(
                        message_name=message.name,
                        signal_values=signal_values,
                        tags=combined_tags,
                    )
                )

        # Generate combined vectors for this branch
        for _ in range(count_per_signal):
            signal_values = {}

            # Set switch to mux_id
            signal_values[switch_signal.name] = float(mux_id)

            # Set all active signals to random values
            for signal in base_signals + branch_sigs:
                rand_val = random.uniform(signal.minimum, signal.maximum)
                signal_values[signal.name] = rand_val

            vectors.append(
                TestVector(
                    message_name=message.name,
                    signal_values=signal_values,
                    tags={"combined", "random", "multiplexed", f"mux_branch_{mux_id}"},
                )
            )

    return vectors
