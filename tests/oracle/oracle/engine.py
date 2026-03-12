from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timezone
import importlib
import json
import logging
import os
from pathlib import Path
import math
import random
import re
import struct
import subprocess
from typing import Any

from . import tolerance as tolerance_module
from .harness import MessageInfo, compile_harness, generate_harness_c, run_harness


LOGGER = logging.getLogger(__name__)
DEFAULT_VECTORS_PER_SIGNAL = 10
_FLOAT32_MAX = 3.4028234663852886e38

_DECODE_FN_PATTERN = re.compile(r"\bbool\s+([A-Za-z_][A-Za-z0-9_]*)_decode\s*\(")
_FIELD_PATTERN = re.compile(r"^([A-Za-z_][A-Za-z0-9_]*)\s+([A-Za-z_][A-Za-z0-9_]*)$")


@dataclass(frozen=True)
class TestVector:
    message: str
    signal: str
    signals: dict[str, float]


@dataclass(frozen=True)
class TestResult:
    message: str
    signal: str
    test_type: str
    passed: bool
    error: str | None = None
    cantools_value: float | None = None
    c_value: float | None = None
    tolerance: float | None = None
    skipped: bool = False

    def to_dict(self) -> dict[str, Any]:
        return {
            "message": self.message,
            "signal": self.signal,
            "test_type": self.test_type,
            "passed": self.passed,
            "error": self.error,
            "cantools_value": self.cantools_value,
            "c_value": self.c_value,
            "tolerance": self.tolerance,
            "skipped": self.skipped,
        }


@dataclass(frozen=True)
class OracleReport:
    dbc_path: str
    config_path: str
    timestamp: str
    passed: int
    failed: int
    skipped: int
    results: list[TestResult]

    def to_dict(self) -> dict[str, Any]:
        return {
            "dbc_path": self.dbc_path,
            "config_path": self.config_path,
            "timestamp": self.timestamp,
            "passed": self.passed,
            "failed": self.failed,
            "skipped": self.skipped,
            "results": [result.to_dict() for result in self.results],
        }

    def write_json(self, path: str | Path) -> None:
        output_path = Path(path)
        output_path.parent.mkdir(parents=True, exist_ok=True)
        output_path.write_text(
            json.dumps(self.to_dict(), indent=2, sort_keys=False),
            encoding="utf-8",
        )


@dataclass(frozen=True)
class _VectorContext:
    vector: TestVector
    message_obj: Any
    signal_obj: Any
    expected_value: float
    cantools_bytes: bytes


def _now_iso8601() -> str:
    return (
        datetime.now(timezone.utc)
        .replace(microsecond=0)
        .isoformat()
        .replace("+00:00", "Z")
    )


def _to_float(value: Any, default: float = 0.0) -> float:
    try:
        return float(value)
    except (TypeError, ValueError):
        return default


def _to_int(value: Any, default: int = 0) -> int:
    try:
        return int(value)
    except (TypeError, ValueError):
        return default


def _report_from_results(
    dbc_path: str,
    config_path: str,
    timestamp: str,
    results: list[TestResult],
) -> OracleReport:
    passed = sum(1 for result in results if result.passed and not result.skipped)
    failed = sum(1 for result in results if not result.passed and not result.skipped)
    skipped = sum(1 for result in results if result.skipped)
    return OracleReport(
        dbc_path=dbc_path,
        config_path=config_path,
        timestamp=timestamp,
        passed=passed,
        failed=failed,
        skipped=skipped,
        results=results,
    )


def _pipeline_error(stage: str, error: str) -> TestResult:
    return TestResult(
        message="__pipeline__",
        signal=stage,
        test_type="decode",
        passed=False,
        error=error,
    )


def _overflow_guarded_result(message: str, signal: str) -> TestResult:
    return TestResult(
        message=message,
        signal=signal,
        test_type="overflow_guarded",
        passed=False,
        error="overflow_guarded",
        skipped=True,
    )


def load_dbc_cantools(dbc_path: str) -> Any:
    try:
        cantools = importlib.import_module("cantools")
    except ModuleNotFoundError as ex:
        raise RuntimeError(
            "cantools is not installed. Install dependencies with "
            "`pip install -r tests/oracle/requirements.txt`."
        ) from ex

    return cantools.database.load_file(dbc_path)


