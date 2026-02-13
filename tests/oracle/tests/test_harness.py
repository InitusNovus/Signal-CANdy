from __future__ import annotations

import os
from pathlib import Path

import pytest

from ..oracle.engine import extract_message_info, run_codegen
from ..oracle.harness import (
    MessageInfo,
    compile_harness,
    generate_harness_c,
    run_harness,
)


def _prepare_generated_sample(
    tmp_path: Path, sample_dbc_path: Path, default_config_path: Path
) -> tuple[Path, str]:
    gen_dir = tmp_path / "generated"
    ok = run_codegen(str(sample_dbc_path), str(default_config_path), str(gen_dir))
    assert ok
    messages = extract_message_info(str(gen_dir))
    assert messages
    source = generate_harness_c(
        messages,
        str(gen_dir / "include"),
        str(gen_dir / "src"),
    )
    assert Path(source).exists()
    return gen_dir, source


def _binary_path(gen_dir: Path) -> Path:
    return (
        gen_dir
        / "build"
        / ("oracle_harness.exe" if os.name == "nt" else "oracle_harness")
    )


def _build_sample_binary(
    tmp_path: Path, sample_dbc_path: Path, default_config_path: Path
) -> tuple[Path, Path]:
    gen_dir, source = _prepare_generated_sample(
        tmp_path, sample_dbc_path, default_config_path
    )
    binary = _binary_path(gen_dir)
    assert compile_harness(source, str(gen_dir), str(binary))
    assert binary.exists()
    return gen_dir, binary


def test_generate_harness_c_contains_dispatch_and_message_name(
    tmp_path: Path,
) -> None:
    include_dir = tmp_path / "include"
    src_dir = tmp_path / "src"
    include_dir.mkdir(parents=True, exist_ok=True)
    (include_dir / "message_1.h").write_text(
        "typedef struct { float Signal_1; float Signal_2; } MESSAGE_1_t;",
        encoding="utf-8",
    )
    source = generate_harness_c(
        [
            MessageInfo(
                name="MESSAGE_1",
                signals=[("Signal_1", "float"), ("Signal_2", "float")],
                dlc=8,
            )
        ],
        str(include_dir),
        str(src_dir),
    )
    text = Path(source).read_text(encoding="utf-8")
    assert "MESSAGE_1_decode" in text
    assert "MESSAGE_1_encode" in text
    assert '#include "message_1.h"' in text
    assert (src_dir / "oracle_harness.c").exists()


@pytest.mark.integration
def test_generate_harness_c_compiles_with_generated_sources(
    tmp_path: Path, sample_dbc_path: Path, default_config_path: Path
) -> None:
    _, binary = _build_sample_binary(tmp_path, sample_dbc_path, default_config_path)
    assert binary.exists()


@pytest.mark.integration
def test_json_protocol_decode_command_returns_signal_object(
    cantools_module, tmp_path: Path, sample_dbc_path: Path, default_config_path: Path
) -> None:
    db = cantools_module.database.load_file(str(sample_dbc_path))
    message = db.get_message_by_name("MESSAGE_1")
    payload = message.encode({"Signal_1": 10.0, "Signal_2": 5.0}, strict=False)

    _, binary = _build_sample_binary(tmp_path, sample_dbc_path, default_config_path)

    responses = run_harness(
        str(binary),
        [
            {
                "message": "MESSAGE_1",
                "action": "decode",
                "data": list(payload),
                "dlc": len(payload),
            }
        ],
    )
    assert len(responses) == 1
    assert responses[0]["ok"] is True
    assert "signals" in responses[0]
    assert set(responses[0]["signals"].keys()) == {"Signal_1", "Signal_2"}


@pytest.mark.integration
def test_json_protocol_encode_command_returns_payload(
    cantools_module, tmp_path: Path, sample_dbc_path: Path, default_config_path: Path
) -> None:
    db = cantools_module.database.load_file(str(sample_dbc_path))
    message = db.get_message_by_name("MESSAGE_1")
    expected = bytes(message.encode({"Signal_1": 7.0, "Signal_2": 12.3}, strict=False))

    _, binary = _build_sample_binary(tmp_path, sample_dbc_path, default_config_path)

    responses = run_harness(
        str(binary),
        [
            {
                "message": "MESSAGE_1",
                "action": "encode",
                "signals": {"Signal_1": 7.0, "Signal_2": 12.3},
            }
        ],
    )
    assert len(responses) == 1
    assert responses[0]["ok"] is True
    returned = bytes(responses[0]["data"])
    assert len(returned) == responses[0]["dlc"]
    assert returned == expected


@pytest.mark.integration
def test_json_protocol_unknown_message_returns_error(
    tmp_path: Path, sample_dbc_path: Path, default_config_path: Path
) -> None:
    _, binary = _build_sample_binary(tmp_path, sample_dbc_path, default_config_path)

    responses = run_harness(
        str(binary),
        [
            {
                "message": "UNKNOWN_MESSAGE",
                "action": "decode",
                "data": [0, 0, 0, 0, 0, 0, 0, 0],
                "dlc": 8,
            }
        ],
    )
    assert len(responses) == 1
    assert responses[0]["ok"] is False
    assert "unknown message" in responses[0]["error"].lower()
