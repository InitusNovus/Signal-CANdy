from __future__ import annotations

from dataclasses import dataclass
import json
from pathlib import Path
import subprocess
from typing import Any


@dataclass(frozen=True)
class MessageInfo:
    name: str
    signals: list[tuple[str, str]]
    dlc: int


def _template_path() -> Path:
    return Path(__file__).resolve().parent.parent / "templates" / "oracle_harness.c"


def _escape_c_string(value: str) -> str:
    return value.replace("\\", "\\\\").replace('"', '\\"')


def _replace_placeholder(template_text: str, placeholder: str, replacement: str) -> str:
    wrapped = f"/* {placeholder} */"
    updated = template_text.replace(wrapped, replacement)
    return updated.replace(placeholder, replacement)


def _resolve_header_name(message_name: str, include_dir: Path) -> str:
    lower_name = f"{message_name.lower()}.h"
    exact_name = f"{message_name}.h"

    if include_dir.exists():
        if (include_dir / lower_name).exists():
            return lower_name
        if (include_dir / exact_name).exists():
            return exact_name
        for header in sorted(include_dir.glob("*.h")):
            if header.stem.lower() == message_name.lower():
                return header.name

    return lower_name


def _build_includes(messages: list[MessageInfo], include_dir: Path) -> str:
    seen: set[str] = set()
    lines: list[str] = []

    for message in messages:
        if message.name in seen:
            continue

        seen.add(message.name)
        header_name = _resolve_header_name(message.name, include_dir)
        lines.append(f'#include "{header_name}"')

    return "\n".join(lines)


def _build_signal_to_json(messages: list[MessageInfo]) -> str:
    lines: list[str] = []

    for index, message in enumerate(messages):
        branch = "if" if index == 0 else "else if"
        lines.append(f'    {branch} (strcmp(msg_name, "{message.name}") == 0) {{')
        lines.append(
            f"        const {message.name}_t* msg = (const {message.name}_t*)msg_value;"
        )

        if message.signals:
            format_parts = [
                f'"{signal_name}": %f' for signal_name, _ in message.signals
            ]
            format_literal = _escape_c_string(", ".join(format_parts))
            value_args = ", ".join(
                f"msg->{signal_name}" for signal_name, _ in message.signals
            )
            lines.append(
                f'        int written = snprintf(out, out_len, "{format_literal}", {value_args});'
            )
        else:
            lines.append('        int written = snprintf(out, out_len, "");')

        lines.append("        return written >= 0 && (size_t)written < out_len;")
        lines.append("    }")

    return "\n".join(lines)


def _build_json_to_signal(messages: list[MessageInfo]) -> str:
    lines: list[str] = []

    for index, message in enumerate(messages):
        branch = "if" if index == 0 else "else if"
        lines.append(f'    {branch} (strcmp(msg_name, "{message.name}") == 0) {{')
        lines.append(f"        {message.name}_t* msg = ({message.name}_t*)msg_value;")

        if message.signals:
            lines.append("        float parsed_value = 0.0f;")
            for signal_name, _ in message.signals:
                lines.append(
                    f'        if (!json_get_signal_float(signals_json, "{signal_name}", &parsed_value)) {{'
                )
                lines.append(
                    f'            snprintf(error, error_len, "missing signal {signal_name} for {message.name}");'
                )
                lines.append("            return false;")
                lines.append("        }")
                lines.append(f"        msg->{signal_name} = parsed_value;")

        lines.append("        return true;")
        lines.append("    }")

    return "\n".join(lines)


def _build_decode_dispatch(messages: list[MessageInfo]) -> str:
    lines: list[str] = []

    for index, message in enumerate(messages):
        branch = "if" if index == 0 else "else if"
        lines.append(f'    {branch} (strcmp(msg_name, "{message.name}") == 0) {{')
        lines.append(f"        {message.name}_t msg = {{0}};")
        lines.append("        char signals_json[ORACLE_LINE_CAPACITY];")
        lines.append("        int written = 0;")
        lines.append(f"        if (!{message.name}_decode(&msg, data, dlc)) {{")
        lines.append(
            f'            snprintf(error, error_len, "decode failed for {message.name}");'
        )
        lines.append("            return false;")
        lines.append("        }")
        lines.append(
            f'        if (!signals_to_json("{message.name}", &msg, signals_json, sizeof(signals_json))) {{'
        )
        lines.append(
            f'            snprintf(error, error_len, "signal serialization failed for {message.name}");'
        )
        lines.append("            return false;")
        lines.append("        }")
        lines.append(
            '        written = snprintf(out_json, out_json_len, "{\\"ok\\": true, \\"signals\\": {%s}}", signals_json);'
        )
        lines.append("        return written >= 0 && (size_t)written < out_json_len;")
        lines.append("    }")

    return "\n".join(lines)