def _find_repo_root() -> Path:
    """Find repository root by walking up from __file__ until .git is found."""
    current = Path(__file__).resolve().parent
    while current != current.parent:
        if (current / ".git").exists():
            return current
        current = current.parent
    raise RuntimeError("Repository root not found")


def run_codegen(dbc_path: str, config_path: str, out_dir: str) -> bool:
    # Resolve all paths relative to current working directory (before changing CWD)
    dbc_abs = str(Path(dbc_path).resolve())
    config_abs = str(Path(config_path).resolve())
    out_abs = str(Path(out_dir).resolve())

    # Create output directory
    out_path = Path(out_abs)
    out_path.mkdir(parents=True, exist_ok=True)

    repo_root = _find_repo_root()

    command = [
        "dotnet",
        "run",
        "--project",
        "src/Generator",
        "--",
        "--dbc",
        dbc_abs,
        "--out",
        out_abs,
        "--config",
        config_abs,
        "--emit-main",
        "false",
    ]

    try:
        result = subprocess.run(
            command,
            check=False,
            capture_output=True,
            text=True,
            encoding="utf-8",
            cwd=repo_root,
        )
    except OSError as ex:
        LOGGER.error("Codegen invocation failed: %s", ex)
        return False

    if result.returncode != 0:
        if result.stdout:
            LOGGER.error("Codegen stdout:\n%s", result.stdout.strip())
        if result.stderr:
            LOGGER.error("Codegen stderr:\n%s", result.stderr.strip())
        return False

    return True


def _extract_message_name(header_text: str) -> str | None:
    match = _DECODE_FN_PATTERN.search(header_text)
    if match is None:
        return None

    return match.group(1)


def _extract_struct_body(header_text: str, message_name: str) -> str | None:
    pattern = re.compile(
        rf"typedef\s+struct\s*\{{(?P<body>.*?)\}}\s*{re.escape(message_name)}_t\s*;",
        re.DOTALL,
    )
    match = pattern.search(header_text)
    if match is None:
        return None

    return match.group("body")


def _parse_struct_signals(struct_body: str) -> list[tuple[str, str]]:
    signals: list[tuple[str, str]] = []
    for raw_line in struct_body.splitlines():
        line = raw_line.split("//", maxsplit=1)[0].strip()
        if not line or not line.endswith(";"):
            continue

        declaration = line[:-1].strip()
        match = _FIELD_PATTERN.match(declaration)
        if match is None:
            continue

        field_type, field_name = match.groups()
        if field_type not in {"float", "double"}:
            continue

        signals.append((field_name, field_type))

    return signals


def _find_source_file(
    src_dir: Path, message_name: str, header_stem: str
) -> Path | None:
    candidates = [
        src_dir / f"{header_stem}.c",
        src_dir / f"{message_name}.c",
        src_dir / f"{message_name.lower()}.c",
    ]
    for candidate in candidates:
        if candidate.exists():
            return candidate

    for source_file in sorted(src_dir.glob("*.c")):
        try:
            source_text = source_file.read_text(encoding="utf-8")
        except OSError:
            continue

        if (
            f"{message_name}_decode" in source_text
            and f"{message_name}_encode" in source_text
        ):
            return source_file

    return None


def _extract_dlc(source_text: str) -> int:
    decode_match = re.search(r"\bdlc\s*<\s*(\d+)\s*\)", source_text)
    if decode_match is not None:
        return _to_int(decode_match.group(1), default=8)

    encode_match = re.search(r"\*out_dlc\s*=\s*(\d+)\s*;", source_text)
    if encode_match is not None:
        return _to_int(encode_match.group(1), default=8)

    return 8


def extract_message_info(gen_dir: str) -> list[MessageInfo]:
    gen_path = Path(gen_dir)
    include_dir = gen_path / "include"
    src_dir = gen_path / "src"
    if not include_dir.exists():
        return []

    messages: list[MessageInfo] = []
    for header_file in sorted(include_dir.glob("*.h")):
        try:
            header_text = header_file.read_text(encoding="utf-8")
        except OSError:
            continue

        message_name = _extract_message_name(header_text)
        if message_name is None:
            continue

        struct_body = _extract_struct_body(header_text, message_name)
        if struct_body is None:
            continue

        signals = _parse_struct_signals(struct_body)
        source_file = _find_source_file(src_dir, message_name, header_file.stem)
        if source_file is None:
            dlc = 8
        else:
            try:
                source_text = source_file.read_text(encoding="utf-8")
            except OSError:
                source_text = ""
            dlc = _extract_dlc(source_text)

        messages.append(MessageInfo(name=message_name, signals=signals, dlc=dlc))

    return messages


