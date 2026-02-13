from pathlib import Path
import sys

ORACLE_ROOT = Path(__file__).resolve().parents[1]
if str(ORACLE_ROOT) not in sys.path:
    sys.path.insert(0, str(ORACLE_ROOT))
