from __future__ import annotations

from pathlib import Path
import sys

import pytest


REPO_ROOT = Path(__file__).resolve().parents[3]
ORACLE_ROOT = Path(__file__).resolve().parents[1]

if str(ORACLE_ROOT) not in sys.path:
    sys.path.insert(0, str(ORACLE_ROOT))


@pytest.fixture
def sample_dbc_path() -> Path:
    return REPO_ROOT / "examples" / "sample.dbc"


@pytest.fixture
def comprehensive_dbc_path() -> Path:
    return REPO_ROOT / "examples" / "comprehensive_test.dbc"


@pytest.fixture
def multiplex_dbc_path() -> Path:
    return REPO_ROOT / "examples" / "multiplex_suite.dbc"


@pytest.fixture
def default_config_path(tmp_path: Path) -> Path:
    config_path = tmp_path / "default.yaml"
    config_path.write_text(
        'phys_type: "float"\n'
        'phys_mode: "double"\n'
        "range_check: false\n"
        'dispatch: "direct_map"\n'
        'motorola_start_bit: "msb"\n'
        "crc_counter_check: false\n",
        encoding="utf-8",
    )
    return config_path


@pytest.fixture
def cantools_module():
    return pytest.importorskip("cantools")


def pytest_configure(config):
    config.addinivalue_line(
        "markers",
        "integration: tests requiring dotnet and gcc",
    )
    config.addinivalue_line(
        "markers",
        "slow: tests that run longer than unit tests",
    )