def build_oracle_binary(gen_dir: str, messages: list[MessageInfo]) -> str:
    gen_path = Path(gen_dir)
    include_dir = gen_path / "include"
    src_dir = gen_path / "src"
    if not include_dir.exists() or not src_dir.exists():
        return ""

    harness_source = generate_harness_c(messages, str(include_dir), str(src_dir))
    binary_name = "oracle_harness.exe" if os.name == "nt" else "oracle_harness"
    binary_path = gen_path / "build" / binary_name
    if not compile_harness(harness_source, str(gen_path), str(binary_path)):
        return ""

    return str(binary_path)


def _message_by_name(db: Any, message_name: str) -> Any | None:
    try:
        return db.get_message_by_name(message_name)
    except Exception:
        target = message_name.lower()
        for message in getattr(db, "messages", []):
            if str(getattr(message, "name", "")).lower() == target:
                return message

    return None


def _message_is_multiplexed(message: Any) -> bool:
    value = getattr(message, "is_multiplexed", None)
    if callable(value):
        try:
            return bool(value())
        except TypeError:
            return False

    if isinstance(value, bool):
        return value

    for signal in getattr(message, "signals", []):
        if bool(getattr(signal, "is_multiplexer", False)):
            return True
        mux_ids = getattr(signal, "multiplexer_ids", None)
        if isinstance(mux_ids, (list, tuple)) and len(mux_ids) > 0:
            return True

    return False


def _signal_scale_offset(signal: Any) -> tuple[float, float]:
    conversion = getattr(signal, "conversion", None)
    if conversion is None:
        return 1.0, 0.0

    scale = _to_float(getattr(conversion, "scale", 1.0), default=1.0)
    offset = _to_float(getattr(conversion, "offset", 0.0), default=0.0)
    return scale, offset


def _raw_bounds(signal: Any) -> tuple[int, int]:
    bit_length = max(1, _to_int(getattr(signal, "length", 1), default=1))
    is_signed = bool(getattr(signal, "is_signed", False))
    if is_signed:
        return (-(1 << (bit_length - 1)), (1 << (bit_length - 1)) - 1)

    return (0, (1 << bit_length) - 1)


def _normalize_boundary_raw(raw_value: int, bit_length: int, is_signed: bool) -> int:
    if not is_signed:
        return max(0, min(raw_value, (1 << bit_length) - 1))

    min_raw = -(1 << (bit_length - 1))
    max_raw = (1 << (bit_length - 1)) - 1
    if raw_value < min_raw:
        return min_raw

    if raw_value <= max_raw:
        return raw_value

    max_unsigned = (1 << bit_length) - 1
    if raw_value <= max_unsigned:
        return max(min_raw, raw_value - (1 << bit_length))

    return max_raw


def _raw_to_physical(signal: Any, raw_value: int) -> float:
    scale, offset = _signal_scale_offset(signal)
    return (raw_value * scale) + offset


def _to_float32(value: float) -> float:
    return struct.unpack("<f", struct.pack("<f", float(value)))[0]


def _try_float32(value: float) -> float | None:
    try:
        narrowed = float(value)
    except (TypeError, ValueError, OverflowError):
        return None

    if not math.isfinite(narrowed) or abs(narrowed) > _FLOAT32_MAX:
        return None

    try:
        narrowed = _to_float32(narrowed)
    except (OverflowError, struct.error):
        return None

    if not math.isfinite(narrowed):
        return None

    return narrowed


def _safe_float32_physical(signal: Any, raw_value: int) -> float | None:
    try:
        physical = _raw_to_physical(signal, raw_value)
    except OverflowError:
        return None

    return _try_float32(physical)


