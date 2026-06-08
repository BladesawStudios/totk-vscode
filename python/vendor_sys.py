"""Helper module to handle adding vendor dependencies to sys.path."""
import sys
from pathlib import Path

_SCRIPT_DIR = Path(__file__).resolve().parent


def add_vendor_to_path(vendor_name: str) -> None:
    """Add a vendor directory to sys.path if it exists."""
    candidates = [
        _SCRIPT_DIR / "vendor" / vendor_name,
        _SCRIPT_DIR.parent / "vendor" / vendor_name,
    ]
    for vendor in candidates:
        vendor_str = str(vendor)
        if vendor.is_dir() and vendor_str not in sys.path:
            sys.path.insert(0, vendor_str)
            return


def get_vendor_path(vendor_name: str) -> Path | None:
    """Get the path to a vendor file or directory."""
    candidates = [
        _SCRIPT_DIR / "vendor" / vendor_name,
        _SCRIPT_DIR.parent / "vendor" / vendor_name,
    ]
    for vendor in candidates:
        if vendor.exists():
            return vendor
    return None
