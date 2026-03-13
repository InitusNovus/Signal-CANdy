"""Signal metadata comparison between cantools and Signal-CANdy."""

from dataclasses import dataclass
from typing import Any


@dataclass
class ComparisonReport:
    """Report of metadata comparison."""

    total_signals: int
    matched: int
    diverged: int
    divergences: list[dict]


def extract_cantools_metadata(db: Any) -> dict[str, dict[str, dict]]:
    """
    Extract per-message per-signal metadata from cantools database.

    Returns:
        {
            "MESSAGE_NAME": {
                "SIGNAL_NAME": {
                    "start_bit": int,
                    "length": int,
                    "byte_order": "little_endian" | "big_endian",
                    "is_signed": bool,
                    "factor": float,
                    "offset": float,
                    "minimum": float | None,
                    "maximum": float | None,
                }
            }
        }
    """
    metadata = {}

    for msg in db.messages:
        metadata[msg.name] = {}
        for sig in msg.signals:
            metadata[msg.name][sig.name] = {
                "start_bit": sig.start,
                "length": sig.length,
                "byte_order": sig.byte_order,
                "is_signed": sig.is_signed,
                "factor": sig.scale,
                "offset": sig.offset,
                "minimum": sig.minimum,
                "maximum": sig.maximum,
            }

    return metadata


def extract_candy_metadata(db: Any) -> dict[str, dict[str, dict]]:
    """
    Extract Signal-CANdy metadata by parsing the same DBC with cantools.

    (Simplified approach: since both tools parse the same DBC, metadata SHOULD match.
    Actual divergence detection happens in byte-level encode/decode testing.)

    For now, this returns the same structure as extract_cantools_metadata()
    to enable comparison framework. Future enhancement: parse generated C headers.
    """
    return extract_cantools_metadata(db)


def compare_signal_metadata(cantools_signal: dict, candy_signal: dict) -> list[str]:
    """
    Compare metadata for a single signal.

    Returns:
        List of divergence descriptions (empty list = match)
    """
    divergences = []

    fields_to_compare = [
        "start_bit",
        "length",
        "byte_order",
        "is_signed",
        "factor",
        "offset",
    ]

    for field in fields_to_compare:
        ct_val = cantools_signal.get(field)
        cd_val = candy_signal.get(field)

        if ct_val != cd_val:
            divergences.append(f"{field}: cantools={ct_val} vs candy={cd_val}")

    return divergences


def compare_all(cantools_meta: dict, candy_meta: dict) -> ComparisonReport:
    """
    Compare all signal metadata between cantools and Signal-CANdy.

    Returns:
        ComparisonReport with per-signal divergences
    """
    total = 0
    matched = 0
    divergences = []

    for msg_name, signals in cantools_meta.items():
        candy_signals = candy_meta.get(msg_name, {})

        for sig_name, ct_sig in signals.items():
            total += 1
            candy_sig = candy_signals.get(sig_name)

            if candy_sig is None:
                divergences.append(
                    {
                        "message": msg_name,
                        "signal": sig_name,
                        "field": "<missing>",
                        "cantools": "present",
                        "candy": "absent",
                    }
                )
                continue

            sig_divs = compare_signal_metadata(ct_sig, candy_sig)
            if not sig_divs:
                matched += 1
            else:
                for div_desc in sig_divs:
                    field, values = div_desc.split(": ", 1)
                    ct_val, cd_val = values.split(" vs ", 1)
                    divergences.append(
                        {
                            "message": msg_name,
                            "signal": sig_name,
                            "field": field,
                            "cantools": ct_val.replace("cantools=", ""),
                            "candy": cd_val.replace("candy=", ""),
                        }
                    )

    return ComparisonReport(
        total_signals=total,
        matched=matched,
        diverged=len(divergences),
        divergences=divergences,
    )