def _safe_physical(signal: Any, raw_value: int) -> float | None:
    min_raw, max_raw = _raw_bounds(signal)
    raw = max(min(raw_value, max_raw), min_raw)

    scale, offset = _signal_scale_offset(signal)
    minimum_raw = getattr(signal, "minimum", None)
    maximum_raw = getattr(signal, "maximum", None)
    minimum = _to_float(minimum_raw) if minimum_raw is not None else None
    maximum = _to_float(maximum_raw) if maximum_raw is not None else None

    if scale != 0.0:
        if minimum is not None:
            limit = (
                math.ceil((minimum - offset) / scale)
                if scale > 0
                else math.floor((minimum - offset) / scale)
            )
            raw = max(raw, limit) if scale > 0 else min(raw, limit)
        if maximum is not None:
            limit = (
                math.floor((maximum - offset) / scale)
                if scale > 0
                else math.ceil((maximum - offset) / scale)
            )
            raw = min(raw, limit) if scale > 0 else max(raw, limit)

    raw = max(min(raw, max_raw), min_raw)

    for _ in range(4096):
        value = _safe_float32_physical(signal, raw)
        if value is None:
            return None
        if minimum is not None and value < minimum and raw < max_raw:
            raw += 1
            continue
        if maximum is not None and value > maximum and raw > min_raw:
            raw -= 1
            continue
        return value

    return _safe_float32_physical(signal, raw)


def _default_signal_value(signal: Any) -> float | None:
    min_raw, max_raw = _raw_bounds(signal)
    candidate_raws = [0, min_raw, max_raw]
    if min_raw <= -1 <= max_raw:
        candidate_raws.append(-1)
    if min_raw <= 1 <= max_raw:
        candidate_raws.append(1)

    seen: set[int] = set()
    for raw_value in candidate_raws:
        if raw_value in seen:
            continue
        seen.add(raw_value)

        value = _safe_physical(signal, raw_value)
        if value is not None:
            return value

    return None


def _signal_values(
    signal: Any, vectors_per_signal: int, rng: random.Random
) -> tuple[list[float], bool]:
    bit_length = max(1, _to_int(getattr(signal, "length", 1), default=1))
    is_signed = bool(getattr(signal, "is_signed", False))

    boundary_raw = [0, (1 << bit_length) - 1]
    if is_signed:
        boundary_raw.append(-(1 << (bit_length - 1)))

    values: list[float] = []
    overflow_guarded = False
    for raw_value in boundary_raw:
        normalized = _normalize_boundary_raw(raw_value, bit_length, is_signed)
        value = _safe_physical(signal, normalized)
        if value is None:
            overflow_guarded = True
            continue
        values.append(value)

    min_raw, max_raw = _raw_bounds(signal)
    for _ in range(max(0, vectors_per_signal)):
        raw_value = rng.randint(min_raw, max_raw)
        value = _safe_physical(signal, raw_value)
        if value is None:
            overflow_guarded = True
            continue
        values.append(value)

    unique: list[float] = []
    seen: set[float] = set()
    for value in values:
        key = round(value, 12)
        if key in seen:
            continue

        seen.add(key)
        unique.append(value)

    if not unique:
        fallback = _default_signal_value(signal)
        if fallback is not None:
            unique.append(fallback)
        else:
            overflow_guarded = True

    return unique, overflow_guarded


def _unsupported_signal_reason(signal: Any) -> str | None:
    if bool(getattr(signal, "is_float", False)):
        return "SIG_VALTYPE_ float signal is skipped"

    mux_ids = getattr(signal, "multiplexer_ids", None)
    if isinstance(mux_ids, (list, tuple)) and len(mux_ids) > 1:
        return "extended multiplex signal is skipped"

    return None


