"""Tolerance computation and comparison for oracle testing."""

# Float32 machine epsilon
FLT_EPSILON = 1.19209e-07


def compute_tolerance(
    factor: float,
    offset: float,
    expected_phys: float,
    bit_length: int,
    is_signed: bool,
) -> float:
    """
    Compute tolerance for comparing physical values.

    Formula: tol = max(abs(factor) * 0.5, abs(expected_phys) * FLT_EPSILON * 8)

    Special cases:
    - Integer signals (factor=1, offset=0): return 0.0 (exact match)
    - Very small factors: lower bound = abs(factor) * 0.5 (1 raw LSB precision limit)
    """
    # Keep signature aligned with signal metadata inputs used by callers.
    _ = bit_length, is_signed

    # Integer signal: exact match required.
    if factor == 1.0 and offset == 0.0:
        return 0.0

    # Lower bound: cannot be more precise than 1 raw LSB.
    lsb_tol = abs(factor) * 0.5

    # Upper bound: float32 precision of expected value.
    float_tol = abs(expected_phys) * FLT_EPSILON * 8

    return max(lsb_tol, float_tol)


def compare_physical(cantools_val: float, c_val: float, tolerance: float) -> bool:
    """Compare two physical values within tolerance."""
    return abs(cantools_val - c_val) <= tolerance


def compare_bytes(
    cantools_bytes: bytes,
    c_bytes: bytes,
    signal_bit_positions: dict | None = None,
) -> tuple[bool, list[str]]:
    """
    Compare byte arrays with +/-1 LSB tolerance for rounding divergence.

    Args:
        cantools_bytes: Reference bytes from cantools.encode()
        c_bytes: Bytes from C harness encode()
        signal_bit_positions: Optional dict mapping byte_index -> list[signal_names]
            (for detailed error messages)

    Returns:
        (match: bool, differences: list[str])
    """
    _ = signal_bit_positions

    if len(cantools_bytes) != len(c_bytes):
        return (
            False,
            [f"Length mismatch: cantools={len(cantools_bytes)} vs C={len(c_bytes)}"],
        )

    differences = []
    for i, (ct_byte, c_byte) in enumerate(zip(cantools_bytes, c_bytes)):
        diff = abs(ct_byte - c_byte)
        if diff > 1:  # Allow +/-1 for rounding.
            differences.append(
                f"Byte[{i}]: cantools=0x{ct_byte:02x} vs C=0x{c_byte:02x} (diff={diff})"
            )

    return (len(differences) == 0, differences)