def _build_encode_dispatch(messages: list[MessageInfo]) -> str:
    lines: list[str] = []

    for index, message in enumerate(messages):
        branch = "if" if index == 0 else "else if"
        lines.append(f'    {branch} (strcmp(msg_name, "{message.name}") == 0) {{')
        lines.append(f"        {message.name}_t msg = {{0}};")
        lines.append(
            f'        if (!json_to_signal("{message.name}", signals_json, &msg, error, error_len)) {{'
        )
        lines.append("            return false;")
        lines.append("        }")
        lines.append(f"        if (!{message.name}_encode(data, out_dlc, &msg)) {{")
        lines.append(
            f'            snprintf(error, error_len, "encode failed for {message.name}");'
        )
        lines.append("            return false;")
        lines.append("        }")
        lines.append("        return true;")
        lines.append("    }")

    return "\n".join(lines)


def generate_harness_c(
    messages: list[MessageInfo], include_dir: str, src_dir: str
) -> str:
    template_text = _template_path().read_text(encoding="utf-8")
    include_path = Path(include_dir)
    output_dir = Path(src_dir)
    output_dir.mkdir(parents=True, exist_ok=True)

    replacements = {
        "{{INCLUDES}}": _build_includes(messages, include_path),
        "{{DECODE_DISPATCH}}": _build_decode_dispatch(messages),
        "{{ENCODE_DISPATCH}}": _build_encode_dispatch(messages),
        "{{SIGNAL_TO_JSON}}": _build_signal_to_json(messages),
        "{{JSON_TO_SIGNAL}}": _build_json_to_signal(messages),
    }

    harness_text = template_text
    for placeholder, replacement in replacements.items():
        harness_text = _replace_placeholder(harness_text, placeholder, replacement)

    output_path = output_dir / "oracle_harness.c"
    output_path.write_text(harness_text, encoding="utf-8")
    return str(output_path)


def compile_harness(c_source: str, gen_dir: str, output_binary: str) -> bool:
    gen_path = Path(gen_dir)
    include_path = gen_path / "include"
    source_path = Path(c_source)
    output_path = Path(output_binary)

    if not source_path.exists() or not include_path.exists():
        return False

    source_files = sorted((gen_path / "src").glob("*.c"))
    source_args = [
        str(path) for path in source_files if path.resolve() != source_path.resolve()
    ]

    output_path.parent.mkdir(parents=True, exist_ok=True)

    command = [
        "gcc",
        "-std=c99",
        "-Wall",
        "-Wextra",
        f"-I{include_path}",
        "-o",
        str(output_path),
        *source_args,
        str(source_path),
        "-lm",
    ]

    try:
        result = subprocess.run(
            command,
            check=False,
            capture_output=True,
            text=True,
        )
    except OSError:
        return False

    return result.returncode == 0


def run_harness(binary: str, commands: list[dict]) -> list[dict]:
    binary_path = Path(binary)
    if not binary_path.exists():
        return [{"ok": False, "error": f"harness binary not found: {binary_path}"}]

    try:
        process = subprocess.Popen(
            [str(binary_path)],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
        )
    except OSError as ex:
        return [{"ok": False, "error": str(ex)}]

    if process.stdin is None or process.stdout is None:
        process.kill()
        return [{"ok": False, "error": "failed to open harness stdin/stdout pipes"}]

    responses: list[dict[str, Any]] = []

    for command in commands:
        payload = json.dumps(command, separators=(",", ":"))
        process.stdin.write(payload + "\n")
        process.stdin.flush()

        line = process.stdout.readline()
        if line == "":
            responses.append({"ok": False, "error": "harness exited without response"})
            break

        line = line.strip()
        if line == "":
            responses.append({"ok": False, "error": "empty response from harness"})
            continue

        try:
            parsed = json.loads(line)
        except json.JSONDecodeError:
            responses.append(
                {"ok": False, "error": "invalid JSON response", "raw": line}
            )
            continue

        if isinstance(parsed, dict):
            responses.append(parsed)
        else:
            responses.append(
                {"ok": False, "error": "response is not a JSON object", "raw": parsed}
            )

    process.stdin.close()
    process.wait()

    stderr_text = ""
    if process.stderr is not None:
        stderr_text = process.stderr.read().strip()

    if process.returncode != 0:
        responses.append(
            {
                "ok": False,
                "error": stderr_text
                or f"harness process exited with code {process.returncode}",
            }
        )

    return [dict(response) for response in responses]