def generate_test_vectors(
    db: Any,
    messages: list[MessageInfo],
    vectors_per_signal: int = DEFAULT_VECTORS_PER_SIGNAL,
) -> tuple[list[TestVector], list[TestResult]]:
    vectors: list[TestVector] = []
    skipped: list[TestResult] = []
    rng = random.Random(1337)

    for message_info in messages:
        message_obj = _message_by_name(db, message_info.name)
        if message_obj is None:
            skipped.append(
                TestResult(
                    message=message_info.name,
                    signal="__message__",
                    test_type="decode",
                    passed=False,
                    error="generated message not found in cantools database",
                    skipped=True,
                )
            )
            continue

        if _message_is_multiplexed(message_obj):
            for signal in getattr(message_obj, "signals", []):
                skipped.append(
                    TestResult(
                        message=message_obj.name,
                        signal=signal.name,
                        test_type="decode",
                        passed=False,
                        error="multiplexed message is skipped in single-config mode",
                        skipped=True,
                    )
                )
            continue

        generated_signal_names = {name for name, _ in message_info.signals}
        signal_map = {
            signal.name: signal
            for signal in getattr(message_obj, "signals", [])
            if signal.name in generated_signal_names
        }
        if not signal_map:
            continue

        defaults: dict[str, float] = {}
        default_overflows: set[str] = set()
        for name, signal in signal_map.items():
            default_value = _default_signal_value(signal)
            if default_value is None:
                default_overflows.add(name)
                skipped.append(_overflow_guarded_result(message_obj.name, name))
                continue
            defaults[name] = default_value

        for signal_name in sorted(signal_map):
            signal_obj = signal_map[signal_name]
            if signal_name in default_overflows:
                continue
            reason = _unsupported_signal_reason(signal_obj)
            if reason is not None:
                skipped.append(
                    TestResult(
                        message=message_obj.name,
                        signal=signal_name,
                        test_type="decode",
                        passed=False,
                        error=reason,
                        skipped=True,
                    )
                )
                continue

            values, overflow_guarded = _signal_values(
                signal_obj, vectors_per_signal, rng
            )
            if overflow_guarded:
                skipped.append(_overflow_guarded_result(message_obj.name, signal_name))

            for value in values:
                payload = dict(defaults)
                payload[signal_name] = value
                vectors.append(
                    TestVector(
                        message=message_obj.name,
                        signal=signal_name,
                        signals=payload,
                    )
                )

    return vectors, skipped


def _cantools_encode(message_obj: Any, signals: dict[str, float]) -> bytes:
    try:
        encoded = message_obj.encode(signals, strict=False)
    except TypeError:
        encoded = message_obj.encode(signals)

    return bytes(encoded)


def _cantools_decode(message_obj: Any, payload: bytes) -> dict[str, Any]:
    try:
        decoded = message_obj.decode(payload, decode_choices=False)
    except TypeError:
        decoded = message_obj.decode(payload)

    if not isinstance(decoded, dict):
        raise ValueError("cantools decode did not return a dictionary")

    return decoded


def _compute_tolerance(signal_obj: Any, expected_value: float) -> float:
    scale, offset = _signal_scale_offset(signal_obj)
    bit_length = max(1, _to_int(getattr(signal_obj, "length", 1), default=1))
    is_signed = bool(getattr(signal_obj, "is_signed", False))

    tolerance_fn = getattr(tolerance_module, "compute_tolerance", None)
    if callable(tolerance_fn):
        try:
            value = tolerance_fn(scale, offset, expected_value, bit_length, is_signed)
            return max(_to_float(value, default=0.0), 0.0)
        except Exception:
            pass

    return max(abs(scale) * 0.5, 1e-6)


def _compare_physical(
    expected_value: float, actual_value: float, tolerance: float
) -> bool:
    compare_fn = getattr(tolerance_module, "compare_physical", None)
    if callable(compare_fn):
        try:
            return bool(compare_fn(expected_value, actual_value, tolerance))
        except Exception:
            pass

    return abs(expected_value - actual_value) <= tolerance


def _compare_bytes(
    expected_payload: bytes, actual_payload: bytes
) -> tuple[bool, str | None]:
    if len(expected_payload) != len(actual_payload):
        return (
            False,
            f"payload length mismatch: cantools={len(expected_payload)} c={len(actual_payload)}",
        )

    for index, (left, right) in enumerate(zip(expected_payload, actual_payload)):
        if abs(left - right) > 1:
            return (
                False,
                f"byte mismatch beyond +/-1 LSB at index {index}: cantools={left}, c={right}",
            )

    return True, None


def _failure_triplet(vector: TestVector, error: str) -> list[TestResult]:
    return [
        TestResult(
            message=vector.message,
            signal=vector.signal,
            test_type="decode",
            passed=False,
            error=error,
        ),
        TestResult(
            message=vector.message,
            signal=vector.signal,
            test_type="encode",
            passed=False,
            error=error,
        ),
        TestResult(
            message=vector.message,
            signal=vector.signal,
            test_type="byte",
            passed=False,
            error=error,
            tolerance=1.0,
        ),
    ]


def _decode_result(context: _VectorContext, response: dict[str, Any]) -> TestResult:
    if not bool(response.get("ok", False)):
        return TestResult(
            message=context.vector.message,
            signal=context.vector.signal,
            test_type="decode",
            passed=False,
            error=str(response.get("error", "decode request failed")),
        )

    signal_values = response.get("signals")
    if not isinstance(signal_values, dict):
        return TestResult(
            message=context.vector.message,
            signal=context.vector.signal,
            test_type="decode",
            passed=False,
            error="decode response missing signals object",
        )

    if context.vector.signal not in signal_values:
        return TestResult(
            message=context.vector.message,
            signal=context.vector.signal,
            test_type="decode",
            passed=False,
            error="decode response missing tested signal",
        )

    c_value = _to_float(signal_values.get(context.vector.signal), default=float("nan"))
    tolerance = _compute_tolerance(context.signal_obj, context.expected_value)
    passed = _compare_physical(context.expected_value, c_value, tolerance)

    return TestResult(
        message=context.vector.message,
        signal=context.vector.signal,
        test_type="decode",
        passed=passed,
        error=None if passed else "decoded value differs from cantools input",
        cantools_value=context.expected_value,
        c_value=c_value,
        tolerance=tolerance,
    )


def _encode_results(
    context: _VectorContext, response: dict[str, Any]
) -> tuple[TestResult, TestResult]:
    if not bool(response.get("ok", False)):
        error = str(response.get("error", "encode request failed"))
        return (
            TestResult(
                message=context.vector.message,
                signal=context.vector.signal,
                test_type="encode",
                passed=False,
                error=error,
            ),
            TestResult(
                message=context.vector.message,
                signal=context.vector.signal,
                test_type="byte",
                passed=False,
                error=error,
                tolerance=1.0,
            ),
        )

    data_field = response.get("data")
    dlc = _to_int(response.get("dlc"), default=0)
    if not isinstance(data_field, list) or dlc < 0 or dlc > len(data_field):
        error = "encode response has invalid payload"
        return (
            TestResult(
                message=context.vector.message,
                signal=context.vector.signal,
                test_type="encode",
                passed=False,
                error=error,
            ),
            TestResult(
                message=context.vector.message,
                signal=context.vector.signal,
                test_type="byte",
                passed=False,
                error=error,
                tolerance=1.0,
            ),
        )

    payload_values: list[int] = []
    for item in data_field[:dlc]:
        byte_value = _to_int(item, default=-1)
        if byte_value < 0 or byte_value > 255:
            error = "encode response contains non-byte value"
            return (
                TestResult(
                    message=context.vector.message,
                    signal=context.vector.signal,
                    test_type="encode",
                    passed=False,
                    error=error,
                ),
                TestResult(
                    message=context.vector.message,
                    signal=context.vector.signal,
                    test_type="byte",
                    passed=False,
                    error=error,
                    tolerance=1.0,
                ),
            )

        payload_values.append(byte_value)

    payload = bytes(payload_values)
    tolerance = _compute_tolerance(context.signal_obj, context.expected_value)

    try:
        decoded = _cantools_decode(context.message_obj, payload)
        if context.vector.signal not in decoded:
            encode_passed = False
            encode_error = "cantools decode output missing tested signal"
            c_value = None
        else:
            c_value = _to_float(
                decoded.get(context.vector.signal), default=float("nan")
            )
            encode_passed = _compare_physical(
                context.expected_value, c_value, tolerance
            )
            encode_error = (
                None if encode_passed else "encode/decode value differs from input"
            )
    except Exception as ex:
        c_value = None
        encode_passed = False
        encode_error = f"cantools decode failed for C payload: {ex}"

    byte_passed, byte_error = _compare_bytes(context.cantools_bytes, payload)
    return (
        TestResult(
            message=context.vector.message,
            signal=context.vector.signal,
            test_type="encode",
            passed=encode_passed,
            error=encode_error,
            cantools_value=context.expected_value,
            c_value=c_value,
            tolerance=tolerance,
        ),
        TestResult(
            message=context.vector.message,
            signal=context.vector.signal,
            test_type="byte",
            passed=byte_passed,
            error=byte_error,
            tolerance=1.0,
        ),
    )


def run_oracle_test(
    db: Any, binary: str, vectors: list[TestVector]
) -> list[TestResult]:
    results: list[TestResult] = []
    if not vectors:
        return results

    commands: list[dict[str, Any]] = []
    order: list[tuple[str, _VectorContext]] = []

    for vector in vectors:
        message_obj = _message_by_name(db, vector.message)
        if message_obj is None:
            results.extend(
                _failure_triplet(vector, "message not found in cantools database")
            )
            continue

        signal_obj = None
        for signal in getattr(message_obj, "signals", []):
            if signal.name == vector.signal:
                signal_obj = signal
                break

        if signal_obj is None:
            results.extend(
                _failure_triplet(vector, "signal not found in cantools message")
            )
            continue

        try:
            cantools_payload = _cantools_encode(message_obj, vector.signals)
        except Exception as ex:
            results.extend(_failure_triplet(vector, f"cantools encode failed: {ex}"))
            continue

        context = _VectorContext(
            vector=vector,
            message_obj=message_obj,
            signal_obj=signal_obj,
            expected_value=_to_float(vector.signals.get(vector.signal), default=0.0),
            cantools_bytes=cantools_payload,
        )

        commands.append(
            {
                "message": vector.message,
                "action": "decode",
                "data": list(cantools_payload),
                "dlc": len(cantools_payload),
            }
        )
        order.append(("decode", context))

        commands.append(
            {
                "message": vector.message,
                "action": "encode",
                "signals": vector.signals,
            }
        )
        order.append(("encode", context))

    if not commands:
        return results

    responses = run_harness(binary, commands)
    if len(responses) < len(order):
        responses.extend(
            [{"ok": False, "error": "harness produced fewer responses than commands"}]
            * (len(order) - len(responses))
        )

    for index, (operation, context) in enumerate(order):
        response = responses[index]
        if operation == "decode":
            results.append(_decode_result(context, response))
            continue

        encode_result, byte_result = _encode_results(context, response)
        results.append(encode_result)
        results.append(byte_result)

    return results


def oracle_pipeline(
    dbc_path: str,
    config_path: str,
    out_dir: str,
    vectors_per_signal: int = DEFAULT_VECTORS_PER_SIGNAL,
    verbose: bool = False,
) -> OracleReport:
    timestamp = _now_iso8601()
    dbc_full = str(Path(dbc_path).resolve())
    config_full = str(Path(config_path).resolve())

    if verbose:
        LOGGER.setLevel(logging.DEBUG)

    out_path = Path(out_dir)
    out_path.mkdir(parents=True, exist_ok=True)
    report_path = out_path / "report.json"

    try:
        db = load_dbc_cantools(dbc_path)
    except Exception as ex:
        report = _report_from_results(
            dbc_full,
            config_full,
            timestamp,
            [_pipeline_error("load_dbc", f"failed to load dbc with cantools: {ex}")],
        )
        report.write_json(report_path)
        return report

    if not run_codegen(dbc_path, config_path, str(out_path)):
        report = _report_from_results(
            dbc_full,
            config_full,
            timestamp,
            [_pipeline_error("codegen", "Signal-CANdy code generation failed")],
        )
        report.write_json(report_path)
        return report

    messages = extract_message_info(str(out_path))
    if not messages:
        report = _report_from_results(
            dbc_full,
            config_full,
            timestamp,
            [
                _pipeline_error(
                    "metadata", "no generated message metadata found in headers"
                )
            ],
        )
        report.write_json(report_path)
        return report

    binary = build_oracle_binary(str(out_path), messages)
    if not binary:
        report = _report_from_results(
            dbc_full,
            config_full,
            timestamp,
            [_pipeline_error("build", "oracle harness compilation failed")],
        )
        report.write_json(report_path)
        return report

    vectors, skipped_results = generate_test_vectors(
        db,
        messages,
        vectors_per_signal=vectors_per_signal,
    )
    execution_results = run_oracle_test(db, binary, vectors)
    all_results = [*skipped_results, *execution_results]

    report = _report_from_results(dbc_full, config_full, timestamp, all_results)
    report.write_json(report_path)
    return report
